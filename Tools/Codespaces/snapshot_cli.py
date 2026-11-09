#!/usr/bin/env python3
"""Persistent Unity Editor camera CLI. Bounded local intents; no asset writes."""
import argparse
import json
import math
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[2]
CONTROL = ROOT / "ci-live-control"
QUEUE = CONTROL / "snapshot-queue"
RESULTS = CONTROL / "snapshot-results"
HEALTH = CONTROL / "snap-health.json"
RUNTIME_PRESETS = CONTROL / "snapshot-presets.json"
SHARED_PRESETS = ROOT / "Tools/Snapshots/presets.json"
ID = re.compile(r"[A-Za-z][A-Za-z0-9_-]{0,47}\Z", re.ASCII)
EDITOR_PATHS = [
    Path.home() / ".local/share/aaris-unity-editors/6000.3.26f1/Editor/Unity",
    Path("/workspaces/.aaris-unity-editors/6000.3.26f1/Editor/Unity"),
    Path("/tmp/aaris-unity-runtime/editors/6000.3.26f1/Editor/Unity"),
]
TERMINAL = {"passed", "suspect", "failed"}


def vector(value, label):
    if isinstance(value, str):
        value = value.split(",")
    if not isinstance(value, (list, tuple)) or len(value) != 3:
        raise ValueError(label + " requires exactly three coordinates")
    if any(isinstance(v, bool) or not isinstance(v, (str, int, float)) for v in value):
        raise ValueError(label + " has invalid coordinate types")
    try:
        coords = [float(v) for v in value]
    except (ValueError, TypeError) as exc:
        raise ValueError(label + " is nonnumeric") from exc
    if not all(math.isfinite(v) for v in coords):
        raise ValueError(label + " contains NaN or infinity")
    if not (-1000 <= coords[0] <= 1000 and -500 <= coords[1] <= 4000
            and -1000 <= coords[2] <= 1000):
        raise ValueError(label + " outside map bounds")
    return coords


def normalize_shot(source):
    if not isinstance(source, dict) or set(source) - {"id", "pos", "lookAt", "width", "height"}:
        raise ValueError("Invalid camera shot object or unknown fields")
    name = source.get("id")
    if not isinstance(name, str) or not ID.fullmatch(name):
        raise ValueError("Invalid camera ID (use ASCII letters, digits, - or _)")
    pos, look = vector(source.get("pos"), "pos"), vector(source.get("lookAt"), "lookAt")
    if sum((a - b) ** 2 for a, b in zip(pos, look)) < 0.0001:
        raise ValueError("lookAt must differ from camera position")
    width, height = source.get("width", 1280), source.get("height", 720)
    if type(width) is not int or type(height) is not int or not (
        320 <= width <= 1920 and 180 <= height <= 1080 and width * height <= 2073600
    ):
        raise ValueError("Camera image dimensions invalid")
    return {"id": name, "pos": pos, "lookAt": look, "width": width, "height": height}


def unity_shot(shot):
    p, l = shot["pos"], shot["lookAt"]
    return {"id": shot["id"], "x": p[0], "y": p[1], "z": p[2],
            "lookX": l[0], "lookY": l[1], "lookZ": l[2],
            "width": shot["width"], "height": shot["height"]}


