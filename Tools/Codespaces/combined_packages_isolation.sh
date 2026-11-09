#!/usr/bin/env bash
# Diagnostic of Unity settings+packages WITHOUT source Assets, protected workspace untouched.
set -Eeuo pipefail
SOURCE=/workspaces/Unity-Game-Aris
REV=f4c5ca4ace2b055757fabaed158977256ffbcfe5
ORIG=/tmp/aaris-native-photo-worktrees/$REV
BASE=/tmp/aaris-codespaces
EDITOR=""
for candidate in "$HOME/.local/share/aaris-unity-editors/6000.3.26f1/Editor/Unity" \
 "/workspaces/.aaris-unity-editors/6000.3.26f1/Editor/Unity" \
 "/tmp/aaris-unity-runtime/editors/6000.3.26f1/Editor/Unity"; do
 if test -x "$candidate"; then EDITOR="$candidate"; break; fi
done
if test -z "$EDITOR"; then echo AARIS_COMBINED_EDITOR_MISSING; exit 21; fi
test -d "$ORIG" && test ! -L "$ORIG" || { echo AARIS_COMBINED_ORIG_MISSING; exit 22; }
test "$(git -C "$ORIG" rev-parse HEAD)" = "$REV" || { echo AARIS_COMBINED_COMMIT_MISMATCH; exit 23; }
git -C "$SOURCE" worktree list --porcelain | grep -Fx "worktree $ORIG" >/dev/null || {
 echo AARIS_COMBINED_NOT_REGISTERED; exit 24;
}
if pgrep -f '[E]ditor/Unity.*-projectPath' >/dev/null; then
 echo AARIS_COMBINED_EDITOR_BUSY; exit 25
fi
free_mib() {
 python3 - <<'PY'
import os
v=os.statvfs('/tmp')
print(v.f_bavail*v.f_frsize//1048576)
PY
}
echo "AARIS_COMBINED_SCRATCH_MIB=$(free_mib)"
if (("$(free_mib)"<4500)); then echo AARIS_COMBINED_NO_SCRATCH; exit 26; fi
umask 077
mkdir -p "$BASE"
WORK="$(mktemp -d "$BASE/combined-settings-packages.XXXXXXXX")"
case "$WORK" in "$BASE"/combined-settings-packages.*) ;; *) exit 27;; esac
trap 'rm -rf -- "$WORK"' EXIT
make_test() {
 local name="$1"
 mkdir -p "$WORK/$name/Assets" "$WORK/$name/Packages" "$WORK/$name/ProjectSettings" "$WORK/$name/Temp"
 cp -a "$ORIG/ProjectSettings/." "$WORK/$name/ProjectSettings/"
 cp -a "$ORIG/Packages/." "$WORK/$name/Packages/"
 printf 'm_EditorVersion: 6000.3.26f1\n' > "$WORK/$name/ProjectSettings/ProjectVersion.txt"
}
classify() {
 local label="$1" log="$2"
 python3 - "$label" "$log" <<'PY'
from pathlib import Path
import re,sys
label,loc=sys.argv[1:]
p=Path(loc)
if not p.is_file():print("AARIS_COMBINED_"+label+"_LOG_MISSING");raise SystemExit(0)
with p.open('rb') as h:
 h.seek(max(0,p.stat().st_size-3*1024*1024))
 s=h.read().decode('utf-8','replace')
for key,pat in [
 ("UPM_ERROR",r"package manager.+(?:error|failed)|dependency resolution.+(?:failed|error)"),
 ("IMPORT_ERROR",r"(?:import worker|asset database).+(?:error|fail)"),
 ("CS_ERROR",r"error CS[0-9]{4}|Compilation failed"),
 ("SHADER_ERROR",r"Shader error in|Failed to compile shader"),
 ("LICENSE_ERROR",r"no valid.{0,25}licen[sc]e|licen[sc]e.{0,40}(?:invalid|failed|expired)"),
 ("DISK_FULL",r"No space left on device"),
 ("EDITOR_EXIT",r"Exiting batchmode|EditorApplication.Exit"),
 ("CRASH",r"SIGSEGV|segmentation fault|signal 11"),
 ("PACKAGE_UNSUPPORTED",r"not supported|unsupported"),
 ("NULL_REF",r"NullReferenceException"),
 ("INDEX_ERROR",r"IndexOutOfRangeException"),
 ("JSON_ERROR",r"(?:invalid|malformed) (?:json|manifest)"),
 ]:
 print("AARIS_COMBINED_"+label+"_"+key+"="+("yes" if re.search(pat,s,re.I) else "no"))
PY
}
probe() {
 local label="$1" dir="$2" rc=0
 echo "AARIS_COMBINED_${label}_STARTED"
 timeout --kill-after=10s 205s env TMPDIR="$dir/Temp" "$EDITOR" \
   -batchmode -nographics -quit -projectPath "$dir" \
   -logFile "$WORK/private-$label-editor.log" \
   > "$WORK/private-$label-stdout.log" 2>"$WORK/private-$label-stderr.log" || rc=$?
 echo "AARIS_COMBINED_${label}_EXIT=$rc"
 case "$rc" in
   0) echo "AARIS_COMBINED_${label}_PASS";;
   124|143) echo "AARIS_COMBINED_${label}_TIMEOUT";;
   139) echo "AARIS_COMBINED_${label}_SEGFAULT";;
   137) echo "AARIS_COMBINED_${label}_OOM_OR_KILLED";;
   *) echo "AARIS_COMBINED_${label}_FAIL_OTHER";;
 esac
 classify "$label" "$WORK/private-$label-editor.log"
 echo "$rc" > "$WORK/rc-$label"
}
make_test original
probe ORIGINAL "$WORK/original"
rc="$(cat "$WORK/rc-ORIGINAL")"
if ((rc==0)); then
  echo AARIS_COMBINED_BASELINE_PASS_ASSETS_ARE_NEXT_SUSPECT
else
  echo AARIS_COMBINED_BASELINE_NOT_PASS
  if (("$(free_mib)">3300)); then
    make_test without_vfx
    python3 - "$WORK/without_vfx/Packages" <<'PY'
import json,sys
from pathlib import Path
folder=Path(sys.argv[1]);p=folder/'manifest.json'
data=json.loads(p.read_text())
removed=data['dependencies'].pop('com.unity.visualeffectgraph',None)
assert removed=='17.3.0'
p.write_text(json.dumps(data,indent=2)+'\n')
# Drop only temporary lock, to allow the resolver to regenerate it properly.
(folder/'packages-lock.json').unlink(missing_ok=True)
PY
    probe WITHOUT_VFX "$WORK/without_vfx"
    if test "$(cat "$WORK/rc-WITHOUT_VFX")" = 0; then
      echo AARIS_COMBINED_VFX_GRAPH_ISOLATION_CANDIDATE
    fi
  else echo AARIS_COMBINED_VFX_TEST_SKIPPED_NO_SPACE; fi
fi
echo "AARIS_COMBINED_TMP_MIB_PRE_CLEANUP=$(free_mib)"
echo AARIS_COMBINED_SAFE_DIAGNOSTICS_COMPLETE
