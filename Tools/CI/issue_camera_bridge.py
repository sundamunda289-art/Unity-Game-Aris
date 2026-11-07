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

from live_camera_server import validate, STATE
from ai_world_commands import parse as parse_world, selftest as world_selftest, EDIT_OPS

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


def publish_world_asset(comment_id, filename, suffix):
    source = STATE / filename
    if not source.is_file() or source.stat().st_size == 0:
        return None
    if suffix == "png" and source.read_bytes()[:8] != b"\x89PNG\r\n\x1a\n":
        raise RuntimeError("Unity object photo is not PNG")
    ensure_release()
    dest = STATE / f"ai-world-{comment_id}.{suffix}"
    shutil.copyfile(source, dest)
    gh("release", "upload", RELEASE, str(dest), "--repo", REPO)
    return f"https://github.com/{REPO}/releases/download/{RELEASE}/{dest.name}"

def process_world(comment_id, body):
    try:
        request = parse_world(body)
    except (ValueError, TypeError, json.JSONDecodeError) as exc:
        issue_comment(f"AI map request #{comment_id}: rejected ({str(exc)[:180]}).")
        return "invalid-world-request"
    request["nonce"] = comment_id
    # A single command per Editor poll; the owner-only issue relay handles them sequentially.
    tmp = STATE / "ai-command.tmp"
    tmp.write_text(json.dumps(request))
    os.replace(tmp, STATE / "ai-command.json")
    response_file = STATE / "ai-result.json"
    reply = {}
    deadline = time.monotonic() + 110
    while time.monotonic() < deadline:
        time.sleep(2)
        try:
            reply = json.loads(response_file.read_text()) if response_file.is_file() else {}
        except (OSError, ValueError):
            reply = {}
        if reply.get("nonce") == comment_id:
            break
    if reply.get("nonce") != comment_id:
        queued = STATE / "ai-command.json"
        stage = "queued_not_read_by_editor" if queued.is_file() else "read_by_editor_result_missing"
        # Public issues contain only a known status label, NEVER private OAuth links.
        safe_states = {"fetching_unity_editor", "editor_starting",
                       "installing_unity_hub_prerequisites",
                       "waiting_for_unity_hub_signin",
                       "waiting_for_mobile_unity_signin", "activating_unity_personal",
                       "unity_personal_activation_succeeded", "license_secret_installed",
                       "unity_activation_requires_attention"}
        license_state = "unknown"
        try:
            status = json.loads((STATE / "license-status.json").read_text())
            candidate = status.get("state")
            if candidate in safe_states:
                license_state = candidate
        except (OSError, ValueError, TypeError):
            pass
        issue_comment(f"AI map request #{comment_id}: timed out waiting for the Editor. "
                      f"Stage={stage}; UnityLicense={license_state}. "
                      "Do not repeat a MOVE/SCALE blindly: if the command was read, "
                      "it could still complete. First INSPECT the object after Editor recovery.")
        return "world-editor-timeout"
    if not reply.get("ok"):
        issue_comment(f"AI map request #{comment_id}: Unity reported an error: "
                      f"{str(reply.get('message', 'unknown'))[:250]}")
        return "world-editor-error"
    if request["op"] in EDIT_OPS:
        # Later edits invalidate any earlier MAP_SAVE marker. Persist them with
        # a new MAP_SAVE before this ephemeral runner terminates.
        (STATE / "session-saved-to-git").unlink(missing_ok=True)
    try:
        result_link = publish_world_asset(comment_id, "ai-result.json", "json")
        attachments = [f"Inspector JSON / coordinates: {result_link}"]
        if reply.get("blueprint"):
            cloud = publish_world_asset(comment_id, "ai-blueprint.ply", "ply")
            attachments.append(f"3D mesh-vertex point cloud (PLY): {cloud}")
        if reply.get("photo"):
            photo = publish_world_asset(comment_id, "ai-photo.png", "png")
            attachments.append(f"Genuine Unity object screenshot: {photo}")
        details = {key: reply.get(key) for key in (
            "op", "path", "position", "bounds", "partCount", "matches",
            "meshVertices", "sampledVertices", "treeWidthScale",
            "treeHeightScale", "treePrototype") if key in reply}
        issue_comment("AI map request #" + str(comment_id) +
                      ": executed by licensed Unity Editor.\n" +
                      "Summary: " + json.dumps(details, ensure_ascii=True)[:1100] +
                      "\n\n" + "\n".join(attachments) +
                      "\nEdits are saved on this runner; use MAP_SAVE to publish a Git branch.")
        return "world-published"
    except (OSError, subprocess.CalledProcessError, RuntimeError) as exc:
        issue_comment(f"AI map request #{comment_id}: editor operation succeeded, but "
                      f"publishing its evidence failed ({type(exc).__name__}).")
        return "world-publish-failed"

