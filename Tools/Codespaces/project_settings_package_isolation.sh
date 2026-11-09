#!/usr/bin/env bash
# One-shot, isolated Unity project configuration A/B diagnosis.
# No source modification, no actual game Assets or native LFS edits.
set -Eeuo pipefail
SOURCE=/workspaces/Unity-Game-Aris
REV=f4c5ca4ace2b055757fabaed158977256ffbcfe5
ORIGINAL=/tmp/aaris-native-photo-worktrees/$REV
BASE=/tmp/aaris-codespaces
EDITOR=""
for candidate in "$HOME/.local/share/aaris-unity-editors/6000.3.26f1/Editor/Unity" \
 "/workspaces/.aaris-unity-editors/6000.3.26f1/Editor/Unity" \
 "/tmp/aaris-unity-runtime/editors/6000.3.26f1/Editor/Unity"; do
  if test -x "$candidate"; then EDITOR="$candidate"; break; fi
done
if test -z "$EDITOR"; then echo AARIS_AB_EDITOR_UNAVAILABLE; exit 21; fi
if test ! -d "$ORIGINAL" || test -L "$ORIGINAL"; then
  echo AARIS_AB_SOURCE_UNAVAILABLE; exit 22
fi
if test "$(git -C "$ORIGINAL" rev-parse HEAD 2>/dev/null)" != "$REV"; then
  echo AARIS_AB_SOURCE_REVISION_MISMATCH; exit 23
fi
if ! git -C "$SOURCE" worktree list --porcelain |
   grep -Fx "worktree $ORIGINAL" >/dev/null; then
  echo AARIS_AB_SOURCE_NOT_REGISTERED; exit 24
fi
if pgrep -f '[E]ditor/Unity.*-projectPath' >/dev/null 2>&1; then
  echo AARIS_AB_EDITOR_BUSY; exit 25
