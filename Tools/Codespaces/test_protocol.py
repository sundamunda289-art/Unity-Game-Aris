#!/usr/bin/env python3
"""Public CI tests; no live token, Unity seat or Codespace quota required."""
import json
from pathlib import Path
r=Path(__file__).resolve().parents[2]
d=json.loads((r/'.devcontainer/devcontainer.json').read_text())
assert d['onCreateCommand']=='bash .devcontainer/prebuild.sh'
assert d['postCreateCommand']=='bash .devcontainer/finalize.sh'
assert 'bash .devcontainer/bootstrap.sh' in (r/'.devcontainer/prebuild.sh').read_text()
assert 'bash Tools/Codespaces/install_unity.sh' in (r/'.devcontainer/prebuild.sh').read_text()
assert 'git lfs pull' in (r/'.devcontainer/prebuild.sh').read_text()
assert 'git lfs pull' in (r/'.devcontainer/finalize.sh').read_text()
assert 'python3 Tools/Codespaces/verify_lfs.py' in (r/'.devcontainer/finalize.sh').read_text()
assert 'UNITY_LICENSE' not in (r/'.devcontainer/prebuild.sh').read_text()
assert 'UNITY_LICENSE' not in (r/'.devcontainer/finalize.sh').read_text()
assert d['postStartCommand']=='bash .devcontainer/start_screen.sh'
assert 'ghcr.io/devcontainers/features/sshd:1' in d['features']
# Regression: WheelCollider / WheelFrictionCurve live in the Unity built-in
# VehiclesModule; leaving it out makes the entire vehicle assembly fail to compile.
manifest=json.loads((r/'Packages/manifest.json').read_text())['dependencies']
assert manifest.get('com.unity.modules.vehicles')=='1.0.0'
vehicle=(r/'Assets/AarisWorld/Scripts/AarisVehicleMotor.cs').read_text()
assert 'WheelCollider' in vehicle and 'WheelFrictionCurve' in vehicle
combat=(r/'Assets/AarisWorld/Scripts/AarisBattleActions.cs').read_text()
# C# CS0136: the inner per-hit loop cannot declare 'delta' when Punch()
# later declares 'delta' for the final obstruction raycast.
assert combat.count('Vector3 candidateOffset = nearest - origin;') == 1
assert 'Vector3 delta = nearest - origin;' not in combat
assert 'Vector3 delta = point - origin;' in combat
assert 'float candidateDistance = candidateOffset.sqrMagnitude;' in combat
assert 'distance = candidateDistance;' in combat
assert 'FindPunchReceiver(collider) != null' in combat
assert 'Physics.Raycast(origin, delta.normalized' in combat
assert manifest.get('com.unity.modules.physics')=='1.0.0'
wf=(r/'.github/workflows/aaris-codespaces-control.yml').read_text()
assert "github.event.comment.user.login == 'sundamunda289-art'" in wf
assert "github.event.issue.number == 77" in wf
assert 'secrets.CODESPACE_PAT' in wf
controller=(r/'Tools/Codespaces/controller.sh').read_text()
response=(r/'Tools/Codespaces/issue_response.py').read_text()
assert "'TARGET_UNAVAILABLE'" in response
assert 'target_codespace' in response
remote_source=(r/'Tools/Codespaces/remote.sh').read_text()
assert 'EDITOR_EXITED_DURING_STARTUP' in remote_source
# A persisted Library/Logs symlink must heal its vanished ephemeral
# /tmp cache target. Otherwise Unity 6000 exits 139 on startup.
assert 'cache_target="/tmp/aaris-unity-runtime/project-cache/$name"' in remote_source
assert 'test "$(readlink "$target")" = "$cache_target"' in remote_source
assert 'mkdir -p "$cache_target"' in remote_source
assert 'CACHE_DANGLING_UNKNOWN_TARGET_' in remote_source
assert remote_source.index('mkdir -p "$cache_target"') < remote_source.index('bash "$PROJECT/.devcontainer/start_screen.sh"')
assert "sleep 12" in remote_source
isolate=(r/'Tools/Codespaces/editor_isolation_probe.sh').read_text()
assert 'EDITOR_ISOLATE_TEMP_ONLY' in isolate
assert 'mktemp -d "$BASE/editor-isolation.' in isolate
assert 'rm -rf -- "$WORK"' in isolate
assert '-batchmode -nographics -quit -projectPath "$PROJECT"' in isolate
assert 'EDITOR_ISOLATE_SOFTWARE_GL_CRASH_CANDIDATE' in isolate
assert 'CS EDITOR ISOLATE' in controller
assert 'editor_isolate) REMOTE_TIMEOUT=650' in controller
assert 'editor_isolation_probe.sh' in controller
assert 'editor_isolate' in wf and 'editor_isolate' in response
audit=(r/'Tools/Codespaces/tmp_audit.sh').read_text()
assert 'TMP_AUDIT_COMPLETE' in audit and 'TMP_SCAN_READ_ONLY' in audit
assert 'TMP_PRIVATE_LOG_' in audit and 'TMP_ORIGINAL_LFS_POINTERS_' in audit
assert 'CS TMP AUDIT' in controller and 'tmp_audit) REMOTE_TIMEOUT=240' in controller
assert "'CS FALLBACK TMP AUDIT') OP=tmp_audit ;;" in controller
assert "'CS FALLBACK TMP RECLAIM') OP=tmp_reclaim ;;" in controller
assert "'CS FALLBACK REFRESH TMP') OP=fresh_tmp ;;" in controller
assert "'CS FALLBACK HYDRATE TMP') OP=hydrate_tmp ;;" in controller
assert "'CS FALLBACK TMP RENDER') OP=scene_render ;;" in controller
assert 'test "$ISSUE" != "CS FALLBACK TMP RENDER"' in controller
assert 'bash -s -- render --preverified-tmp' in controller
scene_script=(r/'Tools/Codespaces/scene_probe.sh').read_text()
assert 'AARIS_PROBE_EXACT_CLEAN_TMP_CLONE_SELECTED' in scene_script
assert 'AARIS_PROBE_TMP_793_LFS_SHA256_VERIFIED' in scene_script
assert 'PREVERIFIED_TMP=0' in scene_script
assert 'audit_pointers(project)' in scene_script
hydrator=(r/'Tools/Codespaces/hydrate_tmp_lfs.py').read_text()
assert 'CACHED_LFS_OID_INVALID_OR_MISSING' in hydrator
assert 'TMP_HYDRATE_NONPOINTER_REPLACEMENTS' in hydrator
assert 'TMP_HYDRATE_DUPLICATE_CACHE_OIDS_REMOVED' in hydrator
assert 'TMP_HYDRATE_DUPLICATE_CACHE_BYTES_RECLAIMED' in hydrator
assert 'TMP_CACHE_OBJECT_OUTSIDE_ROOT' in hydrator
assert 'AARIS_TMP_CLEAN_GITHUB_LFS_793_READY' in hydrator
assert 'hydrate_tmp) REMOTE_TIMEOUT=1500' in controller
fresh=(r/'Tools/Codespaces/fresh_tmp_project.sh').read_text()
assert 'AARIS_TMP_GITHUB_PROJECT_READY' in fresh
assert 'restore_preserved_source_lfs.py' in fresh
assert 'TMP_REFRESH_LFS_HASH_VERIFIED' in fresh
assert 'AARIS_TMP_ORIGINAL_WORKSPACE_UNCHANGED' in fresh
assert 'fresh_tmp) REMOTE_TIMEOUT=2400' in controller
reclaim=(r/'Tools/Codespaces/reclaim_native_photo_tmp.sh').read_text()
assert "/tmp/aaris-native-photo-worktrees" in reclaim
assert "git" in reclaim and "worktree" in reclaim and "remove" in reclaim
assert "/workspaces/Unity-Game-Aris" in reclaim
assert "AARIS_TMP_RECLAIM_ORIGINAL_PROJECT_PRESERVED" in reclaim
assert "tmp_reclaim) REMOTE_TIMEOUT=900" in controller
assert 'tmp_audit.sh' in controller and 'tmp_audit' in wf and 'tmp_audit' in response
assert 'editor-exit-code.txt' in remote_source
assert 'AARIS_EDITOR_EXIT_CODE_' in remote_source
assert 'AARIS_EDITOR_DIAG_SIGKILL_OR_OOM' in remote_source
assert 'AARIS_EDITOR_DIAG_EXEC_OR_LIBRARY_FAILURE' in remote_source
assert 'rc=0; "$1" -projectPath' in remote_source
billing=(r/'Tools/Codespaces/billing_quota.py').read_text()
assert "'CS QUOTA') OP=quota" in controller
assert 'billing_quota.py' in controller and 'CODESPACES_BILLING_REPORT=SUCCESS' in controller
assert 'CS STOP' in controller and 'gh codespace stop -c "$CS"' in controller
assert 'quota' in response and 'USED_CORE_HOURS' in response
assert 'settings/billing/usage' in billing and 'CODESPACES_HOURS_STATUS=UNKNOWN' in billing
assert 'subprocess.run' in billing and 'stderr=subprocess.DEVNULL' in billing
assert 'billing_quota.py --selftest' in wf
assert 'CS QUOTA' in (r/'Docs/CODESPACES_FAST_START.md').read_text()
assert 'CS FRESH SSH' in (r/'Docs/CODESPACES_FAST_START.md').read_text()

