#!/data/data/com.termux/files/usr/bin/bash
# Compatibility entry: the maintained four-step recovery owns login/activation.
set -Eeuo pipefail
umask 077
REPO="sundamunda289-art/Unity-Game-Aris"
PROFILE="${AARIS_CODESPACE_AUTH_DIR:-$HOME/.config/aaris-ai/codespace-gh}"
SOURCE_SHA="${AARIS_SOURCE_SHA:-}"
for tool in gh curl timeout; do
  command -v "$tool" >/dev/null || { printf 'Required Termux tool missing: %s\n' "$tool"; exit 2; }
done
if [[ -z "$SOURCE_SHA" ]]; then
  SOURCE_SHA="$(timeout 60 env -u GH_TOKEN -u GITHUB_TOKEN GH_CONFIG_DIR="$PROFILE" \
    gh api "repos/$REPO/git/ref/heads/main" --jq .object.sha 2>/dev/null)" || {
      echo 'Cannot resolve the reviewed Unity helper with the selected GitHub profile.'; exit 5;
    }
fi
[[ "$SOURCE_SHA" =~ ^[0-9a-f]{40}$ ]] || { echo 'Invalid reviewed helper SHA.'; exit 2; }
TEMP_BASE="${TMPDIR:-${PREFIX:-/data/data/com.termux/files/usr}/tmp}"
mkdir -p "$TEMP_BASE"
TMP="$(mktemp -d "$TEMP_BASE/aaris-unity-allow.XXXXXXXX")"
trap 'rm -rf -- "$TMP"' EXIT
curl -fsSL --retry 3 --connect-timeout 25 --max-time 120 \
  "https://raw.githubusercontent.com/$REPO/$SOURCE_SHA/Tools/Codespaces/termux_unity_login_recover.sh" \
  -o "$TMP/recover.sh" || { echo 'Could not fetch the pinned Unity recovery helper.'; exit 7; }
bash -n "$TMP/recover.sh"
AARIS_SOURCE_SHA="$SOURCE_SHA" AARIS_CODESPACE_AUTH_DIR="$PROFILE" bash "$TMP/recover.sh"

