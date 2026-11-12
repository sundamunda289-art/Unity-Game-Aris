#!/usr/bin/env python3
"""Send allowlisted intents to the already-running Unity Editor V4 bridge.

Never launches a second Editor, compiles scripts, changes scenes automatically,
opens a TCP port, or overwrites an existing request from another agent.
"""
from __future__ import annotations
import argparse
import json
import math
import os
from pathlib import Path
import re
import sys
import time
import uuid

ALLOWED = {"status", "camera", "focus", "play", "stop", "pause", "resume",
           "step", "release", "action", "uimap", "tap_ui", "hold_ui"}
ACTIONS = {"Fire", "Jump", "Crouch", "Prone", "Sprint", "Pickup", "Scope",
           "Reload", "Drop"}
UI_TARGETS = {"HUD Fire", "HUD Jump", "HUD Crouch", "HUD Prone", "HUD Sprint",
              "ADS", "PICK", "DROP", "RELOAD"}
RE_ID = re.compile(r"^[A-Za-z0-9_-]{1,64}$")
FOLDER = Path("ci-live-control")


def validate(command):
    if not isinstance(command, dict):
        raise ValueError("intent must be an object")
    op = command.get("op")
    if op not in ALLOWED:
        raise ValueError("unknown operation")
    expected = {"id", "op"}
    # An unresponsive Editor must not execute old Fire/Jump/Play intents
    # after they have timed out at the terminal.
    if "expiresUnixMs" in command:
        expected.add("expiresUnixMs")
        expiry = command["expiresUnixMs"]
        if type(expiry) is not int or expiry <= 0:
            raise ValueError("invalid expiry timestamp")
    if not isinstance(command.get("id"), str) or not RE_ID.fullmatch(command["id"]):
        raise ValueError("invalid id")
    if op == "focus":
        expected.add("target")
        if not isinstance(command.get("target"), str) or not 1 <= len(command["target"]) <= 180:
            raise ValueError("invalid focus target")
    if op in ("tap_ui", "hold_ui"):
        expected.add("target")
        if command.get("target") not in UI_TARGETS:
            raise ValueError("unknown UI target")
        if op == "hold_ui":
            expected.add("seconds")
            number = command.get("seconds")
            if type(number) not in (int, float) or not math.isfinite(number) or not .05 <= number <= 3:
                raise ValueError("invalid UI hold duration")
    if op == "action":
        expected.add("target")
        if command.get("target") not in ACTIONS:
            raise ValueError("invalid gameplay target")
        if command["target"] in ("Fire", "Jump"):
            expected.add("seconds")
            number = command.get("seconds")
            if type(number) not in (int, float) or not math.isfinite(number) or not .05 <= number <= 3:
                raise ValueError("invalid action duration")
    if op == "camera":
        expected.update(("x", "y", "z", "yaw", "pitch"))
        limits = {"x": (-2000, 2000), "y": (-500, 4000), "z": (-2000, 2000),
                  "yaw": (-360, 360), "pitch": (-85, 85)}
        for key, (low, high) in limits.items():
            value = command.get(key)
            if type(value) not in (int, float) or not math.isfinite(value) or not low <= value <= high:
                raise ValueError("camera field invalid: " + key)
    if set(command) != expected:
        raise ValueError("unexpected fields")
    return command


def send(command, folder, timeout):
    command = dict(validate(command))
    if "expiresUnixMs" not in command:
        # Clock-time deadline enforced inside Unity before executing the action.
        command["expiresUnixMs"] = int((time.time() + min(timeout, 120)) * 1000)
    validate(command)
    folder = Path(folder)
    if not folder.is_dir():
        raise RuntimeError("Unity bridge folder is missing; run inside the Unity project root")
    request = folder / "ai_v4_request.json"
    response = folder / "ai_v4_result.json"
    temp = folder / ("." + command["id"] + ".tmp")
    temp.write_text(json.dumps(command, separators=(",", ":")), encoding="utf-8")
    try:
        # Atomically fail rather than overwrite another agent's pending command.
        os.link(temp, request)
    except FileExistsError as exc:
        raise RuntimeError("Unity bridge busy with an existing request") from exc
    finally:
        temp.unlink(missing_ok=True)
    start = time.monotonic()
    while time.monotonic() - start < timeout:
        try:
            result = json.loads(response.read_text(encoding="utf-8"))
            if result.get("id") == command["id"]:
                result["latency_ms"] = round((time.monotonic() - start) * 1000, 2)
                return result
        except (FileNotFoundError, json.JSONDecodeError):
            pass
        time.sleep(.12)
    # The pending request belongs to Unity; never delete it on timeout.
    raise TimeoutError("Unity did not acknowledge request before timeout; check import/compile/editor status")


def selftest():
    assert validate({"id": "abc", "op": "status"})
    assert validate({"id": "abc", "op": "camera",
                     "x": 450, "y": 20, "z": 800, "yaw": 10, "pitch": 15})
    assert validate({"id": "abc", "op": "action", "target": "Fire", "seconds": 1.5})
    assert validate({"id": "abc", "op": "tap_ui", "target": "PICK"})
    assert validate({"id": "abc", "op": "hold_ui", "target": "HUD Fire", "seconds": 1})
    assert validate({"id": "abc", "op": "status", "expiresUnixMs": 1700000000000})
    for data in [
        {"id": "abc", "op": "eval", "target": "rm -rf"},
        {"id": "abc", "op": "camera", "x": 0},
        {"id": "abc", "op": "action", "target": "Reflection.Invoke"},
        {"id": "abc", "op": "action", "target": "Fire", "seconds": 60},
        {"id": "abc", "op": "tap_ui", "target": "Settings"},
        {"id": "abc", "op": "hold_ui", "target": "PICK", "seconds": 999},
        {"id": "abc", "op": "status", "script": "anything"},
        {"id": "abc", "op": "status", "expiresUnixMs": -1},
    ]:
        try:
            validate(data)
        except ValueError:
            pass
        else:
            raise AssertionError("unsafe V4 intent accepted")
    print("AARIS_V4_UNITY_CLI_SELFTEST_PASS")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("op", nargs="?", choices=sorted(ALLOWED))
    p.add_argument("--target")
    p.add_argument("--x", type=float)
    p.add_argument("--y", type=float)
    p.add_argument("--z", type=float)
    p.add_argument("--yaw", type=float, default=0)
    p.add_argument("--pitch", type=float, default=15)
    p.add_argument("--seconds", type=float)
    p.add_argument("--folder", default=str(FOLDER))
    p.add_argument("--timeout", type=float, default=20)
    p.add_argument("--selftest", action="store_true")
    args = p.parse_args()
    if args.selftest:
        selftest()
        return 0
    if not args.op:
        p.error("operation required")
    if not 0 < args.timeout <= 120:
        p.error("--timeout must be 1..120 seconds")
    data = {"id": "v4_" + uuid.uuid4().hex[:24], "op": args.op}
    if args.op in {"focus", "action", "tap_ui", "hold_ui"}:
        data["target"] = args.target
    if (args.op == "action" and args.target in {"Fire", "Jump"}) or args.op == "hold_ui":
        data["seconds"] = args.seconds
    if args.op == "camera":
        for name in ("x", "y", "z", "yaw", "pitch"):
            data[name] = getattr(args, name)
    answer = send(data, args.folder, args.timeout)
    print(json.dumps(answer, ensure_ascii=False))
    return 0 if answer.get("result") == "ok" else 2


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (ValueError, RuntimeError, TimeoutError, OSError) as exc:
        print("AARIS_V4_CLI_ERROR:" + str(exc), file=sys.stderr)
        sys.exit(2)