assert 'actions/upload-artifact@v4' in wf
assert 'id-token: write' in wf
assert 'AARIS_ISSUE_REPLY_TOKEN' in wf
assert 'RSA-OAEP' in (r/'Tools/Codespaces/auth_bridge.py').read_text()
assert (r/'Tools/Codespaces/approval_relay_public.pem').read_text().startswith('-----BEGIN PUBLIC KEY-----')
assert "'CS PROBE')" in (r/'Tools/Codespaces/controller.sh').read_text()
assert 'RELAY_PROBE_HTTP_' in (r/'Tools/Codespaces/relay_probe.py').read_text()
assert 'CS AUTH' in (r/'Tools/Codespaces/controller.sh').read_text()
assert 'UNITY_PRIVATE_APPROVAL_LINK_READY' in (r/'Tools/Codespaces/auth_bridge.py').read_text()
assert 'UNITY_ENCRYPTED_APPROVAL_POSTED' in (r/'Tools/Codespaces/auth_bridge.py').read_text()
assert 'UNITY_REMOTE_DIAGNOSTIC_' in (r/'Tools/Codespaces/auth_bridge.py').read_text()
assert '"bash -s"' in (r/'Tools/Codespaces/auth_bridge.py').read_text()
assert 'AARIS_UNITY_PY_EOF_22941' in (r/'Tools/Codespaces/auth_bridge.py').read_text()
assert 'https://unity.com/install.sh' in (r/'Tools/Codespaces/mobile_license.py').read_text()
assert 'Path.home()/".local/bin/unity"' in (r/'Tools/Codespaces/mobile_license.py').read_text()
assert 'pwd.getpwuid(os.geteuid())' in (r/'Tools/Codespaces/mobile_license.py').read_text()
assert 'except OSError:' in (r/'Tools/Codespaces/mobile_license.py').read_text()
assert 'def find_cli(candidates):' in (r/'Tools/Codespaces/mobile_license.py').read_text()
assert 'UNITY_CLI_INSTALL_CATEGORY_' in (r/'Tools/Codespaces/mobile_license.py').read_text()
assert 'tempfile.TemporaryFile()' in (r/'Tools/Codespaces/mobile_license.py').read_text()
assert 'UNITY_STATE_HOME_TEMPORARY' in (r/'Tools/Codespaces/mobile_license.py').read_text()
remote=(r/'Tools/Codespaces/remote.sh').read_text()
assert 'eval ' not in remote
assert 'merge --ff-only' in remote
assert 'SYNC_WORKTREE_VERIFIED_RAW_IDENTICAL' in remote
# Sync must neither overwrite Unity's live ProjectSettings migration nor silently
# drop it: preserve copies under /workspaces plus a scoped recoverable Git stash.
assert "SYNC_SETTINGS_BACKUP_AND_STASH_SAVED" in remote
assert ".aaris-recovery/project-settings/" in remote
assert "git stash push -m 'Aaris Unity project settings before safe sync'" in remote
assert "ProjectSettings/ProjectVersion.txt" in remote
assert "ProjectSettings/ProjectSettings.asset" in remote
assert "SYNC_BLOCKED_REAL_OR_UNEXPECTED_CHANGE" in remote
assert "SYNC_BLOCKED_CHANGED_BYTES" in remote
assert "git reset --hard" not in remote and "git clean -" not in remote
assert 'scrot -o' in remote
assert 'glibc=%s' in remote
assert 'docker_ready=yes' in remote
assert 'os_version=%s' in remote
assert 'LEGACY_EDITOR=' in remote
assert '\\nLEGACY_EDITOR=' not in remote
assert 'changed_files_count=' in remote
assert 'LFS_FAIL_CATEGORY_' in remote
assert 'lfs_unhydrated_pointer_count=' in remote
assert 'lfs_error_kind=' in remote
assert 'UNITY_CLI_HOME' in (r/'Tools/Codespaces/install_unity.sh').read_text()
assert 'unityhub --headless' not in (r/'Tools/Codespaces/install_unity.sh').read_text()
assert 'lfs.storage' in remote
assert 'aaris-unity-editors' in remote
assert 'project-cache' in remote
assert 'UNITY_CLI_DISCOVERED' in remote
assert 'bash Tools/Codespaces/unity_gui_deps.sh' in (r/'.devcontainer/bootstrap.sh').read_text()
deps=(r/'Tools/Codespaces/unity_gui_deps.sh').read_text()
assert 'UNITY_GUI_DEPS_READY' in deps and 'libglu1-mesa' in deps
assert "CS DEPS" in (r/'Tools/Codespaces/controller.sh').read_text()
assert 'deps) REMOTE_TIMEOUT=' in (r/'Tools/Codespaces/controller.sh').read_text()
assert 'unity_gui_deps.sh' in (r/'Tools/Codespaces/controller.sh').read_text()
assert 'install, bootstrap, deps, cli_diag' in wf
assert "'CS EDITOR DIAG')" in (r/'Tools/Codespaces/controller.sh').read_text()
assert 'AARIS_EDITOR_DIAG_LOG_MISSING' in remote
assert 'editor-launch-stderr.log' in remote
assert 'editor-launch-stdout.log' in remote
assert 'AARIS_EDITOR_DIAG_LICENSING' not in remote or 'LICENSING' in remote
assert 'editor_diag' in (r/'.github/workflows/aaris-codespaces-control.yml').read_text()
terms=(r/'Tools/Codespaces/accept_unity_terms.sh').read_text()
assert "Unity Editor Software Terms" in terms
assert "xdotool search --onlyvisible" in terms
assert terms.count('xdotool mousemove --window "$wid"') == 2
assert "UNITY_TERMS_ACCEPT_AND_CONTINUE_CLICKED_DIALOG_CLOSED" in terms
assert "'CS ACCEPT TERMS') OP=accept_terms" in (r/'Tools/Codespaces/controller.sh').read_text()
assert 'accept_terms' in (r/'.github/workflows/aaris-codespaces-control.yml').read_text()
assert 'accept_terms' in (r/'Tools/Codespaces/issue_response.py').read_text()
assert 'accept_unity_terms.sh' not in (r/'.devcontainer/prebuild.sh').read_text()
assert 'accept_unity_terms.sh' not in (r/'.devcontainer/finalize.sh').read_text()
safe=(r/'Tools/Codespaces/enter_unity_safe_mode.sh').read_text()
assert "Enter Safe Mode?" in safe and "xdotool search --onlyvisible" in safe
assert safe.count('xdotool mousemove --window "$wid"')==1
assert "'CS SAFE MODE') OP=safe_mode" in (r/'Tools/Codespaces/controller.sh').read_text()
assert "'CS COMPILE REPORT') OP=compile_report" in (r/'Tools/Codespaces/controller.sh').read_text()
assert 'enter_unity_safe_mode.sh' not in (r/'.devcontainer/prebuild.sh').read_text()
assert 'safe_mode, compile_report' in wf
assert "CS REFRESH" in (r/'Tools/Codespaces/controller.sh').read_text()
assert 'refresh_unity.sh' in (r/'Tools/Codespaces/controller.sh').read_text()
assert 'refresh' in (r/'.github/workflows/aaris-codespaces-control.yml').read_text()
refresh=(r/'Tools/Codespaces/refresh_unity.sh').read_text()
assert 'UNITY_ASSETS_REFRESH_REQUESTED' in refresh
assert 'UNITY_VEHICLE_RECOMPILE_MTIME_REQUESTED' in refresh
close=(r/'Tools/Codespaces/close_unity_editor.sh').read_text()
assert "xdotool windowclose" in close
assert "UNITY_EDITOR_GRACEFUL_EXIT_CONFIRMED" in close
assert "SIGKILL" not in close or "do not send SIGKILL" in close
assert "'CS CLOSE EDITOR') OP=close_editor" in (r/'Tools/Codespaces/controller.sh').read_text()
assert 'close_editor' in (r/'.github/workflows/aaris-codespaces-control.yml').read_text()
assert 'close_editor' in (r/'Tools/Codespaces/issue_response.py').read_text()
recovery=(r/'Tools/Codespaces/editor_process_recovery.sh').read_text()
assert 'UNITY_EDITOR_PROCESS_DIAG_READY' in recovery
assert 'UNITY_EDITOR_RECOVER_TERM_CONFIRMED' in recovery
assert 'UNITY_EDITOR_RECOVER_BLOCKED_VISIBLE_WINDOW' in recovery
assert 'signal.SIGTERM' in recovery
assert 'signal.SIGKILL' not in recovery
assert 'cmd[0].endswith(b"/Editor/Unity")' in recovery
assert "'CS EDITOR PROCESS') OP=editor_process" in (r/'Tools/Codespaces/controller.sh').read_text()
assert "'CS EDITOR RECOVER') OP=editor_recover" in (r/'Tools/Codespaces/controller.sh').read_text()
assert 'editor_process, editor_recover' in wf
assert 'editor_process editor_recover' in (r/'Tools/Codespaces/issue_response.py').read_text()
assert 'close_unity_editor.sh' not in (r/'.devcontainer/prebuild.sh').read_text()
assert "touch -c \"$MOTOR\" \"$MANIFEST\"" in refresh
assert "com.unity.modules.vehicles" in refresh
assert "UNITY_REFRESH_VEHICLES_MODULE_NOT_CONFIGURED" in refresh
assert "ctrl+r" in refresh
assert 'Unity-Game-Aris' in refresh
assert 'refresh_unity.sh' not in (r/'.devcontainer/prebuild.sh').read_text()

