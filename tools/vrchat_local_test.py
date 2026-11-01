#!/usr/bin/env python3
"""Launch a locally built world in VRChat and follow the client's log. Linux with Steam and Proton.

    uv run tools/vrchat_local_test.py --project PROJECT [--until TEXT] [--filter REGEX] [--timeout 300] [--vr]
    uv run tools/vrchat_local_test.py --vrcw FILE.vrcw --print-only

--project reads the path that AgentWorldBuild.Export wrote to Logs/agent-last-build.txt. The same
launch arguments as the SDK's Build & Test open a hidden local instance; nothing is uploaded, but
this is the real client, logged in as whoever owns the Steam account, so ask before running it.
Worlds with Udon logging print to the output log: use a unique tag and --until to end the run.
Close VRChat between builds: the .vrcw's name changes on each export, so --watch-worlds does not
pick up a rebuild in a client that is already running.
"""
import argparse
import random
import re
import shutil
import subprocess
import time
from pathlib import Path

DEFAULT_LOGS = ("~/.local/share/Steam/steamapps/compatdata/438100/pfx/drive_c/users/steamuser/"
                "AppData/LocalLow/VRChat/VRChat")


def is_running():
    return subprocess.run(["pgrep", "-x", "VRChat.exe"], capture_output=True).returncode == 0


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--project", type=Path)
    source.add_argument("--vrcw", type=Path)
    parser.add_argument("--until", help="finish at the first log line containing this text")
    parser.add_argument("--filter", default=r"Udon|Exception|Error", help="log lines to print and keep (regex)")
    parser.add_argument("--timeout", type=float, default=300, help="seconds to follow the log")
    parser.add_argument("--vr", action="store_true", help="start in VR (default: desktop)")
    parser.add_argument("--logs", type=Path, default=Path(DEFAULT_LOGS).expanduser(), help="VRChat's log folder in the Proton prefix")
    parser.add_argument("--out", type=Path, help="write the kept lines to this file")
    parser.add_argument("--print-only", action="store_true", help="print the launch arguments; launch nothing")
    args = parser.parse_args()

    vrcw = args.vrcw or Path((args.project / "Logs/agent-last-build.txt").read_text().strip())
    # Wine sees the Linux filesystem as Z:.
    room = f"{random.randrange(10**8):08d}"
    launch = [f"--url=create?roomId={room}&hidden=true&name=BuildAndRun&url=file:///Z:{vrcw.resolve()}",
              "--enable-debug-gui", "--enable-sdk-log-levels", "--enable-udon-debug-logging", "--watch-worlds"]
    if not args.vr:
        launch.append("--no-vr")
    if args.print_only:
        print("\n".join(["steam -applaunch 438100", *launch]))
        return
    if not vrcw.exists():
        raise SystemExit(f"No world at {vrcw}")
    if is_running():
        raise SystemExit("VRChat is already running; close it first (pkill -x VRChat.exe). Use the exact name: "
                         "pkill -f would also match the shell that ran it.")
    if not shutil.which("steam") or not args.logs.is_dir():
        raise SystemExit(f"Needs Steam and VRChat's log folder ({args.logs}; see --logs)")

    # A just-killed client's log can still look recent: only logs new since now count.
    old = {p.name for p in args.logs.glob("output_log_*")}
    subprocess.run(["steam", "-applaunch", "438100", *launch], check=True)
    wanted = re.compile(args.filter)
    kept, log, offset = [], None, 0
    deadline = time.monotonic() + args.timeout
    finished = False
    while time.monotonic() < deadline and not finished:
        time.sleep(1)
        if log is None:
            new = [p for p in args.logs.glob("output_log_*") if p.name not in old]
            log = max(new, key=lambda p: p.stat().st_mtime) if new else None
            if log is None:
                continue
        data = log.read_bytes()
        complete = data.rfind(b"\n") + 1  # A partial last line waits for the next read.
        if complete <= offset:
            continue
        chunk = data[offset:complete].decode(errors="replace")
        offset = complete
        for line in chunk.splitlines():
            if wanted.search(line) or (args.until and args.until in line):
                kept.append(line)
                print(line, flush=True)
            if args.until and args.until in line:
                finished = True
    if args.out:
        args.out.write_text("\n".join(kept) + "\n")
    print(f"Full log: {log}" if log else "VRChat wrote no new log")
    if args.until and not finished:
        raise SystemExit(f"Timed out before {args.until!r}")


if __name__ == "__main__":
    main()
