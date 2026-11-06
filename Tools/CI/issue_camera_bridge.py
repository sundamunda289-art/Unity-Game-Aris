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
            "installing_official_unity_cli", "installing_unity_hub_prerequisites",
            "waiting_for_unity_hub_signin", "starting_unity_oauth",
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
    if comment.get("body", "").startswith("AI "):
        return process_ai(comment)
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


# The same owner-only issue #77 is the AI interface. JSON is intentionally
# restricted to documented world operations, never file paths or shell commands.
AI_OPS = {"status", "nearby", "inspect", "parts", "outline", "photo", "move", "scale", "part_scale", "save"}
SAFE_TARGET = re.compile(r"^[\w /.:()\-]{1,250}$", re.UNICODE)

def parse_ai(body):
    if not isinstance(body, str) or len(body) > 2200 or not body.startswith("AI "):
        raise ValueError("Use AI followed by one JSON command (maximum 2200 characters)")
    try:
        command = json.loads(body[3:].strip())
    except json.JSONDecodeError as exc:
        raise ValueError("AI command must be valid JSON") from exc
    if not isinstance(command, dict):
        raise ValueError("AI request must be an object")
    allowed = {"op", "target", "part", "value", "radius", "scope"}
    if set(command) - allowed:
        raise ValueError("Unknown AI intent fields")
    op = command.get("op")
    if op not in AI_OPS:
        raise ValueError("Allowed AI operations: " + ", ".join(sorted(AI_OPS)))
    if op not in {"nearby", "save", "status"}:
        target = command.get("target")
        if not isinstance(target, str) or not SAFE_TARGET.fullmatch(target):
            raise ValueError("Target must be an exact name, scene:path or tree:tile:index")
        if target.startswith("tree:") and not re.fullmatch(r"tree:\d{1,2}:\d{1,8}", target):
            raise ValueError("Invalid native terrain tree identifier")
    elif "target" in command:
        raise ValueError("Target is not used for nearby/save/status")
    if "part" in command:
        if op != "part_scale" or not isinstance(command["part"], str) or not SAFE_TARGET.fullmatch(command["part"]):
            raise ValueError("Only part_scale accepts a relative child transform path")
        if ".." in command["part"] or command["part"].startswith("/"):
            raise ValueError("Unsafe child path")
    if op == "part_scale" and "part" not in command:
        raise ValueError("part_scale requires part path")
    if op in {"nearby", "move", "scale", "part_scale"}:
        v = command.get("value")
        if not isinstance(v, list) or len(v) != 3 or any(
                type(x) not in (int, float) or not __import__("math").isfinite(x) for x in v):
            raise ValueError("value requires exactly three finite numbers")
        if op == "nearby":
            if not (-1000 <= v[0] <= 1000 and -500 <= v[1] <= 4000 and -1000 <= v[2] <= 1000):
                raise ValueError("Nearby point must be inside 2km world")
            radius = command.get("radius", 35)
            if type(radius) not in (int, float) or not __import__("math").isfinite(radius) or not 1 <= radius <= 200:
                raise ValueError("Nearby radius must be 1..200 metres")
            command["radius"] = float(radius)
        elif op == "move":
            if any(abs(x) > 100 for x in v):
                raise ValueError("Move delta exceeds 100 metres")
        elif any(not 0.25 <= x <= 3 for x in v):
            raise ValueError("Relative scaling must be 0.25..3")
        if op == "scale" and str(command.get("target", "")).startswith("tree:") and abs(v[0] - v[2]) > 1e-6:
            raise ValueError("Native trees use equal X/Z width scaling")
    elif "value" in command or "radius" in command:
        raise ValueError("Unexpected coordinates or radius for this operation")
    if op == "part_scale" and str(command.get("target", "")).startswith("tree:"):
        if command.get("scope") != "prototype":
            raise ValueError("Tree leaf/branch changes affect all trees using a prefab; scope=prototype required")
    elif "scope" in command:
        raise ValueError("scope is only supported for native tree prototype part_scale")
    return command

