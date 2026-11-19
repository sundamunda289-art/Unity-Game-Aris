#!/usr/bin/env python3
"""Isolated temporary C# overlay for real Unity pose QA on the existing Codespace.

The user's active Git branch, native assets and uncommitted work are retained.
The only temporary changes are a verified clean CharacterVisual.cs and an
Editor-only guard around the concurrent Android-loot coroutine. Both files
are restored in a finally block with content/HEAD conflict guards.
Never use this while another Unity Editor instance is running.
"""
import hashlib
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile

ROOT = Path.cwd()
REL = "Assets/AarisWorld/Scripts/AarisCharacterVisual.cs"
SOURCE = ROOT / REL
OUT = Path("/tmp/aaris-pose-qa-20261119/knee-review-v1")
POSE_DIR = Path("/tmp/aaris-codespaces/motion-frames")
BRANCH = "193634097e3e226df866a411957396f380d1f086"
URL = "https://raw.githubusercontent.com/sundamunda289-art/Unity-Game-Aris/" + BRANCH + "/" + REL
LOOT_REL = "Assets/AarisWorld/Scripts/AarisWeaponLootWorld.cs"
LOOT_SOURCE = ROOT / LOOT_REL
RUNS = (
    ("transition", "CaptureTransitionPortrait", "SUCCESS_16_STANCE_TRANSITION_FRAMES"),
    ("settled", "CaptureMultiAnglePortrait", "SUCCESS_16_ACTOR_MULTIANGLE_PORTRAITS"),
)


