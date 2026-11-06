#!/usr/bin/env python3
"""Bounded GitHub Issue -> live Unity Editor AI intents.

The only external input is owner-authored JSON prefixed "AI ". No user command
ever becomes a shell command, Python expression, asset path or C# snippet.
Reports are public because the map repository is public. OAuth/license data
never enters these files or releases.
"""
import html
import json
import math
import os
from pathlib import Path
import re
import shutil
import subprocess
import time

OPS = {
    "find": {"query"},
    "inspect": {"x", "y", "z", "radius"},
    "bounds": {"target"},
    "children": {"target"},
    "outline": {"target", "max_points"},
    "move": {"target", "dx", "dy", "dz", "dry_run"},
    "scale": {"target", "sx", "sy", "sz", "dry_run"},
    "size": {"target", "axis", "meters", "dry_run"},
    "tint": {"target", "hex", "dry_run"},
    "photo": {"target", "distance"},
}
REQUIRED = {
    "find": {"query"}, "inspect": {"x", "y", "z", "radius"},
    "bounds": {"target"}, "children": {"target"}, "outline": {"target"},
    "move": {"target", "dx", "dy", "dz"},
    "scale": {"target", "sx", "sy", "sz"},
    "size": {"target", "axis", "meters"},
    "tint": {"target", "hex"},
    "photo": {"target"},
}
RANGES = {
    "x": (-2500, 2500), "y": (-5000, 5000), "z": (-2500, 2500),
    "radius": (1, 200), "dx": (-100, 100), "dy": (-100, 100),
    "dz": (-100, 100), "sx": (0.25, 4), "sy": (0.25, 4),
    "sz": (0.25, 4), "distance": (2, 200),
    "max_points": (8, 2048), "meters": (0.05, 100),
}
EDIT_BRANCH = "ai-map-edits"
SCENE = "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity"
TERRAIN_DIR = "Assets/TerrainDemoScene_URP/Terrain/Data/"

def parse(body, nonce):
    if not isinstance(body, str) or not body.startswith("AI "):
        raise ValueError('Use AI {"op":"find","query":"Tree"}')
    if len(body) > 1600:
        raise ValueError("AI issue comment too large")
    try:
        data = json.loads(body[3:])
    except json.JSONDecodeError as exc:
        raise ValueError("Malformed AI JSON") from exc
    if not isinstance(data, dict) or data.get("op") not in OPS:
        raise ValueError("Unknown AI operation")
    op = data["op"]
    expected = OPS[op] | {"op"}
    if not REQUIRED[op] <= set(data) or not set(data) <= expected:
        raise ValueError("Missing/unknown AI fields")
    clean = {"op": op, "nonce": int(nonce)}
    for key, val in data.items():
        if key == "op":
            continue
        if key == "dry_run":
            if type(val) is not bool:
                raise ValueError("dry_run must be true/false")
        elif key == "axis":
            if type(val) is not str or val not in ("x", "y", "z"):
                raise ValueError("axis must be x, y or z")
        elif key == "hex":
            if type(val) is not str or not re.fullmatch(r"#[0-9a-fA-F]{6}", val):
                raise ValueError("hex must be a 6-digit #RRGGBB color")
        elif key in RANGES:
            lo, hi = RANGES[key]
            if type(val) not in (int, float) or not math.isfinite(val) or not lo <= val <= hi:
                raise ValueError("Out-of-range numeric field " + key)
            if key == "max_points":
                if type(val) is not int:
                    raise ValueError("max_points must be integer")
            else:
                val = float(val)
        else:
            maximum = 80 if key == "query" else 300
            if (not isinstance(val, str) or not val.strip() or
                len(val) > maximum or any(ord(ch) < 32 for ch in val)):
                raise ValueError("Invalid " + key)
        clean[key] = val
    if op == "scale" and clean["sx"] != clean["sz"] and str(clean["target"]).startswith("terrain-tree:"):
        raise ValueError("Native tree horizontal scale needs sx == sz")
    clean.setdefault("dry_run", False)
    clean.setdefault("max_points", 512)
    clean.setdefault("distance", 25.0)
    return clean

def approved_asset(path):
    if not isinstance(path, str) or ".." in path or "\\" in path:
        return False
    return path == SCENE or bool(
        path.startswith(TERRAIN_DIR) and re.fullmatch(r"[A-Za-z0-9_./-]+\.asset", path)
    )

