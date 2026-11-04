#!/usr/bin/env python3
"""Safe, local, read-only AI World job executor for an existing Unity Codespace.

Jobs use the existing WorldAutomation schema and engine; this does not replace
its preview/apply hash guards. It never edits core files, pushes Git, or
starts/rebuilds a Codespace. Capture requires explicit --allow-capture.
"""
from __future__ import annotations

import argparse
import fcntl
import hashlib
import json
import math
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[2]
ID_RE = re.compile(r"[A-Za-z0-9_-]{1,64}\Z")
MAX_BYTES = 16384
READ_MODES = {"schema", "context", "scan", "site", "capture"}
FIELDS = {
    "schema": {"id", "mode"},
    "context": {"id", "mode"},
    "scan": {"id", "mode", "search", "limit"},
    "site": {"id", "mode", "x", "z", "radius", "limit"},
    "capture": {"id", "mode", "camera_position", "camera_target"},
}


class JobError(ValueError):
    pass


def number(value, field, maximum=100000):
    if type(value) not in (int, float) or not math.isfinite(value) or abs(value) > maximum:
        raise JobError(f"{field}: finite number within +/-{maximum} required")
    return value


def validate(request):
    if not isinstance(request, dict) or request.get("mode") not in READ_MODES:
        raise JobError("Unsupported mode (read-only: schema/context/scan/site/capture)")
    mode = request["mode"]
    unexpected = set(request) - FIELDS[mode]
    if unexpected:
        raise JobError("Unexpected request fields: " + ", ".join(sorted(unexpected)))
    if not isinstance(request.get("id"), str) or not ID_RE.fullmatch(request["id"]):
        raise JobError("id must be 1..64 ASCII letters, digits, '_' or '-'")
    if mode == "scan":
        if not isinstance(request.get("search", ""), str) or len(request.get("search", "")) > 100:
            raise JobError("search must be a string of <=100 characters")
    if mode in ("scan", "site"):
        limit = request.get("limit", 100)
        if type(limit) is not int or not 1 <= limit <= 100:
            raise JobError("limit must be an integer from 1 to 100")
    if mode == "site":
        number(request.get("x"), "x")
        number(request.get("z"), "z")
        radius = number(request.get("radius", 50), "radius", 1000)
        if radius <= 0:
            raise JobError("radius must be positive")
    if mode == "capture":
        for key in ("camera_position", "camera_target"):
            coords = request.get(key)
            if not isinstance(coords, list) or len(coords) != 3:
                raise JobError(f"{key} must contain exactly [x,y,z]")
            for axis, value in zip("xyz", coords):
                number(value, key + "." + axis)
        if request["camera_position"] == request["camera_target"]:
            raise JobError("camera position and target cannot match")
    return request


