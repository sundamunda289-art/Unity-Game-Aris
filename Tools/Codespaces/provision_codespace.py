#!/usr/bin/env python3
"""Resumable, single-machine Unity-first Codespace provisioning.

Remote logs are temporary and private. The journal and runtime live inside the
workspace's Git directory, outside tracked source. This module never logs into
Unity, copies credentials, resets/stashes source, opens a project, or creates a
worktree. The phone owns the one official login/activation session.
"""
from __future__ import annotations

import argparse
from contextlib import contextmanager
import fcntl
import hashlib
import json
import os
from pathlib import Path
import pwd
import re
import shlex
import shutil
import signal
import socket
import stat
import subprocess
import sys
import tempfile
import threading
import time

import verify_lfs

VERSION = "6000.3.26f1"
PUBLIC = "https://github.com/sundamunda289-art/Unity-Game-Aris.git"
SSH_REMOTE = "git@github.com:sundamunda289-art/Unity-Game-Aris.git"
REMOTE = "aaris-master"
GIB = 1024 ** 3
HERE = Path(__file__).resolve().parent
ACTIVE_PHASES = {"STARTING", "PREFLIGHT", "DEPENDENCIES", "UNITY_INSTALL",
                 "SOURCE_FETCH", "SOURCE_SYNC", "LFS_FETCH", "LFS_VERIFY",
                 "WAITING_FOR_UNITY_LOGIN", "VERIFYING"}
SAFE_ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9_-]{0,63}")


class Blocked(RuntimeError):
    """Only fixed, non-secret codes are written into public status."""


def private_dir(path: Path) -> Path:
    if path.is_symlink():
        raise Blocked("UNSAFE_STATE_DIRECTORY")
    path.mkdir(parents=True, exist_ok=True, mode=0o700)
    if path.stat().st_uid != os.getuid():
        raise Blocked("STATE_DIRECTORY_OWNER_MISMATCH")
    path.chmod(0o700)
    return path


