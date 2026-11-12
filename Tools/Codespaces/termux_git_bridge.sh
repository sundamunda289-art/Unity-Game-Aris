#!/data/data/com.termux/files/usr/bin/bash
# Transfer the canonical repo's official Git auth into the selected Codespace,
# over owner-authorized GitHub Codespaces SSH (never via public Issues/URLs/logs).
# Re-run after rotating the Codespaces account. Never starts a new worktree.
set -euo pipefail
umask 077
REPO="sundamunda289-art/Unity-Game-Aris"
BASE="$HOME/.config/aaris-ai"
MASTER="$BASE/master-gh"
WORKER="$BASE/codespace-gh"

command -v pkg >/dev/null || { echo "AARIS_GIT_BRIDGE_TERMUX_REQUIRED"; exit 2; }
for tool in gh curl jq base64 python3; do
  command -v "$tool" >/dev/null || { echo "AARIS_GIT_BRIDGE_TOOL_MISSING=$tool"; exit 3; }
done
GH_CONFIG_DIR="$MASTER" gh auth status -h github.com >/dev/null 2>&1 || {
  echo "AARIS_GIT_BRIDGE_MAIN_ACCOUNT_LOGIN_REQUIRED"; exit 4;
}
GH_CONFIG_DIR="$WORKER" gh auth status -h github.com >/dev/null 2>&1 || {
  echo "AARIS_GIT_BRIDGE_CODESPACE_ACCOUNT_LOGIN_REQUIRED"; exit 5;
}
OWNER="$(GH_CONFIG_DIR="$MASTER" gh api user --jq .login)"
[[ "$OWNER" == sundamunda289-art ]] || {
  echo "AARIS_GIT_BRIDGE_WRONG_MAIN_ACCOUNT"; exit 6;
}

SOURCE_SHA="$(GH_CONFIG_DIR="$MASTER" gh api "repos/$REPO/git/ref/heads/main" --jq .object.sha)"
[[ "$SOURCE_SHA" =~ ^[0-9a-f]{40}$ ]] || exit 7
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
curl -fsSL --retry 3 \
  "https://raw.githubusercontent.com/$REPO/$SOURCE_SHA/Tools/Codespaces/canonical_git.sh" \
  -o "$TMP/canonical_git.sh"
bash -n "$TMP/canonical_git.sh" || exit 8

# No hardcoded Codespace name: only a uniquely available authorized Unity-Game-Aris host.
MACHINE=""
for attempt in $(seq 1 18); do
  GH_CONFIG_DIR="$WORKER" gh codespace list --limit 100 --json name,state,repository > "$TMP/machines.json" || {
    echo "AARIS_GIT_BRIDGE_CANNOT_LIST_CODSPACES"; exit 9;
  }
  count="$(jq '[.[] | select(.state == "Available" and ((.repository // "") | endswith("/Unity-Game-Aris")))] | length' "$TMP/machines.json")"
  if [[ "$count" == 1 ]]; then
    MACHINE="$(jq -r '.[] | select(.state == "Available" and ((.repository // "") | endswith("/Unity-Game-Aris"))) | .name' "$TMP/machines.json")"
    break
  fi
  if [[ "$count" -gt 1 ]]; then
    echo "AARIS_GIT_BRIDGE_AMBIGUOUS_CODESPACE: select exactly one; refusing wrong-account transfer"
    exit 10
  fi
  if [[ "$attempt" -lt 18 ]]; then
    echo "AARIS_GIT_BRIDGE_WAITING_FOR_SELECTED_CODESPACE attempt=$attempt"
    sleep 10
  fi
done
[[ "$MACHINE" =~ ^[a-zA-Z0-9][a-zA-Z0-9-]{2,119}$ ]] || {
  echo "AARIS_GIT_BRIDGE_CODESPACE_NOT_YET_AVAILABLE"
  echo 'Retry only the Git bridge after bootstrap: bash "$HOME/aaris-git-bridge.sh"'
  exit 11
}

# Only Python source code below enters the Codespace command line.
# The main GitHub token and script bytes travel exclusively on SSH stdin.
REMOTE_INSTALL="$(cat <<'PY'
import base64
import os
import pathlib
import re
import subprocess
import sys
import tempfile

token = sys.stdin.readline().strip()
encoded = sys.stdin.readline().strip()
if not re.fullmatch(r"[A-Za-z0-9_]{20,}", token):
    raise SystemExit("AARIS_GIT_BRIDGE_INVALID_TOKEN")
script = base64.b64decode(encoded, validate=True)
if not script.startswith(b"#!/usr/bin/env bash\n"):
    raise SystemExit("AARIS_GIT_BRIDGE_UNEXPECTED_SCRIPT")
subprocess.run(["bash", "-n"], input=script, check=True)

private = pathlib.Path.home() / ".config" / "aaris-ai"
bin_dir = pathlib.Path.home() / ".local" / "share" / "aaris-git"
for folder in (private, bin_dir):
    if folder.is_symlink():
        raise SystemExit("AARIS_GIT_BRIDGE_UNSAFE_SYMLINK_DIR")
    folder.mkdir(parents=True, exist_ok=True, mode=0o700)
    folder.chmod(0o700)

def save_atomic(dest, data, mode):
    fd, tmpname = tempfile.mkstemp(dir=str(dest.parent), prefix=".aaris-git.")
    try:
        os.fchmod(fd, mode)
        with os.fdopen(fd, "wb") as handle:
            handle.write(data)
        os.replace(tmpname, dest)
        os.chmod(dest, mode)
    finally:
        if os.path.exists(tmpname):
            os.unlink(tmpname)

askpass = b"""#!/bin/sh
case "$1" in
  *Username*|*username*) printf "x-access-token\\n" ;;
  *Password*|*password*) cat "$HOME/.config/aaris-ai/canonical-push.token" ;;
  *) exit 1 ;;
esac
"""
save_atomic(bin_dir / "canonical_git.sh", script, 0o700)
save_atomic(private / "canonical-git-askpass.sh", askpass, 0o700)
save_atomic(private / "canonical-push.token", (token + "\n").encode("ascii"), 0o600)
print("AARIS_GIT_BRIDGE_PRIVATE_INSTALL_SUCCESS")
PY
)"
# Empty/stdout in failures never reveal the token. Do not use shell debug tracing.
{
  GH_CONFIG_DIR="$MASTER" gh auth token
  base64 -w0 "$TMP/canonical_git.sh"
  printf '\n'
} | GH_CONFIG_DIR="$WORKER" gh codespace ssh -c "$MACHINE" -- \
  "python3 -c '$REMOTE_INSTALL'"

echo "AARIS_GIT_BRIDGE_INSTALLED_ON=$MACHINE"
GH_CONFIG_DIR="$WORKER" gh codespace ssh -c "$MACHINE" -- \
  'bash "$HOME/.local/share/aaris-git/canonical_git.sh" status'
echo 'AARIS_GIT_BRIDGE_READY: future ChatGPT Turbo commands can run canonical_git.sh {fetch|pull|commit|publish|push-main}'
