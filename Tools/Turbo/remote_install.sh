#!/usr/bin/env bash
# Streamed over owner-authorized GitHub Codespace SSH only; never print grants.
set -euo pipefail
umask 077
command -v python3 >/dev/null || { echo TURBO_REMOTE_PYTHON_MISSING; exit 22; }
HOME_DIR="$(python3 -c 'import os,pwd; print(pwd.getpwuid(os.geteuid()).pw_dir)')"
test -d "$HOME_DIR" && test -w "$HOME_DIR" || { echo TURBO_REMOTE_HOME_UNAVAILABLE; exit 23; }
# The modern caller prepares a pending grant first. Its candidate probe cannot
# consume normal jobs. Only an explicit --commit advances the active generation.
if [[ -n "${AARIS_TRANSACTION_ID:-}" ]]; then
  [[ "$AARIS_TRANSACTION_ID" =~ ^[a-f0-9]{32}$ ]] || { echo TURBO_INVALID_TRANSACTION_ID; exit 20; }
  test -n "${AARIS_TRANSACTION_INSTALL_BASE64:-}" || { echo TURBO_TRANSACTION_INSTALLER_MISSING; exit 20; }
  command -v python3 >/dev/null || { echo TURBO_REMOTE_PYTHON_MISSING; exit 22; }
  PRIVATE="$HOME_DIR/.config/aaris-turbo"
  mkdir -p "$PRIVATE"
  chmod 700 "$PRIVATE"
  UPLOAD="$(mktemp /tmp/aaris-transaction-installer.XXXXXXXX.py)"
  trap 'rm -f "$UPLOAD"' EXIT
  printf '%s' "$AARIS_TRANSACTION_INSTALL_BASE64" | base64 -d > "$UPLOAD"
  chmod 600 "$UPLOAD"
  # Do not print uploaded payloads or credentials on any error.
  python3 - "$UPLOAD" <<'PYVERIFY'
from pathlib import Path
import sys
compile(Path(sys.argv[1]).read_bytes(), "transaction_install.py", "exec")
PYVERIFY
  export AARIS_TRANSACTION_ID AARIS_TRANSACTION_INSTALL_BASE64
  export AARIS_AGENT_TOKEN AARIS_ENDPOINT AARIS_AGENT_EXPIRES_AT AARIS_AGENT_GENERATION
  export AARIS_AGENT_BASE64 AARIS_GUI_BASE64 AARIS_GUI_SETUP_BASE64
  export AARIS_TRANSACTION_PREPARE_ONLY="${AARIS_TRANSACTION_PREPARE_ONLY:-0}"
  python3 "$UPLOAD" --install --wait "${AARIS_TRANSACTION_WAIT_SECONDS:-180}"
  exit $?
fi
# Legacy first-install compatibility. Rotation of an existing valid agent now
# requires a pending grant, so old automation cannot silently revoke it.
mkdir -p "$HOME_DIR/.config/aaris-turbo"
chmod 700 "$HOME_DIR/.config/aaris-turbo"
exec 9>"$HOME_DIR/.config/aaris-turbo/install.lock"
flock -n 9 || { echo TURBO_INSTALL_ALREADY_RUNNING; exit 75; }
if [[ -s "$HOME_DIR/.config/aaris-turbo/token" ]]; then
  echo TURBO_TRANSACTION_REQUIRED_FOR_EXISTING_AGENT
  exit 27
