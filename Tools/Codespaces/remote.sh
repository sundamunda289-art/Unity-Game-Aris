#!/usr/bin/env bash
# Only predetermined terminal tasks; no shell commands from public issue comments.
set -euo pipefail
TASK="$1"
PROJECT="/workspaces/Unity-Game-Aris"
RUN="/tmp/aaris-codespaces"
EDITOR="$HOME/.local/share/aaris-unity-editors/6000.3.26f1/Editor/Unity"
LEGACY_EDITOR="/workspaces/.aaris-unity-editors/6000.3.26f1/Editor/Unity"
OLD_EDITOR="/tmp/aaris-unity-runtime/editors/6000.3.26f1/Editor/Unity"
if ! test -x "$EDITOR" && test -x "$LEGACY_EDITOR"; then EDITOR="$LEGACY_EDITOR"; fi
if ! test -x "$EDITOR" && test -x "$OLD_EDITOR"; then EDITOR="$OLD_EDITOR"; fi
mkdir -p "$RUN"
cd "$PROJECT"
case "$TASK" in
  status)
    echo 'AARIS_CODESPACE_STATUS'
    printf 'commit=%s\n' "$(git rev-parse --short HEAD)"
    printf 'arch=%s\n' "$(uname -m)"
    if test -r /etc/os-release; then
      OS_ID="$(. /etc/os-release && printf '%s' "${ID:-unknown}")"
      OS_VERSION="$(. /etc/os-release && printf '%s' "${VERSION_ID:-unknown}")"
      printf 'os_id=%s\n' "$OS_ID"
      printf 'os_version=%s\n' "$OS_VERSION"
    fi
    GLIBC_VERSION="$(getconf GNU_LIBC_VERSION 2>/dev/null | awk '{print $NF}' || true)"
    if [[ "$GLIBC_VERSION" =~ ^[0-9]+\.[0-9]+$ ]]; then
      printf 'glibc=%s\n' "$GLIBC_VERSION"
    else
      echo 'glibc=unknown'
    fi
    if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
      echo 'docker_ready=yes'
    else
      echo 'docker_ready=no'
    fi
    printf 'cpus=%s\n' "$(nproc)"
    printf 'memory_mb=%s\n' "$(awk '/MemTotal:/ {printf "%.0f", $2/1024}' /proc/meminfo)"
    printf 'workspace_free_kb=%s\n' "$(df -Pk /workspaces | awk 'NR==2{print $4}')"
    printf 'tmp_free_kb=%s\n' "$(df -Pk /tmp | awk 'NR==2{print $4}')"
    if test -x "$EDITOR"; then echo 'editor_installed=yes'; else echo 'editor_installed=no'; fi
    if test -x "$HOME/.local/share/aaris-unity-editors/6000.3.26f1/Editor/Unity"; then echo 'persistent_editor=yes'; else echo 'persistent_editor=no'; fi
    if pgrep -f '[E]ditor/Unity.*-projectPath' >/dev/null 2>&1; then echo 'editor_running=yes'; else echo 'editor_running=no'; fi
    if DISPLAY=:99 xdpyinfo >/dev/null 2>&1; then echo 'display=ready'; else echo 'display=stopped'; fi
    if head -c 100 Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity 2>/dev/null | grep -q 'git-lfs.github.com/spec'; then
      echo 'scene=lfs_pointer'
    else echo 'scene=hydrated_or_missing'; fi
    ;;
  changes)
    # Read-only filenames; never expose diff contents or files outside normal project paths.
    python3 - <<'PY'
import subprocess
r=subprocess.run(['git','status','--porcelain=v1','--untracked-files=no','-z'],stdout=subprocess.PIPE,check=True)
entries=r.stdout.decode('utf-8','replace').split(chr(0))
files=[]
for entry in entries:
    if len(entry)>=4 and entry[:2].strip():
        path=entry[3:]
        if path and path.startswith(('Assets/','Docs/','Tools/','Packages/','ProjectSettings/','.github/')):
            files.append(path)
raw_identical = 0
raw_different = 0
for path in files:
    try:
        index = subprocess.check_output(['git','ls-files','-s','--',path],text=True)
        expected = index.split(chr(9))[0].split()[1]
        current = subprocess.check_output(['git','hash-object','--no-filters','--',path],text=True).strip()
        if current == expected:
            raw_identical += 1
        else:
            raw_different += 1
    except (IndexError, subprocess.CalledProcessError):
        raw_different += 1
