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
SNAP_TOKEN=""
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
    'CS FRESH MINIMAL') OP=fresh_minimal ;;
    'CS REBUILD') OP=rebuild ;;
    'CS FULL REBUILD') OP=full_rebuild ;;
    'CS STATUS') OP=status ;;
    'CS LOGS') OP=logs ;;
    'CS INFO') OP=info ;;
    'CS QUOTA') OP=quota ;;
    'CS SYNC') OP=sync ;;
    'CS RECOVERY SYNC') OP=recovery_sync ;;
    'CS LFS') OP=lfs ;;
    'CS LFS REPORT') OP=lfs_report ;;
    'CS CHANGES') OP=changes ;;
    'CS SAVE CHANGES') OP=preserve_assets ;;
    'CS RETRY SAVE') OP=retry_assets ;;
    'CS INSTALL') OP=install ;;
    'CS BOOTSTRAP') OP=bootstrap ;;
    'CS DEPS') OP=deps ;;
    'CS CLI DIAG') OP=cli_diag ;;
    'CS EDITOR') OP=editor ;;
    'CS EDITOR DIAG') OP=editor_diag ;;
    'CS EDITOR ISOLATE') OP=editor_isolate ;;
    'CS TMP AUDIT') OP=tmp_audit ;;
    'CS LICENSE') OP=license ;;
    'CS AUTH') OP=auth ;;
    'CS PROBE') OP=probe ;;
    'CS SHOT') OP=shot ;;
    'CS SCENE RENDER') OP=scene_render ;;
    'CS SCENE PLAY') OP=scene_play ;;
    'CS DROP VIDEO') OP=drop_video ;;
    'CS DROP FRAMES') OP=drop_frames ;;
    'CS MAP VERIFY') OP=map_verify ;;
    'CS SURVEY') OP=survey ;;
    'CS PLAY DIAG') OP=play_diag ;;
    'CS FOLIAGE DIAG') OP=foliage_diag ;;
    'CS TREE AUDIT') OP=tree_audit ;;
    'CS TREE PREVIEW') OP=tree_preview ;;
    'CS TREE PUBLISH') OP=tree_publish ;;
    'CS TREE REPORT') OP=tree_report ;;
    'CS PHOTO '*) OP=photo ;;
    'CS SNAP '*) OP=snapshot ;;
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
  machines|info|quota|start|fresh|fresh_ssh|fresh_minimal|rebuild|full_rebuild|status|logs|sync|recovery_sync|lfs|lfs_report|changes|preserve_assets|retry_assets|install|bootstrap|deps|cli_diag|editor|editor_diag|editor_isolate|tmp_audit|license|auth|probe|shot|accept_terms|safe_mode|compile_report|refresh|close_editor|editor_process|editor_recover|scene_render|scene_play|drop_video|drop_frames|map_verify|photo|snapshot|survey|play_diag|foliage_diag|tree_audit|tree_preview|tree_publish|tree_report|test|stop) ;;
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
if test "$OP" = snapshot; then
  SNAP_TOKEN="$(python3 "$GITHUB_WORKSPACE/Tools/Codespaces/snapshot_issue_request.py" "$ISSUE" 2>/dev/null)" || {
    DETAIL="invalid_snapshot_payload"; exit 80;
  }
  [[ "$SNAP_TOKEN" =~ ^[A-Za-z0-9_-]{12,16000}={0,2}$ ]] || {
    DETAIL="snapshot_payload_unsafe"; exit 81;
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
# Create an independent, lightweight Ubuntu SSH Codespace on the CURRENT
# Codespaces billing account. It does not alter/delete any existing worktree.
if test "$OP" = fresh_minimal; then
  EXISTING="$(jq -r '[.[] | select(.displayName=="aaris-ai-ubuntu-minimal")][0].name // ""' <<< "$LIST")"
  if test -n "$EXISTING"; then
    printf 'codespace=%s\n' "$EXISTING" > "$OUT/terminal.txt"
    STATE="SUCCESS"; DETAIL="existing_minimal_codespace_selected"; exit 0
  fi
  if ! jq -e '.image == "mcr.microsoft.com/devcontainers/base:ubuntu-22.04" and .remoteUser == "vscode" and .features["ghcr.io/devcontainers/features/sshd:1"] != null' "$GITHUB_WORKSPACE/.devcontainer/minimal-ubuntu/devcontainer.json" >/dev/null; then
    DETAIL="minimal_ubuntu_ssh_configuration_missing"; exit 38
  fi
  if ! gh api "/repos/$REPO/codespaces/machines" --jq '.machines[].name' 2>/dev/null | grep -qxF "standardLinux32gb"; then
    DETAIL="minimal_4core_16gb_unavailable"; exit 39
  fi
  if ! NEW="$(timeout 1200 gh codespace create -R "$REPO" -b main -m standardLinux32gb --devcontainer-path .devcontainer/minimal-ubuntu/devcontainer.json --idle-timeout 240m --display-name aaris-ai-ubuntu-minimal --default-permissions 2>/dev/null)"; then
    DETAIL="minimal_codespace_creation_failed_check_quota"; exit 40
  fi
  NEW="$(printf '%s\n' "$NEW" | tail -n 1 | tr -d "[:space:]")"
  [[ "$NEW" =~ ^[A-Za-z0-9-]+$ ]] || { DETAIL="minimal_creation_response_unverified"; exit 41; }
  printf 'codespace=%s\nmachine=standardLinux32gb\n' "$NEW" > "$OUT/terminal.txt"
  echo 'AARIS_MINIMAL_UBUNTU_SSH_CREATED' >> "$OUT/terminal.txt"
  # Pause compute on only the known failed worker machine, never delete it.
  OLD="aaris-ai-unity-ssh-r7x67xqjg4qphgx6"
  if jq -e --arg target "$OLD" 'any(.[]; .name == $target)' <<< "$LIST" >/dev/null; then
    gh codespace stop -c "$OLD" >/dev/null 2>/dev/null || true
  fi
  STATE="SUCCESS"; DETAIL="minimal_ubuntu_codespace_created_old_preserved"; exit 0
fi
# Account-aware selection for existing Codespaces. The CODESPACE_PAT GitHub CLI
# can only list the selected account's Codespaces, not the master account's.
# Never pick the first result when multiple match; never create on CS START.
# Exact workflow input (if supplied) wins; otherwise preserve the original
# owner's pinned machine, or accept ONE uniquely labeled SSH Codespace.
TARGET_CODESPACE="aaris-ai-unity-ssh-vp5g97jv4r4gfpjgq"
if test -n "$MACHINE"; then
  TARGET_CODESPACE="$MACHINE"
  CS="$(jq -r --arg target "$TARGET_CODESPACE" '[.[] | select(.name == $target)][0].name // ""' <<< "$LIST")"
elif jq -e --arg target "$TARGET_CODESPACE" 'any(.[]; .name == $target)' <<< "$LIST" >/dev/null; then
  CS="$TARGET_CODESPACE"
else
  MINIMAL_COUNT="$(jq '[.[] | select(.displayName == "aaris-ai-ubuntu-minimal")] | length' <<< "$LIST")"
  if test "$MINIMAL_COUNT" -eq 1; then
    CS="$(jq -r '.[] | select(.displayName == "aaris-ai-ubuntu-minimal") | .name' <<< "$LIST")"
    TARGET_CODESPACE="$CS"
  elif test "$MINIMAL_COUNT" -gt 1; then
    CS=""
  else
    COUNT="$(jq '[.[] | select(.displayName == "aaris-ai-unity-ssh")] | length' <<< "$LIST")"
    if test "$COUNT" -eq 1; then
      CS="$(jq -r '.[] | select(.displayName == "aaris-ai-unity-ssh") | .name' <<< "$LIST")"
      TARGET_CODESPACE="$CS"
    else
      CS=""
    fi
  fi
fi
if test -z "$CS"; then
  echo 'reason=selected_account_codespace_missing_or_ambiguous' > "$OUT/terminal.txt"
  STATE="TARGET_UNAVAILABLE"; DETAIL="existing_target_not_unambiguously_selected"; exit 0
fi
[[ "$CS" =~ ^[A-Za-z0-9-]+$ ]] || { DETAIL="bad_codespace_name"; exit 9; }
# A recovery container can lack Python, Xvfb, Unity and LFS; use a reviewed
# Bash-only fast-forward, and only on the owner's newly approved worker Codespace.
if test "$OP" = recovery_sync; then
  if test "$CS" != "aaris-ai-unity-ssh-r7x67xqjg4qphgx6"; then
    DETAIL="recovery_sync_not_approved_for_target"; exit 35
  fi
  if timeout 360 gh codespace ssh -c "$CS" -- "bash -s" < "$GITHUB_WORKSPACE/Tools/Codespaces/recovery_sync.sh" > "$OUT/terminal.txt" 2>/dev/null; then
    if grep -qxF 'AARIS_RECOVERY_UBUNTU_CONFIG_READY' <(tail -n 1 "$OUT/terminal.txt"); then
      STATE="SUCCESS"; DETAIL="recovery_safe_fast_forward_complete"; exit 0
    fi
  fi
  DETAIL="recovery_sync_failed_without_reset"; exit 36
fi
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
if test "$OP" = rebuild || test "$OP" = full_rebuild; then
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
  REBUILD_FLAGS=()
  if test "$OP" = full_rebuild; then REBUILD_FLAGS=(--full); fi
  if timeout 2400 gh codespace rebuild -c "$CS" "${REBUILD_FLAGS[@]}" >/dev/null 2>/dev/null; then
    if test "$OP" = full_rebuild; then
      echo "AARIS_UBUNTU_FULL_REBUILD_REQUEST_COMPLETED" >> "$OUT/terminal.txt"
    else
      echo "AARIS_UBUNTU_REBUILD_REQUEST_COMPLETED" >> "$OUT/terminal.txt"
    fi
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
  bootstrap) REMOTE_TIMEOUT=1500 ;;
  deps) REMOTE_TIMEOUT=900 ;;
  sync|lfs) REMOTE_TIMEOUT=1200 ;;
  preserve_assets) REMOTE_TIMEOUT=1500 ;;
  retry_assets) REMOTE_TIMEOUT=900 ;;
  test) REMOTE_TIMEOUT=500 ;;
  editor) REMOTE_TIMEOUT=350 ;;
  editor_isolate) REMOTE_TIMEOUT=650 ;;
  tmp_audit) REMOTE_TIMEOUT=240 ;;
  scene_render|scene_play|photo|foliage_diag|survey|tree_audit|tree_preview|tree_publish) REMOTE_TIMEOUT=1800 ;;
  snapshot) REMOTE_TIMEOUT=1050 ;;
  drop_video) REMOTE_TIMEOUT=2100 ;;
  drop_frames) REMOTE_TIMEOUT=420 ;;
  map_verify) REMOTE_TIMEOUT=1800 ;;
