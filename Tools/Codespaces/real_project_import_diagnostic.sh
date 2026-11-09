#!/usr/bin/env bash
# Non-graphical project import and software Vulkan capability fingerprint.
# Uses existing immutable-SHA isolated worktree, never the dirty live workspace.
set -Eeuo pipefail
SOURCE=/workspaces/Unity-Game-Aris
SHA=f4c5ca4ace2b055757fabaed158977256ffbcfe5
PROJECT=/tmp/aaris-native-photo-worktrees/$SHA
OUT=/tmp/aaris-codespaces/import-only-review
EDITOR=""
for path in "$HOME/.local/share/aaris-unity-editors/6000.3.26f1/Editor/Unity" \
 "/workspaces/.aaris-unity-editors/6000.3.26f1/Editor/Unity" \
 "/tmp/aaris-unity-runtime/editors/6000.3.26f1/Editor/Unity"; do
  if test -x "$path"; then EDITOR="$path"; break; fi
done
test -n "$EDITOR" || { echo AARIS_IMPORT_EDITOR_MISSING; exit 21; }
test -d "$PROJECT" && test ! -L "$PROJECT" || { echo AARIS_IMPORT_WORKTREE_MISSING; exit 22; }
test "$(git -C "$PROJECT" rev-parse HEAD)" = "$SHA" || { echo AARIS_IMPORT_SHA_REFUSED; exit 23; }
git -C "$SOURCE" worktree list --porcelain | grep -Fx "worktree $PROJECT" >/dev/null || {
  echo AARIS_IMPORT_WORKTREE_NOT_REGISTERED; exit 24;
}
if pgrep -f '[E]ditor/Unity.*-projectPath' >/dev/null 2>&1; then
  echo AARIS_IMPORT_EDITOR_BUSY; exit 25
fi
mkdir -p "$OUT/UnityTemp"
chmod 700 "$OUT"
python3 - <<'PY'
from pathlib import Path
import os,shutil,subprocess,re
d=os.statvfs("/tmp")
print("AARIS_IMPORT_SCRATCH_FREE_MIB="+str(d.f_bavail*d.f_frsize//1048576))
print("AARIS_VULKANINFO_AVAILABLE="+("yes" if shutil.which("vulkaninfo") else "no"))
print("AARIS_VULKAN_ICD_FOLDER_PRESENT="+("yes" if Path("/usr/share/vulkan/icd.d").is_dir() else "no"))
ld=subprocess.run(["ldconfig","-p"],capture_output=True,text=True,timeout=15)
print("AARIS_VULKAN_LOADER_FOUND="+("yes" if "libvulkan.so.1" in ld.stdout else "no"))
if shutil.which("vulkaninfo"):
    try:
        r=subprocess.run(["vulkaninfo","--summary"],capture_output=True,text=True,timeout=20)
        s=r.stdout+"\\n"+r.stderr
        print("AARIS_VULKAN_ENUMERATION_OK="+("yes" if r.returncode==0 else "no"))
        print("AARIS_VULKAN_SOFTWARE_DEVICE_PRESENT="+("yes" if re.search(r"lavapipe|llvmpipe|softpipe",s,re.I) else "no"))
    except (subprocess.TimeoutExpired,OSError):
        print("AARIS_VULKAN_ENUMERATION_UNAVAILABLE")
PY
free_mib="$(python3 - <<'PY'
import os
v=os.statvfs("/tmp")
print(v.f_bavail*v.f_frsize//1048576)
PY
)"
if ((free_mib<3800)); then echo AARIS_IMPORT_INSUFFICIENT_SCRATCH; exit 26; fi
test -s "$PROJECT/Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity" || {
  echo AARIS_IMPORT_SCENE_MISSING; exit 27;
}
if head -c 110 "$PROJECT/Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity" |
    grep -q '^version https://git-lfs.github.com/spec/v1'; then
  echo AARIS_IMPORT_LFS_POINTER_NOT_HYDRATED; exit 28;
fi
echo AARIS_IMPORT_HEADLESS_STARTED
rc=0
timeout --kill-after=12s 330s env TMPDIR="$OUT/UnityTemp" "$EDITOR" \
  -batchmode -nographics -quit -projectPath "$PROJECT" \
  -logFile "$OUT/private-import-only.log" > "$OUT/private-stdout.log" \
  2> "$OUT/private-stderr.log" || rc=$?
echo "AARIS_IMPORT_HEADLESS_EXIT_CODE=$rc"
case "$rc" in
  0) echo AARIS_IMPORT_HEADLESS_SUCCEEDED ;;
  139) echo AARIS_IMPORT_HEADLESS_SIGSEGV ;;
  124|143) echo AARIS_IMPORT_HEADLESS_TIMEOUT ;;
  137) echo AARIS_IMPORT_HEADLESS_KILLED_OR_OOM ;;
  *) echo AARIS_IMPORT_HEADLESS_FAILED ;;
esac
python3 - "$OUT/private-import-only.log" <<'PY'
from pathlib import Path
import re,sys
p=Path(sys.argv[1])
if not p.is_file():
    print("AARIS_IMPORT_PRIVATE_LOG_MISSING")
    raise SystemExit(0)
with p.open("rb") as f:
    f.seek(max(0,p.stat().st_size-4*1024*1024))
    s=f.read().decode("utf-8","replace")
for label,pattern in [
    ("CSHARP_ERROR",r"error CS[0-9]{4}|Compilation failed"),
    ("ASSET_IMPORT_ERROR",r"Failed to import|AssetDatabase.*(?:error|fail)"),
    ("NATIVE_SHADERS",r"UnityShaderCompiler"),
    ("SHADER_EXPLICIT_ERROR",r"Shader error in|Failed to compile shader"),
    ("UNSUPPORTED_RENDERER",r"not supported|unsupported"),
    ("CRASH",r"segmentation fault|SIGSEGV|signal 11"),
    ("DISK",r"No space left on device"),
    ("LICENSE",r"no valid.{0,24}licen[sc]e|licen[sc]e.{0,45}(?:invalid|failed|expired)"),
]:
    print("AARIS_IMPORT_LOG_"+label+"="+("yes" if re.search(pattern,s,re.I) else "no"))
PY
python3 - <<'PY'
import os
v=os.statvfs("/tmp")
print("AARIS_IMPORT_SCRATCH_MIB_AFTER="+str(v.f_bavail*v.f_frsize//1048576))
PY
echo AARIS_IMPORT_READONLY_DIAG_COMPLETE
