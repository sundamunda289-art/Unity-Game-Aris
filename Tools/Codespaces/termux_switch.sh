#!/data/data/com.termux/files/usr/bin/bash
set -euo pipefail
# Isolated GitHub CLI authentication profiles; no embedded PAT or password.
REPO="$(printenv MAIN_REPO || echo sundamunda289-art/Unity-Game-Aris)"
case "${1:-switch}" in
  switch|--switch) MODE=switch ;;
  --resume|resume) MODE=resume ;;
  *) echo 'Usage: bash aaris-switch.sh [--switch|--resume]'; exit 2 ;;
esac
BASE="$HOME/.config/aaris-ai"
MASTER="$BASE/master-gh"
WORKER="$BASE/codespace-gh"
command -v pkg >/dev/null || { echo 'Use Termux on Android'; exit 2; }
pkg install -y gh jq
umask 077
mkdir -p "$MASTER" "$WORKER"
chmod 700 "$BASE" "$MASTER" "$WORKER"
echo 'Authenticate the MAIN repo owner via official GitHub browser/device login (once).'
if ! GH_CONFIG_DIR="$MASTER" gh auth status -h github.com >/dev/null 2>&1; then
  GH_CONFIG_DIR="$MASTER" gh auth login -h github.com -p https -w -s 'repo,workflow'
fi
OWNER="$(GH_CONFIG_DIR="$MASTER" gh api user -q .login)"
PERM="$(GH_CONFIG_DIR="$MASTER" gh repo view "$REPO" --json viewerPermission -q .viewerPermission)"
case "$PERM" in ADMIN|MAINTAIN) ;; *) echo "Main account $OWNER cannot manage repo Secrets"; exit 3;; esac
if test "$MODE" = switch; then
  echo 'Authorize the selected Codespace account via official GitHub device/browser login.'
  if GH_CONFIG_DIR="$WORKER" gh auth status -h github.com >/dev/null 2>&1; then
    GH_CONFIG_DIR="$WORKER" gh auth logout -h github.com \
      -u "$(GH_CONFIG_DIR="$WORKER" gh api user -q .login)" >/dev/null 2>&1 || true
  fi
  GH_CONFIG_DIR="$WORKER" gh auth login -h github.com -p https -w -s 'codespace,repo'
else
  GH_CONFIG_DIR="$WORKER" gh auth status -h github.com >/dev/null 2>&1 || {
    echo 'No saved Codespace account session. Run again without --resume to switch accounts.'
    exit 5
  }
fi
CS_USER="$(GH_CONFIG_DIR="$WORKER" gh api user -q .login)"
# An alternate GitHub account can host its own Unity-Game-Aris fork.
# Do not look up Codespace machine types on the permanent master repository.
if ! GH_CONFIG_DIR="$WORKER" gh codespace list --limit 100 --json name,state,repository >/dev/null; then
  echo 'Codespaces not accessible for this account. Old secret was left unchanged.'
  exit 4
fi
CS_COUNT="$(GH_CONFIG_DIR="$WORKER" gh codespace list --limit 100 --json name,state,repository \
  --jq '[.[]|select((.repository // "")|endswith("/Unity-Game-Aris"))]|length')"
if test "$CS_COUNT" = 0; then
  GH_CONFIG_DIR="$WORKER" gh repo view "$CS_USER/Unity-Game-Aris" --json name -q .name >/dev/null || {
    echo 'No existing Unity-Game-Aris Codespace or authorized hosting fork; old secret unchanged.'
    exit 4
  }
fi
MASTER_TOKEN="$(GH_CONFIG_DIR="$MASTER" gh auth token)"
CS_TOKEN="$(GH_CONFIG_DIR="$WORKER" gh auth token)"
printf '%s' "$CS_TOKEN" | GH_TOKEN="$MASTER_TOKEN" gh secret set CODESPACE_PAT -R "$REPO"
unset MASTER_TOKEN CS_TOKEN
echo "Codespace account authorized: $CS_USER; original repository unchanged."
echo 'Starting one isolated GitHub Actions bootstrap for dynamic Codespace selection and Cloudflare agent installation.'
GH_CONFIG_DIR="$MASTER" gh workflow run aaris-turbo-bootstrap.yml \
  --repo "$REPO" --ref main --field machine=auto || {
    echo 'Bootstrap dispatch failed. Secret was updated; no Codespace was deleted.'
    exit 6
  }
echo 'TURBO_BOOTSTRAP_DISPATCHED'
echo "Actions: https://github.com/$REPO/actions/workflows/aaris-turbo-bootstrap.yml"
echo "Switch or reauthorize next time: bash \"\$HOME/aaris-switch.sh\""
echo "Reuse current login next time: bash \"\$HOME/aaris-switch.sh\" --resume"

