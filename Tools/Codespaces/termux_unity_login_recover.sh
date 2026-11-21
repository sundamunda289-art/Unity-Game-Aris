#!/data/data/com.termux/files/usr/bin/bash
# Private Unity login recovery for the currently selected GitHub Codespace.
# Uses official gh Codespaces SSH and Unity CLI; no public token, OAuth URL or license upload.
set -Eeuo pipefail
umask 077
REPO="sundamunda289-art/Unity-Game-Aris"
PROFILE="$HOME/.config/aaris-ai/codespace-gh"
CS="${AARIS_CODESPACE_NAME:-aaris-ai-ubuntu-minimal-4gvj7qgv7772v5g}"
TMP="$(mktemp -d "$HOME/.aaris-unity-login.XXXXXXXX")"
trap 'rm -rf "$TMP"' EXIT
say() { printf '%s\n' "$*"; }
fail() { say "ERROR: $*"; exit 1; }
[[ "${AARIS_UNITY_INSECURE_STORAGE:-0}" == 1 ]] || fail 'File-based Unity credentials require explicit opt-in: AARIS_UNITY_INSECURE_STORAGE=1'
command -v gh >/dev/null || fail 'GitHub CLI missing. Run: pkg install gh'
command -v curl >/dev/null || fail 'curl missing. Run: pkg install curl'
command -v jq >/dev/null || fail 'jq missing. Run: pkg install jq'
command -v python3 >/dev/null || fail 'Python missing. Run: pkg install python'
command -v timeout >/dev/null || fail 'timeout missing. Run: pkg install coreutils'
[[ "$CS" =~ ^[A-Za-z0-9][A-Za-z0-9-]{2,119}$ ]] || fail 'Invalid Codespace name'
[[ -s "$PROFILE/hosts.yml" ]] || fail 'Codespace GitHub login missing. Run bash "$HOME/aaris-switch.sh" first.'
export GH_CONFIG_DIR="$PROFILE"
unset GH_TOKEN GITHUB_TOKEN
say "Using Codespace: $CS"
CS_LOGIN="$(gh api user --jq .login 2>"$TMP/gh.err")" || fail 'Selected Codespace GitHub account is not authorized.'
LIST="$(gh codespace list --limit 100 --json name,state,repository 2>"$TMP/gh.err")" || fail 'Cannot list Codespaces. Check GitHub session/network.'
if ! printf '%s' "$LIST" | jq -e --arg n "$CS" --arg r "$CS_LOGIN/Unity-Game-Aris" '.[] | select(.name == $n and .repository == $r and .state == "Available")' > /dev/null; then
  fail 'Requested Codespace is not Available under the selected GitHub account.'
fi
say 'Step 1/4: Testing real Codespace SSH and Python...'
if ! timeout 70 gh codespace ssh -c "$CS" -- 'python3 -c "print(\"AARIS_SSH_PYTHON_OK\")"' >"$TMP/probe.out" 2>"$TMP/ssh.err"; then
  if grep -Eqi 'permission denied|publickey|authentication|401|403' "$TMP/ssh.err"; then fail 'Codespace SSH authorization denied. Check Termux GitHub login and Codespace SSH permissions.'; fi
  if grep -Eqi 'timed out|timeout|network|connection|resolve' "$TMP/ssh.err"; then fail 'Codespace SSH connection or network failed. Reconnect and retry this script.'; fi
  fail 'Codespace SSH command failed before Unity login could start. Re-run gh codespace ssh -c YOUR_MACHINE for a connection diagnostic.'
