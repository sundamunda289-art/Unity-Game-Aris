#!/usr/bin/env bash
# One-shot real map capture with fixed GL 3.3 backend; read-only to Unity source.
# Never reset, clean, checkout, modify source, or copy secret/log content to CI.
set -Eeuo pipefail
SOURCE=/workspaces/Unity-Game-Aris
SHA=f4c5ca4ace2b055757fabaed158977256ffbcfe5
PROJECT=/tmp/aaris-native-photo-worktrees/$SHA
OUT=/tmp/aaris-codespaces/gl33-forest-review
PHOTO=/tmp/aaris-codespaces/scene-probe
EDITOR=""
for candidate in "$HOME/.local/share/aaris-unity-editors/6000.3.26f1/Editor/Unity" \
                 "/workspaces/.aaris-unity-editors/6000.3.26f1/Editor/Unity" \
                 "/tmp/aaris-unity-runtime/editors/6000.3.26f1/Editor/Unity"; do
  if test -x "$candidate"; then EDITOR="$candidate"; break; fi
done
if test -z "$EDITOR"; then echo GL33_EDITOR_MISSING; exit 21; fi
if test ! -d "$PROJECT" || test -L "$PROJECT"; then echo GL33_WORKTREE_MISSING; exit 22; fi
if test "$(git -C "$PROJECT" rev-parse HEAD 2>/dev/null)" != "$SHA"; then
  echo GL33_SHA_REFUSED; exit 23
fi
if ! git -C "$SOURCE" worktree list --porcelain | grep -Fx "worktree $PROJECT" >/dev/null; then
  echo GL33_UNREGISTERED_WORKTREE; exit 24
fi
if pgrep -f '[E]ditor/Unity.*-projectPath' >/dev/null 2>&1; then
  echo GL33_EDITOR_BUSY; exit 25
fi
free_mib="$(python3 - <<'PY'
import os
d=os.statvfs("/tmp")
print(d.f_bavail*d.f_frsize//1048576)
PY
)"
if ((free_mib<4500)); then echo GL33_INSUFFICIENT_SCRATCH; exit 26; fi
echo "GL33_TMP_FREE_MIB_BEFORE=$free_mib"
if test ! -s "$PROJECT/Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity"; then
  echo GL33_SCENE_MISSING; exit 27
fi
if head -c 110 "$PROJECT/Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity" |
     grep -q '^version https://git-lfs.github.com/spec/v1'; then
  echo GL33_SCENE_STILL_LFS_POINTER; exit 28
fi
mkdir -p "$OUT/UnityTemp" "$PHOTO"
chmod 700 "$OUT"
# These are only disposable prior screenshot outputs in the isolated /tmp folder.
rm -f "$PHOTO/photo-detail.png" "$PHOTO/photo-context.png" "$PHOTO/photo-report.json"
bash "$PROJECT/.devcontainer/start_screen.sh" >/dev/null 2>&1 || {
  echo GL33_DISPLAY_START_FAILED; exit 29;
}
DISPLAY=:99 xdpyinfo >/dev/null 2>&1 || {
  echo GL33_DISPLAY_NOT_READY; exit 30;
}
echo GL33_NATIVE_CAPTURE_ATTEMPT_STARTED
rc=0
timeout --kill-after=10s 690s env DISPLAY=:99 TMPDIR="$OUT/UnityTemp" \
     LIBGL_ALWAYS_SOFTWARE=1 "$EDITOR" -batchmode -quit \
     -force-glcore33 -force-clamped -diag-debug-shader-compiler \
     -projectPath "$PROJECT" \
     -executeMethod Aaris.MapReview.Editor.AarisCodespaceSceneProbe.CaptureAt \
     -aarisX=418 -aarisZ=-350 -aarisYaw=40 -aarisPitch=18 -aarisDistance=30 \
     -logFile "$OUT/private-unity-editor.log" \
     > "$OUT/private-stdout.log" 2> "$OUT/private-stderr.log" || rc=$?
echo "GL33_EDITOR_EXIT_CODE=$rc"
if ((rc!=0)); then
  case "$rc" in
    124|143) echo GL33_NATIVE_TIMEOUT ;;
    139) echo GL33_NATIVE_SIGSEGV ;;
    137) echo GL33_NATIVE_KILLED_OR_OOM ;;
    *) echo GL33_NATIVE_NONZERO ;;
  esac
  python3 - "$OUT/private-unity-editor.log" <<'PY'
import pathlib,re,sys
f=pathlib.Path(sys.argv[1])
if f.is_file():
    with f.open("rb") as h:
        h.seek(max(0,f.stat().st_size-4*1024*1024))
        s=h.read().decode("utf-8","replace")
    for label,pattern in [
        ("SHADER_ERROR",r"Shader error in|failed to compile shader"),
        ("UNSUPPORTED_BACKEND",r"not supported|unsupported"),
        ("COMPILER_CRASH",r"UnityShaderCompiler.*(?:crash|fail)"),
        ("CSHARP_COMPILE_ERROR",r"error CS[0-9]{4}"),
        ("LICENSE_ERROR",r"no valid.{0,24}licen[sc]e|licen[sc]e.{0,45}(?:invalid|failed|expired)"),
        ("DISK_PRESSURE",r"No space left on device"),
    ]:
        print("GL33_LOG_"+label+"="+("yes" if re.search(pattern,s,re.I) else "no"))
PY
  exit 31
fi
python3 - "$PHOTO" "$OUT" <<'PY'
import json,pathlib,sys
src=pathlib.Path(sys.argv[1]);dst=pathlib.Path(sys.argv[2])
report_file=src/"photo-report.json"
if not report_file.is_file():
    print("GL33_REPORT_MISSING")
    raise SystemExit(32)
try:
    report=json.loads(report_file.read_text("utf-8"))
    assert report.get("status")=="passed"
    assert report.get("terrainTiles")==4
    assert min(report.get("detailVariation",0),report.get("contextVariation",0))>=6
    assert abs(report.get("targetX",0)-418)<0.2
    assert abs(report.get("targetZ",0)+350)<0.2
except (ValueError,TypeError,AssertionError):
    print("GL33_REPORT_REJECTED")
    raise SystemExit(33)
for kind in ("detail","context"):
    file=src/("photo-"+kind+".png")
    if not file.is_file() or file.stat().st_size<8192 or file.read_bytes()[:8]!=b"\\x89PNG\\r\\n\\x1a\\n":
        print("GL33_FRAME_INVALID")
        raise SystemExit(34)
    (dst/("forest-"+kind+".png")).write_bytes(file.read_bytes())
(dst/"photo-report.json").write_text(json.dumps(report,sort_keys=True),encoding="utf-8")
print("GL33_NATIVE_TERRAIN_PHOTOS_VERIFIED")
print("GL33_TERRAIN_TILES=4")
print("GL33_IMAGES=2")
PY