esac
# The Codespace account can read its 775 saved LFS files but Git LFS remote
# fetches for the 18 new meshes may fail even after server-side OID verification.
# Transfer the ORIGINAL historical Unity bake from the trusted Actions artifact,
# never a model-generated replacement or an account token. All payload hashes
# are checked again by restore_preserved_source_lfs.py against main's pointers.
if [[ "$OP" =~ ^(scene_render|scene_play|photo|foliage_diag|survey)$ ]]; then
  ARCHIVE_DIR="$RUNNER_TEMP/aaris-verified-native-tree-archive"
  mkdir -p "$ARCHIVE_DIR"
  if ! GH_TOKEN="$AARIS_ISSUE_REPLY_TOKEN" gh run download 42472771253 \
      -R "$REPO" --name aaris-codespace-42472771253 \
      -D "$ARCHIVE_DIR" >/dev/null 2>"$PRIVATE_ERRORS"; then
    DETAIL="verified_native_tree_archive_not_downloadable"
    safe_error_summary
    exit 72
  fi
  TREE_ARCHIVE="$ARCHIVE_DIR/mass-package.tar.gz"
  EXPECTED_TREE_ARCHIVE_SHA=234024b842e8cc5e1253607ce41b5a6aebec6a7abd3c9fd81837bc960a24197d
  if ! test -f "$TREE_ARCHIVE" ||
     test "$(stat -c %s "$TREE_ARCHIVE")" != "4757727" ||
     test "$(sha256sum "$TREE_ARCHIVE" | cut -d ' ' -f1)" != "$EXPECTED_TREE_ARCHIVE_SHA"; then
    DETAIL="historical_native_tree_archive_hash_mismatch"
    exit 73
  fi
  # This fixed 4.7 MB original archive is non-secret and goes ONLY to /tmp.
  # The project working tree, LFS .git database and Unity license are untouched.
  if ! timeout 180 gh codespace ssh -c "$CS" -- \
      'bash -c "umask 077; mkdir -p /tmp/aaris-codespaces/scene-probe; cat > /tmp/aaris-codespaces/scene-probe/verified-tree-mass-package.tar.gz"' \
      < "$TREE_ARCHIVE" >/dev/null 2>"$PRIVATE_ERRORS"; then
    DETAIL="verified_native_tree_archive_transfer_failed"
    safe_error_summary
    exit 74
  fi
  REMOTE_TREE_SHA="$(timeout 50 gh codespace ssh -c "$CS" -- \
      'sha256sum /tmp/aaris-codespaces/scene-probe/verified-tree-mass-package.tar.gz' \
      2>"$PRIVATE_ERRORS" | cut -d ' ' -f1 | tr -d '\r\n')" || {
    DETAIL="remote_native_tree_archive_hash_unavailable"
    safe_error_summary
    exit 75
  }
  if test "$REMOTE_TREE_SHA" != "$EXPECTED_TREE_ARCHIVE_SHA"; then
    DETAIL="remote_native_tree_archive_hash_mismatch"
    exit 76
  fi
