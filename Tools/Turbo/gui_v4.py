#!/usr/bin/env python3
"""Aaris V4: bounded X11 action sequences with actual-frame replay evidence.

This is an opt-in *local* helper. It does not open a network listener, alter the
Unity project, simulate a passed test or upload screenshots. The existing
authenticated Cloudflare Turbo agent invokes this file as a subprocess.
"""
from __future__ import annotations

import argparse
import base64
import fcntl
import hashlib
import io
import json
import os
from pathlib import Path
import re
import signal
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import zipfile

MAX_STEPS = 48
MAX_SECONDS = 30.0
ROOT = Path("/tmp/aaris-v4-evidence")
SAFE_ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9_-]{0,55}\Z")
SAFE_KEY = re.compile(r"[A-Za-z0-9_+:-]{1,48}\Z")
ALLOWED_OPS = {"key_down", "key_up", "key_tap", "mouse_down", "mouse_up",
               "move", "move_smooth", "click", "mouse_hold", "wait", "mark"}
LOCK_PATH = "/tmp/aaris-v4-input.lock"


def invoke(*args, timeout=8):
    return subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          check=True, timeout=timeout)


def screen_size():
    output = invoke("xdotool", "getdisplaygeometry").stdout.decode().strip().split()
    if len(output) != 2 or not all(x.isdigit() for x in output):
        raise RuntimeError("X11_DISPLAY_UNAVAILABLE")
    return int(output[0]), int(output[1])


def verify_sequence(steps, width, height):
    if not isinstance(steps, list) or not 1 <= len(steps) <= MAX_STEPS:
        raise ValueError("steps must be a bounded nonempty array")
    normalized = []
    total_delay = 0.0
    for step in steps:
        if not isinstance(step, dict) or step.get("op") not in ALLOWED_OPS:
            raise ValueError("unsupported action")
        op = step["op"]
        expected = {"op"}
        clean = {"op": op}
        if op in ("key_down", "key_up", "key_tap"):
            expected.add("key")
            if not isinstance(step.get("key"), str) or not SAFE_KEY.fullmatch(step["key"]):
                raise ValueError("unsafe key")
            clean["key"] = step["key"]
        elif op in ("mouse_down", "mouse_up"):
            expected.add("button")
            if type(step.get("button")) is not int or step["button"] not in (1, 2, 3):
                raise ValueError("invalid button")
            clean["button"] = step["button"]
        elif op in ("move", "move_smooth", "click", "mouse_hold"):
            expected.update(("x", "y"))
            for name, bound in (("x", width), ("y", height)):
                value = step.get(name)
                if type(value) is not int or not 0 <= value < bound:
                    raise ValueError("coordinate outside real desktop")
                clean[name] = value
            if op == "move_smooth":
                expected.add("seconds")
                seconds = step.get("seconds")
                if type(seconds) not in (int, float) or not 0.05 <= seconds <= 2:
                    raise ValueError("invalid smooth-move duration")
                total_delay += seconds
                clean["seconds"] = float(seconds)
            if op == "mouse_hold":
                expected.add("seconds")
                seconds = step.get("seconds")
                if type(seconds) not in (int, float) or not 0.05 <= seconds <= 20:
                    raise ValueError("invalid mouse hold duration")
                total_delay += seconds
                clean["seconds"] = float(seconds)
                button = step.get("button", 1)
                if type(button) is not int or button not in (1, 2, 3):
                    raise ValueError("invalid mouse hold button")
                if "button" in step:
                    expected.add("button")
                clean["button"] = button
        elif op == "wait":
            expected.add("seconds")
            seconds = step.get("seconds")
            if type(seconds) not in (float, int) or not 0 <= seconds <= 3:
                raise ValueError("invalid wait")
            total_delay += seconds
            clean["seconds"] = float(seconds)
        elif op == "mark":
            expected.add("label")
            label = step.get("label")
            if not isinstance(label, str) or not SAFE_ID.fullmatch(label):
                raise ValueError("unsafe marker")
            clean["label"] = label
        if set(step) != expected:
            raise ValueError("unexpected action arguments")
        normalized.append(clean)
    if total_delay > MAX_SECONDS:
        raise ValueError("total sequence too long")
    return normalized


