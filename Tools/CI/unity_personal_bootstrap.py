#!/usr/bin/env python3
"""Authorized Unity 6 Personal bootstrap for a disposable *same-container* CI host.

Accepted paths:
  1. An existing, legitimately issued UNITY_LICENSE ULF secret: install privately.
  2. Otherwise: install Unity's official CLI; offer its genuine OAuth sign-in URL
     *only* through the Cloudflare email-PIN gated /api/license endpoint; wait
     for the user to sign in, then run Unity's documented --personal command.

An account password and Unity Cloud service token are NEVER used to bypass OAuth,
and a new device may still need manual sign-in. No cache/release of auth tokens.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import queue
import re
import shutil
import subprocess
import threading
import time
from urllib.parse import urlparse

PROJECT = Path("/project")
STATE_DIR = Path(os.getenv("AARIS_LIVE_STATE", "/project/ci-live-control"))
LICENSE_FILE = Path("/root/.local/share/unity3d/Unity/Unity_lic.ulf")
UNITY_INSTALLER = "https://unity.com/install.sh"
URL_PATTERN = re.compile(r"auth_url=(https?://[^\s\"'\\]+)|https?://[a-zA-Z0-9._~:/?#\[\]@!$&'()*+,;=%-]+")
CANDIDATES = (
    "/opt/unity/Editor/Unity",
    "/opt/unity/Editor/Unity.x86_64",
    "/usr/local/bin/unity-editor",
)

def private_status(state: str, *, url: str | None = None, detail: str = "") -> None:
    STATE_DIR.mkdir(parents=True, exist_ok=True)
    record = {"state": state, "detail": detail}
    if url:
        record["auth_url"] = url
    temp = STATE_DIR / "license-status.tmp"
    temp.write_text(json.dumps(record))
    # The Unity container runs as root while the Cloudflare-facing Python
    # server runs as the unprivileged GitHub runner. Transfer ownership to
    # that runner, retaining owner-only 0600 access to the private OAuth URL.
    host_uid = os.getenv("AARIS_HOST_UID")
    host_gid = os.getenv("AARIS_HOST_GID")
    if host_uid is not None and host_gid is not None:
        uid, gid = int(host_uid), int(host_gid)
        if not (0 <= uid <= 65535 and 0 <= gid <= 65535):
            raise ValueError("Invalid host ownership for private license status")
        os.chown(temp, uid, gid)
    os.chmod(temp, 0o600)
    os.replace(temp, STATE_DIR / "license-status.json")

def allowed_auth_url(url: str) -> bool:
    try:
        parsed = urlparse(url)
        host = (parsed.hostname or "").lower()
        return (parsed.scheme == "https" and bool(host) and
                any(host == d or host.endswith("." + d)
                    for d in ("unity.com", "unity3d.com")) and
                not parsed.username and not parsed.password and
                len(url) < 4096)
    except ValueError:
        return False

def extract_auth_url(line: str) -> str | None:
    # Unity CLI emits auth_url=... on stderr for remote/headless sign-in.
    for match in URL_PATTERN.finditer(line):
        value = (match.group(1) or match.group(0)).strip('",\'<>)]}')
        if allowed_auth_url(value):
            return value
    return None

def write_provided_license() -> bool:
    raw = os.getenv("UNITY_LICENSE", "").strip()
    if not raw:
        return False
    if len(raw) > 200000 or not raw.startswith("<"):
        raise ValueError("The UNITY_LICENSE Secret is not a valid ULF XML candidate")
    LICENSE_FILE.parent.mkdir(parents=True, exist_ok=True)
    LICENSE_FILE.write_text(raw)
    os.chmod(LICENSE_FILE, 0o600)
    private_status("license_secret_installed",
                   detail="An authorized ULF was installed; Editor must validate it.")
    return True

def installed_cli() -> str | None:
    for binary in (shutil.which("unity"), "/root/.local/bin/unity",
                   "/root/.unity/bin/unity", "/opt/aaris-cli/bin/unity"):
        if binary and Path(binary).is_file() and os.access(binary, os.X_OK):
            return binary
    return None

def install_official_cli() -> str:
    existing = installed_cli()
    if existing:
        return existing
    private_status("installing_official_unity_cli")
    # Download from Unity's own domain; no curl | bash pipeline or third party.
    command = subprocess.run(["curl", "-fsSL", "--retry", "2", "--max-time", "90",
                              UNITY_INSTALLER, "-o", "/tmp/aaris-unity-cli-install.sh"],
                             timeout=120, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    if command.returncode != 0:
        raise RuntimeError("Unity official CLI installer could not be downloaded")
    environment = {**os.environ, "UNITY_CLI_CHANNEL": "beta",
                   "UNITY_CLI_HOME": "/opt/aaris-cli"}
    command = subprocess.run(["bash", "/tmp/aaris-unity-cli-install.sh"],
                             timeout=180, env=environment, stdout=subprocess.DEVNULL,
                             stderr=subprocess.DEVNULL)
    if command.returncode != 0:
        raise RuntimeError("Unity official CLI installation failed")
    binary = installed_cli()
    if not binary:
        raise RuntimeError("Unity CLI binary was not found after installation")
    return binary

def login_via_authorized_browser(binary: str) -> None:
    private_status("starting_unity_oauth")
    # Never allow a service-account env to impersonate a user entitlement.
    environment = {k: v for k, v in os.environ.items()
                   if k not in ("UNITY_SERVICE_ACCOUNT_ID",
                                "UNITY_SERVICE_ACCOUNT_SECRET", "UNITY_PASSWORD")}
    process = subprocess.Popen([binary, "auth", "login", "--format", "json"],
                               env=environment, text=True, encoding="utf-8",
                               errors="replace", stdout=subprocess.PIPE,
                               stderr=subprocess.STDOUT, bufsize=1)
    updates: queue.Queue[str | None] = queue.Queue()
    def reader():
        try:
            for line in process.stdout:
                updates.put(line)
        finally:
            updates.put(None)
    threading.Thread(target=reader, daemon=True).start()
    deadline = time.monotonic() + 500
    seen_url = None
    while time.monotonic() < deadline:
        try:
            line = updates.get(timeout=1.0)
        except queue.Empty:
            if process.poll() is not None:
                break
            continue
        if line is None:
            break
        auth_url = extract_auth_url(line)
        if auth_url and auth_url != seen_url:
            seen_url = auth_url
            private_status("waiting_for_mobile_unity_signin", url=auth_url,
                           detail="Open this Unity-owned URL from the protected Cloudflare session; approve the Unity sign-in.")
        # Deliberately never print raw CLI progress: OAuth URLs are sensitive.
    if process.poll() is None:
        process.terminate()
        try: process.wait(timeout=5)
        except subprocess.TimeoutExpired: process.kill()
        raise RuntimeError("Unity sign-in was not completed in this session")
    if process.wait() != 0:
        raise RuntimeError("Unity's approved sign-in flow did not complete")

def activate_personal(binary: str) -> None:
    private_status("activating_unity_personal")
    environment = {k: v for k, v in os.environ.items()
                   if k not in ("UNITY_SERVICE_ACCOUNT_ID",
                                "UNITY_SERVICE_ACCOUNT_SECRET", "UNITY_PASSWORD")}
    result = subprocess.run([binary, "license", "activate", "--personal",
                             "--accept-eula", "--non-interactive"],
                            env=environment, stdout=subprocess.DEVNULL,
                            stderr=subprocess.DEVNULL, timeout=180)
    if result.returncode != 0:
        raise RuntimeError("Unity CLI Personal activation was refused; Unity Hub may be required for this account")
    private_status("unity_personal_activation_succeeded",
                   detail="Unity CLI reported success; Editor still must validate licensing.")

def find_editor() -> str:
    for binary in CANDIDATES:
        if Path(binary).is_file() and os.access(binary, os.X_OK):
            return binary
    found = shutil.which("unity-editor")
    if found:
        return found
    raise FileNotFoundError("No Unity Editor binary found in the chosen GameCI container")

def run_editor() -> int:
    if (PROJECT/"Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity").is_file():
        content = (PROJECT/"Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity").read_bytes()[:90]
        if b"git-lfs.github.com/spec" in content:
            raise RuntimeError("Native Unity scene is an LFS pointer; assets must be hydrated")
    editor = find_editor()
    # First camera intent ensures the Aaris editor-only bridge can automatically
    # frame the saved world once Unity finishes importing, without a user click.
    first = STATE_DIR / "camera.json"
    if not first.exists():
        first.write_text(json.dumps({
            "x": -420, "y": 22, "z": -210,
            "yaw": 0, "pitch": 20, "distance": 22, "nonce": 1
        }))
    private_status("editor_starting",
                   detail="Editor is importing the 2km saved map; wait for the first true PNG to verify rendering.")
    logfile = PROJECT/"ci-logs"/"live-unity.log"
    logfile.parent.mkdir(parents=True, exist_ok=True)
    # GUI mode: no -batchmode or -nographics; graphics via Xvfb/Mesa.
    cmd = [editor, "-projectPath", str(PROJECT),
           "-logFile", str(logfile), "-force-glcore"]
    # A running container does NOT prove a running Editor. Save process lifecycle
    # evidence separately, without publishing raw command lines or private logs.
    runtime = STATE_DIR / "editor-runtime.json"
    proc = subprocess.Popen(cmd)
    runtime.write_text(json.dumps({"started": True, "finished": False,
                                   "exit_code": None}))
    try:
        code = proc.wait()
    finally:
        code = proc.returncode
        runtime.write_text(json.dumps({"started": True, "finished": True,
                                       "exit_code": code}))
        private_status("editor_exited",
                       detail="Unity Editor process exited; consult sanitized runtime diagnostics.")
    return code

def selftest() -> None:
    assert allowed_auth_url("https://id.unity.com/sign-in")
    assert allowed_auth_url("https://accounts.unity3d.com/oauth?code=abc")
    assert not allowed_auth_url("https://unity.com.attacker.test/?secret=foo")
    assert not allowed_auth_url("http://id.unity.com/login")
    assert not allowed_auth_url("https://other.example/login")
    assert extract_auth_url('{"type":"progress","message":"auth_url=https://id.unity.com/connect?x=123"}')
    assert extract_auth_url("auth_url=https://id.unity.com/start") == "https://id.unity.com/start"
    assert extract_auth_url("https://example.com/bad") is None
    # Regression: private status must remain owner-readable and valid JSON.
    import tempfile
    global STATE_DIR
    prev = STATE_DIR
    try:
        with tempfile.TemporaryDirectory() as tmp:
            STATE_DIR = Path(tmp)
            private_status("test_ready", detail="Private status working")
            value = json.loads((STATE_DIR / "license-status.json").read_text())
            assert value["state"] == "test_ready"
            assert (STATE_DIR / "license-status.json").stat().st_mode & 0o777 == 0o600
    finally:
        STATE_DIR = prev
    print("Authorized Unity OAuth URL parsing, private status and no-credential policy: PASS")

def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    args = parser.parse_args()
    if args.selftest:
        selftest()
        return 0
    try:
        if write_provided_license():
            print("Unity authorized ULF candidate found (content hidden).", flush=True)
        else:
            print("No ULF secret; trying official Unity CLI user authentication.", flush=True)
            binary = install_official_cli()
            login_via_authorized_browser(binary)
            activate_personal(binary)
        print("Starting real Unity Editor after authorized license flow.", flush=True)
        return run_editor()
    except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as error:
        msg = str(error)
        try:
            previous = json.loads((STATE_DIR / "license-status.json").read_text()).get("state")
        except (OSError, ValueError):
            previous = ""
        stage = "editor_launch_failed" if previous == "editor_starting" else "unity_activation_requires_attention"
        private_status(stage, detail=msg)
        print("Unity bootstrap failure (no tokens or login URLs):", type(error).__name__, flush=True)
        # Keep Docker alive so protected VNC and local status remain available.
        time.sleep(5)
        return 1

if __name__ == "__main__":
    raise SystemExit(main())
