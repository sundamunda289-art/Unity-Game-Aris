#!/usr/bin/env bash
# Runs inside one specifically selected Ubuntu Codespace via GitHub SSH.
# Never changes the Unity process, /workspaces files, or GitHub credentials.
set -euo pipefail
umask 077

OP="${1:-}"
NODE_RELEASE="v22.16.0"
NODE_DIR="$HOME/.local/share/aaris-cloudflare-node"
NPM_CONFIG_CACHE="$HOME/.cache/aaris-cloudflare-npm"
export NPM_CONFIG_CACHE

ensure_node() {
  if command -v node >/dev/null 2>&1 && command -v npm >/dev/null 2>&1; then
    local major
    major="$(node -p 'Number(process.versions.node.split(".")[0])' 2>/dev/null || echo 0)"
    if [[ "$major" =~ ^[0-9]+$ ]] && ((major >= 22)); then return 0; fi
  fi
  if test -x "$NODE_DIR/bin/node" && test -x "$NODE_DIR/bin/npm"; then
    export PATH="$NODE_DIR/bin:$PATH"
    return 0
  fi
  local arch name dir
  case "$(uname -m)" in
    x86_64) arch=x64 ;;
    aarch64) arch=arm64 ;;
    *) echo 'AARIS_SWITCH_UNSUPPORTED_CPU' >&2; exit 11 ;;
  esac
  command -v curl >/dev/null && command -v sha256sum >/dev/null && command -v tar >/dev/null || {
    echo 'AARIS_SWITCH_MISSING_CURL_SHA256SUM_TAR' >&2; exit 12;
  }
  local available
  available="$(df -Pk "$HOME" | awk 'END { print $4 }')"
  if [[ "$available" =~ ^[0-9]+$ ]] && (( available < 850000 )); then
    echo 'AARIS_SWITCH_DISK_LOW_KEEP_UNITY_ASSETS' >&2; exit 13
  fi
  name="node-${NODE_RELEASE}-linux-${arch}.tar.xz"
  dir="$(mktemp -d)"
  trap 'rm -rf -- "$dir"' RETURN
  curl -fsSL --retry 2 "https://nodejs.org/dist/${NODE_RELEASE}/${name}" -o "$dir/$name"
  curl -fsSL --retry 2 "https://nodejs.org/dist/${NODE_RELEASE}/SHASUMS256.txt" -o "$dir/SHASUMS256.txt"
  (cd "$dir" && grep -F "  $name" SHASUMS256.txt | sha256sum --check --status)
  mkdir -p "$NODE_DIR"
  tar -xf "$dir/$name" -C "$NODE_DIR" --strip-components=1
  export PATH="$NODE_DIR/bin:$PATH"
  node --version >/dev/null && npm --version >/dev/null
  trap - RETURN
  rm -rf -- "$dir"
}

wrangler() {
  npx --yes wrangler@4 "$@"
}

case "$OP" in
  preflight)
    test -d /workspaces/Unity-Game-Aris || { echo 'AARIS_SWITCH_PROJECT_MISSING'; exit 21; }
    ensure_node
    wrangler --version
    echo 'AARIS_SWITCH_REMOTE_PREPARED'
    ;;
  login)
    ensure_node
    # Avoid silently keeping credentials from the prior Cloudflare account.
    wrangler logout >/dev/null 2>&1 || true
    wrangler login --device --browser=false
    echo 'AARIS_SWITCH_CLOUDFLARE_LOGIN_OK'
    ;;
  accounts)
    ensure_node
    wrangler whoami
    ;;
  deploy)
    # WATCH_TOKEN_HASH is passed over authenticated SSH as an in-memory Bash
    # assignment on stdin, not a CLI argument, GitHub issue, or Actions log.
    [[ "${WATCH_TOKEN_HASH:-}" =~ ^[0-9a-f]{64}$ ]] || {
      echo 'AARIS_SWITCH_WATCH_HASH_INVALID' >&2; exit 30;
    }
    ACCOUNT_ID="${2:-}"
    [[ "$ACCOUNT_ID" =~ ^[0-9a-f]{32}$ ]] || { echo 'AARIS_SWITCH_ACCOUNT_INVALID' >&2; exit 31; }
    export CLOUDFLARE_ACCOUNT_ID="$ACCOUNT_ID"
    ensure_node
    # Fail closed if the target account isn't authorized in the new OAuth login.
    wrangler whoami --account "$ACCOUNT_ID" >/dev/null || {
      echo 'AARIS_SWITCH_CLOUDFLARE_ACCOUNT_NOT_AUTHORIZED' >&2; exit 32;
    }
    # Stage pinned GitHub source privately. Do NOT git pull/reset or change the
    # Unity worktree, which can contain local assets/ongoing editor changes.
    SOURCE_SHA="${SOURCE_SHA:-}"
    [[ "$SOURCE_SHA" =~ ^[0-9a-f]{40}$ ]] || { echo 'AARIS_SWITCH_SOURCE_SHA_INVALID' >&2; exit 33; }
    stage="$(mktemp -d)"
    out="$(mktemp)"
    trap 'rm -rf -- "$stage"; rm -f -- "$out"' EXIT
    base="https://raw.githubusercontent.com/sundamunda289-art/Unity-Game-Aris/${SOURCE_SHA}/Tools/Turbo"
    for name in worker.mjs watch_page.mjs wrangler.jsonc; do
      curl -fsSL --retry 2 "$base/$name" -o "$stage/$name" || {
        echo 'AARIS_SWITCH_SOURCE_DOWNLOAD_FAILED' >&2; exit 33;
      }
    done
    cd "$stage"
    if ! wrangler deploy --config wrangler.jsonc > "$out" 2>&1; then
      sed -E 's/(token|secret|key)[[:space:]]*[:=][[:space:]]*[^[:space:]]+/\1=[redacted]/Ig' "$out" >&2
      echo 'AARIS_SWITCH_DEPLOY_FAILED' >&2
      exit 34
    fi
    # Hash only; raw 64-character browser key is never transferred to Cloudflare.
    printf '%s' "$WATCH_TOKEN_HASH" | wrangler secret put WATCH_TOKEN_HASH --config wrangler.jsonc > /dev/null
    url="$(grep -Eo 'https://aaris-turbo-terminal\.[A-Za-z0-9.-]+\.workers\.dev' "$out" | head -n 1 || true)"
    [[ "$url" =~ ^https://aaris-turbo-terminal\.[a-z0-9.-]+\.workers\.dev$ ]] || {
      echo 'AARIS_SWITCH_DEPLOY_URL_MISSING' >&2; exit 35;
    }
    health="$(curl -fsS --max-time 20 "$url/health")" || { echo 'AARIS_SWITCH_HEALTH_FAILED' >&2; exit 36; }
    if ! printf '%s' "$health" | grep -q '"status":"ready"'; then
      echo 'AARIS_SWITCH_HEALTH_INVALID' >&2; exit 37
    fi
    printf 'AARIS_SWITCH_DEPLOY_URL=%s\n' "$url"
    echo 'AARIS_SWITCH_WORKER_HEALTH_OK'
    ;;
  logout)
    ensure_node
    wrangler logout >/dev/null 2>&1 || true
    echo 'AARIS_SWITCH_REMOTE_OAUTH_CLEARED'
    ;;
  *)
    echo 'Usage: cloudflare_switch_remote.sh preflight|login|accounts|deploy ACCOUNT_ID|logout' >&2
    exit 2
    ;;
esac
