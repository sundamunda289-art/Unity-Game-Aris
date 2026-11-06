#!/usr/bin/env python3
"""Safe GitHub Issue -> Unity Editor intent gateway and LFS-aware review checkpoint.

Public issue #77, repository owner only. Never run caller-provided code, paths,
shell, APIs, Editor menu items or arbitrary Git refs. Unity renders and edits
inside the currently running, licensed Editor; this module only brokers JSON.
"""
import argparse
import datetime as dt
import shutil
import json
import math
import os
from pathlib import Path
import re
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]
STATE = ROOT / "ci-live-control"
REPO = "sundamunda289-art/Unity-Game-Aris"
ISSUE = 77
OWNER = "sundamunda289-art"
ALLOWED = {"inspect", "outline", "move", "scale", "resize", "capture", "tint_shared", "part_scale_shared", "save"}
MUTATING = {"move", "scale", "resize", "tint_shared", "part_scale_shared"}
COMMON = {"action", "id"}
FIELDS = {
    "inspect": COMMON | {"query", "x", "y", "z", "radius", "limit"},
    "outline": COMMON,
    "move": COMMON | {"dx", "dy", "dz"},
    "scale": COMMON | {"sx", "sy", "sz"},
    "resize": COMMON | {"axis", "value"},
    "capture": COMMON | {"x", "y", "z", "yaw", "pitch", "distance"},
    "tint_shared": COMMON | {"part", "hex", "shared"},
    "part_scale_shared": COMMON | {"part", "sx", "sy", "sz", "shared"},
    "save": {"action"},
}
BOUNDS = {
    "x": (-1000, 1000), "y": (-500, 4000), "z": (-1000, 1000),
    "dx": (-50, 50), "dy": (-50, 50), "dz": (-50, 50),
    "sx": (0.2, 3), "sy": (0.2, 3), "sz": (0.2, 3),
    "radius": (0, 250), "limit": (1, 30),
    "value": (0, 100), "yaw": (-360, 360), "pitch": (-85, 85),
    "distance": (2, 500),
}
DEFAULTS = {
    "id": "", "query": "", "part": "", "hex": "", "axis": "",
    "x": 0, "y": 0, "z": 0, "dx": 0, "dy": 0, "dz": 0,
    "sx": 1, "sy": 1, "sz": 1,
    "radius": 35, "limit": 10, "value": 0,
    "yaw": 0, "pitch": 20, "distance": 22, "shared": False,
}
ID_RE = re.compile(r"(?:GlobalObjectId_V1-[0-9A-Fa-f-]+|terrain:[0-9]+:[0-9]+)\Z")
HEX_RE = re.compile(r"#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\Z")
CHANGED_ASSETS = STATE / "ai-changed-assets.json"



def no_duplicates(pairs):
    obj = {}
    for key, val in pairs:
        if key in obj:
            raise ValueError("Duplicate JSON field: " + key)
        obj[key] = val
    return obj


