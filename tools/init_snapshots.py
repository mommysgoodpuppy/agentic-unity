#!/usr/bin/env python3
"""Create the save-snapshot repository that unity/avatar/AvatarAgentSnapshot.cs commits to.

    uv run tools/init_snapshots.py PROJECT

Creates ~/UnitySnapshots/<project>.git with the project as its work tree and an allowlist of Unity
text data. The project's own .git, if any, is not touched. Binaries (textures, models, audio) and
Library are left out, so a snapshot recovers scenes and settings but is not a full backup.
Inspect: git --git-dir=~/UnitySnapshots/<project>.git log
"""
import argparse
import subprocess
from pathlib import Path

EXCLUDE = """# Save snapshots: allowlist of Unity text data only. Binaries are not snapshotted;
# Library/Temp and installed packages are rebuildable.
/*
!/Assets/
!/ProjectSettings/
!/Packages/
/Packages/*
!/Packages/manifest.json
!/Packages/packages-lock.json
!/Packages/vpm-manifest.json
/Assets/**
!/Assets/**/
""" + "".join(f"!/Assets/**/*.{ext}\n" for ext in (
    "meta unity prefab mat asset controller overrideController anim mask physicMaterial lighting preset "
    "cs asmdef asmref shader cginc hlsl orlsource json").split())


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("project", type=Path)
    args = parser.parse_args()
    project = args.project.resolve()
    if not (project / "Assets").is_dir():
        raise SystemExit(f"{project} is not a Unity project")
    git_dir = Path.home() / "UnitySnapshots" / (project.name + ".git")
    if git_dir.exists():
        raise SystemExit(f"{git_dir} already exists; leaving it alone")

    def git(*command):
        subprocess.run(["git", f"--git-dir={git_dir}", *command], check=True, stdout=subprocess.DEVNULL)

    git_dir.parent.mkdir(parents=True, exist_ok=True)
    git("init", "-q", "-b", "main")
    git("config", "core.bare", "false")
    git("config", "core.worktree", str(project))
    (git_dir / "info").mkdir(exist_ok=True)
    (git_dir / "info/exclude").write_text(EXCLUDE)
    git("add", "-A")
    git("commit", "-q", "--allow-empty", "-m", "Initial snapshot")
    print(f"Snapshots in {git_dir}. Install the avatar helpers so each Unity save adds one.")


if __name__ == "__main__":
    main()
