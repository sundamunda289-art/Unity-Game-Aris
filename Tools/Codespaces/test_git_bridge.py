#!/usr/bin/env python3
"""Execute the real Termux bridge against offline providers and an isolated SSH host.

The remote Python installer and canonical Git helper really execute. Only the
provider transports are replaced; no GitHub, network, or real account is used.
"""
import base64
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

HERE = Path(__file__).resolve().parent
CANONICAL = 'sundamunda289-art/Unity-Game-Aris'
SOURCE = 'b' * 40
MACHINE = 'canonical-direct-codespace'
OWNER_TOKEN = 'gho_OWNER_ONLY_TEST_CANARY_9e583be2'
HOST_TOKEN = 'gho_HOST_ONLY_TEST_CANARY_cad76813'

PROVIDER = r'''
import base64,json,os,pathlib,re,shlex,shutil,subprocess,sys
root=pathlib.Path(os.environ['BRIDGE_TEST_ROOT'])
config=json.loads((root/'fixture.json').read_text())
tool=pathlib.Path(sys.argv[0]).name
args=sys.argv[1:]
def record(value):
 with (root/'calls.jsonl').open('a') as output:
  output.write(json.dumps(value,sort_keys=True)+'\n')
def fail(message):
 raise SystemExit('OFFLINE_FIXTURE: '+message)
if tool=='pkg':
 raise SystemExit(0)
if tool=='curl':
 urls=[arg for arg in args if arg.startswith('https://')]
 expected='https://raw.githubusercontent.com/'+config['canonical']+'/'+config['source']+'/Tools/Codespaces/canonical_git.sh'
 if urls!=[expected]: fail('unpinned or unexpected source URL')
 shutil.copyfile(config['helper'],args[args.index('-o')+1])
 record({'tool':'curl','url':urls[0]})
 raise SystemExit(0)
if tool=='getent':
 if args!=['passwd',str(os.getuid())]: fail('unexpected passwd query')
 print('vscode:x:'+str(os.getuid())+':'+str(os.getgid())+'::'+config['remote_home']+':/bin/bash')
 raise SystemExit(0)
if tool=='git':
 record({'tool':'git','args':args,'ssh_command':os.environ.get('GIT_SSH_COMMAND','')})
 if args[:2]==['lfs','version']:
  print('git-lfs/offline-fixture');raise SystemExit(0)
 if args and args[0]=='ls-remote':
  key=config['remote_home']+'/.config/aaris-ai/canonical-deploy-key'
  if key not in os.environ.get('GIT_SSH_COMMAND',''): fail('repository-only key was not used')
  if any(name in os.environ for name in ('GH_TOKEN','GITHUB_TOKEN','GH_CONFIG_DIR')): fail('phone authentication entered remote Git')
  print(config['source']+'\trefs/heads/main');raise SystemExit(0)
 os.execv(config['real_git'],['git',*args])
if tool!='gh': fail('unknown fake provider')
profile=pathlib.Path(os.environ.get('GH_CONFIG_DIR',''))
identity=json.loads((profile/'fixture-credential.json').read_text())
login=identity['login']
record({'tool':'gh','args':args,'login':login})
if args[:2]==['api','user']:
 print(login);raise SystemExit(0)
if args[:2]==['repo','view']:
 if login!='sundamunda289-art' or args[2]!=config['canonical']: fail('repository authority mismatch')
 print('ADMIN');raise SystemExit(0)
if args[:2]==['codespace','list']:
 if login!='hosting-secondary': fail('owner authentication used to select Codespace')
 print(json.dumps(config['machines']));raise SystemExit(0)
if args[:2]==['codespace','ssh']:
 if login!='hosting-secondary' or args[args.index('-c')+1]!=config['machine']: fail('wrong SSH authority or target')
 payload=sys.stdin.read()
 command=args[args.index('--')+1:]
 if len(command)!=1: fail('unexpected SSH command argument count')
 parsed=shlex.split(command[0])
 if parsed[:2]!=['python3','-c'] or len(parsed)!=3: fail('unexpected remote command')
 encoded=re.search(r'base64\.b64decode\("([A-Za-z0-9+/=]+)"\)',parsed[2])
 if not encoded: fail('remote script not encoded as expected')
 decoded=base64.b64decode(encoded.group(1)).decode()
 incoming=base64.b64decode(payload.strip()).decode() if payload.strip() else ''
 remote_env={name:os.environ[name] for name in ('PATH','BRIDGE_TEST_ROOT')}
 remote_env.update(HOME=config['wrong_home'],LC_ALL='C',GIT_CONFIG_GLOBAL='/dev/null',GIT_CONFIG_NOSYSTEM='1')
 record({'tool':'ssh-boundary','login':login,'args':command,'stdin':payload,
         'decoded_command':decoded,'decoded_payload':incoming,'environment':remote_env})
 # Model the remote account's OS home independently of the inherited HOME.
 # The received installer/helper execute unmodified within the fixture tree.
 bootstrap=('import os,pwd,types; '
            'pwd.getpwuid=lambda uid:types.SimpleNamespace(pw_dir='+repr(config['remote_home'])+'); '
            'exec(compile('+repr(parsed[2])+',"ssh-received","exec"))')
 result=subprocess.run([sys.executable,'-c',bootstrap],input=payload,text=True,
  capture_output=True,cwd=config['project'],env=remote_env)
 sys.stdout.write(result.stdout);sys.stderr.write(result.stderr)
 raise SystemExit(result.returncode)
if args and args[0]=='api' and any('/keys' in arg for arg in args):
 if login!='sundamunda289-art': fail('hosting token used for canonical deploy-key registration')
 path=root/'registered-keys.json'
 keys=json.loads(path.read_text()) if path.exists() else []
 if '--method' in args:
  if args[args.index('--method')+1]!='POST': fail('unexpected key mutation')
  key=next(arg[4:] for arg in args if arg.startswith('key='))
  if not key.startswith('ssh-ed25519 '): fail('non-public credential sent to GitHub')
  keys.append({'key':key,'read_only':False});path.write_text(json.dumps(keys))
  print('false')
 else:
  print(json.dumps(keys))
 raise SystemExit(0)
fail('unexpected GitHub operation')
'''


