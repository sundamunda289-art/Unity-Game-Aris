#!/usr/bin/env bash
# Modify ONLY the user's existing Codespace workspace /workspaces/Unity-Game-Aris.
# It is SAFE if Unity Editor is not running: do not start another Editor.
# No worktree, branch checkout/reset/stash, scene or terrain mutation.
set -Eeuo pipefail
umask 077
PROJECT=/workspaces/Unity-Game-Aris
BRANCH=feat/drop-aircraft-realism-fps-20261110
EXPECTED_BASE=8fb69b4e6dd8609f7a5ef4b4ab5493fcc29c143d
EXPECTED_HEAD_FILE=Assets/AarisWorld/Scripts/AarisOfflineDrop.cs
EXPECTED_AIRFRAME=Assets/Resources/AarisDrop/CargoPlane.obj
test -d "$PROJECT/.git" || { echo AARIS_V3_PROJECT_MISSING; exit 11; }
test -f "$PROJECT/$EXPECTED_HEAD_FILE" || { echo AARIS_V3_EXISTING_GAMEPLAY_MISSING; exit 12; }
test -f "$PROJECT/$EXPECTED_AIRFRAME" || { echo AARIS_V3_EXISTING_AIRFRAME_MISSING; exit 13; }

# The user wants the EXISTING project. Never start, kill or close Unity.
# If Unity happens to be live, confirm it owns this exact project. If Unity
# is not running, safely edit on-disk assets so the NEXT normal Editor refresh
# loads them without a second Editor. Refuse another project being open.
python3 - "$PROJECT" <<'PYPREFLIGHT'
from pathlib import Path
import sys
root=Path(sys.argv[1]).resolve()
editors=[]
for process in Path('/proc').iterdir():
    if not process.name.isdigit(): continue
    try:
        args=(process/'cmdline').read_bytes().split(b'\0')
        args=[a.decode('utf-8','replace') for a in args if a]
    except (OSError,PermissionError):
        continue
    if not any(a.endswith('/Editor/Unity') for a in args):
        continue
    if '-projectPath' not in args:
        continue
    at=args.index('-projectPath')
    if at+1<len(args): editors.append(Path(args[at+1]).resolve())
print('AARIS_V3_EDITOR_COUNT='+str(len(editors)))
for editor in editors[:4]:
    label=('primary' if editor==root else
           'tmp' if str(editor).startswith('/tmp/') else
           'other')
    print('AARIS_V3_OPEN_PROJECT_CLASS='+label)
    print('AARIS_V3_OPEN_PROJECT_HAS_ASSETS='+str((editor/'Assets').is_dir()).lower())
    print('AARIS_V3_OPEN_PROJECT_HAS_GIT='+str((editor/'.git').exists()).lower())
if editors and (editors.count(root)!=1 or len(editors)!=1):
    print('AARIS_V3_FOREIGN_OR_MULTIPLE_EDITORS_PROTECT_WORKSPACE')
    sys.exit(21)
if editors:
    print('AARIS_V3_EXISTING_PROJECT_EDITOR_CONFIRMED')
else:
    print('AARIS_V3_EDITOR_STOPPED_WRITING_EXISTING_WORKSPACE_ONLY')
# Avoid asset reloads during an existing Android APK build / expensive imports.
busy={'gradle','java','il2cpp','bee_backend','aapt2','clang','lld','UnityShaderCompiler'}
for process in Path('/proc').iterdir():
    if not process.name.isdigit(): continue
    try: name=(process/'comm').read_text().strip()
    except (OSError,PermissionError): continue
    if name in busy:
        print('AARIS_V3_ACTIVE_BUILD_OR_IMPORT_DETECTED_NO_FILES_CHANGED')
        sys.exit(22)
print('AARIS_V3_EXISTING_CODESPACE_WORKSPACE_VERIFIED')
PYPREFLIGHT

# Fetch immutable reviewed commit history without touching current branch or
# replacing the checked-out source. The checked-in native world remains intact.
git -C "$PROJECT" fetch --quiet origin "refs/heads/$BRANCH:refs/remotes/origin/$BRANCH" || {
  echo AARIS_V3_REVIEW_BRANCH_FETCH_FAILED; exit 23;
}
ACTUAL_SHA="$(git -C "$PROJECT" rev-parse --verify "refs/remotes/origin/$BRANCH^{commit}")"
[[ "$ACTUAL_SHA" =~ ^[0-9a-f]{40}$ ]] || exit 24
echo "AARIS_V3_REVIEW_SHA=${ACTUAL_SHA:0:12}"

python3 - "$PROJECT" "$EXPECTED_BASE" "$ACTUAL_SHA" <<'PYAPPLY'
from pathlib import Path
import subprocess,sys,hashlib,json,tempfile,os,datetime
root=Path(sys.argv[1]).resolve(strict=True)
base,head=sys.argv[2:4]
paths=['Assets/AarisWorld/Scripts/AarisOfflineDrop.cs',
       'Assets/Resources/AarisDrop/CargoPlane.obj']
