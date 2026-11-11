#!/usr/bin/env bash
# Use the CURRENT licensed Codespace, not a new machine. Inspect and preserve
# its original modified Assets by overlaying copies into a temp-only test worktree.
set -Eeuo pipefail
umask 077
SOURCE=/workspaces/Unity-Game-Aris
RUN=/tmp/aaris-codespaces/offline-drop-probe
TEMP_ROOT=/tmp/aaris-offline-drop-worktrees
MODE="${1:-main}"
case "$MODE" in
  main) FEATURE=main ;;
  hud_review) FEATURE=fix/android-ugui-shader-parachute-20261110 ;;
  v3_review) FEATURE=feat/drop-aircraft-realism-fps-20261110 ;;
  *) echo AARIS_DROP_REVIEW_MODE_NOT_ALLOWED; exit 2 ;;
esac
EDITOR="$HOME/.local/share/aaris-unity-editors/6000.3.26f1/Editor/Unity"
for candidate in "/workspaces/.aaris-unity-editors/6000.3.26f1/Editor/Unity" "/tmp/aaris-unity-runtime/editors/6000.3.26f1/Editor/Unity"; do
  if ! test -x "$EDITOR" && test -x "$candidate"; then EDITOR="$candidate"; fi
done
test -x "$EDITOR" || { echo AARIS_DROP_EDITOR_MISSING; exit 11; }
test -d "$SOURCE/.git" || { echo AARIS_DROP_SOURCE_MISSING; exit 12; }

# The original Codespace may contain tracked Unity Editor changes.
# Never checkout, reset, stash, force clean, or run Unity batchmode on this source.
if pgrep -f '[E]ditor/Unity.*-projectPath' >/dev/null 2>&1; then
  echo AARIS_DROP_EDITOR_BUSY_NO_FORCE_KILL
  exit 13
fi
mkdir -p "$RUN" "$TEMP_ROOT" "$RUN/frames" "$RUN/shots" "$RUN/UnityTemp"
printf "AARIS_SAME_CODESPACE_EXISTING_SOURCE_PRESERVED\n"
if ! git -C "$SOURCE" fetch --quiet origin "+refs/heads/$FEATURE:refs/remotes/origin/$FEATURE"; then
  echo AARIS_DROP_FEATURE_FETCH_FAILED
  exit 14
fi
SHA="$(git -C "$SOURCE" rev-parse --verify "refs/remotes/origin/$FEATURE^{commit}")"
[[ "$SHA" =~ ^[0-9a-f]{40}$ ]] || { echo AARIS_DROP_BAD_SHA; exit 14; }
PROJECT="$TEMP_ROOT/$SHA"
if ! test -d "$PROJECT"; then
  GIT_LFS_SKIP_SMUDGE=1 git -C "$SOURCE" -c filter.lfs.process= -c filter.lfs.smudge=cat \
    -c filter.lfs.required=false worktree add --detach "$PROJECT" "$SHA" >/dev/null 2>&1 || {
      echo AARIS_DROP_WORKTREE_FAILED; exit 15;
    }
fi
test "$(git -C "$PROJECT" rev-parse HEAD)" = "$SHA" || {
  echo AARIS_DROP_WORKTREE_MISMATCH; exit 16;
}
if python3 "$PROJECT/Tools/Codespaces/restore_preserved_source_lfs.py" "$PROJECT" >/dev/null 2>&1; then
  echo AARIS_DROP_LOCAL_FIRST_LFS_VERIFIED
elif git -C "$PROJECT" lfs pull >/dev/null 2>&1; then
  echo AARIS_DROP_REMOTE_LFS_VERIFIED
else
  echo AARIS_DROP_LFS_NOT_HYDRATED; exit 17
fi
# Copy ALL real working-tree tracked edits from the existing Codespace to temp,
# except protected player/probe scripts. Never change the source or its git index.
# Dot files, symlinks, credentials, Library, Temp, hidden secret dirs are excluded.
if test "$MODE" = hud_review || test "$MODE" = v3_review; then
  # Exactly the published reviewed player/HUD/parachute code, never a
  # local tracked overlay from an unrelated GUI session.
  echo AARIS_DROP_REVIEW_IMMUTABLE_REF_NO_WORKSPACE_OVERLAY
  echo AARIS_DROP_REVIEW_FEATURE="$FEATURE"
else
python3 - "$SOURCE" "$PROJECT" "$SHA" <<'PY'
import os, subprocess, sys
from pathlib import Path
import shutil
src, dst = (Path(sys.argv[1]), Path(sys.argv[2]))
changed = subprocess.check_output(
    ['git','-C',str(src),'diff','--name-only','-z','HEAD','--','Assets','ProjectSettings','Packages']
).decode('utf-8','replace').split('\0')
changed += subprocess.check_output(
    ['git','-C',str(src),'diff','--cached','--name-only','-z','--','Assets','ProjectSettings','Packages']
).decode('utf-8','replace').split('\0')
# Never copy changed C# source over the exact tested feature implementation.
protected={
 'Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs',
 'Assets/AarisWorld/Scripts/AarisCharacterVisual.cs',
 'Assets/AarisWorld/Editor/AarisOfflineDropPlayProbe.cs'
}
safe_ext={'.asset','.unity','.prefab','.mat','.meta','.shader','.png','.jpg',
          '.jpeg','.tga','.fbx','.obj','.mtl','.json','.cs','.controller',
          '.anim','.overridecontroller','.asmdef','.uss','.uxml','.terrainlayer'}
