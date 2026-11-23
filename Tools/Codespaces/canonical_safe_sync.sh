#!/usr/bin/env bash
# Canonical main synchronization. Sent over SSH stdin by an owner-only Actions
# workflow; does not create a worktree, reset, clean, stash, or alter origin.
set -euo pipefail
MODE="${1:-status}"
case "$MODE" in status|sync|fetch) ;; *) echo AARIS_SYNC_ERROR=invalid_mode; exit 2 ;; esac
PROJECT=/workspaces/Unity-Game-Aris
if [[ ! -d "$PROJECT/.git" ]]; then
  echo AARIS_SYNC_ERROR=project_checkout_missing
  exit 3
fi
cd "$PROJECT"
if [[ "$(git rev-parse --show-toplevel)" != "$PROJECT" ]]; then
  echo AARIS_SYNC_ERROR=unexpected_checkout_root
  exit 4
fi
BRANCH="$(git branch --show-current)"
BEFORE="$(git rev-parse HEAD)"
echo "AARIS_SYNC_BRANCH=$BRANCH"
echo "AARIS_SYNC_BEFORE_SHA=$BEFORE"
if [[ "$MODE" == status ]]; then
  echo "AARIS_SYNC_TRACKED_CHANGES=$(git status --porcelain=v1 --untracked-files=no | wc -l | tr -d ' ')"
  echo "AARIS_SYNC_UNTRACKED_COUNT=$(git ls-files --others --exclude-standard | wc -l | tr -d ' ')"
  # Report counts only: do not expose other agents' filenames or content.
  python3 - <<'PY'
import subprocess
false_positives = {
    'Assets/AarisWorld/Props/ClimbableTower/Geometry.asset',
    'Assets/AarisWorld/Props/ClimbableTower/Geometry_LOD1.asset',
    'Assets/AarisWorld/Props/ClimbableTower/Geometry_LOD2.asset',
    'Assets/AarisWorld/Props/RockCover/Geometry.asset',
    'Assets/AarisWorld/Props/RockCover/Geometry_LOD1.asset',
    'Assets/AarisWorld/Props/RockCover/Geometry_LOD2.asset',
    'Assets/AarisWorld/Props/Shared/rock_09_ARM.png',
    'Assets/AarisWorld/Props/Shared/rusty_metal_02_ARM.png',
    'Assets/AarisWorld/Props/StaticWreck/Geometry.asset',
    'Assets/AarisWorld/Props/StaticWreck/Geometry_LOD1.asset',
    'Assets/AarisWorld/Props/StaticWreck/Geometry_LOD2.asset',
}
entries = [x for x in subprocess.check_output([
    'git','status','--porcelain=v1','--untracked-files=no','-z'
]).split(b'\0') if x]
known = 0
unknown = 0
worktree_only = 0
staged = 0
raw_matches = 0
raw_mismatches = 0
for entry in entries:
    kind = entry[:2].decode('ascii', 'replace')
    path = entry[3:].decode('utf-8', 'replace')
    if path not in false_positives:
        unknown += 1
        continue
    known += 1
    if kind == ' M': worktree_only += 1
    if kind != ' M': staged += 1
    index_row = subprocess.check_output(['git','ls-files','-s','--',path],text=True)
    if not index_row or '\t' not in index_row:
        raw_mismatches += 1
        continue
    indexed = index_row.split('\t')[0].split()[1]
    actual = subprocess.check_output(['git','hash-object','--no-filters','--',path],text=True).strip()
    if indexed == actual: raw_matches += 1
    else: raw_mismatches += 1
for key,value in {
    'KNOWN':known, 'UNKNOWN':unknown, 'WORKTREE_ONLY':worktree_only,
    'STAGED_OR_OTHER':staged, 'RAW_MATCH':raw_matches,
    'RAW_MISMATCH':raw_mismatches
}.items():
    print('AARIS_SYNC_AUDIT_' + key + '=' + str(value))
PY
  echo AARIS_SYNC_STATUS_COMPLETE=yes
  exit 0
fi
if [[ "$MODE" == sync && "$BRANCH" != main ]]; then
  echo AARIS_SYNC_ERROR=main_branch_required
  exit 5
fi
# Keep all local edits in place. Fetching canonical main does not edit the
# working tree; only after comparing every incoming path may we fast-forward.
# Fetch the authoritative repo, not a potentially stale Codespace host fork.
# FETCH_HEAD avoids changing origin or its local remote-tracking branches.
CANONICAL=https://github.com/sundamunda289-art/Unity-Game-Aris.git
if ! git -c filter.lfs.process= -c filter.lfs.required=false \
    fetch --quiet --no-tags "$CANONICAL" main; then
  echo AARIS_SYNC_ERROR=canonical_fetch_failed
  exit 7
