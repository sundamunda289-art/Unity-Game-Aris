#!/usr/bin/env bash
# One immutable remote provisioner; no shell eval or credentials on argv.
set -Eeuo pipefail
umask 077
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
exec python3 "$SCRIPT_DIR/provision_codespace.py" "$@"