fi

if test "$OP" = retry_assets; then
  # Authenticated GitHub Actions checkout handles only the exact saved 210
  # project edits. Existing Codespace is accessed read-only; no new machine.
  if timeout 1650 python3 "$GITHUB_WORKSPACE/Tools/Codespaces/publish_checkpoint_from_actions.py" "$CS" \
      > "$OUT/terminal.txt" 2>"$PRIVATE_ERRORS"; then
    STATE="SUCCESS"
    DETAIL="original_210_unity_files_uploaded_to_review_branch"
    exit 0
  fi
  safe_error_summary
  DETAIL="authenticated_checkpoint_upload_failed"
  exit 54
fi
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
if test "$OP" = preserve_assets; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/preserve_working_assets.sh"
  REMOTE_SHELL="bash -s"
fi
if test "$OP" = retry_assets; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/retry_one_lfs_checkpoint.sh"
  REMOTE_SHELL="bash -s"
fi
if test "$OP" = drop_video; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/offline_drop_probe.sh"
  REMOTE_SHELL="bash -s"
fi
if test "$OP" = drop_frames; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/offline_drop_collect.sh"
  REMOTE_SHELL="bash -s"
fi
if test "$OP" = map_verify; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/map_expansion_probe.sh"
  REMOTE_SHELL="bash -s"