def load(path):
    if path.is_symlink() or not path.is_file() or path.stat().st_size > MAX_BYTES:
        raise JobError("Request must be a non-symlink UTF-8 JSON file <=16KiB")
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise JobError("Invalid JSON request") from exc
    request = validate(raw)
    digest = hashlib.sha256(json.dumps(request, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()).hexdigest()
    return request, digest


def atomic_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    data = (json.dumps(value, sort_keys=True, indent=2, allow_nan=False) + "\n").encode()
    fd, tmp = tempfile.mkstemp(prefix=".atomic-", dir=path.parent)
    try:
        with os.fdopen(fd, "wb") as handle:
            handle.write(data)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(tmp, path)
    finally:
        if os.path.exists(tmp):
            os.unlink(tmp)


def command_for(request, root, directory, allow_capture):
    report = directory / "report.json"
    argv = [sys.executable, str(root / "Tools/World/world.py"),
            "--root", str(root), "--out", str(report)]
    mode = request["mode"]
    if mode in ("schema", "context"):
        argv.append(mode)
    elif mode == "scan":
        argv += ["scan", "--search", request.get("search", ""),
                 "--limit", str(request.get("limit", 100))]
    elif mode == "site":
        argv += ["site", "--x", str(request["x"]), "--z", str(request["z"]),
                 "--radius", str(request.get("radius", 50)),
                 "--limit", str(request.get("limit", 100))]
    elif mode == "capture":
        if not allow_capture:
            raise JobError("Capture disabled: pass --allow-capture only after verifying Unity graphics")
        if not os.environ.get("UNITY_EDITOR_PATH"):
            raise JobError("UNITY_EDITOR_PATH must point to a verified installed Editor")
        def vec(value):
            return dict(zip(("x", "y", "z"), value))
        atomic_json(directory / "engine-request.json", {
            "command": "capture", "cameraPosition": vec(request["camera_position"]),
            "cameraTarget": vec(request["camera_target"])})
        argv += ["engine", "--request", str(directory / "engine-request.json"),
                 "--capture-dir", str(directory)]
    else:
        raise JobError("Unsupported mode")
    return argv, report


def execute(path, root=ROOT, state=None, *, allow_capture=False, timeout=1800):
    root = Path(root).resolve()
    state = Path(state or root / ".aaris_ai_world")
    request, digest = load(Path(path))
    state.mkdir(parents=True, exist_ok=True)
    with (state / "worker.lock").open("a+b") as lock:
        fcntl.flock(lock.fileno(), fcntl.LOCK_EX)
        jobdir = state / "jobs" / request["id"]
        jobdir.mkdir(parents=True, exist_ok=True)
        receipt = jobdir / "status.json"
        if receipt.exists():
            old = json.loads(receipt.read_text())
            if old.get("request_sha256") != digest:
                raise JobError("Job ID reused with different content (refused)")
            if old.get("state") in ("succeeded", "failed"):
                return old  # Idempotent replay: never run a completed job twice.
        status = {"id": request["id"], "mode": request["mode"], "request_sha256": digest,
                  "state": "running", "updated_utc": int(time.time())}
        atomic_json(receipt, status)
        try:
            argv, report = command_for(request, root, jobdir, allow_capture)
            with (jobdir / "worker.log").open("ab") as log:
                proc = subprocess.run(argv, cwd=root, stdout=log, stderr=subprocess.STDOUT,
                                      timeout=timeout, check=False)
            if proc.returncode != 0 or not report.is_file():
                raise JobError(f"World command failed (exit {proc.returncode}); inspect worker.log")
            if report.stat().st_size > 8 * 1024 * 1024:
                raise JobError("World report exceeds 8MiB safety limit")
            result = json.loads(report.read_text())
            if result.get("status") == "error":
                raise JobError("World reported an error; inspect report.json")
            image = jobdir / "capture.png"
            if request["mode"] == "capture":
                if not image.is_file() or image.open("rb").read(8) != bytes.fromhex("89504e470d0a1a0a"):
                    raise JobError("Unity did not produce a valid PNG header")
                status["image"] = str(image)
            status["report"] = str(report)
            status["state"] = "succeeded"
        except (OSError, ValueError, subprocess.TimeoutExpired) as exc:
            status["state"] = "failed"
            status["error"] = str(exc)[:500]
        status["updated_utc"] = int(time.time())
        atomic_json(receipt, status)
        return status


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--request", type=Path, help="One local JSON request file")
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--state", type=Path, help="Ignored local journal/output directory")
    parser.add_argument("--allow-capture", action="store_true")
    parser.add_argument("--timeout", type=int, default=1800)
    args = parser.parse_args()
    if not args.request:
        parser.error("--request is required; no background polling is started automatically")
    if args.timeout < 1 or args.timeout > 3600:
        parser.error("--timeout must be 1..3600 seconds")
    try:
        result = execute(args.request, args.root, args.state,
                         allow_capture=args.allow_capture, timeout=args.timeout)
        print(json.dumps(result, sort_keys=True))
        return 0 if result["state"] == "succeeded" else 2
    except (OSError, ValueError) as exc:
        print(json.dumps({"state": "rejected", "error": str(exc)[:500]}), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
