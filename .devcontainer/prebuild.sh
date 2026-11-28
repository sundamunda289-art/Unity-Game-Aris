#!/usr/bin/env bash
# Codespaces onCreateCommand. GitHub's prebuild service snapshots this phase.
# No secrets, Unity account activation, ULF, OAuth state or Hub login is captured.
set -Eeuo pipefail
cd "$(dirname "$0")/.."
bash .devcontainer/bootstrap.sh

# Heavy game hydration belongs to the one resumable Termux cd provisioner.
# Never let a lifecycle hook fetch LFS before its verified Editor stage.
mkdir -p /tmp/aaris-codespaces
git lfs install --local --skip-smudge || echo AARIS_PREBUILD_LFS_INIT_DEFERRED
echo AARIS_PREBUILD_LFS_DEFERRED_TO_PROVISIONER

# The verified Editor installer is idempotent. It only downloads when missing,
# and does NOT run Unity ID sign-in or request/store a machine license.
# A failed multi-gigabyte Editor download must never turn the entire Codespace
# into an inaccessible recovery container. Retry later with CS INSTALL.
if bash Tools/Codespaces/install_unity.sh >/tmp/aaris-codespaces/prebuild-editor-install.log 2>&1; then
  echo AARIS_PREBUILD_EDITOR_READY
else
  echo AARIS_PREBUILD_EDITOR_DEFERRED_USE_CS_INSTALL
fi