def parse(body):
    """Read a single AI <JSON object> comment; JSON is data, never executable."""
    if not isinstance(body, str) or not body.startswith("AI ") or len(body) > 1300:
        raise ValueError("Expected: AI {\"action\":\"inspect\",...}")
    try:
        request = json.loads(body[3:], object_pairs_hook=no_duplicates,
                             parse_constant=lambda _: (_ for _ in ()).throw(ValueError("Non-finite JSON")))
    except (json.JSONDecodeError, TypeError) as exc:
        raise ValueError("Malformed AI JSON") from exc
    if not isinstance(request, dict) or request.get("action") not in ALLOWED:
        raise ValueError("Unknown or missing AI action")
    action = request["action"]
    if set(request) - FIELDS[action]:
        raise ValueError("Unexpected fields for " + action)
    if action not in {"inspect", "capture", "save"} and not request.get("id"):
        raise ValueError("A precise object id from INSPECT is required")
    if request.get("id") and (not isinstance(request["id"], str) or
                              not ID_RE.fullmatch(request["id"])):
        raise ValueError("Invalid object id")
    clean = dict(DEFAULTS)
    clean.update(request)
    for field in ("query", "part", "axis", "hex"):
        val = clean[field]
        if not isinstance(val, str) or len(val) > 80 or any(ord(c) < 32 for c in val):
            raise ValueError("Invalid text field " + field)
    for field, (lo, hi) in BOUNDS.items():
        val = clean[field]
        if isinstance(val, bool) or not isinstance(val, (int, float)) or not math.isfinite(val) or not lo <= val <= hi:
            raise ValueError("Invalid/out-of-range number: " + field)
    if isinstance(clean["limit"], float) and not clean["limit"].is_integer():
        raise ValueError("limit must be an integer")
    clean["limit"] = int(clean["limit"])
    if action == "scale" and not {"sx", "sy", "sz"} <= set(request):
        raise ValueError("scale needs sx, sy and sz, relative factors")
    if action == "resize" and (clean["axis"] not in ("x", "y", "z") or "value" not in request or clean["value"] < 0.05):
        raise ValueError("resize needs axis=x|y|z and value in metres")
    if action == "part_scale_shared":
        if clean["shared"] is not True or not clean["part"] or not {"sx", "sy", "sz"} <= set(request):
            raise ValueError("part_scale_shared needs shared:true, part name, sx/sy/sz factors")
    if action == "tint_shared":
        if clean["shared"] is not True or not clean["part"] or not HEX_RE.fullmatch(clean["hex"]):
            raise ValueError("tint_shared needs shared:true, named part, valid #RRGGBB colour")
    if action == "save":
        return {"action": "save"}
    clean["action"] = action
    return clean


def gh(*args):
    return subprocess.run(("gh", *args), cwd=ROOT, text=True,
                          capture_output=True, check=True).stdout.strip()


def git(*args):
    return subprocess.run(("git", *args), cwd=ROOT, text=True,
                          capture_output=True, check=True).stdout.strip()


def run_id():
    value = os.environ.get("GITHUB_RUN_ID", "")
    if not value.isdigit() or not 1 <= len(value) <= 16:
        raise RuntimeError("This checkpoint requires a live GitHub Actions run")
    return value


def allowed_changed_asset(path):
    """Single existing Unity source asset, not a credential, cache or .meta."""
    return (isinstance(path, str) and path.startswith("Assets/") and
            len(path) < 300 and not any(x in path for x in ("\\", "\n", "\r")) and
            ".." not in Path(path).parts and
            Path(path).suffix in (".unity", ".asset", ".mat", ".prefab") and
            (ROOT / path).is_file())


def remember_modified(path):
    if not allowed_changed_asset(path):
        raise ValueError("Editor returned a non-allowlisted modified asset")
    try:
        previous = json.loads(CHANGED_ASSETS.read_text(encoding="utf-8"))
        if not isinstance(previous, list):
            previous = []
    except (OSError, ValueError):
        previous = []
    paths = sorted(set(previous + [path]))
    CHANGED_ASSETS.write_text(json.dumps(paths), encoding="utf-8")
    (STATE / "ai-mutated.marker").write_text(path, encoding="utf-8")


