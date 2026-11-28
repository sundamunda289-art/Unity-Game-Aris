#!/usr/bin/env python3
"""Failure-oriented local tests: no network, real credentials, or Unity needed."""
from __future__ import annotations

import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import types
import unittest
from unittest import mock

import provision_codespace as provision
import mobile_license as unity
import verify_lfs


def git(root: Path, *args: str) -> str:
    environment = {**os.environ, "GIT_AUTHOR_NAME": "Provision Test",
                   "GIT_AUTHOR_EMAIL": "test@example.invalid", "GIT_COMMITTER_NAME": "Provision Test",
                   "GIT_COMMITTER_EMAIL": "test@example.invalid", "GIT_LFS_SKIP_SMUDGE": "1"}
    return subprocess.run(["git", "-C", str(root), *args], env=environment,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          text=True, check=True).stdout.strip()


class Fixture(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="aaris-provision-test-", dir="/tmp")
        self.root = Path(self.temporary.name)
        self.repo = self.root / "project"
        self.repo.mkdir()
        git(self.repo, "init", "-q", "-b", "main")
        (self.repo / "ProjectSettings").mkdir()
        (self.repo / "ProjectSettings/ProjectVersion.txt").write_text("m_EditorVersion: 6000.3.26f1\n")
        (self.repo / "source.txt").write_text("original\n")
        git(self.repo, "add", ".")
        git(self.repo, "commit", "-qm", "fixture")
        self.sha = git(self.repo, "rev-parse", "HEAD")

    def tearDown(self):
        self.temporary.cleanup()

    def context(self, operation="test"):
        return provision.Context(self.repo, operation, self.sha)

    def lfs_fixture(self, payload=b"original large asset\n"):
        oid = hashlib.sha256(payload).hexdigest()
        path = self.repo / "Assets/blob.asset"
        path.parent.mkdir(exist_ok=True)
        path.write_text(f"version https://git-lfs.github.com/spec/v1\noid sha256:{oid}\nsize {len(payload)}\n")
        git(self.repo, "add", "Assets/blob.asset")
        git(self.repo, "commit", "-qm", "pointer fixture")
        self.sha = git(self.repo, "rev-parse", "HEAD")
        return path, payload


class IntegrityTests(Fixture):
    def test_pointer_is_not_an_asset(self):
        self.lfs_fixture()
        report = verify_lfs.verify(self.repo, self.sha)
        self.assertFalse(report["ok"])
        self.assertEqual(report["pointers"], 1)

    def test_same_size_corruption_is_rejected(self):
        path, payload = self.lfs_fixture()
        path.write_bytes(payload)
        self.assertTrue(verify_lfs.verify(self.repo, self.sha)["ok"])
        path.write_bytes(b"X" * len(payload))
        report = verify_lfs.verify(self.repo, self.sha)
        self.assertFalse(report["ok"])
        self.assertEqual(report["mismatches"], 1)

    def test_symlink_payload_is_not_followed(self):
        path, payload = self.lfs_fixture()
        target = self.root / "outside"
        target.write_bytes(payload)
        path.unlink()
        path.symlink_to(target)
        report = verify_lfs.verify(self.repo, self.sha)
        self.assertFalse(report["ok"])
        self.assertEqual(report["unsafe"], 1)
        self.assertEqual(target.read_bytes(), payload)

    def test_inventory_is_pinned_to_commit_not_changed_pointer(self):
        path, payload = self.lfs_fixture()
        path.write_text("changed by another process")
        entries = verify_lfs.inventory(self.repo, self.sha)
        self.assertEqual(entries[0]["oid"], hashlib.sha256(payload).hexdigest())

    def test_preserves_modified_untracked_and_symlink_bytes(self):
        (self.repo / "source.txt").write_text("owner work\n")
        note = self.repo / "notes with spaces.txt"
        note.write_bytes(b"\0private owner work\n")
        symlink = self.repo / "owner-link"
        symlink.symlink_to("source.txt")
        manifest = provision.preservation_manifest(self.repo)
        provision.verify_preserved(self.repo, manifest)
        self.assertEqual(set(manifest), {"source.txt", "notes with spaces.txt", "owner-link"})
        note.write_bytes(b"changed")
        with self.assertRaisesRegex(provision.Blocked, "PRESERVED_USER_FILES_CHANGED"):
            provision.verify_preserved(self.repo, manifest)

    def test_wrong_editor_version_not_accepted_from_binary_presence(self):
        root = self.root / "editors"
        editor = root / provision.VERSION / "Editor"
        (editor / "Data").mkdir(parents=True)
        binary = editor / "Unity"
        binary.write_text("#!/bin/sh\nprintf '6000.3.1f1\\n'\n")
        binary.chmod(0o700)
        environment = {**os.environ, "HOME": str(self.root),
                       "AARIS_UNITY_EDITOR_ROOT": str(root), "AARIS_UNITY_PROJECT_DIR": str(self.repo)}
        result = subprocess.run(["bash", str(provision.HERE / "install_unity.sh"), "--status-json"],
                                env=environment, capture_output=True, text=True, check=True)
        self.assertFalse(json.loads(result.stdout)["ready"])

    def test_partial_new_install_requires_success_record(self):
        root = self.root / "editors"
        editor = root / provision.VERSION / "Editor"
        (editor / "Data/Resources").mkdir(parents=True)
        managed = editor / "Data/Managed/UnityEngine"
        managed.mkdir(parents=True)
        (managed / "UnityEngine.CoreModule.dll").write_bytes(b"fixture")
        binary = editor / "Unity"
        binary.write_text("#!/bin/sh\nprintf '6000.3.26f1\\n'\n")
        binary.chmod(0o700)
        environment = {**os.environ, "HOME": str(self.root),
                       "AARIS_UNITY_EDITOR_ROOT": str(root), "AARIS_UNITY_PROJECT_DIR": str(self.repo)}
        command = ["bash", str(provision.HERE / "install_unity.sh"), "--status-json"]
        first = subprocess.run(command, env=environment, capture_output=True, text=True, check=True)
        self.assertFalse(json.loads(first.stdout)["ready"])
        (root / ("verified-" + provision.VERSION + ".json")).write_text(json.dumps(
            {"version": provision.VERSION, "editor": str(binary), "changeset": "de8167e7440c"}))
        second = subprocess.run(command, env=environment, capture_output=True, text=True, check=True)
        self.assertTrue(json.loads(second.stdout)["ready"])


