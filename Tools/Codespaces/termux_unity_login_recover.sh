#!/data/data/com.termux/files/usr/bin/bash
# Four-step private Unity login, with one reattachable session on the exact host.
set -Eeuo pipefail
umask 077
REPO="sundamunda289-art/Unity-Game-Aris"
PROFILE="${AARIS_CODESPACE_AUTH_DIR:-$HOME/.config/aaris-ai/codespace-gh}"
CS="${AARIS_CODESPACE_NAME:-}"
SOURCE_SHA="${AARIS_SOURCE_SHA:-}"
INSECURE="${AARIS_UNITY_INSECURE_STORAGE:-0}"
TEMP_BASE="${TMPDIR:-${PREFIX:-/data/data/com.termux/files/usr}/tmp}"
mkdir -p "$TEMP_BASE"
TMP="$(mktemp -d "$TEMP_BASE/aaris-unity-login.XXXXXXXX")"
trap 'rm -rf -- "$TMP"' EXIT
trap 'printf "\nPhone monitor stopped; the private remote login session can be reattached by running the same command.\n"; exit 130' INT
trap 'exit 143' TERM
say() { printf '%s\n' "$*"; }
fail() { say "ERROR: $2"; exit "$1"; }
for tool in gh curl jq python3 timeout base64; do
  command -v "$tool" >/dev/null || fail 2 "Required Termux tool missing: $tool"
done
[[ "$INSECURE" == 0 || "$INSECURE" == 1 ]] || fail 2 'AARIS_UNITY_INSECURE_STORAGE must be 0 or 1.'
[[ -d "$PROFILE" && ! -L "$PROFILE" ]] || fail 5 'The selected Codespace GitHub profile is unavailable.'
ghc() {
  timeout 60 env -u GH_TOKEN -u GITHUB_TOKEN GH_CONFIG_DIR="$PROFILE" gh "$@"
}
CS_LOGIN="$(ghc api user --jq .login 2>"$TMP/gh.err")" ||
  fail 5 'The selected Codespace GitHub account is not authorized.'
ghc codespace list --limit 100 --json name,state,repository >"$TMP/machines.json" 2>"$TMP/gh.err" ||
  fail 5 'Cannot list Codespaces for the selected GitHub profile.'
if [[ -z "$CS" ]]; then
  COUNT="$(jq '[.[] | select(.state=="Available" and ((.repository // "") | endswith("/Unity-Game-Aris")))] | length' "$TMP/machines.json")"
  [[ "$COUNT" == 1 ]] || fail 5 'Choose the exact Available Codespace; multiple or zero eligible machines were found.'
  CS="$(jq -r '.[] | select(.state=="Available" and ((.repository // "") | endswith("/Unity-Game-Aris"))) | .name' "$TMP/machines.json")"
fi
[[ "$CS" =~ ^[A-Za-z0-9][A-Za-z0-9-]{2,119}$ ]] || fail 2 'Invalid Codespace name.'
jq -e --arg machine "$CS" '.[] | select(.name==$machine and .state=="Available" and ((.repository // "") | endswith("/Unity-Game-Aris")))' \
  "$TMP/machines.json" >/dev/null || fail 5 'The exact requested Codespace is not Available for this account.'
if [[ -z "$SOURCE_SHA" ]]; then
  SOURCE_SHA="$(ghc api "repos/$REPO/git/ref/heads/main" --jq .object.sha 2>"$TMP/gh.err")" ||
    fail 5 'Could not resolve the canonical reviewed source commit.'
fi
[[ "$SOURCE_SHA" =~ ^[0-9a-f]{40}$ ]] || fail 2 'Invalid reviewed source SHA.'
say "Using Codespace: $CS"

# Commands transmitted to SSH contain only public script bytes and validated
# identifiers. Unity URLs and CLI credentials are never put on command argv.
ssh_python() {
  local source_file="$1" seconds="$2" encoded
  encoded="$(base64 <"$source_file" | tr -d '\n')"
  timeout "$seconds" env -u GH_TOKEN -u GITHUB_TOKEN GH_CONFIG_DIR="$PROFILE" \
    gh codespace ssh -c "$CS" -- "python3 -c 'import base64;exec(base64.b64decode(\"$encoded\"))'"
}

say 'Step 1/4: Testing real Codespace SSH and Python...'
python3 - "$CS" >"$TMP/probe.py" <<'PY'
import sys
machine = sys.argv[1]
print("import os\nexpected=" + repr(machine) + "\nactual=os.environ.get('CODESPACE_NAME')\n"
      "assert not actual or actual==expected, 'UNITY_MACHINE_MISMATCH'\n"
      "print('AARIS_SSH_PYTHON_OK')\n")
PY
if ! ssh_python "$TMP/probe.py" 70 >"$TMP/probe.out" 2>"$TMP/ssh.err"; then
  if grep -Eqi 'permission denied|publickey|authentication|401|403' "$TMP/ssh.err"; then
    fail 5 'Codespace SSH authorization failed before Unity login started.'
  fi
  fail 6 'Codespace SSH or Python preflight failed. The existing Unity session was not replaced.'