fi
test -n "$AARIS_AGENT_TOKEN" || exit 20
test -n "$AARIS_ENDPOINT" || exit 20
test -n "$AARIS_AGENT_BASE64" || exit 20
test -n "$AARIS_GUI_BASE64" || exit 20
test -n "$AARIS_GUI_SETUP_BASE64" || exit 20
test -n "$AARIS_AGENT_EXPIRES_AT" || exit 20
[[ "$AARIS_AGENT_TOKEN" =~ ^[0-9a-f]{64}$ ]] || exit 20
[[ "$AARIS_ENDPOINT" =~ ^https://[a-z0-9.-]+/agent/[A-Za-z0-9-]+$ ]] || exit 21
[[ "$AARIS_AGENT_EXPIRES_AT" =~ ^[0-9]{10,12}$ ]] || exit 21
command -v python3 >/dev/null || { echo "TURBO_REMOTE_PYTHON_MISSING"; exit 22; }
BIN="$HOME_DIR/.local/share/aaris-turbo"
CONFIG="$HOME_DIR/.config/aaris-turbo"
mkdir -p "$BIN" "$CONFIG"
chmod 700 "$BIN" "$CONFIG"
printf '%s' "$AARIS_AGENT_BASE64" | base64 -d > "$BIN/agent.new.py"
python3 -m py_compile "$BIN/agent.new.py"
python3 "$BIN/agent.new.py" --selftest >/dev/null
mv "$BIN/agent.new.py" "$BIN/agent.py"
printf '%s' "$AARIS_GUI_BASE64" | base64 -d > "$BIN/gui.new.py"
python3 -m py_compile "$BIN/gui.new.py"
python3 "$BIN/gui.new.py" --selftest >/dev/null
mv "$BIN/gui.new.py" "$BIN/gui.py"
printf '%s' "$AARIS_GUI_SETUP_BASE64" | base64 -d > "$BIN/gui_setup.sh"
bash -n "$BIN/gui_setup.sh"
chmod 700 "$BIN/gui_setup.sh"
printf '%s\n' "$AARIS_AGENT_TOKEN" > "$CONFIG/token"
chmod 600 "$CONFIG/token"
printf '%s\n' "$AARIS_ENDPOINT" > "$CONFIG/endpoint"
printf '%s\n' "$AARIS_AGENT_EXPIRES_AT" > "$CONFIG/expires_at"
chmod 600 "$CONFIG/endpoint" "$CONFIG/expires_at"
if test -s "$CONFIG/agent.pid"; then
  OLD="$(cat "$CONFIG/agent.pid")"
  if [[ "$OLD" =~ ^[0-9]+$ ]] && [[ -r "/proc/$OLD/cmdline" ]] &&
     tr '\0' ' ' < "/proc/$OLD/cmdline" | grep -qF "$BIN/agent.py"; then
    kill "$OLD" 2>/dev/null || true
  fi
fi
: > "$CONFIG/agent.log"
nohup env AARIS_ENDPOINT="$AARIS_ENDPOINT" AARIS_TOKEN_FILE="$CONFIG/token" \
  python3 -u "$BIN/agent.py" >> "$CONFIG/agent.log" 2>&1 </dev/null 9>&- &
PID="$!"
printf '%s\n' "$PID" > "$CONFIG/agent.pid"
sleep 3
kill -0 "$PID" 2>/dev/null || { echo "TURBO_AGENT_START_FAILED"; exit 24; }
if grep -qxF AARIS_TURBO_AGENT_STARTED "$CONFIG/agent.log"; then
  echo "TURBO_AGENT_LIVE"
else
  echo "TURBO_AGENT_PROCESS_ALIVE"
fi
# Prove that the credential works on this Codespace, not merely that a
# detached Python process exists. Never print a token or raw response.
python3 - "$BIN/agent.py" "$CONFIG/token" "$AARIS_ENDPOINT" <<'PY'
import importlib.util
from pathlib import Path
import sys
try:
    spec = importlib.util.spec_from_file_location("aaris_turbo_agent", sys.argv[1])
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    token = Path(sys.argv[2]).read_text(encoding="ascii").strip()
    result = module.Bridge(sys.argv[3], token).post("ping")
    if result.get("ok") is not True:
        raise RuntimeError("handshake_not_acknowledged")
except Exception as exc:
    print("TURBO_CLOUDFLARE_HANDSHAKE_FAILED_" + type(exc).__name__.upper())
    sys.exit(26)
print("TURBO_CLOUDFLARE_HANDSHAKE_PASS")
PY
if test -x "$BIN/gui_setup.sh"; then
  nohup bash "$BIN/gui_setup.sh" >> "$CONFIG/gui-setup.log" 2>&1 </dev/null 9>&- &
  echo "TURBO_GUI_DEPENDENCIES_SCHEDULED"
fi
echo "TURBO_NONDESTRUCTIVE_BOOTSTRAP_OK"

