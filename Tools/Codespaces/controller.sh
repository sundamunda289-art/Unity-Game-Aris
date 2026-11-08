#!/usr/bin/env bash
# GitHub Actions entrypoint. Never interpret arbitrary text as terminal commands.
set -euo pipefail
OUT="$GITHUB_WORKSPACE/codespace-results"
mkdir -p "$OUT"
REPO="$GITHUB_REPOSITORY"
OP="$REQUEST_OPERATION"
MACHINE="$REQUEST_MACHINE"
ISSUE="$ISSUE_COMMAND"
PHOTO_VALUES=""
STATE="FAILED"
DETAIL="not_started"
CS=""
: > "$OUT/terminal.txt"
finish() {
  local rc="$?"
  python3 - "$OUT/result.json" "$OP" "$STATE" "$DETAIL" "$CS" "$rc" <<'PY'
import json,sys
p,op,state,detail,cs,rc=sys.argv[1:]
with open(p,'w') as f:
    json.dump(dict(operation=op,status=state,detail=detail,codespace=cs,exit_code=int(rc)),f,indent=2)
PY
}
trap finish EXIT
if test -n "$ISSUE"; then
  case "$ISSUE" in
    'CS MACHINES') OP=machines ;;
    'CS START') OP=start ;;
    'CS FRESH') OP=fresh ;;
    'CS FRESH SSH') OP=fresh_ssh ;;
    'CS REBUILD') OP=rebuild ;;
    'CS STATUS') OP=status ;;
    'CS LOGS') OP=logs ;;
    'CS INFO') OP=info ;;
    'CS QUOTA') OP=quota ;;
    'CS SYNC') OP=sync ;;
    'CS LFS') OP=lfs ;;
    'CS LFS REPORT') OP=lfs_report ;;
    'CS CHANGES') OP=changes ;;
    'CS INSTALL') OP=install ;;
    'CS DEPS') OP=deps ;;
    'CS CLI DIAG') OP=cli_diag ;;
    'CS EDITOR') OP=editor ;;
    'CS EDITOR DIAG') OP=editor_diag ;;
    'CS LICENSE') OP=license ;;
    'CS AUTH') OP=auth ;;
    'CS PROBE') OP=probe ;;
    'CS SHOT') OP=shot ;;
    'CS SCENE RENDER') OP=scene_render ;;
    'CS SCENE PLAY') OP=scene_play ;;
    'CS DROP VIDEO') OP=drop_video ;;
    'CS DROP FRAMES') OP=drop_frames ;;
    'CS PLAY DIAG') OP=play_diag ;;
    'CS FOLIAGE DIAG') OP=foliage_diag ;;
    'CS PHOTO '*) OP=photo ;;
    'CS ACCEPT TERMS') OP=accept_terms ;;
    'CS SAFE MODE') OP=safe_mode ;;
    'CS COMPILE REPORT') OP=compile_report ;;
    'CS REFRESH') OP=refresh ;;
    'CS CLOSE EDITOR') OP=close_editor ;;
    'CS EDITOR PROCESS') OP=editor_process ;;
    'CS EDITOR RECOVER') OP=editor_recover ;;
    'CS TEST') OP=test ;;
    'CS STOP') OP=stop ;;
    *) DETAIL="unsupported_issue_command"; exit 2 ;;
  esac
fi
case "$OP" in
  machines|info|quota|start|fresh|fresh_ssh|rebuild|status|logs|sync|lfs|lfs_report|changes|install|deps|cli_diag|editor|editor_diag|license|auth|probe|shot|accept_terms|safe_mode|compile_report|refresh|close_editor|editor_process|editor_recover|scene_render|scene_play|drop_video|drop_frames|photo|play_diag|foliage_diag|test|stop) ;;
  *) DETAIL="unsupported_operation"; exit 2 ;;
esac
if test "$OP" = photo; then
  PHOTO_VALUES="$(python3 "$GITHUB_WORKSPACE/Tools/Codespaces/photo_request.py" "$ISSUE" 2>/dev/null)" || {
    DETAIL="invalid_bounded_photo_request"; exit 34
  }
  [[ "$PHOTO_VALUES" =~ ^[-0-9.[:space:]]{12,96}$ ]] || {
    DETAIL="photo_arguments_not_numeric"; exit 35
  }