class OrderingAndRecoveryTests(Fixture):
    def test_editor_failure_starts_neither_lfs_nor_login_check(self):
        context = self.context()
        runner = provision.Provisioner(context)
        runner.ensure_editor = mock.Mock(side_effect=provision.Blocked("EDITOR_INSTALL_FAILED"))
        runner.source = mock.Mock()
        runner.watch_license = mock.Mock()
        self.assertEqual(runner.run(0), 10)
        runner.source.assert_not_called()
        runner.watch_license.assert_not_called()
        self.assertEqual(context.status()["error"], "EDITOR_INSTALL_FAILED")

    def test_editor_finishes_before_parallel_license_and_source(self):
        context = self.context()
        runner = provision.Provisioner(context)
        license_started = threading.Event()
        trace = []

        def editor():
            trace.append("editor-finished")
            context.update(editor_ready=True)
            return {"ready": True}

        def watch():
            self.assertTrue(read_state()["editor_ready"])
            trace.append("license")
            license_started.set()
            context.update(auth_ready=True, license_ready=True)

        def source(manifest):
            self.assertTrue(read_state()["editor_ready"])
            self.assertTrue(license_started.wait(2))
            trace.append("source")
            context.update(source_ready=True)

        def read_state():
            return provision.read_json(context.state_path)

        runner.ensure_editor = editor
        runner.watch_license = watch
        runner.source = source
        self.assertEqual(runner.run(1), 0)
        self.assertEqual(trace[0], "editor-finished")
        state = context.status()
        self.assertEqual(state["phase"], "COMPLETE")
        self.assertTrue(state["ready_to_open"])
        self.assertFalse(state["project_opened"])

    def test_missing_license_leaves_source_ready_and_resumable(self):
        context = self.context()
        runner = provision.Provisioner(context)
        started = threading.Event()
        runner.ensure_editor = lambda: context.update(editor_ready=True)

        def watch():
            context.update(auth_ready=False, license_ready=False)
            started.set()
            runner.stop.wait(5)

        def source(manifest):
            self.assertTrue(started.wait(2))
            context.update(source_ready=True)

        runner.watch_license = watch
        runner.source = source
        self.assertEqual(runner.run(0), 10)
        state = context.status()
        self.assertEqual(state["phase"], "WAITING_FOR_UNITY_LOGIN")
        self.assertTrue(state["source_ready"])
        self.assertFalse(state["ready_to_open"])

    def test_dirty_tracked_work_pauses_without_install_or_byte_changes(self):
        path = self.repo / "source.txt"
        path.write_bytes(b"real owner modifications")
        before = path.read_bytes()
        context = self.context()
        runner = provision.Provisioner(context)
        runner.ensure_editor = mock.Mock()
        self.assertEqual(runner.run(0), 10)
        runner.ensure_editor.assert_not_called()
        self.assertEqual(path.read_bytes(), before)
        self.assertEqual(context.status()["error"], "DIRTY_TRACKED_WORK_PROTECTED")
        self.assertIn("source.txt", provision.read_json(context.directory / "preserved.json"))

    def test_active_operation_is_attached_without_second_process(self):
        context = self.context()
        context.update(phase="UNITY_INSTALL", editor_ready=False)
        provision.atomic_json(context.root / "active.json", {"operation": context.operation})
        provision.atomic_json(context.directory / "process.json", {"pid": 999, "start": "test", "script": "test"})
        with mock.patch.object(provision, "process_alive", return_value=True), \
             mock.patch.object(provision.subprocess, "Popen") as spawn:
            result = provision.start(context, types.SimpleNamespace(license_wait=600))
        self.assertTrue(result["attached"])
        spawn.assert_not_called()

    def test_lock_rejects_second_operation_without_stale_pid_deletion(self):
        context = self.context()
        path = context.root / "operation.lock"
        with provision.lock(path):
            with self.assertRaisesRegex(provision.Blocked, "OPERATION_ALREADY_RUNNING"):
                with provision.lock(path):
                    self.fail("second operation acquired lock")
        self.assertTrue(path.is_file())

    def test_verified_editor_is_reused(self):
        context = self.context()
        runner = provision.Provisioner(context)
        runner.editor_status = lambda: {"ready": True, "version": provision.VERSION,
                                      "editor": "/workspaces/test/Editor/Unity", "persistent_workspace": True}
        runner.dependencies = mock.Mock()
        runner.command = mock.Mock()
        with mock.patch.object(provision.shutil, "which", return_value="/usr/bin/git-lfs"):
            runner.ensure_editor()
        runner.dependencies.assert_not_called()
        runner.command.assert_not_called()

    def test_divergent_history_is_not_reset(self):
        canonical = self.root / "canonical"
        subprocess.run(["git", "clone", "-q", str(self.repo), str(canonical)], check=True)
        (canonical / "new.txt").write_text("canonical advance\n")
        git(canonical, "add", "new.txt")
        git(canonical, "commit", "-qm", "advance")
        target = git(canonical, "rev-parse", "HEAD")
        (self.repo / "local.txt").write_text("local committed work\n")
        git(self.repo, "add", "local.txt")
        git(self.repo, "commit", "-qm", "owner local branch")
        before = git(self.repo, "rev-parse", "HEAD")
        context = provision.Context(self.repo, "divergence", target)
        runner = provision.Provisioner(context)
        with mock.patch.object(provision, "PUBLIC", str(canonical)):
            with self.assertRaisesRegex(provision.Blocked, "DIVERGENT_SOURCE_PROTECTED"):
                runner.source({})
        self.assertEqual(git(self.repo, "rev-parse", "HEAD"), before)
        self.assertEqual((self.repo / "local.txt").read_text(), "local committed work\n")
        self.assertFalse((self.repo / "new.txt").exists())

    def canonical_lfs_target(self):
        canonical = self.root / "canonical"
        subprocess.run(["git", "clone", "-q", str(self.repo), str(canonical)], check=True)
        payload = b"the complete canonical asset\n"
        oid = hashlib.sha256(payload).hexdigest()
        (canonical / "Assets").mkdir()
        (canonical / ".gitattributes").write_text("*.asset filter=lfs diff=lfs merge=lfs -text\n")
        (canonical / "Assets/new.asset").write_text(
            f"version https://git-lfs.github.com/spec/v1\noid sha256:{oid}\nsize {len(payload)}\n")
        git(canonical, "add", ".")
        git(canonical, "commit", "-qm", "canonical asset")
        return canonical, git(canonical, "rev-parse", "HEAD"), payload, oid

    def test_lfs_network_failure_keeps_old_head_and_untracked_bytes(self):
        canonical, target, payload, oid = self.canonical_lfs_target()
        note = self.repo / "owner-note"
        note.write_bytes(b"owner untracked work")
        manifest = provision.preservation_manifest(self.repo)
        context = provision.Context(self.repo, "failed-lfs", target)
        context.home = self.root
        runner = provision.Provisioner(context)
        original_command = runner.command

        def injected(argv, name, **options):
            if name == "lfs-fetch":
                self.assertEqual(git(self.repo, "rev-parse", "HEAD"), self.sha)
                raise provision.Blocked("NETWORK_TRANSIENT")
            return original_command(argv, name, **options)

        runner.command = injected
        with mock.patch.object(provision, "PUBLIC", str(canonical)), \
             mock.patch.object(provision, "workspace_path", return_value=True):
            with self.assertRaisesRegex(provision.Blocked, "NETWORK_TRANSIENT"):
                runner.source(manifest)
        self.assertEqual(git(self.repo, "rev-parse", "HEAD"), self.sha)
        self.assertFalse((self.repo / "Assets/new.asset").exists())
        self.assertEqual(note.read_bytes(), b"owner untracked work")

    def test_checkout_happens_after_all_objects_and_verifies_payload(self):
        canonical, target, payload, oid = self.canonical_lfs_target()
        note = self.repo / "owner-note"
        note.write_bytes(b"owner untracked work")
        manifest = provision.preservation_manifest(self.repo)
        context = provision.Context(self.repo, "complete-lfs", target)
        context.home = self.root
        runner = provision.Provisioner(context)
        original_command = runner.command

        def downloaded(argv, name, **options):
            if name == "lfs-fetch":
                self.assertEqual(git(self.repo, "rev-parse", "HEAD"), self.sha)
                self.assertIn("lfs.concurrenttransfers=1", argv)
                cache = context.gitdir / "lfs/objects" / oid[:2] / oid[2:4] / oid
                cache.parent.mkdir(parents=True, exist_ok=True)
                cache.write_bytes(payload)
                return
            return original_command(argv, name, **options)

        runner.command = downloaded
        with mock.patch.object(provision, "PUBLIC", str(canonical)), \
             mock.patch.object(provision, "workspace_path", return_value=True):
            runner.source(manifest)
        self.assertEqual(git(self.repo, "rev-parse", "HEAD"), target)
        self.assertEqual((self.repo / "Assets/new.asset").read_bytes(), payload)
        self.assertEqual(note.read_bytes(), b"owner untracked work")
        self.assertTrue(context.status()["source_ready"])

    def test_assume_unchanged_file_cannot_hide_owner_modification(self):
        git(self.repo, "update-index", "--assume-unchanged", "source.txt")
        (self.repo / "source.txt").write_bytes(b"hidden owner work")
        runner = provision.Provisioner(self.context())
        with self.assertRaisesRegex(provision.Blocked, "TRACKED_FILE_FLAGS_REQUIRE_REVIEW"):
            runner.require_source_idle()

    def test_incoming_tracked_asset_cannot_overwrite_ignored_owner_file(self):
        canonical, target, payload, oid = self.canonical_lfs_target()
        ignored = self.repo / "Assets/new.asset"
        ignored.parent.mkdir()
        ignored.write_bytes(b"owner ignored asset must survive")
        with (self.repo / ".git/info/exclude").open("a") as handle:
            handle.write("\nAssets/new.asset\n")
        manifest = provision.preservation_manifest(self.repo)
        self.assertNotIn("Assets/new.asset", manifest)
        context = provision.Context(self.repo, "ignored-collision", target)
        context.home = self.root
        runner = provision.Provisioner(context)
        original_command = runner.command

        def downloaded(argv, name, **options):
            if name == "lfs-fetch":
                cache = context.gitdir / "lfs/objects" / oid[:2] / oid[2:4] / oid
                cache.parent.mkdir(parents=True, exist_ok=True)
                cache.write_bytes(payload)
                return
            return original_command(argv, name, **options)

        runner.command = downloaded
        with mock.patch.object(provision, "PUBLIC", str(canonical)), \
             mock.patch.object(provision, "workspace_path", return_value=True):
            with self.assertRaises(provision.Blocked):
                runner.source(manifest)
        self.assertEqual(git(self.repo, "rev-parse", "HEAD"), self.sha)
        self.assertEqual(ignored.read_bytes(), b"owner ignored asset must survive")
        self.assertFalse((self.repo / ".gitattributes").exists())


