#!/usr/bin/env python3
"""Verify every LFS payload against an immutable Git commit without changing files.

Default output preserves the original count/marker interface. --json is used by
the resumable provisioner. Neither mode prints asset paths or credentials.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import subprocess

POINTER_HEADER = b"version https://git-lfs.github.com/spec/v1"


def git(project: Path, *args: str, data: bytes | None = None) -> bytes:
    result = subprocess.run(
        ["git", "-C", str(project), *args], input=data,
        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        env={**os.environ, "GIT_TERMINAL_PROMPT": "0", "GIT_LFS_SKIP_SMUDGE": "1"},
        timeout=120, check=False,
    )
    if result.returncode:
        raise RuntimeError("GIT_READ_FAILED")
    return result.stdout


def commit_sha(project: Path, ref: str) -> str:
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_./-]{0,199}", ref):
        raise ValueError("INVALID_REF")
    value = git(project, "rev-parse", "--verify", ref + "^{commit}").decode().strip()
    if not re.fullmatch(r"[0-9a-f]{40,64}", value):
        raise ValueError("INVALID_COMMIT")
    return value


def tree_entries(project: Path, ref: str) -> list[dict]:
    result = []
    for record in git(project, "ls-tree", "-r", "-z", "--long", ref).split(b"\0"):
        if not record:
            continue
        info, raw_path = record.split(b"\t", 1)
        mode, kind, oid, size = info.split()
        rel = os.fsdecode(raw_path)
        parts = PurePosixPath(rel)
        if parts.is_absolute() or ".." in parts.parts or not parts.parts:
            raise ValueError("UNSAFE_GIT_PATH")
        result.append({"path": rel, "mode": mode.decode(), "kind": kind.decode(),
                       "oid": oid.decode(), "size": int(size) if size != b"-" else 0})
    return result


def pointer(data: bytes) -> dict | None:
    if not data.startswith(POINTER_HEADER):
        return None
    fields = {}
    for line in data.decode("ascii").splitlines():
        key, value = line.split(" ", 1)
        if key in fields:
            raise ValueError("INVALID_LFS_POINTER")
        fields[key] = value
    oid = fields.get("oid", "")
    size = fields.get("size", "")
    if (fields.get("version") != "https://git-lfs.github.com/spec/v1"
            or not re.fullmatch(r"sha256:[0-9a-f]{64}", oid)
            or not re.fullmatch(r"[0-9]+", size)):
        raise ValueError("INVALID_LFS_POINTER")
    return {"oid": oid[7:], "size": int(size)}


def inventory(project: Path, ref: str) -> list[dict]:
    # Read only small Git blobs in one process. No smudge or download occurs.
    entries = [item for item in tree_entries(project, ref)
               if item["kind"] == "blob" and item["mode"] != "120000" and item["size"] <= 1024]
    if not entries:
        return []
    if len(entries) > 200000:
        raise ValueError("GIT_INVENTORY_TOO_LARGE")
    contents = git(project, "cat-file", "--batch",
                   data="".join(item["oid"] + "\n" for item in entries).encode("ascii"))
    at = 0
    result = []
    for item in entries:
        end = contents.find(b"\n", at)
        header = contents[at:end].split()
        if end < 0 or len(header) != 3 or header[1] != b"blob":
            raise ValueError("GIT_INVENTORY_INVALID")
        length = int(header[2])
        at = end + 1
        data = contents[at:at + length]
        at += length + 1
        if len(data) != length:
            raise ValueError("GIT_INVENTORY_TRUNCATED")
        parsed = pointer(data)
        if parsed is not None:
            result.append({"path": item["path"], **parsed})
    return result


def safe_file(project: Path, relative: str) -> Path:
    parts = PurePosixPath(relative)
    if parts.is_absolute() or ".." in parts.parts or not parts.parts:
        raise ValueError("UNSAFE_FILE_PATH")
    current = project
    for part in parts.parts:
        current = current / part
        try:
            if stat.S_ISLNK(current.lstat().st_mode):
                raise ValueError("SYMLINK_FILE_PATH")
        except FileNotFoundError:
            pass
    return current


def digest_file(path: Path) -> str:
    value = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            value.update(chunk)
    return value.hexdigest()


def verify(project: Path, ref: str, items: list[dict] | None = None) -> dict:
    resolved = commit_sha(project, ref)
    items = inventory(project, resolved) if items is None else items
    result = {"ref": resolved, "tracked": len(items), "missing": 0,
              "pointers": 0, "mismatches": 0, "unsafe": 0, "verified_bytes": 0}
    for item in items:
        try:
            path = safe_file(project, item["path"])
            if not stat.S_ISREG(path.stat().st_mode):
                result["unsafe"] += 1
                continue
            with path.open("rb") as handle:
                if handle.read(len(POINTER_HEADER)) == POINTER_HEADER:
                    result["pointers"] += 1
                    continue
            if path.stat().st_size != item["size"] or digest_file(path) != item["oid"]:
                result["mismatches"] += 1
            else:
                result["verified_bytes"] += item["size"]
        except ValueError:
            result["unsafe"] += 1
        except OSError:
            result["missing"] += 1
    result["ok"] = not any(result[k] for k in ("missing", "pointers", "mismatches", "unsafe"))
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", default=str(Path(__file__).resolve().parents[2]))
    parser.add_argument("--ref", default="HEAD")
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args()
    try:
        result = verify(Path(args.project).resolve(), args.ref)
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError):
        result = {"ok": False, "error": "AARIS_LFS_LIST_UNAVAILABLE"}
        print(json.dumps(result) if args.json else result["error"])
        return 3
    if args.json:
        print(json.dumps(result, sort_keys=True))
    else:
        print(f"AARIS_LFS_TRACKED_{result['tracked']}")
        print(f"AARIS_LFS_POINTERS_{result['pointers']}")
        print(f"AARIS_LFS_MISSING_{result['missing']}")
        print(f"AARIS_LFS_MISMATCHES_{result['mismatches']}")
        print(f"AARIS_LFS_UNSAFE_{result['unsafe']}")
        if result["ok"] and result["tracked"]:
            print("AARIS_LFS_HYDRATED")
        elif not result["tracked"]:
            print("AARIS_LFS_TRACKED_ZERO")
    if not result["tracked"]:
        return 4
    return 0 if result["ok"] else 2


if __name__ == "__main__":
    raise SystemExit(main())

