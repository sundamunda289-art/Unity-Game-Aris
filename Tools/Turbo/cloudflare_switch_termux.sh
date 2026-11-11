#!/data/data/com.termux/files/usr/bin/bash
# One-command Cloudflare account switch for the existing Aaris Codespace Turbo.
# Owner-approved OAuth only. No Cloudflare/GitHub personal token in source.
set -euo pipefail
umask 077

REPO=sundamunda289-art/Unity-Game-Aris
GITHUB_RAW="https://raw.githubusercontent.com/$REPO/main/Tools/Turbo/cloudflare_switch_remote.sh"
STATE="$HOME/.config/aaris-ai/cloudflare-switch"
OWNER_GH="$HOME/.config/aaris-ai/master-gh"
CODESPACE_GH="$HOME/.config/aaris-ai/codespace-gh"
mkdir -p "$STATE" "$OWNER_GH" "$CODESPACE_GH"
chmod 700 "$STATE" "$OWNER_GH" "$CODESPACE_GH"

if ! command -v pkg >/dev/null; then
  echo 'AARIS_SWITCH_TERMUX_REQUIRED' >&2; exit 2
fi
pkg install -y gh jq curl openssh openssl coreutils >/dev/null
for cmd in gh jq curl sha256sum openssl; do
  command -v "$cmd" >/dev/null || { echo "AARIS_SWITCH_MISSING_$cmd" >&2; exit 3; }
done
if ! GH_CONFIG_DIR="$OWNER_GH" gh auth status -h github.com >/dev/null 2>&1; then
  echo 'Authorize your main GitHub repository account once.'
  GH_CONFIG_DIR="$OWNER_GH" gh auth login -h github.com -p https -w -s repo,workflow
fi
if ! GH_CONFIG_DIR="$CODESPACE_GH" gh auth status -h github.com >/dev/null 2>&1; then
  echo 'Authorize the GitHub account that owns the active Codespace once.'
  GH_CONFIG_DIR="$CODESPACE_GH" gh auth login -h github.com -p https -w -s codespace,repo
fi
perm="$(GH_CONFIG_DIR="$OWNER_GH" gh repo view "$REPO" --json viewerPermission --jq .viewerPermission)"
case "$perm" in ADMIN|MAINTAIN) ;; *) echo 'AARIS_SWITCH_GITHUB_OWNER_PERMISSION_REQUIRED' >&2; exit 4 ;; esac

machines="$(GH_CONFIG_DIR="$CODESPACE_GH" gh codespace list -R "$REPO" --json name,state --limit 100)"
name="${1:-}"
if test -z "$name"; then
  count="$(jq '[.[] | select(.state == "Available")] | length' <<< "$machines")"
  if test "$count" != 1; then
    echo 'Pass an exact running Codespace name: bash ~/aaris-cf-switch.sh <exact-codespace-name>' >&2
    printf '%s\n' "$machines" | jq -r '.[] | [.name,.state] | @tsv' >&2
    exit 5
  fi
  name="$(jq -r '.[] | select(.state == "Available") | .name' <<< "$machines")"
fi
[[ "$name" =~ ^[A-Za-z0-9][A-Za-z0-9-]{2,119}$ ]] || { echo 'AARIS_SWITCH_BAD_CODESPACE_NAME' >&2; exit 6; }
if ! jq -e --arg n "$name" 'any(.[]; .name==$n and .state=="Available")' <<< "$machines" >/dev/null; then
  echo 'AARIS_SWITCH_CODESPACE_NOT_RUNNING_OR_NOT_OWNED_BY_LOGIN' >&2; exit 7
fi

echo "Selected existing Codespace: $name (Unity will NOT be restarted)"
script_file="$(mktemp)"
login_log="$(mktemp)"
accounts_log="$(mktemp)"
deploy_log="$(mktemp)"
cleanup() { rm -f -- "$script_file" "$login_log" "$accounts_log" "$deploy_log"; }
trap cleanup EXIT
curl -fsSL --retry 2 "$GITHUB_RAW" -o "$script_file"
bash -n "$script_file"

remote() {
  local action="$1"; shift
  GH_CONFIG_DIR="$CODESPACE_GH" gh codespace ssh -c "$name" -- "bash -s -- $action $*" < "$script_file"
}

echo '[1/5] Preparing Wrangler on the running Linux Codespace...'
remote preflight

