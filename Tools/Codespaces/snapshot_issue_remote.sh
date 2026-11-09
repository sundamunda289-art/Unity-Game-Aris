#!/usr/bin/env bash
# Only an owner-authorized, strictly validated Issue #77 CS SNAP payload reaches here.
# A reviewed script is streamed over SSH; never eval the issue comment.
set -Eeuo pipefail
TOKEN="$1"
if [[ ! "$TOKEN" =~ ^[A-Za-z0-9_-]{12,16000}={0,2}$ ]]; then
  echo SNAPSHOT_REQUEST_INVALID_ENVELOPE; exit 21
fi
ROOT=/workspaces/Unity-Game-Aris
if ! test -f "$ROOT/Tools/Codespaces/snapshot_cli.py" ||
   ! test -f "$ROOT/Assets/AarisWorld/Editor/AarisLiveCameraBridge.cs"; then
  echo SNAPSHOT_BRIDGE_NOT_SYNCED_IN_THIS_CODESPACE; exit 22
fi
cd "$ROOT"
BASE=/tmp/aaris-codespaces/hot-snapshots
umask 077
mkdir -p "$BASE"
rm -f "$BASE/bundle.tar.gz" "$BASE/result.json"
mkdir -p "$BASE/images"
# Delete only stale generated PNGs in this fixed temporary output folder.
# Never allow old runs to mix with the current verified archive.
find "$BASE/images" -maxdepth 1 -type f -name '*.png' -delete
python3 - "$TOKEN" "$BASE/request.json" <<'PY'
import base64,json,sys
from pathlib import Path
token,path=sys.argv[1:]
decoded=base64.urlsafe_b64decode(token.encode("ascii"))
if len(decoded)>16000: raise SystemExit(25)
obj=json.loads(decoded)
if not isinstance(obj,dict) or set(obj) not in ({"shots"},{"recapture"}): raise SystemExit(26)
Path(path).write_text(json.dumps(obj))
PY
if ! python3 Tools/Codespaces/snapshot_cli.py status >/dev/null 2>&1; then
  echo SNAPSHOT_EDITOR_NOT_READY_USE_BOOTSTRAP_OR_LICENSE
  exit 23
fi
MODE="$(python3 - "$BASE/request.json" <<'PY'
import json,sys
data=json.load(open(sys.argv[1]))
print("recapture" if "recapture" in data else "batch")
PY
)"
rc=0
if test "$MODE" = recapture; then
  NAME="$(python3 - "$BASE/request.json" <<'PY'
import json,sys
print(json.load(open(sys.argv[1]))["recapture"])
PY
)"
  ./tools/snap recapture --id "$NAME" --out "$BASE/images/$NAME.png" --timeout 840 \
    >"$BASE/cli.stdout" 2>"$BASE/cli.stderr" || rc=$?
else
  ./tools/snap batch "$BASE/request.json" --out-dir "$BASE/images" --timeout 840 \
    >"$BASE/cli.stdout" 2>"$BASE/cli.stderr" || rc=$?
fi
if test "$rc" != 0 && test "$rc" != 3; then
  echo SNAPSHOT_CAPTURE_FAILED_CHECK_EDITOR_HEARTBEAT
  exit 24
fi
REQ="$(grep -oE 'QUEUED snap-[a-f0-9]{32}' "$BASE/cli.stdout" | head -n 1 | cut -d ' ' -f2)"
if [[ ! "$REQ" =~ ^snap-[a-f0-9]{32}$ ]]; then
  echo SNAPSHOT_RESULT_ID_NOT_FOUND; exit 25
fi
SOURCE="$ROOT/ci-live-control/snapshot-results/$REQ.json"
test -s "$SOURCE" || { echo SNAPSHOT_RESULT_FILE_MISSING; exit 26; }
cp "$SOURCE" "$BASE/result.json"
python3 - "$BASE/result.json" "$BASE/images" <<'PY'
import json,re,sys
from pathlib import Path
data=json.load(open(sys.argv[1]));root=Path(sys.argv[2])
if data.get("state") not in ("passed","suspect"): raise SystemExit(27)
frames=data.get("frames",[])
if not isinstance(frames,list) or not 1<=len(frames)<=20: raise SystemExit(28)
for f in frames:
  name=f.get("id")
  if not isinstance(name,str) or not re.fullmatch(r"[A-Za-z][A-Za-z0-9_-]{0,47}",name): raise SystemExit(29)
  photo=root/(name+".png")
  if not photo.is_file() or photo.stat().st_size<5000:
    raise SystemExit(30)
  with photo.open("rb") as handle:
    if handle.read(8)!=b"\x89PNG\r\n\x1a\n": raise SystemExit(31)
PY
tar -C "$BASE" -czf "$BASE/bundle.tar.gz" result.json images
test "$(stat -c %s "$BASE/bundle.tar.gz")" -le 110000000 || { echo SNAPSHOT_ARCHIVE_TOO_BIG; exit 32; }
if test "$rc" -eq 0; then
  echo SNAPSHOT_REAL_EDITOR_FRAMES_PASSED
else
  echo SNAPSHOT_REAL_EDITOR_FRAMES_SUSPECT_REVIEW
fi