print('changed_files_count='+str(len(files)))
print('raw_identical_count='+str(raw_identical))
print('raw_different_count='+str(raw_different))
for path in sorted(files)[:70]:
    if all(c.isalnum() or c in ' /._-()' for c in path):
        print('change='+path)
PY
    ;;
  apply_gameplay)
    # Owner-authorized update of the EXISTING project in place. No worktree,
    # no checkout/reset/stash and no changes to the native map scene or terrain.
    if pgrep -f '[E]ditor/Unity.*-projectPath' >/dev/null 2>&1; then
      echo 'AARIS_GAMEPLAY_INPLACE_EDITOR_BUSY'
      exit 58
    fi
    test -d "$PROJECT/.git" || { echo 'AARIS_GAMEPLAY_REPO_MISSING'; exit 59; }
    git fetch --quiet origin main || { echo 'AARIS_GAMEPLAY_MAIN_FETCH_FAILED'; exit 60; }
    python3 - "$PROJECT" <<'PYINPLACE'
import hashlib
import json
import os
import pathlib
import subprocess
import sys
import tempfile

root = pathlib.Path(sys.argv[1]).resolve(strict=True)
paths = [
    "Assets/AarisWorld/Scripts/AarisOfflineDrop.cs",
    "Assets/AarisWorld/Scripts/AarisOfflineDropVisuals.cs",
    "Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs",
    "Assets/AarisWorld/Scripts/AarisBattleActions.cs",
    "Assets/AarisWorld/Scripts/AarisCharacterVisual.cs",
    "Assets/AarisWorld/Scripts/AarisEquippedGun.cs",
    "Assets/AarisWorld/Scripts/AarisVehicleSession.cs",
    "Assets/AarisWorld/Scripts/AarisWeaponCatalog.cs",
    "Assets/AarisWorld/Scripts/AarisWeaponInventory.cs",
    "Assets/AarisWorld/Scripts/AarisWeaponLootWorld.cs",
    "Assets/AarisWorld/Scripts/AarisWeaponPickup.cs",
    "Assets/AarisWorld/Scripts/AarisBreakablePanel.cs",
    "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs",
    "Assets/AarisWorld/Editor/AarisCodespaceAndroidBuild.cs",
    "Assets/Resources/AarisDrop/CargoPlane.obj",
    "Assets/Resources/AarisDrop/CargoPlane.mtl",
    "Tools/Validation/test_offline_drop_assets.py",
    "Tools/Validation/test_loot_cell_streaming.py",
    "Tools/Validation/simulate_battle_feel.py",
    "Tools/Validation/check_battle_hardening.py",
    "Tools/Validation/check_armed_locomotion.py",
    "Tools/Validation/check_single_weapon_transactions.py",
    "Tools/Validation/check_weapon_loot.py",
]
def git(*args):
    return subprocess.check_output(["git", "-C", str(root), *args], stderr=subprocess.DEVNULL)

head = git("rev-parse", "--verify", "refs/remotes/origin/main^{commit}").decode().strip()
if len(head) != 40 or any(c not in "0123456789abcdef" for c in head):
    raise SystemExit("AARIS_GAMEPLAY_INVALID_MAIN_COMMIT")
before, after = {}, {}
for name in paths:
    target = root / name
    if target.is_symlink() or (target.exists() and not target.is_file()):
        raise SystemExit("AARIS_GAMEPLAY_TARGET_UNSAFE")
    if target.parent.exists() and target.parent.resolve() != (root / name).parent.resolve():
        raise SystemExit("AARIS_GAMEPLAY_TARGET_PARENT_UNSAFE")
    old = target.read_bytes() if target.is_file() else None
    latest = git("show", "refs/remotes/origin/main:" + name)
    if not latest or len(latest) > 2_000_000 or latest.startswith(b"version https://git-lfs.github.com/spec/v1"):
        raise SystemExit("AARIS_GAMEPLAY_TARGET_SOURCE_INVALID")
    before[name] = old
    after[name] = latest

