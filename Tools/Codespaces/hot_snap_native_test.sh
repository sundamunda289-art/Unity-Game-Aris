#!/usr/bin/env bash
# One-time native PR #239 test; original Codespace checkout is read-only.
set -Eeuo pipefail
SHA="$1"
[[ "$SHA" =~ ^[0-9a-f]{40}$ ]] || { echo HOT_SNAP_INVALID_COMMIT;exit 11; }
SRC=/workspaces/Unity-Game-Aris
OUT=/tmp/aaris-codespaces/hot-snapshot-test
mkdir -p "$OUT" /tmp/aaris-hot-review-worktrees
if pgrep -f '[E]ditor/Unity.*-projectPath' >/dev/null 2>&1;then
  echo HOT_SNAP_EDITOR_BUSY;exit 12;
fi
EDITOR=""
for x in "$HOME/.local/share/aaris-unity-editors/6000.3.26f1/Editor/Unity" \
  /workspaces/.aaris-unity-editors/6000.3.26f1/Editor/Unity;do
  if test -x "$x";then EDITOR="$x";break;fi
done
test -n "$EDITOR" || { echo HOT_SNAP_EDITOR_MISSING;exit 13; }
git -C "$SRC" fetch --quiet origin refs/heads/feat/portable-hot-unity-snapshots || {
 echo HOT_SNAP_FETCH_FAILED;exit 14;
}
test "$(git -C "$SRC" rev-parse FETCH_HEAD)" = "$SHA" || {
 echo HOT_SNAP_SHA_MISMATCH;exit 15;
}
PROJECT="/tmp/aaris-hot-review-worktrees/$SHA"
if test ! -d "$PROJECT";then
 GIT_LFS_SKIP_SMUDGE=1 git -C "$SRC" -c filter.lfs.process= \
  -c filter.lfs.smudge=cat -c filter.lfs.required=false \
  worktree add --detach "$PROJECT" "$SHA" >/dev/null 2>&1 || {
  echo HOT_SNAP_WORKTREE_FAILED;exit 16;
 }
fi
test "$(git -C "$PROJECT" rev-parse HEAD)" = "$SHA" || exit 17
cd "$PROJECT"
git lfs checkout >/dev/null 2>&1 || true
if ! python3 Tools/Codespaces/verify_lfs.py >/dev/null 2>&1;then
 python3 - "$SRC" "$PROJECT" <<'PY'
from pathlib import Path
import hashlib,subprocess,sys,shutil
src,dst=map(Path,sys.argv[1:])
items=subprocess.run(['git','lfs','ls-files','--long'],cwd=dst,
  capture_output=True,text=True,check=True).stdout.splitlines()
n=0
for line in items:
  oid,sep,rel=line.partition(' ')
  if not sep or len(oid)!=64 or not all(c in '0123456789abcdef' for c in oid):continue
  rel=rel.lstrip('*- ').strip()
  if not rel or Path(rel).is_absolute() or '..' in Path(rel).parts:continue
  a,b=src/rel,dst/rel
  if not a.is_file() or not b.is_file():continue
  with b.open('rb') as f:
    if not f.read(80).startswith(b'version https://git-lfs.github.com/spec/v1'):continue
  if hashlib.sha256(a.read_bytes()).hexdigest()!=oid:continue
  shutil.copyfile(a,b);n+=1
print('HOT_SNAP_VERIFIED_LFS_COPIES_'+str(n))
PY
fi
python3 Tools/Codespaces/verify_lfs.py >/dev/null || { echo HOT_SNAP_LFS_NOT_READY;exit 18; }
test -x tools/snap || { echo HOT_SNAP_CLI_MISSING;exit 19; }
bash .devcontainer/start_screen.sh >/dev/null || { echo HOT_SNAP_DISPLAY_FAIL;exit 20; }
bash Tools/Codespaces/graphics_preflight.sh >/dev/null || { echo HOT_SNAP_GLX_FAIL;exit 21; }
mkdir -p "$OUT/images"
find "$OUT/images" -maxdepth 1 -type f -name '*.png' -delete
nohup env DISPLAY=:99 LIBGL_ALWAYS_SOFTWARE=1 GALLIUM_DRIVER=llvmpipe \
  "$EDITOR" -projectPath "$PROJECT" -force-glcore \
  -logFile "$OUT/private-editor.log" \
  >"$OUT/private-stdout.log" 2>"$OUT/private-stderr.log" </dev/null &
PID="$!"
READY=0
for i in $(seq 1 540);do
 if ! kill -0 "$PID" 2>/dev/null;then
  echo HOT_SNAP_EDITOR_EXITED;exit 22;
 fi
 if ./tools/snap status >"$OUT/private-health.json" 2>/dev/null && \
   python3 - "$OUT/private-health.json" <<'PY'
import json,sys
d=json.load(open(sys.argv[1]))
sys.exit(0 if d.get('fresh') and d.get('state')=='ready' else 1)
PY
 then READY=1;break;fi
 sleep 2
done
test "$READY" = 1 || { echo HOT_SNAP_EDITOR_HEARTBEAT_TIMEOUT;exit 23; }
echo HOT_SNAP_EDITOR_READY
./tools/snap batch Tools/Snapshots/example-shots.json --out-dir "$OUT/images" --timeout 480 \
  >"$OUT/private-batch.log" 2>&1 || { echo HOT_SNAP_BATCH_FAILED;exit 24; }
./tools/snap recapture --id valley-01 --out "$OUT/images/valley-recapture.png" --timeout 480 \
  >"$OUT/private-recapture.log" 2>&1 || { echo HOT_SNAP_RECAPTURE_FAILED;exit 25; }
python3 - "$PROJECT/ci-live-control" <<'PY'
import json,sys,time
from pathlib import Path
folder=Path(sys.argv[1]);nonce=1234567829
tmp=folder/'camera.json.write'
tmp.write_text(json.dumps(dict(x=-420,y=22,z=-210,yaw=0,pitch=20,distance=80,nonce=nonce)))
tmp.replace(folder/'camera.json')
for i in range(150):
  try:
    d=json.loads((folder/'status.json').read_text())
    p=folder/'latest.png'
    if d.get('nonce')==nonce and p.is_file() and p.stat().st_size>5000:
      if p.open('rb').read(8)==b'\x89PNG\r\n\x1a\n':
        break
  except (ValueError,OSError):pass
  time.sleep(1)
else:raise SystemExit(26)
PY
cp "$PROJECT/ci-live-control/latest.png" "$OUT/images/legacy.png"
python3 - "$OUT" <<'PY'
from pathlib import Path
import json,sys
p=Path(sys.argv[1])
names=('valley-01.png','clocktower-01.png','valley-recapture.png','legacy.png')
for n in names:
  f=p/'images'/n
  if not f.is_file() or f.stat().st_size<5000 or f.open('rb').read(8)!=b'\x89PNG\r\n\x1a\n':
    raise SystemExit(27)
(p/'review.json').write_text(json.dumps(dict(status='passed',imageCount=4,
    hotEditor=True, multiShot=True, recapture=True, legacyBridge=True),indent=2)+'\n')
PY
tar -C "$OUT" -czf "$OUT/hot-review.tar.gz" review.json images
echo HOT_SNAP_REAL_UNITY_BATCH_RECAPTURE_LEGACY_PASSED