fi
if test "$OP" = deps; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/unity_gui_deps.sh"
  REMOTE_SHELL="bash -s"
fi
if test "$OP" = editor_isolate; then
  # Stream reviewed script; do not require its earlier code on remote.
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/editor_isolation_probe.sh"
  REMOTE_SHELL="bash -s"
fi
if test "$OP" = tmp_audit; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/tmp_audit.sh"
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
if test "$OP" = tree_report; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/tree_mass_report.sh"
  REMOTE_SHELL="bash -s"
fi
if test "$OP" = tree_publish; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/tree_mass_pack.sh"
  REMOTE_SHELL="bash -s"
fi
if test "$OP" = tree_preview; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/tree_mass_preview.sh"
  REMOTE_SHELL="bash -s"
fi
if test "$OP" = tree_audit; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/tree_mass_audit.sh"
  REMOTE_SHELL="bash -s"
fi
if test "$OP" = play_diag; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/play_probe_diagnostic.sh"
  REMOTE_SHELL="bash -s"
fi
if test "$OP" = scene_render || test "$OP" = scene_play || test "$OP" = photo || test "$OP" = foliage_diag || test "$OP" = survey; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/scene_probe.sh"
  if test "$OP" = scene_render; then
    REMOTE_SHELL="bash -s -- render"
  elif test "$OP" = foliage_diag; then
    REMOTE_SHELL="bash -s -- foliage"
  elif test "$OP" = survey; then
    REMOTE_SHELL="bash -s -- survey"
  elif test "$OP" = photo; then
    # The parser accepts only finite bounded ASCII decimals.
    REMOTE_SHELL="bash -s -- photo $PHOTO_VALUES"
  else
    REMOTE_SHELL="bash -s -- play"
  fi