fi
if ! grep -Fxq 'AARIS_SSH_PYTHON_OK' "$TMP/probe.out"; then fail 'SSH opened, but remote Python did not run as expected.'; fi
say 'Codespace SSH verified.'
say 'Step 2/4: Preparing verified Unity login helper...'
curl -fsSL --retry 2 "https://raw.githubusercontent.com/$REPO/main/Tools/Codespaces/mobile_license.py" -o "$TMP/remote.py" || fail 'Could not download the reviewed Unity login helper.'
# Explicit opt-in to user-owned file credentials. No secret values are printed or copied to GitHub.
python3 - "$TMP/remote.py" <<'PY'
from pathlib import Path
import sys
p=Path(sys.argv[1])
s=p.read_text(encoding='utf-8')
old='[cli,"auth","login","--format","json"]'
new='[cli,"auth","login","--insecure-storage","--format","json"]'
if s.count(old)!=1:
    raise SystemExit('Unity helper source changed: refusing unverified substitution')
s=s.replace(old,new)
compile(s,str(p),'exec')
p.write_text(s,encoding='utf-8')
PY
SESSION="$(python3 -c 'import secrets;print(secrets.token_hex(8))')"
[[ "$SESSION" =~ ^[0-9a-f]{16}$ ]] || fail 'Session generation failed'
RDIR=".config/aaris-ai/unity-auth/$SESSION"
# Each run lives privately under the selected Codespace user's home, never in the Unity project.
REMOTE_SETUP="umask 077; mkdir -p \"\$HOME/$RDIR\"; cat > \"\$HOME/$RDIR/remote.py\"; chmod 600 \"\$HOME/$RDIR/remote.py\""
if ! timeout 70 gh codespace ssh -c "$CS" -- "$REMOTE_SETUP" < "$TMP/remote.py" > "$TMP/copy.out" 2> "$TMP/ssh.err"; then
  fail 'SSH transport failed while copying login helper; no OAuth page was started.'
fi
cat > "$TMP/runner.sh" <<EOF
#!/usr/bin/env bash
umask 077
python3 -u "\$HOME/$RDIR/remote.py" > "\$HOME/$RDIR/session.log" 2>&1
printf '%s\n' "\$?" > "\$HOME/$RDIR/exit_code"
EOF
if ! timeout 70 gh codespace ssh -c "$CS" -- "umask 077; cat > \"\$HOME/$RDIR/runner.sh\"; chmod 700 \"\$HOME/$RDIR/runner.sh\"" < "$TMP/runner.sh" > "$TMP/copy2.out" 2> "$TMP/ssh.err"; then
  fail 'SSH transport failed while copying session runner.'
fi
say 'Step 3/4: Starting Unity login independently of the SSH connection...'
START="nohup bash \"\$HOME/$RDIR/runner.sh\" </dev/null >/dev/null 2>&1 & echo AARIS_LOGIN_RUNNER_LAUNCHED"
if ! timeout 70 gh codespace ssh -c "$CS" -- "$START" > "$TMP/start.out" 2> "$TMP/ssh.err"; then
  fail 'Could not start the remote Unity login process.'
fi
grep -q 'AARIS_LOGIN_RUNNER_LAUNCHED' "$TMP/start.out" || fail 'Remote login runner launch was not confirmed.'
say 'Step 4/4: Waiting for fresh Unity-owned login link...'
opened=0
started="$(date +%s)"
last_state=''
auth_reported=0
while (( $(date +%s) - started < 570 )); do
  # Short SSH sessions tolerate a phone app switching between Termux and browser.
  OUT="$(timeout 40 gh codespace ssh -c "$CS" -- "cat \"\$HOME/$RDIR/session.log\" 2>/dev/null || true; if test -f \"\$HOME/$RDIR/exit_code\"; then echo REMOTE_EXIT_CODE=\$(cat \"\$HOME/$RDIR/exit_code\"); fi" 2> "$TMP/poll.err" || true)"
  if [[ -n "$OUT" ]]; then
    case "$OUT" in
      *UNITY_CLI_READY*) state=UNITY_CLI_READY ;;
      *) state=WAITING_FOR_UNITY_CLI ;;
    esac
    if [[ "$state" != "$last_state" ]]; then say "$state"; last_state="$state"; fi
    if (( opened == 0 )); then
      URL="$(printf '%s\n' "$OUT" | sed -n 's/^AARIS_AUTH_LINK=//p' | head -n1)"
      if [[ -n "$URL" ]]; then
        if python3 - "$URL" <<'PY'