# Fail closed if GitHub main does not contain the exact reviewed fixes.
required = {
    "Assets/AarisWorld/Scripts/AarisOfflineDropVisuals.cs": b"AarisSharedTriBladeRotor",
    "Assets/AarisWorld/Scripts/AarisOfflineDrop.cs": b"support = Mathf.Max(support, t.SampleHeight(position));",
    "Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs": b"ground = Mathf.Max(ground, t.SampleHeight(position));",
    "Assets/AarisWorld/Scripts/AarisBreakablePanel.cs": b"OpenAngle * openDirection",
    "Assets/AarisWorld/Scripts/AarisWeaponLootWorld.cs": b"deferVisual: true",
    "Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs": b"ProneWallSlide(horizontalVelocity, dt)",
    "Assets/AarisWorld/Scripts/AarisCharacterVisual.cs": b"float stanceMotion = Mathf.Clamp01(horizontal / 1.2f) * pose;",
    "Assets/AarisWorld/Scripts/AarisWeaponCatalog.cs": b"modelCache[modelPath] = prefab;",
    "Assets/AarisWorld/Scripts/AarisWeaponPickup.cs": b"public bool TryReserve()",
    "Assets/AarisWorld/Scripts/AarisEquippedGun.cs": b"node.GetComponents(damageScratch)",
    "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs": b"AARIS_TERRAIN_RENDER_AUDIT",
    "Assets/AarisWorld/Editor/AarisCodespaceAndroidBuild.cs": b"ANDROID_TERRAIN_PAINT_LAYERS_MISSING",
    "Assets/Resources/AarisDrop/CargoPlane.obj": b"o Aaris_TacticalTransport_V3",
}
for name, marker in required.items():
    if marker not in after[name]:
        raise SystemExit("AARIS_GAMEPLAY_REVIEWED_SOURCE_NOT_YET_ON_MAIN")

# Backups are held inside this SAME existing project, not a second clone.
# Never touch the other dirty tracked files, especially native map assets.
backup = root / ".aaris-recovery" / ("gameplay-inplace-" + head[:12])
if backup.exists():
    raise SystemExit("AARIS_GAMEPLAY_ALREADY_APPLIED_BACKUP_EXISTS")
backup.mkdir(parents=True, exist_ok=False)
manifest = []
for name in paths:
    dest = backup / name
    dest.parent.mkdir(parents=True, exist_ok=True)
    if before[name] is not None:
        dest.write_bytes(before[name])
    manifest.append({
        "path": name,
        "before_sha256": hashlib.sha256(before[name]).hexdigest() if before[name] is not None else "not_created",
        "after_sha256": hashlib.sha256(after[name]).hexdigest(),
        "original_file_existed": before[name] is not None,
        "changed": before[name] != after[name]
    })
(backup / "manifest.json").write_text(json.dumps({"sourceMain": head, "files": manifest}, indent=2))

updated = []
try:
    for name in paths:
        if before[name] == after[name]:
            continue
        target = root / name
        target.parent.mkdir(parents=True, exist_ok=True)
        mode = (target.stat().st_mode & 0o777) if target.exists() else 0o644
        fd, temp = tempfile.mkstemp(prefix=".aaris-inplace-", dir=target.parent)
        try:
            with os.fdopen(fd, "wb") as f:
                f.write(after[name])
                f.flush()
                os.fsync(f.fileno())
            os.chmod(temp, mode)
            os.replace(temp, target)
        finally:
            if os.path.exists(temp):
                os.unlink(temp)
        updated.append(name)
except Exception:
    for name in reversed(updated):
        target = root / name
        if before[name] is None:
            target.unlink(missing_ok=True)
        else:
            target.write_bytes(before[name])
    raise
for name in paths:
    if (root / name).read_bytes() != after[name]:
        raise SystemExit("AARIS_GAMEPLAY_WRITE_VERIFY_FAILED")
print("AARIS_GAMEPLAY_EXISTING_PROJECT_UPDATED")
print("gameplay_files_checked=" + str(len(paths)))
print("gameplay_files_updated=" + str(len(updated)))
print("main_commit=" + head)
print("map_scene_terrain_materials_preserved=yes")
print("previous_gameplay_files_backed_up=yes")
PYINPLACE
    # Run license-free regressions directly in the original project.
    python3 Tools/Validation/test_offline_drop_assets.py >/dev/null || {
      echo 'AARIS_GAMEPLAY_INPLACE_ASSET_REGRESSION_FAILED'; exit 61;
    }
    python3 Tools/Validation/test_loot_cell_streaming.py >/dev/null || {
      echo 'AARIS_GAMEPLAY_INPLACE_LOOT_REGRESSION_FAILED'; exit 62;
    }
    python3 Tools/Validation/simulate_battle_feel.py >/dev/null || {
      echo 'AARIS_GAMEPLAY_INPLACE_MOVEMENT_REGRESSION_FAILED'; exit 63;
    }
    python3 Tools/Validation/check_single_weapon_transactions.py >/dev/null || {
      echo 'AARIS_GAMEPLAY_INPLACE_WEAPON_REGRESSION_FAILED'; exit 64;
    }
    echo 'AARIS_GAMEPLAY_INPLACE_STATIC_TESTS_PASSED'
    ;;
  sync)
    # Never discard unsaved work. The legacy repo contains exactly 11 checked-in
    # full blobs mistakenly matched by a later git-lfs clean filter. Allow these
    # only after proving raw byte-for-byte equality with the current Git index.
    test "$(git branch --show-current)" = main || { echo 'SYNC_REQUIRES_MAIN_BRANCH'; exit 8; }
    python3 - <<'PY'
