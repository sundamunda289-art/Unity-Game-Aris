#!/usr/bin/env bash
# Three separate Unity native imports: packages, 48 runtime game scripts,
# then all 94 runtime + Editor game scripts. Never import the original project.
set -Eeuo pipefail
umask 077
SOURCE=/workspaces/Unity-Game-Aris
if [[ ! -d "$SOURCE/.git" || ! -f "$SOURCE/Packages/manifest.json" ]]; then
  echo AARIS_SOURCE_SPLIT_RESULT=original_missing
  exit 20
fi
EDITOR=""
for candidate in "$HOME/.local/share/aaris-unity-editors/6000.3.26f1/Editor/Unity" \
                 /workspaces/.aaris-unity-editors/6000.3.26f1/Editor/Unity \
                 /tmp/aaris-unity-runtime/editors/6000.3.26f1/Editor/Unity; do
  if [[ -x "$candidate" ]]; then EDITOR="$candidate"; break; fi
done
if [[ -z "$EDITOR" ]]; then echo AARIS_SOURCE_SPLIT_RESULT=editor_missing; exit 21; fi
if pgrep -f '[E]ditor/Unity.*-projectPath /workspaces/Unity-Game-Aris' >/dev/null 2>&1; then
  echo AARIS_SOURCE_SPLIT_RESULT=original_editor_busy
  exit 22
fi
available="$(df -Pk /tmp | awk 'NR==2 {print $4}')"
if [[ ! "$available" =~ ^[0-9]+$ ]] || (( available < 4194304 )); then
  echo AARIS_SOURCE_SPLIT_RESULT=tmp_disk_low
  exit 23
fi
WORK="$(mktemp -d /tmp/aaris-csharp-native-split.XXXXXXXX)"
case "$WORK" in /tmp/aaris-csharp-native-split.*) ;; *) echo AARIS_SOURCE_SPLIT_RESULT=bad_tmp; exit 24 ;; esac
trap 'rm -rf -- "$WORK"' EXIT
mkdir -p "$WORK/tmp"
for name in packages runtime all; do
  mkdir -p "$WORK/$name/Assets" "$WORK/$name/Packages" \
           "$WORK/$name/ProjectSettings"
  cp "$SOURCE/Packages/manifest.json" "$WORK/$name/Packages/manifest.json"
  cp "$SOURCE/ProjectSettings/ProjectVersion.txt" \
     "$WORK/$name/ProjectSettings/ProjectVersion.txt"
done
# No original packages-lock.json is read or overwritten; Unity resolves each
# fresh disposable project's own lock, just like the passing package-only test.
cp --help >/dev/null
(
  cd "$SOURCE"
  find Assets/AarisWorld/Scripts -type f \( -name '*.cs' -o -name '*.asmdef' -o -name '*.asmref' \) -print0 |
    while IFS= read -r -d '' file; do
      cp --parents -- "$file" "$WORK/runtime"
      cp --parents -- "$file" "$WORK/all"
    done
  find Assets/AarisWorld/Editor -type f \( -name '*.cs' -o -name '*.asmdef' -o -name '*.asmref' \) -print0 |
    while IFS= read -r -d '' file; do
      cp --parents -- "$file" "$WORK/all"
    done
)
runtime_count="$(find "$WORK/runtime/Assets/AarisWorld" -name '*.cs' | wc -l | tr -d ' ')"
all_count="$(find "$WORK/all/Assets/AarisWorld" -name '*.cs' | wc -l | tr -d ' ')"
echo "AARIS_SOURCE_SPLIT_RUNTIME_GAME_SCRIPTS=$runtime_count"
echo "AARIS_SOURCE_SPLIT_ALL_GAME_SCRIPTS=$all_count"
if (( runtime_count < 45 || all_count < 90 )); then
  echo AARIS_SOURCE_SPLIT_RESULT=incomplete_source_copy
  exit 25
fi
for name in runtime all; do
  mkdir -p "$WORK/$name/Assets/Editor"
  cat > "$WORK/$name/Assets/Editor/AarisNativeSourceCompileGate.cs" <<'CS'
using UnityEditor;
using UnityEngine;
public static class AarisNativeSourceCompileGate
{
    public static void Run()
    {
        Debug.Log("AARIS_NATIVE_SOURCE_COMPILE_METHOD_PASSED");
    }
}
CS
done

report_one() {
  local name="$1" rc="$2"
  python3 - "$name" "$rc" "$WORK/$name.editor.private.log" \
          "$WORK/$name.stdout.private" "$WORK/$name.stderr.private" <<'PY'
from pathlib import Path
import re
import sys

name, code = sys.argv[1].upper(), int(sys.argv[2])
paths = [Path(x) for x in sys.argv[3:]]
log = "\n".join(p.read_bytes()[-6000000:].decode("utf-8", "replace")
                for p in paths if p.is_file())
codes = re.findall(r"\berror\s+(CS\d{4})\s*:", log)
gate = "AARIS_NATIVE_SOURCE_COMPILE_METHOD_PASSED" in log
crash = bool(re.search(r"sigsegv|segmentation fault|native crash|sigabrt|signal 11", log, re.I))
package = bool(re.search(r"failed to resolve packages|package manager.*(?:failed|error)", log, re.I))
print("AARIS_SOURCE_SPLIT_" + name + "_EXIT=" + str(code))
print("AARIS_SOURCE_SPLIT_" + name + "_LOG_FOUND=" + ("yes" if paths[0].is_file() else "no"))
print("AARIS_SOURCE_SPLIT_" + name + "_CS_ERRORS=" + str(len(codes)))
for error in sorted(set(codes))[:10]:
    print("AARIS_SOURCE_SPLIT_" + name + "_ERROR_" + error + "=yes")
print("AARIS_SOURCE_SPLIT_" + name + "_GATE_RAN=" + ("yes" if gate else "no"))
print("AARIS_SOURCE_SPLIT_" + name + "_NATIVE_CRASH=" + ("yes" if crash else "no"))
print("AARIS_SOURCE_SPLIT_" + name + "_PACKAGE_RESOLVE=" + ("yes" if package else "no"))
result = ("pass" if code == 0 and not codes and
          (name == "PACKAGES" or gate) else
          "timeout" if code == 124 else
          "csharp_errors" if codes else
          "native_crash_signal" if crash else
          "editor_startup_or_import_failure")
print("AARIS_SOURCE_SPLIT_" + name + "_RESULT=" + result)
PY
}

# Prewarm exactly the current package set. Only a PASS means it is sensible
# to attribute subsequent failures to actual source import.
rc=0
timeout --kill-after=8s 110s env TMPDIR="$WORK/tmp" \
  "$EDITOR" -batchmode -nographics -quit \
  -projectPath "$WORK/packages" -logFile "$WORK/packages.editor.private.log" \
  >"$WORK/packages.stdout.private" 2>"$WORK/packages.stderr.private" || rc=$?
report_one packages "$rc"
if (( rc != 0 )); then
  echo AARIS_SOURCE_SPLIT_RESULT=package_preflight_failed
  exit 27
fi

for name in runtime all; do
  rc=0
  timeout --kill-after=8s 208s env TMPDIR="$WORK/tmp" \
    "$EDITOR" -batchmode -nographics -quit -projectPath "$WORK/$name" \
    -executeMethod AarisNativeSourceCompileGate.Run \
    -logFile "$WORK/$name.editor.private.log" \
    >"$WORK/$name.stdout.private" 2>"$WORK/$name.stderr.private" || rc=$?
  report_one "$name" "$rc"
done
echo AARIS_SOURCE_SPLIT_RESULT=diagnostic_completed
