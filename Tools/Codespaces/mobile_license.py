#!/usr/bin/env python3
"""Private same-machine Unity login with resumable, single-session phone access.

Raw Unity output is parsed in memory and never emitted. The default invocation
retains the older marker stream. session-* commands use a private JSON journal
so Android may disconnect/reconnect without starting another OAuth attempt.
"""
from __future__ import annotations

import argparse
from contextlib import contextmanager
import fcntl
import json
import os
from pathlib import Path
import pwd
import queue
import re
import secrets
import shutil
import signal
import socket
import stat
import subprocess
import sys
import tempfile
import threading
import time
from urllib.parse import urlsplit

URL_RE = re.compile(r"https://[A-Za-z0-9._~:/?#@!$&'()*+,;=%-]+")
SESSION_RE = re.compile(r"[0-9a-f]{32}")
MACHINE_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9-]{2,119}")
TERMINAL = {"COMPLETE", "FAILED", "EXPIRED", "INTERRUPTED"}
AUTH_URL_FIELDS = {"auth_url", "authUrl", "login_url", "loginUrl", "authorization_url",
                   "verification_uri", "verification_uri_complete"}


def valid(url):
    try:
        parsed = urlsplit(url)
        host = (parsed.hostname or "").lower()
        return (parsed.scheme == "https" and not parsed.username and not parsed.password
                and parsed.port in (None, 443) and len(url) < 4000
                and not any(ord(char) < 32 for char in url)
                and any(host == suffix or host.endswith("." + suffix) for suffix in ("unity.com", "unity3d.com")))
    except (ValueError, TypeError):
        return False


def extract(line):
    try:
        document = json.loads(line)
    except (ValueError, TypeError):
        document = None

    def visit(value):
        if isinstance(value, dict):
            for key, item in value.items():
                if key in AUTH_URL_FIELDS and isinstance(item, str) and valid(item):
                    return item
            for item in value.values():
                found = visit(item)
                if found:
                    return found
        elif isinstance(value, list):
            for item in value:
                found = visit(item)
                if found:
                    return found
        return None

    found = visit(document)
    if found:
        return found
    tagged = re.search(r"auth_url=(https://[^\s\"'\\]+)", line)
    if tagged and valid(tagged.group(1)):
        return tagged.group(1)
    for match in URL_RE.finditer(line.replace("\\/", "/")):
        url = match.group(0).rstrip("'\"\\]},)")
        if not valid(url):
            continue
        parsed = urlsplit(url)
        # Do not mistake a Unity documentation/privacy URL for a sign-in link.
        if parsed.hostname in {"id.unity.com", "accounts.unity3d.com"}:
            return url
        if parsed.hostname == "services.api.unity.com" and "/app-linking/" in parsed.path and "/login/" in parsed.path:
            return url
    return None


def credential_store_unavailable(text):
    text = text.lower()
    stores = r"(?:secret.?service|org\.freedesktop\.(?:secrets|dbus)|libsecret|keyring|d-bus|dbus|credential.?stor(?:e|age))"
    failures = r"(?:unavailable|not available|not found|failed|failure|error|cannot|could not|unable|no such|not supported|locked|denied|not running|timed out|not set)"
    return bool(re.search(stores + r".{0,240}" + failures, text, re.S)
                or re.search(failures + r".{0,240}" + stores, text, re.S))


def provider_expired(text):
    return bool(re.search(r"expired_token|(?:authorization|authentication|login|oauth|device.code)[^\n]{0,100}(?:session|request|code|link|token)?[^\n]{0,30}(?:expired|has expired)",
                          text, re.I))


def writable_home():
    candidates = [Path(pwd.getpwuid(os.geteuid()).pw_dir), Path.home(),
                  Path("/home/vscode"), Path("/home/codespace")]
    for home in dict.fromkeys(candidates):
        try:
            state = home / ".local/share/aaris-unity-cli"
            state.mkdir(parents=True, exist_ok=True, mode=0o700)
            if state.is_symlink() or state.stat().st_uid != os.getuid():
                continue
            fd, name = tempfile.mkstemp(prefix=".write-check-", dir=state)
            os.close(fd)
            os.unlink(name)
            state.chmod(0o700)
            return home, state
        except OSError:
            continue
    raise RuntimeError("UNITY_STATE_DIRECTORY_UNWRITABLE")