import subprocess,sys
# These 11 files have a known Git-LFS filter false positive, while two
# ProjectSettings files may be genuinely changed by Unity's first import.
# Never bypass the raw-blob integrity guard on any asset or unrelated file.
settings={
    'ProjectSettings/ProjectSettings.asset',
    'ProjectSettings/ProjectVersion.txt',
}
allowed={
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
'Assets/AarisWorld/Props/StaticWreck/Geometry_LOD2.asset'}
data=subprocess.check_output(['git','status','--porcelain=v1','--untracked-files=no','-z'])
entries=[e for e in data.split(b'\0') if e]
for e in entries:
    status=e[:2].decode('ascii','replace')
    path=e[3:].decode('utf-8','replace')
    if status not in {' M','M '} or path not in (allowed | settings):
        print('SYNC_BLOCKED_REAL_OR_UNEXPECTED_CHANGE')
        sys.exit(8)
    if path in allowed:
        index=subprocess.check_output(['git','ls-files','-s','--',path],text=True).split('\t')[0].split()[1]
        raw=subprocess.check_output(['git','hash-object','--no-filters','--',path],text=True).strip()
        if raw!=index:
            print('SYNC_BLOCKED_CHANGED_BYTES')
            sys.exit(8)
print('SYNC_WORKTREE_VERIFIED_RAW_IDENTICAL='+str(len(entries)-sum(e[3:].decode('utf-8','replace') in settings for e in entries)))
PY
    # Unity 6000.3 may write real local ProjectSettings migration changes as it
    # imports the scene. Snapshot them in /workspaces, then stash ONLY these
    # two explicitly reviewed files before the fast-forward. Never reset/clean.
    if ! git diff --quiet -- ProjectSettings/ProjectSettings.asset ProjectSettings/ProjectVersion.txt ||
       ! git diff --cached --quiet -- ProjectSettings/ProjectSettings.asset ProjectSettings/ProjectVersion.txt; then
      umask 077
      BACKUP="$PROJECT/.aaris-recovery/project-settings/$(date -u +%Y%m%dT%H%M%SZ)-$"
      mkdir -p "$BACKUP"
      cp -p ProjectSettings/ProjectSettings.asset "$BACKUP/ProjectSettings.asset"
      cp -p ProjectSettings/ProjectVersion.txt "$BACKUP/ProjectVersion.txt"
      # Stop rather than silently discard local ProjectSettings if Git cannot
      # create a recoverable stash commit. The persistent copies remain in repo.
      if ! GIT_AUTHOR_NAME='Aaris Codespace Recovery' \
           GIT_AUTHOR_EMAIL='codespace-recovery@users.noreply.github.com' \
           GIT_COMMITTER_NAME='Aaris Codespace Recovery' \
           GIT_COMMITTER_EMAIL='codespace-recovery@users.noreply.github.com' \
           git stash push -m 'Aaris Unity project settings before safe sync' -- \
             ProjectSettings/ProjectSettings.asset ProjectSettings/ProjectVersion.txt >/dev/null; then
        echo 'SYNC_SETTINGS_BACKUP_SAVED_STASH_FAILED'
        exit 12
      fi
      if ! git diff --quiet -- ProjectSettings/ProjectSettings.asset ProjectSettings/ProjectVersion.txt; then
        echo 'SYNC_SETTINGS_STILL_DIRTY'
        exit 12
      fi
      echo 'SYNC_SETTINGS_BACKUP_AND_STASH_SAVED'
    fi
    git fetch --quiet origin main
    # Suppress only the already-proven LFS clean-filter false positives.
    git -c filter.lfs.process= -c filter.lfs.clean=cat \
        -c filter.lfs.required=false merge --ff-only --quiet origin/main || {
      echo 'SYNC_NON_FAST_FORWARD_OR_FILTER_CONFLICT'; exit 9;
    }
    git lfs pull >/dev/null 2>&1 || { echo 'SYNC_LFS_FAILED'; exit 10; }
    echo 'SYNC_LFS_READY'
    ;;
  lfs_report)
    # Report pointer hydration and diagnostics without printing any raw network logs.
    python3 - <<'PY'
