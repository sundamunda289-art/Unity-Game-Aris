#!/usr/bin/env python3
"""Sanitized, evidence-based diagnostics for an ephemeral Unity Editor runner.

Never export raw Unity logs, OAuth URLs, ULF content, passwords, or session tokens:
these diagnostics are intended for public GitHub Actions artifacts.
"""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]
STATE = ROOT / "ci-live-control"
LOGS = ROOT / "ci-logs"

SIGNATURES = (
    ("licensing", re.compile(r"licens(?:e|ing).{0,90}(?:fail|denied|invalid|expired|not found|not activated|error)|(?:fail|error|invalid).{0,70}licens", re.I)),
    ("auth", re.compile(r"(?:authentication|authorization|sign.in|sign in).{0,65}(?:failed|denied|required|error)", re.I)),
    ("compilation", re.compile(r"\berror CS\d{4}\b|\bcompilation failed\b|\bscript compilation failed\b", re.I)),
    ("asset_import", re.compile(r"\basset import.{0,60}(?:error|failed)|\bimport.{0,50}failed", re.I)),
    ("display_graphics", re.compile(r"(?:failed to|cannot|could not).{0,75}(?:display|opengl|glx|graphics device|x11)|\bNo protocol specified\b", re.I)),
    ("editor_crash", re.compile(r"\bsegmentation fault\b|\bfatal error\b|\bcrash\b|\bOutOfMemoryException\b|\bStackOverflowException\b", re.I)),
    ("missing_runtime", re.compile(r"\bNo such file or directory\b|\bshared libraries\b|\blib[^ ]+\.so.{0,35}not found", re.I)),
)

def read_json(path):
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
        return value if isinstance(value, dict) else {}
    except (OSError, UnicodeError, ValueError):
        return {}

def signatures_from_text(source):
    """Only return fixed category labels and counts, NEVER excerpts."""
    return {name: min(999, len(pattern.findall(source))) for name, pattern in SIGNATURES
            if pattern.search(source)}

def read_log_signatures():
    names = ("live-unity.log", "unity-initial-activation.log", "unity-activate-retry.log")
    result = {}
    for name in names:
        path = LOGS / name
        try:
            if not path.is_file():
                continue
            with path.open("rb") as f:
                f.seek(max(0, path.stat().st_size - 2_000_000))
                source = f.read().decode("utf-8", errors="replace")
            result[name] = {"present": True, "bytes": path.stat().st_size,
                            "error_categories": signatures_from_text(source)}
        except OSError:
            result[name] = {"present": False}
    return result

def docker_status():
    try:
        result = subprocess.run(
            ["docker", "inspect", "--format", "{{.State.Running}} {{.State.OOMKilled}}", "aaris-unity-live"],
            capture_output=True, text=True, timeout=8, check=False)
        if result.returncode != 0:
            return {"running": False, "oom_killed": None, "editor_process_detected": False}
        flags = result.stdout.strip().split()
        top = subprocess.run(["docker", "top", "aaris-unity-live", "-eo", "pid,args"],
                             capture_output=True, text=True, timeout=8, check=False)
        # Do not expose command lines: they can contain sensitive values.
        editor = bool(top.returncode == 0 and any(
            "-projectPath" in line and re.search(r"(?:^|/|\s)Unity(?:\s|$)", line)
            for line in top.stdout.splitlines()))
        return {"running": bool(flags and flags[0] == "true"),
                "oom_killed": flags[1] == "true" if len(flags) > 1 else None,
                "editor_process_detected": editor}
    except (OSError, subprocess.TimeoutExpired):
        return {"running": False, "oom_killed": None, "editor_process_detected": False}