def private_dir(path):
    if path.is_symlink():
        raise RuntimeError("UNITY_UNSAFE_SESSION_PATH")
    path.mkdir(parents=True, exist_ok=True, mode=0o700)
    if path.stat().st_uid != os.getuid():
        raise RuntimeError("UNITY_SESSION_OWNER_MISMATCH")
    path.chmod(0o700)
    return path


def atomic_json(path, data):
    if path.is_symlink():
        raise RuntimeError("UNITY_UNSAFE_SESSION_PATH")
    fd, name = tempfile.mkstemp(prefix=".session-", dir=path.parent)
    try:
        os.fchmod(fd, 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as output:
            json.dump(data, output)
            output.flush()
            os.fsync(output.fileno())
        os.replace(name, path)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def read_json(path):
    if path.is_symlink():
        raise RuntimeError("UNITY_UNSAFE_SESSION_PATH")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return {}


@contextmanager
def private_lock(path):
    fd = os.open(path, os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    try:
        if os.fstat(fd).st_uid != os.getuid():
            raise RuntimeError("UNITY_SESSION_OWNER_MISMATCH")
        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield
    finally:
        os.close(fd)


def process_start(pid):
    try:
        return Path(f"/proc/{int(pid)}/stat").read_text().rsplit(")", 1)[1].split()[19]
    except (OSError, ValueError, IndexError):
        return ""


def process_alive(record):
    try:
        pid = int(record["pid"])
        if pid < 2 or not record.get("process_start") or process_start(pid) != str(record["process_start"]):
            return False
        arguments = Path(f"/proc/{pid}/cmdline").read_bytes().split(b"\0")
        return os.fsencode(record["script"]) in arguments and b"session-run" in arguments
    except (OSError, KeyError, ValueError, TypeError):
        return False


def another_auth_cli_running():
    # A helper killed by a container/transport supervisor can leave its own
    # CLI child alive. Do not launch another OAuth listener beside that child.
    for directory in Path("/proc").iterdir():
        if not directory.name.isdigit():
            continue
        try:
            if directory.stat().st_uid != os.getuid():
                continue
            arguments = directory.joinpath("cmdline").read_bytes().split(b"\0")
            if not arguments or Path(os.fsdecode(arguments[0])).name != "unity":
                continue
            if any(arguments[index:index + 2] == [b"auth", b"login"] for index in range(1, len(arguments) - 1)):
                return True
        except (OSError, ValueError):
            continue
    return False


class Reporter:
    def __init__(self, state_path=None):
        self.path = state_path

    def state(self, **values):
        if self.path:
            current = read_json(self.path)
            current.update(values)
            current["updated_at"] = int(time.time())
            atomic_json(self.path, current)

    def event(self, marker, phase=None, **values):
        if self.path:
            self.state(marker=marker, **({"phase": phase} if phase else {}), **values)
        else:
            print(marker, flush=True)

    def url(self, value):
        if self.path:
            self.state(auth_url=value, phase="WAITING_FOR_UNITY_LOGIN", marker="UNITY_AUTH_LINK_READY")
        else:
            print("AARIS_AUTH_LINK=" + value, flush=True)


def check(cli, environment, what):
    try:
        result = subprocess.run([cli, what, "status", "--format", "json", "--non-interactive"],
                                env=environment, stdin=subprocess.DEVNULL,
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                timeout=40, check=False)
        return result.returncode == 0
    except (OSError, subprocess.TimeoutExpired):
        return False


def find_cli(candidates):
    for candidate in candidates:
        try:
            if candidate.is_file() and os.access(candidate, os.X_OK):
                return str(candidate)
        except OSError:
            continue
    return shutil.which("unity")


def ensure_cli(home, cli_home, reporter):
    candidates = [cli_home / "bin/unity", home / ".local/bin/unity", home / ".unity/bin/unity",
                  Path("/home/vscode/.local/bin/unity"), Path("/tmp/aaris-unity-runtime/cli/bin/unity")]
    cli = find_cli(candidates)
    if cli:
        reporter.event("UNITY_CLI_READY", "CLI_READY")
        return cli
    reporter.event("UNITY_CLI_BOOTSTRAPPING", "BOOTSTRAPPING_CLI")
    folder = private_dir(Path("/tmp") / ("aaris-unity-cli-" + str(os.getuid())))
    fd, name = tempfile.mkstemp(prefix="official-installer-", suffix=".sh", dir=folder)
    os.close(fd)
    script = Path(name)
    try:
        fetched = subprocess.run(["curl", "-fsSL", "--retry", "3", "--connect-timeout", "30", "--max-time", "150",
                                  "https://unity.com/install.sh", "-o", str(script)],
                                 stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=180, check=False)
        if fetched.returncode:
            raise RuntimeError("UNITY_CLI_DOWNLOAD_FAILED")
        environment = {**os.environ, "HOME": str(home), "UNITY_CLI_HOME": str(cli_home), "DO_NOT_TRACK": "1"}
        with tempfile.TemporaryFile() as private_log:
            installed = subprocess.run(["bash", str(script)], env=environment,
                                       stdout=private_log, stderr=subprocess.STDOUT,
                                       timeout=300, check=False)
        if installed.returncode:
            raise RuntimeError("UNITY_CLI_INSTALL_FAILED")
    finally:
        script.unlink(missing_ok=True)
    cli = find_cli(candidates)
    if not cli:
        raise RuntimeError("UNITY_CLI_BINARY_NOT_FOUND")
    reporter.event("UNITY_CLI_READY", "CLI_READY")
    return cli


def terminate_own(process):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=10)


def login(cli, environment, reporter, insecure_storage=False):
    if check(cli, environment, "auth"):
        reporter.event("UNITY_AUTH_ALREADY_VALID", "AUTH_VERIFIED", auth_verified=True, auth_url=None)
        return 0
    reporter.event("UNITY_AUTH_STARTED", "WAITING_FOR_UNITY_LOGIN")
    arguments = [cli, "auth", "login", "--format", "json"]
    if insecure_storage:
        arguments.append("--insecure-storage")
    process = subprocess.Popen(arguments, env=environment, stdin=subprocess.DEVNULL,
                               text=True, encoding="utf-8", errors="replace",
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT, bufsize=1,
                               start_new_session=True)
    reporter.state(auth_pid=process.pid, auth_process_start=process_start(process.pid))
    pending = queue.Queue()

    def reader():
        try:
            for line in process.stdout:
                pending.put(line)
        finally:
            pending.put(None)

    threading.Thread(target=reader, daemon=True).start()
    storage_failure = expired = False
    last_url = None
    heartbeat = 0.0
    try:
        # There is no arbitrary whole-login deadline. The official CLI/provider
        # owns session expiry; the detached process survives phone disconnects.
        while True:
            if time.monotonic() - heartbeat >= 5:
                reporter.state(heartbeat_at=int(time.time()))
                heartbeat = time.monotonic()
            try:
                line = pending.get(timeout=1)
            except queue.Empty:
                if process.poll() is not None:
                    break
                continue
            if line is None:
                break
            if credential_store_unavailable(line):
                storage_failure = True
                break
            if provider_expired(line):
                expired = True
                break
            url = extract(line)
            if url and url != last_url:
                reporter.url(url)
                last_url = url
    finally:
        terminate_own(process)
        reporter.state(auth_pid=None, auth_process_start=None)
    if check(cli, environment, "auth"):
        reporter.event("UNITY_AUTH_VERIFIED", "AUTH_VERIFIED", auth_verified=True, auth_url=None)
        return 0
    if storage_failure:
        marker = "UNITY_FILE_CREDENTIAL_STORAGE_FAILED" if insecure_storage else "UNITY_SECURE_STORAGE_UNAVAILABLE"
        reporter.event(marker, "FAILED", auth_url=None)
        return 30 if insecure_storage else 26
    if expired:
        reporter.event("UNITY_AUTH_SESSION_EXPIRED", "EXPIRED", auth_url=None)
        return 27
    reporter.event("UNITY_AUTH_NOT_COMPLETED", "FAILED", auth_url=None)
    return 12


def activate(reporter, insecure_storage=False):
    home, cli_home = writable_home()
    cli = ensure_cli(home, cli_home, reporter)
    environment = {key: value for key, value in os.environ.items()
                   if key not in {"UNITY_PASSWORD", "UNITY_EMAIL", "UNITY_SERIAL",
                                  "UNITY_SERVICE_ACCOUNT_ID", "UNITY_SERVICE_ACCOUNT_SECRET"}}
    environment.update({"HOME": str(home), "UNITY_CLI_HOME": str(cli_home), "DO_NOT_TRACK": "1"})
    result = login(cli, environment, reporter, insecure_storage)
    if result:
        return result
    if check(cli, environment, "license"):
        reporter.event("UNITY_LICENSE_ALREADY_ACTIVE", "VERIFYING", license_verified=True)
    else:
        reporter.event("UNITY_PERSONAL_ACTIVATION_START", "ACTIVATING_LICENSE", auth_url=None)
        # This CLI action never opens a game project or downloads Editor/LFS.
        # The provider controls request completion; no whole-session cutoff.
        result = subprocess.run([cli, "license", "activate", "--personal", "--accept-eula", "--non-interactive"],
                                env=environment, stdin=subprocess.DEVNULL,
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
        if result.returncode and not check(cli, environment, "license"):
            reporter.event("UNITY_PERSONAL_NOT_ACTIVE_HUB_MAY_BE_REQUIRED", "FAILED")
            return 14
    # Success always requires fresh same-machine auth AND license checks.
    if not check(cli, environment, "auth") or not check(cli, environment, "license"):
        reporter.event("UNITY_LICENSE_FINAL_VERIFICATION_FAILED", "FAILED")
        return 15
    reporter.event("UNITY_PERSONAL_LICENSE_VERIFIED", "COMPLETE",
                   auth_verified=True, license_verified=True, auth_url=None, exit_code=0)
    return 0


def session_root():
    home, _ = writable_home()
    return private_dir(home / ".config/aaris-ai/unity-auth")


def validate_machine(machine):
    if not MACHINE_RE.fullmatch(machine):
        raise RuntimeError("UNITY_INVALID_MACHINE")
    actual = os.environ.get("CODESPACE_NAME")
    if actual and actual != machine:
        raise RuntimeError("UNITY_MACHINE_MISMATCH")


def session_state(root, session, machine):
    if not SESSION_RE.fullmatch(session):
        raise RuntimeError("UNITY_INVALID_SESSION")
    if (root / session).is_symlink():
        raise RuntimeError("UNITY_UNSAFE_SESSION_PATH")
    state = read_json(root / session / "state.json")
    if state and state.get("machine") != machine:
        raise RuntimeError("UNITY_MACHINE_MISMATCH")
    alive = process_alive(state)
    state["running"] = alive
    if (state and not alive and state.get("phase") not in TERMINAL
            and time.time() - state.get("created_at", 0) > 15):
        state.update(phase="INTERRUPTED", marker="UNITY_AUTH_PROCESS_INTERRUPTED",
                     auth_url=None, exit_code=29)
    return state


def start_session(args):
    root = session_root()
    with private_lock(root / "launch.lock"):
        active = read_json(root / "active.json")
        previous = active.get("session", "")
        if SESSION_RE.fullmatch(previous):
            state = session_state(root, previous, args.machine)
            if state.get("running") and state.get("phase") in TERMINAL:
                # The worker has written its final result and is releasing its
                # lock. Wait briefly for that exact process, never kill it.
                for _ in range(50):
                    time.sleep(0.1)
                    state = session_state(root, previous, args.machine)
                    if not state.get("running"):
                        break
            if state.get("running"):
                state["attached"] = True
                return state
        # A legacy/manual invocation of this reviewed helper uses this same lock.
        # Do not launch a second login beside it.
        with private_lock(root / "login.lock"):
            if another_auth_cli_running():
                raise BlockingIOError("UNITY_AUTH_IN_PROGRESS")
        session = secrets.token_hex(16)
        directory = private_dir(root / session)
        state = {"schema": 2, "session": session, "machine": args.machine, "source_sha": args.source_sha,
                 "phase": "STARTING", "marker": "UNITY_LOGIN_RUNNER_STARTING",
                 "auth_url": None, "auth_verified": False, "license_verified": False,
                 "insecure_storage": args.insecure_storage, "exit_code": None,
                 "created_at": int(time.time()), "updated_at": int(time.time())}
        atomic_json(directory / "state.json", state)
        atomic_json(root / "active.json", {"session": session, "machine": args.machine})
        command = [sys.executable, str(Path(__file__).resolve()), "session-run", "--machine", args.machine,
                   "--source-sha", args.source_sha, "--session", session]
        if args.insecure_storage:
            command.append("--insecure-storage")
        # stdout contains only allowed markers, and does not contain the URL in
        # session mode. OAuth is exposed only by the private session-status call.
        with (directory / "runner.log").open("ab") as private_log:
            process = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=private_log,
                                       stderr=private_log, start_new_session=True, close_fds=True)
        for _ in range(20):
            time.sleep(0.1)
            state = session_state(root, session, args.machine)
            if state.get("running") or process.poll() is not None:
                break
        return state


def run_session(args):
    root = session_root()
    directory = root / args.session
    state = read_json(directory / "state.json")
    if (state.get("machine") != args.machine or state.get("source_sha") != args.source_sha
            or state.get("insecure_storage") != args.insecure_storage):
        raise RuntimeError("UNITY_SESSION_IDENTITY_MISMATCH")
    reporter = Reporter(directory / "state.json")
    try:
        with private_lock(root / "login.lock"):
            reporter.state(pid=os.getpid(), process_start=process_start(os.getpid()),
                           script=str(Path(__file__).resolve()))
            result = activate(reporter, args.insecure_storage)
    except BlockingIOError:
        reporter.event("UNITY_AUTH_IN_PROGRESS", "FAILED")
        result = 75
    except (OSError, RuntimeError, subprocess.SubprocessError) as error:
        marker = str(error) if re.fullmatch(r"UNITY_[A-Z0-9_]{1,80}", str(error)) else "UNITY_PRIVATE_HELPER_FAILED"
        reporter.event(marker, "FAILED", auth_url=None)
        result = 11
    reporter.state(exit_code=result, auth_url=None, finished_at=int(time.time()))
    return result


def selftest():
    assert valid("https://id.unity.com/login?x=1")
    assert not valid("https://id.unity.com.evil.tld/")
    assert not valid("https://name:secret@id.unity.com/")
    assert extract("auth_url=https://id.unity.com/login?x=1") == "https://id.unity.com/login?x=1"
    assert extract('{"auth_url":"https://services.api.unity.com/app-linking/v1/login/redirect/test"}')
    assert extract("See https://docs.unity3d.com/Manual/") is None
    assert credential_store_unavailable("Failed to save credentials: keyring is locked")
    assert not credential_store_unavailable("Sign in was denied by the user")
    assert provider_expired("OAuth device_code expired_token")
    print("MOBILE_UNITY_AUTH_SELFTEST_PASS")


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", nargs="?", default="activate",
                        choices=("activate", "session-start", "session-status", "session-run"))
    parser.add_argument("--machine", default=os.environ.get("CODESPACE_NAME") or socket.gethostname())
    parser.add_argument("--source-sha", default="")
    parser.add_argument("--session", default="")
    parser.add_argument("--insecure-storage", action="store_true")
    parser.add_argument("--selftest", action="store_true")
    args = parser.parse_args()
    if args.selftest:
        selftest()
        return 0
    try:
        validate_machine(args.machine)
        if args.mode != "activate" and not re.fullmatch(r"[0-9a-f]{40}", args.source_sha):
            raise RuntimeError("UNITY_INVALID_SOURCE_SHA")
        if args.mode in {"session-status", "session-run"} and not SESSION_RE.fullmatch(args.session):
            raise RuntimeError("UNITY_INVALID_SESSION")
        if args.mode == "session-start":
            print(json.dumps(start_session(args), sort_keys=True))
            return 0
        if args.mode == "session-status":
            print(json.dumps(session_state(session_root(), args.session, args.machine), sort_keys=True))
            return 0
        if args.mode == "session-run":
            return run_session(args)
        with private_lock(session_root() / "login.lock"):
            return activate(Reporter(), args.insecure_storage)
    except BlockingIOError:
        print("UNITY_AUTH_IN_PROGRESS")
        return 75
    except (OSError, RuntimeError, subprocess.SubprocessError) as error:
        marker = str(error) if re.fullmatch(r"UNITY_[A-Z0-9_]{1,80}", str(error)) else "UNITY_PRIVATE_HELPER_FAILED"
        print(marker)
        return 11


if __name__ == "__main__":
    raise SystemExit(main())

