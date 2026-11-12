#!/usr/bin/env bash
set -euo pipefail
mkdir -p /tmp/aaris-codespaces
if ! DISPLAY=:99 xdpyinfo >/dev/null 2>&1; then
  nohup Xvfb :99 -screen 0 1600x900x24 -nolisten tcp >/tmp/aaris-codespaces/xvfb.log 2>&1 </dev/null &
  for i in $(seq 1 20); do
    if DISPLAY=:99 xdpyinfo >/dev/null 2>&1; then break; fi
    sleep 1
  done
fi
DISPLAY=:99 xdpyinfo >/dev/null 2>&1 || { echo 'DISPLAY_ERROR'; exit 3; }
if ! pgrep -x fluxbox >/dev/null 2>&1; then
  nohup env DISPLAY=:99 fluxbox >/tmp/aaris-codespaces/fluxbox.log 2>&1 </dev/null &
fi
echo 'DISPLAY_READY'

# Optional and fail-safe: provision the local AI Eyes/Hands command after each
# Codespace start when the reviewed files exist in this checkout. This never
# launches a second Unity Editor, starts a public server or changes game assets.
AARIS_REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
AARIS_EYE_INSTALLER="$AARIS_REPO_ROOT/Tools/Turbo/install_super_control.sh"
if [[ "${AARIS_SKIP_EYE_AUTO_INSTALL:-0}" != "1" && -f "$AARIS_EYE_INSTALLER" ]]; then
  if bash "$AARIS_EYE_INSTALLER" >/tmp/aaris-codespaces/eye-install.log 2>&1; then
    echo AARIS_AI_EYE_HANDS_READY
  else
    # Missing OS packages should not prevent the normal Linux desktop from booting.
    echo AARIS_AI_EYE_HANDS_DEFERRED
  fi
fi