fi
grep -Fxq AARIS_SSH_PYTHON_OK "$TMP/probe.out" || fail 6 'The exact Codespace did not return the Python verification marker.'
say 'Codespace SSH verified.'

say 'Step 2/4: Preparing verified Unity login helper...'
curl -fsSL --retry 3 --connect-timeout 25 --max-time 120 \
  "https://raw.githubusercontent.com/$REPO/$SOURCE_SHA/Tools/Codespaces/mobile_license.py" \
  -o "$TMP/remote.py" || fail 7 'Could not download the pinned Unity login helper.'
python3 - "$TMP/remote.py" "$SOURCE_SHA" >"$TMP/install.py" <<'PY'
import hashlib, pathlib, sys
source = pathlib.Path(sys.argv[1]).read_bytes()
compile(source, "reviewed-mobile-license.py", "exec")
sha, digest = sys.argv[2], hashlib.sha256(source).hexdigest()
print("""import hashlib, os, pathlib, pwd, sys, tempfile
source=sys.stdin.buffer.read()
sha=""" + repr(sha) + """
expected=""" + repr(digest) + """
assert hashlib.sha256(source).hexdigest()==expected, 'UNITY_HELPER_TRANSPORT_MISMATCH'
compile(source,'reviewed-mobile-license.py','exec')
home=pathlib.Path(pwd.getpwuid(os.geteuid()).pw_dir)
base=home/'.local/share/aaris-unity-login'
if base.is_symlink(): raise SystemExit('UNITY_UNSAFE_HELPER_PATH')
base.mkdir(parents=True,exist_ok=True,mode=0o700)
base.chmod(0o700)
folder=base/sha
if folder.is_symlink(): raise SystemExit('UNITY_UNSAFE_HELPER_PATH')
folder.mkdir(mode=0o700,exist_ok=True)
folder.chmod(0o700)
target=folder/'mobile_license.py'
if target.is_symlink(): raise SystemExit('UNITY_UNSAFE_HELPER_PATH')
if target.exists():
    if hashlib.sha256(target.read_bytes()).hexdigest()!=expected:
        raise SystemExit('UNITY_EXISTING_HELPER_SHA_MISMATCH')
else:
    fd,name=tempfile.mkstemp(prefix='.helper-',dir=folder)
    try:
        os.fchmod(fd,0o600)
        with os.fdopen(fd,'wb') as output:
            output.write(source);output.flush();os.fsync(output.fileno())
        os.replace(name,target)
    finally:
        if os.path.exists(name): os.unlink(name)
print('AARIS_UNITY_HELPER_VERIFIED')
""")
PY
if ! ssh_python "$TMP/install.py" 70 <"$TMP/remote.py" >"$TMP/install.out" 2>"$TMP/ssh.err"; then
  fail 7 'Could not install the reviewed helper on the selected Codespace.'
fi
grep -Fxq AARIS_UNITY_HELPER_VERIFIED "$TMP/install.out" || fail 7 'Remote helper integrity was not verified.'

make_remote_call() {
  local mode="$1" session="$2"
  python3 - "$SOURCE_SHA" "$CS" "$mode" "$session" "$INSECURE" >"$TMP/call.py" <<'PY'
import sys
sha,machine,mode,session,insecure=sys.argv[1:]
arguments=[mode,'--machine',machine,'--source-sha',sha]
if session: arguments+=['--session',session]
if insecure=='1': arguments+=['--insecure-storage']
print("import pathlib,pwd,os,runpy,sys\n"
      "home=pathlib.Path(pwd.getpwuid(os.geteuid()).pw_dir)\n"
      "script=home/'.local/share/aaris-unity-login'/" + repr(sha) + "/'mobile_license.py'\n"
      "sys.argv=[str(script)]+" + repr(arguments) + "\n"
      "runpy.run_path(str(script),run_name='__main__')\n")
PY
}

