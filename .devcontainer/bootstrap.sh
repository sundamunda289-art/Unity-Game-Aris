#!/usr/bin/env bash
set -euo pipefail
# Installed once at Codespace creation. Unity Editor is a separate on-demand install.
# Avoid Codespaces recovery mode on a transient package-mirror or GPU-dependency
# outage. All deferred steps can be retried with the explicit approved operations.
if sudo apt-get update -qq; then
  echo 'AARIS_APT_INDEX_READY'
else
  echo 'AARIS_APT_INDEX_DEFERRED'
fi
if sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq git-lfs jq python3 python3-pil xvfb fluxbox scrot imagemagick x11-utils curl gnupg ca-certificates procps openssh-server; then
  echo 'AARIS_BOOTSTRAP_PACKAGES_READY'
else
  echo 'AARIS_BOOTSTRAP_PACKAGES_DEFERRED'
fi
if command -v git-lfs >/dev/null 2>&1 || command -v git >/dev/null 2>&1; then
  git lfs install --local --skip-smudge || echo 'AARIS_BOOTSTRAP_LFS_INIT_DEFERRED'
fi
# Desktop/OpenGL are optional for an operable SSH shell; retry via CS DEPS.
if bash Tools/Codespaces/unity_gui_deps.sh; then
  echo 'AARIS_BOOTSTRAP_GUI_READY'
else
  echo 'AARIS_BOOTSTRAP_GUI_DEFERRED'
fi
if bash .devcontainer/start_screen.sh; then
  echo 'AARIS_BOOTSTRAP_DISPLAY_READY'
else
  echo 'AARIS_BOOTSTRAP_DISPLAY_DEFERRED'
fi
echo 'AARIS_BOOTSTRAP_COMPLETED_WITH_DEFERRED_STEPS_POSSIBLE'

