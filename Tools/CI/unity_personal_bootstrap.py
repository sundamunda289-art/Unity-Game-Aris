#!/usr/bin/env python3
"""Authorized Unity 6 Personal bootstrap for a disposable *same-container* CI host.

Supported paths:
  1. Re-install an already authorized UNITY_LICENSE ULF from GitHub Secrets and
     let the real Unity Editor validate it on this disposable runner.
  2. Without a ULF, launch the *official Unity Hub GUI* on the cloud display
     (visible to the owner via PIN-protected Cloudflare/noVNC), then wait for
     Unity Hub to issue a Personal license. Start the Editor on success.

Personal licensing requires Unity Hub according to Unity 6000.3 docs; Unity
email/password secrets alone do not constitute automatic Personal activation.
Never cache Hub auth, sign-in tokens or license files in public artifacts.
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
import xml.etree.ElementTree as ET
from license_reuse import check_license

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
    """Unattended reinstall of an already legitimately activated Unity license.

    This does NOT generate or reissue a Personal seat; Unity must accept the
    ULF on this runner. Never expose license bytes to logs or public artifacts.
    """
    secret = os.getenv("UNITY_LICENSE", "")
    if not secret.strip():
        return False
    try:
        raw = check_license(secret.strip().encode("utf-8"))
    except (UnicodeError, ValueError) as exc:
        raise ValueError("UNITY_LICENSE is not a complete valid ULF XML secret") from exc
    LICENSE_FILE.parent.mkdir(parents=True, exist_ok=True)
    temp = LICENSE_FILE.with_name(".Unity_lic.ulf.tmp")
    try:
        with temp.open("wb") as handle:
            os.chmod(temp, 0o600)
            handle.write(raw)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temp, LICENSE_FILE)
    finally:
        temp.unlink(missing_ok=True)
    private_status("license_secret_installed",
                   detail="Authorized ULF candidate securely installed from GitHub Secrets; the Editor must confirm that this runner is licensed.")
    return True

def safe_environment() -> dict[str, str]:
    """Subprocesses get no raw GitHub license or password secrets."""
    return {name: value for name, value in os.environ.items()
            if name not in ("UNITY_LICENSE", "UNITY_PASSWORD",
                            "UNITY_SERVICE_ACCOUNT_SECRET",
                            "UNITY_SERVICE_ACCOUNT_ID")}


def installed_hub() -> str | None:
    for binary in (shutil.which("unityhub"), "/opt/unityhub/unityhub-bin"):
        if binary and Path(binary).is_file() and os.access(binary, os.X_OK):
            return binary
    return None


def install_official_hub() -> str:
    """Install a verified package from Unity's official Ubuntu APT repository."""
    binary = installed_hub()
    if binary:
        return binary
    private_status("installing_official_unity_hub",
                   detail="Installing official Unity Hub on disposable runner (not uploading any credentials).")
    if os.geteuid() != 0:
        raise RuntimeError("Unity Hub package installation requires container root privileges")
    # Minimal GameCI Ubuntu Editor images can omit GPG. Install its signing
    # toolchain from the image's preconfigured signed Ubuntu APT sources,
    # BEFORE adding the Unity Hub repository; never bypass APT signatures.
    apt_env = dict(safe_environment(), DEBIAN_FRONTEND="noninteractive")
    apt_common = ["-o", "Acquire::Retries=1",
                  "-o", "Acquire::http::Timeout=15",
                  "-o", "Acquire::https::Timeout=15"]
    if not shutil.which("gpg"):
        private_status("installing_unity_hub_prerequisites",
                       detail="Installing GPG from authenticated Ubuntu packages before Unity Hub.")
        subprocess.run(["apt-get", *apt_common, "update", "-qq"],
                       timeout=120, check=True, env=apt_env,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        subprocess.run(["apt-get", *apt_common, "install", "-y", "-qq",
                        "--no-install-recommends", "gnupg", "ca-certificates"],
                       timeout=180, check=True, env=apt_env,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    if not shutil.which("gpg"):
        raise RuntimeError("GPG missing after authenticated Ubuntu package installation; cannot verify Unity Hub")
    key_url = "https://hub.unity3d.com/linux/keys/public"
    key = subprocess.run(
        ["curl", "--proto", "=https", "-fsSL", "--retry", "2",
         "--max-time", "50", key_url],
        capture_output=True, timeout=70, check=True).stdout
    if not 200 <= len(key) <= 100000:
        raise RuntimeError("Official Unity APT signing key unavailable")
    key_dir = Path("/etc/apt/keyrings")
    key_dir.mkdir(parents=True, exist_ok=True)
    signing_key = key_dir / "unityhub.gpg"
    with signing_key.open("wb") as output:
        subprocess.run(["gpg", "--dearmor"], input=key, stdout=output,
                       stderr=subprocess.DEVNULL, timeout=25, check=True)
    signing_key.chmod(0o644)
    source = Path("/etc/apt/sources.list.d/unityhub.sources")
    source.write_text("Types: deb\n"
                      "URIs: https://hub.unity3d.com/linux/repos/deb\n"
                      "Suites: stable\nComponents: main\n"
                      "Signed-By: /etc/apt/keyrings/unityhub.gpg\n")
    subprocess.run(["apt-get", *apt_common, "update", "-qq"],
                   timeout=125, check=True, env=apt_env,
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    subprocess.run(["apt-get", *apt_common, "install", "-y", "-qq",
                    "--no-install-recommends", "unityhub", "wmctrl",
                    "dbus", "xdg-utils", "libgbm1"],
                   timeout=240, check=True, env=apt_env,
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    binary = installed_hub()
    if not binary:
        raise RuntimeError("Official Unity Hub installed but no GUI binary was found")
    return binary


def hub_license_present() -> bool:
    """Read-only detection of official Linux license-file locations.

    Unity 6 distinguishes legacy serial/Personal ULF and named-user XML.
    XML format alone is not proof of entitlement: the Editor must validate.
    Never publish content, cache the token, or spoof a machine identity.
    """
    candidates = (
        LICENSE_FILE,
        Path.home() / ".config/unity3d/Unity/licenses/UnityEntitlementLicense.xml",
    )
    for candidate in candidates:
        try:
            raw = candidate.read_bytes()
            if candidate.suffix.lower() == ".ulf":
                check_license(raw)
            else:
                if not 200 <= len(raw) <= 200000 or b"\0" in raw:
                    continue
                doc = ET.fromstring(raw)
                if "license" not in doc.tag.lower():
                    continue
            return True
        except (OSError, ValueError, ET.ParseError):
            # Never print malformed or private license contents.
            continue
    return False


def hub_window_visible() -> bool | None:
    """Check visible EWMH window titles; never expose or log window text.

    None means no reliable GUI probe. A running Electron process alone does
    not mean it actually mapped a window to the owner's Xvfb display.
    """
    wmctrl = shutil.which("wmctrl")
    if not wmctrl:
        return None
    try:
        result = subprocess.run(
            [wmctrl, "-lx"], env=safe_environment(),
            capture_output=True, text=True, timeout=6, check=False)
        if result.returncode != 0:
            return None
        return any("unityhub" in line.lower() or "unity hub" in line.lower()
                   for line in result.stdout.splitlines())
    except (OSError, subprocess.TimeoutExpired):
        return None


def hub_personal_activation() -> None:
    """Owner signs in inside Hub GUI through protected noVNC; no password scripting."""
    binary = install_official_hub()
    if not os.environ.get("DISPLAY"):
        raise RuntimeError("DISPLAY missing; Unity Hub GUI cannot open in noVNC desktop")
    private_status("waiting_for_unity_hub_signin",
                   detail="Open the PIN-protected live desktop, sign in to official Unity Hub and activate Personal. No PC required.")
    # Electron GUI in a root-owned, headless container requires a session
    # D-Bus and software rendering on the host Xvfb. Do NOT use --headless.
    # Never forward a Unity password, ULF or auth token to this process.
    args = [binary, "--disable-gpu", "--disable-dev-shm-usage"]
    if os.geteuid() == 0:
        args.append("--no-sandbox")
    dbus_run = shutil.which("dbus-run-session")
    if dbus_run:
        args = [dbus_run, "--", *args]
    private_status("starting_unity_hub_gui",
                   detail="Starting official Unity Hub with an X11 GUI session (software rendering).")
    process = subprocess.Popen(args, env=safe_environment(),
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    start = time.monotonic()
    deadline = start + 1800
    window_checked = False
    while time.monotonic() < deadline:
        if hub_license_present():
            private_status("unity_hub_license_detected",
                           detail="Hub created an authorized license candidate; opening Editor to verify it.")
            return
        if process.poll() is not None:
            raise RuntimeError("Unity Hub GUI exited before issuing a Personal license")
        visible = hub_window_visible()
        if visible:
            if not window_checked:
                # An Xvfb screen can appear blank if another window is focused.
                # Focus only the official Hub window; no credentials or clicks.
                wmctrl = shutil.which("wmctrl")
                if wmctrl:
                    subprocess.run([wmctrl, "-a", "Unity Hub"],
                                   env=safe_environment(), stdout=subprocess.DEVNULL,
                                   stderr=subprocess.DEVNULL, timeout=5, check=False)
                private_status("waiting_for_unity_hub_signin",
                               detail="Unity Hub window detected on virtual desktop. Open noVNC and sign in.")
                window_checked = True
        elif visible is False and time.monotonic() - start > 75:
            private_status("unity_hub_window_not_visible",
                           detail="Unity Hub process is running but no Unity Hub window was detected on the Xvfb desktop. Browser login cannot proceed.")
            raise RuntimeError("Unity Hub GUI did not display a window in cloud Xvfb; check window manager and GUI runtime compatibility")
        elif visible is None and not window_checked and time.monotonic() - start > 15:
            private_status("unity_hub_window_check_unavailable",
                           detail="Cannot verify Unity Hub GUI window; check the PIN-protected noVNC desktop.")
            window_checked = True
        time.sleep(3)
    raise RuntimeError("Unity Hub sign-in or Personal activation was not completed in this session")


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
    # The Unity Editor needs the securely installed file, not the original
    # ULF secret in its environment. Never forward a password to the GUI.
    code = subprocess.call(cmd, env=safe_environment())
    private_status("editor_process_ended",
                   detail="Unity Editor process finished; inspect protected status and the scene capture results.")
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
    # A GitHub Secret can seed a fresh disposable runner without another login.
    # Test only dummy ULF data; never use real credentials in unit tests.
    import tempfile
    global LICENSE_FILE, STATE_DIR
    previous_license, previous_state = LICENSE_FILE, STATE_DIR
    previous_secret = os.environ.get("UNITY_LICENSE")
    try:
        with tempfile.TemporaryDirectory() as folder:
            STATE_DIR = Path(folder)
            LICENSE_FILE = STATE_DIR / "private" / "Unity_lic.ulf"
            os.environ["UNITY_LICENSE"] = (
                '<?xml version="1.0"?><License><Filler>' +
                "TEST-NOT-A-REAL-LICENSE-" * 12 + "</Filler></License>"
            )
            assert write_provided_license()
            assert LICENSE_FILE.exists()
            assert (LICENSE_FILE.stat().st_mode & 0o777) == 0o600
            assert json.loads((STATE_DIR / "license-status.json").read_text())["state"] == "license_secret_installed"
            assert not (STATE_DIR / "license-status.json").read_text().find("TEST-NOT-A-REAL-LICENSE") >= 0
            os.environ["UNITY_LICENSE"] = "not-an-ulf"
            try:
                write_provided_license()
            except ValueError:
                pass
            else:
                raise AssertionError("Invalid Unity ULF secret was accepted")
    finally:
        LICENSE_FILE, STATE_DIR = previous_license, previous_state
        if previous_secret is None:
            os.environ.pop("UNITY_LICENSE", None)
        else:
            os.environ["UNITY_LICENSE"] = previous_secret
    # Missing ULF must not be treated as a completed Personal activation.
    with tempfile.TemporaryDirectory() as no_license:
        old_license = LICENSE_FILE
        try:
            LICENSE_FILE = Path(no_license) / "missing.ulf"
            assert not hub_license_present()
            LICENSE_FILE.write_text("not an authorized license")
            assert not hub_license_present()
        finally:
            LICENSE_FILE = old_license
    # A missing window inspection tool is not proof that Hub is visible.
    import unittest.mock as mock
    with mock.patch("shutil.which", return_value=None):
        assert hub_window_visible() is None
    with mock.patch("shutil.which", return_value="/usr/bin/wmctrl"):
        with mock.patch("subprocess.run") as test_run:
            test_run.return_value = subprocess.CompletedProcess(
                ["wmctrl", "-lx"], 0,
                "0x04  0 runner UnityHub.unityhub runner Unity Hub\\n", "")
            assert hub_window_visible() is True
            test_run.return_value = subprocess.CompletedProcess(
                ["wmctrl", "-lx"], 0, "0x01  0 runner Openbox.openbox runner\\n", "")
            assert hub_window_visible() is False
    # A subprocess must not inherit any GitHub license or Unity password.
    assert "UNITY_LICENSE" not in safe_environment()
    assert "UNITY_PASSWORD" not in safe_environment()
    # Regression: private status must remain owner-readable and valid JSON.
    import tempfile
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
            print("Unity GitHub Secret ULF candidate installed without another interactive sign-in (content hidden).", flush=True)
        else:
            print("No ULF secret: launching official Unity Hub GUI on PIN-protected remote desktop.", flush=True)
            hub_personal_activation()
        print("Starting real Unity Editor after authorized license flow.", flush=True)
        return run_editor()
    except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as error:
        msg = str(error)
        private_status("unity_activation_requires_attention", detail=msg)
        print("Unity license bootstrap incomplete:", msg, flush=True)
        # Keep Docker alive so protected VNC and local status remain available.
        time.sleep(5)
        return 1

if __name__ == "__main__":
    raise SystemExit(main())
