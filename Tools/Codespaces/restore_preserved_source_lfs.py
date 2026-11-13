#!/usr/bin/env python3
"""Hydrate an isolated Unity snapshot from SHA-matched preserved Codespace files.

The original /workspaces/Unity-Game-Aris is READ ONLY: no reset, clean, stash,
Git ref update, asset writes or license access. Every reused byte must match
the new snapshot's own Git LFS pointer (SHA-256 and length). Other missing OIDs
are fetched from the authentic remote into a separate /tmp-only cache.
"""
from __future__ import annotations
import hashlib
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import tempfile

SOURCE = Path("/workspaces/Unity-Game-Aris")
SNAPSHOTS = Path("/tmp/aaris-native-photo-worktrees")
ANDROID_SNAPSHOTS = Path("/tmp/aaris-native-android-build-worktrees")
CACHE_ROOT = Path("/tmp/aaris-preserved-lfs-cache")
PTR = re.compile(
    rb"\Aversion https://git-lfs.github.com/spec/v1\n"
    rb"oid sha256:([a-f0-9]{64})\nsize ([0-9]+)\n?\Z"
)


class RecoveryError(Exception):
    pass


def git(project: Path, *args: str, timeout: int = 180) -> bytes:
    try:
        return subprocess.run(["git", "-C", str(project), *args],
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                              check=True, timeout=timeout).stdout
    except (subprocess.CalledProcessError, subprocess.TimeoutExpired, OSError):
        raise RecoveryError("GIT_COMMAND_FAILED") from None


def digest(path: Path) -> str:
    sha = hashlib.sha256()
    with path.open("rb") as source:
        for part in iter(lambda: source.read(1024 * 1024), b""):
            sha.update(part)
    return sha.hexdigest()


def candidate_matches(path: Path, oid: str, size: int) -> bool:
    try:
        return (path.is_file() and not path.is_symlink()
                and path.stat().st_size == size and digest(path) == oid)
    except OSError:
        return False


def copy_verified(candidate: Path, target: Path, oid: str, size: int) -> None:
    if candidate_matches(target, oid, size):
        return
    if target.exists():
        raise RecoveryError("EXISTING_CACHE_OBJECT_INVALID")
    target.parent.mkdir(parents=True, exist_ok=True)
    temp = None
    try:
        with tempfile.NamedTemporaryFile(dir=target.parent,
                                         prefix=".verified-lfs-",
                                         delete=False) as writer:
            temp = Path(writer.name)
            with candidate.open("rb") as reader:
                shutil.copyfileobj(reader, writer, 1024 * 1024)
            writer.flush()
            os.fsync(writer.fileno())
        if not candidate_matches(temp, oid, size):
            raise RecoveryError("COPIED_OBJECT_HASH_MISMATCH")
        os.replace(temp, target)
    finally:
        if temp is not None:
            temp.unlink(missing_ok=True)


def object_path(objects: Path, oid: str) -> Path:
    return objects / oid[:2] / oid[2:4] / oid


def original_cache_roots(source: Path) -> list[Path]:
    roots = []
    try:
        raw = git(source, "rev-parse", "--absolute-git-dir").decode().strip()
        roots.append(Path(raw) / "lfs" / "objects")
        env = git(source, "lfs", "env", timeout=30).decode("utf-8", "replace")
        for line in env.splitlines():
            if line.startswith("LocalMediaDir="):
                location = Path(line.split("=", 1)[1])
                if location.is_absolute() and location.name == "objects":
                    roots.append(location)
    except RecoveryError:
        pass
    # Only READ from these locations; content hashes are checked before reuse.
    return list(dict.fromkeys(roots))