def git(*args):
    return subprocess.run(["git", *args], cwd=ROOT, capture_output=True)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def atomic_replace(path, data, mode):
    fd, name = tempfile.mkstemp(prefix=".aaris-pose-qa-", dir=str(path.parent))
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(name, mode)
        os.replace(name, path)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def term(signum, frame):
    raise KeyboardInterrupt("Pose capture interrupted; restoring original source")


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    if SOURCE.is_symlink() or not SOURCE.is_file() or git("diff", "--quiet", "--", REL).returncode:
        raise RuntimeError("ABORT_EXISTING_VISUAL_MODIFICATIONS")
    blob = git("hash-object", REL).stdout.decode().strip()
    expected_source = git("rev-parse", "HEAD:" + REL).stdout.decode().strip()
    if blob != expected_source:
        raise RuntimeError("ABORT_CHARACTER_VISUAL_DIFFERS_FROM_HEAD " + blob)
    if LOOT_SOURCE.is_symlink() or not LOOT_SOURCE.is_file() or git("diff", "--quiet", "--", LOOT_REL).returncode:
        raise RuntimeError("ABORT_EXISTING_LOOT_MODIFICATIONS")
    loot_blob = git("hash-object", LOOT_REL).stdout.decode().strip()
    expected_loot = git("rev-parse", "HEAD:" + LOOT_REL).stdout.decode().strip()
    if loot_blob != expected_loot:
        raise RuntimeError("ABORT_LOOT_SOURCE_DIFFERS_FROM_HEAD " + loot_blob)
    # Inspect executable names, NOT the current shell's argv (which includes
    # this script text in command queues and can match grep/pgrep incorrectly).
    proc_names = subprocess.check_output(["ps", "-eo", "comm="], text=True).splitlines()
    if any(name.strip() == "Unity" for name in proc_names):
        raise RuntimeError("ABORT_EXISTING_UNITY_EDITOR")
    editor = Path.home() / ".local/share/aaris-unity-editors/6000.3.26f1/Editor/Unity"
    if not editor.is_file():
        raise RuntimeError("UNITY_EDITOR_NOT_FOUND")
    patch = subprocess.check_output(["curl", "-fsSL", "--max-time", "45", URL])
    if (b"stanceSpread > 0.57f" not in patch or
        b"AnatomicalKneeForward()" not in patch or
        b"maximumKneeSideMetres" not in patch):
        raise RuntimeError("WRONG_PATCH")
    original = SOURCE.read_bytes()
    original_hash, patch_hash = digest(original), digest(patch)
    mode = SOURCE.stat().st_mode & 0o777
    (OUT / "original_character_visual.cs").write_bytes(original)
    (OUT / "patched_character_visual.cs").write_bytes(patch)
    # The in-progress performance branch has an Android-only method correctly
    # declared behind a player define, but its caller method is not similarly
    # guarded. Keep the exact Android behavior; exclude only that unreachable
    # coroutine from the Editor compile while rendering these QA screenshots.
    original_loot = LOOT_SOURCE.read_bytes()
    marker_begin = b"        private IEnumerator InitializeAndroid()\n"
    marker_end = b"        private void Initialize()\n"
    if original_loot.count(marker_begin) != 1 or original_loot.count(marker_end) != 1:
        raise RuntimeError("LOOT_PREPROCESSOR_MARKERS_MISSING")
    patched_loot = original_loot.replace(
        marker_begin, b"#if UNITY_ANDROID && !UNITY_EDITOR\n" + marker_begin).replace(
        marker_end, b"#endif\n\n" + marker_end)
    original_loot_hash, patched_loot_hash = digest(original_loot), digest(patched_loot)
    loot_mode = LOOT_SOURCE.stat().st_mode & 0o777
    (OUT / "original_loot.cs").write_bytes(original_loot)
    (OUT / "editor_only_loot.cs").write_bytes(patched_loot)
    original_head = git("rev-parse", "HEAD").stdout.decode().strip()
    proc = None
    passed = False
    try:
        atomic_replace(SOURCE, patch, mode)
        atomic_replace(LOOT_SOURCE, patched_loot, loot_mode)
        if digest(SOURCE.read_bytes()) != patch_hash or digest(LOOT_SOURCE.read_bytes()) != patched_loot_hash:
            raise RuntimeError("PATCH_WRITE_RACE")
        print("PATCH_TEMPORARILY_APPLIED_EDITOR_ONLY_LOOT_GUARD originalHead=" + original_head[:12], flush=True)
        for label, method, marker in RUNS:
            folder = OUT / label
            folder.mkdir(parents=True, exist_ok=True)
            print("NATIVE_" + label.upper() + "_START", flush=True)
            command = [
                "xvfb-run", "-a", "-s", "-screen 0 1280x720x24 +extension GLX -ac",
                str(editor), "-batchmode", "-force-glcore",
                "-projectPath", str(ROOT),
                "-executeMethod", "Aaris.MapReview.Editor.AarisPoseFrames." + method,
                "-logFile", str(folder / "editor.log"),
            ]
            with (folder / "launcher.log").open("wb") as log:
                proc = subprocess.Popen(command, cwd=ROOT, stdout=log,
                                        stderr=subprocess.STDOUT, start_new_session=True)
                try:
                    exit_code = proc.wait(timeout=490)
                except subprocess.TimeoutExpired:
                    raise RuntimeError(label + "_TIMEOUT")
            proc = None
            report = POSE_DIR / "report.txt"
            phase = POSE_DIR / "phase.txt"
            images = sorted(POSE_DIR.glob("*.jpg"))
            if report.is_file():
                shutil.copy2(report, folder / "report.txt")
            if phase.is_file():
                shutil.copy2(phase, folder / "phase.txt")
            for img in images:
                shutil.copy2(img, folder / img.name)
            if exit_code != 0 or not report.is_file() or marker not in report.read_text() or (
                len(images) != 16 or any(i.stat().st_size < 4000 for i in images)
            ) or not phase.is_file() or phase.read_text().strip() != "07_COMPLETE":
                raise RuntimeError(f"POSE_{label}_FAIL exit={exit_code} frames={len(images)}")
            print("NATIVE_" + label.upper() + "_SUCCESS 16_REAL_FRAMES", flush=True)
            if digest(SOURCE.read_bytes()) != patch_hash or digest(LOOT_SOURCE.read_bytes()) != patched_loot_hash:
                raise RuntimeError("SOURCE_CHANGED_DURING_CAPTURE")
        passed = True
    finally:
        if proc is not None and proc.poll() is None:
            try:
                os.killpg(proc.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                try:
                    os.killpg(proc.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                proc.wait()
        clean = True
        for path, rel, prior, applied, saved_mode in (
            (LOOT_SOURCE, LOOT_REL, original_loot, patched_loot, loot_mode),
            (SOURCE, REL, original, patch, mode),
        ):
            current = digest(path.read_bytes())
            committed = git("show", "HEAD:" + rel)
            if current == digest(applied) and committed.returncode == 0 and committed.stdout == prior:
                atomic_replace(path, prior, saved_mode)
                print("RESTORED " + rel, flush=True)
            elif current == digest(prior):
                print("ALREADY_RESTORED " + rel, flush=True)
            else:
                print("WARNING_EXTERNAL_CHANGE_RESTORATION_SKIPPED " + rel, flush=True)
            clean = clean and digest(path.read_bytes()) == digest(prior)
        print(f"QA_RESULT success={passed} originals_restored={clean}", flush=True)
        if not clean:
            raise RuntimeError("RESTORATION_REQUIRES_MANUAL_REVIEW")


if __name__ == "__main__":
    signal.signal(signal.SIGTERM, term)
    signal.signal(signal.SIGINT, term)
    try:
        main()
    except (Exception, KeyboardInterrupt) as exc:
        print("POSE_NATIVE_RUN_FAILED " + type(exc).__name__ + " " + str(exc), flush=True)
        sys.exit(2)
