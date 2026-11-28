#!/usr/bin/env bash
# Private Linux deployment runner, including the dedicated Termux PRoot guest.
# No Codespace, GitHub credential, Unity process, or project is required.
set -euo pipefail
umask 077
exec python3 - "$@" <<'AARIS_CF_LINUX_PY'
import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

NODE_VERSION = '22.16.0'
# Reviewed https://nodejs.org/dist/v22.16.0/SHASUMS256.txt, not runtime trust.
NODE_RELEASES = {
    'x86_64': ('x64', 'fb870226119d47378fa9c92c4535389c72dae14fcc7b47e6fdcc82c43de5a547'),
    'aarch64': ('arm64', '1725602e9fb150eb8b8220a899085190e1c04d1a5f3862b01c3dc1dfce0157f9'),
}
REPOSITORY = 'sundamunda289-art/Unity-Game-Aris'
WORKER_NAME = 'aaris-turbo-terminal'
WRANGLER_VERSION = '4.119.0'
OP_RE = re.compile(r'[A-Za-z0-9][A-Za-z0-9_-]{7,79}\Z')
HEX40 = re.compile(r'[0-9a-f]{40}\Z')
HEX64 = re.compile(r'[0-9a-f]{64}\Z')
HEX32 = re.compile(r'[0-9a-f]{32}\Z')
ANSI = re.compile(r'\x1b\[[0-?]*[ -/]*[@-~]')

class RunnerError(Exception):
    def __init__(self, category, code=1):
        super().__init__(category)
        self.category, self.code = category, code

class Interrupted(RunnerError):
    pass

def event(name):
    if not re.fullmatch(r'[A-Z0-9_]+', name):
        raise ValueError('invalid internal event')
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

def atomic_json(path, data):
    path = Path(path)
    private_directory(path.parent)
    if path.is_symlink():
        raise RunnerError('UNSAFE_RECEIPT_FILE', 20)
    fd, tmp = tempfile.mkstemp(prefix='.' + path.name + '-', dir=path.parent)
    try:
        os.fchmod(fd, 0o600)
        with os.fdopen(fd, 'w', encoding='utf-8') as out:
            json.dump(data, out, sort_keys=True)
            out.write('\n')
            out.flush()
            os.fsync(out.fileno())
        os.replace(tmp, path)
        directory_fd = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(directory_fd)
        finally:
            os.close(directory_fd)
    finally:
        if os.path.exists(tmp):
            os.unlink(tmp)

def json_document(raw, expected_type):
    decoder = json.JSONDecoder()
    for index, char in enumerate(raw):
        if char not in '[{':
            continue
        try:
            value, end = decoder.raw_decode(raw[index:])
        except ValueError:
            continue
        if isinstance(value, expected_type) and not raw[index + end:].strip():
            return value
    raise RunnerError('INVALID_WRANGLER_RESPONSE', 21)

def device_authorization(raw):
    clean = ANSI.sub('', raw)
    for candidate in re.findall(r'https://dash\.cloudflare\.com/oauth2/device\?[^\s<>"\x1b]+', clean):
        parsed = urllib.parse.urlsplit(candidate)
        code = urllib.parse.parse_qs(parsed.query).get('user_code', [''])[0]
        if parsed.hostname == 'dash.cloudflare.com' and re.fullmatch(r'[A-Za-z0-9-]{4,40}', code):
            return 'https://dash.cloudflare.com/oauth2/device?user_code=' + urllib.parse.quote(code), code
    return None

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, newurl):
        raise RunnerError('UNEXPECTED_HTTP_REDIRECT', 22)

def request_json(url, *, token=None, method='GET', body=None):
    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme != 'https' or parsed.username or parsed.password:
        raise RunnerError('UNSAFE_HTTPS_ENDPOINT', 22)
    headers = {'Accept': 'application/json', 'User-Agent': 'aaris-cf-switch/1'}
    if token:
        if parsed.hostname != 'api.cloudflare.com':
            raise RunnerError('UNSAFE_CREDENTIAL_DESTINATION', 22)
        headers['Authorization'] = 'Bearer ' + token
    encoded = None
    if body is not None:
        encoded = json.dumps(body).encode()
        headers['Content-Type'] = 'application/json'
    req = urllib.request.Request(url, encoded, headers, method=method)
    try:
        with urllib.request.build_opener(NoRedirect()).open(req, timeout=30) as response:
            return json.load(response)
    except urllib.error.HTTPError as exc:
        try:
            value = json.loads(exc.read(65536))
        except ValueError:
            value = {}
        value['_http_status'] = exc.code
        return value
    except (OSError, ValueError, urllib.error.URLError):
        raise RunnerError('NETWORK_REQUEST_FAILED', 23) from None

