#!/usr/bin/env bash
# Install once, verify the actual version, and reuse it on this Codespace.
set -Eeuo pipefail
umask 077
VERSION="6000.3.26f1"
CHANGESET="de8167e7440c"
PROJECT="${AARIS_UNITY_PROJECT_DIR:-$PWD}"
GIT_DIR="$(git -C "$PROJECT" rev-parse --absolute-git-dir 2>/dev/null || true)"
if [[ -n "$GIT_DIR" && "$GIT_DIR" == /workspaces/* ]]; then
  DEFAULT_ROOT="$GIT_DIR/aaris-runtime/editors"
  DEFAULT_LOCK="$GIT_DIR/aaris-provision/heavy.lock"
else
  DEFAULT_ROOT="$HOME/.local/share/aaris-unity-editors"
  DEFAULT_LOCK="$HOME/.local/state/aaris-provision/heavy.lock"
fi
EDITOR_ROOT="${AARIS_UNITY_EDITOR_ROOT:-$DEFAULT_ROOT}"
CLI_HOME="${AARIS_UNITY_CLI_HOME:-$HOME/.local/share/aaris-unity-cli}"
RUN="${AARIS_PROVISION_LOG_DIR:-/tmp/aaris-codespaces}"
HEAVY_LOCK="${AARIS_HEAVY_LOCK_PATH:-$DEFAULT_LOCK}"

find_cli() {
  local file
  for file in "$CLI_HOME/bin/unity" "$HOME/.local/bin/unity" "$HOME/.unity/bin/unity" \
              "/home/vscode/.local/bin/unity" "/tmp/aaris-unity-runtime/cli/bin/unity"; do
    if test -x "$file"; then printf '%s\n' "$file"; return 0; fi
  done
  command -v unity || return 1
}

# No project is opened by a version probe. An executable's existence alone is
# not enough: an interrupted extraction can leave a binary in a partial tree.
editor_status() {
  python3 - "$VERSION" "$EDITOR_ROOT" "$HOME/.local/share/aaris-unity-editors" "${1:-normal}" <<'PY'
import json, os, re, subprocess, sys
from pathlib import Path
version, current_root, legacy_root, mode = sys.argv[1:]
seen = set()
for root in (current_root, legacy_root):
    binary = Path(root) / version / "Editor/Unity"
    if str(binary) in seen:
        continue
    seen.add(str(binary))
    data = binary.parent / "Data"
    core = (data / "Managed/UnityEngine/UnityEngine.CoreModule.dll", data / "Managed/UnityEngine.CoreModule.dll")
    if (not binary.is_file() or not os.access(binary, os.X_OK)
            or not (data / "Resources").is_dir()
            or not any(path.is_file() and path.stat().st_size > 0 for path in core)):
        continue
    # New persistent installs require the durable completion record written
    # only after the official CLI returned success. A partial extraction must
    # not become "ready" just because Unity -version already happens to work.
    if root == current_root and root != legacy_root and mode != "after-install":
        try:
            marker = json.loads((Path(root) / ("verified-" + version + ".json")).read_text())
            if marker.get("version") != version or marker.get("editor") != str(binary) or marker.get("changeset") != "de8167e7440c":
                continue
        except (OSError, ValueError):
            continue
    try:
        checked = subprocess.run([str(binary), "-version"], stdin=subprocess.DEVNULL,
                                 stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                 timeout=30, check=False)
        output = checked.stdout.decode("utf-8", "replace")
        if checked.returncode == 0 and re.search(r"(?<![\w.])" + re.escape(version) + r"(?![\w.])", output):
            print(json.dumps({"ready": True, "version": version, "editor": str(binary),
                              "persistent_workspace": str(binary).startswith("/workspaces/"),
                              "android_modules_verified": False}))
            raise SystemExit(0)
    except (OSError, subprocess.TimeoutExpired):
        pass
print(json.dumps({"ready": False, "version": version, "android_modules_verified": False}))
PY
}

case "${1:-install}" in
  --status-json|status)
    editor_status
    exit 0
    ;;
  install) ;;
  *) echo 'Usage: install_unity.sh [install|--status-json]'; exit 2 ;;
esac

mkdir -p "$RUN" "$EDITOR_ROOT" "$CLI_HOME" "$(dirname "$HEAVY_LOCK")"
chmod 700 "$CLI_HOME"
[[ ! -L "$HEAVY_LOCK" ]] || { echo UNITY_UNSAFE_INSTALL_LOCK; exit 12; }
exec 9>"$HEAVY_LOCK"
if ! flock -w 60 9; then
  echo UNITY_HEAVY_OPERATION_BUSY
  exit 75
fi
STATUS="$(editor_status)"
if python3 -c 'import json,sys;sys.exit(0 if json.load(sys.stdin).get("ready") else 1)' <<< "$STATUS"; then
  echo UNITY_PERSISTENT_EDITOR_READY
  exit 0
fi

# A conservative floor covers extraction as well as the compressed package.
# The coordinator performs the separate exact-size LFS/workspace budget later.
FREE_KB="$(df -Pk "$EDITOR_ROOT" | awk 'NR==2{print $4}')"
TMP_FREE_KB="$(df -Pk "$RUN" | awk 'NR==2{print $4}')"
FREE_INODES="$(df -Pi "$EDITOR_ROOT" | awk 'NR==2{print $4}')"
if (( FREE_KB < 16000000 || TMP_FREE_KB < 1000000 || FREE_INODES < 20000 )); then
  echo UNITY_PERSISTENT_SPACE_INSUFFICIENT
  echo UNITY_REQUIRED_FREE_KB_MIN=16000000
  exit 7
fi
export UNITY_CLI_HOME="$CLI_HOME" UNITY_NON_INTERACTIVE=1 DO_NOT_TRACK=1
CLI="$(find_cli || true)"
if test -z "$CLI"; then
  echo UNITY_CLI_BOOTSTRAPPING
  if ! curl -fsSL --retry 3 --connect-timeout 30 --max-time 150 \
      https://unity.com/install.sh -o "$RUN/unity-cli-installer.sh"; then
    echo UNITY_CLI_DOWNLOAD_FAILED; exit 3
  fi
  if ! bash "$RUN/unity-cli-installer.sh" >"$RUN/unity-cli-setup.log" 2>&1; then
    echo UNITY_CLI_INSTALL_FAILED; exit 4
  fi
  CLI="$(find_cli || true)"
fi
if test -z "$CLI" || ! test -x "$CLI"; then
  echo UNITY_CLI_BINARY_MISSING; exit 5
fi
echo UNITY_CLI_READY
echo UNITY_EDITOR_PERSISTENT_INSTALL_START
# The CLI owns partial-download recovery. Do not pass --resume to a nonexistent
# paused download, erase a partial tree, or claim success from a stale marker.
if "$CLI" install "$VERSION" --changeset "$CHANGESET" --download-directory "$EDITOR_ROOT" \
    --non-interactive --yes --accept-eula --format json >"$RUN/cli-editor-install.log" 2>&1; then
  :
else
  INSTALL_EXIT=$?
  printf 'UNITY_EDITOR_INSTALL_EXIT_CODE_%d\n' "$INSTALL_EXIT"
  python3 - "$RUN/cli-editor-install.log" <<'PY'
from pathlib import Path
import sys, re
text = Path(sys.argv[1]).read_text(errors="replace")[-30000:]
checks = [
    ("LOW_DISK", r"no space left|insufficient space|disk.*full"),
    ("AUTHORIZATION", r"authentication required|not signed in|unauthorized|403"),
    ("NETWORK", r"timed out|TLS|certificate|network|connection reset"),
    ("VERSION", r"changeset.*not|not.*changeset|release.*not found|404"),
    ("CLI_USAGE", r"unknown command|unknown flag|unexpected argument|invalid option|unrecognized option"),
    ("INSTALL_BUSY", r"install.*lock|another install|waiting for another"),
    ("PERMISSIONS", r"permission denied|operation not permitted|EACCES"),
    ("EULA", r"accept-eula|terms.*not accepted|TERMS_ACCEPT|CONSENT_REQUIRED|confirmation required"),
]
print("UNITY_EDITOR_INSTALL_CATEGORY_" +
      next((name for name, pattern in checks if re.search(pattern, text, re.I)), "UNCLASSIFIED"))
PY
  exit 8
fi
STATUS="$(editor_status after-install)"
if ! python3 -c 'import json,sys;sys.exit(0 if json.load(sys.stdin).get("ready") else 1)' <<< "$STATUS"; then
  echo UNITY_EDITOR_INSTALL_VERIFICATION_FAILED
  exit 9
fi
python3 - "$EDITOR_ROOT" "$VERSION" "$CHANGESET" "$STATUS" <<'PY'
import json, os, sys, tempfile
from pathlib import Path
root = Path(sys.argv[1])
data = {**json.loads(sys.argv[4]), "changeset": sys.argv[3]}
fd, name = tempfile.mkstemp(prefix=".verified-", dir=root)
try:
    with os.fdopen(fd, "w") as handle:
        json.dump(data, handle)
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(name, root / ("verified-" + sys.argv[2] + ".json"))
finally:
    if os.path.exists(name):
        os.unlink(name)
PY
echo UNITY_PERSISTENT_EDITOR_READY