assert 'UNITY_COMPILER_ERROR_COUNT_' in (r/'Tools/Codespaces/compiler_report.sh').read_text()
assert 'safe_mode compile_report' in (r/'Tools/Codespaces/issue_response.py').read_text()

assert '$AUTH_HOME/.local/share/aaris-unity-cli/bin/unity' in remote
assert 'Codespaces' in (r/'Docs/CODESPACES_FAST_START.md').read_text()
assert 'EDITOR_ROOT="$HOME/.local/share/aaris-unity-editors"' in (r/'Tools/Codespaces/install_unity.sh').read_text()
assert '--download-directory "$EDITOR_ROOT"' in (r/'Tools/Codespaces/install_unity.sh').read_text()
assert ' --resume --non-interactive' not in (r/'Tools/Codespaces/install_unity.sh').read_text()
assert ' --non-interactive --yes --accept-eula --format json' in (r/'Tools/Codespaces/install_unity.sh').read_text()
installer=(r/'Tools/Codespaces/install_unity.sh').read_text()
assert '--non-interactive --yes --accept-eula --format json' in installer
assert 'UNITY_EDITOR_INSTALL_EXIT_CODE_' in installer
assert 'UNITY_EDITOR_INSTALL_CATEGORY_' in installer
assert "'CS CLI DIAG')" in (r/'Tools/Codespaces/controller.sh').read_text()
assert 'cli_diag' in (r/'.github/workflows/aaris-codespaces-control.yml').read_text()
assert 'UNITY_CLI_INSTALL_FLAGS_' in remote
assert 'UNITY_CLI_INSTALL_LOG_CODE_' in remote
report=(r/'Tools/Codespaces/issue_response.py').read_text()
assert "op == 'cli_diag'" in report
assert 'UNITY_CLI_INSTALL_LOG_REJECTED_' in report
assert all(f' {operation} ' in report for operation in (
    'cli_diag', 'editor', 'editor_diag', 'editor_isolate', 'tmp_audit', 'license'))
