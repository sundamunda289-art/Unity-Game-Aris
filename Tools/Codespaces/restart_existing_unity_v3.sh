#!/usr/bin/env bash
# Restart only the user's ORIGINAL existing Unity GUI project in the exact
# owner Codespace. No new worktree/project, no reset, no cache deletion.
set -Eeuo pipefail
umask 077
PROJECT=/workspaces/Unity-Game-Aris
BASE=/tmp/aaris-codespaces
VERSION=6000.3.26f1
mkdir -p "$BASE" "$BASE/UnityTemp"
test -d "$PROJECT/.git" || { echo AARIS_GUI_SOURCE_MISSING; exit 11; }
test -s "$PROJECT/Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity" || {
  echo AARIS_GUI_SOURCE_SCENE_MISSING; exit 12;
}
if head -c 100 "$PROJECT/Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity" |
  grep -q 'git-lfs.github.com/spec'; then
  echo AARIS_GUI_NATIVE_SCENE_IS_LFS_POINTER; exit 13;
fi
grep -q 'o Aaris_TacticalTransport_V3' "$PROJECT/Assets/Resources/AarisDrop/CargoPlane.obj" || {
  echo AARIS_GUI_V3_MODEL_MISSING; exit 14;
}
grep -q 'SimulateAirborne(dt);' "$PROJECT/Assets/AarisWorld/Scripts/AarisOfflineDrop.cs" || {
  echo AARIS_GUI_V3_GAMEPLAY_MISSING; exit 15;
}
EDITOR=""
for p in "$HOME/.local/share/aaris-unity-editors/$VERSION/Editor/Unity" \
   "/workspaces/.aaris-unity-editors/$VERSION/Editor/Unity" \
   "/tmp/aaris-unity-runtime/editors/$VERSION/Editor/Unity"; do
  if test -x "$p"; then EDITOR="$p"; break; fi
done
test -n "$EDITOR" || { echo AARIS_GUI_EDITOR_BINARY_MISSING; exit 16; }
if ! command -v flock >/dev/null 2>&1; then
  echo AARIS_GUI_FLOCK_UNAVAILABLE; exit 17;
fi
exec 9>"$BASE/v3-same-project-editor.lock"
flock -n 9 || { echo AARIS_GUI_RESTART_LOCKED_OTHER_PROCESS; exit 18; }

# Exact argv inspection avoids false positives from pgrep/SSH shell command lines.
get_editor_state() {
  python3 - "$PROJECT" <<'PY'
from pathlib import Path
import sys
expected=Path(sys.argv[1]).resolve()
same=0
other=0
for p in Path('/proc').iterdir():
    if not p.name.isdigit():continue
    try:
        v=[x.decode('utf-8','replace') for x in
           (p/'cmdline').read_bytes().split(b'\0') if x]
    except (OSError,PermissionError):continue
    if not v or not v[0].endswith('/Editor/Unity'):continue
    if '-projectPath' not in v:continue
    at=v.index('-projectPath')
    if at+1>=len(v):continue
    if Path(v[at+1]).resolve()==expected:same+=1
    else:other+=1
print(str(same)+' '+str(other))
PY
}
read -r same other < <(get_editor_state)
if ((same>0 && other==0)); then
  echo AARIS_GUI_EXACT_EXISTING_EDITOR_ALREADY_RUNNING
  exit 0
fi
if ((same>1 || other>0)); then
  echo AARIS_GUI_DIFFERENT_EDITOR_PROCESS_BUSY_NO_NEW_LAUNCH; exit 19;
fi
# Never disrupt an active Android build or a different Unity compilation.
if pgrep -x -f 'gradle|il2cpp|bee_backend|aapt2' >/dev/null 2>&1; then
  echo AARIS_GUI_BUILD_BUSY_LEAVE_UNTOUCHED; exit 20;
fi
if ! bash "$PROJECT/.devcontainer/start_screen.sh" >"$BASE/v3-screen-start-private.log" 2>&1; then
  echo AARIS_GUI_DISPLAY_BOOT_FAILED; exit 21;
fi
if ! DISPLAY=:99 xdpyinfo >/dev/null 2>&1; then
  echo AARIS_GUI_DISPLAY_NOT_AVAILABLE; exit 22;
fi
# Engine CLI health is tested in-place (no separate project or GUI).
# Don't assert health from a successful -version alone.
rc=0
timeout --kill-after=3s 18s "$EDITOR" -version \
  >"$BASE/v3-version-private.log" 2>"$BASE/v3-version-error-private.log" || rc=$?
echo "AARIS_GUI_EDITOR_VERSION_PROBE_EXIT_$rc"
if ((rc==139)); then
  echo AARIS_GUI_BINARY_CRASH_NO_PROJECT_RUNNING; exit 23;
fi
if ((rc!=0)); then
  echo AARIS_GUI_VERSION_PROBE_NOT_CONFIRMED
fi

# Separate launch process, but SAME UNITY EDITOR INSTALL AND PROJECT. No new
# Unity installation, no extra project, no scene save, no LFS changes.
rm -f "$BASE/v3-editor-exit-code.private"
cat >"$BASE/v3-launch-wrapper.private.sh" <<'SH'
#!/usr/bin/env bash
set +e
editor="$1"; project="$2"; log="$3"; witness="$4"
"$editor" -force-glcore -projectPath "$project" -logFile "$log"
code=$?
printf '%d\n' "$code" >"$witness"
exit "$code"
SH
chmod 700 "$BASE/v3-launch-wrapper.private.sh"
nohup setsid env DISPLAY=:99 TMPDIR="$BASE/UnityTemp" \
  LIBGL_ALWAYS_SOFTWARE=1 \
  "$BASE/v3-launch-wrapper.private.sh" "$EDITOR" "$PROJECT" \
  "$BASE/v3-editor-gui-private.log" "$BASE/v3-editor-exit-code.private" \
  </dev/null >"$BASE/v3-editor-stdout.private.log" \
  2>"$BASE/v3-editor-stderr.private.log" &
for i in 1 2 3 4; do
  sleep 7
  read -r same other < <(get_editor_state)
  if ((same==1 && other==0)); then
    # Give importer time to surface a fast native failure.
    if ((i>=3)); then
      echo AARIS_GUI_ORIGINAL_PROJECT_EDITOR_PROCESS_ALIVE
      echo AARIS_GUI_REAL_SCENE_RENDER_STILL_REQUIRES_VISUAL_CHECK
      exit 0
    fi
  fi
  if test -s "$BASE/v3-editor-exit-code.private"; then
    code="$(cat "$BASE/v3-editor-exit-code.private")"
    [[ "$code" =~ ^[0-9]{1,3}$ ]] || code=255
    echo "AARIS_GUI_EDITOR_LAUNCH_EXIT_$code"
    if test "$code" = 139; then echo AARIS_GUI_GLCORE_CRASH_SEGFAULT; fi
    exit 24
  fi
done
echo AARIS_GUI_LAUNCH_NOT_YET_VERIFIED_NO_FORCE_KILL
exit 25
