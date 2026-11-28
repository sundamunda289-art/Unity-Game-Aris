#!/usr/bin/env python3
"""Aaris Codespace Turbo Agent: outgoing-only, standard-library terminal relay.

Cloudflare accepts commands only through a privately managed Durable Object SQL
queue. This agent is provisioned only by the trusted GitHub Actions OIDC workflow.
No incoming ports, public Codespace terminal, arbitrary Issue comments, or
GitHub commit-per-command polling are used.
"""
from __future__ import annotations
import base64
import fcntl
import json
import os
from pathlib import Path
import pwd
import re
import selectors
import signal
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request

NAME_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9-]{2,119}$")
PROJECT_ROOT = Path("/workspaces")
SENSITIVE_ENV = ("TOKEN", "SECRET", "PASSWORD", "API_KEY", "CREDENTIAL", "AUTH", "LICENSE", "COOKIE", "PRIVATE_KEY")
MAX_OUTPUT_BYTES = 8 * 1024 * 1024
MAX_ACTIVE = 2
TX_RE = re.compile(r"^[a-f0-9]{32}$")


class ProtocolConflict(RuntimeError):
    """A definite server refusal, never a reason to replay a whole switch."""


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def singleton_lock(path):
    path = Path(path)
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    fd = os.open(path, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    if os.fstat(fd).st_uid != os.getuid() or os.fstat(fd).st_mode & 0o077:
        os.close(fd)
        raise SystemExit("AARIS_AGENT_LOCK_UNSAFE")
    try:
        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        os.close(fd)
        raise SystemExit("AARIS_AGENT_ALREADY_RUNNING")
    return fd  # Hold the descriptor for the whole process; never unlink its inode.


def write_status(path, phase, active_jobs=0, transaction_id=""):
    if not path:
        return
    target = Path(path)
    start_ticks = Path("/proc/self/stat").read_text().rsplit(")", 1)[1].split()[19]
    payload = {"pid": os.getpid(), "start_ticks": start_ticks, "protocol": 1, "phase": phase, "active_jobs": active_jobs,
               "transaction_id": transaction_id, "updated_at": int(time.time())}
    temporary = target.with_name(target.name + "." + str(os.getpid()) + ".tmp")
    with open(temporary, "w", encoding="utf-8") as stream:
        os.chmod(temporary, 0o600)
        json.dump(payload, stream, sort_keys=True)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, target)


def scrub_env():
    result = {k: v for k, v in os.environ.items()
            if not any(fragment in k.upper() for fragment in SENSITIVE_ENV)
            and k not in {"AARIS_ENDPOINT", "AARIS_TOKEN_FILE"}}
    # Codespaces SSH may inherit /root as HOME for a non-root account. Scope
    # correction to this child environment; never change the caller's globals.
    result["HOME"] = pwd.getpwuid(os.geteuid()).pw_dir
    return result