def checkpoint():
    """Git LFS data goes to a run-specific review branch, NEVER directly to main."""
    if os.environ.get("GITHUB_REPOSITORY") != REPO:
        raise RuntimeError("Repository identity mismatch")
    if not (STATE / "ai-mutated.marker").is_file():
        return "No AI edits to checkpoint"
    ident = run_id()
    branch = "ai-session/" + ident
    git("config", "user.name", "github-actions[bot]")
    git("config", "user.email", "41898282+github-actions[bot]@users.noreply.github.com")
    # Fail closed: only already-tracked Asset sources. This never stages secrets,
    # generated .meta files, logs, project auth, Library, or untracked files.
    git("lfs", "install", "--local")
    paths = json.loads(CHANGED_ASSETS.read_text(encoding="utf-8"))
    if not isinstance(paths, list) or not paths or not all(allowed_changed_asset(p) for p in paths):
        raise RuntimeError("Invalid or missing Editor-edited asset list")
    # Stage ONLY files identified as changed by the real Unity Editor.
    git("add", "--", *paths)
    changed = git("diff", "--cached", "--name-only").splitlines()
    if not changed:
        (STATE / "ai-mutated.marker").unlink(missing_ok=True)
        CHANGED_ASSETS.unlink(missing_ok=True)
        return "No staged Unity asset changes"
    if any(not s.startswith("Assets/") or
           s.endswith(".meta") or ".." in Path(s).parts for s in changed):
        raise RuntimeError("Unexpected staged path; checkpoint cancelled")
    # Checkout stays at the original run commit; never rewrite existing PRs.
    current = git("branch", "--show-current")
    if current != branch:
        git("switch", "-c", branch)
    git("commit", "-m", "AI editor-reviewed map changes from run " + ident)
    git("push", "-u", "origin", "HEAD:refs/heads/" + branch)
    pr = gh("pr", "list", "--repo", REPO, "--head", branch,
            "--json", "url", "--jq", ".[0].url")
    if not pr:
        pr = gh("pr", "create", "--repo", REPO, "--base", "main", "--head", branch,
                "--title", "Review AI Unity scene/terrain edits (run " + ident + ")",
                "--body", "Bounded AI commands changed the licensed Unity Editor's saved map.\n"
                          "Review geometry, nearby objects, real screenshots and LFS diffs "
                          "before merging. No credentials or Library cache included.")
    (STATE / "ai-mutated.marker").unlink(missing_ok=True)
    CHANGED_ASSETS.unlink(missing_ok=True)
    return "Saved " + str(len(changed)) + " tracked asset(s) to review PR: " + pr


def publish_report(comment_id, result):
    STATE.mkdir(parents=True, exist_ok=True)
    path = STATE / ("ai-report-" + str(comment_id) + ".json")
    path.write_text(json.dumps(result, indent=2), encoding="utf-8")
    # Live photographer created this public release for genuine visual data.
    # Reports contain only inspected map data, not private Unity login status.
    existing = subprocess.run(("gh", "release", "view", "live-photos", "--repo", REPO),
                              cwd=ROOT, capture_output=True, check=False)
    if existing.returncode:
        gh("release", "create", "live-photos", "--repo", REPO,
           "--target", os.environ.get("GITHUB_SHA", "main"),
           "--title", "Aaris verified Unity editor reports",
           "--notes", "Public geometry/scene data and genuine Unity screenshots.")
    gh("release", "upload", "live-photos", str(path), "--repo", REPO)
    return "https://github.com/" + REPO + "/releases/download/live-photos/" + path.name