fi
if test -z "$GH_TOKEN"; then DETAIL="CODESPACE_PAT_missing"; exit 3; fi
if test -n "$MACHINE" && [[ ! "$MACHINE" =~ ^[A-Za-z0-9-]+$ ]]; then DETAIL="bad_machine"; exit 4; fi
if test "$OP" = probe; then
  if python3 "$GITHUB_WORKSPACE/Tools/Codespaces/relay_probe.py" >"$OUT/terminal.txt" 2>/dev/null; then
    STATE="SUCCESS"; DETAIL="oidc_relay_preflight_ok"; exit 0
  fi
  DETAIL="oidc_relay_preflight_failed"; exit 19
fi
if test "$OP" = quota; then
  # Read authenticated GitHub billing on the Actions runner, without waking or
  # remotely SSHing into the selected Codespace. Never log raw billing JSON.
  python3 "$GITHUB_WORKSPACE/Tools/Codespaces/billing_quota.py" > "$OUT/terminal.txt" 2>/dev/null || {
    DETAIL="codespace_quota_script_failed"; exit 37;
  }
  if grep -qx 'CODESPACES_BILLING_REPORT=SUCCESS' "$OUT/terminal.txt"; then
    STATE="SUCCESS"; DETAIL="codespace_quota_account_billing_read"; exit 0
  fi
  STATE="UNKNOWN"; DETAIL="codespace_billing_account_permission_missing"; exit 0
fi
if test "$OP" = machines; then
  if gh api "/repos/$REPO/codespaces/machines" --jq '.machines[] | [.name, (.cpus|tostring), ((.memory_in_bytes/1073741824|floor)|tostring)] | @tsv' > "$OUT/terminal.txt" 2>/dev/null; then
    STATE="SUCCESS"; DETAIL="machine_types_CPU_RAM_GiB"; exit 0
  fi
  DETAIL="machine_API_unavailable"; exit 5
fi
LIST="$(gh codespace list -R "$REPO" --json name,displayName,machineName,state --limit 30 2>/dev/null)" || {
  DETAIL="codespace_list_failed_token_scope"; exit 6
}
# A new Codespace is safer than another rebuild of an inaccessible container.
# Never delete or reset the old Codespace or its /workspaces and Git LFS assets.
if test "$OP" = fresh; then
  EXISTING="$(jq -r '[.[] | select(.displayName=="aaris-ai-unity")][0].name // ""' <<< "$LIST")"
  if test -n "$EXISTING"; then
    printf "codespace=%s\n" "$EXISTING" > "$OUT/terminal.txt"
    STATE="SUCCESS"; DETAIL="existing_fresh_codespace_selected"; exit 0
  fi
  if ! jq -e '.image == "mcr.microsoft.com/devcontainers/base:ubuntu-22.04" and .remoteUser == "vscode"' "$GITHUB_WORKSPACE/.devcontainer/devcontainer.json" >/dev/null; then
    DETAIL="ubuntu_devcontainer_configuration_missing"; exit 26
  fi
  if ! gh api "/repos/$REPO/codespaces/machines" --jq '.machines[].name' 2>/dev/null | grep -qxF "standardLinux32gb"; then
    DETAIL="fresh_standard_4core_16gb_unavailable"; exit 27
  fi
  if ! NEW="$(timeout 1200 gh codespace create -R "$REPO" -b main -m standardLinux32gb --devcontainer-path .devcontainer/devcontainer.json --idle-timeout 240m --display-name aaris-ai-unity --default-permissions 2>/dev/null)"; then
    DETAIL="fresh_codespace_creation_failed_check_quota"; exit 28
  fi
  NEW="$(printf "%s\n" "$NEW" | tail -n 1 | tr -d "[:space:]")"
  if [[ ! "$NEW" =~ ^[A-Za-z0-9-]+$ ]]; then
    DETAIL="fresh_creation_response_unverified"; exit 29
  fi
  printf "codespace=%s\nmachine=standardLinux32gb\n" "$NEW" > "$OUT/terminal.txt"
  echo "AARIS_FRESH_UBUNTU_DEVCONTAINER_REQUESTED" >> "$OUT/terminal.txt"
  STATE="SUCCESS"; DETAIL="fresh_codespace_created_preserves_old"; exit 0
