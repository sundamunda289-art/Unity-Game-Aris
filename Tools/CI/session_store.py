#!/usr/bin/env python3
"""Verified, chunked Unity session checkpoints stored in GitHub Releases.

Release data is PUBLIC. No secrets, .git, Unity license, Temp or logs are archived.
Use only after checkout; LFS hydration remains necessary for missing source assets.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time

ROOT = Path(__file__).resolve().parents[2]
REPO = os.getenv("GITHUB_REPOSITORY", "sundamunda289-art/Unity-Game-Aris")
PART_BYTES = 1750 * 1024 * 1024  # well below the 2 GiB per Release asset limit
TAGS = {"project": "project-checkpoint", "engine": "engine-cache"}
ALLOWED = ("Assets/", "Docs/", "Packages/", "ProjectSettings/", "Tools/MapBuild/")
MANIFEST = {"project": "project-manifest.json", "engine": "engine-manifest.json"}

def command(*args, check=True, **kwargs):
    return subprocess.run(args, cwd=ROOT, check=check, **kwargs)

def sha(path):
    h = hashlib.sha256()
    with open(path, "rb") as src:
        for block in iter(lambda: src.read(8 * 1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()

def head():
    return command("git", "rev-parse", "HEAD", capture_output=True, text=True).stdout.strip()

def engine_key():
    h = hashlib.sha256()
    for name in ("ProjectSettings/ProjectVersion.txt", "Packages/manifest.json"):
        h.update((ROOT / name).read_bytes())
    h.update(b"ubuntu-x64-gameci-base-3")
    return h.hexdigest()

def allowed(name, kind):
    p = PurePosixPath(name)
    if (not name or p.is_absolute() or ".." in p.parts or
            any(part.startswith(".") for part in p.parts)):
        return False
    if kind == "engine":
        return name == "Library" or name.startswith("Library/")
    return name.startswith(ALLOWED) and not any(
        part.lower() in ("secrets", "licenses", "credentials") for part in p.parts
    )

def unchanged_from_head(name, path):
    """Ignore checkout/LFS filter anomalies; only archive genuinely edited bytes."""
    old_sha = command("git", "rev-parse", "HEAD:" + name,
                      check=False, capture_output=True, text=True)
    if old_sha.returncode:
        return False
    raw_sha = command("git", "hash-object", "--no-filters", "--", name,
                      capture_output=True, text=True).stdout.strip()
    if raw_sha == old_sha.stdout.strip():
        return True
    # A valid hydrated LFS object differs byte-for-byte from its Git pointer,
    # but is NOT a map edit. Compare its recorded content digest, not blob SHA.
    prior = command("git", "cat-file", "blob", "HEAD:" + name,
                    capture_output=True).stdout
    if not prior.startswith(b"version https://git-lfs.github.com/spec/v1\n"):
        return False
    import re
    match = re.search(rb"oid sha256:([0-9a-f]{64})\nsize (\d+)\n", prior)
    return bool(match and path.stat().st_size == int(match.group(2))
                and sha(path) == match.group(1).decode())

def project_paths():
    tracked = command("git", "diff", "--name-only", "-z", "HEAD",
                      capture_output=True).stdout.split(b"\0")
    untracked = command("git", "ls-files", "--others", "--exclude-standard", "-z",
                        capture_output=True).stdout.split(b"\0")
    changed = {os.fsdecode(item) for item in tracked if item}
    selected = set()
    for data in tracked + untracked:
        if not data:
            continue
        name = os.fsdecode(data)
        path = ROOT / name
        if allowed(name, "project") and path.is_file() and not path.is_symlink():
            if name in changed and unchanged_from_head(name, path):
                continue
            selected.add(name)
    return sorted(selected)

def archive_files(files, output):
    if not files:
        return
    # tar paths are validated and passed as argument list, never evaluated by a shell.
    producer = subprocess.Popen(["tar", "--format=posix", "-cf", "-", "--", *files],
                                cwd=ROOT, stdout=subprocess.PIPE)
    try:
        consumer = subprocess.run(["zstd", "-q", "-T2", "-3", "-o", str(output)],
                                  stdin=producer.stdout, check=False)
    finally:
        producer.stdout.close()
        code = producer.wait()
    if code or consumer.returncode:
        raise RuntimeError(f"Archiver failed tar={code} zstd={consumer.returncode}")

def split(path, folder, prefix):
    result = []
    with path.open("rb") as source:
        index = 0
        while True:
            first = source.read(8 * 1024 * 1024)
            if not first:
                break
            index += 1
            name = f"{prefix}.part{index:03d}"
            target = folder / name
            with target.open("wb") as out:
                out.write(first)
                remaining = PART_BYTES - len(first)
                while remaining:
                    block = source.read(min(8 * 1024 * 1024, remaining))
                    if not block:
                        break
                    out.write(block)
                    remaining -= len(block)
            result.append({"name": name, "sha256": sha(target), "bytes": target.stat().st_size})
    return result

def ensure_release(tag):
    exists = command("gh", "release", "view", tag, "--repo", REPO,
                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
    if exists.returncode != 0:
        command("gh", "release", "create", tag, "--repo", REPO,
                "--target", head(), "--title", f"Aaris {tag} automated checkpoint",
                "--notes", "Public, checksummed automation data; not a source code release.")

def upload(tag, paths):
    if paths:
        command("gh", "release", "upload", tag, "--repo", REPO,
                "--clobber", *map(str, paths))

def download(tag, name, folder):
    result = command("gh", "release", "download", tag, "--repo", REPO,
                     "--pattern", name, "--dir", str(folder),
                     stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, check=False)
    return result.returncode == 0 and (folder / name).is_file()

def save(kind):
    if kind == "engine":
        library = ROOT / "Library"
        if not library.is_dir() or not any(library.iterdir()):
            print("No populated Unity Library; engine cache not saved.")
            return
        files = ["Library"]
    else:
        files = project_paths()
        # A pristine fresh runner must never replace a useful Release checkpoint
        # with a zero-file manifest. This can otherwise destroy recovery state.
        if not files:
            print("No changed project files; keeping the previous checkpoint intact.")
            return 0
    tag = TAGS[kind]
    with tempfile.TemporaryDirectory(prefix="aaris-save-") as temp:
        folder = Path(temp)
        serial = os.getenv("GITHUB_RUN_ID", str(int(time.time())))
        attempt = os.getenv("GITHUB_RUN_ATTEMPT", "1")
        prefix = f"{kind}-{serial}-{attempt}"
        archive = folder / "snapshot.tar.zst"
        if files:
            archive_files(files, archive)
            parts = split(archive, folder, prefix)
            archive.unlink()
        else:
            parts = []
        manifest = {
            "schema": 1, "kind": kind, "source_sha": head(),
            "engine_key": engine_key() if kind == "engine" else None,
            "created_utc": int(time.time()), "files": files if kind == "project" else ["Library/"],
            "parts": parts
        }
        path = folder / MANIFEST[kind]
        path.write_text(json.dumps(manifest, indent=2) + "\n")
        ensure_release(tag)
        # Manifest LAST: a failed upload cannot invalidate the old valid checkpoint.
        upload(tag, [folder / p["name"] for p in parts])
        upload(tag, [path])
        print(f"Saved {kind} checkpoint: {len(parts)} chunks, {len(files)} entries.")

def unpack(parts, dest, kind):
    # Stream concatenated part bytes into zstd, then validate every tar entry.
    concat = subprocess.Popen(["cat", *map(str, parts)], stdout=subprocess.PIPE)
    decode = subprocess.Popen(["zstd", "-dc", "-q"], stdin=concat.stdout,
                              stdout=subprocess.PIPE)
    concat.stdout.close()
    try:
        with tarfile.open(fileobj=decode.stdout, mode="r|") as archive:
            for entry in archive:
                name = entry.name
                if name.startswith("./"):
                    name = name[2:]
                if not allowed(name, kind):
                    raise RuntimeError(f"Unsafe or unexpected archive path: {entry.name}")
                target = dest / name
                if not entry.isdir() and not entry.isfile():
                    raise RuntimeError(f"Unsupported archive member: {entry.name}")
                if entry.isdir():
                    target.mkdir(parents=True, exist_ok=True)
                else:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    if target.is_symlink():
                        raise RuntimeError(f"Refusing to overwrite symlink: {name}")
                    stream = archive.extractfile(entry)
                    if stream is None:
                        raise RuntimeError(f"Missing archive data: {name}")
                    with target.open("wb") as out:
                        shutil.copyfileobj(stream, out, length=8 * 1024 * 1024)
    finally:
        decode.stdout.close()
        dec_code = decode.wait()
        cat_code = concat.wait()
    if dec_code or cat_code:
        raise RuntimeError(f"Truncated compressed archive: zstd={dec_code} cat={cat_code}")

def restore(kind):
    tag = TAGS[kind]
    with tempfile.TemporaryDirectory(prefix="aaris-restore-") as temp:
        folder = Path(temp)
        if not download(tag, MANIFEST[kind], folder):
            print(f"No {tag} manifest yet: cold checkout required.")
            return 3
        data = json.loads((folder / MANIFEST[kind]).read_text())
        if data.get("schema") != 1 or data.get("kind") != kind:
            raise RuntimeError(f"Invalid {kind} manifest schema")
        if kind == "engine":
            if data.get("engine_key") != engine_key():
                print("Unity version/packages changed: engine cache not reusable.")
                return 3
        elif data.get("source_sha") != head():
            print("Project checkpoint based on different Git commit; refusing stale overlay.")
            return 3
        elif not data.get("files") or not data.get("parts"):
            # Older versions could publish empty manifests. They are not
            # successful recoveries and must not mask a cold checkout.
            print("Empty legacy project checkpoint; no changes to restore.")
            return 3
        parts = []
        for part in data["parts"]:
            name = part["name"]
            if not name.startswith(kind + "-") or "/" in name:
                raise RuntimeError("Unexpected checkpoint part name")
            if not download(tag, name, folder):
                raise RuntimeError(f"Missing Release part: {name}")
            local = folder / name
            if local.stat().st_size != part["bytes"] or sha(local) != part["sha256"]:
                raise RuntimeError(f"SHA256 mismatch in {name}")
            parts.append(local)
        if parts:
            unpack(parts, ROOT, kind)
        print(f"Restored {kind} checkpoint: {len(parts)} checksum-verified chunks.")
        return 0

def selftest():
    from unittest.mock import patch
    with patch(__name__ + ".project_paths", return_value=[]), \
         patch(__name__ + ".ensure_release", side_effect=AssertionError("Empty checkpoint uploaded")):
        assert save("project") == 0
    if shutil.which("zstd") is None:
        raise RuntimeError("Install zstd before selftest.")
    with tempfile.TemporaryDirectory(prefix="aaris-test-") as temp:
        folder = Path(temp)
        a = folder / "source"
        b = folder / "dest"
        (a / "Assets").mkdir(parents=True)
        b.mkdir()
        (a / "Assets" / "sample.txt").write_text("Unity scene data\n")
        # Test archive/extraction with same safe formats as real saves.
        archive = folder / "test.tar.zst"
        producer = subprocess.Popen(["tar", "-cf", "-", "Assets/sample.txt"],
                                    cwd=a, stdout=subprocess.PIPE)
        subprocess.run(["zstd", "-q", "-3", "-o", str(archive)],
                       stdin=producer.stdout, check=True)
        producer.stdout.close()
        assert producer.wait() == 0
        chunks = split(archive, folder, "project-selftest")
        unpack([folder / x["name"] for x in chunks], b, "project")
        assert (b / "Assets/sample.txt").read_text() == "Unity scene data\n"
    print("Aaris checkpoint round-trip and safe extraction: PASS")

def main():
    p = argparse.ArgumentParser()
    p.add_argument("action", choices=["selftest", "save", "restore"])
    p.add_argument("kind", nargs="?", choices=["project", "engine"])
    args = p.parse_args()
    if args.action == "selftest":
        selftest()
    else:
        if not args.kind:
            p.error("save/restore requires project or engine")
        return save(args.kind) if args.action == "save" else restore(args.kind)
    return 0

if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:
        print(f"[Aaris checkpoint ERROR] {exc}", file=sys.stderr)
        sys.exit(1)
