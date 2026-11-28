#!/usr/bin/env python3
"""Local fault tests; all writes are confined to fresh /tmp fixture homes."""
import base64
import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import time
import unittest
import sys
import subprocess
from unittest.mock import patch
from types import SimpleNamespace

ROOT=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location("transaction_install",ROOT/"transaction_install.py")
install=importlib.util.module_from_spec(spec)
spec.loader.exec_module(install)
agent_spec=importlib.util.spec_from_file_location("transaction_agent",ROOT/"agent.py")
agent=importlib.util.module_from_spec(agent_spec)
agent_spec.loader.exec_module(agent)
TX="a"*32


class FakeBridge:
    def __init__(self):
        self.phase="prepared"
        self.probed=False
        self.jobs=0
        self.expired=0
        self.selected=False
        self.events=[]
        self.lose_next_status=False

    def transaction(self,transaction_id,operation,**payload):
        self.events.append(operation)
        if operation=="status" and self.lose_next_status:
            self.lose_next_status=False
            raise ConnectionError("response_lost")
        if operation=="begin-cutover":
            assert self.probed
            self.phase="draining"
        if operation=="abort":
            assert self.phase in {"prepared","draining"}
            self.phase="aborted"
        return {"ok":True,"transaction_id":transaction_id,"state":self.phase,
                "running_jobs":self.jobs,"expired_running_jobs":self.expired,
                "probe_verified":self.probed,"selected":self.selected}

    def prepare_probe(self,transaction_id):
        self.events.append("probe")
        self.probed=True


class FakeModule:
    ProtocolConflict=agent.ProtocolConflict