fi
# Fresh SSH-equipped Ubuntu machine; leave every older workspace untouched.
if test "$OP" = fresh_ssh; then
  EXISTING="$(jq -r '[.[] | select(.displayName=="aaris-ai-unity-ssh")][0].name // ""' <<< "$LIST")"
  if test -n "$EXISTING"; then
    printf "codespace=%s\n" "$EXISTING" > "$OUT/terminal.txt"
    STATE="SUCCESS"; DETAIL="existing_ssh_codespace_selected"; exit 0
  fi
  if ! jq -e '.image == "mcr.microsoft.com/devcontainers/base:ubuntu-22.04" and .features["ghcr.io/devcontainers/features/sshd:1"] != null' "$GITHUB_WORKSPACE/.devcontainer/devcontainer.json" >/dev/null; then
    DETAIL="ssh_feature_missing_from_ubuntu_config"; exit 30
  fi
  if ! gh api "/repos/$REPO/codespaces/machines" --jq '.machines[].name' 2>/dev/null | grep -qxF "standardLinux32gb"; then
    DETAIL="ssh_codespace_4core_16gb_unavailable"; exit 31
  fi
  if ! NEW="$(timeout 1200 gh codespace create -R "$REPO" -b main -m standardLinux32gb --devcontainer-path .devcontainer/devcontainer.json --idle-timeout 240m --display-name aaris-ai-unity-ssh --default-permissions 2>/dev/null)"; then
    DETAIL="ssh_codespace_create_failed_quota_or_permissions"; exit 32
  fi
  NEW="$(printf "%s\n" "$NEW" | tail -n 1 | tr -d "[:space:]")"
  if [[ ! "$NEW" =~ ^[A-Za-z0-9-]+$ ]]; then
    DETAIL="ssh_codespace_response_unverified"; exit 33
  fi
  printf "codespace=%s\nmachine=standardLinux32gb\n" "$NEW" > "$OUT/terminal.txt"
  echo "AARIS_UBUNTU_SSH_FEATURE_REQUESTED" >> "$OUT/terminal.txt"
  # Stop only the previous fresh, known-broken instance after new creation.
  OLD="$(jq -r '[.[] | select(.displayName=="aaris-ai-unity")][0].name // ""' <<< "$LIST")"
  if test -n "$OLD" && test "$OLD" != "$NEW"; then
    gh codespace stop -c "$OLD" >/dev/null 2>/dev/null || true
  fi
  STATE="SUCCESS"; DETAIL="ssh_codespace_created_preserving_previous_workspaces"; exit 0
fi
CS="$(jq -r '[.[] | select(.displayName=="aaris-ai-unity-ssh")][0].name // [.[] | select(.displayName=="aaris-ai-unity")][0].name // .[0].name // ""' <<< "$LIST")"
if test -z "$CS" && test "$OP" = start; then
  if test -z "$MACHINE"; then
    TYPES="$(gh api "/repos/$REPO/codespaces/machines" 2>/dev/null)" || { DETAIL="machine_API_unavailable"; exit 5; }
    MACHINE="$(jq -r '[.machines[] | select(.cpus>=4 and .memory_in_bytes>=16000000000)] | sort_by(.cpus) | .[0].name // ""' <<< "$TYPES")"
  fi
  test -n "$MACHINE" || { DETAIL="no_available_4core_16gb_machine"; exit 7; }
  CS="$(gh codespace create -R "$REPO" -b main -m "$MACHINE" --idle-timeout 240m --display-name aaris-ai-unity --default-permissions 2>/dev/null)" || {
    DETAIL="codespace_create_failed_quota_or_permissions"; exit 8
  }
  CS="$(printf '%s' "$CS" | tail -n 1 | tr -d '[:space:]')"
  [[ "$CS" =~ ^[A-Za-z0-9-]+$ ]] || { DETAIL="bad_codespace_response"; exit 9; }
  printf 'codespace=%s\nmachine=%s\n' "$CS" "$MACHINE" > "$OUT/terminal.txt"
  STATE="SUCCESS"; DETAIL="codespace_created"; exit 0
