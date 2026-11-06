#!/usr/bin/env python3
"""Safely capture an already authorized Unity Personal ULF from active Editor container.

- Export it ONLY to the local PIN-protected control server for one-time phone
  download, and let the owner save it as a GitHub repository Secret via Termux.
- Never publish the ULF in Issue comments, Git logs, Releases, Actions
  artifacts, cache, or Cloudflare KV.
- This cannot make a machine-bound license portable or generate an entitlement.
"""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import subprocess
import time
import xml.etree.ElementTree as ET

REPO = "sundamunda289-art/Unity-Game-Aris"
STATE = Path(__file__).resolve().parents[2] / "ci-live-control"
ULF = STATE / "Unity_lic.ulf"
POSSIBLE_LICENSE_PATHS = (
    "/root/.local/share/unity3d/Unity/Unity_lic.ulf",
    "/root/.local/share/unity3d/Unity/Unity_lic.ULF",
    "/root/.local/share/unity3d/Unity/Unity_lic.ulf",
)

def check_license(data: bytes) -> bytes:
    if not 200 <= len(data) <= 180000:
        raise ValueError("No complete Unity ULF license was produced")
    if b"\0" in data:
        raise ValueError("Unity license is not an XML text file")
    if not data.lstrip().startswith((b"<?xml", b"<")):
        raise ValueError("Unity license does not begin with XML")
    try:
        parsed = ET.fromstring(data)
    except ET.ParseError as err:
        raise ValueError("Unity ULF is not parseable XML") from err
    if "license" not in parsed.tag.lower() and "root" not in parsed.tag.lower():
        raise ValueError("Unexpected Unity license XML structure")
    return data

def capture_from_container() -> bytes | None:
    running = subprocess.run(
        ["docker", "inspect", "--format", "{{.State.Running}}", "aaris-unity-live"],
        capture_output=True, timeout=8, check=False, text=True)
    if running.returncode or running.stdout.strip() != "true":
        return None
    for path in POSSIBLE_LICENSE_PATHS:
        command = subprocess.run(
            ["docker", "exec", "aaris-unity-live", "cat", path],
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            timeout=8, check=False)
        if command.returncode != 0:
            continue
        try:
            return check_license(command.stdout)
        except ValueError:
            continue
    return None

def safe_status(state: str) -> None:
    STATE.mkdir(parents=True, exist_ok=True)
    target = STATE / "license-export-status.json"
    temp = STATE / "license-export-status.tmp"
    temp.write_text(json.dumps({"state": state}) + "\n")
    os.chmod(temp, 0o600)
    os.replace(temp, target)

def install_export(license_bytes: bytes) -> None:
    STATE.mkdir(parents=True, exist_ok=True)
    temporary = STATE / "Unity_lic.tmp"
    temporary.write_bytes(license_bytes)
    os.chmod(temporary, 0o600)
    os.replace(temporary, ULF)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--watch-seconds", type=int, default=850)
    args = parser.parse_args()
    if args.selftest:
        fake = b'<?xml version="1.0"?><License>' + b"<Filler>" + b"A"*250 + b"</Filler></License>"
        assert check_license(fake) == fake
        for bad in (b"", b"not xml"*90, b"<html>" + b"X"*350 + b"</html>"):
            try:
                check_license(bad)
            except ValueError:
                pass
            else:
                raise AssertionError("Invalid ULF passed")
        print("Unity ULF XML validation / protected export selftest PASS")
        return
    safe_status("waiting_for_valid_unity_ulf")
    deadline = time.monotonic() + min(max(args.watch_seconds, 20), 18600)
    while time.monotonic() < deadline:
        data = capture_from_container()
        if data is not None:
            install_export(data)
            safe_status("download_ready")
            # Do not log anything about license content or token.
            print("A licensed Unity ULF exists; protected one-time export ready.", flush=True)
            return
        time.sleep(5)
    safe_status("no_reusable_ulf_found")
    print("No valid ULF found in the running container. No secret was exported.", flush=True)

if __name__ == "__main__":
    main()
