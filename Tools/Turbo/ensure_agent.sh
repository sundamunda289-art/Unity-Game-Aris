#!/usr/bin/env bash
# Idempotent startup hook for an already-owner-approved Aaris Turbo Agent.
# Starts no new Codespace, downloads nothing, never exposes credentials.
set -euo pipefail
umask 077
# A minimal Codespace can restart with /tmp wiped while project Library,
# Logs and obj still point there. Recreate only those exact, existing cache
# symlinks before the Unity Editor starts; never replace a real directory or
# follow a symlink redirected outside our known ephemeral cache root.
project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
cache_root="/tmp/aaris-unity-runtime/project-cache"
for cache_name in Library Logs obj; do
  cache_link="$project_root/$cache_name"
  if [[ -L "$cache_link" ]] &&
     [[ "$(readlink -- "$cache_link")" == "$cache_root/$cache_name" ]]; then
    mkdir -p -- "$cache_root/$cache_name"
  fi
done

CONFIG="$HOME/.config/aaris-turbo"
BIN="$HOME/.local/share/aaris-turbo/agent.py"
TOKEN="$CONFIG/token"
ENDPOINT_FILE="$CONFIG/endpoint"
EXPIRY_FILE="$CONFIG/expires_at"
PID_FILE="$CONFIG/agent.pid"
LOG="$CONFIG/agent.log"

# A fresh Codespace needs one GitHub OIDC bootstrap first.
for file in "$BIN" "$TOKEN" "$ENDPOINT_FILE" "$EXPIRY_FILE"; do
  if test ! -f "$file" || test -L "$file"; then
    echo "TURBO_NO_VALID_SAVED_SESSION"
    exit 0
  fi
done
test "$(stat -c '%u' "$TOKEN")" = "$(id -u)" || {
  echo "TURBO_SESSION_UNSAFE_OWNER"; exit 0;
}
# A token secret must never be world/group-readable.
mode="$(stat -c '%a' "$TOKEN")"
case "$mode" in 600|400) ;; *) echo "TURBO_SESSION_UNSAFE_MODE"; exit 0 ;; esac
EXPIRY="$(cat "$EXPIRY_FILE")"
if [[ ! "$EXPIRY" =~ ^[0-9]{10,12}$ ]] || (( EXPIRY <= $(date +%s) + 60 )); then
  echo "TURBO_SESSION_EXPIRED_REBOOTSTRAP_REQUIRED"
  exit 0
fi
ENDPOINT="$(cat "$ENDPOINT_FILE")"
if [[ ! "$ENDPOINT" =~ ^https://[a-z0-9.-]+/agent/[A-Za-z0-9-]+$ ]]; then
  echo "TURBO_SESSION_ENDPOINT_INVALID"; exit 0
fi
if test -s "$PID_FILE"; then
  PID="$(cat "$PID_FILE")"
  if [[ "$PID" =~ ^[0-9]+$ ]] && [[ -r "/proc/$PID/cmdline" ]] &&
     tr '\0' ' ' < "/proc/$PID/cmdline" | grep -qF "$BIN"; then
    echo "TURBO_AGENT_ALREADY_RUNNING"
    exit 0
  fi
fi
nohup env AARIS_ENDPOINT="$ENDPOINT" AARIS_TOKEN_FILE="$TOKEN" \
  python3 -u "$BIN" >> "$LOG" 2>&1 </dev/null &
PID="$!"
printf '%s\n' "$PID" > "$PID_FILE"
sleep 1
if kill -0 "$PID" 2>/dev/null; then
  echo "TURBO_AGENT_AUTO_RESUMED"
else
  echo "TURBO_AGENT_AUTO_RESUME_FAILED"
fi
