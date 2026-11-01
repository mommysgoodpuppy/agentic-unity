#!/usr/bin/env python3
"""Preview or fix shader include spelling when an unambiguous local file exists."""
import argparse
import hashlib
import json
from pathlib import Path
import posixpath
import re


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('project', type=Path)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    project = args.project.resolve(strict=True)
    if args.report.exists():
        raise SystemExit('Report already exists; refusing overwrite.')
    files = [f for part in ['Assets', 'Packages'] for f in (project / part).rglob('*') if f.is_file() and not f.is_symlink()]
    exact = {f.relative_to(project).as_posix() for f in files}
    folded = {}
    for name in exact:
        folded.setdefault(name.casefold(), []).append(name)
    pattern = re.compile(rb'(^[ \t]*#[ \t]*include(?:_with_pragmas)?[ \t]*")([^"\r\n]+)(")', re.M)
    changes = []
    # Limit edits to the legacy shader tree whose compiler errors exposed this.
    tree = project / 'Assets/_PoiyomiShaders'
    for f in sorted(tree.rglob('*')):
        if not f.is_file() or f.suffix not in ['.shader', '.cginc', '.hlsl']:
            continue
        original = f.read_bytes()
        relative = f.relative_to(project).as_posix()
        replacements = []

        def correct(match):
            target = match[2].decode('utf-8', errors='surrogateescape')
            normalized = target.replace('\\', '/')
            rooted = normalized.startswith(('Assets/', 'Packages/'))
            candidate = posixpath.normpath(normalized if rooted else posixpath.join(posixpath.dirname(relative), normalized))
            if candidate in exact:
                return match[0]
            candidates = folded.get(candidate.casefold(), [])
            if len(candidates) != 1:
                return match[0]
            canonical = candidates[0]
            replacement = canonical if rooted else posixpath.relpath(canonical, posixpath.dirname(relative))
            replacements.append({'before': target, 'after': replacement})
            return match[1] + replacement.encode('utf-8', errors='surrogateescape') + match[3]

        revised = pattern.sub(correct, original)
        if revised != original:
            changes.append({'path': relative, 'before_sha256': hashlib.sha256(original).hexdigest(),
                            'after_sha256': hashlib.sha256(revised).hexdigest(), 'includes': replacements})
            if args.apply:
                f.write_bytes(revised)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps({'applied': args.apply, 'files': changes}, indent=2) + '\n')
    print(json.dumps({'applied': args.apply, 'files': len(changes), 'include_references': sum(len(x['includes']) for x in changes)}))


if __name__ == '__main__':
    main()