fi
if test "$OP" = snapshot; then
  SCRIPT_FILE="$GITHUB_WORKSPACE/Tools/Codespaces/snapshot_issue_remote.sh"
  REMOTE_SHELL="bash -s -- $SNAP_TOKEN"
fi
for attempt in 1 2; do
  if timeout "$REMOTE_TIMEOUT" gh codespace ssh -c "$CS" -- "$REMOTE_SHELL" \
      < "$SCRIPT_FILE" \
      > "$OUT/terminal.txt" 2>"$PRIVATE_ERRORS"; then
    rm -f "$PRIVATE_ERRORS"
    if test "$OP" = snapshot; then
      if ! timeout 650 gh codespace ssh -c "$CS" -- 'base64 /tmp/aaris-codespaces/hot-snapshots/bundle.tar.gz' \
          > "$OUT/hot-snapshot.bundle.b64" 2>"$PRIVATE_ERRORS"; then
        DETAIL="snapshot_archive_transfer_failed"; safe_error_summary; exit 82
      fi
      if ! base64 --decode "$OUT/hot-snapshot.bundle.b64" > "$OUT/hot-snapshot.tar.gz"; then
        DETAIL="snapshot_archive_decode_failed"; exit 83
      fi
      rm -f "$OUT/hot-snapshot.bundle.b64"
      if ! python3 "$GITHUB_WORKSPACE/Tools/Codespaces/snapshot_bundle_check.py" \
            "$OUT/hot-snapshot.tar.gz" "$OUT" >> "$OUT/terminal.txt" 2>/dev/null; then
        DETAIL="snapshot_archive_not_verified"; exit 84
      fi
      if grep -q '^AARIS_SNAPSHOT_BUNDLE_SUSPECT_COUNT_' "$OUT/terminal.txt"; then
        STATE="UNKNOWN"; DETAIL="real_photos_suspected_magenta_review_artifact"; exit 0
      fi
      DETAIL="real_unity_hot_snapshots_verified"
    elif test "$OP" = tree_publish; then
      if ! timeout 900 gh codespace ssh -c "$CS" -- \
          'base64 /tmp/aaris-codespaces/tree-mass/mass-package.tar.gz' \
          > "$OUT/mass-package.tar.gz.b64" 2>"$PRIVATE_ERRORS"; then
        DETAIL="tree_mass_package_transfer_failed"; safe_error_summary; exit 69
      fi
      if ! base64 --decode "$OUT/mass-package.tar.gz.b64" > "$OUT/mass-package.tar.gz"; then
        DETAIL="tree_mass_package_decode_failed"; exit 70
      fi
      rm -f "$OUT/mass-package.tar.gz.b64"
      if ! python3 "$GITHUB_WORKSPACE/Tools/Codespaces/publish_tree_mass_from_actions.py" \
           "$OUT/mass-package.tar.gz" "$OUT/publish-report.json" >> "$OUT/terminal.txt" 2>/dev/null; then
        DETAIL="tree_mass_lfs_review_branch_publish_failed"; exit 71
      fi
      DETAIL="18_speedtree_meshes_published_as_lfs_on_review_branch"
    elif test "$OP" = tree_preview; then
      TREE_MASS_FILES=(mass-report.json)
      for NAME in "${TREE_MASS_FILES[@]}"; do
        timeout 180 gh codespace ssh -c "$CS" -- \
            "base64 /tmp/aaris-codespaces/tree-mass/$NAME" > "$OUT/$NAME.b64" 2>"$PRIVATE_ERRORS" || {
              DETAIL="tree_mass_report_transfer_failed"; safe_error_summary; exit 64;
            }
        base64 --decode "$OUT/$NAME.b64" > "$OUT/$NAME" || { DETAIL="tree_mass_decode_failed"; exit 65; }
        rm -f "$OUT/$NAME.b64"
      done
      TREE_IMAGES=(foliage-report.json foliage-original.png foliage-far-lod.png foliage-ambient-only.png foliage-shadows-only.png foliage-ambient-test.png)
      for NAME in "${TREE_IMAGES[@]}"; do
        timeout 180 gh codespace ssh -c "$CS" -- \
            "base64 /tmp/aaris-codespaces/scene-probe/$NAME" > "$OUT/$NAME.b64" 2>"$PRIVATE_ERRORS" || {
              DETAIL="tree_mass_image_transfer_failed"; safe_error_summary; exit 66;
            }
        base64 --decode "$OUT/$NAME.b64" > "$OUT/$NAME" || { DETAIL="tree_mass_image_decode_failed"; exit 67; }
        rm -f "$OUT/$NAME.b64"
      done
      if ! python3 - "$OUT" <<'PYMASS'
