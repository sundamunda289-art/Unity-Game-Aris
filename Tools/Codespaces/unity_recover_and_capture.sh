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
  n="$(find "$POSE" -maxdepth 1 -type f -name '*.jpg' 2>/dev/null | wc -l)"
  if [[ "$n" -ge 18 ]]; then break; fi
done
pid="$(pgrep -f '[E]ditor/Unity.*-projectPath /workspaces/Unity-Game-Aris' | head -1 || true)"
say "final_editor_running=$([[ -n "$pid" ]] && echo yes || echo no)"
say "final_editor_log_age_seconds=$(($(date +%s)-$(stat -c %Y "$LOG")))"
say "compilation_errors=$(grep -cE 'error CS[0-9]+' "$LOG" || true)"
n="$(find "$POSE" -maxdepth 1 -type f -name '*.jpg' 2>/dev/null | wc -l)"
say "real_pose_frames=$n"
if [[ -f "$POSE/report.txt" ]]; then
  grep -E '^(STAGE|FRAME|SUCCESS|ERROR|TIMEOUT|PLAYMODE)' "$POSE/report.txt" | tail -30 > "$OUT/pose-report.txt" || true
fi
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