def publish_ai_asset(comment_id, source, suffix):
    if not source.is_file() or source.stat().st_size < 10:
        raise RuntimeError("AI result asset is missing")
    if suffix == "png" and not source.read_bytes().startswith(b"\x89PNG\r\n\x1a\n"):
        raise RuntimeError("No genuine Unity PNG available")
    name = f"live-ai-{comment_id}.{suffix}"
    dest = STATE / name
    shutil.copyfile(source, dest)
    ensure_release()
    gh("release", "upload", RELEASE, str(dest), "--repo", REPO)
    return f"https://github.com/{REPO}/releases/download/{RELEASE}/{name}"

def blueprint_ply(report):
    # Standard ASCII point-cloud file: real Unity world-space XYZ measurements.
    # Deliberately does not invent mesh faces/triangles or leaf colors.
    points = report.get("points") or []
    if not points:
        raise ValueError("Unity returned no blueprint points")
    header = ["ply", "format ascii 1.0", "comment Aaris Unity exterior support point cloud",
              f"element vertex {len(points)}", "property float x",
              "property float y", "property float z", "end_header"]
    for p in points:
        coords = [float(p[k]) for k in ("x", "y", "z")]
        if not all(__import__("math").isfinite(v) for v in coords):
            raise ValueError("Invalid mesh coordinates")
        header.append(" ".join(f"{v:.6f}" for v in coords))
    return "\n".join(header) + "\n"

def blueprint_svg(report):
    # An actual dot projection of Unity-reported exterior mesh positions, not a
    # fabricated screenshot. No Pillow, model API or extra script is required.
    import html
    points = report.get("points") or []
    if not points:
        raise ValueError("Unity returned no exterior points")
    coords = [(float(p["x"]), float(p["y"]), float(p["z"])) for p in points[:256]]
    projected = [(0.707 * (x - z), 0.408 * (x + z) - 0.816 * y)
                 for x, y, z in coords]
    xs = [p[0] for p in projected]
    ys = [p[1] for p in projected]
    lowx, highx, lowy, highy = min(xs), max(xs), min(ys), max(ys)
    scale = min(930 / max(0.001, highx - lowx), 620 / max(0.001, highy - lowy))
    dot = []
    for x, y in projected:
        sx = 55 + (x - lowx) * scale
        sy = 700 - (y - lowy) * scale
        dot.append(f'<circle cx="{sx:.1f}" cy="{sy:.1f}" r="3.2"/>')
    size = report.get("size") or {}
    dims = ", ".join(f"{axis.upper()} {float(size.get(axis, 0)):.2f}m" for axis in ("x", "y", "z"))
    heading = html.escape(str(report.get("target", "Unity object"))[:100])
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="1050" height="780" viewBox="0 0 1050 780">'
            '<rect width="1050" height="780" fill="#081b27"/>'
            f'<text x="45" y="42" fill="#e9faff" font-size="20">{heading}</text>'
            f'<text x="45" y="70" fill="#8dddeb" font-size="15">{dims} | world-space mesh silhouette dots</text>'
            '<g fill="#60eac9" stroke="#8ee9f6" stroke-width=".5">' + "".join(dot) + '</g>'
            '<text x="45" y="750" fill="#b0cad2" font-size="13">Exterior support points from native Unity mesh; '
            'blueprint projection, NOT a rendered photo.</text></svg>')