class LinuxRunner:
    def __init__(self, env):
        self.operation = env.get('AARIS_CF_OPERATION_ID', env.get('AARIS_CF_SESSION_ID', ''))
        self.source = env.get('AARIS_SOURCE_SHA', '')
        self.watch_hash = env.get('AARIS_WATCH_HASH', '')
        self.probe_hash = env.get('AARIS_PROBE_HASH', '')
        self.account = env.get('AARIS_CF_ACCOUNT_ID', '')
        if not (OP_RE.fullmatch(self.operation) and HEX40.fullmatch(self.source)
                and HEX64.fullmatch(self.watch_hash) and HEX64.fullmatch(self.probe_hash)):
            raise RunnerError('INVALID_RUNNER_INPUT', 2)
        if self.account and not HEX32.fullmatch(self.account):
            raise RunnerError('INVALID_ACCOUNT_ID', 2)
        self.base = private_directory(env.get('AARIS_CF_RUNNER_STATE', str(Path.home() / '.local/share/aaris-cf-runner')))
        self.op = private_directory(self.base / 'operations' / self.operation)
        self.profile = private_directory(self.op / 'profile')
        self.logs = private_directory(self.op / 'private-logs')
        self.result, self.receipt = self.op / 'result.json', self.op / 'phase.json'
        self.plan = {'operation_id': self.operation, 'source_sha': self.source,
                     'watch_hash': self.watch_hash, 'probe_hash': self.probe_hash}
        plan_path = self.op / 'plan.json'
        if plan_path.exists() and json.loads(plan_path.read_text()) != self.plan:
            raise RunnerError('OPERATION_INPUT_CHANGED', 24)
        atomic_json(plan_path, self.plan)
        self.child, self.tmp, self.broker_url = None, None, None
        self.resume = env.get('AARIS_CF_RESUME', '0') == '1'
        self.environment = dict(env)
        # 4.119.0 has no WRANGLER_HOME override. Legacy ~/.wrangler wins over
        # XDG, so refuse it instead of touching an unrelated current login.
        if (Path.home() / '.wrangler').exists():
            raise RunnerError('LEGACY_WRANGLER_PROFILE_REQUIRES_PRIVATE_RUNNER', 25)
        for name in list(self.environment):
            if name.startswith(('GH_', 'GITHUB_', 'UNITY_')):
                self.environment.pop(name, None)
        for name in ('CLOUDFLARE_API_TOKEN', 'CF_API_TOKEN', 'CLOUDFLARE_API_KEY', 'CF_API_KEY',
                     'CLOUDFLARE_EMAIL', 'CF_EMAIL', 'CLOUDFLARE_ACCOUNT_ID', 'CF_ACCOUNT_ID',
                     'CLOUDFLARE_API_BASE_URL', 'WRANGLER_API_ENVIRONMENT', 'CLOUDFLARE_PROFILE',
                     'CF_PROFILE', 'NODE_OPTIONS', 'WRANGLER_HOME'):
            self.environment.pop(name, None)
        self.environment.update({'XDG_CONFIG_HOME': str(self.profile),
                                 'XDG_CACHE_HOME': str(private_directory(self.base / 'cache')),
                                 'WRANGLER_LOG_PATH': str(self.logs / 'wrangler.log'),
                                 'WRANGLER_SEND_METRICS': 'false', 'NO_COLOR': '1',
                                 'CI': 'true', 'npm_config_update_notifier': 'false'})

    def phase(self, state, **fields):
        atomic_json(self.receipt, {**self.plan, 'state': state, 'account_id': self.account,
                                  'broker_url': self.broker_url, **fields})

    def stop_child(self):
        child = self.child
        if child is None or child.returncode is not None:
            return
        # Keep the group leader unreaped until all owned group members stop.
        # poll() here could reap an exited leader and miss a live descendant.
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

    def run(self, stage, argv, *, stdin=None, oauth=False):
        event(stage + '_STARTED')
        logfile = self.logs / (stage.lower() + '.log')
        fd = os.open(logfile, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o600)
        seen_auth, captured = set(), bytearray()
        process = None
        try:
            with os.fdopen(fd, 'wb', buffering=0) as log:
                self.child = subprocess.Popen(argv, cwd=self.tmp, env=self.environment,
                                              stdin=subprocess.PIPE if stdin is not None else subprocess.DEVNULL,
                                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                              start_new_session=True)
                process = self.child
                if stdin is not None:
                    self.child.stdin.write(stdin)
                    self.child.stdin.close()
                while True:
                    chunk = os.read(self.child.stdout.fileno(), 8192)
                    if not chunk:
                        break
                    log.write(chunk)
                    captured.extend(chunk)
                    if len(captured) > 2 * 1024 * 1024:
                        del captured[:-1024 * 1024]
                    if oauth:
                        details = device_authorization(captured.decode('utf-8', 'replace'))
                        if details and details[1] not in seen_auth:
                            seen_auth.add(details[1])
                            # Phone-only, never included in shareable event tails.
                            print('AARIS_CF_AUTH_URL=' + details[0], flush=True)
                            print('AARIS_CF_AUTH_CODE=' + details[1], flush=True)
                status = self.child.wait()
                self.child = None
        except BaseException:
            self.stop_child()
            raise
        finally:
            if process is not None:
                if process.stdout is not None:
                    process.stdout.close()
                if process.stdin is not None:
                    process.stdin.close()
        if status:
            event(stage + '_FAILED')
            raise RunnerError(stage + '_FAILED', status if 0 < status < 256 else (128 - status if -127 < status < 0 else 1))
        event(stage + '_PASSED')
        return captured.decode('utf-8', 'replace')

    def download(self, url, target, expected=None):
        for attempt in range(3):
            try:
                with urllib.request.urlopen(url, timeout=45) as response, open(target, 'wb') as out:
                    if urllib.parse.urlsplit(response.url).scheme != 'https':
                        raise RunnerError('INSECURE_DOWNLOAD_REDIRECT', 26)
                    shutil.copyfileobj(response, out)
                if expected and hashlib.sha256(Path(target).read_bytes()).hexdigest() != expected:
                    raise RunnerError('DOWNLOAD_CHECKSUM_FAILED', 26)
                return
            except (OSError, urllib.error.URLError):
                if attempt == 2:
                    raise RunnerError('DOWNLOAD_NETWORK_FAILED', 23) from None
                event('DOWNLOAD_RETRY')
                time.sleep(2 ** (attempt + 1))

    def node(self):
        architecture = os.uname().machine
        if architecture not in NODE_RELEASES:
            raise RunnerError('LINUX_ARCHITECTURE_UNSUPPORTED', 27)
        arch, checksum = NODE_RELEASES[architecture]
        root = self.base / ('node-' + NODE_VERSION + '-' + arch)
        if not (root / 'bin/node').is_file():
            archive = self.tmp / 'node.tar.gz'
            event('NODE_DOWNLOAD_STARTED')
            self.download('https://nodejs.org/dist/v' + NODE_VERSION + '/node-v' + NODE_VERSION + '-linux-' + arch + '.tar.gz', archive, checksum)
            with tarfile.open(archive) as package:
                package.extractall(self.tmp / 'unpack', filter='data')
            staging = self.base / ('.node-' + self.operation)
            if staging.exists():
                shutil.rmtree(staging)
            shutil.move(str(self.tmp / 'unpack' / ('node-v' + NODE_VERSION + '-linux-' + arch)), staging)
            os.replace(staging, root)
            event('NODE_SHA256_VERIFIED')
        self.environment['PATH'] = str(root / 'bin') + ':' + self.environment.get('PATH', '/usr/bin:/bin')
        self.wrangler = [str(root / 'bin/npx'), '--yes', 'wrangler@' + WRANGLER_VERSION]
        version = self.run('NODE_VERIFY', [str(root / 'bin/node'), '--version']).strip()
        if version != 'v' + NODE_VERSION:
            raise RunnerError('NODE_VERSION_MISMATCH', 27)
        self.run('WRANGLER_VERIFY', self.wrangler + ['--version'])

    def auth(self):
        auth_path = self.profile / '.wrangler/config/default.toml'
        if not (self.resume and auth_path.is_file()):
            self.run('BROWSER_LOGIN', self.wrangler + ['login', '--device', '--browser=false'], oauth=True)
        identity = json_document(self.run('IDENTITY_VERIFY', self.wrangler + ['whoami', '--json']), dict)
        accounts = [a for a in identity.get('accounts', []) if isinstance(a, dict) and HEX32.fullmatch(str(a.get('id', '')))]
        if not identity.get('loggedIn') or not accounts:
            raise RunnerError('NO_AUTHORIZED_CLOUDFLARE_ACCOUNT', 28)
        if self.account:
            if self.account not in {a['id'] for a in accounts}:
                raise RunnerError('ACCOUNT_NOT_AUTHORIZED', 28)
        elif len(accounts) == 1:
            self.account = accounts[0]['id']
        else:
            options = [{'id': a['id'], 'name': re.sub(r'[^A-Za-z0-9 ._@+-]', '?', str(a.get('name', '')))[:60]} for a in accounts]
            atomic_json(self.result, {**self.plan, 'state': 'CF_ACCOUNT_SELECTION_REQUIRED', 'account_options': options})
            raise RunnerError('ACCOUNT_SELECTION_REQUIRED', 42)
        account_path = self.op / 'account.json'
        if account_path.exists() and json.loads(account_path.read_text()).get('account_id') != self.account:
            raise RunnerError('OPERATION_ACCOUNT_CHANGED', 24)
        atomic_json(account_path, {'account_id': self.account})
        self.environment['CLOUDFLARE_ACCOUNT_ID'] = self.account
        import tomllib
        if auth_path.is_symlink():
            raise RunnerError('UNSAFE_AUTH_PROFILE', 29)
        auth_path.chmod(0o600)
        self.oauth_token = tomllib.loads(auth_path.read_text()).get('oauth_token')
        if not isinstance(self.oauth_token, str) or not self.oauth_token:
            raise RunnerError('STAGED_OAUTH_CREDENTIAL_UNAVAILABLE', 29)

    def api(self, suffix, **kwargs):
        return request_json('https://api.cloudflare.com/client/v4/accounts/' + self.account + suffix,
                            token=self.oauth_token, **kwargs)

    def resolve_broker(self):
        data = self.api('/workers/subdomain')
        errors = {e.get('code') for e in data.get('errors', []) if isinstance(e, dict)}
        if not data.get('success') and 10007 in errors:
            self.phase('SUBDOMAIN_REGISTER_REQUESTED')
            self.api('/workers/subdomain', method='PUT', body={'subdomain': 'aaris-' + self.account[:12]})
            data = self.api('/workers/subdomain')
        subdomain = (data.get('result') or {}).get('subdomain')
        if not data.get('success') or not isinstance(subdomain, str) or not re.fullmatch(r'[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?', subdomain):
            raise RunnerError('WORKERS_SUBDOMAIN_NOT_READY', 30)
        self.broker_url = 'https://' + WORKER_NAME + '.' + subdomain + '.workers.dev'

    def health_matches(self):
        try:
            data = request_json(self.broker_url + '/health')
        except Interrupted:
            raise
        except RunnerError:
            return False
        return (data.get('service') == 'aaris-turbo' and data.get('status') == 'ready'
                and data.get('transaction_protocol') == 1
                and data.get('deployment_operation') == self.operation
                and data.get('source_sha') == self.source)

    def prepare(self):
        self.lock_fd = os.open(self.base / 'linux-runner.lock', os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
        try:
            fcntl.flock(self.lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            os.close(self.lock_fd)
            raise RunnerError('ANOTHER_RUNNER_IS_ACTIVE', 75)
        self.tmp = Path(tempfile.mkdtemp(prefix='aaris-cf-' + self.operation + '-', dir='/tmp'))
        try:
            self.node()
            for name in ('worker.mjs', 'watch_page.mjs', 'wrangler.jsonc'):
                self.download('https://raw.githubusercontent.com/' + REPOSITORY + '/' + self.source + '/Tools/Turbo/' + name, self.tmp / name)
            config = json.loads((self.tmp / 'wrangler.jsonc').read_text())
            if config.get('name') != WORKER_NAME or config.get('main') != './worker.mjs':
                raise RunnerError('CANONICAL_WORKER_CONFIG_MISMATCH', 31)
            self.run('DEPLOY_PREFLIGHT', self.wrangler + ['deploy', '--dry-run', '--config', str(self.tmp / 'wrangler.jsonc')])
            self.auth()
            self.resolve_broker()
            previous = json.loads(self.receipt.read_text()) if self.receipt.exists() else {}
            deploy_started = previous.get('state') in {
                'DEPLOY_REQUESTED', 'DEPLOY_CONFIRMED', 'WATCH_SECRET_REQUESTED',
                'PROBE_SECRET_REQUESTED', 'CF_DEPLOYED_DO_PROBE_PENDING'}
            already_deployed = (self.resume or deploy_started) and self.health_matches()
            # A failed later step must not let an old operation overwrite a
            # newer deployment, or blindly replay a write whose outcome is lost.
            if deploy_started and not already_deployed:
                raise RunnerError('DEPLOY_OUTCOME_UNKNOWN_RETRY_STATUS', 76)
            if not already_deployed:
                self.phase('DEPLOY_REQUESTED')
                try:
                    self.run('DEPLOY', self.wrangler + ['deploy', '--config', str(self.tmp / 'wrangler.jsonc'),
                             '--keep-vars', '--var', 'AARIS_DEPLOY_OPERATION:' + self.operation,
                             '--var', 'AARIS_SOURCE_SHA:' + self.source])
                except Interrupted:
                    raise
                except RunnerError:
                    if not self.health_matches():
                        raise RunnerError('DEPLOY_OUTCOME_UNKNOWN_RETRY_STATUS', 76) from None
            for attempt in range(18):
                if self.health_matches():
                    break
                if attempt == 17:
                    raise RunnerError('DEPLOY_OUTCOME_UNKNOWN_RETRY_STATUS', 76)
                event('HTTPS_PROPAGATION_PENDING')
                time.sleep(10)
            self.phase('DEPLOY_CONFIRMED')
            secrets = json_document(self.run('SECRET_NAMES_VERIFY', self.wrangler + ['secret', 'list', '--format', 'json', '--config', str(self.tmp / 'wrangler.jsonc')]), list)
            names = {item.get('name') for item in secrets if isinstance(item, dict)}
            watch_installed = bool(previous.get('watch_key_installed')) and 'WATCH_TOKEN_HASH' in names
            if 'WATCH_TOKEN_HASH' not in names:
                self.phase('WATCH_SECRET_REQUESTED')
                self.run('WATCH_KEY_INSTALL', self.wrangler + ['secret', 'put', 'WATCH_TOKEN_HASH', '--config', str(self.tmp / 'wrangler.jsonc')], stdin=self.watch_hash.encode())
                watch_installed = True
            else:
                event('EXISTING_WATCH_KEY_PRESERVED')
            self.phase('PROBE_SECRET_REQUESTED', watch_key_installed=watch_installed)
            self.run('PROBE_KEY_INSTALL', self.wrangler + ['secret', 'put', 'BROKER_PROBE_HASH', '--config', str(self.tmp / 'wrangler.jsonc')], stdin=self.probe_hash.encode())
            result = {'operation_id': self.operation, 'source_sha': self.source, 'account_id': self.account,
                      'broker_url': self.broker_url, 'state': 'CF_DEPLOYED_DO_PROBE_PENDING',
                      'watch_key_installed': watch_installed, 'runner': 'termux-proot', 'transaction_protocol': 1}
            atomic_json(self.result, result)
            self.phase('CF_DEPLOYED_DO_PROBE_PENDING', watch_key_installed=watch_installed)
            event('DEPLOYED_AUTHENTICATED_STORAGE_CHECK_REQUIRED')
            return result
        finally:
            self.stop_child()
            if self.tmp:
                shutil.rmtree(self.tmp, ignore_errors=True)
            os.close(self.lock_fd)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--node-info', choices=list(NODE_RELEASES))
    args = parser.parse_args()
    if args.node_info:
        arch, checksum = NODE_RELEASES[args.node_info]
        print(json.dumps({'version': NODE_VERSION, 'architecture': arch, 'sha256': checksum}))
        return 0
    def interrupted(signum, frame):
        raise Interrupted('CANCELLED' if signum == signal.SIGINT else 'TERMINATED', 128 + signum)
    signal.signal(signal.SIGINT, interrupted)
    signal.signal(signal.SIGTERM, interrupted)
    try:
        LinuxRunner(os.environ).prepare()
        return 0
    except RunnerError as exc:
        event(exc.category)
        return exc.code
    except Exception:
        event('PRIVATE_RUNNER_FAILED')
        return 1

if __name__ == '__main__':
    sys.exit(main())
AARIS_CF_LINUX_PY