import pathlib,json,sys
try:
 p=pathlib.Path(sys.argv[1]);m=json.loads((p/"mass-report.json").read_text());f=json.loads((p/"foliage-report.json").read_text())
 ok=m.get("status")=="passed" and m.get("species")==6 and m.get("meshAssets")==18
 ok=ok and f.get("status")=="passed" and f.get("terrainTiles")==4
 for name in ("foliage-original.png","foliage-far-lod.png","foliage-ambient-only.png","foliage-shadows-only.png","foliage-ambient-test.png"):
  v=p/name;ok=ok and v.stat().st_size>8192 and v.read_bytes()[:8]==b"\x89PNG\r\n\x1a\n"
except (OSError,ValueError,TypeError,KeyError):ok=False
sys.exit(0 if ok else 1)
PYMASS
      then
        DETAIL="tree_mass_preview_invalid"; exit 68
      fi
      DETAIL="six_sculpted_tree_species_native_preview_verified"
    elif test "$OP" = tree_audit; then
      if ! timeout 120 gh codespace ssh -c "$CS" -- \
          'base64 /tmp/aaris-codespaces/tree-mass/audit.json' \
          > "$OUT/tree-mass-audit.b64" 2>"$PRIVATE_ERRORS"; then
        DETAIL="native_tree_mesh_report_transfer_failed"; safe_error_summary; exit 61
      fi
      if ! base64 --decode "$OUT/tree-mass-audit.b64" > "$OUT/tree-mass-audit.json"; then
        DETAIL="native_tree_mesh_report_decode_failed"; exit 62
      fi
      rm -f "$OUT/tree-mass-audit.b64"
      if ! python3 - "$OUT/tree-mass-audit.json" <<'PYAUDIT'
import json,sys
try:
  d=json.load(open(sys.argv[1]))
  ok=d.get("status")=="passed" and d.get("speciesCount")==6
  ok=ok and len(d.get("species",[]))==6
  ok=ok and all(len(v["lods"])==4 and v["colliders"]>=1 for v in d["species"])