import pathlib,subprocess,re
root=pathlib.Path('.')
res=subprocess.run(['git','lfs','ls-files','--name-only'],capture_output=True,text=True,timeout=25)
if res.returncode:
    print('LFS_ENUMERATION_FAILED')
    raise SystemExit(2)
files=[x for x in res.stdout.splitlines() if x]
pointers=[]
for item in files:
    p=root/item
    if p.is_file():
        try:
            if p.open('rb').read(110).startswith(b'version https://git-lfs.github.com/spec/v1'):
                pointers.append(item)
        except OSError:
            pass
log=pathlib.Path('/tmp/aaris-codespaces/lfs-pull.log')
s=log.read_text(errors='replace')[-300000:] if log.is_file() else ''
def kind(s):
    if re.search(r'\[404\]|Object does not exist on the server|batch response: Repository or object not found',s,re.I):
        return 'REMOTE_404'
    if re.search(r'\[403\]|not authorized|permission denied|unauthorized',s,re.I):
        return 'REMOTE_403'
    if re.search(r'bandwidth|quota|billing|exceeded',s,re.I):
        return 'QUOTA'
    if re.search(r'no such file or directory|not found',s,re.I):
        return 'LOCAL_PATH_OR_REMOTE_NOT_FOUND'
    if re.search(r'certificate|dns|connection|timeout|TLS',s,re.I):
        return 'NETWORK'
    return 'UNKNOWN'
print('lfs_tracked_count='+str(len(files)))
print('lfs_unhydrated_pointer_count='+str(len(pointers)))
print('lfs_error_kind='+kind(s))
print('lfs_log_recorded='+('yes' if s else 'no'))
critical='Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity'
p=root/critical
if not p.is_file(): scene='missing'
elif critical in pointers: scene='pointer'
else: scene='file_not_pointer'
print('scene='+scene)
# LFS errors may identify object OIDs, which are public content hashes, not credentials.
sha=list(dict.fromkeys(re.findall(r'\b[a-f0-9]{64}\b',s,re.I)))
print('lfs_error_hash_count='+str(len(sha)))
for i,oid in enumerate(sha[:3],1):
    print('lfs_object_hash_'+str(i)+'='+oid.lower())
PY
    ;;
  lfs)
    mkdir -p /tmp/aaris-unity-runtime/lfs
    if ! git lfs version >/dev/null 2>&1; then
      echo 'GIT_LFS_CLI_MISSING'
      exit 19
    fi
    echo 'GIT_LFS_CLI_PRESENT'
    git config --local lfs.storage /tmp/aaris-unity-runtime/lfs
    if git lfs pull > "$RUN/lfs-pull.log" 2>&1; then
      echo 'LFS_DOWNLOAD_OK'
    else
      python3 - "$RUN/lfs-pull.log" <<'PY'
import sys,re,pathlib
s=pathlib.Path(sys.argv[1]).read_text(errors='replace')[-20000:]
if re.search(r'quota|bandwidth|data transfer limit|billing|over its data',s,re.I):
    reason='quota_or_bandwidth'
elif re.search(r'403|permission denied|not authorized|authentication|unauthorized',s,re.I):
    reason='authorization'
elif re.search(r'404|not found|does not exist',s,re.I):
    reason='missing_lfs_object'
elif re.search(r'no space left|insufficient space',s,re.I):
    reason='disk_full'
elif re.search(r'network|connection|timeout|TLS|certificate',s,re.I):
    reason='network'
elif re.search(r'not a git command|git-lfs is not installed',s,re.I):
    reason='lfs_cli_missing'
else:
    reason='unclassified'
print('LFS_FAIL_CATEGORY_'+reason.upper())
PY
      exit 20
    fi
    ;;
  cli_diag)
    # Read-only bounded diagnostics. The Unity CLI's raw output, OAuth state,
    # paths and arbitrary log lines are never returned to the public issue.
    python3 - <<'PY'