def make_capture(path, width=640, height=360, quality=72):
    from PIL import Image
    fd, temp = tempfile.mkstemp(prefix="aaris-v4-frame-", suffix=".png", dir="/tmp")
    os.close(fd)
    try:
        invoke("scrot", "-z", "-o", temp, timeout=6)
        with Image.open(temp) as src:
            picture = src.convert("RGB")
            picture.thumbnail((width, height))
            picture.save(path, "JPEG", quality=quality, optimize=True)
        return picture.size
    finally:
        Path(temp).unlink(missing_ok=True)


class Recorder:
    def __init__(self, folder, fps, max_frames=180):
        self.folder = folder
        self.fps = fps
        self.max_frames = max_frames
        self.frames = []
        self.errors = []
        self.stop = threading.Event()
        self.started = time.monotonic()
        self.thread = threading.Thread(target=self._work, daemon=True)

    def _work(self):
        period = 1.0 / self.fps
        deadline = self.started
        while not self.stop.is_set() and len(self.frames) < self.max_frames:
            delay = deadline - time.monotonic()
            if delay > 0:
                self.stop.wait(delay)
                if self.stop.is_set():
                    break
            index = len(self.frames)
            shot = self.folder / f"frame_{index:04d}.jpg"
            started = time.monotonic()
            try:
                size = make_capture(shot)
                self.frames.append({"index": index, "file": shot.name,
                                    "at_ms": round((started - self.started) * 1000, 2),
                                    "width": size[0], "height": size[1]})
            except Exception as exc:
                self.errors.append(type(exc).__name__)
                if len(self.errors) >= 3:
                    break
            deadline += period
            # Never pretend the recorder maintained 30 FPS if capture is slow.
            if deadline < time.monotonic() - period:
                deadline = time.monotonic()

    def start(self):
        self.thread.start()

    def finish(self):
        self.stop.set()
        self.thread.join(timeout=12)
        if self.thread.is_alive():
            raise RuntimeError("recorder did not stop")