class GitBridgeIntegrationTests(unittest.TestCase):
    def setUp(self):
        for name in ('bash', 'git', 'jq', 'ssh-keygen', 'base64'):
            if not shutil.which(name):
                self.skipTest(name + ' is required for the offline shell integration test')
        self.temp = tempfile.TemporaryDirectory(prefix='aaris-git-bridge-test-', dir='/tmp')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.bin = self.root / 'bin'
        self.home = self.root / 'phone-home'
        self.remote = self.root / 'remote-home'
        self.wrong_home = self.root / 'inherited-home-must-stay-empty'
        self.project = self.root / 'remote-project'
        self.master = self.home / 'master-profile'
        self.host = self.home / 'hosting-profile'
        for folder in (self.bin, self.home, self.remote, self.wrong_home, self.project, self.master, self.host):
            folder.mkdir(parents=True, mode=0o700, exist_ok=True)
        for folder, login, token in ((self.master, 'sundamunda289-art', OWNER_TOKEN),
                                     (self.host, 'hosting-secondary', HOST_TOKEN)):
            path = folder / 'fixture-credential.json'
            path.write_text(json.dumps({'login': login, 'token': token}))
            path.chmod(0o600)
        self.real_git = shutil.which('git')
        subprocess.run([self.real_git, 'init', '-q', '-b', 'main', str(self.project)], check=True)
        (self.project / 'Assets').mkdir()
        (self.project / 'Assets/.gitkeep').write_text('')
        (self.project / 'ProjectSettings').mkdir()
        (self.project / 'ProjectSettings/ProjectVersion.txt').write_text('m_EditorVersion: 6000.3.26f1\n')
        subprocess.run([self.real_git, '-C', str(self.project), 'add', '.'], check=True)
        subprocess.run([self.real_git, '-C', str(self.project), '-c', 'user.name=Offline Fixture',
                        '-c', 'user.email=fixture@example.invalid', '-c', 'commit.gpgsign=false',
                        'commit', '-q', '-m', 'local fixture'], check=True)
        self.config = {'canonical': CANONICAL, 'source': SOURCE, 'machine': MACHINE,
                       'helper': str(HERE / 'canonical_git.sh'), 'project': str(self.project),
                       'remote_home': str(self.remote), 'wrong_home': str(self.wrong_home),
                       'real_git': self.real_git,
                       'machines': [{'name': MACHINE, 'state': 'Available', 'repository': CANONICAL}]}
        self.fixture = self.root / 'fixture.json'
        self.fixture.write_text(json.dumps(self.config))
        for name in ('gh', 'curl', 'pkg', 'getent', 'git'):
            target = self.bin / name
            target.write_text('#!' + sys.executable + '\n' + PROVIDER)
            target.chmod(0o700)
        self.env = {key: value for key, value in os.environ.items()
                    if not key.startswith(('GH_', 'GITHUB_', 'AARIS_', 'GIT_'))}
        self.env.update(HOME=str(self.home), PATH=str(self.bin) + ':' + os.environ['PATH'],
                        BRIDGE_TEST_ROOT=str(self.root), AARIS_MASTER_AUTH_DIR=str(self.master),
                        AARIS_CODESPACE_AUTH_DIR=str(self.host), AARIS_CODESPACE_NAME=MACHINE,
                        AARIS_SOURCE_SHA=SOURCE, GIT_CONFIG_GLOBAL='/dev/null', GIT_CONFIG_NOSYSTEM='1')

    def invoke(self):
        return subprocess.run(['bash', str(HERE / 'termux_git_bridge.sh')], env=self.env,
                              stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=25)

    def calls(self):
        path = self.root / 'calls.jsonl'
        return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []

    def test_canonical_direct_codespace_uses_repo_key_without_owner_token_over_ssh(self):
        result = self.invoke()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('AARIS_GIT_BRIDGE_READY', result.stdout)
        self.assertIn('AARIS_SYNC_CANONICAL_DEPLOY_KEY_VERIFIED', result.stdout)
        key = self.remote / '.config/aaris-ai/canonical-deploy-key'
        self.assertTrue(key.is_file())
        self.assertEqual(key.stat().st_mode & 0o777, 0o600)
        self.assertFalse(any(self.wrong_home.iterdir()))
        self.assertEqual((self.remote / '.local/share/aaris-git/canonical_git.sh').read_bytes(),
                         (HERE / 'canonical_git.sh').read_bytes())
        events = self.calls()
        ssh = [event for event in events if event['tool'] == 'ssh-boundary']
        self.assertEqual(len(ssh), 2)
        self.assertTrue(all(event['login'] == 'hosting-secondary' for event in ssh))
        boundary = json.dumps(ssh)
        for token in (OWNER_TOKEN, HOST_TOKEN):
            self.assertNotIn(token, boundary)
            self.assertNotIn(base64.b64encode(token.encode()).decode(), boundary)
            self.assertNotIn(token, result.stdout + result.stderr)
        for event in ssh:
            self.assertFalse(any(name in event['environment'] for name in ('GH_TOKEN', 'GITHUB_TOKEN', 'GH_CONFIG_DIR')))
        registrations = [event for event in events if event['tool'] == 'gh' and '--method' in event['args']]
        self.assertEqual(len(registrations), 1)
        self.assertEqual(registrations[0]['login'], 'sundamunda289-art')
        self.assertFalse(any(event['tool'] == 'git' and ('fetch' in event['args'] or 'pull' in event['args']) for event in events))
        first_key = key.read_bytes()
        rerun = self.invoke()
        self.assertEqual(rerun.returncode, 0, rerun.stdout + rerun.stderr)
        self.assertIn('EXISTING_WRITE_DEPLOY_KEY_REUSED', rerun.stdout)
        self.assertEqual(key.read_bytes(), first_key)
        registrations = [event for event in self.calls() if event['tool'] == 'gh' and '--method' in event['args']]
        self.assertEqual(len(registrations), 1, 'resume must not register another key')

    def test_unrelated_same_name_repository_stops_before_ssh(self):
        self.config['machines'][0]['repository'] = 'unrelated-owner/Unity-Game-Aris'
        self.fixture.write_text(json.dumps(self.config))
        result = self.invoke()
        self.assertEqual(result.returncode, 11)
        self.assertIn('AARIS_GIT_BRIDGE_EXACT_MACHINE_NOT_READY', result.stdout)
        self.assertFalse(any(event['tool'] == 'ssh-boundary' for event in self.calls()))
        self.assertFalse((self.remote / '.config/aaris-ai/canonical-deploy-key').exists())


if __name__ == '__main__':
    unittest.main()