class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.temporary=tempfile.TemporaryDirectory(prefix="aaris-txn-test-",dir="/tmp")
        self.layout=install.Layout(self.temporary.name)
        self.environment={
            "AARIS_TRANSACTION_ID":TX,"AARIS_AGENT_TOKEN":"2"*64,
            "AARIS_ENDPOINT":"https://new.workers.dev/agent/codespace-test",
            "AARIS_AGENT_EXPIRES_AT":str(int(time.time())+86400),
            "AARIS_AGENT_GENERATION":"1","AARIS_TRANSACTION_PREPARE_ONLY":"1"}
        for name,key in (("agent.py","AARIS_AGENT_BASE64"),("gui_agent.py","AARIS_GUI_BASE64"),
                         ("gui_setup.sh","AARIS_GUI_SETUP_BASE64")):
            self.environment[key]=base64.b64encode((ROOT/name).read_bytes()).decode()

    def tearDown(self):
        self.temporary.cleanup()

    def legacy(self,same_endpoint=True):
        files=self.layout.active_files()
        for name,path in files.items():
            data={"agent.py":b"# untouched legacy agent\n","gui.py":b"# original gui\n",
                  "gui_setup.sh":b"#!/bin/sh\nexit 0\n","token":("1"*64+"\n").encode(),
                  "endpoint":((self.environment["AARIS_ENDPOINT"] if same_endpoint else
                      "https://old.workers.dev/agent/codespace-test")+"\n").encode(),
                  "expires_at":(str(int(time.time())+86400)+"\n").encode()}[name]
            install.atomic_bytes(path,data)
        return {name:path.read_bytes() for name,path in files.items()}

    def assert_legacy(self,expected):
        self.assertIsNone(self.layout.current())
        self.assertEqual(expected,{name:path.read_bytes() for name,path in self.layout.active_files().items()})

    def stage(self):
        # Actual compilation/syntax verification still happens. Child selftests
        # are covered separately and are mocked here to speed fault fixtures.
        with patch.object(install.subprocess,"run"):
            return install.stage(self.layout,self.environment)

    def run_fake(self,bridge,**patches):
        stack=contextlib.ExitStack()
        with stack:
            stack.enter_context(patch.object(install,"agent_module",return_value=FakeModule))
            stack.enter_context(patch.object(install,"bridge_for",return_value=bridge))
            for name,value in patches.items():
                stack.enter_context(patch.object(install,name,value))
            install.run(self.layout,TX)

    def test_prepare_preserves_all_active_bytes_and_never_starts_agent(self):
        original=self.legacy()
        self.stage()
        self.assert_legacy(original)
        bridge=FakeBridge()
        with patch.object(install,"launch_agent") as launch,patch.object(install,"stop_owned_agent") as stop:
            self.run_fake(bridge)
            launch.assert_not_called();stop.assert_not_called()
        self.assertEqual(install.state(self.layout,TX)["state"],"PREPARED")
        self.assert_legacy(original)
        self.assertEqual(bridge.events,["status","probe","status"])

    def test_precommit_abort_keeps_active_bytes_and_old_agent(self):
        original=self.legacy()
        self.stage()
        install.request_action(self.layout.transaction(TX),"abort")
        bridge=FakeBridge()
        with patch.object(install,"stop_owned_agent") as stop:
            self.run_fake(bridge)
            stop.assert_not_called()
        self.assertEqual(install.state(self.layout,TX)["state"],"ABORTED")
        self.assert_legacy(original)

    def test_request_conflict_cannot_turn_cancel_into_commit(self):
        self.stage()
        directory=self.layout.transaction(TX)
        install.request_action(directory,"abort")
        with self.assertRaisesRegex(install.InstallError,"TRANSACTION_ABORT_REQUESTED"):
            install.request_action(directory,"commit")
        self.assertFalse((directory/"commit.request").exists())

    def test_postcommit_abort_requires_reconciliation(self):
        self.stage()
        directory=self.layout.transaction(TX)
        install.request_action(directory,"commit")
        with self.assertRaisesRegex(install.InstallError,"COMMIT_OUTCOME_MUST_BE_RECONCILED"):
            install.request_action(directory,"abort")
        self.assertFalse((directory/"abort.request").exists())

    def test_replay_is_immutable_and_changed_candidate_does_not_touch_active(self):
        original=self.legacy()
        self.stage();self.stage()
        self.environment["AARIS_AGENT_TOKEN"]="3"*64
        with self.assertRaisesRegex(install.InstallError,"TRANSACTION_PAYLOAD_CHANGED"):
            self.stage()
        self.assert_legacy(original)

    def test_invalid_candidate_is_rejected_before_active_changes(self):
        original=self.legacy()
        self.environment["AARIS_AGENT_BASE64"]=base64.b64encode(b"invalid python !\n").decode()
        with self.assertRaises(SyntaxError):
            self.stage()
        self.assert_legacy(original)

    def test_commit_waits_for_running_jobs_and_recovers_lost_receipt(self):
        self.legacy()
        self.stage()
        install.request_action(self.layout.transaction(TX),"commit")
        bridge=FakeBridge();bridge.jobs=1
        candidate=self.layout.generations/TX
        old={"pid":1234,"start_ticks":"100","argv":[str(self.layout.bin/"agent.py")]}
        live={"value":old}
        stopped=[];started=[]
        def sleep(_):
            bridge.jobs=0
            if bridge.phase=="committed":
                bridge.phase="ready";bridge.selected=True
        def stop(layout,identity):
            self.assertEqual(bridge.jobs,0)
            self.assertEqual(bridge.phase,"draining")
            stopped.append(identity)
            live["value"]=None
        def launch(layout,generation,transaction_id=""):
            if live["value"] is None:
                self.assertEqual(generation,candidate)
                self.assertEqual(layout.current(),candidate)
                started.append(transaction_id)
                live["value"]={"pid":4321,"start_ticks":"200","argv":[str(candidate/"agent.py")]}
                bridge.phase="committed"
                bridge.lose_next_status=True
            return live["value"]
        with patch.object(self.layout,"active_agent",side_effect=lambda:live["value"]),\
             patch.object(install.time,"sleep",side_effect=sleep),\
             patch.object(install.subprocess,"Popen"):
            self.run_fake(bridge,stop_owned_agent=stop,launch_agent=launch)
        self.assertEqual(len(stopped),1)
        self.assertEqual(started,[TX])
        self.assertEqual(install.state(self.layout,TX)["state"],"READY")

    def test_unexpected_active_edit_blocks_cutover(self):
        self.legacy();self.stage()
        (self.layout.config/"endpoint").write_text("https://other.workers.dev/agent/codespace-test\n")
        install.request_action(self.layout.transaction(TX),"commit")
        bridge=FakeBridge()
        with patch.object(install,"stop_owned_agent") as stop:
            self.run_fake(bridge)
            stop.assert_not_called()
        result=install.state(self.layout,TX)
        self.assertEqual(result["state"],"RECONCILE_REQUIRED")
        self.assertEqual(result["error"],"ACTIVE_CONFIGURATION_CHANGED")

    def test_prepare_status_does_not_claim_commit_or_expose_secrets(self):
        self.stage()
        install.update(self.layout,TX,"PREPARED")
        install.request_action(self.layout.transaction(TX),"commit")
        output=io.StringIO()
        with contextlib.redirect_stdout(output):
            code=install.await_result(self.layout,TX,0)
        self.assertEqual(code,75)
        self.assertNotIn("TURBO_TRANSACTION_READY",output.getvalue())
        self.assertNotIn(self.environment["AARIS_AGENT_TOKEN"],json.dumps(install.state(self.layout,TX)))

    def test_flock_is_singleton_and_pid_identity_detects_reuse(self):
        path=self.layout.config/"agent.lock"
        descriptor=agent.singleton_lock(path)
        inode=path.stat().st_ino
        try:
            with self.assertRaisesRegex(SystemExit,"ALREADY_RUNNING"):
                agent.singleton_lock(path)
        finally:
            os.close(descriptor)
        descriptor=agent.singleton_lock(path);os.close(descriptor)
        self.assertEqual(path.stat().st_ino,inode)
        identity=install.process_identity(os.getpid())
        self.assertTrue(install.same_process(identity))
        identity["start_ticks"]="0"
        self.assertFalse(install.same_process(identity))

    def test_generation_symlink_cannot_escape_private_root(self):
        (self.layout.config/"current").symlink_to("/tmp")
        with self.assertRaisesRegex(install.InstallError,"UNSAFE_GENERATION_POINTER"):
            self.layout.current()

    def test_passwd_home_wins_over_wrong_ssh_home(self):
        with patch.dict(os.environ,{"HOME":"/root/not-the-codespace-user"}), \
             patch.object(install.pwd,"getpwuid",return_value=SimpleNamespace(pw_dir=self.temporary.name)):
            self.assertEqual(install.Layout().home,Path(self.temporary.name))
            self.assertEqual(agent.scrub_env()["HOME"],self.temporary.name)
            self.assertEqual(os.environ["HOME"],"/root/not-the-codespace-user")

    def test_commit_request_queues_one_successor_while_prepare_is_exiting(self):
        self.stage()
        directory=self.layout.transaction(TX)
        install.update(self.layout,TX,"PREPARED")
        install.atomic_json(directory/"supervisor.json",{"pid":123,"start_ticks":"1","argv":["prepare"],"purpose":"prepare"})
        install.request_action(directory,"commit")
        with patch.object(install,"same_process",return_value=True), \
             patch.object(install,"process_identity",return_value={"pid":456,"start_ticks":"2","argv":["commit"]}), \
             patch.object(install.subprocess,"Popen") as popen:
            popen.return_value.pid=456
            install.spawn(self.layout,TX)
            install.spawn(self.layout,TX)
        self.assertEqual(popen.call_count,1)
        self.assertEqual(install.load_json(directory/"supervisor.json")["purpose"],"commit")

    def test_probe_failure_publishes_reconciliation_instead_of_dead_wait(self):
        original=self.legacy();self.stage()
        bridge=FakeBridge()
        with patch.object(bridge,"prepare_probe",side_effect=RuntimeError("candidate_probe_failed")):
            self.run_fake(bridge)
        self.assertEqual(install.state(self.layout,TX)["state"],"RECONCILE_REQUIRED")
        self.assert_legacy(original)

    def test_expired_job_is_ambiguous_not_silently_drained(self):
        original=self.legacy();self.stage()
        install.request_action(self.layout.transaction(TX),"commit")
        bridge=FakeBridge();bridge.jobs=1;bridge.expired=1
        with patch.object(install,"stop_owned_agent") as stop:
            self.run_fake(bridge)
        stop.assert_not_called()
        status=install.state(self.layout,TX)
        self.assertEqual(status["state"],"RECONCILE_REQUIRED")
        self.assertEqual(status["error"],"JOB_COMPLETION_UNCONFIRMED")
        self.assert_legacy(original)

    def test_upload_failure_stops_descendant_even_after_group_leader_exits(self):
        bridge=agent.Bridge("https://example.invalid/agent/codespace-test","1"*64)
        child={"pid":None,"reported":False}
        code=("import os,subprocess,sys; p=subprocess.Popen([sys.executable,'-c',"
              "'import time; time.sleep(60)']); print(p.pid,flush=True); os._exit(0)")
        argv=[sys.executable,"-c",code]
        def post(operation,payload):
            if operation=="chunk":
                child["pid"]=int(payload["text"].strip())
                # The leader has exited; its unreaped PID still owns the group.
                time.sleep(0.1)
                raise PermissionError("session_expired")
            if operation=="done":
                child["reported"]=True
                try:
                    state=Path("/proc",str(child["pid"]),"stat").read_text().rsplit(")",1)[1].split()[0]
                except FileNotFoundError:
                    state="Z"
                self.assertEqual(state,"Z")
                self.assertEqual(payload["error"],"SESSION_REVOKED")
                return {"ok":True}
            raise AssertionError(operation)
        with patch.object(agent,"validate_job",return_value=("fixture",argv,self.temporary.name,5)), \
             patch.object(bridge,"post",side_effect=post):
            bridge.stream_job({"id":"fixture"})
        self.assertTrue(child["reported"])
        self.assertEqual(bridge.pending_reports,[])


if __name__=="__main__":
    unittest.main()