def atomic_json(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name("." + path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        with temp.open("x", encoding="utf-8") as f:
            json.dump(data, f, indent=2, allow_nan=False)
            f.write("\n")
        os.replace(temp, path)
    finally:
        temp.unlink(missing_ok=True)


def read_presets(path):
    if not path.exists():
        return {}
    data = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(data, dict) or not isinstance(data.get("shots"), dict):
        raise ValueError("Invalid camera presets file: " + str(path))
    return {k: normalize_shot(v) for k, v in data["shots"].items()}


def save_presets(path, shots):
    existing = read_presets(path)
    existing.update({s["id"]: s for s in shots})
    atomic_json(path, {"shots": dict(sorted(existing.items()))})


def health():
    try:
        data = json.loads(HEALTH.read_text(encoding="utf-8"))
        age = time.time() - float(data["epochUtc"])
        return {"state": data.get("state", "unknown"), "scene": data.get("scene", ""),
                "graphics": data.get("graphics", ""), "ageSeconds": round(age, 1),
                "fresh": -5 <= age <= 12}
    except (OSError, ValueError, KeyError, TypeError):
        return {"state": "editor_not_reporting", "fresh": False}


def find_editor():
    candidates = ([Path(os.environ["UNITY_PATH"])] if os.getenv("UNITY_PATH") else []) + EDITOR_PATHS
    return next((path for path in candidates if path.is_file() and os.access(path, os.X_OK)), None)


def copy_photo(data, shot_id, destination):
    for frame in data.get("frames", []):
        if frame.get("id") == shot_id and frame.get("file") and frame.get("state") in ("passed", "suspect"):
            source = (CONTROL / frame["file"]).resolve()
            if not source.is_relative_to(CONTROL.resolve()) or not source.is_file():
                raise RuntimeError("Unexpected Unity photo path")
            target = Path(destination).expanduser().resolve()
            if target.suffix.lower() != ".png":
                raise ValueError("Output file must end in .png")
            target.parent.mkdir(parents=True, exist_ok=True)
            if target != source:
                shutil.copyfile(source, target)
            print("SAVED " + str(target))
            return
    print("NO_VALID_PNG_FOR " + shot_id, file=sys.stderr)


def submit(shots, wait, timeout, out=None, out_dir=None):
    if not 1 <= len(shots) <= 20 or len({s["id"] for s in shots}) != len(shots):
        raise ValueError("A batch needs 1-20 different camera IDs")
    state = health()
    if not state["fresh"] or state["state"] != "ready":
        raise RuntimeError("Unity Editor not ready (no fresh heartbeat/compiling/licence): " + state["state"])
    request_id = "snap-" + uuid.uuid4().hex
    atomic_json(QUEUE / (request_id + ".json"),
                {"requestId": request_id, "shots": [unity_shot(s) for s in shots]})
    print("QUEUED " + request_id, flush=True)
    if not wait:
        return 0
    until = time.monotonic() + timeout
    result_path = RESULTS / (request_id + ".json")
    while time.monotonic() < until:
        if result_path.is_file():
            data = json.loads(result_path.read_text(encoding="utf-8"))
            if data.get("state") in TERMINAL:
                print(json.dumps(data, indent=2))
                if out and len(shots) == 1:
                    copy_photo(data, shots[0]["id"], out)
                if out_dir:
                    for s in shots:
                        copy_photo(data, s["id"], Path(out_dir) / (s["id"] + ".png"))
                return 0 if data["state"] == "passed" else 3
        if not health()["fresh"] and not (QUEUE / (request_id + ".json.processing")).exists():
            raise RuntimeError("Editor heartbeat lost before processing: inspect request " + request_id)
        time.sleep(0.3)
    raise RuntimeError("Capture timed out, not verified: " + request_id)


def load_batch(path):
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(data, dict) or set(data) != {"shots"} or not isinstance(data["shots"], list):
        raise ValueError("Batch file requires only a shots array")
    shots = [normalize_shot(s) for s in data["shots"]]
    if not 1 <= len(shots) <= 20:
        raise ValueError("Batch must contain 1-20 shots")
    return shots


def bootstrap(no_install=False, start_editor=True):
    if sys.platform != "linux":
        raise RuntimeError("Ubuntu Codespace required")
    if not no_install:
        subprocess.run(["bash", str(ROOT / ".devcontainer/bootstrap.sh")], cwd=ROOT, check=True)
    subprocess.run(["bash", str(ROOT / ".devcontainer/start_screen.sh")], cwd=ROOT, check=True)
    if not no_install:
        lfs_check = subprocess.run([sys.executable, str(ROOT / "Tools/Codespaces/verify_lfs.py")],
                                   cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        if lfs_check.returncode != 0:
            subprocess.run(["git", "lfs", "pull"], cwd=ROOT, check=True)
        if find_editor() is None:
            subprocess.run(["bash", str(ROOT / "Tools/Codespaces/install_unity.sh")], cwd=ROOT, check=True)
    subprocess.run(["bash", str(ROOT / "Tools/Codespaces/graphics_preflight.sh")], cwd=ROOT, check=True)
    editor = find_editor()
    if editor is None:
        raise RuntimeError("Unity Editor missing; run bootstrap without --no-install")
    if not start_editor:
        print("GRAPHICS_READY_EDITOR_NOT_STARTED")
        return
    if health()["fresh"]:
        print("EDITOR_ALREADY_REPORTING " + json.dumps(health()))
        return
    proc = subprocess.run(["pgrep", "-af", r"[/]Editor/Unity"], text=True, capture_output=True)
    if proc.returncode == 0 and str(ROOT) in proc.stdout:
        print("EDITOR_PROCESS_EXISTS; wait for import/licence readiness, no second Editor launched")
        return
    env = os.environ.copy()
    env.update({"DISPLAY": env.get("DISPLAY", ":99"),
                "LIBGL_ALWAYS_SOFTWARE": "1", "GALLIUM_DRIVER": "llvmpipe"})
    logs = CONTROL / "private-logs"
    logs.mkdir(parents=True, exist_ok=True)
    with (logs / "editor-boot.stdout").open("ab") as output:
        subprocess.Popen([str(editor), "-projectPath", str(ROOT), "-force-glcore",
                          "-logFile", str(logs / "editor.log")],
                         cwd=ROOT, env=env, stdin=subprocess.DEVNULL, stdout=output,
                         stderr=subprocess.STDOUT, start_new_session=True)
    print("EDITOR_START_REQUESTED_NOT_YET_VERIFIED (check ./tools/snap status)")


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    if argv and argv[0].startswith("--"):
        argv.insert(0, "shot")
    ap = argparse.ArgumentParser(description="Persistent Unity URP snapshot engine")
    sub = ap.add_subparsers(dest="command", required=True)
    shot = sub.add_parser("shot")
    shot.add_argument("--id", default="camera-01")
    shot.add_argument("--pos", required=True)
    shot.add_argument("--look", required=True)
    shot.add_argument("--width", type=int, default=1280)
    shot.add_argument("--height", type=int, default=720)
    shot.add_argument("--out")
    shot.add_argument("--persist", action="store_true")
    batch = sub.add_parser("batch")
    batch.add_argument("file")
    batch.add_argument("--out-dir")
    batch.add_argument("--persist", action="store_true")
    again = sub.add_parser("recapture")
    again.add_argument("--id", required=True)
    again.add_argument("--out")
    for cmd in (shot, batch, again):
        cmd.add_argument("--no-wait", action="store_true")
        cmd.add_argument("--timeout", type=int, default=300)
    sub.add_parser("status")
    sub.add_parser("doctor")
    b = sub.add_parser("bootstrap")
    b.add_argument("--no-install", action="store_true")
    b.add_argument("--no-start-editor", action="store_true")
    args = ap.parse_args(argv)
    if args.command == "status":
        state = health()
        print(json.dumps(state, indent=2))
        return 0 if state["fresh"] else 2
    if args.command == "doctor":
        return subprocess.run(["bash", str(ROOT / "Tools/Codespaces/graphics_preflight.sh")],
                              cwd=ROOT).returncode
    if args.command == "bootstrap":
        bootstrap(args.no_install, not args.no_start_editor)
        return 0
    if args.command == "shot":
        shots = [normalize_shot({"id": args.id, "pos": args.pos, "lookAt": args.look,
                                 "width": args.width, "height": args.height})]
    elif args.command == "batch":
        shots = load_batch(args.file)
    else:
        saved = read_presets(SHARED_PRESETS)
        saved.update(read_presets(RUNTIME_PRESETS))
        if args.id not in saved:
            raise ValueError("Unknown saved viewpoint: " + args.id)
        shots = [saved[args.id]]
    if not 1 <= args.timeout <= 900:
        raise ValueError("Timeout out of range (1-900)")
    if args.command != "recapture":
        save_presets(RUNTIME_PRESETS, shots)
        if args.persist:
            save_presets(SHARED_PRESETS, shots)
            print("RECIPE_CHANGED: commit Tools/Snapshots/presets.json for reuse in new Codespaces")
    return submit(shots, not args.no_wait, args.timeout,
                  getattr(args, "out", None), getattr(args, "out_dir", None))


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (RuntimeError, ValueError, OSError, json.JSONDecodeError, subprocess.CalledProcessError) as exc:
        print("SNAP_ERROR: " + str(exc), file=sys.stderr)
        sys.exit(2)
