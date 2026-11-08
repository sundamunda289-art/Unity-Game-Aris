#!/usr/bin/env bash
# Diagnoses or gracefully recovers ONLY the owner-owned Unity Editor process
# belonging to /workspaces/Unity-Game-Aris. No arbitrary PID, kill -9,
# broad pkill, login state, scene, or Git LFS modification.
set -Eeuo pipefail
MODE="${1:-}"
if [[ "$MODE" != diag && "$MODE" != recover ]]; then
  echo UNITY_EDITOR_PROCESS_INVALID_MODE
  exit 2
fi
python3 - "$MODE" <<'PY'
import os
from pathlib import Path
import signal
import subprocess
import sys
import time

mode = sys.argv[1]
project = "/workspaces/Unity-Game-Aris"
def inspect():
    result = []
    for node in Path("/proc").iterdir():
        if not node.name.isdecimal():
            continue
        try:
            if node.stat().st_uid != os.getuid():
                continue
            cmd = (node/"cmdline").read_bytes().split(b"\0")
            if not cmd or not cmd[0].endswith(b"/Editor/Unity"):
                continue
            args = [x.decode("utf-8","replace") for x in cmd if x]
            if "-projectPath" not in args:
                continue
            at = args.index("-projectPath")
            if at+1 >= len(args) or args[at+1] != project:
                continue
            state = next((line.split()[1] for line in (node/"status").read_text().splitlines() if line.startswith("State:")), "?")
            result.append((int(node.name),state))
        except (OSError,ValueError,IndexError):
            continue
    return result

def unity_windows():
    try:
        v = subprocess.run(["xdotool","search","--onlyvisible","--name","Unity - Unity-Game-Aris"],capture_output=True,text=True,timeout=5)
        return len([x for x in v.stdout.splitlines() if x.isdecimal()])
    except (OSError,subprocess.TimeoutExpired):
        return -1

processes = inspect()
live = [pid for pid,state in processes if state!="Z"]
zombies = len(processes)-len(live)
windows = unity_windows()
print("UNITY_EDITOR_PROCESS_LIVE_"+str(len(live)))
print("UNITY_EDITOR_PROCESS_ZOMBIE_"+str(zombies))
print("UNITY_EDITOR_VISIBLE_WINDOWS_"+str(windows))
if mode=="diag":
    # Read only the exact running Editor's resource state. No PIDs, command
    # lines, paths or raw application logs are printed to the public Issue.
    def ticks(pid):
        try:
            s=Path(f"/proc/{pid}/stat").read_text()
            fields=s.rsplit(")",1)[1].split()
            return int(fields[11])+int(fields[12])
        except (OSError,ValueError,IndexError):
            return 0
    before=sum(ticks(p) for p in live)
    time.sleep(2)
    after=sum(ticks(p) for p in live)
    print("UNITY_EDITOR_CPU_ACTIVITY_"+("BUSY" if after-before>=15 else "LOW"))
    log=Path("/tmp/aaris-codespaces/Editor.log")
    if log.is_file():
        age=max(0,time.time()-log.stat().st_mtime)
        bucket="RECENT" if age<120 else "AGING" if age<600 else "STALE"
        print("UNITY_EDITOR_LOG_AGE_"+bucket)
        print("UNITY_EDITOR_LOG_PRESENT")
        import re
        tail=log.read_bytes()[-250000:].decode("utf-8","replace")
        flags={
            "IMPORT":r"import(ing|ed)? assets|asset database|refreshing asset|importing.*asset",
            "SCRIPT_COMPILATION":r"script.*compil|compil(ing|ation).*assembl|assembly reload",
            "UPM":r"package manager|resolving packages|package resolution",
            "EDITOR_READY":r"project loaded|editor.*initializ.*completed|project.*ready",
            "ERROR_PRESENT":r"\\berror\\s+(?:CS[0-9]{4}|[A-Za-z]+Exception)",
        }
        for name,pattern in flags.items():
            if re.search(pattern,tail,re.I):
                print("UNITY_EDITOR_LOG_FLAG_"+name)
    else:
        print("UNITY_EDITOR_LOG_MISSING")
    print("UNITY_EDITOR_PROCESS_DIAG_READY")
    raise SystemExit(0)
if windows != 0:
    print("UNITY_EDITOR_RECOVER_BLOCKED_VISIBLE_WINDOW")
    raise SystemExit(20)
if not live:
    print("UNITY_EDITOR_RECOVER_NO_LIVE_PROCESS")
    raise SystemExit(0)
# A WM_DELETE request already closed the window. SIGTERM requests shutdown of
# the same exact-version Editor only; no SIGKILL and no other process matches.
for pid in live:
    try:
        os.kill(pid,signal.SIGTERM)
    except OSError:
        print("UNITY_EDITOR_RECOVER_TERM_FAILED")
        raise SystemExit(21)
for _ in range(25):
    remaining=[p for p,s in inspect() if s!="Z"]
    if not remaining:
        print("UNITY_EDITOR_RECOVER_TERM_CONFIRMED")
        raise SystemExit(0)
    time.sleep(1)
print("UNITY_EDITOR_RECOVER_TERM_PENDING")
raise SystemExit(22)
PY