def handle(comment, comment_cb, photo_cb):
    """Called only after issue_camera_bridge verifies author==owner and issue==77."""
    comment_id = comment["id"]
    try:
        q = parse(comment["body"].strip())
    except ValueError as exc:
        comment_cb("AI request #" + str(comment_id) + " rejected: " + str(exc))
        return "ai-invalid"
    if q["action"] == "save":
        try:
            detail = checkpoint()
            comment_cb("AI checkpoint #" + str(comment_id) + ": " + detail)
            return "ai-saved"
        except (OSError, subprocess.CalledProcessError, RuntimeError) as exc:
            comment_cb("AI checkpoint #" + str(comment_id) +
                       " FAILED (" + type(exc).__name__ + "). Inspect Actions logs.")
            return "ai-save-failed"
    q["nonce"] = comment_id
    STATE.mkdir(parents=True, exist_ok=True)
    temp = STATE / "ai-command.tmp"
    temp.write_text(json.dumps(q), encoding="utf-8")
    os.replace(temp, STATE / "ai-command.json")
    result_path = STATE / "ai-result.json"
    result = {}
    deadline = time.monotonic() + 110
    while time.monotonic() < deadline:
        time.sleep(1.5)
        try:
            candidate = json.loads(result_path.read_text(encoding="utf-8"))
            if candidate.get("nonce") == comment_id:
                result = candidate
                break
        except (OSError, ValueError):
            pass
    if not result:
        comment_cb("AI request #" + str(comment_id) +
                   ": No licensed Editor response within 110 seconds. "
                   "Check protected Unity license/Editor status; request was not verified.")
        return "ai-unity-timeout"
    state = result.get("state", "error")
    if state == "ok" and q["action"] in MUTATING:
        try:
            remember_modified(result.get("modifiedAsset"))
        except ValueError:
            state = "untrusted-asset-path"
            result["state"] = state
            result["message"] = "Editor reported an invalid modified asset path; not checkpointed"
    # Full mesh dots/metadata are published as a downloadable JSON file for AI.
    try:
        report = publish_report(comment_id, result)
    except (OSError, subprocess.CalledProcessError) as exc:
        report = "Report upload failed (" + type(exc).__name__ + ")"
    description = str(result.get("message", ""))[:170].replace("\n", " ")
    summary = ("AI request #" + str(comment_id) + ": " + state +
               "; action=" + q["action"] +
               "; matched=" + str(result.get("matched", 0)) +
               "\n" + description + "\nFull 3D geometry/report: " + report)
    if state == "ok" and q["action"] == "capture":
        try:
            url = photo_cb(comment_id, STATE / "latest.png")
            summary += "\nGenuine Unity screenshot: " + url
        except (OSError, RuntimeError, subprocess.CalledProcessError) as exc:
            summary += "\nPhoto publication FAILED: " + type(exc).__name__
            state = "photo-publish-failed"
    if state == "ok" and q["action"] in MUTATING:
        summary += "\nSaved in Editor; post AI {\"action\":\"save\"} for a Git LFS review PR."
    comment_cb(summary)
    return "ai-" + state



STAGES = (
    "storage_cleaned", "vnc_ready", "cloudflare_ready",
    "issue_relay_ready", "unity_container_started",
    "unity_bootstrap_checked", "session_finished", "checkpoint_attempted",
)
STAGE_PATH = STATE / "ai-stage-log.json"


def mark_stage(label):
    if label not in STAGES:
        raise ValueError("Unknown workflow stage")
    STATE.mkdir(parents=True, exist_ok=True)
    try:
        stages = json.loads(STAGE_PATH.read_text(encoding="utf-8"))
        if not isinstance(stages, list):
            stages = []
    except (OSError, ValueError):
        stages = []
    stages.append({"stage": label, "utc": dt.datetime.now(dt.timezone.utc).isoformat()})
    tmp = STATE / "ai-stage-log.tmp"
    tmp.write_text(json.dumps(stages[-60:], indent=2), encoding="utf-8")
    os.replace(tmp, STAGE_PATH)


