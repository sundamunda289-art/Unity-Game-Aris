#!/data/data/com.termux/files/usr/bin/bash
# Aaris: one-phone-command Codespaces hosting-account rotation.
# Canonical game source always remains sundamunda289-art/Unity-Game-Aris.
# Launcher for the AGENTS.md-approved termux_switch.sh.
# Does not delete/stop the old Codespace or create Git worktrees.
set -Eeuo pipefail
umask 077

REPO="sundamunda289-art/Unity-Game-Aris"
RAW="https://raw.githubusercontent.com/$REPO/main/Tools/Codespaces"
BASE="$HOME/.config/aaris-ai"
MASTER="$BASE/master-gh"
HOST="$BASE/codespace-gh"
LAUNCHER="$HOME/aaris-switch.sh"
MODE="--switch"
if (( $# > 0 )); then MODE="$1"; fi

case "$MODE" in
  --switch|switch) MODE="--switch" ;;
  --resume|resume) MODE="--resume" ;;
  --status|status) MODE="--status" ;;
  *)
    echo 'Usage: bash aaris-next.sh [--switch|--resume|--status]'
    exit 2
    ;;
esac

if ! command -v pkg >/dev/null 2>&1; then
  echo 'AARIS_TERMUX_REQUIRED: run in the real Termux app'
  exit 3
fi

show_status() {
  echo '=== AARIS CODESPACE HOST ACCOUNT ==='
  if ! command -v gh >/dev/null 2>&1; then
    echo AARIS_GH_NOT_INSTALLED
    return
  fi
  if ! GH_CONFIG_DIR="$HOST" gh auth status -h github.com >/dev/null 2>&1; then
    echo AARIS_NO_HOST_LOGIN
    return
  fi
  local login
  login="$(GH_CONFIG_DIR="$HOST" gh api user --jq .login)"
  echo "GitHub Codespace owner: $login"
  echo 'Codespaces for the game hosting repo:'
  GH_CONFIG_DIR="$HOST" gh codespace list --limit 100 \
    --json name,state,repository |
    jq -r '.[] | select((.repository // "") | endswith("/Unity-Game-Aris")) |
      [.name,.state,.repository] | @tsv'
  if GH_CONFIG_DIR="$MASTER" gh auth status -h github.com >/dev/null 2>&1; then
    echo 'Recent official bootstrap attempts (run ID | status | conclusion):'
    GH_CONFIG_DIR="$MASTER" gh run list -R "$REPO" \
      --workflow aaris-turbo-bootstrap.yml --limit 3 \
      --json databaseId,status,conclusion \
      --jq '.[] | [.databaseId,.status,(.conclusion // "-")] | @tsv' \
      2>/dev/null || echo AARIS_BOOTSTRAP_RUN_STATUS_NOT_YET_AVAILABLE
  fi
}

if [[ "$MODE" == "--status" ]]; then
  show_status
  exit 0
fi

echo 'AARIS_CANONICAL_REPO=sundamunda289-art/Unity-Game-Aris'
echo 'AARIS_SWITCH_DOES_NOT_DELETE_OLD_CODESPACE_OR_EDIT_UNCOMMITTED_FILES'
echo 'IMPORTANT: uncommitted edits from the old Codespace are NOT automatically transferred.'
echo 'Using a new GitHub Codespaces account requires its own quota and authorized Unity-Game-Aris hosting repository.'
echo 'Installing official Termux dependencies...'
pkg install -y gh curl jq coreutils openssh

mkdir -p "$BASE" "$MASTER" "$HOST"
chmod 700 "$BASE" "$MASTER" "$HOST"
TMP="$(mktemp "$HOME/.aaris-switch.XXXXXXXX")"
trap 'rm -f "$TMP"' EXIT
curl --fail --show-error --silent --location --retry 3 \
  "$RAW/termux_switch.sh" -o "$TMP"
test -s "$TMP"
grep -q 'aaris-turbo-bootstrap.yml' "$TMP" || {
  echo 'AARIS_UNEXPECTED_SWITCH_SCRIPT: setup aborted'
  exit 4
}
bash -n "$TMP"
chmod 700 "$TMP"
mv -f "$TMP" "$LAUNCHER"
trap - EXIT

export MAIN_REPO="$REPO"
if [[ "$MODE" == "--switch" ]]; then
  echo 'AARIS_NEW_GITHUB_ACCOUNT_LOGIN: choose the SECOND hosting account in official GitHub device/browser login.'
else
  echo 'AARIS_RESUME_EXISTING_HOST_LOGIN: no new account login will be requested.'
fi

# This script keeps owner and hosting-account credentials isolated. It updates
# the selected CODESPACE_PAT, dispatches one bootstrap and attempts the
# repository-scoped Git deploy-key bridge; never writes raw tokens to logs.
bash "$LAUNCHER" "$MODE"

echo 'AARIS_SWITCH_DISPATCH_COMPLETE'
echo 'A bootstrap request is NOT proof the new terminal or Unity Editor is live.'
show_status
echo "AARIS_BOOTSTRAP_RUNS=https://github.com/$REPO/actions/workflows/aaris-turbo-bootstrap.yml"
echo 'If bootstrap is still running, inspect its result before retrying setup.'
echo 'If Git bridge remained pending after a successful bootstrap:'
echo '    bash "$HOME/aaris-git-bridge.sh"'
echo 'To inspect status without another bootstrap:'
echo '    bash "$HOME/aaris-next.sh" --status'
