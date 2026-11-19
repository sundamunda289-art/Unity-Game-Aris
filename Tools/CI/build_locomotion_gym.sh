#!/usr/bin/env bash
# Disposable movement-only Unity 6 Android project. Never modify the real workspace.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
[[ -f "$ROOT/Packages/manifest.json" ]] || { echo "Not Unity project root"; exit 2; }
BIN="${UNITY_BIN:-/opt/unity/Editor/Unity}"
[[ -x "$BIN" ]] || { echo "Unity Editor missing: $BIN (set UNITY_BIN)"; exit 3; }
[[ -z "$(git -C "$ROOT" status --porcelain --untracked-files=no)" ]] || {
  echo "Workspace has tracked edits; building from checked-in files is preferred." >&2
}
STAGE="${AARIS_GYM_STAGE_DIR:-/tmp/aaris-locomotion-gym-project}"
OUT="${AARIS_GYM_OUTPUT_DIR:-/tmp/aaris-locomotion-gym-output}"
mkdir -p "$OUT"
# Single Unity build per staging directory: no parallel editor/file races.
exec 9>"$OUT/.build.lock"
if ! flock -n 9; then
  echo "Another movement-gym build is already running." >&2
  exit 6
fi
if pgrep -x Unity >/dev/null 2>&1; then
  echo "Unity Editor already running. Not starting a competing Editor." >&2
  exit 7
fi
mkdir -p "$STAGE"
echo "Isolated Unity project: $STAGE"
echo "Output directory: $OUT"
mkdir -p "$STAGE/Assets/AarisWorld" "$STAGE/Assets/Resources" \
  "$STAGE/Assets/TerrainDemoScene_URP/Settings"
# Keep Unity's expensive Library cache but remove stale staged source assets.
# Only disposable staging paths are touched; the original working tree is safe.
rm -rf "$STAGE/Assets/AarisWorld/Scripts" \
  "$STAGE/Assets/AarisWorld/Editor" \
  "$STAGE/Assets/AarisWorld/Player" \
  "$STAGE/Assets/Resources/AarisCharacters" \
  "$STAGE/Assets/Resources/AarisMocap"
cp -a "$ROOT/ProjectSettings" "$ROOT/Packages" "$STAGE/"
cp -a "$ROOT/Assets/AarisWorld/Scripts" "$ROOT/Assets/AarisWorld/Editor" \
  "$ROOT/Assets/AarisWorld/Player" "$STAGE/Assets/AarisWorld/"
cp -a "$ROOT/Assets/Resources/AarisCharacters" \
  "$ROOT/Assets/Resources/AarisMocap" "$STAGE/Assets/Resources/"
# Keep only neutral locomotion motions, not unrelated animation content.
ANIMS="$STAGE/Assets/Resources/AarisCharacters/Primary/Animations"
if [[ -d "$ANIMS" ]]; then
  for f in "$ANIMS"/*.fbx; do
    [[ -f "$f" ]] || continue
    case "${f##*/}" in
      idle.fbx|walk.fbx|run.fbx|sprint.fbx|crouch.fbx|crouch_walk.fbx|prone.fbx|crawl.fbx|jump.fbx|jump_start.fbx|jump_land.fbx|fall.fbx) ;;
      *) rm -f "$f" "$f.meta" ;;
    esac
  done
fi
cp -a "$ROOT/Assets/TerrainDemoScene_URP/Settings/URP" \
  "$STAGE/Assets/TerrainDemoScene_URP/Settings/"
# Fail fast if Unity would import 100-byte LFS pointers rather than actual FBX assets.
while IFS= read -r -d '' f; do
  if head -c 90 "$f" | grep -aq 'version https://git-lfs.github.com/spec/v1'; then
    echo "Unhydrated Git LFS asset: $f" >&2
    exit 5
  fi
done < <(find "$STAGE/Assets" -type f \( -iname '*.fbx' -o -iname '*.asset' \) -print0)
export AARIS_GYM_APK_PATH="$OUT/AarisLocomotionGym.apk"
echo "Starting Unity Android Gym build (scene only, filtered Resources)."
"$BIN" -batchmode -nographics -quit -projectPath "$STAGE" -buildTarget Android \
  -executeMethod Aaris.MapReview.Editor.AarisLocomotionGymBuilder.BuildGymAndroid \
  -logFile "$OUT/unity-gym-build.log"
test -s "$AARIS_GYM_APK_PATH"
sha256sum "$AARIS_GYM_APK_PATH" > "$OUT/AarisLocomotionGym.sha256"
echo "AARIS_LOCOMOTION_GYM_READY=$AARIS_GYM_APK_PATH"
echo "Reusable isolated project/cache at $STAGE (no source map changes)."
