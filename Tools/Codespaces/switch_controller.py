#!/usr/bin/env python3
"""Termux CCF/CD controller. Fixed source, recoverable phases, private credentials.

Only structured allowlisted events enter the shareable error box. Provider logs,
OAuth links/codes and credentials never do. A process lock serializes mutations;
its inode is intentionally retained so unlink/open races cannot split the lock.
"""
import argparse
import base64
from collections import deque
import contextlib
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import shlex
import shutil
import signal
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.error
import urllib.request

CANONICAL = "sundamunda289-art/Unity-Game-Aris"
BRANCH = "main"
BROKER_RE = re.compile(r"https://aaris-turbo-terminal\.[a-z0-9-]+\.workers\.dev\Z")
MACHINE_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9-]{2,119}\Z")
SHA_RE = re.compile(r"[0-9a-f]{40}\Z")
OP_RE = re.compile(r"[0-9a-f]{32}\Z")
ROOT = Path(__file__).resolve().parent
CF_EVENT_STAGES = {
    "LINUX_RUNNER_PACKAGE_INSTALL", "RUNNER_CAPABILITIES", "LINUX_RUNNER_INSTALL",
    "LINUX_PACKAGES", "LINUX_RUNNER_VERIFY", "LINUX_DEPLOY", "NODE_VERIFY",
    "WRANGLER_VERIFY", "BROWSER_LOGIN", "IDENTITY_VERIFY", "DEPLOY_PREFLIGHT",
    "DEPLOY", "SECRET_NAMES_VERIFY", "WATCH_KEY_INSTALL", "PROBE_KEY_INSTALL",
}
CF_EVENTS = {"AARIS_CF_" + stage + "_" + outcome for stage in CF_EVENT_STAGES
             for outcome in ("STARTED", "PASSED", "FAILED")} | {
    "AARIS_CF_" + name for name in ("DOWNLOAD_RETRY", "NODE_DOWNLOAD_STARTED",
    "NODE_SHA256_VERIFIED", "HTTPS_PROPAGATION_PENDING", "EXISTING_WATCH_KEY_PRESERVED",
    "DEPLOYED_AUTHENTICATED_STORAGE_CHECK_REQUIRED", "OPEN_OFFICIAL_URL_MANUALLY",
    "PROFILE_SELECTED", "LOCAL_ABORT_RECEIPTS_PRESERVED")}
BOOTSTRAP_ERRORS = frozenset("""
TURBO_BROKER_UPGRADE_REQUIRED_RUN_CCF TURBO_CANDIDATE_CREDENTIAL_INVALID
TURBO_CANDIDATE_CREDENTIAL_MISSING TURBO_CANDIDATE_EXPIRY_INVALID
TURBO_CANDIDATE_GENERATION_INVALID TURBO_CANDIDATE_GRANT_MISMATCH
TURBO_CANONICAL_MAIN_UNCONFIRMED TURBO_CANONICAL_OWNER_REQUIRED
TURBO_CANONICAL_SOURCE_REQUIRED TURBO_CHECKOUT_SOURCE_MISMATCH
TURBO_CODESPACE_ACCOUNT_MISMATCH TURBO_CODESPACE_UNSAFE_OR_FAILED_STATE
TURBO_DUPLICATE_CREATION_REQUIRES_REVIEW TURBO_EXACT_MACHINE_NOT_FOUND
TURBO_EXACT_MACHINE_SELECTION_REQUIRED TURBO_EXISTING_STARTUP_REVIEW_REQUIRED
TURBO_EXPECTED_HOST_REQUIRED TURBO_HOST_CREDENTIAL_MISSING TURBO_HTTP_REQUEST_FAILED
TURBO_INSTALL_FAILED_BEFORE_STAGING TURBO_INVALID_BROKER
TURBO_INVALID_CANDIDATE_SECRET_NAME TURBO_INVALID_MACHINE
TURBO_INVALID_MACHINE_RESPONSE TURBO_INVALID_TRANSACTION
TURBO_MACHINE_LIST_REQUIRES_EXACT_REVIEW TURBO_MACHINE_NOT_CANONICAL_FORK
TURBO_MACHINE_REPOSITORY_REJECTED TURBO_MINIMAL_DEVCONTAINER_UNCONFIRMED
TURBO_NETWORK_OR_RESPONSE_UNCONFIRMED TURBO_OIDC_CLAIMS_REJECTED
TURBO_OIDC_INVALID TURBO_OIDC_SCOPE_OR_EXPIRY_REJECTED
TURBO_PROVIDER_COMMAND_FAILED TURBO_PROVIDER_OUTCOME_UNCONFIRMED
TURBO_PROVIDER_RATE_LIMITED TURBO_PROVIDER_REQUEST_REJECTED
TURBO_PROVIDER_RESPONSE_UNCONFIRMED TURBO_PROVIDER_WRITE_OUTCOME_UNCONFIRMED
TURBO_REMOTE_GENERATION_MISMATCH TURBO_REMOTE_INVOCATION_INVALID
TURBO_REMOTE_STATE_UNRECOGNIZED TURBO_REMOTE_STATUS_INVALID
TURBO_REMOTE_TRANSACTION_MISMATCH TURBO_REQUIRED_MACHINE_TYPE_UNAVAILABLE
TURBO_SOURCE_NOT_CANONICAL_MAIN_HISTORY TURBO_UNITY_FIRST_DEVCONTAINER_REQUIRED
TURBO_USE_SAFE_START_NOT_IMPLICIT_RESTART TURBO_TRANSACTION_RECONCILE_REQUIRED
TURBO_TRANSACTION_ROLLBACK_FAILED TURBO_TRANSACTION_ABORTED
TURBO_TRANSACTION_ROLLED_BACK TURBO_TRANSACTION_FAILED
""".split())


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        # A provider must never redirect a bearer credential to a different host.
        return None


class SetupError(Exception):
    def __init__(self, code, *, rc=1, http=0):
        self.code, self.rc, self.http = code, rc, http
        super().__init__(code)


class Cancelled(Exception):
    def __init__(self, signum=signal.SIGINT):
        self.signum = signum


def private_dir(path):
    path = Path(path)
    if path.is_symlink():
        raise SetupError("UNSAFE_PRIVATE_DIRECTORY")
    path.mkdir(parents=True, exist_ok=True, mode=0o700)
    if path.stat().st_uid != os.getuid():
        raise SetupError("PRIVATE_DIRECTORY_OWNER_MISMATCH")
    path.chmod(0o700)
    return path


