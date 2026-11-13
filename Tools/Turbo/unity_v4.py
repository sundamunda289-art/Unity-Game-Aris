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
import subprocess
import time
import uuid

ALLOWED = {"status", "diag_render", "camera", "focus", "play", "stop", "pause", "resume",
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



def wait_for_play_state(folder, target, ready_timeout):
    """After play/stop acknowledgement, wait for a *fresh* Editor response.

    Play/stop may force a domain reload. Never resend a non-idempotent command
    after an uncertain acknowledgement, and never remove another agent's inbox.
    """
    deadline = time.monotonic() + ready_timeout
    while time.monotonic() < deadline:
        remaining = deadline - time.monotonic()
        if remaining < 0.5:
            break
        reply = send({"id": "v4_" + uuid.uuid4().hex[:24], "op": "status"},
                     folder, min(remaining, 115))
        if reply.get("result") != "ok":
            raise RuntimeError("Unity rejected ready check: " + str(reply.get("detail", "")))
        if (reply.get("playing") is target and
                not reply.get("compiling") and not reply.get("updating")):
            return reply
        time.sleep(min(0.6, max(0, deadline - time.monotonic())))
    raise TimeoutError("Unity play/stop acknowledged but fresh ready state not proven")


def capture_live_screen(op, settle, full):
    """Capture the *real* X11 screen via the installed private Turbo GUI helper.

    For scene navigation the native Unity Scene window is raised and sized,
    rather than leaving a tiny popup over the Game view. No scene is saved,
    gameplay objects are not teleported, and no public screenshot is uploaded.
    """
    env = os.environ.copy()
    unity_pids = []
    for entry in Path("/proc").glob("[0-9]*/cmdline"):
        try:
            cmd = entry.read_bytes()
            if b"/Editor/Unity" in cmd and b"-projectPath" in cmd:
                unity_pids.append(int(entry.parent.name))
                for part in (Path("/proc") / entry.parent.name / "environ").read_bytes().split(b"\0"):
                    if part.startswith(b"DISPLAY="):
                        env["DISPLAY"] = part[8:].decode("utf-8")
                        break
        except (OSError, ValueError, UnicodeError):
            pass
    if not unity_pids:
        raise RuntimeError("Unity Editor is not running; cannot verify screenshot")
    if "DISPLAY" not in env:
        raise RuntimeError("Unity X11 DISPLAY is unavailable")
    if op in ("camera", "focus"):
        found = subprocess.run(["xdotool", "search", "--onlyvisible", "--name", "^Scene$"],
                               env=env, text=True, capture_output=True, timeout=5)
        windows = found.stdout.split()
        candidates = []
        for wid in windows:
            pid = subprocess.run(["xdotool", "getwindowpid", wid],
                                 env=env, text=True, capture_output=True, timeout=5)
            if pid.stdout.strip().isdigit() and int(pid.stdout.strip()) in unity_pids:
                candidates.append(wid)
        if not candidates:
            raise RuntimeError("Unity Scene window not visible; refusing a misleading Game screenshot")
        selected = candidates[-1]
        size = subprocess.run(["xdotool", "getdisplaygeometry"], env=env,
                              text=True, capture_output=True, check=True, timeout=5)
        width, height = map(int, size.stdout.split())
        w, h = max(480, width - 40), max(360, height - 96)
        subprocess.run(["xdotool", "windowsize", "--sync", selected, str(w), str(h)],
                       env=env, check=True, timeout=8)
        subprocess.run(["xdotool", "windowmove", selected, "15", "38"],
                       env=env, check=True, timeout=8)
        subprocess.run(["xdotool", "windowraise", selected], env=env, check=True, timeout=8)
    time.sleep(settle)
    gui = Path.home() / ".local/share/aaris-turbo/gui.py"
    if not gui.is_file():
        raise RuntimeError("Private Turbo GUI capture helper is not installed")
    args = [sys.executable, str(gui), "look"]
    if not full:
        args.append("--fast")
    result = subprocess.run(args, env=env, text=True, capture_output=True, timeout=18)
    if result.returncode != 0 or "AARIS_GUI_FRAME_COMPLETE" not in result.stdout:
        raise RuntimeError("fresh X11 screenshot failed: " + result.stderr[:160])
    print("AARIS_V4_FRESH_SCREEN_BEGIN", flush=True)
    print(result.stdout, end="", flush=True)
    print("AARIS_V4_FRESH_SCREEN_END", flush=True)


def capture_native_photo(target, folder, timeout, full):
    """Read-only object-framed photo from the existing Unity Editor world bridge.

    The bridge owns the real Unity Camera.Render output; do not create a
    parallel scene, copy game assets, or use a screenshot from an old nonce.
    """
    import base64
    import hashlib
    import io
    from PIL import Image

    folder = Path(folder)
    inbox, outbox = folder / "ai-command.json", folder / "ai-result.json"
    nonce = time.time_ns() // 1000
    payload = json.dumps({"op": "photo", "target": target, "nonce": nonce},
                         separators=(",", ":"))
    try:
        fd = os.open(inbox, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    except FileExistsError as ex:
        raise RuntimeError("native Unity photo bridge busy; do not overwrite agent work") from ex
    with os.fdopen(fd, "w") as stream:
        stream.write(payload)
    started_ns = time.time_ns()
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            reply = json.loads(outbox.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            time.sleep(0.2)
            continue
        if reply.get("nonce") != nonce:
            time.sleep(0.2)
            continue
        if not reply.get("ok") or reply.get("op") != "photo" or reply.get("target") != target:
            raise RuntimeError("Unity native photo rejected: " + str(reply.get("message", "")))
        if reply.get("photo") != "ai-photo.png":
            raise RuntimeError("native photo returned an unexpected path")
        path = folder / "ai-photo.png"
        if not path.is_file() or path.stat().st_mtime_ns + 2_000_000_000 < started_ns:
            raise RuntimeError("native photo is stale or missing")
        with Image.open(path) as im:
            if im.size != (1280, 720):
                raise RuntimeError("unexpected native photo dimensions")
            image = im.convert("RGB")
        # Fail closed on Unity's saturated magenta error-shader fallback.
        # A single decorative pink pixel is not an error; large coverage is.
        sample = image.resize((160, 90))
        magenta_count = sum(
            1 for red, green, blue in sample.getdata()
            if red > 190 and blue > 180 and green < 110
            and red > green * 1.6 and blue > green * 1.6
        )
        magenta_fraction = round(magenta_count / (160 * 90), 6)
        if magenta_fraction > 0.04:
            raise RuntimeError(
                "Unity magenta error shader visible: "
                + str(magenta_fraction * 100) + "% of frame"
            )
        if not full:
            image.thumbnail((900, 506))
        buf = io.BytesIO()
        image.save(buf, format="JPEG", quality=72, optimize=True)
        raw = buf.getvalue()
        meta = {"nonce": nonce, "target": target, "scene": reply.get("scene"),
                "position": reply.get("position"), "size": image.size,
                "sha256": hashlib.sha256(raw).hexdigest(),
                "native_png_bytes": path.stat().st_size,
                "magentaFraction": magenta_fraction}
        print("AARIS_NATIVE_PHOTO_META_JSON=" + json.dumps(meta), flush=True)
        print("AARIS_NATIVE_PHOTO_BASE64=" + base64.b64encode(raw).decode(), flush=True)
        print("AARIS_NATIVE_PHOTO_COMPLETE", flush=True)
        return meta
    # Never remove the pending request on timeout: it might be executing.
    raise TimeoutError("native photo request not acknowledged; left pending safely")


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
    p.add_argument("--wait-ready", action="store_true",
                   help="verify fresh Unity state after play/stop domain reload")
    p.add_argument("--ready-timeout", type=float, default=110)
    p.add_argument("--photo", action="store_true",
                   help="frame named object using native Unity Camera.Render (Edit Mode)")
    p.add_argument("--capture", action="store_true",
                   help="return actual private X11 screenshot after operation")
    p.add_argument("--full", action="store_true", help="full resolution for --capture or --photo")
    p.add_argument("--settle", type=float, default=0.5,
                   help="Scene repaint delay before capturing")
    args = p.parse_args()
    if args.selftest:
        selftest()
        return 0
    if not args.op:
        p.error("operation required")
    if not 0 < args.timeout <= 120:
        p.error("--timeout must be 1..120 seconds")
    if args.wait_ready and args.op not in ("play", "stop"):
        p.error("--wait-ready requires play or stop")
    if not 1 <= args.ready_timeout <= 115:
        p.error("--ready-timeout must be within 1..115")
    if not 0.1 <= args.settle <= 3:
        p.error("--settle must be within 0.1..3")
    if args.full and not (args.capture or args.photo):
        p.error("--full requires --capture or --photo")
    if args.photo and (args.op != "focus" or args.capture):
        p.error("--photo requires focus and cannot be combined with --capture")
    data = {"id": "v4_" + uuid.uuid4().hex[:24], "op": args.op}
    if args.op in {"focus", "action", "tap_ui", "hold_ui"}:
        data["target"] = args.target
    if (args.op == "action" and args.target in {"Fire", "Jump"}) or args.op == "hold_ui":
        data["seconds"] = args.seconds
    if args.op == "camera":
        for name in ("x", "y", "z", "yaw", "pitch"):
            data[name] = getattr(args, name)
    answer = send(data, args.folder, args.timeout)
    print(json.dumps(answer, ensure_ascii=False), flush=True)
    if answer.get("result") != "ok":
        return 2
    if args.wait_ready:
        verified = wait_for_play_state(args.folder, args.op == "play", args.ready_timeout)
        print("AARIS_V4_READY_JSON=" + json.dumps(verified, ensure_ascii=False), flush=True)
    if args.capture:
        capture_live_screen(args.op, args.settle, args.full)
    if args.photo:
        if answer.get("playing") or answer.get("scene") != "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity":
            raise RuntimeError("native object photo requires the authorized scene in Edit Mode")
        capture_native_photo(args.target, args.folder, min(60, args.ready_timeout), args.full)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (ValueError, RuntimeError, TimeoutError, OSError, subprocess.SubprocessError) as exc:
        print("AARIS_V4_CLI_ERROR:" + str(exc), file=sys.stderr)
        sys.exit(2)