except (ValueError,TypeError,OSError,KeyError):ok=False
sys.exit(0 if ok else 1)
PYAUDIT
      then
        DETAIL="native_tree_mesh_report_invalid"; exit 63
      fi
      DETAIL="six_native_speedtree_meshes_audited"
    elif test "$OP" = retry_assets; then
      if ! timeout 120 gh codespace ssh -c "$CS" --            'base64 /tmp/aaris-codespaces/working-changes-checkpoint/manifest.json'            > "$OUT/changes-manifest.json.b64" 2>"$PRIVATE_ERRORS"; then
        DETAIL="checkpoint_manifest_transfer_failed"; safe_error_summary; exit 49
      fi
      if ! base64 --decode "$OUT/changes-manifest.json.b64" > "$OUT/changes-manifest.json"; then
        DETAIL="checkpoint_manifest_decode_failed"; exit 50
      fi
      rm -f "$OUT/changes-manifest.json.b64"
      if ! python3 - "$OUT/changes-manifest.json" <<'PYMANIFEST'
import json,sys,re
try:
    d=json.load(open(sys.argv[1])); files=d.get('files',[])
    valid=d.get('kind')=='aaris_codespace_working_assets_checkpoint'
    valid=valid and isinstance(files,list) and len(files)==210 and d.get('changedCount')==210
    valid=valid and d.get('lfsChangedCount')==1
    valid=valid and d.get('branch')=='backup/codespace-local-changes-20261108'
    valid=valid and all(re.fullmatch('[a-f0-9]{64}',f['sha256']) for f in files)
except (ValueError,OSError,KeyError,TypeError):valid=False
sys.exit(0 if valid else 1)
PYMANIFEST
      then
        DETAIL="checkpoint_manifest_invalid"; exit 51
      fi
      DETAIL="specific_210_tracked_changes_uploaded_to_review_branch"
    elif test "$OP" = preserve_assets; then
      if ! timeout 120 gh codespace ssh -c "$CS" -- \
           'base64 /tmp/aaris-codespaces/working-changes-checkpoint/manifest.json' \
           > "$OUT/changes-manifest.json.b64" 2>"$PRIVATE_ERRORS"; then
        DETAIL="checkpoint_manifest_transfer_failed"; safe_error_summary; exit 46
      fi
      if ! base64 --decode "$OUT/changes-manifest.json.b64" > "$OUT/changes-manifest.json"; then
        DETAIL="checkpoint_manifest_decode_failed"; exit 47
      fi
      rm -f "$OUT/changes-manifest.json.b64"
      if ! python3 - "$OUT/changes-manifest.json" <<'PYMANIFEST'
import json,sys,re
try:
    d=json.load(open(sys.argv[1]))
    files=d.get('files',[])
    ok=d.get('kind')=='aaris_codespace_working_assets_checkpoint'
    ok=ok and isinstance(files,list) and 1<=len(files)<=1500
    ok=ok and len(files)==d.get('changedCount')
    ok=ok and all(re.fullmatch(r'[a-f0-9]{64}',f['sha256']) for f in files)
    ok=ok and d.get('branch')=='backup/codespace-local-changes-20261108'
except (ValueError,TypeError,KeyError,OSError):ok=False
sys.exit(0 if ok else 1)
PYMANIFEST
      then
        DETAIL="checkpoint_manifest_invalid"; exit 48
      fi
      DETAIL="all_tracked_assets_preserved_on_review_branch"
    elif test "$OP" = drop_frames; then
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
    elif test "$OP" = survey; then
      # Four 3x2 contact sheets containing all 24 genuine Unity camera views.
      # Sources were validated in the isolated native Editor worktree.
      SURVEY_FILES=(survey-report.json survey-sheet-1.png survey-sheet-2.png survey-sheet-3.png survey-sheet-4.png)
      for BASENAME in "${SURVEY_FILES[@]}"; do
        if ! timeout 200 gh codespace ssh -c "$CS" -- \
          "base64 /tmp/aaris-codespaces/scene-probe/$BASENAME" \
          > "$OUT/$BASENAME.b64" 2>"$PRIVATE_ERRORS"; then
          DETAIL="survey_artifact_transfer_failed"; safe_error_summary; exit 52
        fi
        if ! base64 --decode "$OUT/$BASENAME.b64" > "$OUT/$BASENAME"; then
          DETAIL="survey_artifact_base64_invalid"; exit 53
        fi
        rm -f "$OUT/$BASENAME.b64"
      done
      if ! python3 - "$OUT" <<'PYSURVEY'