import sys
from urllib.parse import urlsplit
u=sys.argv[1]
try:
    p=urlsplit(u)
    host=(p.hostname or '').lower()
    ok=(p.scheme=='https' and bool(host) and not p.username and not p.password
        and len(u)<4000 and any(host==s or host.endswith('.'+s) for s in ('unity.com','unity3d.com')))
except ValueError: ok=False
sys.exit(0 if ok else 1)
PY
        then
          say 'NEW UNITY LOGIN LINK RECEIVED FROM CODESPACE.'
          if command -v termux-clipboard-set >/dev/null; then
            printf '%s' "$URL" | timeout 8 termux-clipboard-set >/dev/null 2>&1 && say 'Fresh link copied to Android clipboard.' || true
          fi
          OPENED=0
          if command -v termux-open-url >/dev/null; then
            if timeout 12 termux-open-url "$URL" >/dev/null 2>&1; then OPENED=1; say 'Unity login requested in Android browser.'; fi
          fi
          if ((OPENED == 0)) && command -v am >/dev/null; then
            if timeout 12 am start -a android.intent.action.VIEW -d "$URL" >/dev/null 2>&1; then OPENED=1; say 'Unity login requested using Android VIEW intent.'; fi
          fi
          say 'If no browser appears, copy the NEW link below into Chrome on this phone:'
          printf '%s\n' "$URL"
          say 'Sign in, tap Allow, and then return to Termux. Do not share the URL or code.'
          opened=1
          unset URL
        else fail 'The remote helper returned a non-Unity HTTPS URL. Aborting.'; fi
      fi
    fi
    if grep -Fq 'UNITY_PERSONAL_LICENSE_VERIFIED' <<< "$OUT" || grep -Fq 'UNITY_LICENSE_ALREADY_ACTIVE' <<< "$OUT"; then
      say 'SUCCESS: Unity login and Personal license verified on selected Codespace.'
      # Remove only this script's one-time private OAuth data; keep Unity's own signed-in state.
      timeout 30 gh codespace ssh -c "$CS" -- "rm -f \"\$HOME/$RDIR/remote.py\" \"\$HOME/$RDIR/runner.sh\" \"\$HOME/$RDIR/session.log\" \"\$HOME/$RDIR/exit_code\"; rmdir \"\$HOME/$RDIR\" 2>/dev/null || true" >/dev/null 2>&1 || true
      exit 0
    fi
    if ((auth_reported == 0)) && grep -Fq 'UNITY_AUTH_VERIFIED' <<< "$OUT"; then say 'Unity browser login verified. Checking license activation...'; auth_reported=1; fi
    if grep -Fq 'UNITY_PERSONAL_NOT_ACTIVE_HUB_MAY_BE_REQUIRED' <<< "$OUT"; then
      fail 'Account login succeeded, but Unity Personal activation needs Unity Hub or a separate license check.'
    fi
    if grep -Fq 'UNITY_AUTH_NOT_COMPLETED' <<< "$OUT"; then
      fail 'Unity login was not completed in the active session. Check browser Allow/redirect behavior.'
    fi
    if grep -q '^REMOTE_EXIT_CODE=' <<< "$OUT"; then
      if (( opened == 0 )); then
        fail 'Remote Unity helper exited before producing a login URL. No browser could have opened. Check Unity CLI setup and remote authentication.'
      fi
      fail 'Remote Unity helper exited without verifying a Personal license.'
    fi
  fi
  sleep 8
done
fail 'Unity login did not complete before the session expired. Run this script for a FRESH link; never reuse a previous link.'