def project_save(path, repo):
    """Commit only the scene/terrain asset that Unity says it saved.

    This intentionally pushes to an isolated branch, never main. Git LFS's
    normal clean filter uploads objects; no ad-hoc binary GitHub API writes.
    """
    if not approved_asset(path):
        raise RuntimeError("Editor reported an unexpected asset path; refusing to git add")
    def run(*cmd):
        return subprocess.run(cmd, cwd=repo, capture_output=True, text=True,
                              timeout=120, check=True)
    if run("git", "branch", "--show-current").stdout.strip() != EDIT_BRANCH:
        raise RuntimeError("Not on AI edit branch; refusing to write")
    run("git", "add", "--", path)
    changed = subprocess.run(["git", "diff", "--cached", "--quiet", "--", path],
                             cwd=repo, timeout=20, check=False)
    if changed.returncode == 0:
        return "no-byte-change"
    if changed.returncode != 1:
        raise RuntimeError("git diff failed")
    run("git", "-c", "user.name=Aaris Unity AI", "-c",
        "user.email=41898282+github-actions[bot]@users.noreply.github.com",
        "commit", "-m", "AI map edit: " + Path(path).name, "--", path)
    run("git", "push", "origin", "HEAD:refs/heads/" + EDIT_BRANCH)
    return run("git", "rev-parse", "--short", "HEAD").stdout.strip()

def wireframe_svg(points):
    """Orthographic point-cloud plan, elevation and side view (not photoreal render)."""
    valid = []
    for p in points[:2048]:
        if not isinstance(p, dict):
            continue
        try:
            coords = tuple(float(p[k]) for k in ("x", "y", "z"))
        except (ValueError, KeyError, TypeError):
            continue
        if all(math.isfinite(v) for v in coords):
            valid.append(coords)
    if not valid:
        return None
    parts = ['<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1200 410">',
             '<rect width="1200" height="410" fill="#101b24"/>',
             '<text x="20" y="28" fill="#e6f4ff" font-family="monospace" font-size="18">AARIS 3D MESH POINTS / ORTHOGRAPHIC BLUEPRINT</text>']
    for panel, (xi, yi, title) in enumerate(((0, 1, "FRONT  X/Y"),
                                              (2, 1, "SIDE  Z/Y"),
                                              (0, 2, "TOP  X/Z"))):
        x0 = panel * 400 + 15
        parts.append(f'<rect x="{x0}" y="45" width="370" height="330" fill="none" stroke="#46758a"/>')
        parts.append(f'<text x="{x0+12}" y="69" fill="#8adef4" font-family="monospace" font-size="15">{title}</text>')
        minimum_x = min(p[xi] for p in valid)
        maximum_x = max(p[xi] for p in valid)
        minimum_y = min(p[yi] for p in valid)
        maximum_y = max(p[yi] for p in valid)
        span = max(maximum_x - minimum_x, maximum_y - minimum_y, 0.01)
        for p in valid:
            cx = x0 + 30 + (p[xi] - minimum_x) / span * 306
            cy = 345 - (p[yi] - minimum_y) / span * 244
            parts.append(f'<circle cx="{cx:.1f}" cy="{cy:.1f}" r="1.45" fill="#96e7ff"/>')
        parts.append(f'<text x="{x0+12}" y="392" fill="#c9d9e2" font-family="monospace" font-size="13">span {maximum_x-minimum_x:.2f}m x {maximum_y-minimum_y:.2f}m</text>')
    parts.append('</svg>')
    return "\n".join(parts).encode()

def point_cloud_ply(points):
    """Bounded, coordinate-accurate ASCII PLY point cloud (not a closed 3D mesh)."""
    valid = []
    for point in points[:2048]:
        if not isinstance(point, dict):
            continue
        try:
            xyz = tuple(float(point[axis]) for axis in ("x", "y", "z"))
        except (TypeError, ValueError, KeyError):
            continue
        if all(math.isfinite(n) for n in xyz):
            valid.append(xyz)
    if not valid:
        return None
    lines = [
        "ply", "format ascii 1.0",
        "comment Aaris Unity sampled 3D outline; not a watertight mesh",
        f"element vertex {len(valid)}",
        "property float x", "property float y", "property float z",
        "end_header",
    ]
    lines.extend(f"{x:.6f} {y:.6f} {z:.6f}" for x, y, z in valid)
    return ("\n".join(lines) + "\n").encode("ascii")