import json,sys,pathlib
p=pathlib.Path(sys.argv[1])
try:
    d=json.loads((p/"survey-report.json").read_text())
    good=d.get("status")=="passed" and d.get("mode")=="survey"
    good=good and d.get("terrainTiles")==4 and d.get("frameCount")==24
    good=good and d.get("locationCount")==12 and len(d.get("locations",[]))==12
    good=good and all(min(v.get("detailVariation",0),v.get("contextVariation",0))>=6 for v in d["locations"])
    for i in (1,2,3,4):
        file=p/f"survey-sheet-{i}.png"
        good=good and file.is_file() and file.stat().st_size>8192
        good=good and file.read_bytes()[:8]==bytes([137,80,78,71,13,10,26,10])
except (ValueError,OSError,TypeError,KeyError):
    good=False
sys.exit(0 if good else 1)
PYSURVEY
      then
        DETAIL="survey_native_unity_frames_invalid"; exit 54
      fi
      DETAIL="twelve_regions_twentyfour_native_frames_four_contact_sheets_verified"
    elif test "$OP" = map_verify; then
      # Read only JSON and fixed-name genuine Unity camera PNGs. Never publish
      # raw Editor logs, original dirty Assets or private license material.
      if ! timeout 180 gh codespace ssh -c "$CS" --         'base64 /tmp/aaris-codespaces/map-expansion-probe/report.json'         > "$OUT/map-report.b64" 2>"$PRIVATE_ERRORS"; then
        DETAIL="map_probe_report_transfer_failed"; safe_error_summary; exit 39
      fi
      if ! base64 --decode "$OUT/map-report.b64" > "$OUT/map-report.json"; then
        DETAIL="map_probe_report_decode_failed"; exit 40
      fi
      rm -f "$OUT/map-report.b64"
      MAP_FILES=(
        ARX_01_OldVillage_detached.png
        ARX_03_ValleySettlement_detached.png
        ARX_05_ClockTowerTown_detached.png
        ARX_07_CoastalHamlet_detached.png
        ARX_09_LakesideHamlet_cottage.png
        ARX_13_HighlandObservatory_watchtower.png
        ARX_14_ForestLookout_watchtower.png
        ARX_15_SouthernAirfield_watchtower.png
      )
      PHOTO_FOUND=0
      for BASENAME in "${MAP_FILES[@]}"; do
        if timeout 180 gh codespace ssh -c "$CS" --           "base64 /tmp/aaris-codespaces/map-expansion-probe/$BASENAME"           > "$OUT/$BASENAME.b64" 2>"$PRIVATE_ERRORS"; then
          if base64 --decode "$OUT/$BASENAME.b64" > "$OUT/$BASENAME" &&
             file "$OUT/$BASENAME" | grep -q 'PNG image data'; then
            PHOTO_FOUND=$((PHOTO_FOUND+1))
          else
            rm -f "$OUT/$BASENAME"
          fi
        fi
        rm -f "$OUT/$BASENAME.b64"
      done
      if ! python3 - "$OUT/map-report.json" "$PHOTO_FOUND" <<'PYMAP'
import json,sys
try:
  d=json.load(open(sys.argv[1]))
  images=int(sys.argv[2])
  ok=d.get("status")=="passed" and d.get("placed")==15
  ok=ok and d.get("imageCount")==8 and images==8
  ok=ok and d.get("runtimeErrors")==0 and d.get("terrainTiles")==4
except (OSError,ValueError,TypeError):
  ok=False
sys.exit(0 if ok else 1)
PYMAP
      then
        DETAIL="map_native_play_failed_artifacts_preserved"; exit 41
      fi
      DETAIL="real_unity_15_buildings_and_8_photos_verified"
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
