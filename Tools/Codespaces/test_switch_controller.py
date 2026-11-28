#!/usr/bin/env python3
"""Offline failure/recovery tests; no accounts, deployment or Unity license used."""
import io
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import switch_controller as control

OLD = "https://aaris-turbo-terminal.old-account.workers.dev"
NEW = "https://aaris-turbo-terminal.new-account.workers.dev"
TXN = "a" * 32
SHA = "b" * 40


class ControllerTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.base = Path(self.tmp.name)
        self.output = io.StringIO()
        self.c = control.Controller(self.base / "config", control.Reporter(self.output))
        self.c.op = {"id": TXN, "command": "cf", "canonical": control.CANONICAL,
                     "source_sha": SHA, "project_sha": "c" * 40,
                     "status": "RUNNING", "committed": False}
        self.c.config["operation"] = TXN
        self.c.save()

    def tearDown(self):
        self.c.close()
        self.tmp.cleanup()

    def test_atomic_state_rejects_symlink_and_preserves_previous(self):
        target = self.base / "untouched.json"
        target.write_text('{"active":"old"}')
        link = self.base / "config" / "unsafe.json"
        link.symlink_to(target)
        with self.assertRaises(control.SetupError):
            control.atomic_json(link, {"active": "new"})
        self.assertEqual(json.loads(target.read_text()), {"active": "old"})
        control.atomic_json(self.c.config_file, self.c.config)
        self.assertEqual(self.c.config_file.stat().st_mode & 0o777, 0o600)

    def test_lock_is_exclusive_released_and_not_inherited(self):
        first = control.Lock(self.base / "switch.lock")
        second = control.Lock(self.base / "switch.lock")
        try:
            self.assertTrue(first.acquire())
            self.assertFalse(os.get_inheritable(first.fd))
            self.assertFalse(second.acquire())
            first.close()
            self.assertTrue(second.acquire())
            self.assertTrue((self.base / "switch.lock").exists())
        finally:
            first.close()
            second.close()

    def test_error_box_contains_only_safe_events_not_oauth_or_raw_logs(self):
        self.c.runner_event("AARIS_CF_BROWSER_LOGIN_STARTED")
        self.c.runner_event("AARIS_CF_AUTH_URL=https://dash.cloudflare.com/oauth2/device?user_code=SECRET-CODE")
        self.c.runner_event("AARIS_CF_SECRETACCESSTOKEN")
        self.c.runner_event("\x1b[31mPASSWORD=private")
        self.c.reporter.error(control.SetupError("NETWORK_RETRY_REQUIRED", rc=1, http=503))
        box = self.output.getvalue().split("--- COPY FOR CHATGPT ---", 1)[1]
        self.assertIn("http_status=503", box)
        self.assertIn("AARIS_CF_BROWSER_LOGIN_STARTED", box)
        for secret in ("SECRET-CODE", "SECRETACCESSTOKEN", "PASSWORD", "https://", "\x1b"):
            self.assertNotIn(secret, box)

    def test_profiles_do_not_inherit_alternate_host_or_global_token(self):
        with patch.dict(os.environ, {"GH_HOST": "other.example", "GH_ENTERPRISE_TOKEN": "private", "GH_TOKEN": "private"}):
            env = self.c.profile_env("staged")
        self.assertEqual(env["GH_HOST"], "github.com")
        self.assertEqual(env["GH_CONFIG_DIR"], "staged")
        self.assertNotIn("GH_TOKEN", env)
        self.assertNotIn("GH_ENTERPRISE_TOKEN", env)

    def test_authenticated_redirect_is_not_followed(self):
        handler = control.NoRedirect()
        request = control.urllib.request.Request(OLD, headers={"Authorization": "Bearer private"})
        self.assertIsNone(handler.redirect_request(request, None, 302, "Found", {}, "https://other.example"))

    def test_pending_cloudflare_preserves_active_connection_and_host_secret(self):
        self.c.config["active"] = {"broker_url": OLD, "machine": "old-machine"}
        variables, secrets = {"AARIS_BROKER_URL": OLD}, {}
        def gh(args, profile=None, **kwargs):
            if args[:2] == ["variable", "get"]:
                return (0, variables.get(args[2], ""))
            if args[:2] == ["variable", "set"]:
                variables[args[2]] = args[-1]
                return 0, ""
            if args[:2] == ["secret", "set"]:
                secrets[args[2]] = kwargs.get("data")
            return 0, ""
        self.c.gh = gh
        self.c.runner.run = Mock(return_value=(0, ""))
        self.c.publish_connection(NEW, pending=True)
        self.assertEqual(variables["AARIS_BROKER_URL"], OLD)
        self.assertEqual(variables["AARIS_NEXT_BROKER_URL"], NEW)
        self.assertEqual(self.c.config["active"]["machine"], "old-machine")
        self.assertEqual(self.c.config["desired_broker"]["state"], "CF_READY_CODESPACE_PENDING")
        self.assertNotIn("CODESPACE_PAT", secrets)
        self.assertTrue(self.c.op["committed"])

    def test_probe_from_other_deployment_does_not_change_active(self):
        self.c.config["active"] = {"broker_url": OLD}
        self.c.credentials()
        self.c.request_json = lambda url, **kw: {
            "nonce": kw["data"]["nonce"], "storage_ready": True, "service": "aaris-turbo",
            "transaction_protocol": 1, "source_sha": SHA, "deployment_operation": "d" * 32}
        with self.assertRaisesRegex(control.SetupError, "CF_DURABLE_OBJECT_PROBE_FAILED"):
            self.c.verify_broker_candidate({"broker_url": NEW})
        self.assertEqual(self.c.config["active"]["broker_url"], OLD)
        self.assertFalse(self.c.op["committed"])

    def test_failed_current_broker_read_does_not_authorize_overwrite(self):
        self.c.gh = Mock(return_value=(1, "network unavailable"))
        with self.assertRaisesRegex(control.SetupError, "CANONICAL_CONFIG_READ_UNCONFIRMED"):
            self.c.set_broker_variable(NEW)
        self.assertFalse(any(call.args[0][:2] == ["variable", "set"] for call in self.c.gh.call_args_list))

    def test_unknown_cloudflare_abort_preserves_probe_key_and_operation(self):
        self.c.op["cf_preparation_started"] = True
        credentials = self.c.credentials()
        self.c.runner.run = Mock(return_value=(76, "AARIS_CF_ABORT_REQUIRES_REMOTE_RECONCILIATION"))
        self.c.cancel()
        self.assertEqual(self.c.op["status"], "RECONCILE_REQUIRED")
        self.assertEqual(control.read_json(self.c.secrets_file()), credentials)
        self.assertNotIn("CANCELLED", self.output.getvalue())

    def test_cancel_before_dispatch_removes_only_owned_staging(self):
        self.c.op["cf_preparation_started"] = True
        self.c.credentials()
        owned = self.c.base / "operations" / TXN / "github-staging"
        owned.mkdir()
        (owned / "hosts.yml").write_text("private")
        old = self.c.base / "codespace-gh"
        old.mkdir()
        (old / "hosts.yml").write_text("keep")
        self.c.runner.run = Mock(return_value=(0, ""))
        self.c.cancel()
        self.assertEqual(self.c.op["status"], "CANCELLED")
        self.assertFalse(owned.exists())
        self.assertFalse(self.c.secrets_file().exists())
        self.assertEqual((old / "hosts.yml").read_text(), "keep")

    def test_cancel_after_commit_does_not_revert_or_cancel_remote(self):
        self.c.op["committed"] = True
        self.c.gh = Mock(side_effect=AssertionError("No remote mutation after committed monitor cancellation"))
        self.c.cancel()
        self.assertTrue(self.c.op["committed"])
        self.assertNotEqual(self.c.op["status"], "CANCELLED")

    def test_commit_uncertainty_keeps_receipt_for_reconciliation(self):
        self.c.op["commit_requested"] = True
        credentials = self.c.credentials()
        self.c.cancel()
        self.assertEqual(self.c.op["status"], "RECONCILE_REQUIRED")
        self.assertEqual(control.read_json(self.c.secrets_file()), credentials)

    def test_real_commit_markers_are_followed_by_authoritative_json_status(self):
        self.c.op["machine"] = "exact-codespace"
        called = []
        def gh(args, *rest, **kwargs):
            called.append(args)
            return 0, "TURBO_TRANSACTION_READY\n" + json.dumps({"transaction_id": TXN, "state": "READY"})
        self.c.gh = gh
        result = self.c.remote_transaction("commit")
        self.assertEqual(result["state"], "READY")
        self.assertIn("--commit", called[0][-1])
        self.assertIn("--status", called[0][-1])
        self.assertIn(TXN, called[0][-1])
        self.assertIn("pwd.getpwuid", called[0][-1])
        self.assertEqual(called[0][3], "exact-codespace")

    def test_other_transaction_ready_is_not_accepted(self):
        self.c.op["machine"] = "exact-codespace"
        self.c.gh = Mock(return_value=(0, json.dumps({"transaction_id": "d" * 32, "state": "READY"})))
        with self.assertRaises(control.SetupError):
            self.c.remote_transaction("status")

    def test_reconcile_required_does_not_poll_forever(self):
        self.c.remote_transaction = Mock(return_value={"transaction_id": TXN, "state": "RECONCILE_REQUIRED"})
        with patch.object(control.time, "sleep", side_effect=AssertionError("must not poll terminal state")):
            with self.assertRaisesRegex(control.SetupError, "AGENT_HANDOVER_NOT_COMMITTED"):
                self.c.commit_agent()

    def test_exact_workflow_identity_not_latest_run(self):
        self.c.gh_json = Mock(return_value=[
            {"databaseId": 1, "displayTitle": "Aaris switch " + "f" * 32},
            {"databaseId": 2, "displayTitle": "Aaris switch " + TXN},
        ])
        self.assertEqual(self.c.find_run()["databaseId"], 2)

    def test_provider_failure_survives_without_raw_logs_or_false_missing_machine(self):
        logs = "runner step 2026-11-28 TURBO_PROVIDER_REQUEST_REJECTED\nrunner step TURBO_HTTP_STATUS=403\nprivate ghp_supersecret\nTURBO_SECRETACCESSTOKEN\n"
        result = self.c.workflow_failure(logs)
        self.assertEqual((result.code, result.http), ("TURBO_PROVIDER_REQUEST_REJECTED", 403))
        self.c.reporter.error(result)
        self.assertNotIn("ghp_supersecret", self.output.getvalue())
        self.assertNotIn("SECRETACCESSTOKEN", self.output.getvalue())

    def test_failed_optional_cleanup_is_queued_and_retried(self):
        self.c.op.update(committed=True, credential_staged=True, credential_secret="AARIS_STAGE_" + TXN.upper())
        self.c.gh = Mock(return_value=(1, "network unavailable"))
        self.c.cleanup_secret()
        self.c.retry_cleanup()
        self.assertEqual(len(self.c.config["cleanup"]), 1)
        self.assertTrue(self.c.op["committed"])
        self.c.gh = Mock(return_value=(0, ""))
        self.c.retry_cleanup()
        self.assertEqual(self.c.config["cleanup"], [])
        self.assertNotIn("❌", self.output.getvalue())

    def test_runtime_and_project_sources_are_distinct_immutable_values(self):
        commands = []
        def gh(args, *rest, **kwargs):
            commands.append(args[-1])
            return 0, json.dumps({"operation": TXN, "source_sha": "c" * 40, "phase": "STARTING"})
        self.c.gh = gh
        self.c.op["machine"] = "exact-codespace"
        self.c.provision("start")
        self.assertIn("--source-sha", commands[0])
        self.assertIn("c" * 40, commands[0])
        self.assertNotIn(SHA, commands[0])

    def test_cf_can_follow_committed_cd_without_losing_remote_setup_receipt(self):
        self.c.op.update(command="codespace", committed=True, status="WAITING_FOR_UNITY_LOGIN", machine="same-codespace")
        self.c.save()
        self.c.load_operation("cf")
        self.assertEqual(self.c.op["command"], "cf")
        self.assertNotEqual(self.c.op["id"], TXN)
        self.assertEqual(self.c.config["project_operation"], TXN)
        self.c.op["status"] = "COMPLETE"
        self.c.config["active"] = {"machine": "same-codespace"}
        self.c.config["desired_broker"] = {"state": "CONNECTED"}
        self.c.save()
        self.c.load_operation("codespace")
        self.assertEqual(self.c.op["id"], TXN)
        self.assertEqual(self.c.op["status"], "WAITING_FOR_UNITY_LOGIN")

    def test_broker_pending_cd_selects_new_host_instead_of_resuming_old_setup(self):
        project = {**self.c.op, "command": "codespace", "committed": True,
                   "status": "PROVISIONING", "machine": "stopped-codespace"}
        control.atomic_json(self.c.base / "operations" / TXN / "operation.json", project)
        self.c.config.update(project_operation=TXN, operation="d" * 32,
                             active={"machine": "stopped-codespace"},
                             desired_broker={"state": "CF_READY_CODESPACE_PENDING"})
        control.atomic_json(self.c.base / "operations" / ("d" * 32) / "operation.json", {"status": "COMPLETE", "command": "cf"})
        self.c.load_operation("codespace")
        self.assertNotEqual(self.c.op["id"], TXN)
        self.assertNotIn("machine", self.c.op)

    def test_shell_shortcuts_preserve_directory_arguments(self):
        script = Path(__file__).with_name("termux_functions.sh")
        code = 'source "$1"; _aaris_control() { printf "CONTROL:%s\\n" "$*"; }; cd "$2"; pwd; CCF; CD --status; cd --status; cd; cd ..; pwd'
        result = subprocess.run(["bash", "--noprofile", "--norc", "-ic", code, "aaris-test", str(script), str(self.base)],
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, check=True)
        self.assertIn(str(self.base), result.stdout)
        self.assertIn("CONTROL:cf", result.stdout)
        self.assertEqual(result.stdout.count("CONTROL:codespace --status"), 2)
        self.assertIn("CONTROL:codespace\n", result.stdout)

    def test_shortcuts_install_over_old_aliases_without_bash_parse_error(self):
        script = Path(__file__).with_name("termux_functions.sh")
        code = 'alias cf="echo OLD"; alias cd="echo OLD"; source "$1"; _aaris_control() { printf "CONTROL:%s\\n" "$*"; }; cf; cd "$2"; pwd'
        result = subprocess.run(["bash", "--noprofile", "--norc", "-ic", code, "aaris-test", str(script), str(self.base)],
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, check=True)
        self.assertIn("CONTROL:cf", result.stdout)
        self.assertNotIn("syntax error", result.stderr)
        self.assertIn(str(self.base), result.stdout)


class ProcessOwnershipTests(unittest.TestCase):
    def test_transport_cancel_stops_owned_process_but_leaves_unrelated_alive(self):
        with tempfile.TemporaryDirectory() as directory:
            runner = control.Runner(Path(directory), control.Reporter(io.StringIO()))
            unrelated = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"], start_new_session=True)
            try:
                with self.assertRaisesRegex(control.SetupError, "TRANSPORT_OUTCOME_UNCONFIRMED"):
                    runner.run([sys.executable, "-c", "import time; time.sleep(30)"], transport_limit=0.2)
                self.assertFalse(runner.children)
                self.assertIsNone(unrelated.poll())
            finally:
                unrelated.terminate()
                unrelated.wait(timeout=5)


if __name__ == "__main__":
    unittest.main()
