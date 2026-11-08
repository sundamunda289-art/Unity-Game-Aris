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
assert 'distance = candidateOffset.sqrMagnitude;' in combat
assert 'Physics.Raycast(origin, delta.normalized' in combat
assert manifest.get('com.unity.modules.physics')=='1.0.0'
wf=(r/'.github/workflows/aaris-codespaces-control.yml').read_text()
assert "github.event.comment.user.login == 'sundamunda289-art'" in wf
assert "github.event.issue.number == 77" in wf
assert 'secrets.CODESPACE_PAT' in wf
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
assert 'install, deps, cli_diag' in wf
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
assert 'UNITY_EDITOR_CPU_ACTIVITY_' in recovery
assert 'UNITY_EDITOR_LOG_AGE_' in recovery
assert 'UNITY_EDITOR_LOG_FLAG_' in recovery
assert 'log.read_bytes()[-250000:]' in recovery
assert 'time.sleep(2)' in recovery
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
assert 'cli_diag editor editor_diag license' in report
assert 'machine-id' not in remote
assert 'Unity_auth_vault' not in remote
controller=(r/'Tools/Codespaces/controller.sh').read_text()
assert "'CS MACHINES')" in controller
assert "'CS REBUILD')" in controller
assert "'CS FRESH')" in controller
assert "'CS FRESH SSH')" in controller
assert "aaris-ai-unity-ssh" in controller
assert '--devcontainer-path .devcontainer/devcontainer.json' in controller
assert '--idle-timeout 240m' in controller
assert 'fresh_codespace_created_preserves_old' in controller
assert "'CS LOGS')" in controller
assert 'codespace_logs.py' in controller
assert (r/'Tools/Codespaces/codespace_logs.py').read_text().startswith('#!/usr/bin/env python3')
assert 'gh codespace rebuild -c "$CS"' in controller
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
    else exit 9; fi ;;
  codespace)
    case "$2" in
      list) echo '[]' ;;
      create) echo 'mock-codespace' ;;
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
    assert result['status']=='SUCCESS' and result['codespace']=='mock-codespace'
direct=(r/'Tools/Codespaces/termux_unity_allow.sh').read_text()
assert 'termux-open-url' in direct
assert 'GH_CONFIG_DIR' in direct
assert 'mobile_license.py' in direct
assert 'gh codespace ssh' in direct
assert 'workers.dev' not in direct
assert 'APPROVALS' not in direct
# Regression: phone Unity Allow must target the SSH-ready Codespace, not an old
# 'Available' machine with a nonfunctional sshd, regardless of list order.
selection_line=next(line for line in direct.splitlines() if line.startswith('CS="$(printf'))
selector=selection_line.split("jq -r '",1)[1].split("'",1)[0]
old={"displayName":"aaris-ai-unity","name":"old-unreachable"}
ssh={"displayName":"aaris-ai-unity-ssh","name":"new-ssh-ready"}
for candidates, expected in (([old,ssh],"new-ssh-ready"),
                             ([ssh,old],"new-ssh-ready"),
                             ([old],"old-unreachable")):
    selected=subprocess.run(["jq","-r",selector],input=json.dumps(candidates),
                            capture_output=True,text=True,check=True).stdout.strip()
    assert selected==expected, (selected,expected)
print('CODESPACE_CONTROLLER_MOCK_PASS')
