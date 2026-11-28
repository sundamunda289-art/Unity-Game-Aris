#!/usr/bin/env python3
"""Private, resumable Turbo agent generation installer (standard library only).

Transport contract: remote_install.sh supplies the legacy AARIS_* payload plus
AARIS_TRANSACTION_ID, AARIS_AGENT_GENERATION, AARIS_TRANSACTION_PREPARE_ONLY.
--install stages and starts a detached PREPARE. --commit ID is the explicit
durable cutover request. --abort ID never undoes an already requested commit.
--status ID returns a secret-free journal; --resume ID resumes its requested
phase. PREPARED and READY return 0; a still-running operation returns 75.
No source checkout, Unity process, or unrelated PID is modified by this helper.
"""
from __future__ import annotations

import argparse
import base64
import contextlib
import fcntl
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import pwd
import re
import signal
import ssl
import subprocess
import sys
import time
import urllib.error

TX_RE = re.compile(r"^[a-f0-9]{32}$")
ENDPOINT_RE = re.compile(r"^https://[a-z0-9.-]+\.workers\.dev/agent/[A-Za-z0-9-]{3,120}$")
TERMINAL = {"READY", "ABORTED", "FAILED", "ROLLED_BACK", "RECONCILE_REQUIRED"}


class InstallError(RuntimeError):
    pass


def private_directory(path):
    path = Path(path)
    if path.is_symlink():
        raise InstallError("UNSAFE_DIRECTORY")
    path.mkdir(parents=True, mode=0o700, exist_ok=True)
    if path.stat().st_uid != os.getuid():
        raise InstallError("UNSAFE_DIRECTORY_OWNER")
    path.chmod(0o700)
    return path


def atomic_bytes(path, data, mode=0o600):
    path = Path(path)
    temporary = path.with_name(path.name + "." + str(os.getpid()) + ".tmp")
    fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, mode)
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
        directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        if temporary.exists():
            temporary.unlink()


def request_action(directory, action):
    with lock(Path(directory)/"request.lock"):
        opposite = "abort" if action=="commit" else "commit"
        if (Path(directory)/(opposite+".request")).exists():
            raise InstallError("COMMIT_OUTCOME_MUST_BE_RECONCILED" if action=="abort" else "TRANSACTION_ABORT_REQUESTED")
        if not (Path(directory)/(action+".request")).exists():
            atomic_bytes(Path(directory)/(action+".request"),(action+"\n").encode())


def atomic_json(path, data):
    atomic_bytes(path, (json.dumps(data, sort_keys=True) + "\n").encode())


def load_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


