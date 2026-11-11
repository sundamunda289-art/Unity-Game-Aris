#!/data/data/com.termux/files/usr/bin/bash
# Owner-run: deploy the same reviewed Turbo Worker to an authorized Cloudflare account
# and reconnect the existing Codespace agent without stopping Unity.
set -euo pipefail
umask 077
REPO="sundamunda289-art/Unity-Game-Aris"
LEGACY="https://aaris-turbo-terminal.aaris-game-control-6f9c.workers.dev"
# Read ONLY safe error categories from the Codespace's recent Wrangler logs,
# without starting a new OAuth flow, restarting Unity, or exposing any token.
if [[ "${1:-}" == "--diagnose" ]]; then
  MACHINE="${2:-}"
  [[ "$MACHINE" =~ ^[A-Za-z0-9][A-Za-z0-9-]{2,119}$ ]] || {
    echo 'USAGE: bash aaris-cf-switch.sh --diagnose EXACT-RUNNING-CODESPACE-NAME'
    exit 2
  }
  CFG="$HOME/.config/gh"
  [[ -s "$HOME/.config/aaris-ai/codespace-gh/hosts.yml" ]] && CFG="$HOME/.config/aaris-ai/codespace-gh"
  command -v gh >/dev/null && command -v curl >/dev/null || {
    echo AARIS_CF_DIAG_REQUIRES_GH_AND_CURL; exit 3;
  }
  DIAG_FILE="$(mktemp)"
  trap 'rm -f "$DIAG_FILE"' EXIT
  curl -fsSL --retry 3 "https://raw.githubusercontent.com/$REPO/main/Tools/Turbo/wrangler_auth_diag.py" -o "$DIAG_FILE"
  echo 'AARIS_CF_INSPECTING_RECENT_WRANGLER_AUTH_ERRORS_NO_NEW_LOGIN'
  GH_CONFIG_DIR="$CFG" gh codespace ssh -c "$MACHINE" -- 'python3 - --latest' < "$DIAG_FILE"
  exit 0
fi
if ! command -v pkg >/dev/null 2>&1; then echo "TERMUX_REQUIRED"; exit 2; fi
# In Termux the openssl executable is shipped by openssl-tool, not openssl.
pkg install -y gh curl jq openssl openssl-tool coreutils >/dev/null
command -v openssl >/dev/null 2>&1 || {
  echo "TERMUX_OPENSSL_TOOL_MISSING: pkg install -y openssl-tool"
  exit 20
}
BASE="$HOME/.config/aaris-ai"
mkdir -p "$BASE"
chmod 700 "$BASE"
MASTER="$HOME/.config/gh"
CODESPACE_AUTH="$MASTER"
[[ -s "$BASE/master-gh/hosts.yml" ]] && MASTER="$BASE/master-gh"
[[ -s "$BASE/codespace-gh/hosts.yml" ]] && CODESPACE_AUTH="$BASE/codespace-gh"
if ! GH_CONFIG_DIR="$MASTER" gh auth status -h github.com >/dev/null 2>&1; then
  echo "Authenticate the sundamunda289-art repository owner once."
  GH_CONFIG_DIR="$MASTER" gh auth login -h github.com -p https -w -s 'repo,workflow'
fi
PERMISSION="$(GH_CONFIG_DIR="$MASTER" gh repo view "$REPO" --json viewerPermission -q .viewerPermission)"
case "$PERMISSION" in ADMIN|MAINTAIN) ;; *) echo "GITHUB_REPO_ADMIN_REQUIRED"; exit 3;; esac
if ! GH_CONFIG_DIR="$CODESPACE_AUTH" gh auth status -h github.com >/dev/null 2>&1; then
  echo "Authorize the account that owns the currently running Codespace."
  GH_CONFIG_DIR="$CODESPACE_AUTH" gh auth login -h github.com -p https -w -s 'codespace,repo'