assert 'machine-id' not in remote
assert 'Unity_auth_vault' not in remote
controller=(r/'Tools/Codespaces/controller.sh').read_text()
assert "'CS MACHINES')" in controller
assert "'CS REBUILD')" in controller
assert "'CS RECOVERY SYNC') OP=recovery_sync" in controller
assert 'recovery_sync' in wf and 'recovery_sync' in response
assert 'recovery_sync_not_approved_for_target' in controller
recover=(r/'Tools/Codespaces/recovery_sync.sh').read_text()
assert 'git merge --ff-only FETCH_HEAD' in recover
assert 'GIT_LFS_SKIP_SMUDGE=1' in recover
assert 'git diff --quiet -- .devcontainer' in recover
assert "stash push -u -m 'Aaris new Codespace devcontainer recovery 20261109' -- .devcontainer" in recover
assert 'tar -czf "$BACKUP/devcontainer.tar.gz"' in recover
assert 'sha256sum -c checksum.sha256' in recover
assert 'git restore --source=HEAD --staged --worktree -- .devcontainer' in recover
assert 'AARIS_RECOVERY_PRIVATE_CONFIG_BACKUP_SAVED' in recover
assert 'AARIS_RECOVERY_CONFIG_STASH_SAVED' in recover
assert 'git reset --hard' not in recover and 'git clean -' not in recover
assert "python3 -" not in (r/'Tools/Codespaces/rebuild_preflight.sh').read_text()
assert "'CS FULL REBUILD') OP=full_rebuild" in controller
assert 'gh codespace rebuild -c "$CS" "${REBUILD_FLAGS[@]}"' in controller
assert 'full_rebuild' in wf and 'full_rebuild' in response
for lifecycle in ('bootstrap.sh','prebuild.sh','finalize.sh'):
    assert 'DEFERRED' in (r/'.devcontainer'/lifecycle).read_text()
