#!/usr/bin/env bash
set -Eeuo pipefail
SCRIPT_DIR="$(CDPATH= builtin cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
command -v python3 >/dev/null || { printf '%s\n' 'Python missing: pkg install python'; exit 2; }
# An interrupted operation keeps the controller version that created it, even
# when the owner installs a newer release before resuming it.
PINNED_DIR="$(python3 - "$SCRIPT_DIR" <<'PY'
import json, os, re, sys
from pathlib import Path
current=Path(sys.argv[1]).resolve()
base=Path.home()/'.config/aaris-ai'
try:
    state=json.loads((base/'control.json').read_text())
    operation=state.get('operation','')
    if not re.fullmatch(r'[0-9a-f]{32}', operation): raise ValueError()
    receipt=json.loads((base/'operations'/operation/'operation.json').read_text())
    source=receipt.get('source_sha','')
    if receipt.get('status') not in ('COMPLETE','CANCELLED') and re.fullmatch(r'[0-9a-f]{40}',source):
        candidate=Path.home()/'.local/share/aaris-switch/releases'/source/'Tools/Codespaces'
        if (candidate/'switch_controller.py').is_file() and candidate.resolve().is_relative_to((Path.home()/'.local/share/aaris-switch/releases').resolve()):
            current=candidate.resolve()
except (OSError,ValueError,KeyError):
    pass
print(current)
PY
)"
SCRIPT_DIR="$PINNED_DIR"
exec python3 "$SCRIPT_DIR/switch_controller.py" "$@"
