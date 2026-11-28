#!/data/data/com.termux/files/usr/bin/bash
# Compatibility entry point; all mutations use the shared CCF/CD controller.
set -Eeuo pipefail
LOCAL_DIR="$(CDPATH= builtin cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
CONTROLLER="$LOCAL_DIR/termux_controller.sh"
if [[ ! -f "$CONTROLLER" ]]; then
  CONTROLLER="$HOME/.local/share/aaris-switch/current/Tools/Codespaces/termux_controller.sh"
fi
if [[ ! -f "$CONTROLLER" ]]; then
  command -v pkg >/dev/null || { echo 'Use Android Termux.'; exit 2; }
  pkg install -y curl
  INSTALLER="$(mktemp "${TMPDIR:-$PREFIX/tmp}/aaris-install.XXXXXXXX")"
  trap 'rm -f "$INSTALLER"' EXIT
  curl --fail --silent --show-error --retry 3 https://raw.githubusercontent.com/sundamunda289-art/Unity-Game-Aris/main/Tools/Codespaces/install_termux_commands.sh -o "$INSTALLER"
  bash -n "$INSTALLER"
  bash "$INSTALLER"
fi
exec bash "$CONTROLLER" cf "$@"
