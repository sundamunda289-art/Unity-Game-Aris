#!/usr/bin/env bash
# Runs ONLY on the owner's authorized Linux Codespace over GitHub SSH.
# Never run Wrangler natively on Android Termux.
set -euo pipefail
umask 077
: "${AARIS_SOURCE_SHA:?source commit required}"
: "${AARIS_WATCH_HASH:?private viewer SHA-256 digest required}"
[[ "$AARIS_SOURCE_SHA" =~ ^[a-f0-9]{40}$ ]] || exit 20
[[ "$AARIS_WATCH_HASH" =~ ^[a-f0-9]{64}$ ]] || exit 21
: "${AARIS_CF_SESSION_ID:?random account-switch login session required}"
[[ "$AARIS_CF_SESSION_ID" =~ ^[a-f0-9]{16}$ ]] || { echo AARIS_CF_INVALID_SESSION_ID; exit 32; }
[[ "${AARIS_REUSE_LOGIN:-0}" =~ ^[01]$ ]] || exit 22
if [[ -n "${AARIS_CF_ACCOUNT_ID:-}" ]] &&
   [[ ! "$AARIS_CF_ACCOUNT_ID" =~ ^[a-f0-9]{32}$ ]]; then
  echo AARIS_CF_INVALID_ACCOUNT_ID
  exit 23
fi
for bin in curl python3 tar sha256sum; do
  command -v "$bin" >/dev/null || {
    echo "AARIS_CF_MISSING_LINUX_TOOL=$bin"
    exit 24
  }
done
TMP="$(mktemp -d)"
# A detached device code must be readable by a separate authorized SSH
# session, because gh codespace ssh may buffer its original stdout.
AUTH_LOG_DIR="$HOME/.config/aaris-turbo"
mkdir -p "$AUTH_LOG_DIR"
chmod 700 "$AUTH_LOG_DIR"
AUTH_LOG="$AUTH_LOG_DIR/cf-login-$AARIS_CF_SESSION_ID.log"
: > "$AUTH_LOG"
chmod 600 "$AUTH_LOG"
trap 'rm -rf "$TMP"; rm -f "$AUTH_LOG"' EXIT
# Minimal Ubuntu Codespaces may not ship with Node. Install a verified, user-local
# LTS binary only when needed, without sudo, global npm changes, or Unity restart.
NODE_MAJOR=0
if command -v node >/dev/null 2>&1; then
  NODE_MAJOR="$(node -p "parseInt(process.versions.node.split('.')[0],10)")"
fi
if ((NODE_MAJOR<20)) || ! command -v npx >/dev/null 2>&1; then
  [[ "$(uname -m)" == x86_64 ]] || { echo AARIS_CF_NODE_ARCH_UNSUPPORTED; exit 25; }
  NODE_ROOT="$HOME/.local/share/aaris-turbo-node/v22.16.0"
  if [[ ! -x "$NODE_ROOT/bin/node" || ! -x "$NODE_ROOT/bin/npx" ]]; then
    mkdir -p "$HOME/.local/share/aaris-turbo-node"
    NODE_ARCHIVE="node-v22.16.0-linux-x64.tar.gz"
    curl -fsSL --retry 3 --max-time 150 \
      "https://nodejs.org/dist/v22.16.0/$NODE_ARCHIVE" -o "$TMP/$NODE_ARCHIVE"
    (cd "$TMP"; printf '%s  %s\n' \
      fb870226119d47378fa9c92c4535389c72dae14fcc7b47e6fdcc82c43de5a547 "$NODE_ARCHIVE" |
      sha256sum -c --status)
    tar -xzf "$TMP/$NODE_ARCHIVE" -C "$TMP"
    mv "$TMP/node-v22.16.0-linux-x64" "$NODE_ROOT"
    echo AARIS_CF_LOCAL_NODE22_SHA256_VERIFIED
  fi
  export PATH="$NODE_ROOT/bin:$PATH"
fi
command -v node >/dev/null && command -v npx >/dev/null || exit 25
if [[ -n "${CLOUDFLARE_API_TOKEN:-}" || -n "${CLOUDFLARE_API_KEY:-}" ]]; then
  echo AARIS_CF_ENV_TOKEN_CONFLICT
  exit 26
fi
for name in worker.mjs watch_page.mjs wrangler.jsonc wrangler_auth_diag.py; do
  curl -fsSL --retry 3 --max-time 40 \
    "https://raw.githubusercontent.com/sundamunda289-art/Unity-Game-Aris/$AARIS_SOURCE_SHA/Tools/Turbo/$name" \
    -o "$TMP/$name"
  test -s "$TMP/$name"
done
cd "$TMP"
WRANGLER=(npx --yes wrangler@4.119.0)
# Log in with official OAuth device flow. Nothing is saved in GitHub.
# Existing local Wrangler authentication is not used after logout.
if [[ "${AARIS_REUSE_LOGIN:-0}" != 1 ]]; then
  "${WRANGLER[@]}" logout >/dev/null 2>&1 || true
  echo 'AARIS_CF_DEVICE_OAUTH_STARTED'
  # Run Wrangler in a pseudo-TTY and flush every byte to a private,
  # session-specific 0600 log. Another authenticated SSH connection reads
  # that log immediately, even if this gh codespace ssh call buffers stdout.
  OAUTH_STATUS=0
  python3 - "$AUTH_LOG" "${WRANGLER[@]}" login --device --browser=false <<'PYOAUTH' || OAUTH_STATUS=$?