assert "'CS FRESH')" in controller
assert "'CS FRESH SSH')" in controller
assert "'CS FRESH MINIMAL') OP=fresh_minimal" in controller
assert "'CS BOOTSTRAP') OP=bootstrap" in controller
assert 'aaris-ai-ubuntu-minimal' in controller
assert 'minimal-ubuntu/devcontainer.json' in controller
assert 'bootstrap' in wf and 'bootstrap' in response
assert 'fresh_minimal' in wf and 'fresh_minimal' in response
assert 'bash "$PROJECT/.devcontainer/bootstrap.sh"' in remote_source
assert "aaris-ai-unity-ssh" in controller
assert '--devcontainer-path .devcontainer/devcontainer.json' in controller
assert '--idle-timeout 240m' in controller
assert 'fresh_codespace_created_preserves_old' in controller
assert "'CS LOGS')" in controller
assert 'codespace_logs.py' in controller
assert (r/'Tools/Codespaces/codespace_logs.py').read_text().startswith('#!/usr/bin/env python3')
assert 'gh codespace rebuild -c "$CS"' in controller
assert 'TARGET_CODESPACE="aaris-ai-unity-ssh-vp5g97jv4r4gfpjgq"' in controller
assert 'COUNT="$(jq' in controller and 'if test "$COUNT" -eq 1' in controller
assert 'selected_account_codespace_missing_or_ambiguous' in controller
assert 'if test -n "$MACHINE"; then' in controller
assert 'never_create_or_select_another_codespace_implicitly' not in controller
assert 'existing_target_not_unambiguously_selected' in controller
assert '[.[] | select(.name == $target)][0].name' in controller
assert 'rebuild_preflight.sh' in controller
assert 'bash -s -- $OP' in controller
assert "'CS INFO')" in controller
assert 'gh codespace view -c' in controller
assert 'safe_error_summary' in controller
assert 'trap \'finish; rm -f' in controller
termux=(r/'Tools/Codespaces/termux_switch.sh').read_text()
assert 'gh secret set CODESPACE_PAT' in termux
assert 'printf ' in termux and '"$CS_TOKEN"' in termux
print('CODESPACE_PROTOCOL_SELFTEST_PASS')