def git_blob(commit,path):
    return subprocess.check_output(['git','-C',str(root),'show',f'{commit}:{path}'],
                                   stderr=subprocess.DEVNULL)
def sha(v):return hashlib.sha256(v).hexdigest()
prepared=[]
for relative in paths:
    target=root/relative
    if not target.is_file() or target.is_symlink():
        raise SystemExit('AARIS_V3_TARGET_NOT_REGULAR_FILE')
    ours=target.read_bytes()
    before=git_blob(base,relative)
    desired=git_blob(head,relative)
    if desired.startswith(b'version https://git-lfs.github.com/spec/v1') or len(desired)>2_000_000:
        raise SystemExit('AARIS_V3_REVIEW_SOURCE_INVALID')
    if relative.endswith('CargoPlane.obj') and b'o Aaris_TacticalTransport_V3' not in desired:
        raise SystemExit('AARIS_V3_PLANE_MARKER_MISSING')
    if relative.endswith('AarisOfflineDrop.cs') and not all(x in desired for x in (
        b'SimulateAirborne(dt);',b'AdvanceDepartingAircraft(dt);',
        b'footMotor.ResumeFromAerialDrop();')):
        raise SystemExit('AARIS_V3_DROP_RESTORE_MISSING')
    if ours==desired:
        print('AARIS_V3_ALREADY_APPLIED='+relative)
        continue
    if ours==before:
        merged=desired
    else:
        # Never clobber a dirty player script. Merge only if git's 3-way
        # textual merge is clean, otherwise abort before ANY file is changed.
        with tempfile.TemporaryDirectory(prefix='aaris-v3-merge-') as tmp:
            names=[Path(tmp)/x for x in ('ours','base','v3')]
            for p,data in zip(names,(ours,before,desired)):p.write_bytes(data)
            p=subprocess.run(['git','merge-file','-p',*(str(x) for x in names)],
                             stdout=subprocess.PIPE,stderr=subprocess.DEVNULL)
            if p.returncode or b'<<<<<<<' in p.stdout:
                print('AARIS_V3_LOCAL_EDIT_CONFLICT='+relative)
                sys.exit(25)
            merged=p.stdout
        print('AARIS_V3_EXISTING_LOCAL_EDITS_MERGED='+relative)
    prepared.append((relative,target,ours,merged))
if not prepared:
    print('AARIS_V3_LIVE_PROJECT_ALREADY_CURRENT')
    sys.exit(0)

# Preserve exact old byte content, including unsaved local edits. Backups are
# inside the owner project, outside Unity Assets; never in a public artifact.
backup=root/'.aaris-recovery'/('open-editor-v3-'+head[:12])
if backup.exists():
    print('AARIS_V3_EXISTING_BACKUP_STOP_NO_REWRITE')
    sys.exit(26)
backup.mkdir(parents=True,exist_ok=False)
manifest=[]
for relative,target,ours,merged in prepared:
    b=backup/relative;b.parent.mkdir(parents=True,exist_ok=True);b.write_bytes(ours)
    manifest.append({'path':relative,'previous_sha256':sha(ours),
                     'upgrade_sha256':sha(merged),'bytes':len(merged)})
(backup/'manifest.json').write_text(json.dumps({'base':base,'head':head,
      'created_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
      'files':manifest},indent=2))
applied=[]
try:
    for relative,target,ours,merged in prepared:
        mode=target.stat().st_mode&0o777
        fd,name=tempfile.mkstemp(prefix='.aaris-reviewed-v3-',dir=target.parent)
        try:
            with os.fdopen(fd,'wb') as writer:
                writer.write(merged);writer.flush();os.fsync(writer.fileno())
            os.chmod(name,mode)
            os.replace(name,target)
        finally:
            if os.path.exists(name):os.unlink(name)
        applied.append((target,ours))
    for _,target,_,merged in prepared:
        if target.read_bytes()!=merged:
            raise RuntimeError('postwrite_hash_mismatch')
except Exception:
    for target,ours in reversed(applied):target.write_bytes(ours)
    print('AARIS_V3_WRITE_FAILED_ROLLBACK')
    raise
print('AARIS_V3_EXISTING_CODESPACE_PROJECT_UPDATED')
print('AARIS_V3_FILES_UPDATED='+str(len(prepared)))
print('AARIS_V3_LOCAL_BACKUPS_CREATED=yes')
print('AARIS_V3_NATIVE_SCENE_AND_TERRAIN_UNTOUCHED=yes')
PYAPPLY

# No second Unity instance, no build, no Editor shutdown. Existing GUI Editor
# imports changed .cs/.obj when idle. The runtime itself remains unchanged
# until Unity completes its normal asset refresh and script domain reload.
echo AARIS_V3_SAME_CODESPACE_PROJECT_PRESERVED_NO_EDITOR_LAUNCHED