@contextlib.contextmanager
def lock(path, blocking=False):
    fd = os.open(path, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    try:
        if os.fstat(fd).st_uid != os.getuid() or os.fstat(fd).st_mode & 0o077:
            raise InstallError("UNSAFE_LOCK")
        fcntl.flock(fd, fcntl.LOCK_EX | (0 if blocking else fcntl.LOCK_NB))
        yield
    finally:
        os.close(fd)


def process_identity(pid):
    if not isinstance(pid, int) or pid < 2:
        return None
    try:
        fields = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
        if fields[0] == "Z":
            return None
        args = Path(f"/proc/{pid}/cmdline").read_bytes().split(b"\0")
        return {"pid": pid, "start_ticks": fields[19],
                "argv": [a.decode("utf-8", "replace") for a in args if a]}
    except (OSError, ValueError, IndexError):
        return None


def same_process(expected):
    actual = process_identity(expected.get("pid", 0)) if expected else None
    return bool(actual and all(actual[key] == expected.get(key) for key in ("pid","start_ticks","argv")))


class Layout:
    def __init__(self, home=None):
        self.home = Path(home or pwd.getpwuid(os.geteuid()).pw_dir).resolve()
        for path in (self.home/".local/share/aaris-turbo", self.home/".config/aaris-turbo"):
            if not path.resolve().is_relative_to(self.home):
                raise InstallError("PRIVATE_ROOT_ESCAPES_HOME")
        self.bin = private_directory(self.home / ".local/share/aaris-turbo")
        self.config = private_directory(self.home / ".config/aaris-turbo")
        self.generations = private_directory(self.bin / "generations")
        self.transactions = private_directory(self.config / "transactions")
        self.install_lock = self.config / "install.lock"

    def transaction(self, transaction_id):
        if not TX_RE.fullmatch(transaction_id):
            raise InstallError("INVALID_TRANSACTION_ID")
        return self.transactions / transaction_id

    def current(self):
        pointer = self.config / "current"
        if pointer.is_symlink():
            target = pointer.resolve(strict=True)
            if target.parent != self.generations:
                raise InstallError("UNSAFE_GENERATION_POINTER")
            return target
        if pointer.exists():
            raise InstallError("UNSAFE_GENERATION_POINTER")
        return None

    def active_files(self):
        current = self.current()
        if current:
            return {name: current / name for name in ("agent.py", "gui.py", "gui_setup.sh", "token", "endpoint", "expires_at")}
        return {"agent.py": self.bin / "agent.py", "gui.py": self.bin / "gui.py",
                "gui_setup.sh": self.bin / "gui_setup.sh", "token": self.config / "token",
                "endpoint": self.config / "endpoint", "expires_at": self.config / "expires_at"}

    def fingerprint(self):
        values = {}
        for name, path in self.active_files().items():
            if path.exists():
                if not path.is_file():
                    raise InstallError("UNSAFE_ACTIVE_FILE")
                values[name] = hashlib.sha256(path.read_bytes()).hexdigest()
        return hashlib.sha256(json.dumps(values, sort_keys=True).encode()).hexdigest()

    def active_agent(self):
        pid_file = self.config / "agent.pid"
        try:
            identity = process_identity(int(pid_file.read_text().strip()))
        except (OSError, ValueError):
            identity=None
        current = self.current()
        if not identity and current and (current/"agent-status.json").is_file():
            status=load_json(current/"agent-status.json")
            possible=process_identity(status.get("pid",0))
            if possible and possible["start_ticks"]==status.get("start_ticks"):
                identity=possible
        if not identity:
            return None
        allowed = {str(self.bin / "agent.py")}
        if current:
            allowed.add(str(current / "agent.py"))
        if not any(arg in allowed for arg in identity["argv"]):
            raise InstallError("AGENT_PID_OWNERSHIP_MISMATCH")
        return identity

    def set_current(self, generation):
        generation = Path(generation).resolve(strict=True)
        if generation.parent != self.generations:
            raise InstallError("UNSAFE_GENERATION")
        temporary = self.config / ("current." + str(os.getpid()) + ".tmp")
        temporary.symlink_to(generation)
        os.replace(temporary, self.config / "current")
        # Existing callers keep their familiar paths. The pointer is the sole
        # source of truth for the entire generation, including credentials.
        for name in ("agent.py", "gui.py", "gui_setup.sh"):
            self._alias(self.bin / name, self.config / "current" / name)
        for name in ("token", "endpoint", "expires_at"):
            self._alias(self.config / name, self.config / "current" / name)

    @staticmethod
    def _alias(path, target):
        temporary = path.with_name(path.name + "." + str(os.getpid()) + ".link")
        temporary.symlink_to(target)
        os.replace(temporary, path)


def state(layout, transaction_id):
    directory = layout.transaction(transaction_id)
    path = directory / "state.json"
    if not path.exists():
        return {"transaction_id": transaction_id, "state": "NOT_FOUND"}
    data = load_json(path)
    # Only these non-secret fields may cross SSH or appear in a status screen.
    return {key: data.get(key) for key in ("transaction_id", "state", "generation", "machine",
            "updated_at", "error", "backend_state", "running_jobs", "expired_running_jobs") if key in data}


def update(layout, transaction_id, phase, **extra):
    path = layout.transaction(transaction_id) / "state.json"
    data = load_json(path)
    data.update(state=phase, updated_at=int(time.time()), **extra)
    atomic_json(path, data)


def agent_module(generation):
    spec = importlib.util.spec_from_file_location("aaris_transaction_agent", Path(generation) / "agent.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def bridge_for(generation, module):
    return module.Bridge((Path(generation)/"endpoint").read_text().strip(),
                         (Path(generation)/"token").read_text().strip())


def stage(layout, environ):
    transaction_id = environ.get("AARIS_TRANSACTION_ID", "")
    directory = layout.transaction(transaction_id)
    token = environ.get("AARIS_AGENT_TOKEN", "")
    endpoint = environ.get("AARIS_ENDPOINT", "")
    expiry = environ.get("AARIS_AGENT_EXPIRES_AT", "")
    generation_number = environ.get("AARIS_AGENT_GENERATION", "")
    if not re.fullmatch("[a-f0-9]{64}", token) or not ENDPOINT_RE.fullmatch(endpoint):
        raise InstallError("INVALID_GRANT")
    if not expiry.isdigit() or int(expiry) <= time.time()+60 or not generation_number.isdigit() or int(generation_number)<1:
        raise InstallError("INVALID_GRANT_LIFETIME")
    payloads = {}
    for name, variable in (("agent.py", "AARIS_AGENT_BASE64"), ("gui.py", "AARIS_GUI_BASE64"),
                           ("gui_setup.sh", "AARIS_GUI_SETUP_BASE64")):
        encoded = environ.get(variable, "")
        if not encoded or len(encoded)>8*1024*1024:
            raise InstallError("INVALID_SOURCE_PAYLOAD")
        payloads[name] = base64.b64decode(encoded, validate=True)
    for name in ("agent.py", "gui.py"):
        compile(payloads[name], name, "exec")
    payloads.update({"token": (token+"\n").encode(), "endpoint": (endpoint+"\n").encode(),
                     "expires_at": (expiry+"\n").encode(),
                     "transaction_install.py": Path(__file__).read_bytes()})
    with lock(layout.install_lock):
        generation = layout.generations / transaction_id
        if (directory/"state.json").exists():
            recorded = load_json(directory/"state.json")
            if recorded["generation"] != int(generation_number):
                raise InstallError("GENERATION_MISMATCH")
            for name, content in payloads.items():
                if not (generation/name).is_file() or (generation/name).read_bytes()!=content:
                    raise InstallError("TRANSACTION_PAYLOAD_CHANGED")
            atomic_bytes(layout.bin/"transaction_install.py",Path(__file__).read_bytes(),0o700)
            if environ.get("AARIS_TRANSACTION_PREPARE_ONLY","0")!="1":
                request_action(directory,"commit")
            return transaction_id
        private_directory(directory)
        private_directory(generation)
        for name, content in payloads.items():
            atomic_bytes(generation/name, content, 0o700 if name.endswith(".sh") else 0o600)
        subprocess.run(["bash", "-n", str(generation/"gui_setup.sh")], check=True,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for name in ("agent.py", "gui.py"):
            subprocess.run([sys.executable, str(generation/name), "--selftest"], check=True,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30)
        previous_fingerprint=layout.fingerprint()
        previous = layout.current()
        if not previous and any(path.exists() for path in layout.active_files().values()):
            previous = private_directory(layout.generations/("legacy-"+transaction_id))
            for name, path in layout.active_files().items():
                if path.is_file():
                    atomic_bytes(previous/name, path.read_bytes(), 0o700 if name.endswith(".sh") else 0o600)
        if layout.fingerprint()!=previous_fingerprint:
            raise InstallError("ACTIVE_CONFIGURATION_CHANGED_DURING_SNAPSHOT")
        atomic_json(directory/"state.json", {"transaction_id":transaction_id,"state":"STAGED",
            "generation":int(generation_number),"machine":endpoint.rsplit("/",1)[1],
            "candidate":str(generation),"previous":str(previous) if previous else None,
            "previous_fingerprint":previous_fingerprint,"updated_at":int(time.time())})
        # Install the small recovery entrypoint only; active source/config stays
        # byte-for-byte unchanged throughout PREPARE.
        atomic_bytes(layout.bin/"transaction_install.py", Path(__file__).read_bytes(),0o700)
        if environ.get("AARIS_TRANSACTION_PREPARE_ONLY", "0") != "1":
            request_action(directory,"commit")
    return transaction_id


def spawn(layout, transaction_id):
    directory = layout.transaction(transaction_id)
    data = load_json(directory/"state.json")
    if data["state"] in TERMINAL:
        return
    with lock(directory/"launch.lock"):
        old = load_json(directory/"supervisor.json") if (directory/"supervisor.json").exists() else None
        purpose="rollback" if (directory/"rollback.request").exists() else "commit" if (directory/"commit.request").exists() else "abort" if (directory/"abort.request").exists() else "prepare"
        if old and same_process(old) and old.get("purpose","prepare")==purpose:
            return
        log = open(directory/"supervisor.log", "ab", buffering=0)
        os.chmod(directory/"supervisor.log",0o600)
        environment = {key:value for key,value in os.environ.items() if not any(
            term in key.upper() for term in ("TOKEN","SECRET","PASSWORD","BASE64","CREDENTIAL","AUTH","LICENSE"))}
        process = subprocess.Popen([sys.executable,str(Path(data["candidate"])/"transaction_install.py"),"--run",transaction_id],
            stdin=subprocess.DEVNULL,stdout=log,stderr=log,env=environment,start_new_session=True,close_fds=True)
        log.close()
        identity = process_identity(process.pid)
        if identity:
            identity["purpose"]=purpose
            atomic_json(directory/"supervisor.json",identity)


def stop_owned_agent(layout, identity):
    if not identity or not same_process(identity):
        return
    if not hasattr(os,"pidfd_open") or not hasattr(signal,"pidfd_send_signal"):
        raise InstallError("PIDFD_REQUIRED_FOR_SAFE_AGENT_STOP")
    try:
        descriptor=os.pidfd_open(identity["pid"])
    except ProcessLookupError:
        return
    try:
        if not same_process(identity):
            raise InstallError("AGENT_PID_CHANGED")
        signal.pidfd_send_signal(descriptor,signal.SIGTERM)
    finally:
        os.close(descriptor)
    while same_process(identity):
        time.sleep(1)
    # No SIGKILL, broad process matching, Unity kill, or guessed stale PID.


def launch_agent(layout, generation, transaction_id=""):
    generation = Path(generation)
    identity = layout.active_agent()
    if identity:
        if str(generation/"agent.py") in identity["argv"]:
            return identity
        raise InstallError("ANOTHER_AGENT_STILL_RUNNING")
    environment = {key:value for key,value in os.environ.items() if not any(
        term in key.upper() for term in ("TOKEN","SECRET","PASSWORD","BASE64","CREDENTIAL","AUTH","LICENSE"))}
    environment.update(AARIS_ENDPOINT=(generation/"endpoint").read_text().strip(),
        AARIS_TOKEN_FILE=str(generation/"token"), AARIS_AGENT_LOCK=str(layout.config/"agent.lock"),
        AARIS_AGENT_STATUS_FILE=str(generation/"agent-status.json"))
    if transaction_id:
        environment["AARIS_TRANSACTION_ID"] = transaction_id
    else:
        environment.pop("AARIS_TRANSACTION_ID",None)
    log = open(layout.config/"agent.log","ab",buffering=0)
    os.chmod(layout.config/"agent.log",0o600)
    process = subprocess.Popen([sys.executable,"-u",str(generation/"agent.py")],
        stdin=subprocess.DEVNULL,stdout=log,stderr=log,env=environment,start_new_session=True,close_fds=True)
    log.close()
    atomic_bytes(layout.config/"agent.pid",(str(process.pid)+"\n").encode())
    return process_identity(process.pid)


def restore_previous(layout, data, module):
    candidate=Path(data["candidate"])
    current=layout.current()
    identity=layout.active_agent()
    if current==candidate and identity:
        if str(candidate/"agent.py") not in identity["argv"]:
            raise InstallError("ROLLBACK_AGENT_OWNERSHIP_MISMATCH")
        stop_owned_agent(layout,identity)
    previous=Path(data["previous"]) if data.get("previous") else None
    if not previous:
        if current==candidate:
            (layout.config/"current").unlink()
            for path in (layout.bin/"agent.py",layout.bin/"gui.py",layout.bin/"gui_setup.sh",
                         layout.config/"token",layout.config/"endpoint",layout.config/"expires_at"):
                if path.is_symlink() and str(layout.config/"current") in str(path.readlink()):
                    path.unlink()
        return
    previous_bridge=bridge_for(previous,module)
    try:
        previous_bridge.post("control",{"operation":"resume","transaction_id":data["transaction_id"]})
    except ConnectionError as exc:
        if "404" not in str(exc):
            raise
    if current==candidate:
        layout.set_current(previous)
    if not layout.active_agent():
        if int((previous/"expires_at").read_text())<=time.time():
            raise InstallError("PREVIOUS_SESSION_EXPIRED")
        if layout.current() is None:
            layout.set_current(previous)
        launch_agent(layout,previous)


def run(layout, transaction_id):
    directory = layout.transaction(transaction_id)
    # A commit/abort successor may be queued while PREPARE is publishing its
    # final status. Waiting for the same lock prevents losing that last-moment
    # request, while there remains only one mutating supervisor at a time.
    with lock(layout.install_lock,blocking=True):
        data = load_json(directory/"state.json")
        if data["state"] in {"READY","ABORTED","ROLLED_BACK"} and not (directory/"rollback.request").exists():
            return
        candidate = Path(data["candidate"])
        module = agent_module(candidate)
        bridge = bridge_for(candidate,module)
        delay = 1
        while True:
            try:
                backend = bridge.transaction(transaction_id,"status")
                if (directory/"rollback.request").exists():
                    if backend["state"] in {"prepared","draining"}:
                        backend=bridge.transaction(transaction_id,"abort")
                    elif backend["state"] in {"committed","ready","rollback_draining"}:
                        backend=bridge.transaction(transaction_id,"rollback-prepare")
                        update(layout,transaction_id,"ROLLBACK_DRAINING",backend_state=backend["state"],running_jobs=backend["running_jobs"])
                        if backend.get("expired_running_jobs"):
                            raise InstallError("JOB_COMPLETION_UNCONFIRMED")
                        if backend["running_jobs"]:
                            time.sleep(2)
                            continue
                        identity=layout.active_agent()
                        if identity and str(candidate/"agent.py") in identity["argv"]:
                            stop_owned_agent(layout,identity)
                        backend=bridge.transaction(transaction_id,"rollback")
                    if backend["state"] in {"aborted","rolled_back"}:
                        restore_previous(layout,data,module)
                        update(layout,transaction_id,"ROLLED_BACK",backend_state=backend["state"])
                        return
                    raise InstallError("ROLLBACK_OUTCOME_UNKNOWN")
                if (directory/"abort.request").exists() and not (directory/"commit.request").exists():
                    bridge.transaction(transaction_id,"abort")
                    update(layout,transaction_id,"ABORTED",backend_state="aborted")
                    return
                if backend["state"] in {"aborted","rolled_back"}:
                    restore_previous(layout,data,module)
                    update(layout,transaction_id,"ABORTED" if backend["state"]=="aborted" else "ROLLED_BACK",backend_state=backend["state"])
                    return
                if backend["state"]=="prepared" and not backend.get("probe_verified"):
                    # Candidate-only executable; this cannot consume queued work.
                    bridge.prepare_probe(transaction_id)
                    backend = bridge.transaction(transaction_id,"status")
                if backend["state"]=="prepared" and not (directory/"commit.request").exists():
                    update(layout,transaction_id,"PREPARED",backend_state="prepared")
                    return
                if not (directory/"commit.request").exists():
                    raise InstallError("COMMIT_NOT_REQUESTED")
                if backend["state"] in {"prepared","draining"}:
                    if layout.current()!=candidate and layout.fingerprint()!=data["previous_fingerprint"]:
                        raise InstallError("ACTIVE_CONFIGURATION_CHANGED")
                    if backend["state"]=="prepared":
                        backend=bridge.transaction(transaction_id,"begin-cutover")
                    update(layout,transaction_id,"DRAINING",backend_state=backend["state"],running_jobs=backend["running_jobs"])
                    if backend.get("expired_running_jobs"):
                        raise InstallError("JOB_COMPLETION_UNCONFIRMED")
                    if backend["running_jobs"]:
                        time.sleep(2)
                        continue
                    old=layout.active_agent()
                    if old and str(candidate/"agent.py") not in old["argv"]:
                        previous=Path(data["previous"])
                        previous_endpoint=(previous/"endpoint").read_text().strip()
                        if previous_endpoint!=(candidate/"endpoint").read_text().strip():
                            previous_status=previous/"agent-status.json"
                            modern=False
                            if previous_status.is_file():
                                status=load_json(previous_status)
                                modern=(status.get("protocol")==1 and status.get("pid")==old["pid"] and status.get("start_ticks")==old["start_ticks"])
                            if not modern:
                                previous_bridge=bridge_for(previous,module)
                                try:
                                    drain=previous_bridge.post("control",{"operation":"drain","transaction_id":transaction_id})
                                except ConnectionError as exc:
                                    if "404" in str(exc):
                                        # No race-prone PID-only kill fallback for a
                                        # legacy broker that cannot pause dispatch.
                                        bridge.transaction(transaction_id,"abort")
                                        raise InstallError("LEGACY_BROKER_UPGRADE_REQUIRED") from None
                                    raise
                                if drain.get("running_jobs"):
                                    if drain.get("expired_running_jobs"):
                                        raise InstallError("JOB_COMPLETION_UNCONFIRMED")
                                    time.sleep(2)
                                    continue
                        stop_owned_agent(layout,old)
                    # Write recovery intent before changing the sole active pointer.
                    update(layout,transaction_id,"CUTOVER_REQUESTED",backend_state="draining")
                    layout.set_current(candidate)
                    launch_agent(layout,candidate,transaction_id)
                elif backend["state"] in {"committed","ready"}:
                    if layout.current()!=candidate:
                        raise InstallError("COMMITTED_GENERATION_POINTER_MISMATCH")
                    if backend["state"]=="committed" and backend.get("probe_state") in {"failed","expired"}:
                        backend=bridge.transaction(transaction_id,"retry-probe")
                    launch_agent(layout,candidate,transaction_id)
                else:
                    raise InstallError("BACKEND_RECONCILIATION_REQUIRED")
                backend=bridge.transaction(transaction_id,"status")
                if backend["state"]=="ready" and backend.get("selected"):
                    identity=layout.active_agent()
                    if not identity or str(candidate/"agent.py") not in identity["argv"]:
                        raise InstallError("READY_AGENT_NOT_RUNNING")
                    update(layout,transaction_id,"READY",backend_state="ready",running_jobs=backend["running_jobs"])
                    # The sequential provisioner owns GUI/package setup. Starting
                    # another apt process here races the Unity-first dependency step.
                    return
                if backend["state"]=="ready" and not backend.get("selected"):
                    raise InstallError("ACTIVE_TARGET_CHANGED")
                update(layout,transaction_id,"VERIFYING_AGENT",backend_state=backend["state"],running_jobs=backend["running_jobs"])
                delay=1
                time.sleep(2)
            except (module.ProtocolConflict, PermissionError) as exc:
                update(layout,transaction_id,"RECONCILE_REQUIRED",error=type(exc).__name__.upper())
                return
            except (ConnectionError,TimeoutError,urllib.error.URLError) as exc:
                if isinstance(getattr(exc,"reason",None),ssl.SSLCertVerificationError):
                    update(layout,transaction_id,"RECONCILE_REQUIRED",error="TLS_CERTIFICATE_VERIFICATION_FAILED")
                    return
                update(layout,transaction_id,"WAITING_FOR_NETWORK",error=type(exc).__name__.upper())
                time.sleep(delay)
                delay=min(30,delay*2)
            except InstallError as exc:
                update(layout,transaction_id,"RECONCILE_REQUIRED",error=str(exc))
                return
            except (OSError,ValueError,RuntimeError,subprocess.SubprocessError) as exc:
                update(layout,transaction_id,"RECONCILE_REQUIRED",error=type(exc).__name__.upper())
                return


def ensure(layout):
    resume_id=None
    with lock(layout.install_lock):
        pending=[]
        for path in layout.transactions.iterdir():
            if TX_RE.fullmatch(path.name) and (path/"state.json").is_file():
                data=load_json(path/"state.json")
                if data["state"] not in TERMINAL and (data["state"]!="PREPARED" or
                        (path/"commit.request").exists() or (path/"abort.request").exists()):
                    pending.append(data)
        if len(pending)>1:
            raise InstallError("MULTIPLE_TRANSACTIONS_REQUIRE_RECONCILIATION")
        if pending:
            resume_id=pending[0]["transaction_id"]
        elif layout.active_agent():
            print("TURBO_AGENT_ALREADY_RUNNING")
            return 0
        else:
            current=layout.current()
            files=layout.active_files()
            if not all(files[name].is_file() for name in ("agent.py","token","endpoint","expires_at")):
                print("TURBO_NO_VALID_SAVED_SESSION")
                return 0
            if int(files["expires_at"].read_text().strip())<=time.time()+60:
                print("TURBO_SESSION_EXPIRED_REBOOTSTRAP_REQUIRED")
                return 0
            if current:
                # READY generations resume their active grant directly; a newer
                # pending transaction must not invalidate the saved old session.
                launch_agent(layout,current)
            else:
                endpoint=files["endpoint"].read_text().strip()
                token=files["token"]
                if not ENDPOINT_RE.fullmatch(endpoint) or token.stat().st_mode & 0o077:
                    raise InstallError("UNSAFE_LEGACY_SESSION")
                environment={key:value for key,value in os.environ.items() if not key.startswith("AARIS_")}
                environment.update(AARIS_ENDPOINT=endpoint,AARIS_TOKEN_FILE=str(token),
                                   AARIS_AGENT_LOCK=str(layout.config/"agent.lock"))
                log=open(layout.config/"agent.log","ab")
                process=subprocess.Popen([sys.executable,"-u",str(files["agent.py"])],stdin=subprocess.DEVNULL,
                    stdout=log,stderr=log,env=environment,start_new_session=True,close_fds=True)
                log.close()
                atomic_bytes(layout.config/"agent.pid",(str(process.pid)+"\n").encode())
            print("TURBO_AGENT_AUTO_RESUMED")
            return 0
    if resume_id:
        spawn(layout,resume_id)
        print("TURBO_TRANSACTION_RECOVERY_STARTED="+resume_id)
    return 0


def await_result(layout, transaction_id, seconds):
    deadline=time.monotonic()+seconds
    while True:
        result=state(layout,transaction_id)
        phase=result["state"]
        directory=layout.transaction(transaction_id)
        prepared_complete=(phase=="PREPARED" and not (directory/"commit.request").exists() and not (directory/"abort.request").exists())
        if phase in TERMINAL or prepared_complete or time.monotonic()>=deadline:
            print("TURBO_TRANSACTION_ID="+transaction_id)
            print("TURBO_TRANSACTION_STATE="+phase)
            if phase=="READY" or prepared_complete:
                print("TURBO_TRANSACTION_"+phase)
                return 0
            if phase=="ABORTED":
                print("TURBO_TRANSACTION_ABORTED")
                return 0
            if phase=="ROLLED_BACK":
                print("TURBO_TRANSACTION_ROLLED_BACK")
                return 0
            print("TURBO_TRANSACTION_PENDING" if phase not in TERMINAL else "TURBO_TRANSACTION_RECONCILE_REQUIRED")
            return 75 if phase not in TERMINAL else 76
        time.sleep(1)


def main():
    parser=argparse.ArgumentParser()
    group=parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--install",action="store_true")
    group.add_argument("--ensure",action="store_true")
    for name in ("status","resume","commit","abort","rollback","run"):
        group.add_argument("--"+name,metavar="ID")
    parser.add_argument("--wait",type=int,default=180)
    args=parser.parse_args()
    layout=Layout()
    try:
        if args.ensure:
            return ensure(layout)
        if args.install:
            transaction_id=stage(layout,os.environ)
        else:
            transaction_id=next(getattr(args,k) for k in ("status","resume","commit","abort","rollback","run") if getattr(args,k))
            layout.transaction(transaction_id)
        if args.status:
            print(json.dumps(state(layout,transaction_id),sort_keys=True))
            return 0
        directory=layout.transaction(transaction_id)
        if args.run:
            run(layout,transaction_id)
            return 0
        if args.commit:
            if (directory/"abort.request").exists():
                raise InstallError("TRANSACTION_ABORT_REQUESTED")
            if state(layout,transaction_id)["state"] not in {"PREPARED","STAGED","WAITING_FOR_NETWORK","DRAINING","CUTOVER_REQUESTED","VERIFYING_AGENT","READY"}:
                raise InstallError("TRANSACTION_NOT_PREPARED")
            request_action(directory,"commit")
        if args.abort:
            if (directory/"commit.request").exists():
                raise InstallError("COMMIT_OUTCOME_MUST_BE_RECONCILED")
            request_action(directory,"abort")
        if args.rollback:
            if not (directory/"commit.request").exists():
                raise InstallError("ABORT_PREPARED_TRANSACTION_INSTEAD")
            with lock(directory/"request.lock"):
                atomic_bytes(directory/"rollback.request",b"rollback\n")
                update(layout,transaction_id,"ROLLBACK_REQUESTED")
        spawn(layout,transaction_id)
        return await_result(layout,transaction_id,max(0,min(args.wait,3600)))
    except BlockingIOError:
        print("TURBO_TRANSACTION_PENDING_INSTALL_LOCK_HELD")
        return 75
    except (InstallError, ValueError, OSError, subprocess.SubprocessError) as exc:
        # Exception details, tracebacks, command lines and payloads remain private.
        category=str(exc) if isinstance(exc,InstallError) else type(exc).__name__.upper()
        print("TURBO_TRANSACTION_ERROR="+category)
        return 76


if __name__=="__main__":
    raise SystemExit(main())