import errno, os, pty, subprocess, sys
log_path = sys.argv[1]
master, slave = pty.openpty()
proc = subprocess.Popen(sys.argv[2:], stdin=subprocess.DEVNULL,
                        stdout=slave, stderr=slave, start_new_session=True)
os.close(slave)
with open(log_path, "ab", buffering=0) as log:
    while True:
        try:
            chunk = os.read(master, 8192)
        except OSError as exc:
            if exc.errno == errno.EIO:
                break
            raise
        if not chunk:
            break
        log.write(chunk)
os.close(master)
sys.exit(proc.wait())
PYOAUTH
  if ((OAUTH_STATUS!=0)); then
    echo "AARIS_CF_DEVICE_LOGIN_COMMAND_FAILED_EXIT_$OAUTH_STATUS"
    python3 "$TMP/wrangler_auth_diag.py" --log "$AUTH_LOG" || echo AARIS_CF_LOGIN_DIAGNOSTIC_UNAVAILABLE
    exit "$OAUTH_STATUS"
  fi
  echo AARIS_CF_DEVICE_LOGIN_COMMAND_PASS
fi
if ! "${WRANGLER[@]}" whoami --json > "$TMP/whoami.json"; then
  echo AARIS_CF_WHOAMI_AFTER_LOGIN_FAILED
  python3 "$TMP/wrangler_auth_diag.py" --latest || true
  exit 34
fi
python3 - "$TMP/whoami.json" <<'PY'
import json,sys
obj=json.load(open(sys.argv[1]))
accounts=obj.get("accounts",[])
if not obj.get("loggedIn") or not isinstance(accounts,list) or not accounts:
    raise SystemExit("AARIS_CF_NO_AUTHORIZED_ACCOUNTS")
for account in accounts:
    aid=account.get("id","")
    label=account.get("name","")
    if len(aid)==32 and all(x in "0123456789abcdef" for x in aid):
        print("AARIS_CF_ACCOUNT_OPTION="+aid+" "+str(label)[:60])
PY
echo AARIS_CF_WHOAMI_AUTHENTICATED
mapfile -t IDS < <(python3 - "$TMP/whoami.json" <<'PY'
import json,sys,re
obj=json.load(open(sys.argv[1]))
print("\n".join(a["id"] for a in obj.get("accounts",[])
      if isinstance(a,dict) and re.fullmatch("[0-9a-f]{32}",str(a.get("id","")))))
PY
)
if (("${#IDS[@]}" == 0)); then
  echo AARIS_CF_AUTHENTICATED_ACCOUNT_MISSING
  exit 27
fi
if [[ -n "${AARIS_CF_ACCOUNT_ID:-}" ]]; then
  valid=0
  for i in "${IDS[@]}"; do [[ "$i" == "$AARIS_CF_ACCOUNT_ID" ]] && valid=1; done
  if ((valid==0)); then
    echo AARIS_CF_ACCOUNT_NOT_AUTHORIZED
    exit 28
  fi
elif (("${#IDS[@]}" == 1)); then
  AARIS_CF_ACCOUNT_ID="${IDS[0]}"
else
  echo AARIS_CF_ACCOUNT_CHOICE_REQUIRED
  exit 42
fi
export CLOUDFLARE_ACCOUNT_ID="$AARIS_CF_ACCOUNT_ID"
echo "AARIS_CF_DEPLOY_ACCOUNT=$CLOUDFLARE_ACCOUNT_ID"
"${WRANGLER[@]}" deploy --config "$TMP/wrangler.jsonc" 2>&1 | tee "$TMP/deploy.log"
URL="$(grep -Eo 'https://[a-zA-Z0-9.-]+\.workers\.dev' "$TMP/deploy.log" | head -n 1 || true)"
if [[ ! "$URL" =~ ^https://[a-z0-9.-]+\.workers\.dev$ ]]; then
  echo AARIS_CF_WORKER_URL_NOT_FOUND
  exit 29
fi
# A viewer key, if supplied by the owner on their phone, stays on the phone.
# Only its SHA-256 digest reaches this remote Linux session and the Worker.
printf '%s' "$AARIS_WATCH_HASH" |
  "${WRANGLER[@]}" secret put WATCH_TOKEN_HASH --config "$TMP/wrangler.jsonc" >/dev/null
STATUS="$(curl -sS --retry 3 --max-time 20 "$URL/health")"
python3 - "$STATUS" <<'PY'
import json,sys
obj=json.loads(sys.argv[1])
assert obj.get("service")=="aaris-turbo" and obj.get("status")=="ready", obj
PY
# Confirm this new deployment rejects anonymous command execution before activation.
HTTP="$(curl -sS --max-time 20 -o /dev/null -w '%{http_code}' \
  -X POST "$URL/bootstrap" -H 'Content-Type: application/json' -d '{"machine":"aaris-invalid-test"}')"
[[ "$HTTP" == 401 ]] || { echo "AARIS_CF_UNSAFE_BOOTSTRAP_STATUS=$HTTP"; exit 30; }
HTTP="$(curl -sS --max-time 20 -o /dev/null -w '%{http_code}' \
  -X POST "$URL/agent/aaris-invalid-test/next" -H 'Content-Type: application/json' -d '{}')"
[[ "$HTTP" == 401 ]] || { echo "AARIS_CF_UNSAFE_AGENT_STATUS=$HTTP"; exit 31; }
echo AARIS_CF_WORKER_HEALTH_PASS
echo "AARIS_CF_BROKER=$URL"
