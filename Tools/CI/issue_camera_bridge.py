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
    if isinstance(comment.get("body"), str) and comment["body"].startswith("AGENT "):
        return process_agent(comment)
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


# JSON intents are deliberately constrained; GitHub issue text is never a shell command.
AGENT_ACTIONS = {"health", "scan", "describe", "move", "scale", "photo", "save"}
DRAFT_REF = "ai-map-draft"
MAP_ASSETS = ("Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity",
              "Assets/TerrainDemoScene_URP/Terrain/Data/")

def parse_agent(body):
    if not isinstance(body, str) or not body.startswith("AGENT ") or len(body) > 1800:
        raise ValueError("Expected AGENT plus compact JSON (maximum 1800 characters)")
    def unique_pairs(pairs):
        obj = {}
        for k, v in pairs:
            if k in obj: raise ValueError("Duplicate JSON field")
            obj[k] = v
        return obj
    try:
        obj = json.loads(body[6:], object_pairs_hook=unique_pairs,
                         parse_constant=lambda _: (_ for _ in ()).throw(ValueError("Non-finite number")))
    except (ValueError, TypeError) as exc:
        raise ValueError("Invalid agent JSON") from exc
    if not isinstance(obj, dict) or obj.get("action") not in AGENT_ACTIONS:
        raise ValueError("Unsupported agent action")
    action = obj["action"]
    keys = {"health":{"action"}, "save":{"action"},
            "scan":{"action","x","z","radius"},
            "describe":{"action","target"}, "photo":{"action","target"},
            "move":{"action","target","x","y","z","apply"},
            "scale":{"action","target","x","y","z","apply"}}[action]
    if set(obj) - keys:
        raise ValueError("Unexpected agent fields")
    if action in {"describe", "move", "scale", "photo"}:
        target = obj.get("target")
        if (not isinstance(target,str) or not 1<=len(target)<=180
            or any(ord(c)<32 or ord(c)>126 or c in "<>{}$" for c in target)):
            raise ValueError("Invalid or unsafe target name")
    if action in {"move","scale"} and type(obj.get("apply",False)) is not bool:
        raise ValueError("apply must be boolean")
    import math
    fields = {"x","z","radius"} if action=="scan" else (
        {"x","y","z"} if action in {"move","scale"} else set())
    for key in fields:
        v=obj.get(key)
        if type(v) not in (float,int) or not math.isfinite(v):
            raise ValueError("Invalid number: "+key)
    if action=="scan" and not (-1000<=obj["x"]<=1000 and -1000<=obj["z"]<=1000
                               and 1<=obj["radius"]<=150):
        raise ValueError("Scan out of bounds")
    if action=="move" and not (-1000<=obj["x"]<=1000 and -500<=obj["y"]<=4000
                               and -1000<=obj["z"]<=1000):
        raise ValueError("Move out of bounds")
    if action=="scale" and any(not .2<=obj[k]<=3 for k in ("x","y","z")):
        raise ValueError("Scale factor out of bounds")
    obj.setdefault("apply",False)
    return obj

def checkpoint_map():
    """Push only tracked scene + native terrain edits to a review draft branch."""
    branch = subprocess.run(["git","branch","--show-current"],text=True,
                            capture_output=True,check=True).stdout.strip()
    if branch != DRAFT_REF:
        raise RuntimeError("Checkpoint requires dedicated ai-map-draft branch")
    subprocess.run(["git","add","-u","--",*MAP_ASSETS],check=True,capture_output=True)
    paths = subprocess.run(["git","diff","--cached","--name-only"],check=True,
                           capture_output=True,text=True).stdout.splitlines()
    if any(not (p==MAP_ASSETS[0] or p.startswith(MAP_ASSETS[1])) for p in paths):
        raise RuntimeError("Unexpected file staged")
    if not paths: return "no-changes"
    subprocess.run(["git","commit","-m","checkpoint(map): save reviewed AI Unity edits"],
                   check=True,capture_output=True)
    subprocess.run(["git","push","origin","HEAD:refs/heads/"+DRAFT_REF],
                   check=True,capture_output=True,timeout=180)
    return "pushed-"+str(len(paths))+"-files"

def agent_health():
    try: return json.loads((STATE/"agent-report.json").read_text())
    except (OSError,ValueError): return {}