def save_review_pr(comment_id):
    # Only the world source assets may be committed. Never add cloud state,
    # auth, Library, Logs or the .git database to a public review branch.
    tracked = gh("api", f"repos/{REPO}", check=True)
    if not tracked.stdout:
        raise RuntimeError("Cannot validate GitHub repository")
    subprocess.run(["git", "lfs", "install", "--local"], check=True,
                   capture_output=True, text=True)
    subprocess.run(["git", "config", "user.name", "github-actions[bot]"], check=True)
    subprocess.run(["git", "config", "user.email", "41898282+github-actions[bot]@users.noreply.github.com"], check=True)
    # Stage only exact edited native scene/TerrainData assets or prefab files,
    # not every imported asset in broad Unity directories.
    changed_worktree = subprocess.run(
        ["git", "diff", "--name-only", "-z", "--", "Assets"],
        check=True, capture_output=True).stdout
    candidates = [os.fsdecode(n) for n in changed_worktree.split(b"\0") if n]
    def native_asset(path):
        return (path == "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity"
                or (path.startswith("Assets/TerrainDemoScene_URP/Terrain/Data/")
                    and path.endswith(".asset"))
                or (path.startswith("Assets/") and path.endswith(".prefab")))
    selected = [p for p in candidates if native_asset(p)]
    if not selected:
        return "No eligible saved Unity scene/TerrainData/prefab changes to publish."
    subprocess.run(["git", "add", "--", *selected], check=True, capture_output=True, text=True)
    changed = subprocess.run(["git", "diff", "--cached", "--name-only", "-z"],
                             check=True, capture_output=True).stdout
    files = [os.fsdecode(n) for n in changed.split(b"\0") if n]
    if not files or any(not native_asset(p) for p in files):
        raise RuntimeError("Unexpected source path staged; refusing automatic publish")
    current_branch = subprocess.run(["git", "branch", "--show-current"], check=True,
                                    capture_output=True, text=True).stdout.strip()
    branch = current_branch if current_branch.startswith("ai/live-review-") else (
        "ai/live-review-" + os.environ.get("GITHUB_RUN_ID", str(comment_id)))
    if branch != current_branch:
        subprocess.run(["git", "switch", "-c", branch], check=True, capture_output=True, text=True)
    subprocess.run(["git", "diff", "--cached", "--check"], check=True, capture_output=True, text=True)
    subprocess.run(["git", "commit", "-m", f"feat(map): owner-reviewed live editor changes #{comment_id}"],
                   check=True, capture_output=True, text=True)
    subprocess.run(["git", "push", "origin", f"HEAD:refs/heads/{branch}"],
                   check=True, capture_output=True, text=True)
    url = f"https://github.com/{REPO}/compare/main...{branch}?expand=1"
    if current_branch != branch:
        result = gh("pr", "create", "--repo", REPO, "--base", "main", "--head", branch,
                    "--title", "AI Unity world changes from live editor (review required)",
                    "--body", "Native scene / terrain changes from owner-validated AI commands. "
                              "Review dimensions, collisions, LFS assets and Unity render before merging.",
                    check=False)
        if result.returncode == 0:
            url = result.stdout.strip()
    return "Saved and pushed " + str(len(files)) + " native files to review branch.\n" + url

def runtime_status():
    """Safe on-demand diagnostics while the Actions runner is alive.

    Never post a Unity sign-in URL, ULF, logs, tokens or arbitrary status detail
    into a public GitHub issue. A running Docker container is NOT a licensed Editor.
    """
    safe_stages = {
        "installing_official_unity_cli", "installing_unity_hub_prerequisites",
        "waiting_for_unity_hub_signin", "starting_unity_oauth",
        "waiting_for_mobile_unity_signin", "activating_unity_personal",
        "unity_personal_activation_succeeded", "license_secret_installed",
        "unity_activation_requires_attention", "editor_starting",
        "fetching_unity_editor"
    }
    stage = "not_reported"
    try:
        raw = json.loads((STATE / "license-status.json").read_text())
        value = raw.get("state")
        if isinstance(value, str) and value in safe_stages:
            stage = value
        else:
            stage = "unrecognized_or_unavailable"
    except (OSError, ValueError, TypeError):
        pass

    try:
        docker = subprocess.run(
            ["docker", "inspect", "--format", "{{.State.Running}}", "aaris-unity-live"],
            capture_output=True, text=True, check=False, timeout=5)
        docker_running = docker.returncode == 0 and docker.stdout.strip() == "true"
    except (OSError, subprocess.TimeoutExpired):
        docker_running = False

    # Check only the loopback TCP port. Never expose the VNC password or
    # Cloudflare URL, and do not confuse an open port with an Editor render.
    import socket
    try:
        with socket.create_connection(("127.0.0.1", 6080), timeout=2):
            vnc_running = True
    except OSError:
        vnc_running = False

    try:
        disk_free = f"{shutil.disk_usage('.').free / (1024**3):.1f} GiB"
    except OSError:
        disk_free = "unavailable"

    camera = camera_health()
    camera_ok = camera.get("state") == "Camera moved and real Unity PNG saved"
    last_ai = "none"
    try:
        data = json.loads((STATE / "ai-result.json").read_text())
        if data.get("op") in AI_OPS and data.get("state") in {"ok", "error"}:
            last_ai = str(data["op"]) + ":" + str(data["state"])
    except (OSError, ValueError, TypeError):
        pass

    return (
        "Aaris runner on-demand diagnostics (no secrets):\n"
        f"Unity bootstrap stage: {stage}\n"
        f"Unity Docker container running: {docker_running}\n"
        f"noVNC loopback port reachable: {vnc_running}\n"
        f"Last camera successfully reported a Unity PNG: {camera_ok}\n"
        f"Camera PNG file present: {(STATE / 'latest.png').is_file()}\n"
        f"Last AI command: {last_ai}\n"
        f"Temporary runner disk free: {disk_free}\n"
        "A running desktop, Docker, or a cached PNG does NOT prove that "
        "the current Unity Editor is licensed or rendering. "
        "Check the protected Cloudflare page for private activation guidance."
    )


