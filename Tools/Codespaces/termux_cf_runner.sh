#!/data/data/com.termux/files/usr/bin/bash
# CF deployment does not depend on a live Codespace or a Codespaces quota.
# The controller owns the cross-command lock and final authenticated probe.
set -euo pipefail
umask 077
if ! command -v python3 >/dev/null 2>&1; then
  command -v pkg >/dev/null 2>&1 || { echo AARIS_CF_TERMUX_PYTHON_REQUIRED; exit 20; }
  _aaris_boot_log="$(mktemp)"
  if ! pkg install -y python >"$_aaris_boot_log" 2>&1; then
    rm -f "$_aaris_boot_log"
    echo AARIS_CF_PYTHON_INSTALL_FAILED
    exit 20
  fi
  rm -f "$_aaris_boot_log"
fi
exec python3 - "$(builtin cd -- "$(dirname -- "$0")" && pwd)" "$@" <<'AARIS_CF_PHONE_PY'
import argparse
import fcntl
import json
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import urllib.parse
import urllib.request

OP_RE = re.compile(r'[A-Za-z0-9][A-Za-z0-9_-]{7,79}\Z')
HEX40 = re.compile(r'[0-9a-f]{40}\Z')
HEX64 = re.compile(r'[0-9a-f]{64}\Z')
HEX32 = re.compile(r'[0-9a-f]{32}\Z')
GUEST = 'aaris-cf-runner'
REPOSITORY = 'sundamunda289-art/Unity-Game-Aris'

class RunnerError(Exception):
    def __init__(self, category, code=1):
        super().__init__(category)
        self.category, self.code = category, code

class Interrupted(RunnerError):
    pass

def event(name):
    if not re.fullmatch(r'[A-Z0-9_]+', name):
        raise ValueError('invalid event')
    print('AARIS_CF_' + name, flush=True)

def private_directory(path):
    path = Path(path)
    if path.is_symlink():
        raise RunnerError('UNSAFE_PRIVATE_DIRECTORY', 20)
    path.mkdir(parents=True, exist_ok=True, mode=0o700)
    if not path.is_dir():
        raise RunnerError('UNSAFE_PRIVATE_DIRECTORY', 20)
    path.chmod(0o700)
    return path

def atomic_json(path, value):
    path = Path(path)
    private_directory(path.parent)
    if path.is_symlink():
        raise RunnerError('UNSAFE_PRIVATE_OUTPUT', 20)
    fd, tmp = tempfile.mkstemp(prefix='.' + path.name + '-', dir=path.parent)
    try:
        os.fchmod(fd, 0o600)
        with os.fdopen(fd, 'w') as output:
            json.dump(value, output, sort_keys=True)
            output.write('\n')
            output.flush()
            os.fsync(output.fileno())
        os.replace(tmp, path)
    finally:
        if os.path.exists(tmp):
            os.unlink(tmp)

def guest_install_command(help_text):
    if '--name' in help_text:
        return ['proot-distro', 'install', 'ubuntu:24.04', '--name', GUEST]
    if '--override-alias' in help_text:
        return ['proot-distro', 'install', '--override-alias', GUEST, 'ubuntu']
    raise RunnerError('PROOT_VERSION_UNSUPPORTED', 21)

def safe_auth_url(text):
    try:
        parsed = urllib.parse.urlsplit(text)
        code = urllib.parse.parse_qs(parsed.query).get('user_code', [''])[0]
        if (parsed.scheme == 'https' and parsed.hostname == 'dash.cloudflare.com'
                and parsed.path == '/oauth2/device' and not parsed.username
                and not parsed.password and parsed.port in (None, 443)
                and re.fullmatch(r'[A-Za-z0-9-]{4,40}', code)):
            return 'https://dash.cloudflare.com/oauth2/device?user_code=' + urllib.parse.quote(code)
    except ValueError:
        pass
    return None

