#!/usr/bin/env bash
# ONE-TIME native Unity fog A/B evidence on an isolated review-commit worktree.
# Revalidate the existing saved-scene daylight comparison after APK publishing; no source asset changes.
# NEVER clean, stash, reset or change the dirty original Codespace project.
set -Eeuo pipefail
SHA="$1"
[[ "$SHA" =~ ^[0-9a-f]{40}$ ]] || { echo REVIEW_SHA_INVALID; exit 10; }
SOURCE=/workspaces/Unity-Game-Aris
test -e "$SOURCE/.git" || { echo REVIEW_SOURCE_MISSING; exit 11; }
OUT=/tmp/aaris-codespaces/daylight-preview
mkdir -p "$OUT"
if pgrep -f '[E]ditor/Unity.*-projectPath' >/dev/null 2>&1; then
  echo REVIEW_EDITOR_ALREADY_RUNNING; exit 12
fi
EDITOR=""
for candidate in "$HOME/.local/share/aaris-unity-editors/6000.3.26f1/Editor/Unity" \
  /workspaces/.aaris-unity-editors/6000.3.26f1/Editor/Unity \
  /tmp/aaris-unity-runtime/editors/6000.3.26f1/Editor/Unity; do
  if test -x "$candidate"; then EDITOR="$candidate"; break; fi
done
test -n "$EDITOR" || { echo REVIEW_EDITOR_MISSING; exit 13; }
git -C "$SOURCE" fetch --quiet origin refs/heads/fix/clear-daylight-no-fog || {
  echo REVIEW_BRANCH_FETCH_FAILED; exit 14;
}
FETCHED="$(git -C "$SOURCE" rev-parse --verify FETCH_HEAD)"
test "$FETCHED" = "$SHA" || { echo REVIEW_SHA_CHANGED_OR_INVALID; exit 15; }
PROJECT="/tmp/aaris-native-photo-worktrees/$SHA"
mkdir -p /tmp/aaris-native-photo-worktrees
if test ! -d "$PROJECT"; then
  GIT_LFS_SKIP_SMUDGE=1 git -C "$SOURCE" \
    -c filter.lfs.process= -c filter.lfs.smudge=cat -c filter.lfs.required=false \
    worktree add --detach "$PROJECT" "$SHA" >/dev/null 2>&1 || {
    echo REVIEW_ISOLATED_WORKTREE_FAILED; exit 16;
  }
fi
test "$(git -C "$PROJECT" rev-parse HEAD)" = "$SHA" || {
  echo REVIEW_WORKTREE_REV_MISMATCH; exit 17;
}
cd "$PROJECT"
# Restore SHA-verified native LFS through the same read-only owner-source,
# authenticated original tree archive and isolated object-cache process as
# genuine native photo probes. This requires /tmp/aaris-native-photo-worktrees.
# Never mutate the original owner GUI tree or replace actual .unity assets.
if ! python3 Tools/Codespaces/restore_preserved_source_lfs.py "$PROJECT" \
    >"$OUT/private-preserved-lfs.log" 2>&1; then
  echo REVIEW_VERIFIED_LFS_RECOVERY_FAILED
  exit 18
fi
python3 Tools/Codespaces/verify_lfs.py >"$OUT/private-lfs-audit.log" 2>&1 || {
  echo REVIEW_ALL_LFS_NOT_HYDRATED; exit 18;
}
echo REVIEW_793_AUTHENTIC_LFS_ASSETS_VERIFIED
test -s Assets/AarisWorld/Editor/AarisDaylightFogReview.cs || {
  echo REVIEW_CSHARP_TEST_MISSING; exit 19;
}
grep -q '^  m_Fog: 1$' Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity || {
  echo REVIEW_FOG_CORE_CHANGE_MISSING; exit 20;
}
bash .devcontainer/start_screen.sh >/dev/null || { echo REVIEW_XVFB_FAILED; exit 21; }
rm -f "$OUT"/forest-fog-on.png "$OUT"/forest-fog-off.png \
  "$OUT"/clocktower-fog-on.png "$OUT"/clocktower-fog-off.png \
  "$OUT"/daylight-report.json "$OUT"/daylight-bundle.tar.gz
mkdir -p "$OUT/unity-temp"
rc=0
timeout --signal=TERM --kill-after=15s 1500 \
  env DISPLAY=:99 LIBGL_ALWAYS_SOFTWARE=1 GALLIUM_DRIVER=llvmpipe \
      TMPDIR="$OUT/unity-temp" "$EDITOR" \
  -batchmode -quit -force-glcore -projectPath "$PROJECT" \
  -executeMethod Aaris.MapReview.Editor.AarisDaylightFogReview.Capture \
  -logFile "$OUT/private-unity-editor.log" \
  >"$OUT/private-stdout.log" 2>"$OUT/private-stderr.log" || rc=$?
if test "$rc" != 0; then
  if grep -Eqi 'error CS[0-9]{4}' "$OUT/private-unity-editor.log"; then
    echo REVIEW_UNITY_CSHARP_COMPILE_ERROR
  else
    echo REVIEW_UNITY_EDITOR_RENDER_FAILED
  fi
  exit 22
fi
python3 - "$OUT" <<'PY'
import json,sys
from pathlib import Path
p=Path(sys.argv[1])
d=json.loads((p/"daylight-report.json").read_text())
assert d.get("status")=="passed" and d.get("terrainTiles")==4
assert not d.get("nativeFogDisabled") and 0 < d.get("nativeFogDensity",0) <= .001
assert d.get("sunIntensity",0)>=1.3
shots=d.get("shots",[])
assert len(shots)==2
for shot in shots:
  for key in ("baselineImage","clearImage"):
    f=p/shot[key]
    assert f.is_file() and 8192<f.stat().st_size<12*1024*1024
    assert f.read_bytes()[:8]==b"\x89PNG\r\n\x1a\n"
  assert shot["baselineVariance"]>=6 and shot["clearVariance"]>=6
print("REVIEW_REAL_UNITY_DAYLIGHT_AB_VALIDATED")
PY
tar -C "$OUT" -czf "$OUT/daylight-bundle.tar.gz" daylight-report.json \
  forest-fog-on.png forest-fog-off.png \
  clocktower-fog-on.png clocktower-fog-off.png
echo REVIEW_FOG_AB_REAL_UNITY_PNGS_READY
