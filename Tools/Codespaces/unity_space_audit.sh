#!/usr/bin/env bash
# Diagnose low disk / inode / Unity import stalls on the EXISTING Codespace.
# No source modification. Never delete Library, package cache, scenes or Git LFS.
set -euo pipefail
PROJECT=/workspaces/Unity-Game-Aris
OUT=/tmp/aaris-codespaces/space-audit
mkdir -p "$OUT"
cd "$PROJECT"
printf '%s\n' 'AARIS_SPACE_AUDIT_BEGIN'
date -u '+UTC=%Y-%m-%dT%H:%M:%SZ'
printf 'branch=%s\n' "$(git branch --show-current)"
printf 'commit=%s\n' "$(git rev-parse --short HEAD)"
printf 'tracked_changes=%s\n' "$(git status --porcelain --untracked-files=no | wc -l)"
printf '%s\n' '===FILE_SYSTEM_CAPACITY_KB==='
df -Pk "$PROJECT" /tmp "$HOME" | awk 'NR==1 || !seen[$1]++'
printf '%s\n' '===FILE_SYSTEM_INODES==='
df -Pi "$PROJECT" /tmp "$HOME" | awk 'NR==1 || !seen[$1]++'
printf '%s\n' '===KNOWN_CACHE_AND_TEMP_KB==='
for dir in "$PROJECT/Temp" "$PROJECT/Library" "$PROJECT/Logs" "$PROJECT/obj" \
  "$HOME/.cache" "$HOME/.local/share/aaris-unity-editors" \
  /tmp/aaris-codespaces /tmp/aaris-unity-recovery; do
  if [[ -d "$dir" ]]; then
    du -sk "$dir" 2>/dev/null | head -1 || true
  else
    printf '0\t%s [absent]\n' "$dir"
  fi
done
printf '%s\n' '===UNITY_EDITOR_PROCESSES==='
pgrep -af '[E]ditor/Unity.*-projectPath /workspaces/Unity-Game-Aris' | head -3 || true
printf '%s\n' '===UNITY_IMPORT_WORKERS==='
pgrep -af '[U]nityShaderCompiler|[A]ssetImportWorker|[U]nityPackageManager' | head -5 || true
printf '%s\n' '===MEMORY==='
free -m | head -4
log=/tmp/aaris-codespaces/Editor.log
if [[ -f "$log" ]]; then
  age=$(($(date +%s)-$(stat -c %Y "$log")))
  printf 'editor_log_age_seconds=%s\n' "$age"
  printf 'editor_log_size_kb=%s\n' "$(( $(stat -c %s "$log") / 1024 ))"
  printf 'recent_compiler_errors=%s\n' "$(grep -cE 'error CS[0-9]+' "$log" || true)"
  printf '%s\n' '===RECENT_IMPORT_STATUS==='
  tail -n 14 "$log" | cut -c 1-160
else
  printf 'editor_log=missing\n'
fi
printf '%s\n' '===SAFE_CLEANUP_DECISION==='
free_kb=$(df -Pk "$PROJECT" | awk 'NR==2 {print $4}')
tmp_kb=0
if [[ -d "$PROJECT/Temp" ]]; then
  tmp_kb=$(du -sk "$PROJECT/Temp" 2>/dev/null | awk 'NR==1 {print $1}')
fi
printf 'free_kb_before=%s\n' "$free_kb"
printf 'unity_temp_kb_before=%s\n' "$tmp_kb"
# Clean ONLY the project Temp cache when objectively useful and the editor is
# already stopped; clearing the cache while running corrupts in-flight imports.
if [[ -d "$PROJECT/Temp" && ! -L "$PROJECT/Temp" ]] &&
   [[ "$(realpath "$PROJECT/Temp")" == "$PROJECT/Temp" ]] &&
   ! pgrep -f '[E]ditor/Unity.*-projectPath /workspaces/Unity-Game-Aris' >/dev/null &&
   { [[ "$free_kb" -lt 4194304 ]] || [[ "$tmp_kb" -gt 2097152 ]]; }; then
  printf 'unity_temp_cleanup=safe_stopped_editor\n'
  find "$PROJECT/Temp" -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +
else
  printf 'unity_temp_cleanup=skipped_no_pressure_or_running_editor\n'
fi
# Only eliminate harmless stale, user-owned transient crash temp items.
# Keep recovery backups, LFS, Unity logs and screenshots.
find /tmp -maxdepth 1 -type f -user "$(id -un)" \
  \( -name 'unity-temp-*.tmp' -o -name 'aaris-ephemeral-*.tmp' \) \
  -mmin +1440 -delete 2>/dev/null || true
printf 'free_kb_after=%s\n' "$(df -Pk "$PROJECT" | awk 'NR==2 {print $4}')"
printf '%s\n' 'AARIS_SPACE_AUDIT_COMPLETE'