def process_save(comment_id):
    # Checkpoint is recoverable while base SHA is unchanged; Git branch is the durable source.
    checkpoint = subprocess.run(["flock", "-w", "60", "/tmp/aaris-project-checkpoint.lock",
                                sys.executable, "Tools/CI/session_store.py", "save", "project"],
                                capture_output=True, text=True, timeout=300, check=False)
    changes = subprocess.run(["git", "diff", "--name-only", "HEAD", "--", "Assets/"],
                             capture_output=True, text=True, timeout=20, check=True)
    files = [name for name in changes.stdout.splitlines() if name]
    approved = (".unity", ".prefab", ".asset", ".mat", ".meta")
    if any(not name.startswith("Assets/") or not name.endswith(approved) for name in files):
        raise RuntimeError("Refused to stage non-Unity source files")
    if len(files) > 50:
        raise RuntimeError("More than 50 changed Unity files; manual review required")
    if not files:
        issue_comment(f"MAP_SAVE #{comment_id}: no tracked Unity source edits to commit. "
                      f"Release checkpoint status: {checkpoint.returncode}.")
        return "world-no-changes"
    subprocess.run(["git", "add", "--", *files], check=True, timeout=60)
    run_id = os.environ.get("GITHUB_RUN_ID", "")
    if not run_id.isdigit():
        raise RuntimeError("Actions run ID missing")
    branch = "ai-world-session-" + run_id
    current = subprocess.run(["git", "branch", "--show-current"], capture_output=True,
                             text=True, check=True).stdout.strip()
    if current != branch:
        subprocess.run(["git", "switch", "-c", branch], check=True)
    subprocess.run(["git", "-c", "user.name=Aaris AI World",
                    "-c", "user.email=actions@users.noreply.github.com",
                    "commit", "-m", "Save owner-approved Unity AI scene edits"], check=True)
    subprocess.run(["git", "push", "origin", "HEAD:refs/heads/" + branch],
                   check=True, timeout=180)
    pr_url = f"https://github.com/{REPO}/compare/main...{branch}"
    opened = gh("pr", "view", branch, "--repo", REPO, "--json", "url", check=False)
    if opened.returncode == 0:
        try: pr_url = json.loads(opened.stdout)["url"]
        except (KeyError, ValueError): pass
    else:
        made = gh("pr", "create", "--repo", REPO, "--head", branch, "--base", "main",
                  "--title", "AI world session: Unity scene edits " + run_id,
                  "--body", "Owner-directed changes from a real licensed Unity Editor. "
                  "Review modified assets, LFS uploads and CI before merging.", check=False)
        if made.returncode == 0 and made.stdout.strip():
            pr_url = made.stdout.strip()
    (STATE / "session-saved-to-git").write_text(branch + "\n")
    issue_comment(f"MAP_SAVE #{comment_id}: saved {len(files)} changed Unity source files "
                   f"to Git branch `{branch}`.\nReview: {pr_url}\n"
                  f"Release checkpoint: {'ok' if checkpoint.returncode == 0 else 'failed'}."
                  "\nChanges are not automatically merged into main.")
    return "world-git-saved"

def process(comment):
    author = comment.get("user") or {}
    if author.get("login") != OWNER:
        return "ignored-non-owner"
    comment_id = comment.get("id")
    if type(comment_id) is not int or comment_id <= 0:
        return "ignored-invalid-comment-id"
    if comment.get("body", "").strip() == "ACTIVATE_UNITY":
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
                      "the authorized Unity CLI activation process.")
        return "activation-launch-failed"
    body = comment.get("body", "").strip()
    if body == "MAP_SAVE":
        try:
            return process_save(comment_id)
        except (OSError, RuntimeError, subprocess.CalledProcessError,
                subprocess.TimeoutExpired) as exc:
            issue_comment(f"MAP_SAVE #{comment_id}: save failed ({type(exc).__name__}). "
                          "No automatic merge was attempted.")
            return "world-save-failed"
    if body.startswith("MAP "):
        return process_world(comment_id, body)
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
        world_selftest()
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
    print("Starting owner-only camera polling on active Unity session", flush=True)
    start = dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    since = start
    seen = set()
    last_license_state = None
    allowed_license_states = {
        "installing_official_unity_cli", "starting_unity_oauth",
        "waiting_for_mobile_unity_signin", "activating_unity_personal",
        "unity_personal_activation_succeeded", "license_secret_installed",
        "unity_activation_requires_attention", "editor_starting",
        "installing_unity_hub_prerequisites", "waiting_for_unity_hub_signin",
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
                if outcome not in ("ignored-non-camera", "ignored-non-owner"):
                    print(f"Camera issue comment {comment_id}: {outcome}", flush=True)
        except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError) as exc:
            print("Issue relay transient error:", type(exc).__name__, flush=True)
        time.sleep(4)

if __name__ == "__main__":
    main()
