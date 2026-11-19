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
STAGE="$(mktemp -d /tmp/aaris-locomotion-gym.XXXXXX)"
OUT="${AARIS_GYM_OUTPUT_DIR:-/tmp/aaris-locomotion-gym-output}"
mkdir -p "$OUT"
echo "Isolated Unity project: $STAGE"
echo "Output directory: $OUT"
mkdir -p "$STAGE/Assets/AarisWorld" "$STAGE/Assets/Resources" \
  "$STAGE/Assets/TerrainDemoScene_URP/Settings"
cp -a "$ROOT/ProjectSettings" "$ROOT/Packages" "$STAGE/"
cp -a "$ROOT/Assets/AarisWorld/Scripts" "$ROOT/Assets/AarisWorld/Editor" \
  "$ROOT/Assets/AarisWorld/Player" "$STAGE/Assets/AarisWorld/"
cp -a "$ROOT/Assets/Resources/AarisCharacters" \
  "$ROOT/Assets/Resources/AarisMocap" "$STAGE/Assets/Resources/"
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
echo "Project staging remains at $STAGE for diagnostics/cache reuse; remove when safe."
