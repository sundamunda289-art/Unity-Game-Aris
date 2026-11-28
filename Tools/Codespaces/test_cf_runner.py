#!/usr/bin/env python3
"""Offline fault-injection tests; no Cloudflare/GitHub account or network calls."""
import argparse
import contextlib
import fcntl
import io
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import threading
import time
import types
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]

def embedded_module(path, marker, name):
    text = path.read_text()
    source = text.split("<<'" + marker + "'\n", 1)[1].rsplit('\n' + marker, 1)[0]
    module = types.ModuleType(name)
    exec(compile(source, str(path), 'exec'), module.__dict__)
    return module

LINUX = embedded_module(ROOT / 'Tools/Turbo/cloudflare_account_switch_linux.sh', 'AARIS_CF_LINUX_PY', 'linux_runner_under_test')
PHONE = embedded_module(ROOT / 'Tools/Codespaces/termux_cf_runner.sh', 'AARIS_CF_PHONE_PY', 'phone_runner_under_test')

class Fixture(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='aaris-cf-runner-test-', dir='/tmp')
        self.root = Path(self.temporary.name)
        self.home = self.root / 'home'
        self.home.mkdir()
        self.home_patch = mock.patch.object(Path, 'home', return_value=self.home)
        self.home_patch.start()
        self.base = self.root / 'state'
        self.environment = {
            'AARIS_CF_RUNNER_STATE': str(self.base),
            'AARIS_CF_OPERATION_ID': 'operation_test_0123',
            'AARIS_SOURCE_SHA': 'a' * 40,
            'AARIS_WATCH_HASH': 'b' * 64,
            'AARIS_PROBE_HASH': 'c' * 64,
            'PATH': os.environ.get('PATH', '/usr/bin:/bin'),
        }

    def tearDown(self):
        self.home_patch.stop()
        self.temporary.cleanup()

    def phone(self, **changes):
        values = dict(operation='operation_test_0123', source_sha='a' * 40,
                      watch_hash='b' * 64, probe_hash='c' * 64, account=None,
                      output=str(self.root / 'output.json'), mode=None, action=None,
                      resume=False)
        values.update(changes)
        with mock.patch.dict(os.environ, {'AARIS_CF_RUNNER_STATE': str(self.base)}):
            return PHONE.PhoneRunner(argparse.Namespace(**values), ROOT / 'Tools/Codespaces')

    def fake_linux(self, *, existing_watch=True, resumed=False, health=True, deploy_failure=None):
        environment = dict(self.environment)
        if resumed:
            environment['AARIS_CF_RESUME'] = '1'
        runner = LINUX.LinuxRunner(environment)
        runner.calls = []
        runner.node = lambda: setattr(runner, 'wrangler', ['mock-wrangler'])
        def download(url, target, expected=None):
            value = {'name': 'aaris-turbo-terminal', 'main': './worker.mjs'}
            target.write_text(json.dumps(value) if target.name == 'wrangler.jsonc' else '// fixture')
        runner.download = download
        runner.auth = lambda: setattr(runner, 'account', 'd' * 32)
        runner.resolve_broker = lambda: setattr(runner, 'broker_url', 'https://aaris-turbo-terminal.example.workers.dev')
        runner.health_matches = lambda: health
        def run(stage, argv, **kwargs):
            runner.calls.append((stage, argv, kwargs))
            if stage == 'DEPLOY':
                self.assertEqual(json.loads(runner.receipt.read_text())['state'], 'DEPLOY_REQUESTED')
                if deploy_failure:
                    raise deploy_failure
            if stage == 'SECRET_NAMES_VERIFY':
                return json.dumps([{'name': 'WATCH_TOKEN_HASH'}] if existing_watch else [])
            return ''
        runner.run = run
        return runner