start_session() {
  make_remote_call session-start ''
  if ! ssh_python "$TMP/call.py" 70 >"$TMP/state.json" 2>"$TMP/ssh.err"; then
    if grep -Fxq UNITY_AUTH_IN_PROGRESS "$TMP/state.json"; then
      fail 75 'Another verified Unity login already owns this machine. Finish it, then re-run to verify.'
    fi
    fail 8 'Remote login start was not confirmed. Re-run the same command to reattach safely.'
  fi
  SESSION="$(jq -er '.session | select(test("^[0-9a-f]{32}$"))' "$TMP/state.json")" ||
    fail 8 'Remote login session identity was not verified.'
}
say 'Step 3/4: Starting or reattaching Unity login independently of SSH...'
start_session
say 'Step 4/4: Waiting for a fresh Unity-owned login link or an already active license...'
last_link=''
last_marker=''
transport_reported=0
while :; do
  make_remote_call session-status "$SESSION"
  if ! ssh_python "$TMP/call.py" 45 >"$TMP/state.next.json" 2>"$TMP/poll.err"; then
    if (( transport_reported == 0 )); then
      say 'Phone connection interrupted; the same private remote session is continuing. Reconnecting...'
      transport_reported=1
    fi
    sleep 5
    continue
  fi
  if ! jq -e --arg session "$SESSION" --arg machine "$CS" \
      '.session==$session and .machine==$machine' "$TMP/state.next.json" >/dev/null; then
    fail 9 'The remote response did not match the selected machine and login session.'
  fi
  mv "$TMP/state.next.json" "$TMP/state.json"
  transport_reported=0
  MARKER="$(jq -r '.marker // ""' "$TMP/state.json")"
  PHASE="$(jq -r '.phase // ""' "$TMP/state.json")"
  if [[ "$MARKER" != "$last_marker" ]]; then
    case "$MARKER" in
      UNITY_CLI_BOOTSTRAPPING|UNITY_CLI_READY|UNITY_AUTH_STARTED|UNITY_AUTH_VERIFIED|UNITY_AUTH_ALREADY_VALID|UNITY_PERSONAL_ACTIVATION_START)
        say "$MARKER" ;;
    esac
    last_marker="$MARKER"
  fi
  LINK_HASH="$(python3 - "$TMP/state.json" <<'PY'
import hashlib,json,sys
from urllib.parse import urlsplit
url=json.load(open(sys.argv[1])).get('auth_url')
if not url: raise SystemExit(0)
try:
    parsed=urlsplit(url);host=(parsed.hostname or '').lower()
    ok=(parsed.scheme=='https' and not parsed.username and not parsed.password
        and parsed.port in (None,443) and len(url)<4000 and not any(ord(c)<32 for c in url)
        and any(host==suffix or host.endswith('.'+suffix) for suffix in ('unity.com','unity3d.com')))
except (ValueError,TypeError): ok=False
if not ok: raise SystemExit(9)
print(hashlib.sha256(url.encode()).hexdigest())
PY
)" || fail 9 'The helper returned an invalid authorization URL.'
  if [[ -n "$LINK_HASH" && "$LINK_HASH" != "$last_link" ]]; then
    URL="$(jq -r '.auth_url' "$TMP/state.json")"
    say 'NEW UNITY LOGIN LINK RECEIVED FROM CODESPACE.'
    if command -v termux-clipboard-set >/dev/null; then
      printf '%s' "$URL" | timeout 8 termux-clipboard-set >/dev/null 2>&1 &&
        say 'Fresh link copied to Android clipboard.' || true
    fi
    OPENED=0
    if command -v termux-open-url >/dev/null; then
      if timeout 12 termux-open-url "$URL" >/dev/null 2>&1; then
        OPENED=1
        say 'Unity login requested in Android browser.'
      fi
    fi
    if (( OPENED == 0 )) && command -v am >/dev/null; then
      timeout 12 am start -a android.intent.action.VIEW -d "$URL" >/dev/null 2>&1 &&
        say 'Unity login requested using Android VIEW intent.' || true
    fi
    say 'If no browser appears, copy the NEW link below into Chrome on this phone:'
    printf '%s\n' "$URL"
    say 'Sign in, tap Allow, then return to Termux. Do not share the URL or code.'
    last_link="$LINK_HASH"
    unset URL
  fi
  case "$PHASE" in
    COMPLETE)
      jq -e '.auth_verified==true and .license_verified==true and .exit_code==0' "$TMP/state.json" >/dev/null ||
        fail 9 'The remote license verification was incomplete.'
      say 'SUCCESS: Unity login and Personal license verified on selected Codespace.'
      exit 0
      ;;
    EXPIRED)
      say 'The Unity provider expired that login session. Requesting a fresh link for this same Codespace...'
      sleep 2
      start_session
      last_link=''
      last_marker=''
      ;;
    INTERRUPTED)
      say 'The previous remote login process ended. Resuming verification on this same Codespace...'
      start_session
      last_link=''
      last_marker=''
      ;;
    FAILED)
      EXIT_CODE="$(jq -r '.exit_code // 11' "$TMP/state.json")"
      case "$EXIT_CODE:$MARKER" in
        26:*|*:UNITY_SECURE_STORAGE_UNAVAILABLE)
          say 'UNITY_SECURE_STORAGE_UNAVAILABLE'
          say 'The secure credential store is unavailable on this Codespace. No weaker storage was enabled automatically.'
          exit 26
          ;;
        14:*|*:UNITY_PERSONAL_NOT_ACTIVE_HUB_MAY_BE_REQUIRED)
          fail 14 'Unity account login succeeded, but its Personal license needs Unity Hub or entitlement review.'
          ;;
        30:*)
          fail 30 'The explicitly selected file credential store could not save authentication on this machine.'
          ;;
        75:*)
          fail 75 'Another login process owns this Codespace. No duplicate OAuth session was started.'
          ;;
        *)
          fail 12 'Unity did not verify this login session. Re-run the same command for a fresh provider session.'
          ;;
      esac
      ;;
  esac
  sleep 5
done