echo '[2/5] Opening the official Cloudflare account authorization...'
remote login > "$login_log" 2>&1 &
login_pid=$!
link=""
for _ in $(seq 1 180); do
  link="$(grep -Eo 'https://dash\.cloudflare\.com/oauth2/device(\?user_code=[A-Za-z0-9]+)?' "$login_log" | tail -1 || true)"
  if test -n "$link"; then break; fi
  if ! kill -0 "$login_pid" 2>/dev/null; then break; fi
  sleep 1
done
if test -n "$link"; then
  echo "Official Cloudflare login: $link"
  if command -v termux-open-url >/dev/null; then termux-open-url "$link" || true; fi
  echo 'Approve the login with the Cloudflare account you want to use.'
else
  echo 'Device login URL not captured. Wrangler output follows:'
  cat "$login_log"
fi
if ! wait "$login_pid"; then
  echo 'AARIS_SWITCH_LOGIN_FAILED' >&2
  cat "$login_log" >&2
  exit 8
fi
grep -q 'AARIS_SWITCH_CLOUDFLARE_LOGIN_OK' "$login_log" || { echo 'AARIS_SWITCH_LOGIN_UNVERIFIED' >&2; exit 8; }

echo '[3/5] Identifying the authorized Cloudflare account...'
remote accounts > "$accounts_log"
cat "$accounts_log"
mapfile -t ids < <(grep -Eo '[0-9a-f]{32}' "$accounts_log" | sort -u)
if (( ${#ids[@]} == 1 )); then
  account_id="${ids[0]}"
else
  echo 'Several Cloudflare accounts were returned. Choose the intended Account ID from the list above.'
  read -r -p 'Cloudflare Account ID (32 hexadecimal characters): ' account_id
  [[ "$account_id" =~ ^[0-9a-f]{32}$ ]] || { echo 'AARIS_SWITCH_ACCOUNT_INVALID' >&2; exit 9; }
  if (( ${#ids[@]} > 0 )); then
    printf '%s\n' "${ids[@]}" | grep -qxF "$account_id" || { echo 'AARIS_SWITCH_ACCOUNT_NOT_LISTED' >&2; exit 9; }
  fi
fi

key_file="$STATE/watch_key"
if test ! -f "$key_file"; then
  openssl rand -hex 32 > "$key_file"
  chmod 600 "$key_file"
  echo 'Created a new private watch key in Termux (not committed or uploaded).'
fi
mode="$(stat -c '%a' "$key_file")"
test "$mode" = 600 || { echo 'AARIS_SWITCH_WATCH_KEY_PERMISSIONS_INVALID' >&2; exit 10; }
key="$(cat "$key_file")"
[[ "$key" =~ ^[0-9a-f]{64}$ ]] || { echo 'AARIS_SWITCH_WATCH_KEY_INVALID' >&2; exit 11; }
watch_hash="$(printf '%s' "$key" | sha256sum | awk '{print $1}')"
unset key

# The hash is sent over the Codespace's encrypted SSH input channel, not through
# argv, repo history, issue comments, or Github Actions logs.
echo '[4/5] Deploying the same Worker source into the selected Cloudflare account...'
source_sha="$(GH_CONFIG_DIR="$OWNER_GH" gh api "/repos/$REPO/commits/main" --jq .sha)"
[[ "$source_sha" =~ ^[0-9a-f]{40}$ ]] || { echo 'AARIS_SWITCH_GITHUB_SOURCE_INVALID' >&2; exit 12; }
{
  printf 'WATCH_TOKEN_HASH=%q\n' "$watch_hash"
  printf 'SOURCE_SHA=%q\n' "$source_sha"
  cat "$script_file"
} | GH_CONFIG_DIR="$CODESPACE_GH" gh codespace ssh -c "$name" -- "bash -s -- deploy $account_id" > "$deploy_log" 2>&1 || {
  echo 'Worker deploy failed; GitHub routing remains unchanged.' >&2
  cat "$deploy_log" >&2
  exit 12
}
url="$(sed -n 's/^AARIS_SWITCH_DEPLOY_URL=\(https:\/\/[^ ]*\)$/\1/p' "$deploy_log" | tail -1)"
[[ "$url" =~ ^https://aaris-turbo-terminal\.[a-z0-9.-]+\.workers\.dev$ ]] || { echo 'AARIS_SWITCH_BAD_DEPLOY_URL' >&2; exit 13; }
echo "Deployed and health-checked: $url"

# The Cloudflare OAuth session is not needed after Worker deploy. The new
# Codespace agent will receive its own short-lived token through GitHub OIDC.
remote logout >/dev/null 2>&1 || echo 'Cloudflare OAuth cleanup deferred.'

echo '[5/5] Updating GitHub routing and re-authorizing only the Turbo agent...'
old_url="$(GH_CONFIG_DIR="$OWNER_GH" gh variable get AARIS_BROKER_URL -R "$REPO" 2>/dev/null || true)"
var_existed=no
if test -n "$old_url"; then var_existed=yes; fi
restore_var() {
  if test "$var_existed" = yes; then
    GH_CONFIG_DIR="$OWNER_GH" gh variable set AARIS_BROKER_URL -R "$REPO" --body "$old_url" || true
  else
    GH_CONFIG_DIR="$OWNER_GH" gh variable delete AARIS_BROKER_URL -R "$REPO" >/dev/null 2>&1 || true
  fi
}
if ! GH_CONFIG_DIR="$OWNER_GH" gh variable set AARIS_BROKER_URL -R "$REPO" --body "$url"; then
  echo 'Failed to update GitHub broker variable; deployed Worker is intact.' >&2
  exit 14
fi
# Record existing workflow_dispatch IDs before triggering a new run; do not
# accidentally watch another account's concurrent bootstrap.
previous="$(GH_CONFIG_DIR="$OWNER_GH" gh run list -R "$REPO" -w aaris-turbo-bootstrap.yml --event workflow_dispatch --limit 30 --json databaseId --jq '.[].databaseId' 2>/dev/null || true)"
if ! GH_CONFIG_DIR="$OWNER_GH" gh workflow run aaris-turbo-bootstrap.yml -R "$REPO" --ref main --field "machine=$name"; then
  restore_var
  echo 'Bootstrap dispatch failed; previous routing restored.' >&2
  exit 15
fi

printf '\nAARIS_CLOUDFLARE_SWITCH_DISPATCHED\n'
printf 'New Worker: %s\n' "$url"
printf 'Bootstrap: https://github.com/%s/actions/workflows/aaris-turbo-bootstrap.yml\n' "$REPO"
run_id=""
for _ in $(seq 1 24); do
  current="$(GH_CONFIG_DIR="$OWNER_GH" gh run list -R "$REPO" -w aaris-turbo-bootstrap.yml --event workflow_dispatch --limit 30 --json databaseId --jq '.[].databaseId' 2>/dev/null || true)"
  found="$(comm -13 <(printf '%s\n' "$previous" | sed '/^$/d' | sort -u) <(printf '%s\n' "$current" | sed '/^$/d' | sort -u))"
  matches="$(printf '%s\n' "$found" | sed '/^$/d' | wc -l)"
  if test "$matches" -eq 1; then run_id="$found"; break; fi
  if test "$matches" -gt 1; then echo 'Multiple simultaneous bootstrap runs; see Actions URL.'; break; fi
  sleep 2
done
if [[ "$run_id" =~ ^[0-9]+$ ]]; then
  echo "Waiting for verified Turbo agent bootstrap run $run_id ..."
  if GH_CONFIG_DIR="$OWNER_GH" gh run watch "$run_id" -R "$REPO" --exit-status; then
    if GH_CONFIG_DIR="$OWNER_GH" gh run view "$run_id" -R "$REPO" --log 2>/dev/null | grep -F 'TURBO_CLOUDFLARE_HANDSHAKE_PASS' >/dev/null; then
      echo 'AARIS_CLOUDFLARE_SWITCH_AGENT_VERIFIED'
    else
      echo 'AARIS_SWITCH_ACTIONS_SUCCESS_BUT_HANDSHAKE_NOT_VERIFIED'
      echo 'Inspect the authenticated bootstrap run before using the agent.'
    fi
  else
    conclusion="$(GH_CONFIG_DIR="$OWNER_GH" gh run view "$run_id" -R "$REPO" --json conclusion --jq .conclusion 2>/dev/null || echo unknown)"
    if test "$conclusion" = failure; then
      restore_var
      echo 'AARIS_SWITCH_BOOTSTRAP_FAILED_PREVIOUS_ROUTING_RESTORED' >&2
      exit 16
    fi
    echo "AARIS_SWITCH_BOOTSTRAP_STATUS=$conclusion (verify Actions manually)"
  fi
else
  echo 'AARIS_SWITCH_BOOTSTRAP_DISPATCHED_RUN_NOT_YET_IDENTIFIED'
fi
printf 'Codespace: %s (Unity Editor not restarted)\n' "$name"
echo 'Private watch key remains in Termux:'
echo '  ~/.config/aaris-ai/cloudflare-switch/watch_key'