class DeploymentTests(Fixture):
    def test_existing_watch_key_is_preserved(self):
        runner = self.fake_linux()
        result = runner.prepare()
        self.assertFalse(result['watch_key_installed'])
        self.assertNotIn('WATCH_KEY_INSTALL', [call[0] for call in runner.calls])
        self.assertIn('PROBE_KEY_INSTALL', [call[0] for call in runner.calls])
        self.assertEqual(result['state'], 'CF_DEPLOYED_DO_PROBE_PENDING')
        self.assertFalse((self.base / 'current-profile.json').exists())

    def test_new_worker_gets_watch_and_probe_hashes_via_stdin(self):
        runner = self.fake_linux(existing_watch=False)
        result = runner.prepare()
        self.assertTrue(result['watch_key_installed'])
        secrets = [call for call in runner.calls if call[0].endswith('KEY_INSTALL')]
        self.assertEqual(secrets[0][2]['stdin'], b'b' * 64)
        self.assertEqual(secrets[1][2]['stdin'], b'c' * 64)
        self.assertNotIn('b' * 64, ' '.join(secrets[0][1]))
        self.assertNotIn('c' * 64, ' '.join(secrets[1][1]))

    def test_unknown_deploy_does_not_retry_or_change_secrets(self):
        runner = self.fake_linux(health=False, deploy_failure=LINUX.RunnerError('NETWORK', 1))
        with self.assertRaises(LINUX.RunnerError) as raised:
            runner.prepare()
        self.assertEqual(raised.exception.code, 76)
        self.assertEqual(sum(c[0] == 'DEPLOY' for c in runner.calls), 1)
        self.assertFalse(any('KEY_INSTALL' in c[0] for c in runner.calls))
        self.assertEqual(json.loads(runner.receipt.read_text())['state'], 'DEPLOY_REQUESTED')

    def test_resuming_unknown_deploy_checks_receipt_before_redeploy(self):
        runner = self.fake_linux(resumed=True, health=False)
        runner.phase('DEPLOY_REQUESTED')
        with self.assertRaises(LINUX.RunnerError) as raised:
            runner.prepare()
        self.assertEqual(raised.exception.code, 76)
        self.assertNotIn('DEPLOY', [c[0] for c in runner.calls])

    def test_confirmed_remote_deploy_resumes_without_redeploy(self):
        runner = self.fake_linux(resumed=True, health=True)
        runner.phase('DEPLOY_REQUESTED')
        runner.prepare()
        self.assertNotIn('DEPLOY', [c[0] for c in runner.calls])

    def test_stale_confirmed_operation_never_overwrites_changed_deployment(self):
        for phase in ('DEPLOY_CONFIRMED', 'WATCH_SECRET_REQUESTED',
                      'PROBE_SECRET_REQUESTED', 'CF_DEPLOYED_DO_PROBE_PENDING'):
            with self.subTest(phase=phase):
                runner = self.fake_linux(resumed=True, health=False)
                runner.phase(phase)
                with self.assertRaises(LINUX.RunnerError) as raised:
                    runner.prepare()
                self.assertEqual(raised.exception.code, 76)
                self.assertNotIn('DEPLOY', [call[0] for call in runner.calls])
                self.assertFalse(any('KEY_INSTALL' in call[0] for call in runner.calls))

    def test_resume_preserves_successful_watch_key_receipt(self):
        runner = self.fake_linux(resumed=True, health=True)
        runner.phase('PROBE_SECRET_REQUESTED', watch_key_installed=True)
        result = runner.prepare()
        self.assertTrue(result['watch_key_installed'])
        self.assertNotIn('WATCH_KEY_INSTALL', [c[0] for c in runner.calls])

    def test_ctrl_c_is_not_swallowed_by_deploy_reconciliation(self):
        runner = self.fake_linux(deploy_failure=LINUX.Interrupted('CANCELLED', 130))
        with self.assertRaises(LINUX.Interrupted) as raised:
            runner.prepare()
        self.assertEqual(raised.exception.code, 130)
        self.assertNotIn('PROBE_KEY_INSTALL', [c[0] for c in runner.calls])
        self.assertEqual(json.loads(runner.receipt.read_text())['state'], 'DEPLOY_REQUESTED')

    def test_health_requires_exact_operation_and_source(self):
        runner = self.fake_linux()
        runner.broker_url = 'https://aaris-turbo-terminal.example.workers.dev'
        original = LINUX.LinuxRunner.health_matches.__get__(runner, LINUX.LinuxRunner)
        valid = dict(service='aaris-turbo', status='ready', transaction_protocol=1,
                     deployment_operation=runner.operation, source_sha=runner.source)
        with mock.patch.object(LINUX, 'request_json', return_value=valid):
            self.assertTrue(original())
        for key in ('deployment_operation', 'source_sha', 'transaction_protocol'):
            wrong = {**valid, key: 'wrong'}
            with mock.patch.object(LINUX, 'request_json', return_value=wrong):
                self.assertFalse(original())

    def test_operation_identity_cannot_change_on_resume(self):
        LINUX.LinuxRunner(self.environment)
        changed = {**self.environment, 'AARIS_SOURCE_SHA': 'f' * 40}
        with self.assertRaises(LINUX.RunnerError):
            LINUX.LinuxRunner(changed)

    def test_hashes_are_pinned_for_both_cpu_architectures(self):
        self.assertEqual(set(LINUX.NODE_RELEASES), {'x86_64', 'aarch64'})
        self.assertEqual(LINUX.NODE_RELEASES['aarch64'][1], '1725602e9fb150eb8b8220a899085190e1c04d1a5f3862b01c3dc1dfce0157f9')
        self.assertEqual(LINUX.NODE_RELEASES['x86_64'][1], 'fb870226119d47378fa9c92c4535389c72dae14fcc7b47e6fdcc82c43de5a547')