count=0; total=0
for relative in sorted(set(filter(None,changed))):
    if relative in protected:
        print("AARIS_DROP_PROTECTED_SOURCE_CONFLICT")
        sys.exit(18)
    path=Path(relative)
    if (path.parts[0] not in ('Assets','Packages','ProjectSettings')
        or any(p.startswith('.') for p in path.parts)
        or path.suffix.lower() not in safe_ext):
        continue
    old=src/path
    new=dst/path
    if not old.is_file() or old.is_symlink() or new.is_symlink(): continue
    size=old.stat().st_size
    total+=size
    if total>24*1024**3 or count>600:
        print("AARIS_DROP_OVERLAY_TOO_LARGE")
        sys.exit(19)
    new.parent.mkdir(parents=True,exist_ok=True)
    shutil.copy2(old,new)
    count+=1
print("AARIS_DROP_LOCAL_MODIFICATIONS_COPIED")
print("overlay_files="+str(count))
print("overlay_bytes="+str(total))
PY
fi
SCENE="$PROJECT/Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity"
test -s "$SCENE" || { echo AARIS_DROP_SCENE_MISSING; exit 21; }
if head -c 110 "$SCENE" | grep -q 'version https://git-lfs.github.com/spec/v1'; then
  echo AARIS_DROP_SCENE_IS_LFS_POINTER; exit 22
fi
test -s "$PROJECT/Assets/AarisWorld/Editor/AarisOfflineDropPlayProbe.cs" || {
  echo AARIS_DROP_PROBE_SOURCE_MISSING; exit 23;
}
bash "$PROJECT/.devcontainer/start_screen.sh" >/dev/null 2>&1 || {
  echo AARIS_DROP_DISPLAY_INIT_FAILED; exit 24;
}
DISPLAY=:99 xdpyinfo >/dev/null 2>&1 || {
  echo AARIS_DROP_DISPLAY_MISSING; exit 25;
}
rm -f "$RUN/report.json" "$RUN/drop.mp4"
find "$RUN/frames" -maxdepth 1 -name 'frame_*.jpg' -type f -delete
find "$RUN/shots" -maxdepth 1 -name '*.png' -type f -delete
echo AARIS_DROP_NATIVE_PLAYMODE_STARTED

set +e
timeout --signal=TERM 1500 env DISPLAY=:99 TMPDIR="$RUN/UnityTemp" \
  LIBGL_ALWAYS_SOFTWARE=1 "$EDITOR" -batchmode -force-glcore \
  -projectPath "$PROJECT" -executeMethod Aaris.MapReview.Editor.AarisOfflineDropPlayProbe.Record \
  -logFile "$RUN/unity-private.log" >"$RUN/stdout-private.log" 2>"$RUN/stderr-private.log"
UNITY_RC=$?
set -e
if test ! -s "$RUN/report.json"; then
  if grep -Eqi 'error CS[0-9]{4}' "$RUN/unity-private.log" 2>/dev/null; then
    echo AARIS_DROP_COMPILE_FAILED
  elif grep -Eqi 'licen[sc]e.*(invalid|missing|failed|expired)' "$RUN/unity-private.log" 2>/dev/null; then
    echo AARIS_DROP_LICENSE_EDITOR_REJECTED
  elif test "$UNITY_RC" -eq 124; then
    echo AARIS_DROP_PLAYMODE_TIMED_OUT
  else
    echo AARIS_DROP_REPORT_MISSING
  fi
  exit 26
fi
# Encode the REAL Unity camera captures using software-only MP4.
if ! command -v ffmpeg >/dev/null 2>&1; then
  if command -v sudo >/dev/null 2>&1; then
    sudo DEBIAN_FRONTEND=noninteractive apt-get update -qq >/dev/null 2>&1 || true
    sudo DEBIAN_FRONTEND=noninteractive apt-get install -qq -y ffmpeg >/dev/null 2>&1 || true
  fi
fi
if ! command -v ffmpeg >/dev/null 2>&1; then
  echo AARIS_DROP_VIDEO_ENCODER_MISSING
  exit 27
fi
if ! ffmpeg -nostdin -hide_banner -loglevel error -y -framerate 8 \
   -i "$RUN/frames/frame_%05d.jpg" -vcodec libx264 -preset veryfast -crf 28 \
   -pix_fmt yuv420p -movflags +faststart "$RUN/drop.mp4" >/dev/null 2>&1; then
  echo AARIS_DROP_VIDEO_ENCODE_FAILED
  exit 28
fi
python3 - "$RUN/report.json" "$RUN/drop.mp4" <<'PY'
import json, pathlib,sys
r=json.loads(pathlib.Path(sys.argv[1]).read_text())
v=pathlib.Path(sys.argv[2])
if not v.is_file() or v.stat().st_size < 32768 or b'ftyp' not in v.read_bytes()[:64]:
    raise SystemExit('AARIS_DROP_BAD_VIDEO_CONTAINER')
print("AARIS_DROP_VIDEO_FROM_UNITY_CAMERA")
print("video_bytes="+str(v.stat().st_size))
print("play_frames="+str(int(r.get('frames',0))))
print("runtime_errors="+str(int(r.get('errors',0))))
status=r.get('status','failed')
if status=="passed" and r.get('observedOpening') and r.get('observedGliding') and r.get('observedLanded'):
    print("AARIS_DROP_PLAYMODE_VERIFIED")
else:
    print("AARIS_DROP_PLAYMODE_FAILED_REVIEW_VIDEO")
PY
# Return a video and sanitized report even on gameplay failure,
# so that its actual visual/physics bug can be inspected before a PR is merged.
echo AARIS_DROP_VIDEO_AVAILABLE_FOR_REVIEW
