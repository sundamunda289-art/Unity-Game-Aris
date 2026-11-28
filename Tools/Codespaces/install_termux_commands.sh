#!/data/data/com.termux/files/usr/bin/bash
# One-time command installation. Credentials and account selection are separate.
set -Eeuo pipefail
umask 077
REPO='sundamunda289-art/Unity-Game-Aris'
command -v pkg >/dev/null || { echo 'Run this installer in Android Termux.'; exit 2; }
pkg install -y python gh curl jq coreutils util-linux proot-distro
ROOT_DIR="$HOME/.local/share/aaris-switch"
mkdir -p "$ROOT_DIR/releases" "$HOME/.config/aaris-ai"
chmod 700 "$ROOT_DIR" "$ROOT_DIR/releases" "$HOME/.config/aaris-ai"
exec 9>"$ROOT_DIR/install.lock"
flock -n 9 || { echo 'An Aaris command installation is already running.'; exit 75; }
TEMP_DIR="$(mktemp -d "${TMPDIR:-$PREFIX/tmp}/aaris-command-install.XXXXXXXX")"
trap 'rm -rf "$TEMP_DIR"' EXIT
SOURCE_SHA="${AARIS_INSTALL_SHA:-}"
if [[ -z "$SOURCE_SHA" ]]; then
  curl --fail --silent --show-error --retry 3 \
    "https://api.github.com/repos/$REPO/git/ref/heads/main" -o "$TEMP_DIR/ref.json"
  SOURCE_SHA="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["object"]["sha"])' "$TEMP_DIR/ref.json")"
fi
[[ "$SOURCE_SHA" =~ ^[0-9a-f]{40}$ ]] || { echo 'Canonical source SHA could not be verified.'; exit 3; }
RELEASE="$ROOT_DIR/releases/$SOURCE_SHA"
STAGE="$ROOT_DIR/releases/.staging-$SOURCE_SHA-$$"
mkdir -p "$STAGE/Tools/Codespaces" "$STAGE/Tools/Turbo"
cleanup() { rm -rf "$TEMP_DIR" "$STAGE"; }
trap cleanup EXIT
FILES=(
  Tools/Codespaces/termux_controller.sh
  Tools/Codespaces/termux_functions.sh
  Tools/Codespaces/switch_controller.py
  Tools/Codespaces/termux_cf_runner.sh
  Tools/Codespaces/termux_git_bridge.sh
  Tools/Codespaces/canonical_git.sh
  Tools/Codespaces/termux_unity_login_recover.sh
  Tools/Codespaces/mobile_license.py
  Tools/Codespaces/auto_provision_codespace.sh
  Tools/Codespaces/provision_codespace.py
  Tools/Codespaces/install_unity.sh
  Tools/Codespaces/verify_lfs.py
  Tools/Codespaces/unity_gui_deps.sh
  Tools/Turbo/cloudflare_account_switch_linux.sh
  Tools/Turbo/device_oauth_parse.py
  Tools/Turbo/wrangler_auth_diag.py
)
for FILE in "${FILES[@]}"; do
  curl --fail --silent --show-error --retry 3 \
    "https://raw.githubusercontent.com/$REPO/$SOURCE_SHA/$FILE" -o "$STAGE/$FILE"
  test -s "$STAGE/$FILE"
  case "$FILE" in
    *.sh) bash -n "$STAGE/$FILE" ;;
    *.py) python3 - "$STAGE/$FILE" <<'PY'
import pathlib,sys
p=pathlib.Path(sys.argv[1]); compile(p.read_bytes(), str(p), 'exec')
PY
      ;;
  esac
  chmod 700 "$STAGE/$FILE"
done
printf '%s\n' "$SOURCE_SHA" > "$STAGE/SOURCE_SHA"
if [[ -e "$RELEASE" ]]; then
  # Never mutate an installed immutable release in place.
  for FILE in "${FILES[@]}"; do
    cmp -s "$STAGE/$FILE" "$RELEASE/$FILE" || { echo 'Installed release differs; preserved for review.'; exit 4; }
  done
  rm -rf "$STAGE"
else
  mv "$STAGE" "$RELEASE"
fi
ln -s "$RELEASE" "$ROOT_DIR/.current-$$"
mv -Tf "$ROOT_DIR/.current-$$" "$ROOT_DIR/current"
python3 - <<'PY'
from pathlib import Path
import os,tempfile,time
rc=Path.home()/'.bashrc'
line='source "$HOME/.local/share/aaris-switch/current/Tools/Codespaces/termux_functions.sh"'
old=rc.read_text() if rc.exists() else ''
if line not in old.splitlines():
    if rc.is_symlink(): raise SystemExit('Symlinked .bashrc preserved; add the printed source line manually.')
    backup=Path.home()/'.config/aaris-ai/install-backups'
    backup.mkdir(parents=True,exist_ok=True,mode=0o700)
    if rc.exists():
        dest=backup/('bashrc-'+str(time.time_ns()))
        dest.write_bytes(rc.read_bytes());dest.chmod(0o600)
    mode=(rc.stat().st_mode & 0o777) if rc.exists() else 0o600
    fd,name=tempfile.mkstemp(dir=rc.parent,prefix='.aaris-bashrc-')
    with os.fdopen(fd,'w') as f:
        f.write(old+('\n' if old and not old.endswith('\n') else '')+'\n# Aaris verified command launcher\n'+line+'\n')
        f.flush();os.fsync(f.fileno())
    os.chmod(name,mode);os.replace(name,rc)
PY
printf '%s\n' '✅ CCF / CF / cf और CD commands install हो गए। Accounts अभी नहीं बदले गए।'
printf '%s\n' 'अब चलाएँ: source "$HOME/.bashrc"' 'Cloudflare: CCF' 'Codespace + Unity setup: CD' 'स्थिति: CD --status'
