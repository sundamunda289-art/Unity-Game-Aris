#!/usr/bin/env python3
"""GitHub issue-based camera intent relay for an active licensed Unity Editor.

Only comments from the single expected repository owner are accepted.
No general-purpose shell, VNC input, source edits, or credential access.
The GH_TOKEN is the ephemeral built-in Actions token and is never logged.
"""
import argparse
import datetime as dt
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time

from live_camera_server import validate, STATE, public_diagnostics
import ai_intents

ISSUE = int(os.getenv("AARIS_CAMERA_ISSUE", "77"))
REPO = os.getenv("GITHUB_REPOSITORY", "sundamunda289-art/Unity-Game-Aris")
OWNER = "sundamunda289-art"
RELEASE = "live-photos"
COMMAND = re.compile(
    r"^CAMERA\s+([+\-]?(?:\d+(?:\.\d*)?|\.\d+))\s+"
    r"([+\-]?(?:\d+(?:\.\d*)?|\.\d+))\s+"
    r"([+\-]?(?:\d+(?:\.\d*)?|\.\d+))\s+"
    r"([+\-]?(?:\d+(?:\.\d*)?|\.\d+))\s+"
    r"([+\-]?(?:\d+(?:\.\d*)?|\.\d+))\s+"
    r"([+\-]?(?:\d+(?:\.\d*)?|\.\d+))\s*$"
)

def parse(body):
    match = COMMAND.fullmatch(body.strip()) if isinstance(body, str) else None
    if not match:
        raise ValueError("Use CAMERA x y z yaw pitch distance (six numeric fields)")
    return validate(dict(zip(("x", "y", "z", "yaw", "pitch", "distance"),
                             (float(value) for value in match.groups()))))

def gh(*args, check=True, **kwargs):
    return subprocess.run(("gh", *args), check=check,
                          capture_output=True, text=True, **kwargs)

def issue_comment(body):
    # Do not post any credentials or internal runner IPs.
    gh("api", "-X", "POST", f"repos/{REPO}/issues/{ISSUE}/comments",
       "-f", f"body={body}")

def camera_health():
    p = STATE / "status.json"
    if not p.exists():
        return {}
    try:
        return json.loads(p.read_text())
    except (OSError, ValueError):
        return {}

def ensure_release():
    current = gh("release", "view", RELEASE, "--repo", REPO, check=False)
    if current.returncode != 0:
        gh("release", "create", RELEASE, "--repo", REPO,
           "--target", os.environ.get("GITHUB_SHA", "main"),
           "--title", "Aaris genuine Unity camera captures",
           "--notes", "Actual licensed Unity Editor camera PNGs only. Not live video.")

def publish_photo(comment_id, photo):
    if not photo.is_file() or photo.stat().st_size < 5000:
        raise RuntimeError("No real Unity Editor PNG was captured")
    data = photo.read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise RuntimeError("Camera output is not a PNG")
    # The unique filename prevents overwrites / stale cache for AI viewers.
    filename = f"live-camera-{comment_id}.png"
    temp = STATE / filename
    shutil.copyfile(photo, temp)
    ensure_release()
    gh("release", "upload", RELEASE, str(temp), "--repo", REPO)
    url = f"https://github.com/{REPO}/releases/download/{RELEASE}/{filename}"
    return url

def publish_ai_file(comment_id, path):
    """Publish only validated AI reports, blueprints or genuine Editor PNG files."""
    if not path.is_file() or path.stat().st_size > 6 * 1024 * 1024:
        raise RuntimeError("AI result absent or too large")
    if not re.fullmatch(r"ai-(?:result-\d+\.json|blueprint-\d+\.(?:svg|ply)|photo-\d+\.png)", path.name):
        raise RuntimeError("Unexpected AI report filename")
    ensure_release()
    gh("release", "upload", RELEASE, str(path), "--repo", REPO)
    return f"https://github.com/{REPO}/releases/download/{RELEASE}/{path.name}"