def publish_agent_report(comment_id):
    source=STATE/"agent-report.json"
    if not source.is_file() or source.stat().st_size>250000:
        raise RuntimeError("Missing/oversized AI report")
    obj=json.loads(source.read_text())
    if obj.get("nonce")!=comment_id or obj.get("source")!="licensed_unity_editor_scene":
        raise RuntimeError("Incorrect Editor report identity")
    filename="agent-report-"+str(comment_id)+".json"
    dest=STATE/filename
    shutil.copyfile(source,dest)
    ensure_release()
    gh("release","upload",RELEASE,str(dest),"--repo",REPO)
    return f"https://github.com/{REPO}/releases/download/{RELEASE}/{filename}"

def process_agent(comment):
    comment_id=comment["id"]
    try: payload=parse_agent(comment.get("body",""))
    except ValueError as exc:
        issue_comment(f"AI map #{comment_id}: rejected: {str(exc)[:220]}")
        return "rejected"
    payload["nonce"]=comment_id
    tmp=STATE/"agent-request.tmp"
    tmp.write_text(json.dumps(payload,separators=(",",":")))
    os.replace(tmp,STATE/"agent-request.json")
    deadline=time.monotonic()+115
    result={}
    while time.monotonic()<deadline:
        time.sleep(2)
        result=agent_health()
        if result.get("nonce")==comment_id: break
    if result.get("nonce")!=comment_id:
        issue_comment(f"AI map #{comment_id}: Unity Editor did not respond in 115s; "
                      "check actual license, import progress and Actions diagnostics.")
        return "editor-timeout"
    if result.get("state")!="ok":
        issue_comment(f"AI map #{comment_id}: Unity rejected: {str(result.get('note','error'))[:300]}")
        return "unity-error"
    try:
        url=publish_agent_report(comment_id)
        summary=f"AI map #{comment_id}: {payload['action']} processed in Unity Editor.\n"
        item=result.get("selected")
        if item:
            summary+="Object: "+str(item.get("name",""))[:80]+"\n"
            summary+="Dimensions metres: "+json.dumps(item.get("dimensions_m"))+"\n"
        if payload["action"]=="scan":
            summary+="Nearby items: "+str(len(result.get("nearby") or []))+"\n"
        if result.get("applied"):
            summary+="Edit saved to local native scene/TerrainData; Git draft checkpoint pending.\n"
        summary+="Full AI-readable result: "+url
        if result.get("genuine_unity_png") and result.get("photo_file")=="agent-latest.png":
            summary+="\nReal Unity PNG: "+publish_photo(comment_id,STATE/"agent-latest.png")
        if payload["action"]=="save":
            summary+="\nGit draft checkpoint: "+checkpoint_map()
        issue_comment(summary)
        return "agent-reported"
    except (OSError,RuntimeError,ValueError,subprocess.CalledProcessError) as exc:
        issue_comment(f"AI map #{comment_id}: Editor finished but report/checkpoint "
                      f"upload failed ({type(exc).__name__}). Check runner diagnostics.")
        return "artifact-error"

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--checkpoint", action="store_true")
    parser.add_argument("--seconds", type=int, default=4800)
    args = parser.parse_args()
    if args.checkpoint:
        print("Git checkpoint:", checkpoint_map())
        return
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
        assert parse_agent('AGENT {"action":"scan","x":0,"z":0,"radius":25}')["radius"]==25
        assert parse_agent('AGENT {"action":"move","target":"tree:tile:0","x":1,"y":1,"z":2}')["apply"] is False
        for unsafe in ('AGENT {"action":"shell","command":"ls"}',
                       'AGENT {"action":"scan","x":NaN,"z":0,"radius":12}',
                       'AGENT {"action":"scan","x":0,"z":0,"radius":500}',
                       'AGENT {"action":"move","target":"Tree","x":0,"y":0,"z":0,"apply":"yes"}',
                       'AGENT {"action":"save","code":"danger"}'):
            try: parse_agent(unsafe)
            except ValueError: pass
            else: raise AssertionError("Unsafe agent command accepted")
        print("GitHub owner-only camera and validated map agent protocol: PASS")
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
            if run_id.isdigit() and mins in ("15", "45", "90"):
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
        "fetching_unity_editor"
    }
    until = time.monotonic() + max(15, min(args.seconds, 5400))
    next_checkpoint=time.monotonic()+240
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
        if time.monotonic()>=next_checkpoint:
            try: print("Periodic Git checkpoint:",checkpoint_map(),flush=True)
            except (OSError,RuntimeError,subprocess.CalledProcessError) as exc:
                print("Checkpoint failed:",type(exc).__name__,flush=True)
            next_checkpoint=time.monotonic()+240
        time.sleep(4)

if __name__ == "__main__":
    main()