def snapshot():
    license_status = read_json(STATE / "license-status.json")
    last_camera = read_json(STATE / "status.json")
    last_world = read_json(STATE / "ai-result.json")
    heartbeat = read_json(STATE / "editor-heartbeat.json")
    runtime = read_json(STATE / "editor-runtime.json")
    docker = docker_status()
    now = time.time()
    stamp = heartbeat.get("unixSeconds")
    graphics = heartbeat.get("graphicsDevice")
    beat_recent = (type(stamp) in (int, float) and 0 <= now - stamp <= 45
                   and isinstance(graphics, str) and graphics not in ("", "Null"))
    camera = (last_camera.get("state") == "Camera moved and real Unity PNG saved"
              and (STATE / "latest.png").is_file())
    world = last_world.get("ok") is True
    try:
        free_gb = round(shutil.disk_usage(ROOT).free / (1024 ** 3), 2)
    except OSError:
        free_gb = None
    safe_states = {
        "fetching_unity_editor", "installing_official_unity_cli", "starting_unity_oauth",
        "waiting_for_mobile_unity_signin", "activating_unity_personal",
        "unity_personal_activation_succeeded", "license_secret_installed",
        "unity_activation_requires_attention", "editor_starting", "editor_exited",
        "editor_launch_failed", "editor_ready",
    }
    stage = license_status.get("state")
    if stage not in safe_states:
        stage = "unavailable"
    is_editor = bool(beat_recent or camera or world)
    logs = read_log_signatures()
    return {
        "schema": 1,
        "run_id": os.getenv("GITHUB_RUN_ID", "") if os.getenv("GITHUB_RUN_ID", "").isdigit() else "",
        "stage": stage,
        "unity_container_running": docker["running"],
        "unity_container_oom_killed": docker["oom_killed"],
        "editor_process_detected": docker["editor_process_detected"],
        "editor_start_recorded": runtime.get("started") is True,
        "editor_exit_recorded": runtime.get("finished") is True,
        "editor_exit_code": runtime.get("exit_code") if type(runtime.get("exit_code")) is int else None,
        "editor_heartbeat_recent": bool(beat_recent),
        "editor_heartbeat_age_sec": round(now - stamp, 1) if beat_recent else None,
        "editor_graphics_backend": graphics if beat_recent and graphics in
            ("OpenGLCore", "Vulkan", "Direct3D11", "Direct3D12", "Metal", "OpenGLES3") else "unverified",
        "unity_editor_confirmed": is_editor,
        "native_scene_open": bool(beat_recent and heartbeat.get("nativeSceneOpen") is True),
        "real_camera_photo_confirmed": bool(camera or (world and (STATE / "ai-photo.png").is_file())),
        "last_world_command_success": bool(world),
        "logs": logs,
        "free_disk_gb": free_gb,
        "guidance": (
            "Review fixed error_categories and process/heartbeat signals. "
            "OAuth browser success is not proof the Editor is licensed. "
            "Raw Unity logs remain private on the runner, not in public artifacts."
        ),
    }

def selftest():
    source = ("error CS0246: The type or namespace name... \n"
              "licensing failed: no valid license\n"
              "Segmentation fault\n"
              "https://id.unity.com/auth?access_token=private")
    categories = signatures_from_text(source)
    assert categories.get("compilation") == 1
    assert categories.get("licensing") == 1
    assert categories.get("editor_crash") == 1
    assert "private" not in json.dumps(categories)
    assert signatures_from_text("Scene loaded successfully") == {}
    print("Sanitized Unity Editor diagnostic classification: PASS")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    args = parser.parse_args()
    if args.selftest:
        selftest()
    else:
        report = snapshot()
        STATE.mkdir(parents=True, exist_ok=True)
        outfile = STATE / "editor-runtime-diagnostics-safe.json"
        tmp = STATE / "editor-runtime-diagnostics.tmp"
        tmp.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        os.replace(tmp, outfile)
        print("Safe Unity diagnostic:", json.dumps({
            key: report[key] for key in ("stage", "unity_container_running",
            "editor_process_detected", "unity_editor_confirmed",
            "native_scene_open", "real_camera_photo_confirmed", "logs")
        }))