class PrivacyTests(Fixture):
    def test_resuming_operation_cannot_change_its_authorized_account(self):
        env = {**self.environment, 'AARIS_CF_RESUME': '1', 'AARIS_CF_ACCOUNT_ID': 'd' * 32}
        runner = LINUX.LinuxRunner(env)
        auth_path = runner.profile / '.wrangler/config/default.toml'
        auth_path.parent.mkdir(parents=True)
        auth_path.write_text('oauth_token = "PRIVATE_TEST_TOKEN"\n')
        identity = json.dumps({'loggedIn': True, 'accounts': [{'id': 'd' * 32}, {'id': 'e' * 32}]})
        runner.wrangler = ['mock-wrangler']
        runner.run = lambda *args, **kwargs: identity
        runner.auth()
        other = LINUX.LinuxRunner({**env, 'AARIS_CF_ACCOUNT_ID': 'e' * 32})
        other.wrangler = ['mock-wrangler']
        other.run = lambda *args, **kwargs: identity
        with self.assertRaises(LINUX.RunnerError) as raised:
            other.auth()
        self.assertEqual(raised.exception.category, 'OPERATION_ACCOUNT_CHANGED')
        self.assertEqual(json.loads((runner.op / 'account.json').read_text())['account_id'], 'd' * 32)

    def test_unrelated_auth_environment_is_not_inherited(self):
        env = {**self.environment, 'GH_TOKEN': 'gh-secret', 'GITHUB_TOKEN': 'repo-secret',
               'UNITY_PASSWORD': 'unity-secret', 'CLOUDFLARE_API_TOKEN': 'cf-secret',
               'CF_API_KEY': 'api-secret', 'CLOUDFLARE_API_BASE_URL': 'https://untrusted.invalid',
               'NODE_OPTIONS': '--require=bad.js'}
        runner = LINUX.LinuxRunner(env)
        for key in ('GH_TOKEN', 'GITHUB_TOKEN', 'UNITY_PASSWORD', 'CLOUDFLARE_API_TOKEN',
                    'CF_API_KEY', 'CLOUDFLARE_API_BASE_URL', 'NODE_OPTIONS'):
            self.assertNotIn(key, runner.environment)
        self.assertEqual(runner.environment['XDG_CONFIG_HOME'], str(runner.profile))

    def test_legacy_profile_is_never_logged_out_or_overwritten(self):
        legacy = self.home / '.wrangler'
        legacy.mkdir()
        saved = legacy / 'auth'
        saved.write_text('old-current-auth')
        with self.assertRaises(LINUX.RunnerError) as raised:
            LINUX.LinuxRunner(self.environment)
        self.assertIn('LEGACY_WRANGLER', raised.exception.category)
        self.assertEqual(saved.read_text(), 'old-current-auth')

    def test_raw_tool_output_is_private(self):
        runner = LINUX.LinuxRunner(self.environment)
        runner.tmp = self.root
        stream = io.StringIO()
        with contextlib.redirect_stdout(stream):
            raw = runner.run('PROBE', [sys.executable, '-c', "print('oauth_token=SECRET_SENTINEL')"])
        self.assertIn('SECRET_SENTINEL', raw)
        self.assertNotIn('SECRET_SENTINEL', stream.getvalue())
        self.assertEqual((runner.logs / 'probe.log').stat().st_mode & 0o777, 0o600)

    def test_phone_filters_raw_output_and_provider_credentials(self):
        runner = self.phone()
        stream = io.StringIO()
        command = [sys.executable, '-c',
                   'import os; print("SECRET_SENTINEL"); '
                   'print("AARIS_CF_NODE_VERIFY_PASSED"); '
                   'print("LEAK=" + str(os.environ.get("GH_TOKEN"))); '
                   'print("CF=" + str(os.environ.get("CLOUDFLARE_API_TOKEN")))']
        with mock.patch.dict(os.environ, {'GH_TOKEN': 'GH_SECRET', 'CLOUDFLARE_API_TOKEN': 'CF_SECRET'}):
            with contextlib.redirect_stdout(stream):
                raw = runner.command('TEST', command, guest_events=True)
        self.assertIn('LEAK=None', raw)
        self.assertIn('CF=None', raw)
        self.assertNotIn('SECRET_SENTINEL', stream.getvalue())
        self.assertIn('AARIS_CF_NODE_VERIFY_PASSED', stream.getvalue())

    def test_auth_url_only_allows_official_https_device_flow(self):
        good = 'https://dash.cloudflare.com/oauth2/device?user_code=AbCd1234'
        self.assertEqual(PHONE.safe_auth_url(good), good)
        for bad in ('http://dash.cloudflare.com/oauth2/device?user_code=AbCd1234',
                    'https://dash.cloudflare.com.evil.invalid/oauth2/device?user_code=AbCd1234',
                    'https://user@dash.cloudflare.com/oauth2/device?user_code=AbCd1234',
                    'https://dash.cloudflare.com:444/oauth2/device?user_code=AbCd1234',
                    'https://dash.cloudflare.com/oauth2/device?user_code=x%0Asecret'):
            self.assertIsNone(PHONE.safe_auth_url(bad))
        self.assertEqual(LINUX.device_authorization('\x1b[31m' + good + '\x1b[0m')[1], 'AbCd1234')

    def test_receipt_rejects_symlink_target(self):
        target = self.root / 'real'
        target.write_text('preserve')
        link = self.root / 'link'
        link.symlink_to(target)
        with self.assertRaises(LINUX.RunnerError):
            LINUX.atomic_json(link, {'state': 'new'})
        self.assertEqual(target.read_text(), 'preserve')

    def test_auth_token_cannot_be_sent_to_arbitrary_host(self):
        with self.assertRaises(LINUX.RunnerError):
            LINUX.request_json('https://evil.invalid/a', token='SECRET')