import os, pathlib, re, subprocess
home=pathlib.Path.home()
candidates=[home/".local/bin/unity",home/".local/share/aaris-unity-cli/bin/unity",
            home/".unity/bin/unity",pathlib.Path("/home/vscode/.local/bin/unity")]
cli=next((str(p) for p in candidates if p.is_file() and os.access(p,os.X_OK)),None)
print("UNITY_CLI_PRESENT="+("yes" if cli else "no"))
if cli:
    try:
        r=subprocess.run([cli,"install","--help"],capture_output=True,
                         text=True,errors="replace",timeout=25,check=False)
        h=(r.stdout+"\n"+r.stderr)[:100000]
        print("UNITY_CLI_INSTALL_HELP_EXIT_"+str(r.returncode if 0<=r.returncode<256 else 255))
        flags={"CHANGESET":"--changeset","DOWNLOAD_DIRECTORY":"--download-directory",
               "RESUME":"--resume","NON_INTERACTIVE":"--non-interactive",
               "YES":"--yes","ACCEPT_EULA":"--accept-eula","FORMAT":"--format"}
        for name,flag in flags.items():
            print("UNITY_CLI_INSTALL_FLAGS_"+name+"="+("yes" if flag in h else "no"))
    except (OSError,subprocess.TimeoutExpired):
        print("UNITY_CLI_INSTALL_HELP_UNAVAILABLE")
log=pathlib.Path("/tmp/aaris-codespaces/cli-editor-install.log")
if not log.is_file():
    print("UNITY_CLI_INSTALL_LOG_MISSING")
else:
    s=log.read_text(errors="replace")[-100000:]
    codes=set(re.findall(r'"code"\s*:\s*"([A-Z_]{3,50})"',s))
    allowed={"INVALID_COMMAND_ARGS","INVALID_ARGUMENTS","RELEASE_NOT_FOUND",
             "COMMAND_FAILED","PATH_NOT_A_DIRECTORY","AUTH_REQUIRED",
             "EULA_REQUIRED","INSTALL_FAILED","ELEVATION_FAILED"}
    print("UNITY_CLI_INSTALL_LOG_RECORDED=yes")
    for c in sorted(codes & allowed):
        print("UNITY_CLI_INSTALL_LOG_CODE_"+c)
    print("UNITY_CLI_INSTALL_LOG_CODE_OTHER="+("yes" if bool(codes-allowed) else "no"))
    for name,pattern in {
        "CONFLICTING_ARGUMENTS":r"mutually exclusive|conflict(?:s|ing)? with",
        "MISSING_REQUIRED_ARGUMENT":r"missing (?:required )?argument",
        "UNKNOWN_ARGUMENT":r"unknown (?:option|flag|argument)|unexpected argument|unrecognized option",
        "VERSION_FORMAT":r"invalid version|invalid version format",
        "CHANGESET":r"invalid changeset|changeset.*(invalid|unexpected)",
        "AUTHORIZATION":r"not signed in|authentication required|authorization",
    }.items():
        print("UNITY_CLI_INSTALL_LOG_DIAGNOSTIC_"+name+"="+("yes" if re.search(pattern,s,re.I) else "no"))
    for name,flag in {"CHANGESET":"--changeset","DOWNLOAD_DIRECTORY":"--download-directory",
                      "RESUME":"--resume","NON_INTERACTIVE":"--non-interactive",
                      "YES":"--yes","ACCEPT_EULA":"--accept-eula",
                      "FORMAT":"--format"}.items():
        # Only flag names from a constant allowlist can enter the response.
        bad=(r"(?:unknown|unrecognized|unexpected|invalid|unsupported)"
             r".{0,90}"+re.escape(flag))
        print("UNITY_CLI_INSTALL_LOG_REJECTED_"+name+"="+("yes" if re.search(bad,s,re.I|re.S) else "no"))
PY
    ;;
  bootstrap)
    bash "$PROJECT/.devcontainer/bootstrap.sh"
    ;;
  install)
    bash "$PROJECT/Tools/Codespaces/install_unity.sh"
    ;;
  editor_diag)
    # A bounded, read-only scan of the local Editor launch log: no raw text,
    # source paths, user IDs, license XML, OAuth URLs or credentials emitted.
    python3 - <<'PY'
