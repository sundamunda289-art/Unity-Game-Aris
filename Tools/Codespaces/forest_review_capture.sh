#!/usr/bin/env bash
# Read-only source and native Editor photo review of one SHA-pinned PR checkout.
# Never touches /workspaces files, dirty owner GUI assets, or checked-in LFS objects.
set -Eeuo pipefail
EXPECTED="${1:?expected-sha}"
[[ "$EXPECTED" =~ ^[a-f0-9]{40}$ ]] || { echo FOREST_REVIEW_BAD_SHA; exit 10; }
SOURCE=/workspaces/Unity-Game-Aris
ROOT=/tmp/aaris-native-photo-worktrees
PROJECT="$ROOT/$EXPECTED"
OUT=/tmp/aaris-codespaces/forest-review
PHOTO=/tmp/aaris-codespaces/scene-probe
EDITOR="$HOME/.local/share/aaris-unity-editors/6000.3.26f1/Editor/Unity"
for candidate in "$EDITOR" /workspaces/.aaris-unity-editors/6000.3.26f1/Editor/Unity /tmp/aaris-unity-runtime/editors/6000.3.26f1/Editor/Unity; do
  if test -x "$candidate"; then EDITOR="$candidate"; break; fi
done
test -x "$EDITOR" || { echo FOREST_REVIEW_EDITOR_MISSING; exit 11; }
if pgrep -f '[E]ditor/Unity.*-projectPath' >/dev/null 2>&1; then
  echo FOREST_REVIEW_EDITOR_BUSY
  exit 12
fi
test -e "$SOURCE/.git" || { echo FOREST_REVIEW_ORIGINAL_SOURCE_UNAVAILABLE; exit 13; }
git -C "$SOURCE" fetch --quiet origin refs/heads/review/native-heavy-forest-v1-20261109 >/dev/null 2>&1 || { echo FOREST_REVIEW_BRANCH_FETCH_FAILED; exit 14; }
ACTUAL="$(git -C "$SOURCE" rev-parse FETCH_HEAD)"
test "$ACTUAL" = "$EXPECTED" || { echo FOREST_REVIEW_REVISION_CHANGED; exit 15; }
mkdir -p "$ROOT" "$OUT" "$PHOTO" "$OUT/UnityTemp"
if test ! -d "$PROJECT"; then
  if test ! -e "$PROJECT" && git -C "$SOURCE" worktree list --porcelain | grep -Fx -- "worktree $PROJECT" >/dev/null; then
    git -C "$SOURCE" worktree remove --force "$PROJECT" >/dev/null 2>&1 || { echo FOREST_REVIEW_STALE_TEMP_WORKTREE; exit 16; }
  fi
  GIT_LFS_SKIP_SMUDGE=1 git -C "$SOURCE" -c filter.lfs.process= -c filter.lfs.smudge=cat -c filter.lfs.required=false \
    worktree add --detach "$PROJECT" "$ACTUAL" >/dev/null 2>&1 || { echo FOREST_REVIEW_WORKTREE_FAILED; exit 16; }
fi
test "$(git -C "$PROJECT" rev-parse HEAD)" = "$EXPECTED" || { echo FOREST_REVIEW_CHECKOUT_MISMATCH; exit 17; }
test -s "$PROJECT/Assets/TerrainDemoScene_URP/Prefabs/Trees/DeepForest/DeepForestPine.prefab" || { echo FOREST_REVIEW_VARIANT_MISSING; exit 18; }
if ! python3 "$PROJECT/Tools/Codespaces/restore_preserved_source_lfs.py" "$PROJECT" >"$OUT/private-lfs.log" 2>&1; then
  echo FOREST_REVIEW_LFS_793_NOT_VERIFIED
  exit 19
fi
echo FOREST_REVIEW_793_LFS_VERIFIED
bash "$PROJECT/.devcontainer/start_screen.sh" >/dev/null 2>&1 || { echo FOREST_REVIEW_DISPLAY_FAILED; exit 20; }
DISPLAY=:99 xdpyinfo >/dev/null 2>&1 || { echo FOREST_REVIEW_XVFB_MISSING; exit 20; }
cd "$PROJECT"
for spec in 'town 350 220 90 35 85' 'forest 418 -350 40 18 30'; do
  read -r label x z yaw pitch distance <<<"$spec"
  rm -f "$PHOTO/photo-detail.png" "$PHOTO/photo-context.png" "$PHOTO/photo-report.json"
  set +e
  timeout --signal=TERM 1450 env DISPLAY=:99 TMPDIR="$OUT/UnityTemp" LIBGL_ALWAYS_SOFTWARE=1 \
     "$EDITOR" -batchmode -quit -force-glcore -projectPath "$PROJECT" \
     -executeMethod Aaris.MapReview.Editor.AarisCodespaceSceneProbe.CaptureAt \
     "-aarisX=$x" "-aarisZ=$z" "-aarisYaw=$yaw" "-aarisPitch=$pitch" "-aarisDistance=$distance" \
     -logFile "$OUT/private-${label}-editor.log" >"$OUT/private-${label}-stdout.log" 2>"$OUT/private-${label}-stderr.log"
  rc=$?
  set -e
  if test "$rc" -ne 0; then
    if grep -Eqi 'error CS[0-9]{4}' "$OUT/private-${label}-editor.log" 2>/dev/null; then
      echo FOREST_REVIEW_CSHARP_COMPILE_FAILED
    else
      echo FOREST_REVIEW_NATIVE_EDITOR_FAILED
    fi
    exit 21
  fi
  python3 - "$PHOTO" "$label" "$x" "$z" <<'PYVERIFY'
import json,pathlib,sys
folder,label,x,z=sys.argv[1:]
folder=pathlib.Path(folder)
report=json.loads((folder/"photo-report.json").read_text())
assert report.get("status")=="passed" and report.get("terrainTiles")==4
assert report.get("unityVersion")=="6000.3.26f1"
assert abs(report["targetX"]-float(x))<.2 and abs(report["targetZ"]-float(z))<.2
assert min(report.get("detailVariation",0),report.get("contextVariation",0))>=6
for mode in ("detail","context"):
    f=folder/("photo-"+mode+".png")
    assert f.stat().st_size>8192 and f.open("rb").read(8)==b"\x89PNG\r\n\x1a\n"
PYVERIFY
  cp "$PHOTO/photo-detail.png" "$OUT/${label}-detail.png"
  cp "$PHOTO/photo-context.png" "$OUT/${label}-context.png"
  cp "$PHOTO/photo-report.json" "$OUT/${label}-report.json"
  echo "FOREST_REVIEW_${label^^}_NATIVE_PHOTO_PASSED"
done
echo FOREST_REVIEW_RENDER_COMPLETED