# Smoke-test the real controller with a mocked GitHub CLI; no external token or paid VM.
import os
import subprocess
import tempfile
with tempfile.TemporaryDirectory() as tmp:
    working=Path(tmp)
    bindir=working/'bin'
    bindir.mkdir()
    mock=bindir/'gh'
    mock.write_text("""#!/usr/bin/env bash
set -e
case "$1" in
  api)
    if [[ "$2" == */machines ]]; then
      if [[ "$3" == "--jq" ]]; then printf 'standardLinux32gb\\t4\\t16\\n'; else
        printf '{"machines":[{"name":"standardLinux32gb","cpus":4,"memory_in_bytes":17179869184}]}\\n'; fi
    elif [[ "$2" == "-X" && "$3" == "POST" && "$4" == "/user/codespaces/aaris-ai-unity-ssh-vp5g97jv4r4gfpjgq/start" ]]; then
      echo '{}'
    else exit 9; fi ;;
  codespace)
    case "$2" in
      list)
        if [[ "${AARIS_MOCK_NO_CS:-0}" == 1 ]]; then echo '[]'; else
          echo '[{"name":"unrelated-workspace","displayName":"aaris-ai-unity-ssh"},{"name":"aaris-ai-unity-ssh-vp5g97jv4r4gfpjgq","displayName":"aaris-ai-unity-ssh"}]'
        fi ;;
      create) exit 99 ;;
      *) exit 9 ;;
    esac ;;
  *) exit 9 ;;
esac
""")
    mock.chmod(0o700)
    env={**os.environ,
         'PATH':str(bindir)+os.pathsep+os.environ['PATH'],
         'GITHUB_WORKSPACE':str(working),
         'GITHUB_REPOSITORY':'sundamunda289-art/Unity-Game-Aris',
         'GH_TOKEN':'mock-not-real',
         'ISSUE_COMMAND':'',
         'REQUEST_MACHINE':'',
         'REQUEST_OPERATION':'machines'}
    controller=str(r/'Tools/Codespaces/controller.sh')
    run=subprocess.run(['bash',controller],env=env,capture_output=True,text=True)
    assert run.returncode==0,run.stderr
    result=json.loads((working/'codespace-results/result.json').read_text())
    assert result['status']=='SUCCESS' and result['operation']=='machines'
    text=(working/'codespace-results/terminal.txt').read_text()
    assert 'standardLinux32gb' in text
    env['REQUEST_OPERATION']='start'
    run=subprocess.run(['bash',controller],env=env,capture_output=True,text=True)
    assert run.returncode==0,run.stderr
    result=json.loads((working/'codespace-results/result.json').read_text())
    assert result['status']=='SUCCESS' and result['codespace']=='aaris-ai-unity-ssh-vp5g97jv4r4gfpjgq'
    assert 'exact' not in result['detail']
    # Missing target fails closed: no auto-create and no switching to an
    # unrelated workspace that happens to share the display name.
    env['AARIS_MOCK_NO_CS']='1'
    run=subprocess.run(['bash',controller],env=env,capture_output=True,text=True)
    assert run.returncode==0,run.stderr
    result=json.loads((working/'codespace-results/result.json').read_text())
    assert result['status']=='TARGET_UNAVAILABLE'
    assert result['codespace']==''