class MemoryReporter:
    def __init__(self):
        self.values = {}
        self.markers = []

    def state(self, **values):
        self.values.update(values)

    def event(self, marker, phase=None, **values):
        self.markers.append(marker)
        self.values.update(values)
        if phase:
            self.values["phase"] = phase

    def url(self, value):
        self.values["auth_url"] = value


class FakeUnityProcess:
    def __init__(self, output):
        self.stdout = io.StringIO(output)
        self.pid = 2147483646
        self.returncode = 0

    def poll(self):
        return self.returncode


class UnityAuthTests(Fixture):
    def test_secure_default_returns_26_without_enabling_file_storage(self):
        reporter = MemoryReporter()
        fake = FakeUnityProcess('{"error":"Failed to save credentials: keyring is locked"}\n')
        with mock.patch.object(unity, "check", side_effect=[False, False]), \
             mock.patch.object(unity.subprocess, "Popen", return_value=fake) as started:
            code = unity.login("unity", {}, reporter)
        self.assertEqual(code, 26)
        self.assertIn("UNITY_SECURE_STORAGE_UNAVAILABLE", reporter.markers)
        self.assertNotIn("--insecure-storage", started.call_args.args[0])
        self.assertIsNone(reporter.values.get("auth_url"))

    def test_file_storage_flag_requires_explicit_option(self):
        reporter = MemoryReporter()
        fake = FakeUnityProcess('{"auth_url":"https://id.unity.com/login?test=1"}\n')
        with mock.patch.object(unity, "check", side_effect=[False, True]), \
             mock.patch.object(unity.subprocess, "Popen", return_value=fake) as started:
            self.assertEqual(unity.login("unity", {}, reporter, insecure_storage=True), 0)
        self.assertIn("--insecure-storage", started.call_args.args[0])

    def test_long_valid_provider_session_has_no_420_second_cutoff(self):
        reporter = MemoryReporter()
        fake = FakeUnityProcess('{"auth_url":"https://id.unity.com/login?test=1"}\n')
        counter = iter(range(0, 100000, 5000))
        with mock.patch.object(unity, "check", side_effect=[False, True]), \
             mock.patch.object(unity.subprocess, "Popen", return_value=fake), \
             mock.patch.object(unity.time, "monotonic", side_effect=lambda: next(counter)):
            self.assertEqual(unity.login("unity", {}, reporter), 0)
        self.assertTrue(reporter.values["auth_verified"])

    def test_provider_expiry_is_explicit_and_removes_old_url(self):
        reporter = MemoryReporter()
        fake = FakeUnityProcess('{"error":"expired_token"}\n')
        with mock.patch.object(unity, "check", side_effect=[False, False]), \
             mock.patch.object(unity.subprocess, "Popen", return_value=fake):
            self.assertEqual(unity.login("unity", {}, reporter), 27)
        self.assertEqual(reporter.values["phase"], "EXPIRED")
        self.assertIsNone(reporter.values.get("auth_url"))

    def test_documentation_url_is_not_opened_as_login(self):
        self.assertIsNone(unity.extract("Help: https://docs.unity3d.com/Manual/"))
        self.assertFalse(unity.valid("https://id.unity.com.attacker.invalid/login"))
        self.assertFalse(unity.valid("https://user:password@id.unity.com/login"))

    def test_success_requires_both_fresh_final_status_checks(self):
        reporter = MemoryReporter()
        with mock.patch.object(unity, "writable_home", return_value=(self.root, self.root / "cli")), \
             mock.patch.object(unity, "ensure_cli", return_value="unity"), \
             mock.patch.object(unity, "login", return_value=0), \
             mock.patch.object(unity, "check", side_effect=[True, True, False]):
            self.assertEqual(unity.activate(reporter), 15)
        self.assertNotIn("UNITY_PERSONAL_LICENSE_VERIFIED", reporter.markers)

    def test_phone_reattaches_without_second_oauth_process(self):
        root = self.root / "auth"
        root.mkdir()
        session = "a" * 32
        folder = root / session
        folder.mkdir()
        unity.atomic_json(root / "active.json", {"session": session})
        unity.atomic_json(folder / "state.json", {"session": session, "machine": "test-machine",
                                                  "phase": "WAITING_FOR_UNITY_LOGIN"})
        args = types.SimpleNamespace(machine="test-machine", source_sha="a" * 40, insecure_storage=False)
        with mock.patch.object(unity, "session_root", return_value=root), \
             mock.patch.object(unity, "process_alive", return_value=True), \
             mock.patch.object(unity.subprocess, "Popen") as spawn:
            result = unity.start_session(args)
        self.assertTrue(result["attached"])
        self.assertEqual(result["session"], session)
        spawn.assert_not_called()

    def test_dead_worker_never_republishes_a_stale_oauth_url(self):
        root = self.root / "auth"
        root.mkdir()
        session = "b" * 32
        folder = root / session
        folder.mkdir()
        unity.atomic_json(folder / "state.json", {"session": session, "machine": "test-machine",
                                                  "phase": "WAITING_FOR_UNITY_LOGIN", "created_at": 0,
                                                  "auth_url": "https://id.unity.com/login?test=expired"})
        with mock.patch.object(unity, "process_alive", return_value=False):
            result = unity.session_state(root, session, "test-machine")
        self.assertEqual(result["phase"], "INTERRUPTED")
        self.assertIsNone(result["auth_url"])

    def phone_environment(self, count=1):
        folder = self.root / "phone-bin"
        folder.mkdir(exist_ok=True)
        profile = self.root / "phone-profile"
        profile.mkdir(exist_ok=True)
        temporary = self.root / "phone-tmp"
        temporary.mkdir(exist_ok=True)
        gh = folder / "gh"
        gh.write_text(r'''#!/usr/bin/env python3
import base64,json,os,re,sys
args=sys.argv[1:]
if args[:2]==['api','user']:
    print('hosting-user')
elif args[:2]==['codespace','list']:
    print(json.dumps([{'name':'test-machine'+('-extra' if i else ''),'state':'Available','repository':'hosting-user/Unity-Game-Aris'} for i in range(int(os.environ['TEST_MACHINE_COUNT']))]))
elif args[:2]==['codespace','ssh']:
    code=base64.b64decode(re.search(r'b64decode\("([^\"]+)"\)',args[-1]).group(1)).decode()
    if 'AARIS_SSH_PYTHON_OK' in code: print('AARIS_SSH_PYTHON_OK')
    elif 'AARIS_UNITY_HELPER_VERIFIED' in code: print('AARIS_UNITY_HELPER_VERIFIED')
    else:
        state={'session':'a'*32,'machine':'test-machine','phase':'STARTING','marker':'UNITY_LOGIN_RUNNER_STARTING'}
        if 'session-status' in code: state.update(phase='FAILED',marker='UNITY_SECURE_STORAGE_UNAVAILABLE',exit_code=26)
        print(json.dumps(state))
else: raise SystemExit(3)
''')
        gh.chmod(0o700)
        curl = folder / "curl"
        curl.write_text('''#!/usr/bin/env python3
import os,shutil,sys
shutil.copyfile(os.environ['TEST_HELPER_SOURCE'],sys.argv[sys.argv.index('-o')+1])
''')
        curl.chmod(0o700)
        environment = {**os.environ, "PATH": str(folder) + os.pathsep + os.environ["PATH"],
                       "AARIS_CODESPACE_AUTH_DIR": str(profile), "AARIS_SOURCE_SHA": "a" * 40,
                       "TMPDIR": str(temporary), "AARIS_UNITY_INSECURE_STORAGE": "0",
                       "TEST_MACHINE_COUNT": str(count), "TEST_HELPER_SOURCE": str(provision.HERE / "mobile_license.py")}
        environment.pop("AARIS_CODESPACE_NAME", None)
        return environment

    def test_four_step_phone_flow_propagates_storage_exit_26(self):
        result = subprocess.run(["bash", str(provision.HERE / "termux_unity_login_recover.sh")],
                                env=self.phone_environment(), capture_output=True, text=True, timeout=20)
        self.assertEqual(result.returncode, 26, result.stderr + result.stdout)
        for number in range(1, 5):
            self.assertIn(f"Step {number}/4:", result.stdout)
        self.assertIn("Using Codespace: test-machine", result.stdout)
        self.assertIn("UNITY_SECURE_STORAGE_UNAVAILABLE", result.stdout)

    def test_ambiguous_machine_is_rejected_before_login(self):
        result = subprocess.run(["bash", str(provision.HERE / "termux_unity_login_recover.sh")],
                                env=self.phone_environment(count=2), capture_output=True, text=True, timeout=20)
        self.assertEqual(result.returncode, 5)
        self.assertNotIn("Step 3/4:", result.stdout)


if __name__ == "__main__":
    unittest.main(verbosity=2)