def atomic_json(path, value):
    path = Path(path)
    private_dir(path.parent)
    if path.is_symlink():
        raise SetupError("UNSAFE_STATE_SYMLINK")
    fd, name = tempfile.mkstemp(prefix=".state-", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            os.fchmod(stream.fileno(), 0o600)
            json.dump(value, stream, ensure_ascii=True, sort_keys=True)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(name, path)
        directory = os.open(path.parent, os.O_RDONLY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        with contextlib.suppress(FileNotFoundError):
            os.unlink(name)


def read_json(path, default=None):
    try:
        if Path(path).is_symlink():
            raise SetupError("UNSAFE_STATE_SYMLINK")
        return json.loads(Path(path).read_text())
    except FileNotFoundError:
        return {} if default is None else default
    except ValueError:
        raise SetupError("STATE_INVALID_PRESERVED_FOR_RECOVERY") from None


class Lock:
    def __init__(self, path):
        self.path, self.fd = Path(path), None

    def acquire(self):
        private_dir(self.path.parent)
        self.fd = os.open(self.path, os.O_RDWR | os.O_CREAT | os.O_CLOEXEC | os.O_NOFOLLOW, 0o600)
        try:
            fcntl.flock(self.fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            os.close(self.fd)
            self.fd = None
            return False
        return True

    def close(self):
        if self.fd is not None:
            fcntl.flock(self.fd, fcntl.LOCK_UN)
            os.close(self.fd)
            self.fd = None


class Reporter:
    def __init__(self, stream=None):
        self.stream = stream or sys.stdout
        self.events = deque(maxlen=15)
        self.step = "START"
        self.started = time.monotonic()

    def event(self, code, text, *, passed=False):
        if not re.fullmatch(r"[A-Z][A-Z0-9_]{1,95}", code):
            raise ValueError("Only fixed event codes may enter diagnostics")
        self.step = code
        elapsed = round(time.monotonic() - self.started, 1)
        self.events.append(f"{elapsed:.1f}s {code} {'PASS' if passed else 'RUNNING'}")
        print(f"{'✅' if passed else '⏳'} {text} ({elapsed:.1f}s)", file=self.stream, flush=True)

    def note(self, text):
        print(text, file=self.stream, flush=True)

    def error(self, failure):
        # Never interpolate provider stderr, URLs, arbitrary exception text or env.
        code = failure.code if re.fullmatch(r"[A-Z][A-Z0-9_]{1,119}", failure.code) else "UNCLASSIFIED_SETUP_FAILURE"
        print("\n❌ Setup रुका; पूरा setup दोबारा शुरू न करें। वही command resume करेगी.", file=self.stream)
        print("--- COPY FOR CHATGPT ---", file=self.stream)
        print("canonical=" + CANONICAL + " branch=" + BRANCH, file=self.stream)
        print("failed_step=" + self.step, file=self.stream)
        print("reason=" + code, file=self.stream)
        print("exit_code=" + str(int(failure.rc)) + " http_status=" + str(int(failure.http)), file=self.stream)
        for event in self.events:
            print(event, file=self.stream)
        print("--- END COPY ---", file=self.stream, flush=True)


class Runner:
    def __init__(self, folder, reporter):
        self.folder = private_dir(folder)
        self.reporter = reporter
        self.children = set()
        self.counter = 0

    def stop_owned(self):
        children = list(self.children)
        for child in children:
            if child.poll() is None:
                with contextlib.suppress(ProcessLookupError):
                    os.killpg(child.pid, signal.SIGTERM)
        # The nested Linux runner has its own 10-second cooperative shutdown.
        # Allow it to stop its owned process groups before escalating this group.
        limit = time.monotonic() + 25
        while any(p.poll() is None for p in children) and time.monotonic() < limit:
            time.sleep(0.05)
        for child in children:
            if child.poll() is None:
                with contextlib.suppress(ProcessLookupError):
                    os.killpg(child.pid, signal.SIGKILL)
            with contextlib.suppress(Exception):
                child.wait(timeout=2)
            self.children.discard(child)

    def run(self, argv, *, env=None, data=None, interactive=False, on_line=None, check=True, transport_limit=None):
        self.counter += 1
        raw_path = self.folder / f"private-{self.counter}.log"
        input_file = None
        if data is not None:
            input_file = tempfile.TemporaryFile()
            input_file.write(data.encode() if isinstance(data, str) else data)
            input_file.seek(0)
        with raw_path.open("wb") as raw:
            raw_path.chmod(0o600)
            child = subprocess.Popen(argv, env=env, stdin=input_file if input_file else (None if interactive else subprocess.DEVNULL),
                                     stdout=None if interactive else raw, stderr=None if interactive else subprocess.STDOUT,
                                     start_new_session=True, close_fds=True)
            self.children.add(child)
            offset, remainder, last_notice = 0, "", time.monotonic()
            launched = last_notice
            try:
                while child.poll() is None:
                    if transport_limit and time.monotonic() - launched > transport_limit:
                        self.stop_owned()
                        raise SetupError("TRANSPORT_OUTCOME_UNCONFIRMED", rc=75)
                    if on_line:
                        offset, remainder = self._events(raw_path, offset, remainder, on_line)
                    if not interactive and time.monotonic() - last_notice > 30:
                        self.reporter.note("⏳ काम जारी है; दूसरी copy शुरू करने की जरूरत नहीं।")
                        last_notice = time.monotonic()
                    time.sleep(0.1)
                if on_line:
                    self._events(raw_path, offset, remainder, on_line, final=True)
                rc = child.wait()
            except BaseException:
                self.stop_owned()
                raise
            finally:
                self.children.discard(child)
                if input_file:
                    input_file.close()
        output = raw_path.read_bytes()[-2_000_000:].decode("utf-8", "replace") if not interactive else ""
        if rc and check:
            http = 0
            match = re.search(r"HTTP (\d{3})", output)
            if match:
                http = int(match.group(1))
            code = "PROVIDER_COMMAND_FAILED"
            for pattern, reason in ((r"no space left|disk.*full", "DISK_SPACE_REQUIRED"),
                                    (r"quota|billing|spending limit", "PROVIDER_QUOTA_OR_BILLING"),
                                    (r"401|authentication.*failed|bad credentials", "AUTHORIZATION_REQUIRED"),
                                    (r"403|permission denied", "ACCESS_DENIED"),
                                    (r"resolve|timed out|connection reset|network", "NETWORK_RETRY_REQUIRED")):
                if re.search(pattern, output, re.I):
                    code = reason
                    break
            raise SetupError(code, rc=rc if rc > 0 else 128 - rc, http=http)
        return rc, output

    @staticmethod
    def _events(path, offset, remainder, callback, final=False):
        with path.open("rb") as source:
            source.seek(offset)
            chunk = source.read()
            offset = source.tell()
        value = remainder + chunk.decode("utf-8", "replace")
        lines = value.split("\n")
        remainder = lines.pop()
        if final and remainder:
            lines.append(remainder)
            remainder = ""
        for line in lines:
            callback(line.rstrip("\r"))
        return offset, remainder


class Controller:
    def __init__(self, base=None, reporter=None):
        self.base = private_dir(base or Path.home() / ".config/aaris-ai")
        self.reporter = reporter or Reporter()
        self.config_file = self.base / "control.json"
        self.config = read_json(self.config_file, {"schema": 1, "canonical": CANONICAL, "branch": BRANCH})
        if self.config.get("canonical") != CANONICAL or self.config.get("branch") != BRANCH:
            raise SetupError("CANONICAL_CENTER_MISMATCH")
        self.op = None
        self.lock = Lock(self.base / "switch.lock")
        self.scratch = Path(tempfile.mkdtemp(prefix="aaris-switch-", dir=os.environ.get("TMPDIR")))
        self.scratch.chmod(0o700)
        self.runner = Runner(self.scratch, self.reporter)

    def close(self):
        self.runner.stop_owned()
        self.lock.close()
        shutil.rmtree(self.scratch, ignore_errors=True)

    def save(self):
        atomic_json(self.config_file, self.config)
        if self.op:
            atomic_json(self.base / "operations" / self.op["id"] / "operation.json", self.op)

    def phase(self, name, text, passed=False):
        self.op["phase"] = name
        self.save()
        self.reporter.event(name, text, passed=passed)

    def profile_env(self, profile):
        env = {k: v for k, v in os.environ.items() if k not in ("GH_TOKEN", "GITHUB_TOKEN", "GH_CONFIG_DIR", "GH_HOST", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN")}
        env["GH_CONFIG_DIR"] = str(profile)
        env["GH_HOST"] = "github.com"
        env["GH_PROMPT_DISABLED"] = "1"
        return env

    def gh(self, args, profile=None, **kwargs):
        return self.runner.run(["gh", *args], env=self.profile_env(profile or self.master()), **kwargs)

    def master(self):
        return self.config.get("master_profile", str(self.base / "master-gh"))

    def gh_json(self, args, profile=None, **kwargs):
        _, out = self.gh(args, profile, **kwargs)
        try:
            return json.loads(out)
        except ValueError:
            raise SetupError("GITHUB_RESPONSE_UNCONFIRMED") from None

    def auth(self, profile, *, master=False, fresh=False):
        profile = private_dir(profile)
        rc, out = self.gh(["api", "user", "--jq", ".login"], profile, check=False) if not fresh else (1, "")
        if master and rc == 0 and out.strip() != "sundamunda289-art":
            rc = 1  # A prior wrong-account approval is recoverable by logging in again.
        if rc:
            self.reporter.note("GitHub browser में " + ("sundamunda289-art repository owner" if master else "चुने हुए Codespace hosting account") + " से login करें।")
            env = self.profile_env(profile)
            env.pop("GH_PROMPT_DISABLED", None)
            self.runner.run(["gh", "auth", "login", "-h", "github.com", "-p", "https", "-w", "-s", "repo,workflow" if master else "codespace,repo"], env=env, interactive=True)
            _, out = self.gh(["api", "user", "--jq", ".login"], profile)
        login = out.strip()
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9-]{0,38}", login):
            raise SetupError("GITHUB_LOGIN_IDENTITY_INVALID")
        if master and login != "sundamunda289-art":
            raise SetupError("CANONICAL_OWNER_LOGIN_REQUIRED")
        return login

    def ensure_master(self):
        profile = self.master()
        if not Path(profile).exists():
            legacy = Path.home() / ".config/gh"
            if legacy.exists():
                rc, out = self.gh(["api", "user", "--jq", ".login"], legacy, check=False)
                if rc == 0 and out.strip() == "sundamunda289-art":
                    profile = str(legacy)
        self.auth(profile, master=True)
        permission = self.gh_json(["repo", "view", CANONICAL, "--json", "viewerPermission"] , profile).get("viewerPermission")
        if permission != "ADMIN":
            raise SetupError("CANONICAL_REPOSITORY_ADMIN_REQUIRED")
        self.config["master_profile"] = str(profile)
        self.save()

    def latest_sha(self):
        _, out = self.gh(["api", "repos/" + CANONICAL + "/git/ref/heads/main", "--jq", ".object.sha"])
        if not SHA_RE.fullmatch(out.strip()):
            raise SetupError("CANONICAL_MAIN_SHA_INVALID")
        return out.strip()

    def installed_sha(self):
        receipt = ROOT.parent.parent / "SOURCE_SHA"
        if receipt.is_file() and not receipt.is_symlink():
            value = receipt.read_text().strip()
            if SHA_RE.fullmatch(value):
                return value
        raise SetupError("IMMUTABLE_RELEASE_RECEIPT_MISSING_RUN_INSTALLER")

    def resume_pinned_runtime(self, command, resume):
        source = self.op.get("source_sha")
        if not source or source == self.installed_sha():
            return
        releases = Path.home() / ".local/share/aaris-switch/releases"
        release = releases / source
        script = release / "Tools/Codespaces/switch_controller.py"
        if (not script.is_file() or not script.resolve().is_relative_to(releases.resolve())
                or (release / "SOURCE_SHA").read_text().strip() != source):
            raise SetupError("SAVED_RUNTIME_RELEASE_MISSING")
        self.reporter.note("इस अधूरे operation की मूल verified script version से resume हो रहा है।")
        self.close()
        os.execv(sys.executable, [sys.executable, str(script), command] + (["--resume"] if resume else []))

    def load_operation(self, command, resume=False):
        pending = self.config.get("operation")
        if pending and OP_RE.fullmatch(pending):
            old = read_json(self.base / "operations" / pending / "operation.json")
            if old and old.get("status") not in ("COMPLETE", "CANCELLED"):
                if old.get("command") != command:
                    if old.get("committed") and old.get("command") == "codespace" and command == "cf":
                        # Provisioning has its own remote lock and journal. A
                        # completed connection must not trap the owner in an
                        # expired Unity login before they can rotate Cloudflare.
                        self.config["project_operation"] = old["id"]
                    else:
                        raise SetupError("OTHER_SWITCH_PENDING_USE_ITS_ORIGINAL_COMMAND")
                else:
                    self.op = old
                    self.validate_operation()
                    self.reporter.note("अधूरा operation resume हो रहा है; नया duplicate switch नहीं बनेगा।")
                    return
        project_id = self.config.get("project_operation", "")
        if command == "codespace" and OP_RE.fullmatch(project_id):
            project = read_json(self.base / "operations" / project_id / "operation.json")
            if (project.get("status") not in (None, "COMPLETE", "CANCELLED") and project.get("committed")
                    and project.get("machine") == (self.config.get("active") or {}).get("machine")
                    and (self.config.get("desired_broker") or {}).get("state") == "CONNECTED"):
                self.op = project
                self.validate_operation()
                self.config["operation"] = project_id
                self.save()
                self.reporter.note("इसी Codespace का अधूरा Unity/LFS setup resume हो रहा है।")
                return
        reuse = (self.config.get("active") or {}) if resume and command == "codespace" else {}
        if resume and not reuse:
            raise SetupError("NO_PENDING_OPERATION")
        ident = secrets.token_hex(16)
        self.op = {"id": ident, "command": command, "canonical": CANONICAL, "created": int(time.time()), "phase": "START", "status": "RUNNING", "committed": False}
        if reuse:
            self.op.update({key: reuse[key] for key in ("hosting_profile", "hosting_login", "machine") if key in reuse})
        self.config["operation"] = ident
        private_dir(self.base / "operations" / ident)
        self.save()

    def validate_operation(self):
        if not OP_RE.fullmatch(str(self.op.get("id", ""))) or self.op.get("canonical") != CANONICAL:
            raise SetupError("OPERATION_IDENTITY_INVALID")
        if "source_sha" in self.op and not SHA_RE.fullmatch(str(self.op["source_sha"])):
            raise SetupError("OPERATION_SOURCE_SHA_INVALID")
        if "project_sha" in self.op and not SHA_RE.fullmatch(str(self.op["project_sha"])):
            raise SetupError("OPERATION_PROJECT_SHA_INVALID")
        if self.op.get("machine") not in (None, "auto", "new") and not MACHINE_RE.fullmatch(str(self.op["machine"])):
            raise SetupError("OPERATION_MACHINE_INVALID")
        if self.op.get("credential_secret") not in (None, "AARIS_STAGE_" + self.op["id"].upper()):
            raise SetupError("OPERATION_SECRET_IDENTITY_INVALID")

    def secrets_file(self):
        return self.base / "operations" / self.op["id"] / "credentials.json"

    def credentials(self):
        data = read_json(self.secrets_file())
        changed = False
        for key in ("agent_token", "probe_token"):
            if key not in data:
                data[key] = secrets.token_hex(32)
                changed = True
        if changed:
            atomic_json(self.secrets_file(), data)
        return data

    def request_json(self, url, *, bearer=None, data=None):
        headers = {"User-Agent": "Mozilla/5.0 Aaris-Termux", "Accept": "application/json"}
        if bearer:
            headers["Authorization"] = "Bearer " + bearer
        if data is not None:
            headers["Content-Type"] = "application/json"
        for attempt in range(3):
            try:
                req = urllib.request.Request(url, data=json.dumps(data).encode() if data is not None else None, headers=headers)
                with urllib.request.build_opener(NoRedirect).open(req, timeout=90) as response:
                    return json.load(response)
            except urllib.error.HTTPError as exc:
                if exc.code not in (408, 429, 500, 502, 503, 504) or attempt == 2:
                    raise SetupError("BROKER_HTTP_FAILED", http=exc.code) from None
            except (OSError, ValueError):
                if attempt == 2:
                    raise SetupError("BROKER_NETWORK_OR_RESPONSE_UNCONFIRMED") from None
            time.sleep(2 ** (attempt + 1))

    def runner_event(self, line):
        # These two values are for the owner's phone ONLY, never Reporter.events.
        if line.startswith("AARIS_CF_AUTH_URL="):
            url = line.split("=", 1)[1]
            if re.fullmatch(r"https://dash\.cloudflare\.com/oauth2/device(?:/verify)?(?:\?[A-Za-z0-9_=%&.-]+)?", url):
                self.reporter.note("Cloudflare की official login link (इसे error box में न भेजें):\n" + url)
        elif line.startswith("AARIS_CF_AUTH_CODE="):
            code = line.split("=", 1)[1]
            if re.fullmatch(r"[A-Za-z0-9-]{4,32}", code):
                self.reporter.note("Cloudflare device code: " + code)
        elif line in CF_EVENTS:
            self.reporter.event(line, line, passed=line.endswith("_PASSED"))

    def cf_prepare(self):
        if "candidate" in self.op:
            self.verify_broker_candidate(self.op["candidate"])
            return
        self.phase("CF_PREPARE", "Cloudflare login और deployment तैयार हो रहे हैं")
        key_path = self.base / "cloudflare-watch.key"
        if not key_path.exists():
            fd = os.open(key_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
            with os.fdopen(fd, "w") as out:
                out.write(secrets.token_hex(32) + "\n")
        if key_path.is_symlink() or key_path.stat().st_mode & 0o077:
            raise SetupError("UNSAFE_WATCH_KEY_FILE")
        key = key_path.read_text().strip()
        if not re.fullmatch(r"[0-9a-f]{64}", key):
            raise SetupError("INVALID_WATCH_KEY_FILE")
        result_path = self.base / "operations" / self.op["id"] / "cf-candidate.json"
        args = ["bash", str(ROOT / "termux_cf_runner.sh"), "--operation", self.op["id"], "--source-sha", self.op["source_sha"],
                "--watch-hash", hashlib.sha256(key.encode()).hexdigest(), "--probe-hash", hashlib.sha256(self.credentials()["probe_token"].encode()).hexdigest(), "--output", str(result_path)]
        if self.op.get("cf_account"):
            args += ["--account", self.op["cf_account"]]
        if self.op.get("cf_preparation_started"):
            args += ["--resume"]
        self.op["cf_preparation_started"] = True
        self.save()
        rc, _ = self.runner.run(args, check=False, on_line=self.runner_event)
        result = read_json(result_path)
        if rc == 42 and result.get("state") == "CF_ACCOUNT_SELECTION_REQUIRED":
            options = result.get("account_options", [])
            for i, option in enumerate(options, 1):
                self.reporter.note(f"{i}. " + re.sub(r"[^A-Za-z0-9 ._-]", "", str(option.get("name", "")))[:80])
            answer = input("Cloudflare account का नंबर: ").strip()
            if not answer.isdigit() or not 1 <= int(answer) <= len(options):
                raise SetupError("CF_ACCOUNT_SELECTION_REQUIRED")
            chosen = options[int(answer) - 1].get("id", "")
            if not re.fullmatch(r"[0-9a-f]{32}", chosen):
                raise SetupError("CF_ACCOUNT_RESPONSE_INVALID")
            self.op["cf_account"] = chosen
            self.save()
            return self.cf_prepare()
        if rc:
            state = result.get("state", "")
            category = "CF_DEPLOY_OUTCOME_UNCONFIRMED" if "UNKNOWN" in str(state) or "UNCONFIRMED" in str(state) else "CF_PREPARATION_FAILED"
            raise SetupError(category, rc=rc)
        if result.get("operation_id") != self.op["id"] or result.get("source_sha") != self.op["source_sha"] or not BROKER_RE.fullmatch(result.get("broker_url", "")):
            raise SetupError("CF_PREPARATION_RECEIPT_MISMATCH")
        self.verify_broker_candidate(result)
        self.op["candidate"] = result
        self.save()
        self.phase("CF_BROKER_VERIFIED", "Cloudflare Worker और authenticated Durable Object सत्यापित", passed=True)

    def verify_broker_candidate(self, result):
        nonce = secrets.token_hex(16)
        probe = self.request_json(result["broker_url"] + "/broker/check", bearer=self.credentials()["probe_token"], data={"nonce": nonce})
        if (probe.get("nonce") != nonce or probe.get("storage_ready") is not True
                or probe.get("transaction_protocol") != 1 or probe.get("service") != "aaris-turbo"
                or probe.get("source_sha") != self.op["source_sha"]
                or probe.get("deployment_operation") != self.op["id"]):
            raise SetupError("CF_DURABLE_OBJECT_PROBE_FAILED")

    def hosting_profile(self):
        return self.op.get("hosting_profile") or (self.config.get("active") or {}).get("hosting_profile") or str(self.base / "codespace-gh")

    def machines(self, profile, login=None):
        result = self.gh_json(["codespace", "list", "--limit", "100", "--json", "name,state,repository,displayName"], profile)
        if not isinstance(result, list) or len(result) >= 100:
            raise SetupError("CODESPACE_LIST_REQUIRES_REVIEW")
        allowed = {CANONICAL, str(login) + "/Unity-Game-Aris"}
        return [m for m in result if m.get("repository") in allowed and MACHINE_RE.fullmatch(str(m.get("name", "")))]

    def select_host(self, fresh=True):
        if self.op.get("hosting_profile") and self.op.get("hosting_login"):
            return
        profile = self.base / "operations" / self.op["id"] / "github-staging"
        login = self.auth(profile, fresh=fresh)
        choices = self.machines(profile, login)
        machine = "new"
        if len(choices) == 1 and choices[0].get("state") == "Available":
            machine = choices[0]["name"]
        elif choices:
            self.reporter.note("0. नया minimal Codespace बनाएँ; पुराने Codespaces सुरक्षित रहेंगे।")
            for i, item in enumerate(choices, 1):
                self.reporter.note(f"{i}. {item['name']} ({item.get('state', '')})")
            self.reporter.note("पुराने stopped Codespace की startup scripts सुरक्षित साबित न हों तो automatic start रुकेगा।")
            selection = input("Codespace का नंबर [0 = नया]: ").strip() or "0"
            if not selection.isdigit() or not 0 <= int(selection) <= len(choices):
                raise SetupError("EXACT_CODESPACE_SELECTION_REQUIRED")
            if int(selection):
                machine = choices[int(selection) - 1]["name"]
        self.op.update(hosting_profile=str(profile), hosting_login=login, machine=machine)
        self.save()

    def cf_existing_machine(self):
        profile = self.hosting_profile()
        if not Path(profile).exists():
            return False
        rc, out = self.gh(["api", "user", "--jq", ".login"], profile, check=False)
        if rc:
            self.reporter.note("Cloudflare तैयार है; saved Codespace login अभी उपलब्ध नहीं है।")
            return False
        login = out.strip()
        try:
            choices = self.machines(profile, login)
        except SetupError as exc:
            self.op["codespace_pending_reason"] = exc.code
            self.save()
            return False
        current = (self.config.get("active") or {}).get("machine")
        selected = [m for m in choices if m["name"] == current] if current else choices
        if len(selected) > 1:
            live = [m for m in selected if m.get("state") == "Available"]
            selected = live if len(live) == 1 else []
        if len(selected) != 1 or selected[0].get("state") != "Available":
            return False
        try:
            rc, probe = self.gh(["codespace", "ssh", "-c", selected[0]["name"], "--", "printf AARIS_SSH_AVAILABLE"], profile,
                                check=False, transport_limit=90)
            if rc or probe.strip() != "AARIS_SSH_AVAILABLE":
                return False
        except SetupError:
            return False
        # A live SSH host can still be attached to a quota-blocked old broker.
        # Check its authenticated, read-only control path privately before
        # scheduling a handover that would have to wait for that old broker.
        probe_code = r'''import base64, json, pathlib, pwd, re, urllib.error, urllib.request
class NoRedirect(urllib.request.HTTPRedirectHandler):
 def redirect_request(self,*args,**kwargs): return None
folder=pathlib.Path(pwd.getpwuid(__import__('os').geteuid()).pw_dir)/'.config/aaris-turbo'
try:
 if not (folder/'endpoint').exists() and not (folder/'token').exists():
  print('AARIS_OLD_AGENT_ABSENT')
 else:
  endpoint=(folder/'endpoint').read_text().strip(); token=(folder/'token').read_text().strip()
  assert re.fullmatch(r'https://aaris-turbo-terminal\.[a-z0-9-]+\.workers\.dev/agent/[A-Za-z0-9-]+',endpoint)
  payload=json.dumps({'operation':'status','transaction_id':TRANSACTION}).encode()
  req=urllib.request.Request(endpoint+'/control',data=payload,headers={'Authorization':'Bearer '+token,'Content-Type':'application/json','User-Agent':'Mozilla/5.0'})
  opener=urllib.request.build_opener(NoRedirect)
  try:
   with opener.open(req,timeout=30) as response: result=json.load(response)
  except urllib.error.HTTPError as error:
   if error.code!=403: raise
   base,machine=endpoint.rsplit('/agent/',1)
   req=urllib.request.Request(base+'/health',headers={'User-Agent':'Mozilla/5.0','X-Aaris-Op':'control','X-Aaris-Machine':machine,'X-Aaris-Token':token,'X-Aaris-Payload':base64.urlsafe_b64encode(payload).decode().rstrip('=')})
   with opener.open(req,timeout=30) as response: result=json.load(response)
  print('AARIS_OLD_BROKER_READY' if result.get('ok') is True else 'AARIS_OLD_BROKER_UNAVAILABLE')
except Exception:
 print('AARIS_OLD_BROKER_UNAVAILABLE')
'''.replace("TRANSACTION", repr(self.op["id"]))
        try:
            rc, probe = self.gh(["codespace", "ssh", "-c", selected[0]["name"], "--", "python3 -c " + shlex.quote(probe_code)], profile,
                                check=False, transport_limit=90)
            if rc or probe.strip() not in ("AARIS_OLD_AGENT_ABSENT", "AARIS_OLD_BROKER_READY"):
                self.op["codespace_pending_reason"] = "PREVIOUS_BROKER_UNAVAILABLE_OR_UPGRADE_REQUIRED"
                self.save()
                return False
        except SetupError:
            return False
        self.op.update(hosting_profile=str(profile), hosting_login=login, machine=selected[0]["name"])
        self.save()
        return True

    def stage_credential(self):
        if self.op.get("credential_staged"):
            return
        _, token = self.gh(["auth", "token", "-h", "github.com"], self.hosting_profile())
        credential = {"hosting_token": token.strip(), "agent_token": self.credentials()["agent_token"]}
        name = "AARIS_STAGE_" + self.op["id"].upper()
        self.gh(["secret", "set", name, "-R", CANONICAL], data=json.dumps(credential))
        self.op.update(credential_staged=True, credential_secret=name)
        self.save()

    def find_run(self):
        runs = self.gh_json(["run", "list", "-R", CANONICAL, "--workflow", "aaris-turbo-bootstrap.yml", "--event", "workflow_dispatch", "--limit", "100", "--json", "databaseId,displayTitle,status,conclusion"])
        matched = [r for r in runs if r.get("displayTitle") == "Aaris switch " + self.op["id"]]
        if len(matched) > 1:
            raise SetupError("DUPLICATE_WORKFLOW_TRANSACTION")
        return matched[0] if matched else None

    @staticmethod
    def workflow_failure(logs):
        codes = [m for m in re.findall(r"(?:^|\s)(TURBO_[A-Z0-9_]+)(?=\s|$)", logs) if m in BOOTSTRAP_ERRORS]
        statuses = re.findall(r"(?:^|\s)TURBO_HTTP_STATUS=(\d{3})(?=\s|$)", logs)
        return SetupError(codes[-1], http=int(statuses[-1]) if statuses else 0) if codes else None

    def prepare_agent(self, broker):
        if self.op.get("remote_prepared"):
            return
        self.stage_credential()
        self.phase("AGENT_PREPARE", "Exact Codespace पर सुरक्षित candidate agent तैयार हो रहा है")
        run_info = self.find_run()
        if run_info is None and not self.op.get("dispatch_sent"):
            self.op["dispatch_sent"] = True
            self.save()  # Receipt precedes side effect; unknown dispatch is not repeated.
            rc, _ = self.gh(["workflow", "run", "aaris-turbo-bootstrap.yml", "-R", CANONICAL, "--ref", BRANCH,
                "-f", "machine=" + self.op["machine"], "-f", "hosting_login=" + self.op["hosting_login"],
                "-f", "transaction_id=" + self.op["id"], "-f", "candidate_broker=" + broker,
                "-f", "credential_secret=" + self.op["credential_secret"], "-f", "source_sha=" + self.op["source_sha"]], check=False)
            self.op["dispatch_exit"] = rc
            self.save()
        while run_info is None:
            self.reporter.note("⏳ उसी operation का GitHub run खोज रहे हैं; दूसरा bootstrap नहीं भेजा जाएगा।")
            time.sleep(5)
            run_info = self.find_run()
            if self.op.get("dispatch_exit", 0) and run_info is None:
                raise SetupError("WORKFLOW_DISPATCH_OUTCOME_UNCONFIRMED", rc=self.op["dispatch_exit"])
        self.op["run_id"] = run_info["databaseId"]
        self.save()
        while run_info.get("status") != "completed":
            time.sleep(10)
            run_info = self.gh_json(["run", "view", str(self.op["run_id"]), "-R", CANONICAL, "--json", "status,conclusion"])
            self.reporter.note("⏳ GitHub bootstrap जारी है; पुराने active endpoint को नहीं बदला गया।")
        _, logs = self.gh(["run", "view", str(self.op["run_id"]), "-R", CANONICAL, "--log"], check=False)
        failure = self.workflow_failure(logs)
        if run_info.get("conclusion") != "success" and failure:
            raise failure
        names = set(re.findall(r"TURBO_MACHINE=([A-Za-z0-9][A-Za-z0-9-]{2,119})(?:\s|$)", logs))
        if self.op["machine"] in ("auto", "new"):
            if len(names) != 1:
                raise SetupError("BOOTSTRAP_MACHINE_RECEIPT_MISSING")
            self.op["machine"] = names.pop()
            self.save()
        elif names and names != {self.op["machine"]}:
            raise SetupError("BOOTSTRAP_MACHINE_MISMATCH")
        # Workflow success is insufficient; ask the exact machine's journal.
        status = self.remote_transaction("status")
        phase = status.get("state", status.get("phase"))
        if phase not in ("PREPARED", "READY"):
            error = status.get("error", "")
            if re.fullmatch(r"[A-Z0-9_]{1,100}", str(error)) and error:
                raise SetupError(str(error))
            raise SetupError("CANDIDATE_AGENT_NOT_PREPARED")
        self.op["remote_prepared"] = True
        self.save()
        self.phase("AGENT_PROBE_VERIFIED", "नए connection पर वास्तविक restricted command probe सफल", passed=True)

    def remote_transaction(self, action):
        if action not in ("status", "commit", "abort", "resume", "rollback"):
            raise ValueError(action)
        machine = self.op.get("machine", "")
        if not MACHINE_RE.fullmatch(machine):
            raise SetupError("EXACT_MACHINE_REQUIRED")
        arguments = ["--" + action, self.op["id"]]
        if action != "status":
            arguments += ["--wait", "10" if action == "abort" else "180"]
            # Mutations print safe markers. Only the subsequent authoritative
            # status JSON is a receipt, including after a lost/mixed SSH reply.
        program = ("import os,pathlib,pwd,subprocess,sys\n"
                   "home=pathlib.Path(pwd.getpwuid(os.geteuid()).pw_dir)\n"
                   "script=str(home/'.local/share/aaris-turbo/transaction_install.py')\n")
        if action != "status":
            program += "subprocess.run([sys.executable,script]+" + repr(arguments) + ",check=False)\n"
        program += "os.execv(sys.executable,[sys.executable,script,'--status'," + repr(self.op["id"]) + "])\n"
        cmd = "python3 -c " + shlex.quote(program)
        rc, output = self.gh(["codespace", "ssh", "-c", machine, "--", cmd], self.hosting_profile(), check=False,
                             **({"transport_limit": 45} if action == "abort" else {}))
        for line in reversed(output.splitlines()):
            try:
                result = json.loads(line)
                if isinstance(result, dict) and result.get("transaction_id") == self.op["id"]:
                    return result
            except ValueError:
                continue
        raise SetupError("REMOTE_TRANSACTION_OUTCOME_UNCONFIRMED", rc=rc or 1)

    def commit_agent(self):
        if self.op.get("agent_ready"):
            return
        self.op["commit_requested"] = True
        self.save()
        status = self.remote_transaction("commit")
        while status.get("state", status.get("phase")) not in ("READY", "FAILED", "ABORTED", "ROLLED_BACK", "ROLLBACK_FAILED", "RECONCILE_REQUIRED", "NOT_FOUND"):
            self.reporter.note("⏳ चल रहे काम के बाद agent handover पूरा होगा।")
            time.sleep(5)
            status = self.remote_transaction("status")
        if status.get("state", status.get("phase")) != "READY":
            if status.get("state") in ("ROLLED_BACK", "ABORTED"):
                self.op["status"] = "CANCELLED"
                self.save()
            raise SetupError("AGENT_HANDOVER_NOT_COMMITTED")
        self.op["agent_ready"] = True
        self.save()
        self.phase("AGENT_READY", "Codespace agent का verified handover पूरा", passed=True)

    def set_broker_variable(self, broker, *, desired=False):
        variable = "AARIS_NEXT_BROKER_URL" if desired else "AARIS_BROKER_URL"
        original_key = "original_next_broker" if desired else "original_broker"
        before = self.read_broker_variable(variable)
        if original_key not in self.op:
            self.op[original_key] = before if BROKER_RE.fullmatch(before) else ""
            self.save()
        elif before and before not in (self.op[original_key], broker):
            raise SetupError("BROKER_CHANGED_BY_ANOTHER_OPERATION")
        rc, _ = self.gh(["variable", "set", variable, "-R", CANONICAL, "--body", broker], check=False)
        if rc:
            if self.read_broker_variable(variable) != broker:
                raise SetupError("BROKER_SAVE_OUTCOME_UNCONFIRMED", rc=rc)

    def read_broker_variable(self, name):
        if name not in ("AARIS_BROKER_URL", "AARIS_NEXT_BROKER_URL"):
            raise ValueError(name)
        rc, found = self.gh(["variable", "get", name, "-R", CANONICAL], check=False)
        if rc and not re.search(r"HTTP 404|variable not found|could not find variable", found, re.I):
            raise SetupError("CANONICAL_CONFIG_READ_UNCONFIRMED", rc=rc)
        value = "" if rc else found.strip()
        if value and not BROKER_RE.fullmatch(value):
            raise SetupError("CANONICAL_BROKER_VARIABLE_INVALID")
        return value

    def publish_connection(self, broker, pending=False):
        if self.op.get("committed"):
            return
        self.phase("SAVE_CONNECTION", "सत्यापित configuration सेव हो रही है")
        # A verified broker with no reachable agent is desired state. Keep the
        # actually active broker untouched, including from other clients.
        self.set_broker_variable(broker, desired=True)
        if not pending:
            _, token = self.gh(["auth", "token", "-h", "github.com"], self.hosting_profile())
            self.gh(["secret", "set", "CODESPACE_PAT", "-R", CANONICAL], data=token.strip())
            self.set_broker_variable(broker)
            old_active = self.config.get("active") or {}
            old_broker = old_active.get("broker_url") or self.op.get("original_broker")
            if old_broker and old_broker != broker:
                self.config["previous_connection"] = {
                    "broker_url": old_broker, "machine": old_active.get("machine"),
                    "selected": False,
                    "retirement": "AGENT_HANDOVER_VERIFIED" if old_active.get("machine") == self.op.get("machine") else "PENDING",
                }
            self.config["active"] = {"broker_url": broker, "machine": self.op["machine"], "hosting_login": self.op["hosting_login"], "hosting_profile": self.hosting_profile(), "operation": self.op["id"]}
        self.config["desired_broker"] = {"broker_url": broker, "account_id": self.op.get("candidate", {}).get("account_id"), "operation": self.op["id"], "state": "CF_READY_CODESPACE_PENDING" if pending else "CONNECTED"}
        self.op["committed"] = True
        self.op["status"] = "COMPLETE" if self.op["command"] == "cf" else "PROVISIONING"
        if self.op["command"] == "codespace":
            self.config["project_operation"] = self.op["id"]
        self.save()  # Actual commit receipt precedes optional cleanup.
        if self.op["command"] == "cf":
            self.config.setdefault("cleanup", []).append({"kind": "cf_receipt", "operation": self.op["id"]})
            self.save()
        self.cleanup_secret()
        self.retry_cleanup()

    def cleanup_secret(self):
        name = self.op.get("credential_secret")
        if name and self.op.get("credential_staged"):
            entry = {"kind": "secret", "name": name}
            queue = self.config.setdefault("cleanup", [])
            if entry not in queue:
                queue.append(entry)
            self.op["credential_staged"] = False
            self.save()

    def retry_cleanup(self):
        pending = []
        for item in self.config.get("cleanup", []):
            try:
                if item.get("kind") == "secret" and re.fullmatch(r"AARIS_STAGE_[0-9A-F]{32}", str(item.get("name", ""))):
                    rc, output = self.gh(["secret", "delete", item["name"], "-R", CANONICAL], check=False, transport_limit=30)
                    if rc and "HTTP 404" not in output:
                        pending.append(item)
                elif item.get("kind") == "cf_receipt" and OP_RE.fullmatch(str(item.get("operation", ""))):
                    rc, _ = self.runner.run(["bash", str(ROOT / "termux_cf_runner.sh"), "--mode", "commit", "--operation", item["operation"]], check=False, transport_limit=30)
                    if rc:
                        pending.append(item)
            except (SetupError, OSError):
                pending.append(item)
        self.config["cleanup"] = pending
        self.save()
        if pending:
            self.reporter.note("ℹ️ Connection की स्थिति नहीं बदली। बाकी temporary cleanup अगली command पर फिर चलेगा।")

    def do_cf(self):
        self.cf_prepare()
        broker = self.op["candidate"]["broker_url"]
        if self.op.get("committed"):
            self.cleanup_secret()
            self.retry_cleanup()
            return
        reachable = bool(self.op.get("machine") and self.op.get("remote_prepared")) or self.cf_existing_machine()
        if reachable:
            self.prepare_agent(broker)
            self.commit_agent()
        self.publish_connection(broker, pending=not reachable)
        if reachable:
            self.phase("CF_COMPLETE", "Cloudflare और Codespace दोनों verified और सेव", passed=True)
        else:
            self.phase("CF_READY_CODESPACE_PENDING", "Cloudflare सेव है; Codespace जोड़ने के लिए अब CD चलाएँ", passed=True)

    def source_bundle(self):
        required = ["auto_provision_codespace.sh", "provision_codespace.py", "install_unity.sh", "verify_lfs.py", "unity_gui_deps.sh"]
        archive = self.scratch / "provision.tar"
        with tarfile.open(archive, "w") as tar:
            for name in required:
                source = ROOT / name
                if not source.is_file():
                    raise SetupError("LOCAL_INSTALLATION_INCOMPLETE_RUN_INSTALLER")
                tar.add(source, arcname=name, recursive=False)
        return archive.read_bytes()

    def install_provision(self):
        op = self.op["id"]
        # The bundle is our own named public scripts, no auth material or project files.
        # Re-validate/recreate the small immutable helper after a Codespace
        # rebuild; the project/runtime journal remains under /workspaces.
        program = r'''import os,pathlib,pwd,sys,tarfile,tempfile
base=pathlib.Path(pwd.getpwuid(os.geteuid()).pw_dir)/'.local/share/aaris-provision'/OPERATION
if base.is_symlink(): raise SystemExit('UNSAFE_PROVISION_HELPER_PATH')
base.mkdir(parents=True,exist_ok=True,mode=0o700);base.chmod(0o700)
allowed={'auto_provision_codespace.sh','provision_codespace.py','install_unity.sh','verify_lfs.py','unity_gui_deps.sh'}
seen=set()
with tarfile.open(fileobj=sys.stdin.buffer,mode='r|') as archive:
 for entry in archive:
  if entry.name not in allowed or not entry.isfile() or entry.name in seen: raise SystemExit('INVALID_HELPER_BUNDLE')
  seen.add(entry.name); data=archive.extractfile(entry).read(); target=base/entry.name
  if target.is_symlink(): raise SystemExit('UNSAFE_PROVISION_HELPER_PATH')
  if target.exists():
   if target.read_bytes()!=data: raise SystemExit('PROVISION_HELPER_SOURCE_CHANGED')
  else:
   fd,name=tempfile.mkstemp(prefix='.helper-',dir=base)
   try:
    os.fchmod(fd,0o700)
    with os.fdopen(fd,'wb') as out: out.write(data);out.flush();os.fsync(out.fileno())
    os.replace(name,target)
   finally:
    if os.path.exists(name): os.unlink(name)
if seen!=allowed: raise SystemExit('INCOMPLETE_HELPER_BUNDLE')
print('AARIS_PROVISION_HELPER_VERIFIED')
'''.replace("OPERATION", repr(op))
        remote = "python3 -c " + shlex.quote(program)
        self.gh(["codespace", "ssh", "-c", self.op["machine"], "--", remote], self.hosting_profile(), data=self.source_bundle())
        self.op["provision_installed"] = True
        self.save()

    def provision(self, mode):
        arguments = [mode, "--operation", self.op["id"], "--source-sha", self.op["project_sha"], "--project", "/workspaces/Unity-Game-Aris"]
        program = ("import os,pathlib,pwd,subprocess,sys\n"
                   "home=pathlib.Path(pwd.getpwuid(os.geteuid()).pw_dir)\n"
                   "script=home/'.local/share/aaris-provision'/" + repr(self.op["id"]) + "/'auto_provision_codespace.sh'\n"
                   "sys.exit(subprocess.call(['bash',str(script)]+" + repr(arguments) + "))\n")
        cmd = "python3 -c " + shlex.quote(program)
        rc, out = self.gh(["codespace", "ssh", "-c", self.op["machine"], "--", cmd], self.hosting_profile(), check=False)
        for line in reversed(out.splitlines()):
            try:
                result = json.loads(line)
                if (isinstance(result, dict) and result.get("operation") == self.op["id"]
                        and result.get("source_sha") == self.op["project_sha"]):
                    return result
            except ValueError:
                pass
        raise SetupError("PROVISION_STATUS_UNCONFIRMED", rc=rc or 1)

    def license(self):
        env = self.profile_env(self.hosting_profile())
        env.update(AARIS_CODESPACE_NAME=self.op["machine"], AARIS_CODESPACE_AUTH_DIR=self.hosting_profile(), AARIS_SOURCE_SHA=self.op["source_sha"])
        approved = self.config.get("unity_storage_consent", {}).get(self.op["machine"]) is True
        if approved:
            env["AARIS_UNITY_INSECURE_STORAGE"] = "1"
        else:
            env.pop("AARIS_UNITY_INSECURE_STORAGE", None)
        rc, _ = self.runner.run(["bash", str(ROOT / "termux_unity_login_recover.sh")], env=env, interactive=True, check=False)
        if rc == 26 and not approved:
            self.reporter.note("इस Codespace में secure keyring उपलब्ध नहीं है। AGENTS.md के अनुसार file-based Unity credential storage के लिए तुम्हारी अनुमति चाहिए।")
            answer = input("इसी private Codespace में owner-only, बिना encryption वाले credential storage की अनुमति? [y/N]: ").strip().lower()
            if answer in ("y", "yes"):
                self.config.setdefault("unity_storage_consent", {})[self.op["machine"]] = True
                self.save()
                env["AARIS_UNITY_INSECURE_STORAGE"] = "1"
                rc, _ = self.runner.run(["bash", str(ROOT / "termux_unity_login_recover.sh")], env=env, interactive=True, check=False)
        if rc:
            raise SetupError("UNITY_LOGIN_OR_LICENSE_PENDING", rc=rc)

    def provision_watch(self):
        self.install_provision()
        state = self.provision("start")
        previous = None
        license_tried = False
        while True:
            phase = state.get("phase", "UNKNOWN")
            if phase != previous:
                self.reporter.note("⏳ Codespace setup: " + re.sub(r"[^A-Z0-9_]", "", str(phase))[:80])
                previous = phase
            if state.get("phase") == "COMPLETE" and state.get("source_ready") and state.get("license_ready") and state.get("editor_ready"):
                self.op["status"] = "COMPLETE"
                self.op["provision_result"] = {k: state.get(k) for k in ("phase", "editor_ready", "source_ready", "license_ready", "ready_to_open", "project_opened")}
                self.save()
                self.phase("PROJECT_READY", "Unity, canonical source/LFS और license सत्यापित — project तैयार", passed=True)
                return
            if state.get("editor_ready") and not state.get("license_ready") and not license_tried:
                license_tried = True
                self.reporter.note("Unity install पूरा है। LFS चलता रहेगा; अब उसी Codespace के चार login steps चलेंगे।")
                try:
                    self.license()
                except SetupError:
                    self.op["status"] = "WAITING_FOR_UNITY_LOGIN"
                    self.save()
                    self.reporter.note("ℹ️ Remote setup सुरक्षित है। Unity approval बाकी है; CD दोबारा चलाएँ।")
                    return
                state = self.provision("start")
                continue
            if phase in ("PAUSED", "FAILED"):
                reason = state.get("error", "PROVISION_PAUSED")
                if not isinstance(reason, str) or not re.fullmatch(r"[A-Z0-9_]{1,110}", reason):
                    reason = "PROVISION_PAUSED"
                self.op["status"] = "PAUSED"
                self.save()
                raise SetupError(reason)
            time.sleep(10)
            state = self.provision("status")

    def do_codespace(self):
        broker = self.op.get("broker_url") or (self.config.get("desired_broker") or {}).get("broker_url")
        if not broker:
            broker = self.read_broker_variable("AARIS_NEXT_BROKER_URL")
        if not broker:
            broker = self.read_broker_variable("AARIS_BROKER_URL")
        if not broker or not BROKER_RE.fullmatch(broker):
            raise SetupError("RUN_CCF_ONCE_TO_SAVE_CLOUDFLARE")
        self.op["broker_url"] = broker
        self.save()
        if not self.op.get("committed"):
            health = self.request_json(broker + "/health")
            if health.get("transaction_protocol") != 1:
                raise SetupError("BROKER_UPGRADE_REQUIRED_RUN_CCF")
            self.select_host()
            self.prepare_agent(broker)
            self.commit_agent()
            self.publish_connection(broker)
        if self.op.get("git_bridge_ready"):
            probe = "import os,pathlib,pwd,sys; p=pathlib.Path(pwd.getpwuid(os.geteuid()).pw_dir)/'.config/aaris-ai/canonical-deploy-key'; sys.exit(0 if p.is_file() and not p.is_symlink() and p.stat().st_size else 1)"
            rc, _ = self.gh(["codespace", "ssh", "-c", self.op["machine"], "--",
                             "python3 -c " + shlex.quote(probe)], self.hosting_profile(), check=False)
            if rc:
                self.op["git_bridge_ready"] = False
                self.save()
        if not self.op.get("git_bridge_ready"):
            env = self.profile_env(self.hosting_profile())
            env.update(AARIS_MASTER_AUTH_DIR=self.master(), AARIS_CODESPACE_AUTH_DIR=self.hosting_profile(), AARIS_CODESPACE_NAME=self.op["machine"], AARIS_SOURCE_SHA=self.op["source_sha"])
            self.runner.run(["bash", str(ROOT / "termux_git_bridge.sh")], env=env)
            self.op["git_bridge_ready"] = True
            self.save()
            self.phase("GIT_ACCESS_READY", "Canonical repository की इसी Codespace के लिए Git access तैयार", passed=True)
        self.provision_watch()

    def status(self, remote=True):
        ident = self.config.get("operation")
        op = read_json(self.base / "operations" / ident / "operation.json") if ident and OP_RE.fullmatch(ident) else {}
        self.op = op or None
        result = {"canonical": CANONICAL, "branch": BRANCH, "operation": ident,
                  "phase": op.get("phase", "NOT_CONFIGURED"), "status": op.get("status"),
                  "committed": bool(op.get("committed")), "cloudflare": (self.config.get("desired_broker") or {}).get("state"),
                  "machine": op.get("machine")}
        if self.config.get("previous_connection"):
            result["previous_connection"] = self.config["previous_connection"]
        if remote and op.get("provision_installed"):
            try:
                result["provision"] = self.provision("status")
            except SetupError as exc:
                result["remote_status"] = exc.code
        elif self.config.get("project_operation") and self.config["project_operation"] != ident:
            project_id = self.config["project_operation"]
            project = read_json(self.base / "operations" / project_id / "operation.json") if OP_RE.fullmatch(project_id) else {}
            result["project_setup"] = {k: project.get(k) for k in ("machine", "status", "phase")}
            if remote and project.get("provision_installed"):
                try:
                    self.op = project
                    self.validate_operation()
                    result["project_setup"]["provision"] = self.provision("status")
                except SetupError as exc:
                    result["project_setup"]["remote_status"] = exc.code
                finally:
                    self.op = op or None
        self.reporter.note(json.dumps(result, ensure_ascii=False, indent=2))
        return result

    def cancel(self):
        if not self.op:
            return
        self.runner.stop_owned()
        if self.op.get("committed"):
            self.reporter.note("ℹ️ Connection पहले ही सुरक्षित सेव है। केवल phone monitor रुका; remote setup की स्थिति CD --status में मिलेगी।")
            return
        if self.op.get("commit_requested"):
            self.op["status"] = "RECONCILE_REQUIRED"
            self.save()
            self.reporter.note("ℹ️ Handover का परिणाम दोबारा जाँचना है। वही command चलाएँ; configuration अनुमान से वापस नहीं बदली जाएगी।")
            return
        if self.op.get("command") == "cf" and self.op.get("cf_preparation_started"):
            rc, _ = self.runner.run(["bash", str(ROOT / "termux_cf_runner.sh"), "--mode", "abort", "--operation", self.op["id"]], check=False)
            if rc:
                # Keep the private probe key: it may be the only proof that a
                # request completed just before the response was interrupted.
                self.op["status"] = "RECONCILE_REQUIRED"
                self.save()
                self.reporter.note("ℹ️ Cloudflare request का परिणाम अस्पष्ट है। पुराना active profile सुरक्षित है; CCF उसी operation को जाँचकर आगे बढ़ाएगा।")
                return
        safe_to_remove = not self.op.get("dispatch_sent")
        if self.op.get("run_id"):
            with contextlib.suppress(SetupError):
                self.gh(["run", "cancel", str(self.op["run_id"]), "-R", CANONICAL], check=False, transport_limit=15)
        if self.op.get("dispatch_sent") and self.op.get("machine") not in (None, "auto", "new"):
            try:
                state = self.remote_transaction("abort")
                safe_to_remove = state.get("state", state.get("phase")) in ("ABORTED", "ROLLED_BACK")
            except SetupError:
                pass
        if safe_to_remove:
            self.cleanup_secret()
            self.retry_cleanup()
            staging = self.base / "operations" / self.op["id"] / "github-staging"
            if staging.exists() and not staging.is_symlink():
                shutil.rmtree(staging)
            with contextlib.suppress(FileNotFoundError):
                self.secrets_file().unlink()
            self.op["status"] = "CANCELLED"
            self.save()
            self.reporter.note("रद्द किया। इस command के local processes रुके और active profile सुरक्षित रही।")
        else:
            self.op["status"] = "CANCEL_PENDING_RECONCILIATION"
            self.save()
            self.reporter.note("ℹ️ Remote cancellation की पुष्टि बाकी है; सुरक्षित receipt रखी है। वही command दोबारा चलाएँ।")

    def execute(self, command, *, resume=False):
        if command == "status":
            return self.status()
        if not self.lock.acquire():
            self.reporter.note("⏳ पहले से एक switch चल रहा है। दूसरा switch शुरू नहीं किया गया।")
            return self.status(remote=False)
        self.load_operation(command, resume=resume)
        self.resume_pinned_runtime(command, resume)
        self.ensure_master()
        self.retry_cleanup()
        if "original_broker" not in self.op:
            self.op["original_broker"] = self.read_broker_variable("AARIS_BROKER_URL")
            self.save()
        if "source_sha" not in self.op:
            self.op["source_sha"] = self.installed_sha()
            self.save()
        if "project_sha" not in self.op:
            self.op["project_sha"] = self.latest_sha()
            self.save()
        if self.op.get("status") == "CANCEL_PENDING_RECONCILIATION":
            self.cancel()
            if self.op.get("status") != "CANCELLED":
                raise SetupError("REMOTE_CANCEL_CONFIRMATION_PENDING")
            self.reporter.note("पिछला cancellation confirm है। नया switch शुरू करने के लिए command फिर चलाएँ।")
            return
        if command == "cf":
            self.do_cf()
        else:
            self.do_codespace()


def main(argv=None):
    parser = argparse.ArgumentParser(description="Aaris CCF/CD safe account and Codespace setup")
    parser.add_argument("command", choices=("cf", "codespace", "status"))
    parser.add_argument("--status", action="store_true")
    parser.add_argument("--resume", action="store_true")
    args = parser.parse_args(argv)
    os.umask(0o077)
    controller = Controller()
    previous = {}
    def interrupted(signum, _frame):
        raise Cancelled(signum)
    for sig in (signal.SIGINT, signal.SIGTERM):
        previous[sig] = signal.signal(sig, interrupted)
    try:
        controller.execute("status" if args.status else args.command, resume=args.resume)
        return 0
    except Cancelled as exc:
        for sig in previous:
            signal.signal(sig, signal.SIG_IGN)
        with contextlib.suppress(SetupError, OSError):
            controller.cancel()
        return 128 + exc.signum
    except SetupError as exc:
        if controller.op:
            if controller.op.get("committed"):
                controller.reporter.note("ℹ️ Connection सेव है; नीचे वाला कारण अगले setup चरण का है।")
            controller.op["last_error"] = exc.code
            if controller.op.get("status") not in ("CANCELLED", "CANCEL_PENDING_RECONCILIATION", "RECONCILE_REQUIRED"):
                controller.op["status"] = "PAUSED"
            controller.save()
        controller.reporter.error(exc)
        return exc.rc
    except (OSError, ValueError, KeyError, EOFError):
        controller.reporter.error(SetupError("LOCAL_STATE_OR_PROVIDER_RESPONSE_REQUIRES_REVIEW"))
        return 1
    finally:
        controller.close()
        for sig, handler in previous.items():
            signal.signal(sig, handler)


if __name__ == "__main__":
    raise SystemExit(main())