fi
if test -z "$CS"; then STATE="NO_CODESPACE"; DETAIL="run_CS_START"; exit 0; fi
[[ "$CS" =~ ^[A-Za-z0-9-]+$ ]] || { DETAIL="bad_codespace_name"; exit 9; }
# Read Codespace machine and state without SSH. Safe when the old container is unavailable.
if test "$OP" = logs; then
  if timeout 110 python3 "$GITHUB_WORKSPACE/Tools/Codespaces/codespace_logs.py" "$CS" > "$OUT/terminal.txt" 2>/dev/null; then
    STATE="SUCCESS"; DETAIL="codespace_logs_classified_without_raw_output"; exit 0
  fi
  DETAIL="codespace_logs_command_failed"; exit 25
fi
if test "$OP" = info; then
  if gh codespace view -c "$CS" --json name,machineName,state,idleTimeoutMinutes,devcontainerPath \
      --jq '[("codespace=" + .name), ("machine=" + .machineName), ("state=" + .state), ("idle_minutes=" + (.idleTimeoutMinutes|tostring)), ("devcontainer=" + (.devcontainerPath // "default"))] | .[]' > "$OUT/terminal.txt" 2>/dev/null; then
    STATE="SUCCESS"; DETAIL="codespace_machine_and_state"; exit 0
  fi
  DETAIL="codespace_view_failed"; exit 14
fi
if test "$OP" = stop; then
  gh codespace stop -c "$CS" >/dev/null 2>&1 || { DETAIL="codespace_stop_failed"; exit 10; }
  STATE="SUCCESS"; DETAIL="codespace_stopped"; exit 0
fi
if test "$OP" = start; then
  # Existing Codespaces are started through GitHub's supported API; files are retained.
  gh api -X POST "/user/codespaces/$CS/start" >/dev/null 2>&1 || {
    # It may already be running; the next STATUS will verify.
    echo 'START_API_NOT_CONFIRMED_CHECK_STATUS' > "$OUT/terminal.txt"
    STATE="UNKNOWN"; DETAIL="check_status"; exit 0
  }
  echo "codespace=$CS" > "$OUT/terminal.txt"
  STATE="SUCCESS"; DETAIL="existing_codespace_start_requested"; exit 0
fi
# GitHub Codespaces preserves /workspaces on devcontainer rebuild.
# Only the repository owner's exact CS REBUILD operation reaches this block.
if test "$OP" = rebuild; then
  if ! jq -e '.image == "mcr.microsoft.com/devcontainers/base:ubuntu-22.04" and .remoteUser == "vscode"' "$GITHUB_WORKSPACE/.devcontainer/devcontainer.json" >/dev/null; then
    DETAIL="ubuntu_devcontainer_configuration_missing"; exit 21
  fi
  if ! timeout 120 gh codespace ssh -c "$CS" -- "bash -s" < "$GITHUB_WORKSPACE/Tools/Codespaces/rebuild_preflight.sh" > "$OUT/terminal.txt" 2>/dev/null; then
    DETAIL="rebuild_preflight_workspace_unverified"; exit 22
  fi
  if ! grep -qxF "AARIS_REBUILD_PREFLIGHT_READY" <(tail -n 1 "$OUT/terminal.txt"); then
    DETAIL="rebuild_preflight_not_ready"; exit 23
  fi
  # Non-full rebuild; do not reset Git or overwrite /workspaces/LFS.
  if timeout 2400 gh codespace rebuild -c "$CS" >/dev/null 2>/dev/null; then
    echo "AARIS_UBUNTU_REBUILD_REQUEST_COMPLETED" >> "$OUT/terminal.txt"
    STATE="SUCCESS"; DETAIL="rebuild_command_completed_verify_status"; exit 0
  fi
  echo "AARIS_UBUNTU_REBUILD_COMMAND_FAILED" >> "$OUT/terminal.txt"
  DETAIL="codespace_rebuild_failed_or_timed_out"; exit 24
