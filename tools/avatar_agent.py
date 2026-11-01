#!/usr/bin/env python3
"""Send one JSON recipe to the running Unity editor's local queue."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parent))
import unity_env  # noqa: E402


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("project", type=Path)
    parser.add_argument("recipe", type=Path)
    parser.add_argument("--timeout", type=float, default=60, help="live queue wait in seconds; does not limit batch execution")
    parser.add_argument("--batch", action="store_true", help="start Unity without a GUI; project must be closed")
    parser.add_argument("--editor", type=Path, help="Unity binary; otherwise derive version from project")
    args = parser.parse_args()
    request = json.loads(args.recipe.read_text())
    if request.get("command") not in ("list", "inspect", "assemble", "import", "preview", "open", "frame", "edit", "undo", "redo", "checkpoint", "audit-meshes", "swap-meshes", "refresh", "inspect-accessory", "attach-accessory", "export-accessory", "fix-accessory-bounds", "audit-accessory-bounds", "attach-prefab", "place-prefab", "closeup", "scene-view", "add-toggle", "add-fury-slider", "save", "upload"):
        parser.error("Unsupported avatar command")
    queue = args.project.resolve() / "Library/VRUnityAgent"
    queue.mkdir(mode=0o700, parents=True, exist_ok=True)
    queue.chmod(0o700)
    key = f"{time.time_ns()}-{uuid.uuid4().hex}"
    temporary = queue / (key + ".tmp")
    pending = queue / (key + ".request.json")
    result = queue / (key + ".result.json")
    if args.batch:
        if not request.get("scene"):
            parser.error("batch recipe requires scene")
        editor = unity_env.find_editor(args.project, args.editor)
        for process in Path("/proc").glob("[0-9]*/cmdline"):
            try:
                command = process.read_bytes().split(b"\0")
                if command and Path(os.fsdecode(command[0])).name == "Unity" and b"-projectPath" in command:
                    index = command.index(b"-projectPath")
                    if Path(os.fsdecode(command[index + 1])).resolve() == args.project.resolve():
                        parser.error("This project already has a Unity editor running; close it or use a separate working copy")
            except (OSError, IndexError):
                continue
        recipe = args.recipe.resolve()
        log = queue / (key + ".editor.log")
        print(f"Private log: {log}", flush=True)
        # Keep graphics: rendering checks cannot use -nographics.
        completed = subprocess.run([str(editor), "-batchmode", *unity_env.graphics_args(), "-projectPath", str(args.project.resolve()),
            "-executeMethod", "AvatarAgent.RunBatch", "-avatarRecipe", str(recipe), "-avatarReport", str(result),
            "-logFile", str(log)], env=unity_env.display_env())
        if not result.exists():
            raise SystemExit(f"Unity exited {completed.returncode} without a report; inspect {log}")
        data = json.loads(result.read_text())
        print(f"Result: {result}")
        if completed.returncode or not data.get("ok"):
            raise SystemExit(data.get("error", f"Unity exited {completed.returncode}"))
        print(json.dumps({k: v for k, v in data.items() if k not in ("nodes", "skins", "avatars", "meshes", "meshCandidates")}, indent=2))
        return
    temporary.write_text(json.dumps(request))
    temporary.chmod(0o600)
    os.replace(temporary, pending)
    print(f"Request: {key}", flush=True)
    deadline = time.monotonic() + args.timeout
    while time.monotonic() < deadline:
        if result.exists():
            data = json.loads(result.read_text())
            print(f"Result: {result}")
            if not data.get("ok"):
                raise SystemExit(data.get("error", "Unity operation failed"))
            print(json.dumps({k: v for k, v in data.items() if k not in ("nodes", "skins", "avatars", "meshes", "meshCandidates")}, indent=2))
            return
        time.sleep(0.25)
    # Withdraw only an unclaimed request. A running request may still finish.
    try:
        pending.unlink()
        raise SystemExit("Editor did not claim request; withdrawn. Check focus/import/compilation.")
    except FileNotFoundError:
        raise SystemExit(f"Request was claimed; result pending at {result}. Do not replay mutations.")


if __name__ == "__main__":
    main()
