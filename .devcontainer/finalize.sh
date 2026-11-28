#!/usr/bin/env bash
# Codespaces postCreateCommand: verify actual assets on a NEW machine.
# Never reset/clean/revert a Unity scene, Git LFS file, or uncommitted edit.
set -Eeuo pipefail
cd "$(dirname "$0")/.."
mkdir -p /tmp/aaris-codespaces
if bash .devcontainer/start_screen.sh; then
  echo AARIS_POSTCREATE_DISPLAY_READY
else
  echo AARIS_POSTCREATE_DISPLAY_DEFERRED_USE_CS_DEPS
fi
if git lfs install --local --skip-smudge; then
  echo AARIS_POSTCREATE_GIT_LFS_READY
else
  echo AARIS_POSTCREATE_GIT_LFS_DEFERRED_USE_CS_LFS
fi

# Source SHA selection and LFS hydration belong to the remote cd job. This hook
# cannot race it with an independent download or a different hosting-fork HEAD.
echo AARIS_CODESPACE_LFS_DEFERRED_TO_PROVISIONER
# Unity download/auth errors must not bring down the Ubuntu devcontainer.
if bash Tools/Codespaces/install_unity.sh >/tmp/aaris-codespaces/postcreate-editor-install.log 2>&1; then
  echo AARIS_CODESPACE_EDITOR_READY
else
  echo AARIS_CODESPACE_EDITOR_DEFERRED_USE_CS_INSTALL
fi
echo AARIS_CODESPACE_PROVISION_WITH_TERMUX_CD