def audit_pointers(project: Path) -> dict[str, tuple[str, int]]:
    paths = git(project, "lfs", "ls-files", "--name-only").decode().splitlines()
    # The completed PR #302 adds sprint/prone/crawl FBXs as three new LFS objects.
    # Fail closed on any incomplete or unrelated snapshot.
    if len(paths) not in (793, 796, 822) or len(set(paths)) != len(paths):
        raise RecoveryError("EXPECTED_793_796_OR_822_LFS_POINTERS_NOT_FOUND")
    if len(paths) >= 796:
        motions = "Assets/Resources/AarisCharacters/Primary/Animations/"
        for clip in ("sprint", "prone", "crawl"):
            if motions + clip + ".fbx" not in paths:
                raise RecoveryError("KAYKIT_NEW_LFS_MOTION_MISSING")
    expected = {}
    for relative in paths:
        parsed = PurePosixPath(relative)
        if (parsed.is_absolute() or ".." in parsed.parts or
                not parsed.parts or "," in relative or "\n" in relative):
            raise RecoveryError("LFS_FILE_PATH_UNSAFE")
        match = PTR.fullmatch(git(project, "show", "HEAD:" + relative))
        if match is None:
            raise RecoveryError("HEAD_LFS_POINTER_INVALID")
        oid, size = match.group(1).decode("ascii"), int(match.group(2))
        if size <= 0 or size >= 2_000_000_000:
            raise RecoveryError("HEAD_LFS_OBJECT_SIZE_INVALID")
        expected[relative] = (oid, size)
    return expected


PACKAGE = Path("/tmp/aaris-codespaces/scene-probe/verified-tree-mass-package.tar.gz")
PACKAGE_SHA256 = "234024b842e8cc5e1253607ce41b5a6aebec6a7abd3c9fd81837bc960a24197d"


def import_authenticated_tree_bake(expected, objects: Path) -> int:
    """Only 18 historical Unity mesh OIDs; never import materials or .meta."""
    if not PACKAGE.is_file():
        return 0
    if PACKAGE.stat().st_size != 4757727 or digest(PACKAGE) != PACKAGE_SHA256:
        raise RecoveryError("ORIGINAL_TREE_ARCHIVE_SHA256_MISMATCH")
    # This reviewed archive verifier additionally checks 45 tar members,
    # provenance, per-file manifest and every HEAD LFS pointer.
    from recover_verified_tree_lfs import PATHS, load_verified_meshes
    if len(PATHS) != 18 or any(path not in expected for path in PATHS):
        raise RecoveryError("CURRENT_SNAPSHOT_TREE_PATH_MISMATCH")
    try:
        meshes = load_verified_meshes(PACKAGE)
    except Exception:
        raise RecoveryError("ARCHIVED_TREE_MESH_VERIFICATION_FAILED") from None
    if set(meshes) != {expected[path][0] for path in PATHS}:
        raise RecoveryError("TREE_ARCHIVE_OIDS_NOT_IN_HEAD")
    for oid, raw in meshes.items():
        target = object_path(objects, oid)
        if candidate_matches(target, oid, len(raw)):
            continue
        if target.exists():
            raise RecoveryError("TREE_OBJECT_CACHE_CORRUPT")
        target.parent.mkdir(parents=True, exist_ok=True)
        temp = None
        try:
            with tempfile.NamedTemporaryFile(
                    dir=target.parent, prefix=".original-tree-",
                    delete=False) as writer:
                temp = Path(writer.name)
                writer.write(raw)
                writer.flush()
                os.fsync(writer.fileno())
            if not candidate_matches(temp, oid, len(raw)):
                raise RecoveryError("TREE_OBJECT_IMPORT_HASH_MISMATCH")
            os.replace(temp, target)
        finally:
            if temp is not None:
                temp.unlink(missing_ok=True)
    print("AARIS_CODESPACE_ORIGINAL_TREE_ARCHIVE_HASH_VERIFIED=18", flush=True)
    return len(meshes)