fi
# Stream the approved checked-in script over SSH stdin. No SCP or old remote checkout needed.
PRIVATE_ERRORS="$RUNNER_TEMP/aaris-codespace-ssh-errors.log"
trap 'finish; rm -f "$PRIVATE_ERRORS"' EXIT

safe_error_summary() {
  python3 - "$PRIVATE_ERRORS" "$OUT/ssh-diagnostic.txt" <<'PY'
from pathlib import Path
import re,sys
s=Path(sys.argv[1]).read_text(errors="replace") if Path(sys.argv[1]).exists() else ""
if not s.strip():
    reason="no_stderr_from_gh_cli"
elif re.search(r"permission denied|not authorized|forbidden|unauthorized|HTTP 403",s,re.I):
    reason="permission_or_token_denied"
elif re.search(r"connection refused|connect.*failed|connect.*error|unable to connect",s,re.I):
    reason="ssh_connection_failed"
elif re.search(r"no such file|not found|missing",s,re.I):
    reason="missing_binary_or_file"
elif re.search(r"unknown flag|unknown command|invalid argument|usage:",s,re.I):
    reason="gh_cli_invocation_invalid"
elif re.search(r"timed out|timeout|deadline exceeded",s,re.I):
    reason="ssh_timeout"
elif re.search(r"could not resolve|lookup|DNS",s,re.I):
    reason="dns_or_network"
elif re.search(r"public key|ssh key|agent",s,re.I):
    reason="ssh_key_failure"
else:
    reason="unclassified_gh_cli_error"
# Preserve diagnostic shape without logging credentials or arbitrary remote stderr.
Path(sys.argv[2]).write_text("diagnostic_category="+reason+"\nerror_bytes="+str(len(s.encode()))+"\n")
PY
}

REMOTE_TIMEOUT=130
case "$OP" in
  install) REMOTE_TIMEOUT=3000 ;;
  deps) REMOTE_TIMEOUT=900 ;;
  sync|lfs) REMOTE_TIMEOUT=1200 ;;
  test) REMOTE_TIMEOUT=500 ;;
  editor) REMOTE_TIMEOUT=350 ;;
  scene_render|scene_play|photo|foliage_diag) REMOTE_TIMEOUT=1800 ;;
  drop_video) REMOTE_TIMEOUT=2100 ;;
  drop_frames) REMOTE_TIMEOUT=420 ;;
esac
if test "$OP" = auth; then
  # Intercept the remote Unity OAuth URL, keep it OFF all public logs/artifacts.
  if timeout 825 python3 "$GITHUB_WORKSPACE/Tools/Codespaces/auth_bridge.py" "$CS" > "$OUT/terminal.txt" 2>"$PRIVATE_ERRORS"; then
    STATE="SUCCESS"; DETAIL="unity_license_checked"; exit 0
  fi
  safe_error_summary
  DETAIL="unity_auth_incomplete_see_private_link"
  exit 17
fi
SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/remote.sh"
REMOTE_SHELL="bash -s -- $OP"
if test "$OP" = drop_video; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/offline_drop_probe.sh"
  REMOTE_SHELL="bash -s"
fi
if test "$OP" = drop_frames; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/offline_drop_collect.sh"
  REMOTE_SHELL="bash -s"
fi
if test "$OP" = deps; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/unity_gui_deps.sh"
  REMOTE_SHELL="bash -s"
fi
if test "$OP" = accept_terms; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/accept_unity_terms.sh"
  REMOTE_SHELL="bash -s"
fi
if test "$OP" = safe_mode; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/enter_unity_safe_mode.sh"
  REMOTE_SHELL="bash -s"
fi
if test "$OP" = compile_report; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/compiler_report.sh"
  REMOTE_SHELL="bash -s"
fi
if test "$OP" = refresh; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/refresh_unity.sh"
  REMOTE_SHELL="bash -s"
fi
if test "$OP" = close_editor; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/close_unity_editor.sh"
  REMOTE_SHELL="bash -s"
fi
if test "$OP" = editor_process || test "$OP" = editor_recover; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/editor_process_recovery.sh"
  if test "$OP" = editor_process; then
    REMOTE_SHELL="bash -s -- diag"
  else
    REMOTE_SHELL="bash -s -- recover"
  fi