def compose_sheet(folder, frames):
    from PIL import Image, ImageOps, ImageDraw
    if not frames:
        return None
    slots = min(12, len(frames))
    indices = [round(i * (len(frames) - 1) / max(slots - 1, 1)) for i in range(slots)]
    tw, th = 320, 204
    sheet = Image.new("RGB", (tw * 3, th * 4), (24, 32, 40))
    draw = ImageDraw.Draw(sheet)
    for pos, index in enumerate(indices):
        item = frames[index]
        with Image.open(folder / item["file"]) as frame:
            pic = ImageOps.fit(frame.convert("RGB"), (tw, th - 24))
            x, y = (pos % 3) * tw, (pos // 3) * th
            sheet.paste(pic, (x, y))
        draw.text((x + 6, y + th - 20),
                  f'#{item["index"]}   {item["at_ms"]:.0f}ms', fill=(255, 255, 255))
    destination = folder / "contact_sheet.jpg"
    sheet.save(destination, "JPEG", quality=81, optimize=True)
    return destination


def smoothly_move(x, y, seconds):
    raw = invoke("xdotool", "getmouselocation", "--shell").stdout.decode()
    fields = dict(line.split("=", 1) for line in raw.splitlines() if "=" in line)
    ox, oy = int(fields["X"]), int(fields["Y"])
    segments = max(2, min(40, round(seconds * 30)))
    start = time.monotonic()
    for i in range(1, segments + 1):
        fraction = i / segments
        px = round(ox + (x - ox) * fraction)
        py = round(oy + (y - oy) * fraction)
        invoke("xdotool", "mousemove", "--sync", str(px), str(py))
        remaining = start + seconds * fraction - time.monotonic()
        if remaining > 0: time.sleep(remaining)


def hold_at_point(x, y, seconds, button=1):
    """Hold one X11 mouse button at any on-screen coordinate, always release.

    Uses short interruptible sleeps instead of one uncontrolled long-press.
    SIGTERM/KeyboardInterrupt also runs the finally release handler.
    """
    invoke("xdotool", "mousemove", "--sync", str(x), str(y))
    invoke("xdotool", "mousedown", str(button))
    try:
        end = time.monotonic() + seconds
        while True:
            left = end - time.monotonic()
            if left <= 0:
                break
            time.sleep(min(left, 0.1))
    finally:
        # Keep this in finally so an exception never leaves Fire pressed.
        invoke("xdotool", "mouseup", str(button), timeout=3)


def execute_steps(steps, trace, start):
    held_keys, held_buttons = set(), set()
    def log(event, **fields):
        trace.append({"at_ms": round((time.monotonic() - start) * 1000, 2),
                      "event": event, **fields})
    try:
        for step in steps:
            op = step["op"]
            log(op, **{k: v for k, v in step.items() if k != "op"})
            if op == "key_down":
                invoke("xdotool", "keydown", step["key"])
                held_keys.add(step["key"])
            elif op == "key_up":
                invoke("xdotool", "keyup", step["key"])
                held_keys.discard(step["key"])
            elif op == "key_tap":
                invoke("xdotool", "key", "--clearmodifiers", step["key"])
            elif op == "mouse_down":
                invoke("xdotool", "mousedown", str(step["button"]))
                held_buttons.add(step["button"])
            elif op == "mouse_up":
                invoke("xdotool", "mouseup", str(step["button"]))
                held_buttons.discard(step["button"])
            elif op == "mouse_hold":
                hold_at_point(step["x"], step["y"], step["seconds"], step["button"])
            elif op == "move_smooth":
                smoothly_move(step["x"], step["y"], step["seconds"])
            elif op in ("move", "click"):
                invoke("xdotool", "mousemove", "--sync", str(step["x"]), str(step["y"]))
                if op == "click":
                    invoke("xdotool", "click", "1")
            elif op == "wait":
                time.sleep(step["seconds"])
            elif op == "mark":
                pass
    finally:
        # Input must never remain held after a failing or interrupted sequence.
        for button in held_buttons:
            try: invoke("xdotool", "mouseup", str(button), timeout=3)
            except Exception: pass
        for key in held_keys:
            try: invoke("xdotool", "keyup", key, timeout=3)
            except Exception: pass
        log("release_all")


def package(folder, frames, trace, errors, target_fps):
    manifest = {
        "schema": "aaris-v4-evidence-1",
        "source": "real X11 screenshot capture; NOT simulated Unity frames",
        "target_fps": target_fps,
        "actual_frames": len(frames),
        "duration_ms": round(frames[-1]["at_ms"] - frames[0]["at_ms"], 2) if len(frames) > 1 else 0,
        "frames": frames,
        "timeline": trace,
        "capture_errors": errors,
    }
    span = manifest["duration_ms"]
    manifest["measured_fps"] = round((len(frames) - 1) * 1000 / span, 2) if span > 0 else 0
    manifest["fps_target_met"] = manifest["measured_fps"] >= target_fps * 0.90
    manifest["missed_frames_estimate"] = max(0, round(span * target_fps / 1000) + 1 - len(frames))
    (folder / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    sheet = compose_sheet(folder, frames)
    archive = folder / "replay.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=4) as z:
        z.write(folder / "manifest.json", "manifest.json")
        if sheet: z.write(sheet, sheet.name)
        for frame in frames: z.write(folder / frame["file"], "frames/" + frame["file"])
    manifest["archive_sha256"] = hashlib.sha256(archive.read_bytes()).hexdigest()
    return manifest


def replay(steps, run_id, seconds, fps, inline_preview=False):
    if not SAFE_ID.fullmatch(run_id) or not 1 <= seconds <= 30 or not 1 <= fps <= 30:
        raise ValueError("invalid replay duration, rate, or id")
    width, height = screen_size()
    steps = verify_sequence(steps, width, height)
    if shutil.which("scrot") is None:
        raise RuntimeError("scrot is required for real screen captures")
    ROOT.mkdir(parents=True, exist_ok=True, mode=0o700)
    folder = ROOT / run_id
    folder.mkdir(mode=0o700, exist_ok=False)
    rec = Recorder(folder, fps)
    trace = []
    start = time.monotonic()
    action_error = None
    # Use one local OS lock, never multiple independent input writers of V4.
    with open(LOCK_PATH, "a+b") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        rec.start()
        try:
            time.sleep(0.20)
            execute_steps(steps, trace, start)
        except Exception as exc:
            action_error = type(exc).__name__ + ": " + str(exc)[:200]
        finally:
            remaining = seconds - (time.monotonic() - start)
            if remaining > 0:
                time.sleep(remaining)
            rec.finish()
            fcntl.flock(lock, fcntl.LOCK_UN)
    manifest = package(folder, rec.frames, trace, rec.errors, fps)
    manifest.update({"run_id": run_id, "path": str(folder),
                     "contact_sheet": str(folder / "contact_sheet.jpg"),
                     "archive": str(folder / "replay.zip"),
                     "action_error": action_error,
                     "result": "action_error" if action_error else ("capture_error" if rec.errors or not rec.frames else "captured")})
    print(json.dumps({k: v for k, v in manifest.items() if k not in ("frames", "timeline")}, ensure_ascii=False))
    if inline_preview and (folder / "contact_sheet.jpg").exists():
        jpeg = (folder / "contact_sheet.jpg").read_bytes()
        if len(jpeg) <= 220_000:
            print("AARIS_V4_PREVIEW_BASE64=" + base64.b64encode(jpeg).decode("ascii"))
            print("AARIS_V4_PREVIEW_COMPLETE")
    return 0 if manifest["result"] == "captured" else 2


def selftest():
    ok = [{"op": "key_down", "key": "w"}, {"op": "wait", "seconds": 0.2},
          {"op": "key_up", "key": "w"}, {"op": "mouse_down", "button": 1},
          {"op": "mouse_up", "button": 1}, {"op": "mark", "label": "fire"},
          {"op": "move", "x": 3, "y": 4}]
    assert len(verify_sequence(ok, 1280, 720)) == len(ok)
    assert verify_sequence([{"op":"move_smooth","x":55,"y":66,"seconds":0.4}],1280,720)
    hold = verify_sequence([{"op":"mouse_hold","x":1279,"y":719,"seconds":2}],1280,720)
    assert hold[0]["button"] == 1
    assert hold[0]["seconds"] == 2
    bad = [[{"op": "key_down", "key": "a;rm -rf"}],
           [{"op": "wait", "seconds": 25}],
           [{"op": "click", "x": 1280, "y": 2}],
           [{"op": "shell", "command": "whoami"}],
           [{"op": "key_up", "key": "a", "script": "hack"}],
           [{"op": "wait", "seconds": 2.9}] * 4,
           [{"op": "move_smooth", "x": 20, "y": 30, "seconds": 19}],
           [{"op":"mouse_hold","x":20,"y":30,"seconds":21}],
           [{"op":"mouse_hold","x":20,"y":30,"seconds":2,"button":4}]]
    for steps in bad:
        try: verify_sequence(steps, 1280, 720)
        except ValueError: continue
        raise AssertionError("bad V4 action accepted")
    print("AARIS_V4_SELFTEST_PASS")


def main():
    p = argparse.ArgumentParser(description="Private, local Aaris V4 GUI replay controller")
    p.add_argument("--selftest", action="store_true")
    p.add_argument("--sequence", help="JSON array of bounded X11 actions")
    p.add_argument("--id", default=None, help="unique run identifier")
    p.add_argument("--seconds", type=float, default=3)
    p.add_argument("--fps", type=int, default=15)
    p.add_argument("--inline-preview", action="store_true")
    args = p.parse_args()
    if args.selftest:
        selftest()
        return 0
    if not args.sequence:
        p.error("--sequence is required, or --selftest")
    steps = json.loads(args.sequence)
    run_id = args.id or time.strftime("v4_%Y%m%d_%H%M%S") + "_" + str(os.getpid())
    return replay(steps, run_id, args.seconds, args.fps, inline_preview=args.inline_preview)


if __name__ == "__main__":
    def _cancel(signum, frame):
        raise RuntimeError("cancelled by signal; releasing held inputs")
    signal.signal(signal.SIGTERM, _cancel)
    try: sys.exit(main())
    except (ValueError, RuntimeError, subprocess.SubprocessError, OSError, ImportError) as exc:
        print("AARIS_V4_ERROR:" + type(exc).__name__ + ":" + str(exc)[:160], file=sys.stderr)
        sys.exit(2)