fi
TMP="$(mktemp -d)"
SSH_PID=""
cleanup() {
  if [[ -n "${SSH_PID:-}" ]] && kill -0 "$SSH_PID" 2>/dev/null; then
    kill "$SSH_PID" 2>/dev/null || true
    wait "$SSH_PID" 2>/dev/null || true
  fi
  rm -rf "$TMP"
}
trap cleanup EXIT
# Source repo is the sundamunda289-art master, but this Codespace can be
# hosted in any authorized GitHub account's Unity-Game-Aris repository.
GH_CONFIG_DIR="$CODESPACE_AUTH" gh codespace list --limit 100 --json name,state,repository > "$TMP/codespaces.json"
echo 'Available Codespaces in authorized Unity-Game-Aris hosting repositories:'
jq -r '.[]|select((.repository // "")|endswith("/Unity-Game-Aris"))|[.name,.state,.repository]|@tsv' "$TMP/codespaces.json"
MACHINE="${1:-}"
if [[ -z "$MACHINE" ]]; then
  mapfile -t RUNNING < <(jq -r '.[]|select(.state=="Available" and ((.repository // "")|endswith("/Unity-Game-Aris")))|.name' "$TMP/codespaces.json")
  if (("${#RUNNING[@]}"==1)); then MACHINE="${RUNNING[0]}"; else
    read -r -p "Exact running Codespace name (from list): " MACHINE
  fi
fi
[[ "$MACHINE" =~ ^[A-Za-z0-9][A-Za-z0-9-]{2,119}$ ]] || { echo "INVALID_MACHINE"; exit 4; }
jq -e --arg m "$MACHINE" '.[]|select(.name==$m and .state=="Available" and ((.repository // "")|endswith("/Unity-Game-Aris")))' "$TMP/codespaces.json" >/dev/null || {
  echo "CODESPACE_MUST_ALREADY_BE_RUNNING; NOT STARTING OR RESTARTING IT"
  exit 5
}
SOURCE_SHA="$(GH_CONFIG_DIR="$MASTER" gh api "/repos/$REPO/git/ref/heads/main" --jq .object.sha)"
[[ "$SOURCE_SHA" =~ ^[0-9a-f]{40}$ ]] || exit 6
SRC="https://raw.githubusercontent.com/$REPO/$SOURCE_SHA/Tools/Turbo"
curl -fsSL --retry 3 "$SRC/cloudflare_account_switch_linux.sh" -o "$TMP/remote.sh"
curl -fsSL --retry 3 "$SRC/wrangler.jsonc" -o "$TMP/wrangler.jsonc"
curl -fsSL --retry 3 "$SRC/device_oauth_parse.py" -o "$TMP/device_oauth_parse.py"
python3 "$TMP/device_oauth_parse.py" --selftest
bash -n "$TMP/remote.sh"
python3 -m json.tool "$TMP/wrangler.jsonc" >/dev/null 2>&1 || jq empty "$TMP/wrangler.jsonc"
WATCH_KEY_FILE="$BASE/cloudflare-watch.key"
if [[ ! -s "$WATCH_KEY_FILE" ]]; then
  openssl rand -hex 32 > "$WATCH_KEY_FILE"
  chmod 600 "$WATCH_KEY_FILE"