def validate_job(job):
    if not isinstance(job, dict):
        raise ValueError("job")
    command_id = job.get("id")
    argv = json.loads(job.get("argv_json", "null"))
    cwd = job.get("cwd", "")
    timeout = job.get("max_seconds", 120)
    if not isinstance(command_id, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", command_id):
        raise ValueError("id")
    if not isinstance(argv, list) or not 1 <= len(argv) <= 64 or not all(
        isinstance(a, str) and 0 < len(a) <= 4096 for a in argv
    ) or sum(map(len, argv)) > 16384:
        raise ValueError("argv")
    if not isinstance(cwd, str) or not cwd.startswith("/workspaces/") or "\x00" in cwd:
        raise ValueError("cwd")
    path = Path(cwd).resolve(strict=True)
    if not path.is_dir() or not path.is_relative_to(PROJECT_ROOT):
        raise ValueError("cwd_scope")
    if not isinstance(timeout, int) or not 1 <= timeout <= 3600:
        raise ValueError("timeout")
    return command_id, argv, str(path), timeout


class Bridge:
    # Validated from GitHub Codespace: default python-urllib UA was blocked by the edge.
    USER_AGENT = "Mozilla/5.0"
    def __init__(self, endpoint, token):
        if not endpoint.startswith("https://") or "/agent/" not in endpoint:
            raise ValueError("https_endpoint_required")
        if len(token) != 64 or not all(c in "0123456789abcdef" for c in token):
            raise ValueError("token")
        self.endpoint = endpoint.rstrip("/")
        self.token = token
        self.base, self.machine = self.endpoint.rsplit("/agent/", 1)
        if not NAME_RE.fullmatch(self.machine):
            raise ValueError("machine")
        self.transport = "direct"
        self.opener = urllib.request.build_opener(NoRedirect())
        self.pending_reports = []
        self.report_lock = threading.Lock()

    def _check_result(self, operation, result):
        if not isinstance(result, dict):
            raise ConnectionError("response_type")
        if operation == "next" and "job" not in result:
            raise ConnectionError("response_missing_job")
        if operation != "next" and result.get("ok") is not True:
            raise ConnectionError("response_missing_ack")
        return result

    def post(self, operation, payload=None):
        if operation not in {"ping", "next", "chunk", "done", "transaction", "control"}:
            raise ValueError("operation")
        b = json.dumps(payload or {}, ensure_ascii=False).encode("utf-8")
        if self.transport == "direct":
            req = urllib.request.Request(
                self.endpoint + "/" + operation,
                data=b,
                headers={"Authorization": "Bearer " + self.token, "Content-Type": "application/json",
                         "User-Agent": self.USER_AGENT},
                method="POST")
            try:
                with self.opener.open(req, timeout=12) as response:
                    return self._check_result(operation, json.loads(response.read(65536)))
            except urllib.error.HTTPError as exc:
                if exc.code == 403:
                    # Network or edge rejects Codespace /agent routes; switch once.
                    self.transport = "health"
                elif exc.code == 401:
                    raise PermissionError("session_expired") from None
                elif exc.code == 409:
                    raise ProtocolConflict("transaction_conflict") from None
                elif 300 <= exc.code < 400:
                    raise ProtocolConflict("authenticated_redirect_refused") from None
                else:
                    raise ConnectionError("remote_http_" + str(exc.code)) from None
        # The same authenticated Durable Object operations, routed through one
        # HTTPS GET endpoint. Output and tokens live in headers, never URLs.
        blob = base64.urlsafe_b64encode(b).decode("ascii").rstrip("=")
        if len(blob) > 11000:
            raise ValueError("request_payload_too_large")
        req = urllib.request.Request(
            self.base + "/health", method="GET",
            headers={"User-Agent": self.USER_AGENT, "X-Aaris-Op": operation,
                     "X-Aaris-Machine": self.machine, "X-Aaris-Token": self.token,
                     "X-Aaris-Payload": blob})
        try:
            with self.opener.open(req, timeout=12) as response:
                return self._check_result(operation, json.loads(response.read(65536)))
        except urllib.error.HTTPError as exc:
            if exc.code == 401:
                raise PermissionError("session_expired") from None
            if exc.code == 409:
                raise ProtocolConflict("transaction_conflict") from None
            if 300 <= exc.code < 400:
                raise ProtocolConflict("authenticated_redirect_refused") from None
            raise ConnectionError("fallback_http_" + str(exc.code)) from None

    def transaction(self, transaction_id, operation, **payload):
        if not TX_RE.fullmatch(transaction_id):
            raise ValueError("transaction_id")
        return self.post("transaction", {"transaction_id": transaction_id,
                                         "operation": operation, **payload})

    def prepare_probe(self, transaction_id):
        challenge = self.transaction(transaction_id, "probe")
        nonce = challenge.get("nonce", "")
        if not TX_RE.fullmatch(nonce):
            raise ValueError("probe_nonce")
        # This is the sole process allowed by a pending grant. No public input is
        # evaluated and the ordinary command queue is never polled here.
        result = subprocess.run([sys.executable, "-c",
                                 "print('AARIS_TRANSACTION_PROBE=" + nonce + "')"],
                                stdin=subprocess.DEVNULL, capture_output=True,
                                timeout=20, cwd="/workspaces/Unity-Game-Aris",
                                env=scrub_env(), check=False)
        expected = "AARIS_TRANSACTION_PROBE=" + nonce + "\n"
        if result.returncode != 0 or result.stdout.decode("ascii") != expected or result.stderr:
            raise ValueError("candidate_probe_failed")
        return self.transaction(transaction_id, "probe-result", nonce=nonce,
                                exit_code=result.returncode, output=expected)

    @staticmethod
    def stop_job_group(proc, grace=3.0):
        """Stop only this command's unreaped session, including grandchildren.

        The unreaped session leader reserves its PID. Calling poll()/wait()
        before killing its group can otherwise hide surviving descendants and
        eventually let PID reuse target an unrelated process.
        """
        if proc.returncode is not None:
            return

        def running():
            for entry in Path("/proc").iterdir():
                if not entry.name.isdecimal():
                    continue
                try:
                    fields = (entry / "stat").read_text().rsplit(")", 1)[1].split()
                    if fields[0] != "Z" and int(fields[2]) == proc.pid and int(fields[3]) == proc.pid:
                        return True
                except (OSError, ValueError, IndexError):
                    continue
            return False

        try:
            os.killpg(proc.pid, signal.SIGTERM)
        except ProcessLookupError:
            proc.wait()
            return
        deadline = time.monotonic() + grace
        while running() and time.monotonic() < deadline:
            time.sleep(0.05)
        if running():
            try:
                os.killpg(proc.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
        while running():
            time.sleep(0.05)
        proc.wait()

    def stream_job(self, job):
        job_id = str(job.get("id", ""))[:128]
        seq = 0

        def send(kind, text):
            nonlocal seq
            if not text:
                return
            for off in range(0, len(text), 3000):
                payload={"id":job_id,"seq":seq,"kind":kind,"text":text[off:off+3000]}
                backoff=0.5
                while True:
                    try:
                        self.post("chunk",payload)
                        break
                    except PermissionError:
                        raise
                    except (OSError,ConnectionError):
                        if time.monotonic()>=started+timeout:
                            raise
                        time.sleep(min(backoff,max(0.01,started+timeout-time.monotonic())))
                        backoff=min(5.0,backoff*1.8)
                seq += 1

        try:
            job_id, argv, cwd, timeout = validate_job(job)
        except (ValueError, OSError, TypeError, json.JSONDecodeError) as exc:
            self.post("done", {"id": job_id, "exit_code": 2, "error": "INVALID_" + type(exc).__name__})
            return
        exit_code, problem = 127, ""
        started = time.monotonic()
        proc = None
        try:
            proc = subprocess.Popen(argv, cwd=cwd, env=scrub_env(),
                                    stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                    start_new_session=True, bufsize=0)
            total = 0
            truncated = False
            with selectors.DefaultSelector() as selector:
                selector.register(proc.stdout, selectors.EVENT_READ, "stdout")
                selector.register(proc.stderr, selectors.EVENT_READ, "stderr")
                while selector.get_map():
                    if time.monotonic() - started > timeout:
                        exit_code, problem = 124, "TIMEOUT"
                        break
                    for key, _ in selector.select(timeout=0.2):
                        data = os.read(key.fileobj.fileno(), 4000)
                        if not data:
                            selector.unregister(key.fileobj)
                            continue
                        if total < MAX_OUTPUT_BYTES:
                            send(key.data, data.decode("utf-8", "replace"))
                            total += len(data)
                        elif not truncated:
                            send("stderr", "\n[AARIS_OUTPUT_LIMIT_REACHED]\n")
                            truncated = True
            if not problem:
                exit_code = proc.wait(timeout=max(0.01, timeout-(time.monotonic()-started)))
        except FileNotFoundError:
            problem = "EXECUTABLE_NOT_FOUND"
        except PermissionError:
            problem = "SESSION_REVOKED"
        except ProtocolConflict:
            problem = "PROTOCOL_REFUSED"
        except (OSError, subprocess.TimeoutExpired) as exc:
            problem = type(exc).__name__.upper()
        except Exception:
            problem = "COMMAND_PROCESS_FAILED"
        finally:
            # The job is not drained until its owned child is stopped and reaped.
            # This also covers upload failures and proc.wait() exceptions.
            if proc is not None and problem:
                self.stop_job_group(proc)
            if proc is not None:
                for stream in (proc.stdout, proc.stderr):
                    if stream is not None:
                        stream.close()
            report={"id":job_id,"exit_code":max(-255,min(255,exit_code)),"error":problem}
            try:
                self.post("done",report)
            except (OSError,ConnectionError,ProtocolConflict):
                with self.report_lock:
                    self.pending_reports.append(report)

    def flush_reports(self):
        with self.report_lock:
            reports=list(self.pending_reports)
        for report in reports:
            self.post("done",report)
            with self.report_lock:
                if report in self.pending_reports:
                    self.pending_reports.remove(report)


def selftest():
    assert NAME_RE.fullmatch("sample-codespace-123")
    assert not NAME_RE.fullmatch("../unsafe")
    assert "GITHUB_TOKEN" not in scrub_env() or "GITHUB_TOKEN" not in SENSITIVE_ENV
    for name in ("AARIS_ENDPOINT", "AARIS_TOKEN_FILE"):
        assert name not in scrub_env()
    try:
        validate_job({"id": "1", "argv_json": '["echo","hi"]', "cwd": "/etc",
                      "max_seconds": 10})
    except ValueError:
        pass
    else:
        raise AssertionError("unsafe_directory_accepted")
    assert MAX_ACTIVE == 2
    assert Bridge.USER_AGENT == "Mozilla/5.0"
    print("AARIS_TURBO_AGENT_SELFTEST_PASS")


def main():
    if "--selftest" in sys.argv:
        selftest()
        return
    endpoint = os.environ.get("AARIS_ENDPOINT", "")
    token_file = Path(os.environ.get("AARIS_TOKEN_FILE", "/nonexistent"))
    if not token_file.is_file() or token_file.stat().st_mode & 0o077:
        raise SystemExit("AARIS_TOKEN_FILE_PERMISSIONS_INVALID")
    token = token_file.read_text(encoding="ascii").strip()
    bridge = Bridge(endpoint, token)
    transaction_id = os.environ.get("AARIS_TRANSACTION_ID", "")
    if "--prepare-probe" in sys.argv:
        bridge.prepare_probe(transaction_id)
        print("TURBO_CANDIDATE_NONCE_PROBE_PASS", flush=True)
        return
    lock_fd = singleton_lock(os.environ.get("AARIS_AGENT_LOCK", str(Path(pwd.getpwuid(os.geteuid()).pw_dir)/".config/aaris-turbo/agent.lock")))
    status_file = os.environ.get("AARIS_AGENT_STATUS_FILE", "")
    stop = threading.Event()
    signal.signal(signal.SIGTERM, lambda *_: stop.set())
    signal.signal(signal.SIGINT, lambda *_: stop.set())
    write_status(status_file, "ACTIVATING" if transaction_id else "STARTING", transaction_id=transaction_id)
    if transaction_id:
        if not TX_RE.fullmatch(transaction_id):
            raise SystemExit("AARIS_TRANSACTION_ID_INVALID")
        backoff = 1.0
        while not stop.is_set():
            try:
                result = bridge.transaction(transaction_id, "status")
                if result.get("state") == "draining":
                    result = bridge.transaction(transaction_id, "commit")
                if result.get("state") not in {"committed", "ready"}:
                    raise SystemExit("AARIS_TRANSACTION_NOT_COMMITTABLE")
                break
            except (PermissionError, ProtocolConflict):
                write_status(status_file, "RECONCILE_REQUIRED", transaction_id=transaction_id)
                raise SystemExit("AARIS_TRANSACTION_RECONCILE_REQUIRED")
            except (OSError, ConnectionError, ValueError, json.JSONDecodeError):
                write_status(status_file, "WAITING_FOR_NETWORK", transaction_id=transaction_id)
                stop.wait(backoff)
                backoff = min(30.0, backoff * 1.8)
        if stop.is_set():
            return
    print("AARIS_TURBO_AGENT_STARTED", flush=True)
    write_status(status_file, "RUNNING", transaction_id=transaction_id)
    workers = set()
    last_heartbeat = 0.0
    backoff = 1.0
    last_status = None
    while True:
        try:
            now = time.monotonic()
            workers = {thread for thread in workers if thread.is_alive()}
            bridge.flush_reports()
            state = (stop.is_set(), len(workers))
            if state != last_status:
                write_status(status_file, "DRAINING" if stop.is_set() else "RUNNING",
                             len(workers), transaction_id)
                last_status = state
            if stop.is_set() and not workers and not bridge.pending_reports:
                write_status(status_file, "STOPPED", transaction_id=transaction_id)
                os.close(lock_fd)
                return
            if now - last_heartbeat > 25:
                bridge.post("ping")
                last_heartbeat = now
            if not stop.is_set() and len(workers) < MAX_ACTIVE:
                response = bridge.post("next")
                job = response.get("job")
                if job:
                    thread = threading.Thread(target=bridge.stream_job, args=(job,), daemon=False)
                    thread.start()
                    workers.add(thread)
            backoff = 1.0
            time.sleep(1.0)
        except PermissionError:
            print("AARIS_TURBO_SESSION_REVOKED", flush=True)
            return
        except (OSError, ConnectionError, ValueError, json.JSONDecodeError) as exc:
            print("AARIS_TURBO_RETRY_" + type(exc).__name__, flush=True)
            time.sleep(backoff)
            backoff = min(30.0, backoff * 1.8)


if __name__ == "__main__":
    main()

