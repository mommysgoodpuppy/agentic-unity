#!/usr/bin/env python3
"""Copy the editor helpers into a Unity project's Assets/VRUnityAgent/Editor.

    uv run tools/install_helpers.py PROJECT --kind avatar|world [--dry-run]

Only that folder is written. Existing .meta files are kept, so GUIDs stay stable across updates.
Unity recompiles when it next refreshes; the helpers stay out of uploads because they are editor-only.
"""
import argparse
import shutil
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import unity_env  # noqa: E402

REPO = Path(__file__).resolve().parent.parent
REQUIRED = {
    "avatar": ["com.vrchat.avatars", "nadena.dev.modular-avatar"],
    "world": ["com.vrchat.worlds"],
}
# Found at runtime, so the helpers compile without them (docs/dependencies.md).
OPTIONAL = {
    "avatar": {
        "com.anatawa12.avatar-optimizer": "optimize_avatar.py",
        "com.ramtype0.meshia.mesh-simplification": "optimize_avatar.py",
        "com.vrcfury.vrcfury": "add-fury-slider",
    },
    "world": {},
}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("project", type=Path)
    parser.add_argument("--kind", choices=sorted(REQUIRED), required=True)
    parser.add_argument("--dry-run", action="store_true", help="show what would change; write nothing")
    args = parser.parse_args()
    project = args.project.resolve()
    if not (project / "Assets").is_dir() or not (project / "ProjectSettings").is_dir():
        raise SystemExit(f"{project} is not a Unity project (no Assets and ProjectSettings)")
    installed = unity_env.installed_packages(project)
    missing = [name for name in REQUIRED[args.kind] if name not in installed]
    if missing:
        raise SystemExit(f"The {args.kind} helpers need {', '.join(missing)}, which this project does not have. "
                         "Add them with ALCOM or VCC first; this tool does not change packages.")
    target = project / "Assets/VRUnityAgent/Editor"
    changed = 0
    for source in sorted((REPO / "unity" / args.kind).iterdir()):
        if source.suffix not in (".cs", ".shader"):
            continue
        destination = target / source.name
        if destination.exists() and destination.read_bytes() == source.read_bytes():
            continue
        changed += 1
        print(("update " if destination.exists() else "add    ") + str(destination.relative_to(project)))
        if not args.dry_run:
            target.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, destination)
    print(f"{changed} file(s) {'would change' if args.dry_run else 'changed'}.")
    for package, used_by in OPTIONAL[args.kind].items():
        if package not in installed:
            print(f"Note: no {package}, which {used_by} needs. See docs/dependencies.md.")


if __name__ == "__main__":
    main()