def deliver(result, nonce, state, repo, issue_comment, publish_file):
    if result.get("nonce") != nonce:
        raise RuntimeError("Stale Unity response nonce")
    if result.get("state") != "ok":
        issue_comment(f"AI request #{nonce}: Unity error: {str(result.get('error', 'unknown'))[:250]}")
        return "unity-error"
    changed = result.get("changedAsset") or ""
    commit = None
    if changed:
        try:
            commit = project_save(changed, repo)
        except (subprocess.CalledProcessError, RuntimeError, OSError, subprocess.TimeoutExpired) as exc:
            issue_comment(f"AI request #{nonce}: Editor saved a local edit but Git persistence FAILED "
                          f"({type(exc).__name__}). This change is NOT guaranteed on the next runner.")
            return "save-failed"
    op = result.get("op", "")
    public = state / f"ai-result-{nonce}.json"
    public.write_text(json.dumps(result, separators=(",", ":"), ensure_ascii=False))
    links = ["JSON: " + publish_file(nonce, public)]
    if op == "outline" and isinstance(result.get("points"), list):
        svg = wireframe_svg(result["points"])
        if svg:
            path = state / f"ai-blueprint-{nonce}.svg"
            path.write_bytes(svg)
            links.append("3-view dot blueprint: " + publish_file(nonce, path))
        ply = point_cloud_ply(result["points"])
        if ply:
            path = state / f"ai-blueprint-{nonce}.ply"
            path.write_bytes(ply)
            links.append("3D point-cloud PLY: " + publish_file(nonce, path))
    photo = result.get("photo")
    if isinstance(photo, str) and re.fullmatch(r"ai-photo-\d+\.png", photo):
        path = state / photo
        if path.is_file() and path.stat().st_size >= 5000 and path.read_bytes()[:8] == b"\x89PNG\r\n\x1a\n":
            links.append("Real Unity PNG: " + publish_file(nonce, path))
    summary = f"AI request #{nonce}: {op} complete; objects={result.get('count', 0)}."
    if commit:
        summary += f" Git edit branch {EDIT_BRANCH} commit: {commit}."
    issue_comment(summary + "\n\n" + "\n".join(links))
    return "published"

def handle(body, nonce, state, repo, issue_comment, publish_file):
    request = parse(body, nonce)
    state.mkdir(parents=True, exist_ok=True)
    result_file = state / "ai-result.json"
    result_file.unlink(missing_ok=True)
    tmp = state / "ai-command.tmp"
    tmp.write_text(json.dumps(request, separators=(",", ":")))
    os.replace(tmp, state / "ai-command.json")
    deadline = time.monotonic() + 110
    while time.monotonic() < deadline:
        time.sleep(1)
        try:
            result = json.loads(result_file.read_text())
        except (FileNotFoundError, json.JSONDecodeError, OSError):
            continue
        if result.get("nonce") == nonce:
            return deliver(result, nonce, state, repo, issue_comment, publish_file)
    issue_comment(f"AI request #{nonce}: timeout: Unity Editor not running/activated, importing "
                  "assets, or AI bridge not compiled. No successful edit/photo claimed.")
    return "no-unity-response"

def selftest():
    sample = parse('AI {"op":"outline","target":"House[1]/Wall[0]","max_points":16}', 123)
    assert sample["target"] == "House[1]/Wall[0]"
    measured = parse('AI {"op":"size","target":"House[1]/Wall[0]","axis":"z","meters":0.35,"dry_run":true}', 123)
    assert measured["axis"] == "z" and measured["meters"] == 0.35 and measured["dry_run"]
    tint = parse('AI {"op":"tint","target":"terrain-tree:0:20","hex":"#7CBF68","dry_run":true}', 123)
    assert tint["hex"] == "#7CBF68" and tint["dry_run"] is True
    child = parse('AI {"op":"children","target":"House[1]"}', 123)
    assert child["op"] == "children" and child["target"] == "House[1]"
    assert len(wireframe_svg([{"x":0,"y":0,"z":0},{"x":1,"y":1,"z":1}])) > 100
    ply = point_cloud_ply([{"x":0,"y":0,"z":0},{"x":1,"y":2,"z":3}])
    assert ply and ply.startswith(b"ply\nformat ascii 1.0\n")
    assert ply.count(b"\n") >= 10 and b"element vertex 2\n" in ply
    assert b"1.000000 2.000000 3.000000\n" in ply
    assert point_cloud_ply([{"x":float("nan"),"y":0,"z":0}]) is None
    for value in ('AI {"op":"shell","command":"rm -rf /"}',
                  'AI {"op":"move","target":"X","dx":1e999,"dy":0,"dz":0}',
                  'AI {"op":"bounds","target":"X","secret":true}',
                  'AI {"op":"scale","target":"X","sx":0,"sy":1,"sz":1}',
                  'AI {"op":"size","target":"X","axis":"q","meters":2}',
                  'AI {"op":"size","target":"X","axis":"x","meters":0}',
                  'AI {"op":"tint","target":"terrain-tree:0:20","hex":"red"}',
                  'AI {"op":"tint","target":"terrain-tree:0:20","hex":"#BADBAD","shell":"no"}'):
        try:
            parse(value, 1)
        except ValueError:
            pass
        else:
            raise AssertionError("Unsafe AI input accepted: " + value)
    assert approved_asset(SCENE)
    assert approved_asset(TERRAIN_DIR + "Terrain_1_1.asset")
    assert not approved_asset("Assets/Editor/Secrets.asset")
    print("Aaris AI intents validation and three-view blueprint selftest: PASS")

if __name__ == "__main__":
    selftest()