fi
TARGET="$(git rev-parse FETCH_HEAD)"
echo "AARIS_SYNC_TARGET_SHA=$TARGET"
if [[ "$MODE" == fetch ]]; then
  # Reference-only sync for a busy feature branch. Never checkout, merge,
  # modify the index, or write a single authored/agent working file.
  git update-ref refs/remotes/aaris-canonical/main "$TARGET"
  [[ "$(git rev-parse HEAD)" == "$BEFORE" ]] || {
    echo AARIS_SYNC_ERROR=fetch_changed_head_unexpectedly
    exit 12
  }
  [[ "$(git rev-parse refs/remotes/aaris-canonical/main)" == "$TARGET" ]] || {
    echo AARIS_SYNC_ERROR=canonical_ref_mismatch
    exit 13
  }
  echo AARIS_SYNC_FETCHED_REF=refs_remotes_aaris-canonical_main
  echo AARIS_SYNC_CURRENT_BRANCH_UNCHANGED=yes
  echo AARIS_SYNC_TRACKED_WORKTREE_UNTOUCHED=yes
  echo AARIS_SYNC_SUCCESS=reference_only
  exit 0
fi
if ! git merge-base --is-ancestor HEAD FETCH_HEAD; then
  echo AARIS_SYNC_ERROR=non_fast_forward_preserving_local_history
  exit 8
fi
# A real local edit may coexist with a fast-forward when main changes only
# OTHER files. Compare staged, unstaged, and untracked paths against the entire
# incoming main diff. Do not stash or touch another agent's edits.
SNAPSHOT="$(mktemp /tmp/aaris-canonical-safe-ff.XXXXXX)"
export SNAPSHOT
trap 'rm -f -- "$SNAPSHOT"' EXIT
python3 - <<'PY'
import hashlib, json, os, subprocess, sys

def paths(*args):
    data = subprocess.check_output(['git', *args])
    return set(x.decode('utf-8', 'surrogateescape') for x in data.split(b'\0') if x)

staged = paths('diff', '--cached', '--name-only', '--no-renames', '-z')
unstaged = paths('diff', '--name-only', '--no-renames', '-z')
# HEAD includes both effective changes and staged-then-reverted content.
effective = paths('diff', 'HEAD', '--name-only', '--no-renames', '-z')
dirty = staged | unstaged | effective
untracked = paths('ls-files', '--others', '--exclude-standard', '-z')
incoming = paths('diff', '--name-only', '--no-renames', '-z', 'HEAD', 'FETCH_HEAD')
overlap = (dirty | untracked) & incoming
print('AARIS_SYNC_PRESERVED_TRACKED_COUNT=' + str(len(dirty)))
print('AARIS_SYNC_PRESERVED_UNTRACKED_COUNT=' + str(len(untracked)))
print('AARIS_SYNC_INCOMING_PATH_COUNT=' + str(len(incoming)))
print('AARIS_SYNC_OVERLAP_COUNT=' + str(len(overlap)))
if overlap:
    print('AARIS_SYNC_ERROR=tracked_local_changes_preserved_due_overlap')
    sys.exit(6)

def digest(path):
    if not os.path.lexists(path): return 'missing'
    if os.path.islink(path): return 'symlink:' + os.readlink(path)
    if not os.path.isfile(path): return 'other'
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()

with open(os.environ['SNAPSHOT'], 'w', encoding='utf-8') as f:
    json.dump({p: digest(p) for p in sorted(dirty)}, f)
print('AARIS_SYNC_DISJOINT_PATH_PREFLIGHT=pass')
PY
if ! git -c filter.lfs.process= -c filter.lfs.clean=cat \
    -c filter.lfs.required=false merge --ff-only --quiet FETCH_HEAD; then
  echo AARIS_SYNC_ERROR=fast_forward_refused_preserving_worktree
  exit 9
fi
# Prove the original tracked local bytes survived the fast-forward.
python3 - <<'PY'
import hashlib, json, os, sys

def digest(path):
    if not os.path.lexists(path): return 'missing'
    if os.path.islink(path): return 'symlink:' + os.readlink(path)
    if not os.path.isfile(path): return 'other'
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()

with open(os.environ['SNAPSHOT'], encoding='utf-8') as f:
    expected = json.load(f)
for path, original in expected.items():
    if digest(path) != original:
        print('AARIS_SYNC_ERROR=tracked_local_file_byte_mismatch')
        sys.exit(11)
print('AARIS_SYNC_LOCAL_TRACKED_BYTES_PRESERVED=yes')
PY
AFTER="$(git rev-parse HEAD)"
if [[ "$AFTER" != "$TARGET" ]]; then
  echo AARIS_SYNC_ERROR=head_mismatch
  exit 10
fi
echo "AARIS_SYNC_AFTER_SHA=$AFTER"
echo AARIS_SYNC_SUCCESS=yes
echo AARIS_SYNC_NO_RESET_NO_CLEAN_NO_STASH_NO_WORKTREE=yes