fi
if test "$OP" = install; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/install_unity.sh"
  REMOTE_SHELL="bash -s"
fi
if test "$OP" = play_diag; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/play_probe_diagnostic.sh"
  REMOTE_SHELL="bash -s"
fi
if test "$OP" = scene_render || test "$OP" = scene_play || test "$OP" = photo || test "$OP" = foliage_diag; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/scene_probe.sh"
  if test "$OP" = scene_render; then
    REMOTE_SHELL="bash -s -- render"
  elif test "$OP" = foliage_diag; then
    REMOTE_SHELL="bash -s -- foliage"
  elif test "$OP" = photo; then
    # The parser accepts only finite bounded ASCII decimals.
    REMOTE_SHELL="bash -s -- photo $PHOTO_VALUES"
  else
    REMOTE_SHELL="bash -s -- play"
  fi
fi
for attempt in 1 2; do
  if timeout "$REMOTE_TIMEOUT" gh codespace ssh -c "$CS" -- "$REMOTE_SHELL" \
      < "$SCRIPT_FILE" \
      > "$OUT/terminal.txt" 2>"$PRIVATE_ERRORS"; then
    rm -f "$PRIVATE_ERRORS"
    if test "$OP" = drop_frames; then
      if ! timeout 360 gh codespace ssh -c "$CS" --           'base64 /tmp/aaris-codespaces/offline-drop-probe/evidence.tar.gz'           > "$OUT/drop-frames.tar.gz.b64" 2>"$PRIVATE_ERRORS"; then
        DETAIL="drop_frame_transfer_failed"; safe_error_summary; exit 42
      fi
      if ! base64 --decode "$OUT/drop-frames.tar.gz.b64" > "$OUT/drop-frames.tar.gz"; then
        DETAIL="drop_frame_invalid_base64"; exit 43
      fi
      rm -f "$OUT/drop-frames.tar.gz.b64"
      if ! python3 - "$OUT/drop-frames.tar.gz" <<'PYCHECK'
import re,sys,tarfile,pathlib
path=pathlib.Path(sys.argv[1])
try:
    if path.stat().st_size>180*1024*1024:raise ValueError()
    with tarfile.open(path,"r:gz") as archive:
        members=archive.getmembers()
    if not members or len(members)>810: raise ValueError()
    for item in members:
        safe=bool(re.fullmatch(r"frames/frame_[0-9]{5}\.jpg|shots/(InPlane|Freefall|Opening|Gliding|Landed)\.png|report\.json",item.name))
        if not item.isfile() or item.size>8*1024*1024 or not safe:raise ValueError()
except (OSError,ValueError,tarfile.TarError):
    sys.exit(1)
PYCHECK
      then
        DETAIL="drop_frame_archive_invalid"; exit 44
      fi
      DETAIL="real_unity_frames_archived"
    elif test "$OP" = drop_video; then
      # Download genuine Unity camera video/report from the SAME Codespace;
      # do not transfer private Unity Editor logs or original working files.
      for BASENAME in drop.mp4 report.json; do
        if ! timeout 240 gh codespace ssh -c "$CS" -- "base64 /tmp/aaris-codespaces/offline-drop-probe/$BASENAME" \
             > "$OUT/$BASENAME.b64" 2>"$PRIVATE_ERRORS"; then
          DETAIL="drop_video_artifact_transfer_failed"; safe_error_summary; exit 36
        fi
        if ! base64 --decode "$OUT/$BASENAME.b64" > "$OUT/$BASENAME"; then
          DETAIL="drop_video_artifact_decode_failed"; exit 37
        fi
        rm -f "$OUT/$BASENAME.b64"
      done
      if ! python3 - "$OUT/report.json" "$OUT/drop.mp4" <<'PYVERIFY'
import json,sys,pathlib
try:
  data=json.load(open(sys.argv[1])); video=pathlib.Path(sys.argv[2])
  ok=video.is_file() and video.stat().st_size>=32768 and b'ftyp' in video.read_bytes()[:64]
  ok=ok and data.get('mode')=='offline_drop_video' and data.get('status')=='passed'
  ok=ok and data.get('observedInPlane') and data.get('observedFreefall')
  ok=ok and data.get('observedOpening') and data.get('observedGliding') and data.get('observedLanded')
  ok=ok and data.get('existingMotorRestored') and data.get('canopyActuallyRendered')
  ok=ok and data.get('errors')==0 and data.get('terrainTiles')==4