def atomic_json(path: Path, data: dict) -> None:
    if path.is_symlink():
        raise Blocked("UNSAFE_STATE_FILE")
    fd, name = tempfile.mkstemp(prefix=".state-", dir=path.parent)
    try:
        os.fchmod(fd, 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            json.dump(data, handle, sort_keys=True)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(name, path)
        directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def read_json(path: Path, default=None):
    try:
        if path.is_symlink():
            raise Blocked("UNSAFE_STATE_FILE")
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return {} if default is None else default


@contextmanager
def lock(path: Path, wait: bool = False):
    fd = os.open(path, os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    try:
        if os.fstat(fd).st_uid != os.getuid():
            raise Blocked("LOCK_OWNER_MISMATCH")
        try:
            fcntl.flock(fd, fcntl.LOCK_EX | (0 if wait else fcntl.LOCK_NB))
        except BlockingIOError:
            raise Blocked("OPERATION_ALREADY_RUNNING") from None
        yield fd
    finally:
        os.close(fd)


def proc_start(pid: int) -> str:
    try:
        return Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()[19]
    except (OSError, IndexError):
        return ""


def process_alive(record: dict) -> bool:
    try:
        pid = int(record["pid"])
        started = str(record["start"])
        if pid < 2 or not started or proc_start(pid) != started:
            return False
        args = Path(f"/proc/{pid}/cmdline").read_bytes().split(b"\0")
        return os.fsencode(record["script"]) in args and b"run" in args
    except (KeyError, TypeError, ValueError, OSError):
        return False


def file_identity(project: Path, relative: str) -> dict:
    path = project / relative
    # Do not follow a parent symlink when preserving an untracked path.
    current = project
    for part in Path(relative).parts[:-1]:
        current = current / part
        if current.is_symlink():
            raise Blocked("USER_FILE_PARENT_SYMLINK")
    try:
        info = path.lstat()
    except FileNotFoundError:
        return {"type": "missing"}
    if stat.S_ISLNK(info.st_mode):
        data = os.fsencode(os.readlink(path))
        return {"type": "symlink", "sha256": hashlib.sha256(data).hexdigest(),
                "mode": stat.S_IMODE(info.st_mode)}
    if stat.S_ISREG(info.st_mode):
        return {"type": "file", "sha256": verify_lfs.digest_file(path),
                "size": info.st_size, "mode": stat.S_IMODE(info.st_mode)}
    raise Blocked("USER_FILE_TYPE_REQUIRES_REVIEW")


def preservation_manifest(project: Path) -> dict:
    paths = set()
    for args in (("diff", "--name-only", "-z"),
                 ("diff", "--cached", "--name-only", "-z"),
                 ("ls-files", "--others", "--exclude-standard", "-z")):
        paths.update(os.fsdecode(value) for value in verify_lfs.git(project, *args).split(b"\0") if value)
    return {relative: file_identity(project, relative) for relative in sorted(paths)}


def verify_preserved(project: Path, manifest: dict) -> None:
    if any(file_identity(project, relative) != original for relative, original in manifest.items()):
        raise Blocked("PRESERVED_USER_FILES_CHANGED")


def tracked_clean(project: Path) -> bool:
    for args in (("diff", "--quiet"), ("diff", "--cached", "--quiet")):
        code = subprocess.run(["git", "-C", str(project), *args],
                              stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode
        if code:
            return False
    return True


def workspace_path(path: Path) -> bool:
    return path.is_absolute() and path.is_relative_to(Path("/workspaces"))


def project_editor_running(project: Path) -> bool:
    for directory in Path("/proc").iterdir():
        if not directory.name.isdigit():
            continue
        try:
            args = directory.joinpath("cmdline").read_bytes().split(b"\0")
            if not args or not args[0].endswith(b"/Editor/Unity"):
                continue
            for index, value in enumerate(args[:-1]):
                if value == b"-projectPath" and Path(os.fsdecode(args[index + 1])).resolve() == project:
                    return True
        except (OSError, ValueError):
            continue
    return False


def classify_failure(path: Path) -> str:
    try:
        with path.open("rb") as handle:
            handle.seek(max(0, path.stat().st_size - 65536))
            text = handle.read().decode("utf-8", "replace").lower()
    except OSError:
        return "COMMAND_FAILED"
    markers = {
        "unity_heavy_operation_busy": "UNITY_INSTALL_BUSY",
        "unity_editor_install_category_install_busy": "UNITY_INSTALL_BUSY",
        "unity_editor_install_verification_failed": "EDITOR_NOT_VERIFIED",
        "unity_editor_install_category_authorization": "AUTHORIZATION_REQUIRED",
        "unity_editor_install_category_eula": "UNITY_TERMS_REQUIRED",
        "unity_cli_binary_missing": "UNITY_CLI_BINARY_MISSING",
        "unity_cli_install_failed": "UNITY_CLI_INSTALL_FAILED",
    }
    for marker, code in markers.items():
        if marker in text:
            return code
    for code, pattern in (
        ("INSUFFICIENT_DISK", r"no space left|disk.*full|space_insufficient"),
        ("AUTHORIZATION_REQUIRED", r"permission denied|unauthorized|authentication failed|http.?403|http.?401"),
        ("QUOTA_EXHAUSTED", r"quota|bandwidth.*exceeded|storage.*limit"),
        ("NETWORK_TRANSIENT", r"timed out|timeout|could not resolve|connection reset|connection refused|network.*unreachable|http.?50[234]|http.?429"),
        ("VERSION_UNAVAILABLE", r"release.*not found|changeset.*not|http.?404"),
        ("DEPENDENCY_MISSING", r"shared librar|not found|command not found"),
    ):
        if re.search(pattern, text):
            return code
    return "COMMAND_FAILED"


class Context:
    def __init__(self, project: Path, operation: str, source_sha: str):
        self.project = project.resolve(strict=True)
        self.operation = operation
        self.source_sha = source_sha
        self.gitdir = Path(verify_lfs.git(self.project, "rev-parse", "--absolute-git-dir").decode().strip()).resolve()
        self.root = private_dir(self.gitdir / "aaris-provision")
        self.directory = private_dir(self.root / operation)
        self.logs = private_dir(private_dir(Path("/tmp") / f"aaris-provision-{os.getuid()}") / operation)
        self.runtime = private_dir(self.gitdir / "aaris-runtime")
        self.state_path = self.directory / "state.json"
        self.mutex = threading.RLock()
        self.machine = os.environ.get("CODESPACE_NAME") or socket.gethostname()
        # Codespaces SSH can inherit HOME=/root for a non-root UID. Use the
        # effective account's home, as the reviewed mobile license helper does.
        self.home = Path(pwd.getpwuid(os.geteuid()).pw_dir)
        self.env = {key: value for key, value in os.environ.items()
                    if key not in {"GH_TOKEN", "GITHUB_TOKEN", "UNITY_PASSWORD", "UNITY_SERVICE_ACCOUNT_SECRET"}}
        self.env.update({"GIT_TERMINAL_PROMPT": "0", "GIT_LFS_SKIP_SMUDGE": "1",
                         "HOME": str(self.home),
                         "AARIS_UNITY_PROJECT_DIR": str(self.project),
                         "AARIS_UNITY_EDITOR_ROOT": str(self.runtime / "editors"),
                         "AARIS_PROVISION_LOG_DIR": str(self.logs),
                         "AARIS_HEAVY_LOCK_PATH": str(self.root / "heavy.lock")})
        current = read_json(self.state_path)
        if current and any(current.get(key) != value for key, value in
                           (("operation", operation), ("source_sha", source_sha),
                            ("project", str(self.project)), ("machine", self.machine))):
            raise Blocked("OPERATION_IDENTITY_MISMATCH")

    def update(self, **values) -> dict:
        with self.mutex:
            state = read_json(self.state_path)
            state.update({"schema": 1, "operation": self.operation, "source_sha": self.source_sha,
                          "project": str(self.project), "machine": self.machine, "version": VERSION,
                          "updated_at": int(time.time()), **values})
            atomic_json(self.state_path, state)
            return state

    def status(self) -> dict:
        with self.mutex:
            state = read_json(self.state_path) or {"operation": self.operation, "phase": "NOT_STARTED",
                                                  "source_sha": self.source_sha, "project": str(self.project)}
            alive = process_alive(read_json(self.directory / "process.json"))
            state["running"] = alive
            state["resumable"] = state.get("phase") != "COMPLETE"
            if not alive and state.get("phase") in ACTIVE_PHASES - {"WAITING_FOR_UNITY_LOGIN"}:
                state["phase"] = "INTERRUPTED"
            return state


class Provisioner:
    def __init__(self, context: Context):
        self.ctx = context
        self.stop = threading.Event()
        self.children = set()
        self.child_mutex = threading.RLock()
        self.license_thread = None

    def command(self, argv: list[str], name: str, timeout: int = 1800, retry: bool = False) -> None:
        logfile = self.ctx.logs / (name + ".log")
        for attempt in range(3 if retry else 1):
            if self.stop.is_set():
                raise Blocked("INTERRUPTED")
            with logfile.open("ab") as log:
                process = subprocess.Popen(argv, cwd=self.ctx.project, env=self.ctx.env,
                                           stdin=subprocess.DEVNULL, stdout=log, stderr=log,
                                           start_new_session=True)
                with self.child_mutex:
                    self.children.add(process)
                deadline = time.monotonic() + timeout
                began = time.monotonic()
                last_heartbeat = 0.0
                try:
                    while process.poll() is None:
                        if time.monotonic() - last_heartbeat >= 5:
                            self.ctx.update(step=name, step_elapsed_seconds=int(time.monotonic() - began),
                                            heartbeat_at=int(time.time()))
                            last_heartbeat = time.monotonic()
                        if self.stop.wait(1) or time.monotonic() > deadline:
                            os.killpg(process.pid, signal.SIGTERM)
                            try:
                                process.wait(timeout=10)
                            except subprocess.TimeoutExpired:
                                os.killpg(process.pid, signal.SIGKILL)
                                process.wait(timeout=10)
                            raise Blocked("INTERRUPTED" if self.stop.is_set() else "COMMAND_TIMED_OUT")
                    if process.returncode == 0:
                        return
                finally:
                    with self.child_mutex:
                        self.children.discard(process)
            error = classify_failure(logfile)
            if not retry or error != "NETWORK_TRANSIENT" or attempt == 2:
                raise Blocked(error)
            self.ctx.update(retry_attempt=attempt + 2, last_retry_reason=error)
            if self.stop.wait((3, 8, 15)[attempt]):
                raise Blocked("INTERRUPTED")

    def editor_status(self) -> dict:
        result = subprocess.run(["bash", str(HERE / "install_unity.sh"), "--status-json"],
                                cwd=self.ctx.project, env=self.ctx.env,
                                stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                timeout=75, check=False)
        try:
            return json.loads(result.stdout) if result.returncode == 0 else {"ready": False}
        except (ValueError, UnicodeDecodeError):
            return {"ready": False}

    def dependencies(self) -> None:
        self.ctx.update(phase="DEPENDENCIES")
        os_release = Path("/etc/os-release").read_text()
        if 'ID=ubuntu' not in os_release or 'VERSION_ID="22.04"' not in os_release:
            raise Blocked("SUPPORTED_UBUNTU_2204_REQUIRED")
        self.command(["sudo", "-n", "apt-get", "-o", "DPkg::Lock::Timeout=180", "update", "-qq"],
                     "apt-index", timeout=600, retry=True)
        self.command(["sudo", "-n", "env", "DEBIAN_FRONTEND=noninteractive", "apt-get",
                      "-o", "DPkg::Lock::Timeout=180", "install", "-y", "-qq",
                      "git-lfs", "jq", "curl", "ca-certificates", "unzip", "util-linux"],
                     "apt-packages", timeout=900, retry=True)
        self.command(["bash", str(HERE / "unity_gui_deps.sh")], "gui-dependencies", timeout=1200, retry=True)

    def ensure_editor(self) -> dict:
        status = self.editor_status()
        if status.get("ready"):
            if not shutil.which("git-lfs"):
                self.dependencies()
            self.ctx.update(editor_ready=True, editor=status["editor"], editor_storage_persistent=status["persistent_workspace"])
            return status
        self.dependencies()
        self.ctx.update(phase="UNITY_INSTALL", editor_ready=False)
        self.command(["bash", str(HERE / "install_unity.sh")], "unity-install", timeout=14400)
        status = self.editor_status()
        if not status.get("ready") or status.get("version") != VERSION:
            raise Blocked("EDITOR_NOT_VERIFIED")
        self.ctx.update(editor_ready=True, editor=status["editor"], editor_storage_persistent=status["persistent_workspace"])
        return status

    def license_status(self) -> tuple[bool, bool]:
        home = self.ctx.home
        cli_home = Path(os.environ.get("UNITY_CLI_HOME", home / ".local/share/aaris-unity-cli"))
        candidates = [cli_home / "bin/unity", home / ".local/bin/unity", home / ".unity/bin/unity"]
        executable = next((str(path) for path in candidates if path.is_file() and os.access(path, os.X_OK)), shutil.which("unity"))
        if not executable:
            return False, False
        env = {**self.ctx.env, "UNITY_CLI_HOME": str(cli_home), "DO_NOT_TRACK": "1"}
        results = []
        for what in ("auth", "license"):
            try:
                checked = subprocess.run([executable, what, "status", "--format", "json", "--non-interactive"],
                                         stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                                         stderr=subprocess.DEVNULL, timeout=40, env=env)
                results.append(checked.returncode == 0)
            except (OSError, subprocess.TimeoutExpired):
                results.append(False)
        return results[0], results[0] and results[1]

    def watch_license(self) -> None:
        while not self.stop.is_set():
            auth, licensed = self.license_status()
            if self.stop.is_set():
                return
            self.ctx.update(auth_ready=auth, license_ready=licensed,
                            license_checked_at=int(time.time()))
            if licensed or self.stop.wait(10):
                return

    def git(self, *args: str) -> list[str]:
        return ["git", "-C", str(self.ctx.project), *args]

    def require_source_idle(self) -> None:
        if (self.ctx.gitdir / "index.lock").exists():
            raise Blocked("GIT_OPERATION_BUSY")
        if project_editor_running(self.ctx.project):
            raise Blocked("PROJECT_EDITOR_ALREADY_RUNNING")
        # A sparse/assume-unchanged checkout can hide missing or modified files
        # from git diff. Do not certify it as a complete canonical checkout.
        flags = verify_lfs.git(self.ctx.project, "ls-files", "-v", "-z").split(b"\0")
        if any(record and (record[:1] == b"S" or record[:1].islower()) for record in flags):
            raise Blocked("TRACKED_FILE_FLAGS_REQUIRE_REVIEW")
        if not tracked_clean(self.ctx.project):
            raise Blocked("DIRTY_TRACKED_WORK_PROTECTED")

    def lfs_budget(self, items: list[dict], media: Path, regular_bytes: int = 0) -> None:
        missing = {}
        hydration = 0
        for item in items:
            try:
                cache = verify_lfs.safe_file(media, item["oid"][:2] + "/" + item["oid"][2:4] + "/" + item["oid"])
            except ValueError:
                raise Blocked("UNSAFE_LFS_CACHE") from None
            if cache.is_file():
                if cache.stat().st_size != item["size"] or verify_lfs.digest_file(cache) != item["oid"]:
                    raise Blocked("CORRUPT_LFS_CACHE_REQUIRES_REPAIR")
            else:
                missing[item["oid"]] = item["size"]
            path = verify_lfs.safe_file(self.ctx.project, item["path"])
            if not path.is_file() or path.stat().st_size != item["size"] or verify_lfs.digest_file(path) != item["oid"]:
                hydration += item["size"]
        nearest = media
        while not nearest.exists():
            nearest = nearest.parent
        cache_need = sum(missing.values()) + max(missing.values(), default=0)
        work_need = hydration + regular_bytes
        same_device = nearest.stat().st_dev == self.ctx.project.stat().st_dev
        checks = [(nearest, cache_need + (work_need if same_device else 0) + 2 * GIB)]
        if not same_device:
            checks.append((self.ctx.project, work_need + 2 * GIB))
        for folder, needed in checks:
            filesystem = os.statvfs(folder)
            if filesystem.f_bavail * filesystem.f_frsize < needed:
                self.ctx.update(required_free_bytes=needed, available_free_bytes=filesystem.f_bavail * filesystem.f_frsize)
                raise Blocked("INSUFFICIENT_DISK")
            if filesystem.f_favail >= 0 and filesystem.f_favail < len(items) * 3 + 10000:
                raise Blocked("INSUFFICIENT_INODES")
        self.ctx.update(lfs_total_files=len(items), lfs_total_bytes=sum(item["size"] for item in items),
                        lfs_download_bytes=sum(missing.values()), lfs_hydration_bytes=hydration,
                        lfs_concurrent_transfers=1)

    def source(self, manifest: dict) -> None:
        self.require_source_idle()
        verify_preserved(self.ctx.project, manifest)
        self.ctx.update(phase="SOURCE_FETCH", source_ready=False)
        self.command(self.git("-c", "credential.helper=", "fetch", "--no-tags", "--no-recurse-submodules",
                              PUBLIC, self.ctx.source_sha), "git-fetch", timeout=1800, retry=True)
        resolved = verify_lfs.commit_sha(self.ctx.project, self.ctx.source_sha)
        if resolved != self.ctx.source_sha:
            raise Blocked("SOURCE_SHA_MISMATCH")
        version = verify_lfs.git(self.ctx.project, "show", f"{resolved}:ProjectSettings/ProjectVersion.txt").decode()
        if not re.search(r"^m_EditorVersion:\s*" + re.escape(VERSION) + r"\s*$", version, re.M):
            raise Blocked("PROJECT_EDITOR_VERSION_MISMATCH")
        entries = verify_lfs.tree_entries(self.ctx.project, resolved)
        if any(item["kind"] == "commit" for item in entries):
            raise Blocked("SUBMODULE_SETUP_REQUIRES_REVIEW")
        self.require_source_idle()
        verify_preserved(self.ctx.project, manifest)
        head = verify_lfs.commit_sha(self.ctx.project, "HEAD")
        if head != resolved:
            ancestor = subprocess.run(self.git("merge-base", "--is-ancestor", head, resolved),
                                      stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode
            if ancestor:
                raise Blocked("DIVERGENT_SOURCE_PROTECTED")
        items = verify_lfs.inventory(self.ctx.project, resolved)
        # Local Git LFS configuration is required, but existing hooks are never
        # forced/overwritten. A custom-hook conflict is a clear review state.
        self.command(self.git("lfs", "install", "--local", "--skip-smudge"), "lfs-install", timeout=120)
        environment = subprocess.run(self.git("lfs", "env"), env=self.ctx.env, stdout=subprocess.PIPE,
                                     stderr=subprocess.DEVNULL, timeout=30, check=False)
        values = environment.stdout.decode("utf-8", "replace").splitlines()
        media_values = [line.split("=", 1)[1] for line in values if line.startswith("LocalMediaDir=")]
        if environment.returncode or len(media_values) != 1:
            raise Blocked("LFS_STORAGE_UNVERIFIED")
        media = Path(media_values[0])
        if not workspace_path(media):
            raise Blocked("LFS_STORAGE_MUST_BE_PERSISTENT")
        pointer_paths = {item["path"] for item in items}
        regular_bytes = sum(item["size"] for item in entries
                            if item["kind"] == "blob" and item["path"] not in pointer_paths)
        with lock(self.ctx.root / "heavy.lock"):
            self.lfs_budget(items, media, regular_bytes)
            self.require_source_idle()
            verify_preserved(self.ctx.project, manifest)
            if items:
                key = self.ctx.home / ".config/aaris-ai/canonical-deploy-key"
                existing = subprocess.run(self.git("remote", "get-url", REMOTE), stdout=subprocess.PIPE,
                                          stderr=subprocess.DEVNULL, check=False)
                if existing.returncode:
                    remote_url = SSH_REMOTE if key.is_file() else PUBLIC
                    self.command(self.git("remote", "add", REMOTE, remote_url), "canonical-remote", timeout=30)
                else:
                    remote_url = existing.stdout.decode().strip()
                    if remote_url not in (SSH_REMOTE, PUBLIC):
                        raise Blocked("CANONICAL_REMOTE_MISMATCH")
                if remote_url == SSH_REMOTE:
                    if (key.is_symlink() or not key.is_file() or key.stat().st_uid != os.getuid()
                            or stat.S_IMODE(key.stat().st_mode) != 0o600):
                        raise Blocked("CANONICAL_DEPLOY_KEY_REQUIRED")
                    self.ctx.env["GIT_SSH_COMMAND"] = (
                        "ssh -F /dev/null -i " + shlex.quote(str(key)) +
                        " -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=accept-new")
                self.ctx.update(phase="LFS_FETCH")
                self.command(self.git("-c", "lfs.concurrenttransfers=1", "-c", "lfs.transfer.maxretries=3",
                                      "-c", "lfs.transfer.maxretrydelay=10", "lfs", "fetch",
                                      "--include=", "--exclude=", REMOTE, resolved),
                             "lfs-fetch", timeout=14400, retry=True)
            # Keep the old checked-out source until every target object has
            # downloaded. A network failure above cannot leave a partial merge.
            self.require_source_idle()
            verify_preserved(self.ctx.project, manifest)
            if verify_lfs.commit_sha(self.ctx.project, "HEAD") != head:
                raise Blocked("SOURCE_CHANGED_DURING_SETUP")
            self.ctx.update(phase="SOURCE_SYNC")
            if head != resolved:
                # Command-scoped: keep user hooks on disk, but do not let a
                # post-merge hook start an unbudgeted competing LFS download.
                self.command(self.git("-c", "core.hooksPath=/dev/null", "merge", "--ff-only", "--no-overwrite-ignore", resolved),
                             "git-fast-forward", timeout=300)
            if verify_lfs.commit_sha(self.ctx.project, "HEAD") != resolved:
                raise Blocked("SOURCE_HEAD_MISMATCH")
            verify_preserved(self.ctx.project, manifest)
            if items:
                self.command(self.git("lfs", "checkout"), "lfs-checkout", timeout=3600)
            self.ctx.update(phase="LFS_VERIFY")
            checked = verify_lfs.verify(self.ctx.project, resolved, items)
            if not checked["ok"]:
                self.ctx.update(lfs_verification=checked)
                raise Blocked("LFS_INTEGRITY_FAILED")
            verify_preserved(self.ctx.project, manifest)
            if not tracked_clean(self.ctx.project):
                raise Blocked("SOURCE_CHANGED_DURING_SETUP")
            self.ctx.update(source_ready=True, source_verified_at=int(time.time()),
                            lfs_verified_files=checked["tracked"], lfs_verified_bytes=checked["verified_bytes"])

    def run(self, license_wait: int = 600) -> int:
        self.ctx.update(phase="PREFLIGHT", error=None, editor_ready=False, source_ready=False,
                        auth_ready=False, license_ready=False, ready_to_open=False,
                        project_opened=False, android_modules_verified=False)
        try:
            manifest_path = self.ctx.directory / "preserved.json"
            previous = read_json(manifest_path)
            verify_preserved(self.ctx.project, previous)
            manifest = {**preservation_manifest(self.ctx.project), **previous}
            atomic_json(manifest_path, manifest)
            self.ctx.update(preserved_user_files=len(manifest))
            self.require_source_idle()
            self.ensure_editor()
            # License checks are read-only; the phone's existing helper owns
            # the one OAuth/activation process. LFS starts only after this point.
            self.license_thread = threading.Thread(target=self.watch_license, daemon=True)
            self.license_thread.start()
            self.source(manifest)
            self.ctx.update(phase="WAITING_FOR_UNITY_LOGIN")
            deadline = time.monotonic() + license_wait
            while not read_json(self.ctx.state_path).get("license_ready"):
                if self.stop.wait(2):
                    raise Blocked("INTERRUPTED")
                if time.monotonic() >= deadline:
                    self.ctx.update(phase="WAITING_FOR_UNITY_LOGIN", error=None)
                    return 10
            self.ctx.update(phase="VERIFYING")
            verify_preserved(self.ctx.project, manifest)
            if verify_lfs.commit_sha(self.ctx.project, "HEAD") != self.ctx.source_sha:
                raise Blocked("SOURCE_CHANGED_DURING_SETUP")
            self.ctx.update(phase="COMPLETE", ready_to_open=True, error=None,
                            preservation_verified=True, completed_at=int(time.time()))
            return 0
        except Blocked as error:
            self.ctx.update(phase="PAUSED", error=str(error), ready_to_open=False)
            return 10
        except (OSError, ValueError, RuntimeError, subprocess.SubprocessError):
            self.ctx.update(phase="PAUSED", error="PROVISION_INTERNAL_CHECK_FAILED", ready_to_open=False)
            return 11
        finally:
            self.stop.set()
            if self.license_thread:
                self.license_thread.join(timeout=2)


def start(context: Context, args) -> dict:
    with lock(context.root / "launch.lock"):
        active = read_json(context.root / "active.json")
        active_id = active.get("operation", "")
        if SAFE_ID.fullmatch(active_id):
            previous_dir = context.root / active_id
            if process_alive(read_json(previous_dir / "process.json")):
                state = read_json(previous_dir / "state.json")
                state["running"] = True
                state["attached"] = True
                if state.get("source_sha") != context.source_sha or state.get("project") != str(context.project):
                    state["error"] = "ANOTHER_PROVISION_TARGET_IS_RUNNING"
                return state
        context.update(phase="STARTING", error=None, ready_to_open=False)
        atomic_json(context.root / "active.json", {"operation": context.operation})
        argv = [sys.executable, str(Path(__file__).resolve()), "run",
                "--operation", context.operation, "--source-sha", context.source_sha,
                "--project", str(context.project), "--license-wait", str(args.license_wait)]
        with (context.logs / "supervisor.log").open("ab") as output:
            subprocess.Popen(argv, stdin=subprocess.DEVNULL, stdout=output, stderr=output,
                             start_new_session=True, close_fds=True)
        for _ in range(20):
            time.sleep(0.1)
            state = context.status()
            if state.get("running"):
                return state
        return context.status()


def main() -> int:
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("start", "status", "run"))
    parser.add_argument("--operation", required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--project", required=True)
    parser.add_argument("--license-wait", type=int, default=600)
    args = parser.parse_args()
    if (not SAFE_ID.fullmatch(args.operation) or not re.fullmatch(r"[0-9a-f]{40}", args.source_sha)
            or not 0 <= args.license_wait <= 3600):
        parser.error("invalid operation, source SHA, or license wait")
    try:
        project = Path(args.project).resolve(strict=True)
        if not workspace_path(project) or project == Path("/workspaces"):
            raise Blocked("PROJECT_MUST_BE_IN_WORKSPACES")
        context = Context(project, args.operation, args.source_sha)
        if args.mode == "status":
            print(json.dumps(context.status(), sort_keys=True))
            return 0
        if args.mode == "start":
            print(json.dumps(start(context, args), sort_keys=True))
            return 0
        with lock(context.root / "operation.lock"):
            atomic_json(context.root / "active.json", {"operation": context.operation})
            atomic_json(context.directory / "process.json",
                        {"pid": os.getpid(), "start": proc_start(os.getpid()),
                         "script": str(Path(__file__).resolve())})
            runner = Provisioner(context)
            for signum in (signal.SIGINT, signal.SIGTERM):
                signal.signal(signum, lambda *_: runner.stop.set())
            code = runner.run(args.license_wait)
            print(json.dumps(context.status(), sort_keys=True))
            return code
    except Blocked as error:
        print(json.dumps({"phase": "PAUSED", "error": str(error), "ready_to_open": False}))
        return 10
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError):
        print(json.dumps({"phase": "PAUSED", "error": "PROVISION_CONTEXT_UNAVAILABLE", "ready_to_open": False}))
        return 11


if __name__ == "__main__":
    raise SystemExit(main())