fi
[[ ! -L "$WATCH_KEY_FILE" && "$(stat -c '%a' "$WATCH_KEY_FILE")" == 600 ]] || {
  echo "UNSAFE_LOCAL_WATCH_KEY_FILE"; exit 7;
}
WATCH_KEY="$(cat "$WATCH_KEY_FILE")"
[[ "$WATCH_KEY" =~ ^[a-f0-9]{64}$ ]] || { echo "INVALID_LOCAL_WATCH_KEY"; exit 8; }
WATCH_HASH="$(printf %s "$WATCH_KEY"|sha256sum|cut -d' ' -f1)"
unset WATCH_KEY
OLD_URL="$(GH_CONFIG_DIR="$MASTER" gh variable get AARIS_BROKER_URL -R "$REPO" 2>/dev/null || true)"
[[ -n "$OLD_URL" ]] || OLD_URL="$LEGACY"
[[ "$OLD_URL" =~ ^https://[a-z0-9.-]+\.workers\.dev$ ]] || { echo "INVALID_OLD_BROKER_URL"; exit 9; }
# Optional explicit target Cloudflare account ID: reuse the Codespace's
# existing authorized Wrangler session without restarting the OAuth device flow.
# The Linux installer verifies this exact ID appears in Wrangler's whoami
# memberships before deploying. Never silently select a different account.
ACCOUNT="${2:-}"
LOGIN_AGAIN=0
if [[ -n "$ACCOUNT" ]]; then
  [[ "$ACCOUNT" =~ ^[a-f0-9]{32}$ ]] || {
    echo "INVALID_EXPLICIT_CF_ACCOUNT_ID"; exit 10;
  }
  LOGIN_AGAIN=1
  echo "CLOUDFLARE_REUSE_EXISTING_AUTHORIZED_LOGIN_FOR_EXPLICIT_ACCOUNT"
fi
SESSION_ID="$(openssl rand -hex 8)"
[[ "$SESSION_ID" =~ ^[a-f0-9]{16}$ ]] || { echo AARIS_CF_INVALID_SESSION_ID; exit 33; }
for ATTEMPT in 1 2; do
  : > "$TMP/deploy.log"
  set +e
  GH_CONFIG_DIR="$CODESPACE_AUTH" gh codespace ssh -c "$MACHINE" -- \
    "AARIS_SOURCE_SHA=$SOURCE_SHA AARIS_WATCH_HASH=$WATCH_HASH AARIS_REUSE_LOGIN=$LOGIN_AGAIN AARIS_CF_ACCOUNT_ID=$ACCOUNT AARIS_CF_SESSION_ID=$SESSION_ID bash -s" \
    < "$TMP/remote.sh" > "$TMP/deploy.log" 2>&1 &
  SSH_PID=$!
  set -e
  OPENED=0
  echo 'CLOUDFLARE_STARTING_REMOTE_WRANGLER_LOGIN'
  for LOGIN_CHECK in $(seq 1 900); do
    if ! kill -0 "$SSH_PID" 2>/dev/null; then break; fi
    if ((LOGIN_AGAIN==0 && OPENED==0 && LOGIN_CHECK % 8 == 1)); then
      # Poll the Codespace's *private* PTY log using a separate authenticated
      # SSH session; the long-lived remote deployment SSH may buffer output.
      # SESSION_ID is random and validated; never poll arbitrary remote paths.
      timeout 18 env GH_CONFIG_DIR="$CODESPACE_AUTH" gh codespace ssh -c "$MACHINE" -- \
        "cat \$HOME/.config/aaris-turbo/cf-login-$SESSION_ID.log 2>/dev/null || true" \
        > "$TMP/device-live.log" 2>/dev/null || true
      DETAILS="$(python3 "$TMP/device_oauth_parse.py" "$TMP/device-live.log" || true)"
      if [[ -n "$DETAILS" ]]; then
        IFS=$'\t' read -r LOGIN_LINK DEVICE_CODE <<< "$DETAILS"
        echo 'CLOUDFLARE_LOGIN_READY — approve within 5 minutes'
        echo "Device code: $DEVICE_CODE"
        echo "Official URL: $LOGIN_LINK"
        if command -v termux-clipboard-set >/dev/null 2>&1; then
          if printf '%s' "$DEVICE_CODE" | timeout 5 termux-clipboard-set >/dev/null 2>&1; then
            echo 'Device code copied to clipboard.'
          fi
        fi
        if command -v termux-open-url >/dev/null 2>&1; then
          termux-open-url "$LOGIN_LINK" || true
        else
          echo 'Open the official URL above and enter the code.'
        fi
        OPENED=1
      fi
    fi
    if ((LOGIN_CHECK % 30 == 0)); then
      if ((OPENED==1)); then
        echo 'CLOUDFLARE_AUTHORIZATION_OR_DEPLOYMENT_IN_PROGRESS'
      else
        echo 'CLOUDFLARE_PREPARING_OR_FETCHING_DEVICE_CODE'
      fi
    fi
    sleep 1
  done
  set +e
  wait "$SSH_PID"
  RESULT=$?
  SSH_PID=""
  set -e
  cat "$TMP/deploy.log"
  if ((RESULT==0)); then break; fi
  if ((RESULT!=42 || ATTEMPT==2)); then
    echo "CLOUDFLARE_DEPLOY_FAILED_NO_GITHUB_CONFIGURATION_CHANGED"
    exit "$RESULT"
  fi
  read -r -p "Authorized Cloudflare Account ID (32 hex chars, from above): " ACCOUNT
  [[ "$ACCOUNT" =~ ^[a-f0-9]{32}$ ]] || { echo "INVALID_CF_ACCOUNT"; exit 10; }
  LOGIN_AGAIN=1
done
NEW_URL="$(sed -nE 's/^AARIS_CF_BROKER=(https:\/\/[a-z0-9.-]+\.workers\.dev)$/\1/p' "$TMP/deploy.log"|tail -n 1)"
[[ "$NEW_URL" =~ ^https://[a-z0-9.-]+\.workers\.dev$ ]] || { echo "BROKER_NOT_VERIFIED"; exit 11; }
GH_CONFIG_DIR="$MASTER" gh variable set AARIS_BROKER_URL -R "$REPO" --body "$NEW_URL"
echo "GITHUB_BROKER_VARIABLE_UPDATED"
# The workflow uses CODESPACE_PAT only for SSH; sync the selected Codespace account.
if ! GH_CONFIG_DIR="$CODESPACE_AUTH" gh auth token | GH_CONFIG_DIR="$MASTER" gh secret set CODESPACE_PAT -R "$REPO"; then
  GH_CONFIG_DIR="$MASTER" gh variable set AARIS_BROKER_URL -R "$REPO" --body "$OLD_URL"
  echo "CODESPACE_CREDENTIAL_SYNC_FAILED_BROKER_ROLLED_BACK"
  exit 15
fi
echo "STARTING_ONE_SIGNED_GITHUB_OIDC_REBOOTSTRAP"
DISPATCHED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
if ! GH_CONFIG_DIR="$MASTER" gh workflow run aaris-turbo-bootstrap.yml -R "$REPO" --ref main -f machine="$MACHINE"; then
  GH_CONFIG_DIR="$MASTER" gh variable set AARIS_BROKER_URL -R "$REPO" --body "$OLD_URL"
  echo "BOOTSTRAP_DISPATCH_FAILED_BROKER_ROLLED_BACK"
  exit 12
fi
echo "GitHub Actions: https://github.com/$REPO/actions/workflows/aaris-turbo-bootstrap.yml"
RUN_ID=""
for TRY in $(seq 1 25); do
  RUN_ID="$(GH_CONFIG_DIR="$MASTER" gh run list -R "$REPO" --workflow aaris-turbo-bootstrap.yml     --event workflow_dispatch --limit 20     --json databaseId,createdAt     --jq '.[] | select(.createdAt >= "'"$DISPATCHED_AT"'") | .databaseId'     2>/dev/null | head -n 1 || true)"
  [[ "$RUN_ID" =~ ^[0-9]+$ ]] && break
  sleep 3
done
if [[ ! "$RUN_ID" =~ ^[0-9]+$ ]]; then
  GH_CONFIG_DIR="$MASTER" gh variable set AARIS_BROKER_URL -R "$REPO" --body "$OLD_URL"
  echo "BOOTSTRAP_RUN_NOT_FOUND_BROKER_ROLLED_BACK"
  exit 13
fi
echo "Verifying authenticated agent handshake: https://github.com/$REPO/actions/runs/$RUN_ID"
if ! GH_CONFIG_DIR="$MASTER" gh run watch "$RUN_ID" -R "$REPO" --exit-status; then
  GH_CONFIG_DIR="$MASTER" gh variable set AARIS_BROKER_URL -R "$REPO" --body "$OLD_URL"
  echo "BOOTSTRAP_FAILED_GITHUB_BROKER_URL_ROLLED_BACK"
  exit 14
fi
echo "Cloudflare Worker: $NEW_URL"
if command -v termux-clipboard-set >/dev/null 2>&1; then
  if printf '%s' "$NEW_URL/watch#key=$(cat "$WATCH_KEY_FILE")" | termux-clipboard-set; then
    echo "PRIVATE_WATCH_LINK_COPIED_TO_CLIPBOARD_NOT_PRINTED"
  else
    echo "OPTIONAL_CLIPBOARD_UNAVAILABLE"
  fi
fi
echo "AARIS_CF_SWITCH_SUCCESS_VERIFIED_AGENT_HANDSHAKE"
echo "Unity Editor was not restarted. Bootstrap passed; verify a harmless terminal command before changing assets."