direct=(r/'Tools/Codespaces/termux_unity_allow.sh').read_text()
assert 'termux-open-url' in direct
assert 'GH_CONFIG_DIR' in direct
assert 'mobile_license.py' in direct
assert 'gh codespace ssh' in direct
assert 'workers.dev' not in direct
assert 'APPROVALS' not in direct
# Regression: an explicitly named Codespace must always beat legacy/fallback
# names, independent of list order; missing or duplicate names fail closed.
exact_line=next(line for line in direct.splitlines()
                if 'jq -r --arg name "$AARIS_CODESPACE_NAME"' in line)
exact_selector=exact_line.rsplit(chr(39),2)[1]
old={"displayName":"aaris-ai-ubuntu-fallback-2","name":"old-fallback"}
new={"displayName":"aaris-ai-ubuntu-minimal","name":"new-clean-ubuntu"}
for candidates, requested, expected in (
    ([old,new],"new-clean-ubuntu","new-clean-ubuntu"),
    ([new,old],"new-clean-ubuntu","new-clean-ubuntu"),
    ([old,new],"old-fallback","old-fallback"),
    ([old],"new-clean-ubuntu",""),
    ([new,new],"new-clean-ubuntu",""),
):
    selected=subprocess.run(["jq","-r","--arg","name",requested,exact_selector],
                            input=json.dumps(candidates),
                            capture_output=True,text=True,check=True).stdout.strip()
    assert selected==expected, (selected,expected)
# The convenience default is allowed only for exactly one eligible Ubuntu
# instance; if both old and new exist the user must select by exact name.
default_line=next(line for line in direct.splitlines()
                  if 'jq -r ' in line and 'select(.displayName' in line)
default_selector=default_line.rsplit(chr(39),2)[1]
for candidates, expected in (([old],"old-fallback"),([new],"new-clean-ubuntu"),
                             ([old,new],""),([], "")):
    selected=subprocess.run(["jq","-r",default_selector],input=json.dumps(candidates),
                            capture_output=True,text=True,check=True).stdout.strip()
    assert selected==expected, (selected,expected)
print('CODESPACE_CONTROLLER_MOCK_PASS')
