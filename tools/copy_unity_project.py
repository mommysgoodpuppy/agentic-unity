#!/usr/bin/env python3
"""Copy a Windows Unity project read-only; verify every file before editor use."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
from datetime import datetime, timezone


def sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('destination', type=Path)
    parser.add_argument('manifest', type=Path)
    args = parser.parse_args()
    source = args.source.resolve(strict=True)
    destination = args.destination.resolve()
    if destination.exists() or args.manifest.exists():
        raise SystemExit('Destination or manifest already exists; refusing to overwrite.')
    if source == destination or source in destination.parents or destination in source.parents:
        raise SystemExit('Source and destination must be separate trees.')
    mount = json.loads(subprocess.check_output(['findmnt', '-J', '-T', str(source)]))['filesystems'][0]
    if 'ro' not in mount['options'].split(','):
        raise SystemExit('Windows source must be on a read-only mount.')
    for required in ['Assets', 'Packages', 'ProjectSettings']:
        if not (source / required).is_dir():
            raise SystemExit(f'Missing Unity project directory: {required}')
    excluded = ['Library', 'Logs']
    files = []
    directories = []
    for base, dirs, names in os.walk(source, followlinks=False):
        if Path(base) == source:
            dirs[:] = [d for d in dirs if d not in excluded]
        for name in dirs + names:
            if (Path(base) / name).is_symlink():
                raise SystemExit('Source contains a symlink; inspect before copying.')
        directories.extend((Path(base) / name).relative_to(source) for name in dirs)
        files.extend((Path(base) / name).relative_to(source) for name in names)
    total = sum((source / rel).stat().st_size for rel in files)
    destination.parent.mkdir(parents=True, exist_ok=True)
    if shutil.disk_usage(destination.parent).free < total * 2 + 8 * 1024**3:
        raise SystemExit('Insufficient space for working copy, recovery snapshot, and import reserve.')
    print(f'Copying and hashing {len(files)} files ({total:,} bytes); excluding Library and Logs.', flush=True)
    destination.mkdir(mode=0o700)
    for rel in directories:
        (destination / rel).mkdir(parents=True, exist_ok=True, mode=0o700)
    entries = []
    for index, rel in enumerate(sorted(files), 1):
        original = source / rel
        copied = destination / rel
        digest = sha256(original)
        with original.open('rb') as reader, copied.open('xb') as writer:
            shutil.copyfileobj(reader, writer, length=1024 * 1024)
        shutil.copystat(original, copied)
        # NTFS reports all files executable here; keep transferred assets private.
        copied.chmod(0o600)
        if sha256(copied) != digest:
            raise SystemExit(f'Hash verification failed for {rel}; copy retained for inspection.')
        entries.append({'path': rel.as_posix(), 'bytes': copied.stat().st_size, 'sha256': digest})
        if index % 1000 == 0:
            print(f'Verified {index}/{len(files)} files.', flush=True)
    manifest = {
        'created_at': datetime.now(timezone.utc).isoformat(),
        'source': str(source), 'destination': str(destination), 'source_mount': mount,
        'excluded_root_directories': excluded,
        'policy': 'New destination only; source read-only; all regular file bytes SHA-256 verified; permissions private to owner.',
        'files': entries, 'summary': {'files': len(entries), 'bytes': total},
    }
    args.manifest.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    with args.manifest.open('x') as stream:
        json.dump(manifest, stream, indent=2)
        stream.write('\n')
    args.manifest.chmod(0o600)
    print(json.dumps(manifest['summary']), flush=True)


if __name__ == '__main__':
    main()