class PhoneRunner:
    def __init__(self, args, script_dir):
        self.args, self.script_dir = args, Path(script_dir)
        if not OP_RE.fullmatch(args.operation):
            raise RunnerError('INVALID_OPERATION_ID', 2)
        self.base = private_directory(os.environ.get('AARIS_CF_RUNNER_STATE', str(Path.home() / '.local/share/aaris-cf-runner')))
        self.op = private_directory(self.base / 'operations' / args.operation)
        self.child = None

    def stop_child(self):
        child = self.child
        if child is None or child.returncode is not None:
            return
        # Do not reap the group leader before stopping its descendants: the
        # unreaped leader reserves the process-group identity throughout cleanup.
        def group_running():
            for entry in Path('/proc').iterdir():
                if not entry.name.isdecimal():
                    continue
                try:
                    fields = (entry / 'stat').read_text().rsplit(')', 1)[1].split()
                    if fields[0] != 'Z' and int(fields[2]) == child.pid and int(fields[3]) == child.pid:
                        return True
                except (OSError, ValueError, IndexError):
                    continue
            return False
        try:
            os.killpg(child.pid, signal.SIGTERM)
        except ProcessLookupError:
            child.wait()
            return
        deadline = time.monotonic() + 10
        while group_running() and time.monotonic() < deadline:
            time.sleep(0.05)
        if group_running():
            try:
                os.killpg(child.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
        child.wait()

    def command(self, stage, argv, *, guest_events=False):
        event(stage + '_STARTED')
        log_path = self.op / (stage.lower() + '.private.log')
        fd = os.open(log_path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o600)
        env = dict(os.environ)
        # Unrelated provider credentials never go to package-manager/PRoot children.
        for key in list(env):
            if (key.startswith(('GH_', 'GITHUB_', 'CLOUDFLARE_', 'CF_', 'UNITY_'))
                    or key in ('NODE_OPTIONS', 'WRANGLER_HOME', 'WRANGLER_API_ENVIRONMENT')):
                env.pop(key, None)
        captured = []
        process = None
        try:
            with os.fdopen(fd, 'w', buffering=1) as log:
                self.child = subprocess.Popen(argv, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                              stdin=subprocess.DEVNULL, text=True, env=env,
                                              start_new_session=True, errors='replace')
                process = self.child
                for line in self.child.stdout:
                    log.write(line)
                    captured.append(line)
                    if sum(map(len, captured)) > 1024 * 1024:
                        captured = captured[-100:]
                    value = line.rstrip('\r\n')
                    if guest_events and re.fullmatch(r'AARIS_CF_[A-Z0-9_]+', value):
                        print(value, flush=True)
                    elif guest_events and value.startswith('AARIS_CF_AUTH_URL='):
                        url = safe_auth_url(value.partition('=')[2])
                        if url:
                            print('AARIS_CF_AUTH_URL=' + url, flush=True)
                            opener = shutil.which('termux-open-url')
                            if opener:
                                try:
                                    subprocess.run([opener, url], stdin=subprocess.DEVNULL,
                                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                                   timeout=20, check=False)
                                except (OSError, subprocess.TimeoutExpired):
                                    event('OPEN_OFFICIAL_URL_MANUALLY')
                    elif guest_events and re.fullmatch(r'AARIS_CF_AUTH_CODE=[A-Za-z0-9-]{4,40}', value):
                        print(value, flush=True)
                status = self.child.wait()
                self.child = None
        except BaseException:
            self.stop_child()
            raise
        finally:
            if process is not None and process.stdout is not None:
                process.stdout.close()
        if status:
            event(stage + '_FAILED')
            raise RunnerError(stage + '_FAILED', status if 0 < status < 256 else (128 - status if -127 < status < 0 else 1))
        event(stage + '_PASSED')
        return ''.join(captured)

    def guest_command(self, command):
        return ['proot-distro', 'login', GUEST, '--bind', str(self.base) + ':/aaris-cf-state',
                '--', *command]

    def ensure_guest(self):
        if not shutil.which('pkg'):
            raise RunnerError('TERMUX_REQUIRED', 20)
        if os.uname().machine not in ('aarch64', 'x86_64'):
            raise RunnerError('PHONE_ARCHITECTURE_UNSUPPORTED', 22)
        if not shutil.which('proot-distro'):
            self.command('LINUX_RUNNER_PACKAGE_INSTALL', ['pkg', 'install', '-y', 'proot-distro'])
        prefix = Path(os.environ.get('PREFIX', '/data/data/com.termux/files/usr'))
        root = prefix / 'var/lib/proot-distro'
        exists = ((root / 'containers' / GUEST / 'rootfs').is_dir()
                  or (root / 'installed-rootfs' / GUEST).is_dir())
        owner_path = self.base / 'guest-owner.json'
        if exists and not owner_path.is_file():
            raise RunnerError('EXISTING_RUNNER_NOT_OWNED', 23)
        if not exists:
            help_text = self.command('RUNNER_CAPABILITIES', ['proot-distro', 'install', '--help'])
            install = guest_install_command(help_text)
            atomic_json(owner_path, {'owner': 'aaris-cf-runner', 'guest': GUEST, 'install_requested': True})
            self.command('LINUX_RUNNER_INSTALL', install)
        else:
            owner = json.loads(owner_path.read_text())
            if owner.get('owner') != 'aaris-cf-runner' or owner.get('guest') != GUEST:
                raise RunnerError('EXISTING_RUNNER_NOT_OWNED', 23)
        ready_path = self.base / 'guest-ready.json'
        if not ready_path.is_file():
            self.command('LINUX_PACKAGES', self.guest_command([
                '/bin/bash', '-c',
                'set -euo pipefail; export DEBIAN_FRONTEND=noninteractive; '
                'apt-get update -qq; apt-get install -y -qq ca-certificates python3 curl tar coreutils libstdc++6 libgcc-s1; '
                'python3 -c "import sys; assert sys.version_info >= (3,11)"'
            ]))
            atomic_json(ready_path, {'guest': GUEST, 'packages_ready': True})
        self.command('LINUX_RUNNER_VERIFY', self.guest_command([
            '/usr/bin/python3', '-c',
            'import platform,sys; assert sys.version_info >= (3,11); '
            'name,version=platform.libc_ver(); assert name=="glibc" and tuple(map(int,version.split(".")[:2])) >= (2,35)'
        ]))

    def copy_source(self):
        local = self.script_dir.parent / 'Turbo/cloudflare_account_switch_linux.sh'
        target = self.op / 'linux-runner.sh'
        if target.is_symlink():
            raise RunnerError('UNSAFE_RUNNER_SCRIPT', 24)
        if local.is_file():
            source = local.read_bytes()
        else:
            url = 'https://raw.githubusercontent.com/' + REPOSITORY + '/' + self.args.source_sha + '/Tools/Turbo/cloudflare_account_switch_linux.sh'
            try:
                with urllib.request.urlopen(url, timeout=45) as response:
                    if urllib.parse.urlsplit(response.url).scheme != 'https':
                        raise RunnerError('INSECURE_SOURCE_REDIRECT', 24)
                    source = response.read(2 * 1024 * 1024)
            except (OSError, urllib.error.URLError):
                raise RunnerError('RUNNER_SOURCE_DOWNLOAD_FAILED', 24) from None
        if not source.startswith(b'#!/usr/bin/env bash') or b'AARIS_CF_LINUX_PY' not in source:
            raise RunnerError('UNSUPPORTED_CANONICAL_RUNNER', 24)
        fd, tmp = tempfile.mkstemp(prefix='.runner-', dir=self.op)
        with os.fdopen(fd, 'wb') as output:
            os.fchmod(output.fileno(), 0o600)
            output.write(source)
        os.replace(tmp, target)

    def prepare(self):
        a = self.args
        if not (a.source_sha and HEX40.fullmatch(a.source_sha)
                and a.watch_hash and HEX64.fullmatch(a.watch_hash)
                and a.probe_hash and HEX64.fullmatch(a.probe_hash) and a.output):
            raise RunnerError('INVALID_PREPARE_INPUT', 2)
        if a.account and not HEX32.fullmatch(a.account):
            raise RunnerError('INVALID_ACCOUNT_ID', 2)
        self.ensure_guest()
        self.copy_source()
        child_environment = [
            'AARIS_CF_RUNNER_STATE=/aaris-cf-state', 'AARIS_CF_OPERATION_ID=' + a.operation,
            'AARIS_SOURCE_SHA=' + a.source_sha, 'AARIS_WATCH_HASH=' + a.watch_hash,
            'AARIS_PROBE_HASH=' + a.probe_hash, 'AARIS_CF_ACCOUNT_ID=' + (a.account or ''),
            'AARIS_CF_RESUME=' + ('1' if a.resume else '0'),
        ]
        result = self.op / 'result.json'
        try:
            self.command('LINUX_DEPLOY', self.guest_command([
                '/usr/bin/env', *child_environment, '/bin/bash',
                '/aaris-cf-state/operations/' + a.operation + '/linux-runner.sh',
            ]), guest_events=True)
        except RunnerError:
            if result.is_file():
                atomic_json(a.output, json.loads(result.read_text()))
            raise
        if not result.is_file():
            raise RunnerError('DEPLOY_RECEIPT_MISSING', 25)
        value = json.loads(result.read_text())
        if (value.get('operation_id') != a.operation or value.get('source_sha') != a.source_sha
                or value.get('state') != 'CF_DEPLOYED_DO_PROBE_PENDING'):
            raise RunnerError('DEPLOY_RECEIPT_MISMATCH', 25)
        atomic_json(a.output, value)

    def commit(self):
        result = self.op / 'result.json'
        if not result.is_file():
            raise RunnerError('DEPLOY_RECEIPT_MISSING', 25)
        value = json.loads(result.read_text())
        if (value.get('operation_id') != self.args.operation
                or value.get('state') != 'CF_DEPLOYED_DO_PROBE_PENDING'):
            raise RunnerError('DEPLOY_RECEIPT_MISMATCH', 25)
        # Controller invokes only after its authenticated DO check/handoff.
        atomic_json(self.base / 'current-profile.json', {
            'operation_id': value['operation_id'], 'account_id': value['account_id'],
            'broker_url': value['broker_url'], 'source_sha': value['source_sha'],
        })
        event('PROFILE_SELECTED')

    def abort(self):
        # A local interruption cannot undo a remote request. Keep its private
        # profile/receipts for reconciliation and never revoke global auth.
        atomic_json(self.op / 'abort.json', {'operation_id': self.args.operation, 'state': 'LOCAL_ABORT_RECONCILE_REMOTE'})
        event('LOCAL_ABORT_RECEIPTS_PRESERVED')
        receipt = self.op / 'phase.json'
        if receipt.is_file() and json.loads(receipt.read_text()).get('state') in {
                'SUBDOMAIN_REGISTER_REQUESTED', 'DEPLOY_REQUESTED',
                'WATCH_SECRET_REQUESTED', 'PROBE_SECRET_REQUESTED'}:
            raise RunnerError('ABORT_REQUIRES_REMOTE_RECONCILIATION', 76)

    def execute(self):
        fd = os.open(self.base / 'phone-runner.lock', os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
        try:
            try:
                fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                raise RunnerError('ANOTHER_RUNNER_IS_ACTIVE', 75)
            getattr(self, self.args.mode or self.args.action or 'prepare')()
        finally:
            self.stop_child()
            os.close(fd)

def main():
    script_dir = sys.argv.pop(1)
    parser = argparse.ArgumentParser()
    parser.add_argument('action', nargs='?', choices=('prepare', 'commit', 'abort'))
    parser.add_argument('--mode', choices=('prepare', 'commit', 'abort'))
    parser.add_argument('--operation', required=True)
    parser.add_argument('--source-sha')
    parser.add_argument('--watch-hash')
    parser.add_argument('--probe-hash')
    parser.add_argument('--output')
    parser.add_argument('--account')
    parser.add_argument('--resume', action='store_true')
    args = parser.parse_args()
    if args.mode and args.action and args.mode != args.action:
        parser.error('conflicting modes')
    def interrupted(signum, frame):
        raise Interrupted('CANCELLED' if signum == signal.SIGINT else 'TERMINATED', 128 + signum)
    signal.signal(signal.SIGINT, interrupted)
    signal.signal(signal.SIGTERM, interrupted)
    try:
        PhoneRunner(args, script_dir).execute()
        return 0
    except RunnerError as exc:
        event(exc.category)
        return exc.code
    except Exception:
        event('PRIVATE_RUNNER_FAILED')
        return 1

if __name__ == '__main__':
    sys.exit(main())
AARIS_CF_PHONE_PY