def restore(project: Path) -> None:
    project = project.resolve(strict=True)
    source = SOURCE.resolve(strict=True)
    revision = git(project, "rev-parse", "HEAD").decode().strip()
    if (project.parent not in (SNAPSHOTS, ANDROID_SNAPSHOTS)
            or project.name != revision
            or not re.fullmatch("[0-9a-f]{40}", revision)):
        raise RecoveryError("WRONG_TARGET_IMMUTABLE_WORKTREE")
    if source == project or not (source / ".git").exists():
        raise RecoveryError("ORIGINAL_SOURCE_REPOSITORY_NOT_FOUND")
    expected = audit_pointers(project)
    cache = CACHE_ROOT / revision / "lfs"
    objects = cache / "objects"
    old_caches = original_cache_roots(source)
    local = 0
    missing = []
    for relative, (oid, size) in expected.items():
        target = object_path(objects, oid)
        # Prior isolated worktree content may already be hydrated.
        candidates = [project / relative, source / relative]
        candidates += [object_path(folder, oid) for folder in old_caches]
        candidates.append(target)
        verified = next((item for item in candidates
                         if candidate_matches(item, oid, size)), None)
        if verified is None:
            missing.append(relative)
            continue
        if verified != target:
            copy_verified(verified, target, oid, size)
        local += 1
    print("AARIS_PRESERVED_OR_CACHED_LFS_HASH_VERIFIED=" + str(local), flush=True)
    if missing:
        import_authenticated_tree_bake(expected, objects)
        missing = [path for path in missing
                   if not candidate_matches(object_path(objects, expected[path][0]),
                                            expected[path][0], expected[path][1])]
    print("AARIS_REMOTE_LFS_OIDS_NEEDED=" + str(len(missing)), flush=True)
    if missing:
        # Fetch ONLY missing paths; avoid an unnecessary 1.5 GiB cold LFS pull.
        # Logs stay private in the Codespace; issue comments expose counts only.
        report = Path("/tmp/aaris-codespaces/scene-probe/private-preserved-lfs-fetch.log")
        report.parent.mkdir(parents=True, exist_ok=True)
        command = [
            "git", "-C", str(project), "-c", "lfs.storage=" + str(cache),
            "lfs", "fetch", "--include=" + ",".join(missing), "--exclude=",
            "origin", revision,
        ]
        with report.open("wb") as private:
            try:
                result = subprocess.run(command, stdout=private, stderr=private,
                                        timeout=1200)
            except (OSError, subprocess.TimeoutExpired):
                raise RecoveryError("ISOLATED_REMOTE_LFS_FETCH_FAILED") from None
        if result.returncode:
            raise RecoveryError("ISOLATED_REMOTE_LFS_FETCH_FAILED")
    for oid, size in expected.values():
        if not candidate_matches(object_path(objects, oid), oid, size):
            raise RecoveryError("ALL_LFS_OBJECT_HASHES_NOT_AVAILABLE")
    git(project, "-c", "lfs.storage=" + str(cache),
        "lfs", "checkout", timeout=900)
    for relative, (oid, size) in expected.items():
        if not candidate_matches(project / relative, oid, size):
            raise RecoveryError("ISOLATED_LFS_WORKTREE_NOT_HYDRATED")
    print("AARIS_PRESERVED_LFS_SNAPSHOT_VERIFIED=" + str(len(expected)), flush=True)
    print("AARIS_PRESERVED_LFS_ORIGINAL_WORKSPACE_UNCHANGED=yes", flush=True)


def selftest() -> None:
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        original = root / "original"
        objects = root / "objects"
        original.write_bytes(b"AARIS_VERIFIED_SOURCE_TEST_20261108")
        size = original.stat().st_size
        oid = digest(original)
        assert candidate_matches(original, oid, size)
        target = object_path(objects, oid)
        copy_verified(original, target, oid, size)
        assert candidate_matches(target, oid, size)
        original.write_bytes(b"tampered")
        assert not candidate_matches(original, oid, size)
        assert candidate_matches(target, oid, size)
    print("AARIS_PRESERVED_LFS_SELFTEST_PASSED")


if __name__ == "__main__":
    try:
        if len(sys.argv) == 2 and sys.argv[1] == "--selftest":
            selftest()
        elif len(sys.argv) == 2:
            restore(Path(sys.argv[1]))
        else:
            raise RecoveryError("PROJECT_ARGUMENT_REQUIRED")
    except RecoveryError as exc:
        print("AARIS_PRESERVED_LFS_RECOVERY_FAILED=" + str(exc), flush=True)
        sys.exit(1)