def process(comment):
    author = comment.get("user") or {}
    if author.get("login") != OWNER:
        return "ignored-non-owner"
    comment_id = comment.get("id")
    if type(comment_id) is not int or comment_id <= 0:
        return "ignored-invalid-comment-id"
    body = comment.get("body", "")
    if isinstance(body, str) and body.strip() == "DIAGNOSE":
        # Works before Unity licensing or C# compilation. Public reply only
        # contains allowlisted states/booleans, never logs or sign-in URLs.
        report = public_diagnostics()
        STATE.mkdir(parents=True, exist_ok=True)
        path = STATE / f"ai-diagnostics-{comment_id}.json"
        path.write_text(json.dumps(report, indent=2, sort_keys=True))
        issue_comment(f"DIAGNOSE #{comment_id}: sanitized runner report (no credentials):\n\n"
                      + json.dumps(report, sort_keys=True))
        return "diagnosed"
    if isinstance(body, str) and body.startswith("AI "):
        try:
            return ai_intents.handle(body, comment_id, STATE, Path.cwd(),
                                     issue_comment, publish_ai_file)
        except ValueError as error:
            issue_comment(f"AI request #{comment_id}: validation rejected: {str(error)[:200]}")
            return "invalid-ai-intent"
        except (RuntimeError, OSError, subprocess.CalledProcessError) as error:
            issue_comment(f"AI request #{comment_id}: operation failed ({type(error).__name__}).")
            return "ai-operation-failed"
    if body.strip() == "ACTIVATE_UNITY":
        license_state = {}
        private_file = STATE / "license-status.json"
        if private_file.exists():
            try:
                license_state = json.loads(private_file.read_text())
            except (OSError, ValueError):
                pass
        if license_state.get("state") in {
            "installing_official_unity_cli", "starting_unity_oauth",
            "waiting_for_mobile_unity_signin", "activating_unity_personal",
            "installing_unity_hub_prerequisites",
        "installing_official_unity_hub", "waiting_for_unity_hub_signin",
            "editor_starting"
        }:
            issue_comment(f"Activation request #{comment_id}: Unity is already at "
                          f"{license_state['state']}. Follow the protected Cloudflare "
                          "Unity login prompt if present. No second activation was started.")
            return "activation-already-in-progress"
        # Fixed Docker command; never build shell strings from issue content.
        # Only the repository owner can reach this branch.
        running = subprocess.run(
            ["docker", "inspect", "--format", "{{.State.Running}}", "aaris-unity-live"],
            text=True, capture_output=True, timeout=15, check=False)
        if running.returncode != 0 or running.stdout.strip() != "true":
            issue_comment(f"Activation request #{comment_id}: no running Unity container. "
                          "Post ACTIVATE_UNITY 15 to start a new session.")
            return "no-active-container"
        command = ("flock -n /tmp/aaris-unity-bootstrap.lock "
                   "python3 Tools/CI/unity_personal_bootstrap.py "
                   "> /project/ci-logs/unity-activate-retry.log 2>&1")
        result = subprocess.run(
            ["docker", "exec", "-d", "aaris-unity-live", "bash", "-lc", command],
            text=True, capture_output=True, timeout=15, check=False)
        if result.returncode == 0:
            issue_comment(f"Activation request #{comment_id}: official Unity activation "
                          "was requested inside the currently running Editor container. "
                          "Check Cloudflare's protected License Status; fresh OAuth sign-in "
                          "may still be required.")
            return "activation-requested"
        issue_comment(f"Activation request #{comment_id}: Docker could not launch "
                      "the authorized Unity Hub bootstrap process.")
        return "activation-launch-failed"
    try:
        camera = parse(comment.get("body", ""))
    except ValueError:
        return "ignored-non-camera"
    camera["nonce"] = comment_id
    state_file = STATE / "camera.json"
    tmp = STATE / "camera-issue.tmp"
    tmp.write_text(json.dumps(camera))
    os.replace(tmp, state_file)
    status_file = STATE / "status.json"
    status = {}
    deadline = time.monotonic() + 90
    while time.monotonic() < deadline:
        time.sleep(2)
        status = camera_health()
        if status.get("nonce") == comment_id:
            break
    if status.get("nonce") != comment_id:
        issue_comment(f"Camera request #{comment_id}: timed out waiting for a licensed Unity Editor. "
                      "The live desktop alone is not proof that Unity activated.")
        return "no-unity-response"
    if "Camera moved and real Unity PNG saved" not in status.get("state",""):
        issue_comment(f"Camera request #{comment_id}: Unity reported an error: "
                      f"{status.get('state', 'unknown')[:250]}")
        return "unity-error"
    try:
        url = publish_photo(comment_id, STATE / "latest.png")
        issue_comment(f"Camera request #{comment_id}: real Unity 1280×720 screenshot:\n\n{url}\n\n"
                      "The desktop's noVNC stream remains available separately while the runner is live.")
        return "published"
    except (RuntimeError, OSError, subprocess.CalledProcessError) as exc:
        issue_comment(f"Camera request #{comment_id}: screenshot upload failed "
                      f"({type(exc).__name__}).")
        return "publish-failed"

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--seconds", type=int, default=4800)
    args = parser.parse_args()
    if args.selftest:
        ai_intents.selftest()
        check = public_diagnostics()
        assert set(check) == {"license_phase", "vnc_backend", "vnc_websocket",
                              "container", "process_inspection", "unity_editor_process",
                              "unity_hub_process", "lfs_scene_hydrated",
                              "genuine_unity_photo_present", "next_action", "screenshot_note"}
        assert "auth_url" not in check and "detail" not in check
        sample = parse("CAMERA -420 22 -210 0 20 22")
        assert sample["x"] == -420 and sample["pitch"] == 20
        for body in ("CAMERA 9999 22 -210 0 20 22",
                     "SHELL rm -rf /", "CAMERA NaN 0 0 0 0 22",
                     "CAMERA 0 0 0 0 0 22; cat /etc/passwd"):
            try:
                parse(body)
            except ValueError:
                pass
            else:
                raise AssertionError("Unsafe camera intent was accepted")
        assert "ACTIVATE_UNITY" != "ACTIVATE_UNITY 15"
        assert parse("CAMERA -420 22 -210 0 20 22")["distance"] == 22
        print("GitHub issue camera and explicit license retry parser selftest: PASS")
        return
    if REPO != "sundamunda289-art/Unity-Game-Aris" or ISSUE != 77:
        raise RuntimeError("Issue queue is restricted to the configured Aaris repository")
    # Announce only the time-limited, email-PIN-protected tunnel address.
    # This enables the persistent free Cloudflare Worker hub to discover it.
    tunnel_file = Path("/tmp/aaris-tunnel-url")
    if tunnel_file.is_file():
        live_url = tunnel_file.read_text().strip()
        if re.fullmatch(r"https://[a-z0-9-]+\.trycloudflare\.com", live_url):
            run_id = os.getenv("GITHUB_RUN_ID", "")
            mins = os.getenv("SESSION_MINUTES", "15")
            if run_id.isdigit() and mins in ("15", "45", "90", "240", "300"):
                try:
                    issue_comment(f"AARIS_LIVE_DESKTOP run_id={run_id} "
                                  f"session_minutes={mins} url={live_url}")
                    print("Protected tunnel announced to GitHub issue (PIN required)", flush=True)
                except subprocess.CalledProcessError:
                    print("Could not announce live tunnel; camera polling still available", flush=True)
    print("Starting owner-only camera + AI intent polling on live Unity session", flush=True)
    start = dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    since = start
    seen = set()
    last_license_state = None
    allowed_license_states = {
        "installing_official_unity_cli", "starting_unity_oauth",
        "waiting_for_mobile_unity_signin", "activating_unity_personal",
        "unity_personal_activation_succeeded", "license_secret_installed",
        "installing_unity_hub_prerequisites", "installing_official_unity_hub",
        "starting_unity_hub_gui", "waiting_for_unity_hub_signin",
        "unity_hub_window_not_visible", "unity_hub_window_check_unavailable",
        "unity_hub_license_detected", "editor_process_ended",
        "unity_activation_requires_attention", "editor_starting",
        "fetching_unity_editor"
    }
    until = time.monotonic() + max(15, min(args.seconds, 18000))
    while time.monotonic() < until:
        try:
            # Public GitHub issue gets only a fixed, non-sensitive state name.
            # Unity OAuth URLs remain ONLY on the email-PIN gated /api/license.
            private_license = STATE / "license-status.json"
            if private_license.is_file():
                public_state = json.loads(private_license.read_text()).get("state")
                if (public_state in allowed_license_states and
                        public_state != last_license_state):
                    issue_comment("AARIS_LICENSE_STATUS " + public_state)
                    last_license_state = public_state
            result = gh("api", "--method", "GET", f"repos/{REPO}/issues/{ISSUE}/comments",
                        "-f", f"since={since}", "-f", "per_page=100",
                        check=True)
            comments = json.loads(result.stdout)
            for comment in comments:
                comment_id = comment.get("id")
                if comment_id in seen:
                    continue
                seen.add(comment_id)
                if len(seen) > 2000:
                    seen.clear()
                    seen.add(comment_id)
                created = comment.get("created_at")
                if isinstance(created, str) and created > since:
                    since = created
                outcome = process(comment)
                if outcome != "ignored-non-camera":
                    print(f"Camera issue comment {comment_id}: {outcome}", flush=True)
        except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError) as exc:
            print("Issue relay transient error:", type(exc).__name__, flush=True)
        time.sleep(4)

if __name__ == "__main__":
    main()