from pathlib import Path
import re
logs=[Path("/tmp/aaris-codespaces/Editor.log"),
      Path("/tmp/aaris-codespaces/editor-launch-stderr.log")]
available=[p for p in logs if p.is_file() and p.stat().st_size>0]
# Only a bounded integer is public; never show raw crash paths/logs.
exit_witness=Path("/tmp/aaris-codespaces/editor-exit-code.txt")
if exit_witness.is_file():
    try:
        code=int(exit_witness.read_text().strip())
        if 0 <= code <= 255:
            print("AARIS_EDITOR_EXIT_CODE_"+str(code))
            if code==127:
                print("AARIS_EDITOR_DIAG_EXEC_OR_LIBRARY_FAILURE")
            elif code==137:
                print("AARIS_EDITOR_DIAG_SIGKILL_OR_OOM")
            elif code==139:
                print("AARIS_EDITOR_DIAG_SEGFAULT")
            elif code != 0:
                print("AARIS_EDITOR_DIAG_NONZERO_EXIT")
    except (OSError,ValueError):
        print("AARIS_EDITOR_DIAG_EXIT_STATUS_UNREADABLE")
if not available:
    print("AARIS_EDITOR_DIAG_LOG_MISSING")
    raise SystemExit(0)
s="\n".join(p.read_text(errors="replace")[-100000:] for p in available)
print("AARIS_EDITOR_DIAG_LOG_FOUND")
classes={
    "LICENSING":r"no valid.*licen[sc]e|licen[sc]e.*(failed|expired|invalid|not found|required|not active)|failed to activate",
    "GRAPHICS":r"failed.*(initialize|init).*(graphics|opengl|vulkan)|could not create.*(gl|graphics)|GfxDevice.*failed",
    "DISPLAY":r"cannot open display|cannot connect to.*X server|X11.*error|no display",
    "LIBRARY":r"error while loading shared libraries|cannot open shared object file|lib[A-Za-z0-9._+-]+\\.so[^\\n]*not found",
    "CRASH":r"segmentation fault|SIGSEGV|signal 11|crash handler",
    "OOM":r"out of memory|std::bad_alloc|failed to allocate",
    "PROJECT_LOCK":r"project.*(already open|in use|lock)",
    "IMPORT_ERROR":r"failed to import|asset database.*error|project.*corrupt",
    "PACKAGE_ERROR":r"package manager.*error|failed.*(resolve|download).*package"
}
found=False
for name,pattern in classes.items():
    if re.search(pattern,s,re.I):
        print("AARIS_EDITOR_DIAG_"+name)
        found=True
if not found:
    print("AARIS_EDITOR_DIAG_UNCLASSIFIED")