class PhoneFlowTests(Fixture):
    def test_both_proot_generations_have_explicit_owned_guest_name(self):
        self.assertEqual(PHONE.guest_install_command('--name NAME'), ['proot-distro', 'install', 'ubuntu:24.04', '--name', 'aaris-cf-runner'])
        self.assertEqual(PHONE.guest_install_command('--override-alias alias'), ['proot-distro', 'install', '--override-alias', 'aaris-cf-runner', 'ubuntu'])

    def test_worker_prepare_has_no_codespace_dependency(self):
        runner = self.phone()
        runner.ensure_guest = lambda: None
        runner.copy_source = lambda: None
        calls = []
        def command(stage, argv, **kwargs):
            calls.append(argv)
            PHONE.atomic_json(runner.op / 'result.json', {
                'operation_id': runner.args.operation, 'source_sha': runner.args.source_sha,
                'state': 'CF_DEPLOYED_DO_PROBE_PENDING', 'account_id': 'd' * 32,
                'broker_url': 'https://aaris-turbo-terminal.example.workers.dev',
                'watch_key_installed': False,
            })
        runner.command = command
        runner.prepare()
        self.assertEqual(calls[0][0], 'proot-distro')
        self.assertNotIn('gh', calls[0])
        self.assertNotIn('codespace', ' '.join(calls[0]).lower())
        self.assertTrue(Path(runner.args.output).is_file())

    def test_abort_keeps_profile_and_unknown_remote_receipt(self):
        runner = self.phone()
        profile = runner.op / 'profile'
        profile.mkdir()
        (profile / 'credential').write_text('PRIVATE')
        PHONE.atomic_json(runner.op / 'phase.json', {'state': 'DEPLOY_REQUESTED'})
        PHONE.atomic_json(runner.base / 'current-profile.json', {'operation_id': 'old-operation'})
        with self.assertRaises(PHONE.RunnerError) as raised:
            runner.abort()
        self.assertEqual(raised.exception.code, 76)
        self.assertEqual((profile / 'credential').read_text(), 'PRIVATE')
        self.assertEqual(json.loads((runner.op / 'phase.json').read_text())['state'], 'DEPLOY_REQUESTED')
        self.assertEqual(json.loads((runner.base / 'current-profile.json').read_text())['operation_id'], 'old-operation')

    def test_commit_changes_pointer_only_after_receipt(self):
        runner = self.phone()
        with self.assertRaises(PHONE.RunnerError):
            runner.commit()
        PHONE.atomic_json(runner.op / 'result.json', {
            'operation_id': runner.args.operation, 'source_sha': runner.args.source_sha,
            'state': 'CF_DEPLOYED_DO_PROBE_PENDING', 'account_id': 'd' * 32,
            'broker_url': 'https://aaris-turbo-terminal.example.workers.dev',
        })
        runner.commit()
        self.assertEqual(json.loads((runner.base / 'current-profile.json').read_text())['operation_id'], runner.args.operation)

    def test_second_runner_is_busy_without_cleanup(self):
        runner = self.phone(mode='abort')
        fd = os.open(runner.base / 'phone-runner.lock', os.O_RDWR | os.O_CREAT, 0o600)
        try:
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            with self.assertRaises(PHONE.RunnerError) as raised:
                runner.execute()
            self.assertEqual(raised.exception.code, 75)
            self.assertFalse((runner.op / 'abort.json').exists())
        finally:
            os.close(fd)

    def test_signal_exit_code_is_preserved(self):
        runner = self.phone()
        with self.assertRaises(PHONE.RunnerError) as raised:
            runner.command('CANCEL_TEST', [sys.executable, '-c', 'raise SystemExit(130)'])
        self.assertEqual(raised.exception.code, 130)