fi
free_mb() {
  python3 - <<'PY'
import os
v=os.statvfs('/tmp')
print(v.f_bavail*v.f_frsize//1048576)
PY
}
before="$(free_mb)"
echo "AARIS_AB_TMP_FREE_MIB_BEFORE=$before"
if ((before<4500)); then echo AARIS_AB_LOW_SCRATCH; exit 26; fi
test -f "$ORIGINAL/ProjectSettings/ProjectSettings.asset" || {
  echo AARIS_AB_SETTINGS_MISSING; exit 27;
}
test -f "$ORIGINAL/Packages/manifest.json" || {
  echo AARIS_AB_PACKAGES_MISSING; exit 28;
}
mkdir -p "$BASE"
umask 077
WORK="$(mktemp -d "$BASE/project-config-ab.XXXXXXXX")"
case "$WORK" in "$BASE"/project-config-ab.*) ;; *) echo AARIS_AB_SCRATCH_PATH_REFUSED; exit 29;; esac
trap 'rm -rf -- "$WORK"' EXIT
function make_project() {
  local name="$1"
  mkdir -p "$WORK/$name/Assets" "$WORK/$name/ProjectSettings" "$WORK/$name/Packages" "$WORK/$name/Temp"
  printf 'm_EditorVersion: 6000.3.26f1\n' > "$WORK/$name/ProjectSettings/ProjectVersion.txt"
}
function classify_log() {
  local label="$1" file="$2"
  python3 - "$label" "$file" <<'PY'
import pathlib,re,sys
label,path=sys.argv[1:]
f=pathlib.Path(path)
if not f.is_file():
 print("AARIS_AB_"+label+"_LOG_MISSING"); raise SystemExit(0)
with f.open("rb") as h:
 h.seek(max(0,f.stat().st_size-2*1024*1024))
 log=h.read().decode("utf-8","replace")
patterns={
 "ASSET_IMPORT":r"importing.+assets|import worker|asset database",
 "PACKAGE_MANAGER":r"Package Manager|UPM|package resolution",
 "SHADER":r"ShaderCompiler|Shader error in|failed to compile shader",
 "CS_ERROR":r"error CS[0-9]{4}|Compilation failed",
 "LICENSE":r"no valid.{0,24}licen[sc]e|licen[sc]e.{0,40}(invalid|expired|failed)",
 "DISK_FULL":r"No space left on device",
 "EDITOR_EXIT":r"Exiting batchmode|EditorApplication.Exit",
 "UNSUPPORTED":r"not supported|unsupported",
}
for k,regex in patterns.items():
 print("AARIS_AB_"+label+"_"+k+"="+("yes" if re.search(regex,log,re.I) else "no"))
PY
}
function probe() {
  local label="$1" root="$2" code=0
  echo "AARIS_AB_${label}_STARTED"
  timeout --kill-after=12s 155s env TMPDIR="$root/Temp" \
    "$EDITOR" -batchmode -nographics -quit -projectPath "$root" \
    -logFile "$WORK/private-$label-editor.log" >"$WORK/private-$label-stdout.log" \
    2>"$WORK/private-$label-stderr.log" || code=$?
  echo "AARIS_AB_${label}_EXIT=$code"
  case "$code" in
    0) echo "AARIS_AB_${label}_PASS" ;;
    139) echo "AARIS_AB_${label}_NATIVE_SIGSEGV" ;;
    124|143) echo "AARIS_AB_${label}_TIMEOUT" ;;
    137) echo "AARIS_AB_${label}_KILLED_OR_OOM" ;;
    *) echo "AARIS_AB_${label}_FAIL_OTHER" ;;
  esac
  classify_log "$label" "$WORK/private-$label-editor.log"
  printf '%s\n' "$code"
}
make_project settings
cp -a "$ORIGINAL/ProjectSettings/." "$WORK/settings/ProjectSettings/"
# Keep a strict current Unity version when source version differs.
printf 'm_EditorVersion: 6000.3.26f1\n' > "$WORK/settings/ProjectSettings/ProjectVersion.txt"
r1="$(probe SETTINGS "$WORK/settings" | tee /dev/stderr | tail -n 1)"
if ((r1==139)); then
 echo AARIS_AB_DIAG_CONFIGURATION_ONLY_CRASHES
elif ((r1==0)); then
 echo AARIS_AB_DIAG_CONFIGURATION_ONLY_OK
fi
if (("$(free_mb)"<2500)); then
 echo AARIS_AB_STOPPED_LOW_SCRATCH
 exit 0
fi
make_project packages
cp -a "$ORIGINAL/Packages/." "$WORK/packages/Packages/"
r2="$(probe PACKAGES "$WORK/packages" | tee /dev/stderr | tail -n 1)"
if ((r2==139)); then
 echo AARIS_AB_DIAG_PACKAGES_ONLY_CRASHES
elif ((r2==0)); then
 echo AARIS_AB_DIAG_PACKAGES_ONLY_OK
fi
if ((r1==0 && r2==0 && "$(free_mb)">2500)); then
 make_project combined
 cp -a "$ORIGINAL/ProjectSettings/." "$WORK/combined/ProjectSettings/"
 cp -a "$ORIGINAL/Packages/." "$WORK/combined/Packages/"
 printf 'm_EditorVersion: 6000.3.26f1\n' > "$WORK/combined/ProjectSettings/ProjectVersion.txt"
 r3="$(probe COMBINED "$WORK/combined" | tee /dev/stderr | tail -n 1)"
 if ((r3==139)); then echo AARIS_AB_DIAG_INTERACTION_CRASHES; fi
 if ((r3==0)); then echo AARIS_AB_DIAG_CONFIGURATION_PACKAGES_PASS; fi
else
 echo AARIS_AB_DIAG_COMBINED_SKIPPED
fi
echo "AARIS_AB_TMP_FREE_MIB_END=$(free_mb)"
echo AARIS_AB_ISOLATION_COMPLETE
