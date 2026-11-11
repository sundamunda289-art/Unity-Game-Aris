#!/usr/bin/env bash
# One-time owner-only Unity recovery inside the active Codespace.
set -euo pipefail
cd /workspaces/Unity-Game-Aris
OUT=/tmp/aaris-unity-recovery
LOG=/tmp/aaris-codespaces/Editor.log
POSE=/tmp/aaris-codespaces/motion-frames
mkdir -p "$OUT"
chmod 700 "$OUT"
: > "$OUT/status.txt"
say() { printf '%s\n' "$*" | tee -a "$OUT/status.txt"; }
say "AARIS_RECOVERY_STARTED"
say "branch=$(git branch --show-current)"
say "commit=$(git rev-parse --short HEAD)"
say "tracked_dirty_count=$(git status --porcelain --untracked-files=no | wc -l)"
test -f "$LOG" || { say "editor_log=missing"; exit 23; }
if DISPLAY=:99 xdpyinfo >/dev/null 2>&1; then say "x11=ready"; else say "x11=missing"; exit 24; fi
# A previous, assistant-generated UNTRACKED Editor-only pose-capture probe
# was truncated in transport. Repair only when its exact CS1513 signature is
# present, and back up that scratch source first. Never touch tracked game C#.
python3 - <<'PY'
from pathlib import Path
import subprocess
p=Path('Assets/AarisWorld/Editor/AarisPoseFrames.cs')
log=Path('/tmp/aaris-codespaces/Editor.log')
if not p.is_file():
    print('POSE_SCRATCH=ABSENT')
elif subprocess.run(['git','ls-files','--error-unmatch','--',str(p)],
                    stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL).returncode == 0:
    print('POSE_SCRATCH=TRACKED_DO_NOT_EDIT')
elif 'AarisPoseFrames.cs(99,2): error CS1513' not in log.read_text(errors='replace'):
    print('POSE_SCRATCH=NO_EXACT_COMPILER_SIGNATURE')
else:
    source=p.read_text()
    balance=source.count('{')-source.count('}')
    if source.count('class AarisPoseFrames')==1 and 1<=balance<=3:
        backup=Path('/tmp/aaris-unity-recovery/AarisPoseFrames-before-brace-repair.cs')
        backup.write_text(source)
        p.write_text(source.rstrip()+'\n'+'}\n'*balance)
        print('POSE_SCRATCH_BRACES_APPENDED='+str(balance))
        print('POSE_SCRATCH_BACKUP=created')
    else:
        print('POSE_SCRATCH_UNEXPECTED_BALANCE='+str(balance))
PY
# Ask the original Editor to re-import this one new source file; if stalled,
# the bounded restart below will take over without touching Library or LFS.
DISPLAY=:99 xdotool key --clearmodifiers ctrl+r 2>/dev/null || true
pid="$(pgrep -f '[E]ditor/Unity.*-projectPath /workspaces/Unity-Game-Aris' | head -1 || true)"
age=$(($(date +%s)-$(stat -c %Y "$LOG")))
say "editor_pid_present=$([[ -n "$pid" ]] && echo yes || echo no)"
say "editor_log_age_seconds=$age"
if [[ -z "$pid" || "$age" -ge 900 ]]; then
  BACKUP="$OUT/preserved-source-$(date -u +%Y%m%dT%H%M%SZ)"
  mkdir -p "$BACKUP"
  cp -p Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity "$BACKUP/scene.unity"
  for f in AarisCharacterVisual AarisOfflineGlooWall AarisThirdPersonPlayer; do
    cp -p "Assets/AarisWorld/Scripts/$f.cs" "$BACKUP/$f.cs"
  done
  say "source_backup=complete"
  if [[ -n "$pid" ]]; then
    kill -TERM "$pid" || true
    for i in $(seq 1 20); do
      if ! kill -0 "$pid" 2>/dev/null; then break; fi
      sleep 2
    done
    if kill -0 "$pid" 2>/dev/null; then
      kill -KILL "$pid" 2>/dev/null || true
      sleep 2
      say "stalled_editor_terminated=yes"
    fi
  fi
  bash Tools/Codespaces/remote.sh editor > "$OUT/relaunch.log" 2>&1 || {
    say "relaunch=failed"; exit 26;
  }
  say "relaunch=same_project_started"
else
  say "relaunch=unnecessary_active_editor"
fi
# Bounded import and frame-production observation; never alter LFS/caches.
for i in $(seq 1 12); do
  sleep 10
  mkdir -p "$POSE"
n="$(find "$POSE" -maxdepth 1 -type f -name '*.jpg' 2>/dev/null | wc -l)"
  if [[ "$n" -ge 18 ]]; then break; fi
done
pid="$(pgrep -f '[E]ditor/Unity.*-projectPath /workspaces/Unity-Game-Aris' | head -1 || true)"
say "final_editor_running=$([[ -n "$pid" ]] && echo yes || echo no)"
say "final_editor_log_age_seconds=$(($(date +%s)-$(stat -c %Y "$LOG")))"
say "compilation_errors=$(grep -cE 'error CS[0-9]+' "$LOG" || true)"
# These diagnostics refer only to public project C# source paths, never tokens.
grep -E 'error CS[0-9]+' "$LOG" | tail -n 12 > "$OUT/compiler-errors.txt" || true
if [[ -s "$OUT/compiler-errors.txt" ]]; then
  say "compiler_error_details_begin"
  cat "$OUT/compiler-errors.txt"
  say "compiler_error_details_end"
fi
n="$(find "$POSE" -maxdepth 1 -type f -name '*.jpg' 2>/dev/null | wc -l)"
say "real_pose_frames=$n"
if [[ -f "$POSE/report.txt" ]]; then
  grep -E '^(STAGE|FRAME|SUCCESS|ERROR|TIMEOUT|PLAYMODE)' "$POSE/report.txt" | tail -30 > "$OUT/pose-report.txt" || true
fi
touch "$OUT/pose-report.txt"
export DISPLAY=:99
window="$(xdotool search --onlyvisible --name '^Unity - Unity-Game-Aris' | head -1 || true)"
if [[ -n "$window" ]]; then
  xdotool windowactivate --sync "$window" 2>/dev/null || true
  if scrot -u -q 85 -o "$OUT/UnityEditor.jpg" >/dev/null 2>&1 && [[ -s "$OUT/UnityEditor.jpg" ]]; then
    say "unity_editor_jpeg=captured"
  fi
fi
if [[ "$n" -ge 18 ]]; then
  for jpg in "$POSE"/*.jpg; do
    cp -n "$jpg" "$OUT/"
  done
  say "pose_frames_copied=$n"
fi
say "AARIS_RECOVERY_FINISHED"