class ProcessCleanupTests(Fixture):
    def test_exited_group_leader_does_not_hide_live_descendant(self):
        for module, kind in ((LINUX, 'linux'), (PHONE, 'phone')):
            with self.subTest(runner=kind):
                runner = LINUX.LinuxRunner(self.environment) if kind == 'linux' else self.phone()
                runner.tmp = self.root
                pidfile = self.root / (kind + '-exited-leader.json')
                previous_handler = signal.getsignal(signal.SIGTERM)
                unrelated = subprocess.Popen([sys.executable, '-c', 'import time;time.sleep(30)'])
                def interrupt(signum, frame):
                    raise module.Interrupted('TERMINATED', 143)
                signal.signal(signal.SIGTERM, interrupt)
                def send_signal():
                    for _ in range(150):
                        try:
                            ids = json.loads(pidfile.read_text())
                            fields = (Path('/proc') / str(ids['leader']) / 'stat').read_text().rsplit(')', 1)[1].split()
                            if fields[0] == 'Z':
                                os.kill(os.getpid(), signal.SIGTERM)
                                return
                        except (OSError, ValueError):
                            pass
                        time.sleep(0.02)
                timer = threading.Thread(target=send_signal, daemon=True)
                timer.start()
                code = ('import json,os,pathlib,subprocess,sys; '
                        'p=subprocess.Popen([sys.executable,"-c","import time;time.sleep(30)"]); '
                        'pathlib.Path(sys.argv[1]).write_text(json.dumps({"leader":os.getpid(),"child":p.pid})); '
                        'os._exit(0)')
                try:
                    command = runner.run if kind == 'linux' else runner.command
                    with self.assertRaises(module.Interrupted):
                        command('EXITED_LEADER', [sys.executable, '-c', code, str(pidfile)])
                    self.assertIsNone(unrelated.poll())
                    ids = json.loads(pidfile.read_text())
                    status = Path('/proc') / str(ids['child']) / 'stat'
                    self.assertTrue(not status.exists() or status.read_text().rsplit(')', 1)[1].split()[0] == 'Z')
                finally:
                    signal.signal(signal.SIGTERM, previous_handler)
                    timer.join(timeout=4)
                    runner.stop_child()
                    unrelated.terminate()
                    unrelated.wait(timeout=5)
                    if pidfile.is_file():
                        try:
                            os.kill(json.loads(pidfile.read_text())['child'], signal.SIGTERM)
                        except ProcessLookupError:
                            pass

    def test_interruption_stops_only_owned_process_group(self):
        runner = LINUX.LinuxRunner(self.environment)
        runner.tmp = self.root
        pidfile = self.root / 'owned-child.pid'
        unrelated = subprocess.Popen([sys.executable, '-c', 'import time;time.sleep(30)'])
        old_handler = signal.getsignal(signal.SIGTERM)
        def interrupt(signum, frame):
            raise LINUX.Interrupted('TERMINATED', 143)
        signal.signal(signal.SIGTERM, interrupt)
        def send_signal():
            for _ in range(100):
                if pidfile.exists():
                    os.kill(os.getpid(), signal.SIGTERM)
                    return
                time.sleep(0.02)
        timer = threading.Thread(target=send_signal, daemon=True)
        timer.start()
        child_code = (
            'import subprocess,sys,time,pathlib; '
            'p=subprocess.Popen([sys.executable,"-c","import time;time.sleep(30)"]); '
            'pathlib.Path(sys.argv[1]).write_text(str(p.pid)); time.sleep(30)'
        )
        try:
            with self.assertRaises(LINUX.Interrupted):
                runner.run('CLEANUP_FIXTURE', [sys.executable, '-c', child_code, str(pidfile)])
            self.assertIsNone(unrelated.poll())
            self.assertIsNotNone(runner.child.poll())
            child_pid = int(pidfile.read_text())
            status = Path('/proc') / str(child_pid) / 'stat'
            self.assertTrue(not status.exists() or status.read_text().split()[2] == 'Z')
        finally:
            signal.signal(signal.SIGTERM, old_handler)
            timer.join(timeout=3)
            runner.stop_child()
            unrelated.terminate()
            unrelated.wait(timeout=5)

if __name__ == '__main__':
    unittest.main()
