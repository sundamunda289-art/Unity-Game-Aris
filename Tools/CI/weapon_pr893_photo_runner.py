#!/usr/bin/env python3
"""Reproducible isolated Unity 6000.3.26f1 photo QA; no git worktree.

Run on the existing Aaris Codespace:
 python3 Tools/CI/weapon_pr893_photo_runner.py
All writes go to /tmp. The active workspace and other agents' changes remain intact.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import urllib.request

SOURCE = Path("/workspaces/Unity-Game-Aris")
MAIN = "f6d3ccbacc189889abedd44795ecb33e9bf81a6f"
PR = "dc985e150b8f45de4bf12d762159484a7570696b"
OUT = Path("/tmp/wqa893-native-photo-c5")
PROJECT = OUT / "Project"
EDITOR = "/home/vscode/.local/share/aaris-unity-editors/6000.3.26f1/Editor/Unity"
AUDIT = Path("/tmp/aaris-gun-door-live-audit")
OUT.mkdir(parents=True, exist_ok=True)


def note(message):
    line = time.strftime("%H:%M:%S", time.gmtime()) + " " + message
    with (OUT / "progress.txt").open("a") as f:
        f.write(line + "\n")
    print(line, flush=True)


def git(*args):
    return subprocess.run(
        ["git", *args], cwd=SOURCE, check=True, capture_output=True
    ).stdout


def snapshot():
    if PROJECT.exists():
        raise RuntimeError("REFUSING_TO_OVERWRITE_EXISTING_SNAPSHOT")
    if git("rev-parse", "main").decode().strip() != MAIN:
        raise RuntimeError("MAIN_SHA_DRIFT")
    PROJECT.mkdir()
    for folder in ("Assets", "Packages", "ProjectSettings", "Library"):
        note("COPY_BEGIN " + folder)
        proc = subprocess.run(
            ["rsync", "-a", "--partial",
             str(SOURCE / folder) + "/", str(PROJECT / folder) + "/"],
            capture_output=True, text=True, timeout=700)
        if proc.returncode:
            raise RuntimeError("RSYNC_" + folder + ": " + proc.stderr[-500:])
        note("COPY_DONE " + folder)

    # Remove untracked files only from the temporary snapshot.
    untracked = git("ls-files", "--others", "--exclude-standard",
                    "--", "Assets", "Packages", "ProjectSettings").decode().splitlines()
    for rel in untracked:
        dest = PROJECT / rel
        if dest.is_file() or dest.is_symlink():
            dest.unlink()
        elif dest.is_dir():
            shutil.rmtree(dest)

    # Resolve local HEAD/worktree edits back to canonical MAIN without touching source.
    changed = git("diff", "--name-only", MAIN, "--",
                  "Assets", "Packages", "ProjectSettings").decode().splitlines()
    for rel in changed:
        dest = PROJECT / rel
        has_file = subprocess.run(
            ["git", "cat-file", "-e", MAIN + ":" + rel],
            cwd=SOURCE, capture_output=True).returncode == 0
        if not has_file:
            if dest.is_file() or dest.is_symlink():
                dest.unlink()
            elif dest.is_dir():
                shutil.rmtree(dest)
            continue
        blob = git("show", MAIN + ":" + rel)
        if blob.startswith(b"version https://git-lfs.github.com/spec/v1"):
            oid = re.search(rb"oid sha256:([0-9a-f]{64})", blob).group(1).decode()
            object_path = SOURCE / ".git/lfs/objects" / oid[:2] / oid[2:4] / oid
            blob = object_path.read_bytes()
            if hashlib.sha256(blob).hexdigest() != oid:
                raise RuntimeError("LFS_BLOB_HASH_MISMATCH: " + rel)
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_bytes(blob)
    note("MAIN_SNAPSHOT_RESTORED changed=" + str(len(changed)) +
         " untracked_excluded=" + str(len(untracked)))

    for rel in (
        "Assets/AarisWorld/Scripts/AarisWeaponLootWorld.cs",
        "Assets/AarisWorld/Editor/AarisWeaponSupportPhysicsQA.cs",
        "Assets/AarisWorld/Editor/AarisWeaponSupportPhysicsQA.cs.meta",
    ):
        url = ("https://raw.githubusercontent.com/sundamunda289-art/"
               "Unity-Game-Aris/" + PR + "/" + rel)
        req = urllib.request.Request(url, headers={"User-Agent": "Aaris-QA"})
        with urllib.request.urlopen(req, timeout=55) as response:
            content = response.read()
        if rel.endswith("AarisWeaponLootWorld.cs"):
            blob_hash = hashlib.sha1(
                b"blob " + str(len(content)).encode() + b"\0" + content
            ).hexdigest()
            if blob_hash != "22736a8c4ad471af3e6d0203e5d645046d700699":
                raise RuntimeError("PR_BLOB_HASH_MISMATCH")
        dest = PROJECT / rel
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_bytes(content)
    note("PR_893_PATCH_APPLIED")


def run():
    snapshot()
    if not (PROJECT / "ProjectSettings/ProjectVersion.txt").is_file():
        raise RuntimeError("MISSING_PROJECT_SETTINGS")
    log = OUT / "unity-ground-photo.log"
    command = [EDITOR, "-batchmode", "-force-glcore",
               "-projectPath", str(PROJECT),
               "-executeMethod",
               "Aaris.MapReview.Editor.AarisGunDoorLiveAudit.RunGroundPhoto",
               "-logFile", str(log), "-accept-apiupdate"]
    env = {**os.environ, "DISPLAY": ":99", "LIBGL_ALWAYS_SOFTWARE": "1"}
    note("UNITY_GROUND_PHOTO_BEGIN")
    proc = subprocess.run(command, cwd=OUT, env=env,
                          capture_output=True, text=True, timeout=550)
    note("UNITY_EXIT " + str(proc.returncode))
    images = []
    evidence = OUT / "proof"
    evidence.mkdir(exist_ok=True)
    if AUDIT.exists():
        for pic in AUDIT.glob("*.jpg"):
            copy = evidence / pic.name
            shutil.copy2(pic, copy)
            images.append({"file": pic.name, "bytes": copy.stat().st_size})
        if (AUDIT / "report.txt").is_file():
            shutil.copy2(AUDIT / "report.txt", evidence / "report.txt")
    text = log.read_text(errors="replace") if log.exists() else ""
    cs_errors = [x[:200] for x in text.splitlines() if "error CS" in x]
    report = (evidence / "report.txt").read_text() if (
        evidence / "report.txt").is_file() else "NO_REPORT"
    result = {"exit": proc.returncode, "images": images,
              "error_cs_count": len(cs_errors), "report": report[-3000:],
              "unity_log_bytes": len(text)}
    (OUT / "result.json").write_text(json.dumps(result, indent=2))
    note("RESULT " + json.dumps(result))
    return proc.returncode


if __name__ == "__main__":
    try:
        sys.exit(run())
    except Exception as error:
        note("FAIL " + type(error).__name__ + " " + str(error)[:450])
        raise