except (OSError,ValueError,TypeError,KeyError): ok=False
sys.exit(0 if ok else 1)
PYVERIFY
      then
        DETAIL="drop_video_test_failed_artifact_preserved"; exit 38
      fi
      DETAIL="real_unity_parachute_video_verified"
    elif test "$OP" = shot; then
      if timeout 90 gh codespace ssh -c "$CS" -- 'base64 /tmp/aaris-codespaces/screen.png' \
          > "$OUT/screen.b64" 2>"$PRIVATE_ERRORS"; then
        base64 --decode "$OUT/screen.b64" > "$OUT/screen.png"
        rm -f "$OUT/screen.b64"
        if ! file "$OUT/screen.png" | grep -q 'PNG image data'; then
          DETAIL="screen_invalid_png"; exit 16
        fi
        DETAIL="screen_in_actions_artifact"
      else DETAIL="screen_transfer_failed"; safe_error_summary; exit 13; fi
    elif test "$OP" = scene_render || test "$OP" = scene_play || test "$OP" = photo || test "$OP" = foliage_diag; then
      # Transfer only the Unity-generated verified PNG and small JSON report,
      # never an Xvfb desktop screen, a log, or a license dialog.
      if test "$OP" = scene_render; then
        IMAGE=map-overview.png
        REPORT=render-report.json
      elif test "$OP" = foliage_diag; then
        IMAGE=foliage-original.png
        CONTEXT=foliage-far-lod.png
        THIRD=foliage-ambient-test.png
        FOURTH=foliage-ambient-only.png
        FIFTH=foliage-shadows-only.png
        REPORT=foliage-report.json
      elif test "$OP" = photo; then
        IMAGE=photo-detail.png
        CONTEXT=photo-context.png
        REPORT=photo-report.json
      else
        IMAGE=play-frame.png
        REPORT=play-report.json
      fi
      FILES=("$IMAGE" "$REPORT")
      if test "$OP" = photo || test "$OP" = foliage_diag; then FILES+=("$CONTEXT"); fi
      if test "$OP" = foliage_diag; then FILES+=("$THIRD" "$FOURTH" "$FIFTH"); fi
      for BASENAME in "${FILES[@]}"; do
        if ! timeout 180 gh codespace ssh -c "$CS" -- "base64 /tmp/aaris-codespaces/scene-probe/$BASENAME" \
             > "$OUT/$BASENAME.b64" 2>"$PRIVATE_ERRORS"; then
          DETAIL="native_probe_artifact_transfer_failed"; safe_error_summary; exit 31
        fi
        if ! base64 --decode "$OUT/$BASENAME.b64" > "$OUT/$BASENAME"; then
          DETAIL="native_probe_artifact_base64_invalid"; exit 32
        fi
        rm -f "$OUT/$BASENAME.b64"
      done
      if ! file "$OUT/$IMAGE" | grep -q 'PNG image data' ||
         ! python3 -m json.tool "$OUT/$REPORT" >/dev/null ||
         { { test "$OP" = photo || test "$OP" = foliage_diag; } &&
           ! file "$OUT/$CONTEXT" | grep -q 'PNG image data'; } ||
         { test "$OP" = foliage_diag && ! file "$OUT/$THIRD" | grep -q 'PNG image data'; } ||
         { test "$OP" = foliage_diag && ! file "$OUT/$FOURTH" | grep -q 'PNG image data'; } ||
         { test "$OP" = foliage_diag && ! file "$OUT/$FIFTH" | grep -q 'PNG image data'; }; then
        DETAIL="native_probe_artifact_integrity_failed"; exit 33
      fi
      DETAIL="native_probe_real_unity_verified"
    else DETAIL="remote_task_complete"; fi
    STATE="SUCCESS"; exit 0
  fi
  test "$OP" = status || break
  sleep 7
done
safe_error_summary
DETAIL="SSH_execution_failed_see_diagnostic_category"
exit 11
