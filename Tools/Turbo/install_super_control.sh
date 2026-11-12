#!/usr/bin/env bash
# Idempotent AARIS Eyes & Hands installer for Codespace rotation.
# Installs just local user commands. Never launches a second Unity or changes
# project files, licenses, Cloudflare secrets, scenes or Git branches.
set -euo pipefail
SOURCE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
DEST="${AARIS_EYE_HOME:-$HOME/.local/share/aaris-v4}"
BIN="${AARIS_EYE_BIN:-$HOME/.local/bin}"
for app in xdotool scrot python3; do
  if ! command -v "$app" >/dev/null 2>&1; then
    echo "AARIS_SUPER_CONTROL_REQUIRES_$app" >&2
    exit 2
  fi
done
python3 - <<'PY'
from PIL import Image
print('AARIS_PIL_READY')
PY
mkdir -p "$DEST" "$BIN"
for file in gui_v4.py unity_v4.py eye_v5.py;do
  install -m 0644 -T "$SOURCE/$file" "$DEST/$file"
done
install -m 0755 -T "$SOURCE/aaris-eye" "$DEST/aaris-eye"
python3 -m py_compile "$DEST/gui_v4.py" "$DEST/unity_v4.py" "$DEST/eye_v5.py"
"$DEST/aaris-eye" selftest
ln -sfn "$DEST/aaris-eye" "$BIN/aaris-eye"
echo "AARIS_SUPER_CONTROL_READY=$BIN/aaris-eye"
echo "AARIS_SUPER_CONTROL_CODESPACE=${CODESPACE_NAME:-local}"