def diagnostics():
    """Only an allowlisted, credential-free debug report is exported."""
    def safe_json(name):
        try:
            data = json.loads((STATE / name).read_text(encoding="utf-8"))
            return data if isinstance(data, dict) else {}
        except (OSError, ValueError):
            return {}

    def state_value(obj):
        value = obj.get("state", "not_started")
        return value[:100] if isinstance(value, str) and re.fullmatch(r"[A-Za-z0-9_ -]{1,100}", value) else "redacted"

    def running_container():
        try:
            check = subprocess.run(
                ["docker", "inspect", "--format", "{{.State.Running}}", "aaris-unity-live"],
                capture_output=True, text=True, timeout=4, check=False)
            return check.returncode == 0 and check.stdout.strip() == "true"
        except (OSError, subprocess.TimeoutExpired):
            return False

    try:
        disk = shutil.disk_usage(ROOT)
        disk_report = {"total_gb": round(disk.total / (1024**3), 2),
                       "free_gb": round(disk.free / (1024**3), 2)}
    except OSError:
        disk_report = {"status": "unavailable"}
    scene = ROOT / "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity"
    try:
        native_scene = scene.is_file() and b"git-lfs.github.com/spec" not in scene.read_bytes()[:120]
    except OSError:
        native_scene = False
    status = safe_json("status.json")
    last = safe_json("ai-result.json")
    lic = safe_json("license-status.json")
    try:
        stages = json.loads(STAGE_PATH.read_text(encoding="utf-8"))
        stages = stages[-60:] if isinstance(stages, list) else []
    except (OSError, ValueError):
        stages = []
    report = {
        "utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "github_run_id": os.environ.get("GITHUB_RUN_ID", "")[:20],
        "workflow": "aaris-live-unity",
        "note": "Only safe diagnostics; green verification is not proof of a running licensed Unity Editor.",
        "disk": disk_report,
        "stages": stages,
        "native_scene_lfs_hydrated": native_scene,
        "unity_container_running": running_container(),
        "virtual_screen_started": (Path("/tmp/aaris-xvfb.pid").exists()),
        "vnc_server_log_present": Path("/tmp/aaris-x11vnc.log").exists(),
        "cloudflare_tunnel_started": Path("/tmp/aaris-tunnel.pid").exists(),
        "issue_relay_started": Path("/tmp/aaris-issue-bridge.pid").exists(),
        "license_state": state_value(lic),
        "last_camera_state": state_value(status),
        "last_ai_state": state_value(last),
        "last_ai_action": str(last.get("action", "none"))[:40],
        "ai_unsaved_changes": (STATE / "ai-mutated.marker").exists(),
        "real_photo_png_exists": (STATE / "latest.png").is_file(),
    }
    folder = ROOT / "ci-diagnostics"
    folder.mkdir(exist_ok=True)
    out = folder / "ai-debug-report.json"
    out.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print("Sanitized AI debug report ready:", out.relative_to(ROOT))
    print("Stages:", ",".join(x.get("stage", "?") for x in stages))
    return str(out)


def selftest():
    valid = parse('AI {"action":"inspect","query":"Tree","x":120,"z":-44,"radius":25}')
    assert valid["sx"] == 1 and valid["radius"] == 25
    assert parse('AI {"action":"scale","id":"terrain:1:4","sx":1.3,"sy":0.8,"sz":1.3}')["sx"] == 1.3
    assert parse('AI {"action":"save"}') == {"action": "save"}
    for body in [
        'AI {"action":"shell","cmd":"rm -rf /"}',
        'AI {"action":"move","id":"terrain:0:0","dx":900}',
        'AI {"action":"outline","id":"../../etc/passwd"}',
        'AI {"action":"resize","id":"terrain:0:0","axis":"w","value":1}',
        'AI {"action":"scale","id":"terrain:0:0","sx":1.4,"sy":1.4}',
        'AI {"action":"tint_shared","id":"terrain:0:0","part":"leaf","hex":"#fff","shared":true}',
        'AI {"action":"part_scale_shared","id":"terrain:0:0","part":"Leaves","sx":3.5,"sy":1,"sz":1,"shared":true}',
        'AI {"action":"inspect","x":NaN}',
        'AI {"action":"inspect","x":4,"x":5}',
        'AI {"action":"inspect","cmd":"anything"}',
        'AI {"action":"inspect","radius":999}',
    ]:
        try:
            parse(body)
        except ValueError:
            pass
        else:
            raise AssertionError("Unsafe request accepted: " + body)
    for bad in ("Assets/../../secrets.ulf", "Library/cache.asset", "Assets/Test.cs", "Assets/Test.meta"):
        assert not allowed_changed_asset(bad)
    print("Aaris bounded AI parser, native tree scaling and checkpoint intent validation: PASS")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=["selftest", "checkpoint", "stage", "diagnostics"])
    parser.add_argument("stage_name", nargs="?", default="")
    args = parser.parse_args()
    if args.action == "selftest":
        selftest()
    elif args.action == "checkpoint":
        print(checkpoint())
    elif args.action == "stage":
        mark_stage(args.stage_name)
    elif args.action == "diagnostics":
        diagnostics()
