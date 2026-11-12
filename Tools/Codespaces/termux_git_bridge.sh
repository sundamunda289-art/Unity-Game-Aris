#!/data/data/com.termux/files/usr/bin/bash
# Set up a per-Codespace, single-repository GitHub SSH deploy key.
# The master account's token NEVER enters a Codespace. No new worktree.
set -euo pipefail
umask 077
REPO="sundamunda289-art/Unity-Game-Aris"
BASE="$HOME/.config/aaris-ai"
MASTER="$BASE/master-gh"
WORKER="$BASE/codespace-gh"

command -v pkg >/dev/null || { echo "AARIS_GIT_BRIDGE_TERMUX_REQUIRED"; exit 2; }
for tool in gh curl jq base64; do
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
PERM="$(GH_CONFIG_DIR="$MASTER" gh repo view "$REPO" --json viewerPermission -q .viewerPermission)"
[[ "$PERM" == ADMIN ]] || {
  echo "AARIS_GIT_BRIDGE_DEPLOY_KEY_REQUIRES_REPO_ADMIN"; exit 7;
}

SOURCE_SHA="$(GH_CONFIG_DIR="$MASTER" gh api "repos/$REPO/git/ref/heads/main" --jq .object.sha)"
[[ "$SOURCE_SHA" =~ ^[0-9a-f]{40}$ ]] || exit 8
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
curl -fsSL --retry 3 \
  "https://raw.githubusercontent.com/$REPO/$SOURCE_SHA/Tools/Codespaces/canonical_git.sh" \
  -o "$TMP/canonical_git.sh"
bash -n "$TMP/canonical_git.sh" || exit 9

# Exactly one available Codespace under the currently chosen hosting account.
MACHINE=""
for attempt in $(seq 1 18); do
  GH_CONFIG_DIR="$WORKER" gh codespace list --limit 100 --json name,state,repository > "$TMP/machines.json" || {
    echo "AARIS_GIT_BRIDGE_CANNOT_LIST_CODESPACES"; exit 10;
  }
  count="$(jq '[.[] | select(.state == "Available" and ((.repository // "") | endswith("/Unity-Game-Aris")))] | length' "$TMP/machines.json")"
  if [[ "$count" == 1 ]]; then
    MACHINE="$(jq -r '.[] | select(.state == "Available" and ((.repository // "") | endswith("/Unity-Game-Aris"))) | .name' "$TMP/machines.json")"
    break
  fi
  if [[ "$count" -gt 1 ]]; then
    echo "AARIS_GIT_BRIDGE_AMBIGUOUS_CODESPACE: refusing to select the wrong machine"
    exit 11
  fi
  if [[ "$attempt" -lt 18 ]]; then
    echo "AARIS_GIT_BRIDGE_WAITING_FOR_CODESPACE attempt=$attempt"
    sleep 10
  fi
done
[[ "$MACHINE" =~ ^[a-zA-Z0-9][a-zA-Z0-9-]{2,119}$ ]] || {
  echo "AARIS_GIT_BRIDGE_CODESPACE_NOT_YET_AVAILABLE"
  echo 'After bootstrap retry: bash "$HOME/aaris-git-bridge.sh"'
  exit 12
}

# SSH generates/stores a brand-new private key ONLY inside that Codespace.
# The canonical helper (public source) is sent over SSH stdin; no password/token.
REMOTE_INSTALL="$(cat <<'PY'
import base64
import os
import pathlib
import subprocess
import sys
import tempfile

encoded = sys.stdin.readline().strip()
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

save_atomic(bin_dir / "canonical_git.sh", script, 0o700)
key = private / "canonical-deploy-key"
public = private / "canonical-deploy-key.pub"
if key.is_symlink() or public.is_symlink():
    raise SystemExit("AARIS_GIT_BRIDGE_UNSAFE_KEY_SYMLINK")
if not key.exists() and not public.exists():
    subprocess.run(
        ["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", str(key),
         "-C", "aaris-canonical-repo-only"], check=True,
        stdout=subprocess.DEVNULL
    )
elif not key.is_file() or not public.is_file():
    raise SystemExit("AARIS_GIT_BRIDGE_INCOMPLETE_EXISTING_KEYPAIR")
key.chmod(0o600)
parts = public.read_text(encoding="ascii").strip().split()
if len(parts) < 2 or parts[0] != "ssh-ed25519":
    raise SystemExit("AARIS_GIT_BRIDGE_INVALID_PUBLIC_KEY")
# No private key/token is ever included in stdout.
print("AARIS_GIT_DEPLOY_PUBLIC_KEY " + parts[0] + " " + parts[1])
print("AARIS_GIT_BRIDGE_PRIVATE_KEY_READY")
PY
)"
{
  base64 -w0 "$TMP/canonical_git.sh"
  printf '\n'
} | GH_CONFIG_DIR="$WORKER" gh codespace ssh -c "$MACHINE" -- \
  "python3 -c '$REMOTE_INSTALL'" > "$TMP/install.log"

PUBLIC_KEY="$(sed -nE 's/^AARIS_GIT_DEPLOY_PUBLIC_KEY (ssh-ed25519 [A-Za-z0-9+\/=]+)$/\1/p' "$TMP/install.log" | tail -1)"
[[ "$PUBLIC_KEY" =~ ^ssh-ed25519\ [A-Za-z0-9+/=]+$ ]] || {
  echo "AARIS_GIT_BRIDGE_NO_VERIFIED_PUBLIC_KEY"; exit 13;
}

GH_CONFIG_DIR="$MASTER" gh api "repos/$REPO/keys?per_page=100" > "$TMP/keys.json"
READ_ONLY="$(jq -r --arg key "$PUBLIC_KEY" '[.[] | select((.key | split(" ") | .[0:2] | join(" ")) == $key) | .read_only] | if length == 0 then "absent" else .[0] | tostring end' "$TMP/keys.json")"
case "$READ_ONLY" in
  false)
    echo "AARIS_GIT_BRIDGE_EXISTING_WRITE_DEPLOY_KEY_REUSED"
    ;;
  true)
    echo "AARIS_GIT_BRIDGE_EXISTING_KEY_IS_READ_ONLY: manual review required"
    exit 14
    ;;
  absent)
    GH_CONFIG_DIR="$MASTER" gh api --method POST "repos/$REPO/keys" \
      -f "title=aaris-codespace-$MACHINE" \
      -f "key=$PUBLIC_KEY" -F read_only=false \
      --jq '.read_only' > "$TMP/key-created.txt"
    [[ "$(cat "$TMP/key-created.txt")" == false ]] || {
      echo "AARIS_GIT_BRIDGE_WRITE_KEY_REGISTRATION_FAILED"; exit 15;
    }
    echo "AARIS_GIT_BRIDGE_REPO_ONLY_WRITE_DEPLOY_KEY_REGISTERED"
    ;;
  *)
    echo "AARIS_GIT_BRIDGE_UNEXPECTED_DEPLOY_KEY_RESPONSE"; exit 16
    ;;
esac

echo "AARIS_GIT_BRIDGE_SELECTED_MACHINE=$MACHINE"
GH_CONFIG_DIR="$WORKER" gh codespace ssh -c "$MACHINE" -- \
  'bash "$HOME/.local/share/aaris-git/canonical_git.sh" status'
GH_CONFIG_DIR="$WORKER" gh codespace ssh -c "$MACHINE" -- \
  'bash "$HOME/.local/share/aaris-git/canonical_git.sh" verify'
echo "AARIS_GIT_BRIDGE_READY: canonical fetch/pull/commit/publish/push-main available via Turbo terminal"