def process_ai(comment):
    comment_id = comment["id"]
    try:
        command = parse_ai(comment.get("body", ""))
    except ValueError as error:
        issue_comment(f"AI request #{comment_id}: rejected: {error}")
        return "invalid-ai-intent"
    if command["op"] == "status":
        issue_comment(f"AI request #{comment_id}: " + runtime_status())
        return "runtime-status-reported"
    if command["op"] == "save":
        try:
            issue_comment(f"AI request #{comment_id}: " + save_review_pr(comment_id))
            return "review-branch-pushed"
        except (OSError, RuntimeError, subprocess.CalledProcessError) as exc:
            issue_comment(f"AI request #{comment_id}: PR save failed ({type(exc).__name__}). "
                          "Edits remain on the active runner; issue a new AI SAVE before it shuts down.")
            return "review-save-failed"
    command["nonce"] = comment_id
    request_file = STATE / "ai-command.json"
    temp = STATE / "ai-issue.tmp"
    temp.write_text(json.dumps(command))
    os.replace(temp, request_file)
    deadline = time.monotonic() + 110
    result = {}
    while time.monotonic() < deadline:
        time.sleep(2)
        path = STATE / "ai-result.json"
        try:
            result = json.loads(path.read_text()) if path.exists() else {}
        except (OSError, ValueError):
            result = {}
        if result.get("nonce") == comment_id:
            break
    if result.get("nonce") != comment_id:
        issue_comment(f"AI request #{comment_id}: Unity Editor did not respond within 110s. "
                      "Check the private Unity license/Editor state and Actions diagnostics.")
        return "ai-editor-timeout"
    if result.get("state") != "ok":
        detail = str(result.get("detail", "unknown"))[:240]
        issue_comment(f"AI request #{comment_id}: Editor error: {detail}")
        return "ai-editor-error"
    size, pos = result.get("size") or {}, result.get("position") or {}
    dims = " × ".join(f"{float(size.get(k, 0)):.2f}" for k in ("x","y","z")) + " m"
    coords = ", ".join(f"{float(pos.get(k, 0)):.2f}" for k in ("x","y","z"))
    message = (f"AI request #{comment_id}: {command['op'].upper()} succeeded.\n"
               f"Target: {result.get('target','nearby')}\n"
               f"Type: {result.get('kind','unknown')}\n"
               f"Position X,Y,Z: {coords} m\n"
               f"Bounding dimensions X×Y×Z: {dims}\n"
               f"Saved on runner: {result.get('saved',False)}\n"
               f"Changed asset: {result.get('asset','none')}\n")
    if result.get("detail"): message += "Detail: " + str(result["detail"])[:300] + "\n"
    if command["op"] == "nearby":
        message += f"Nearby count: {result.get('nearbyCount',0)}\n"
        message += "\n".join((result.get("nearby") or [])[:30]) + "\n"
    if command["op"] == "parts":
        parts = result.get("parts") or []
        message += f"Child part count: {result.get('partCount', len(parts))}\n"
        message += "Exact relative child paths (first 20):\n"
        message += "\n".join(str(part)[:210] for part in parts[:20]) + "\n"
        if len(parts) > 20:
            try:
                url = publish_ai_asset(comment_id, STATE / "ai-result.json", "json")
                message += f"More child paths and numeric measurements: {url}\n"
            except (OSError, RuntimeError, subprocess.CalledProcessError) as exc:
                message += f"Full parts report upload failed ({type(exc).__name__})\n"
    if command["op"] == "outline":
        try:
            json_url = publish_ai_asset(comment_id, STATE / "ai-result.json", "json")
            svg_file = STATE / "ai-outline.svg"
            svg_file.write_text(blueprint_svg(result))
            svg_url = publish_ai_asset(comment_id, svg_file, "svg")
            ply_file = STATE / "ai-outline.ply"
            ply_file.write_text(blueprint_ply(result))
            ply_url = publish_ai_asset(comment_id, ply_file, "ply")
            message += f"Actual Unity mesh exterior points: {result.get('pointCount',0)}\n3D points JSON: {json_url}\n3D point cloud PLY: {ply_url}\nDotted blueprint SVG: {svg_url}\n"
        except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError) as exc:
            message += "Blueprint upload failed: " + type(exc).__name__ + "\n"
    if command["op"] == "photo":
        try:
            url = publish_ai_asset(comment_id, STATE / "ai-photo.png", "png")
            message += "Verified real Unity photo: " + url + "\n"
        except (OSError, RuntimeError, subprocess.CalledProcessError) as exc:
            message += "Photo upload failed: " + type(exc).__name__ + "\n"
    if result.get("saved"):
        message += "Next: AI {\"op\":\"save\"} publishes a Git LFS-backed PR. Unsaved runner branches are temporary."
    issue_comment(message[:5500])
    return "ai-" + command["op"] + "-reported"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--seconds", type=int, default=4800)
    args = parser.parse_args()
    if args.selftest:
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
        assert parse_ai('AI {"op":"status"}') == {"op": "status"}
        assert parse_ai('AI {"op":"parts","target":"tree:0:123"}')["op"] == "parts"
        good = parse_ai('AI {"op":"outline","target":"tree:0:123"}')
        assert good["target"] == "tree:0:123"
        assert parse_ai('AI {"op":"part_scale","target":"tree:0:123","part":"Leaves","value":[1.1,1,1.1],"scope":"prototype"}')["scope"] == "prototype"
        assert parse_ai('AI {"op":"nearby","value":[420,0,-520],"radius":35}')["radius"] == 35
        for bad in ('AI {"op":"status","target":"tree:0:1"}',
                    'AI {"op":"status","command":"ls"}',
                    'AI {"op":"shell","target":"foo"}',
                    'AI {"op":"move","target":"Tree","value":[1000,0,0]}',
                    'AI {"op":"scale","target":"tree:0:9","value":[1,1,2]}',
                    'AI {"op":"part_scale","target":"X","part":"../private","value":[1,1,1]}',
                    'AI {"op":"part_scale","target":"tree:0:3","part":"Leaves","value":[1,1,1]}',
                    'AI {"op":"save","command":"rm"}'):
            try: parse_ai(bad)
            except ValueError: pass
            else: raise AssertionError("Unsafe AI intent was accepted")
        assert "element vertex 1" in blueprint_ply({"points":[{"x":1,"y":2,"z":3}]})
        print("Owner-only GitHub issue camera/AI intent parser selftest: PASS")
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
            if run_id.isdigit() and mins in ("15", "45", "90", "300"):
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
        "installing_official_unity_cli", "installing_unity_hub_prerequisites",
        "waiting_for_unity_hub_signin", "starting_unity_oauth",
        "waiting_for_mobile_unity_signin", "activating_unity_personal",
        "unity_personal_activation_succeeded", "license_secret_installed",
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