PY
    ;;
  editor)
    case "${2:-}" in ''|glcore) ;; *) echo 'EDITOR_LAUNCH_MODE_NOT_ALLOWED'; exit 12;; esac
    EDITOR_ARGS=()
    if test "${2:-}" = glcore; then EDITOR_ARGS=(-force-glcore); fi
    if ! test -x "$EDITOR"; then
      echo 'EDITOR_ABSENT_INSTALLING_ON_TMP_DISK'
      echo 'EDITOR_NOT_INSTALLED_USE_CS_INSTALL_FIRST'
      exit 11
    fi
    # Put Unity's large import cache on /tmp and preserve checked-in source files.
    mkdir -p /tmp/aaris-unity-runtime/project-cache
    for name in Library Temp Logs obj; do
      target="$PROJECT/$name"
      if test -L "$target"; then continue; fi
      if test -d "$target"; then
        if test -n "$(ls -A "$target" 2>/dev/null)"; then
          echo "CACHE_DIRECTORY_NONEMPTY_$name"
          continue
        fi
        rmdir "$target"
      fi
      if ! test -e "$target"; then
        mkdir -p "/tmp/aaris-unity-runtime/project-cache/$name"
        ln -s "/tmp/aaris-unity-runtime/project-cache/$name" "$target"
      fi
    done
    bash "$PROJECT/.devcontainer/start_screen.sh" >/dev/null
    if ! pgrep -f '[E]ditor/Unity.*-projectPath' >/dev/null 2>&1; then
      mkdir -p "$RUN/UnityTemp"
      # Keep raw loader errors PRIVATE on this Codespace; only editor_diag
      # may export a fixed classification to the owner Issue.
      rm -f "$RUN/editor-exit-code.txt"
      # A private exit-status witness survives a fast native crash. No raw
      # arguments, OAuth values, terminal output or proprietary logs leave
      # this Codespace. The wrapper is detached from the SSH session.
      nohup setsid env DISPLAY=:99 TMPDIR="$RUN/UnityTemp" LIBGL_ALWAYS_SOFTWARE=1 \
        bash -c 'rc=0; "$1" -projectPath "$2" "${@:5}" -logFile "$3" || rc=$?; printf "%s\\n" "$rc" > "$4"; exit "$rc"' \
        bash "$EDITOR" "$PROJECT" "$RUN/Editor.log" "$RUN/editor-exit-code.txt" "${EDITOR_ARGS[@]}" \
        </dev/null >"$RUN/editor-launch-stdout.log" 2>"$RUN/editor-launch-stderr.log" &
      sleep 8
    fi
    if ! pgrep -f '[E]ditor/Unity.*-projectPath' >/dev/null 2>&1; then
      echo 'EDITOR_LAUNCH_FAILED'
      exit 12
    fi
    # Process existence after eight seconds is NOT a successful Editor launch.
    # Large imports must remain running; catch delayed licensing/GL crashes
    # before reporting a false-positive and requesting a screenshot.
    sleep 12
    if pgrep -f '[E]ditor/Unity.*-projectPath' >/dev/null 2>&1; then
      echo 'EDITOR_PROCESS_STARTED_LICENSE_NOT_VERIFIED'
    else
      echo 'EDITOR_EXITED_DURING_STARTUP'
      exit 12
    fi
    ;;
  license)
    # Unity CLI may be installed under UNITY_CLI_HOME/bin, not ~/.local/bin.
    # Use the effective SSH user's real home when inherited HOME is misleading.
    AUTH_HOME="$(getent passwd "$(id -u)" | cut -d: -f6)"
    if ! test -d "$AUTH_HOME"; then AUTH_HOME="$HOME"; fi
    CLI=""
    for candidate in "$AUTH_HOME/.local/share/aaris-unity-cli/bin/unity" \
                     "$AUTH_HOME/.local/bin/unity" "$AUTH_HOME/.unity/bin/unity" \
                     "$HOME/.local/share/aaris-unity-cli/bin/unity" "$HOME/.local/bin/unity" \
                     "/home/vscode/.local/share/aaris-unity-cli/bin/unity" \
                     "/home/vscode/.local/bin/unity" "/tmp/aaris-unity-runtime/cli/bin/unity"; do
      if test -x "$candidate"; then CLI="$candidate"; break; fi
    done
    if test -z "$CLI"; then
      echo 'UNITY_CLI_NOT_INSTALLED'
    else
      echo 'UNITY_CLI_DISCOVERED'
      if env UNITY_CLI_HOME="$AUTH_HOME/.local/share/aaris-unity-cli" \
          "$CLI" license status --format json --non-interactive >/dev/null 2>&1; then
        echo 'UNITY_PERSONAL_OR_SUBSCRIPTION_LICENSE_ACTIVE'
      else
        echo 'UNITY_LICENSE_NOT_VERIFIED'
      fi
    fi
    ;;
  shot)
    pgrep -f '[E]ditor/Unity.*-projectPath' >/dev/null || { echo 'SHOT_BLOCKED_EDITOR_NOT_RUNNING'; exit 14; }
    DISPLAY=:99 xdpyinfo >/dev/null 2>&1 || { echo 'SHOT_BLOCKED_NO_DISPLAY'; exit 15; }
    DISPLAY=:99 scrot -o "$RUN/screen.png" || { echo 'SHOT_FAILED'; exit 16; }
    test -s "$RUN/screen.png" || exit 16
    VARIANCE="$(convert "$RUN/screen.png" -colorspace Gray -format '%[fx:standard_deviation]' info: 2>/dev/null || echo 0)"
    python3 - "$VARIANCE" <<'PY'
import sys
try: ok=float(sys.argv[1])>=0.015
except ValueError: ok=False
if not ok: raise SystemExit("SHOT_BLANK_REJECTED")
PY
    echo 'SHOT_CREATED_NEEDS_VISUAL_REVIEW'
    ;;
  test)
    python3 Tools/CI/ai_world_commands.py
    python3 Tools/CI/session_store.py selftest
    echo 'SELFTEST_OK'
    ;;
  *)
    echo 'INVALID_TASK'; exit 2 ;;
esac
