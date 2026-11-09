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
# Optional GL readiness check: retain the exact original postStartCommand.
# Missing Mesa must not put the entire Codespace into recovery mode.
if ! bash Tools/Codespaces/graphics_preflight.sh; then
  echo 'AARIS_SNAPSHOT_GL_PREFLIGHT_DEFERRED'
fi
