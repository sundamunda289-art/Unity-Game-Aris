#!/usr/bin/env python3
"""Failure-oriented bootstrap tests. No GitHub, Cloudflare or SSH calls."""
import base64
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import shlex
import sys
import tempfile
from types import SimpleNamespace
import unittest
import urllib.error
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("bootstrap_transaction", ROOT / "bootstrap_transaction.py")
bootstrap = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bootstrap)
TX, SOURCE, MACHINE = "a" * 32, "b" * 40, "codespace-transaction-test"


def response(code=0, value=None, stderr=""):
    stdout = json.dumps(value) if not isinstance(value, str) else value
    return subprocess.CompletedProcess([], code, stdout or "", stderr)


class BootstrapTests(unittest.TestCase):
    def test_definite_write_denial_is_not_unknown_outcome(self):
        for status in (400, 401, 403, 404, 409, 422, 429):
            with self.subTest(status=status), patch.object(bootstrap, "run", return_value=response(1, "private provider payload", "HTTP " + str(status))) as runner:
                with self.assertRaises(bootstrap.Failure) as caught:
                    bootstrap.api("repos/owner/repo/codespaces", "POST", {}, check=False)
                self.assertEqual(caught.exception.http, status)
                self.assertEqual(runner.call_count, 1)

    def test_unknown_write_is_never_automatically_resent(self):
        for stderr in ("connection closed", "HTTP 503"):
            with patch.object(bootstrap, "run", return_value=response(1, "", stderr)) as runner:
                self.assertIsNone(bootstrap.api("write", "POST", {}, check=False))
                self.assertEqual(runner.call_count, 1)

    def test_failed_read_retries_but_auth_denial_stops(self):
        with patch.object(bootstrap, "run", side_effect=[response(1, "", "network"), response(1, "", "HTTP 403")]) as runner, patch.object(bootstrap.time, "sleep"), contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaises(bootstrap.Failure) as caught:
                bootstrap.api("user")
            self.assertEqual(caught.exception.http, 403)
            self.assertEqual(runner.call_count, 2)

    def test_auth_redirect_is_rejected_without_second_request(self):
        opener = Mock()
        opener.open.side_effect = urllib.error.HTTPError("https://example.invalid", 302, "redirect", {}, None)
        with patch.object(bootstrap.urllib.request, "build_opener", return_value=opener) as build:
            with self.assertRaises(bootstrap.Failure) as caught:
                bootstrap.request("https://example.invalid", bearer="private-credential")
        self.assertEqual(caught.exception.http, 302)
        self.assertEqual(opener.open.call_count, 1)
        handler = build.call_args.args[0]
        self.assertIsInstance(handler, bootstrap.NoRedirect)
        self.assertIsNone(handler.redirect_request(None, None, 302, "", {}, "https://other.invalid"))

    def test_create_uses_reviewed_canonical_source_not_host_fork(self):
        existing = {"name": MACHINE, "display_name": "aaris-setup-" + TX[:16],
                    "repository": {"full_name": bootstrap.CANONICAL}}
        values = [{"login": "hosting-account"}, {"codespaces": []},
                  {"machines": [{"name": "standardLinux32gb"}]}, None,
                  {"codespaces": [existing]}]
        with patch.object(bootstrap, "api", side_effect=values) as api, patch.object(bootstrap, "verify_minimal_devcontainer") as verify, patch.object(bootstrap.time, "sleep"), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(bootstrap.choose_machine(TX, "hosting-account", "auto", SOURCE), MACHINE)
        verify.assert_called_once_with(SOURCE)
        writes = [call for call in api.call_args_list if len(call.args) > 1 and call.args[1] == "POST"]
        self.assertEqual(len(writes), 1)
        self.assertEqual(writes[0].args[0], "repos/" + bootstrap.CANONICAL + "/codespaces")
        self.assertEqual(writes[0].args[2]["ref"], SOURCE)
        self.assertFalse(any("fork" in call.args[0] for call in api.call_args_list))

    def test_exact_existing_machine_does_not_create_or_restart(self):
        values = [{"login": "hosting-account"}, {"codespaces": [{"name": MACHINE,
                  "repository": {"full_name": "hosting-account/Unity-Game-Aris"}}]},
                  {"fork":True,"parent":{"full_name":bootstrap.CANONICAL}}]
        with patch.object(bootstrap, "api", side_effect=values) as api, contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(bootstrap.choose_machine(TX, "hosting-account", MACHINE, SOURCE), MACHINE)
        self.assertEqual(api.call_count, 3)

    def test_new_request_preserves_old_and_reconciles_its_own_display(self):
        old={"name":"old-stopped-machine","repository":{"full_name":bootstrap.CANONICAL}}
        ours={"name":MACHINE,"display_name":"aaris-setup-"+TX[:16],"repository":{"full_name":bootstrap.CANONICAL}}
        with patch.object(bootstrap,"api",side_effect=[{"login":"hosting-account"},{"codespaces":[old,ours]}]) as api, contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(bootstrap.choose_machine(TX,"hosting-account","new",SOURCE),MACHINE)
            self.assertEqual(api.call_count,2)

    def test_unrelated_same_name_repository_cannot_be_selected(self):
        info={"name":MACHINE,"repository":{"full_name":"hosting-account/Unity-Game-Aris"}}
        with patch.object(bootstrap,"api",return_value={"fork":False}):
            with self.assertRaisesRegex(bootstrap.Failure,"NOT_CANONICAL_FORK"):
                bootstrap.verified_machine(info)

    def test_stopped_legacy_hooks_require_review_before_start(self):
        with patch.object(bootstrap, "api", return_value={"state": "Shutdown", "devcontainer_path": ".devcontainer/devcontainer.json"}) as api:
            with self.assertRaisesRegex(bootstrap.Failure, "EXISTING_STARTUP_REVIEW_REQUIRED"):
                bootstrap.wait_available(MACHINE, TX, SOURCE)
        self.assertEqual(api.call_count, 1)

    def test_start_quota_denial_is_reported_once(self):
        info = {"state": "Stopped", "display_name": "aaris-setup-" + TX[:16],
                "repository": {"full_name": bootstrap.CANONICAL}, "devcontainer_path": bootstrap.DEVCONTAINER,
                "git_status": {"ref": SOURCE, "has_uncommitted_changes": False, "has_unpushed_changes": False}}
        with patch.object(bootstrap, "api", side_effect=[info, bootstrap.Failure("TURBO_PROVIDER_REQUEST_REJECTED", 403)]) as api:
            with self.assertRaises(bootstrap.Failure) as caught:
                bootstrap.wait_available(MACHINE, TX, SOURCE)
        self.assertEqual(caught.exception.http, 403)
        self.assertEqual(api.call_count, 2)

    def test_source_must_be_canonical_main_ancestor_and_actual_checkout(self):
        with patch.object(bootstrap, "run", return_value=response(value=SOURCE + "\n")), patch.object(bootstrap, "api", side_effect=[{"object": {"sha": "c" * 40}}, {"status": "diverged", "merge_base_commit": {"sha": "d" * 40}}]):
            with self.assertRaisesRegex(bootstrap.Failure, "SOURCE_NOT_CANONICAL_MAIN_HISTORY"):
                bootstrap.validate_source({"AARIS_SOURCE_SHA": SOURCE})
        with patch.object(bootstrap, "run", return_value=response(value="c" * 40)), patch.object(bootstrap, "api") as api:
            with self.assertRaisesRegex(bootstrap.Failure, "CHECKOUT_SOURCE_MISMATCH"):
                bootstrap.validate_source({"AARIS_SOURCE_SHA": SOURCE})
            api.assert_not_called()

    def test_devcontainer_with_early_lfs_hook_is_rejected(self):
        data = json.loads((ROOT.parents[1] / bootstrap.DEVCONTAINER).read_text())
        data["postCreateCommand"] = "git lfs pull"
        with patch.object(bootstrap, "api", return_value={"content": base64.b64encode(json.dumps(data).encode()).decode()}):
            with self.assertRaisesRegex(bootstrap.Failure, "UNITY_FIRST_DEVCONTAINER_REQUIRED"):
                bootstrap.verify_minimal_devcontainer(SOURCE)

    def test_lost_stage_response_replays_exact_payload_after_confirmed_absence(self):
        missing = {"transaction_id": TX, "state": "NOT_FOUND"}
        ready = {"transaction_id": TX, "state": "PREPARED", "machine": MACHINE, "generation": 2}
        values = [response(75, ""), response(value=missing), response(), response(value=ready)]
        with patch.object(bootstrap, "run", side_effect=values) as runner, patch.object(bootstrap.time, "sleep"), contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(bootstrap.install_and_observe(MACHINE, TX, 2, "immutable-private-payload", True), 0)
        payloads = [call.kwargs["payload"] for call in runner.call_args_list if "payload" in call.kwargs]
        self.assertEqual(payloads, ["immutable-private-payload"] * 2)
        self.assertNotIn("immutable-private-payload", output.getvalue())

    def test_remote_status_identity_is_mandatory(self):
        for key, wrong in (("transaction_id", "c" * 32), ("machine", "other-machine"), ("generation", 9)):
            status = {"transaction_id": TX, "state": "READY", "machine": MACHINE, "generation": 2, key: wrong}
            with self.subTest(key=key), patch.object(bootstrap, "run", side_effect=[response(), response(value=status)]), contextlib.redirect_stdout(io.StringIO()) as output:
                with self.assertRaises(bootstrap.Failure):
                    bootstrap.install_and_observe(MACHINE, TX, 2, "payload", True)
                self.assertNotIn("TURBO_TRANSACTION_READY", output.getvalue())

    def test_raw_remote_markers_do_not_override_reconcile_failure(self):
        status = {"transaction_id": TX, "state": "RECONCILE_REQUIRED", "machine": MACHINE, "generation": 2}
        values = [response(value="TURBO_TRANSACTION_READY\nTURBO_PRIVATE_SECRET=private\n"), response(value=status)]
        with patch.object(bootstrap, "run", side_effect=values), contextlib.redirect_stdout(io.StringIO()) as output:
            with self.assertRaisesRegex(bootstrap.Failure, "RECONCILE_REQUIRED"):
                bootstrap.install_and_observe(MACHINE, TX, 2, "payload", True)
        self.assertNotIn("READY", output.getvalue())
        self.assertNotIn("private", output.getvalue())

    def test_status_wrapper_uses_passwd_home_with_wrong_ssh_home(self):
        import os
        import pwd
        command=shlex.split(bootstrap.remote_invocation(TX,"status"))
        self.assertEqual(command[:2],["python3","-c"])
        with tempfile.TemporaryDirectory(prefix="aaris-bootstrap-test-",dir="/tmp") as fixture, \
             patch.dict(os.environ,{"HOME":"/root/wrong-ssh-home"}), \
             patch.object(pwd,"getpwuid",return_value=SimpleNamespace(pw_dir=fixture)), \
             patch.object(sys,"argv",[]), contextlib.redirect_stdout(io.StringIO()) as output:
            exec(compile(command[2],"remote-status-wrapper","exec"),{})
        self.assertEqual(json.loads(output.getvalue()),{"transaction_id":TX,"state":"NOT_FOUND"})


if __name__ == "__main__":
    unittest.main()
