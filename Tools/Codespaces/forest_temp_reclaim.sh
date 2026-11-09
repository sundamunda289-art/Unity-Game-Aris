#!/usr/bin/env bash
# Reclaim ONLY ignored Unity Library caches in three audited, inactive scratch
# worktrees. Do not delete any Git checkout, source file, LFS object, TerrainData,
# .meta, prefab, user change or the original /workspaces workspace.
set -Eeuo pipefail
python3 - <<'PY'
import os
import pathlib
import re
import shutil
import subprocess

ROOT=pathlib.Path("/tmp/aaris-native-photo-worktrees")
SOURCE=pathlib.Path("/workspaces/Unity-Game-Aris")
# Audited read-only 2026-11-09. Other worktrees (including active forest bake)
# are intentionally excluded, regardless of their cache size.
OLD_PREFIXES=("332f06e8d6", "51380697bb", "8e27964700")

def fail(reason):
    print("FOREST_SCRATCH_LIBRARY_REFUSED_"+reason)
    raise SystemExit(1)

if not ROOT.is_dir() or ROOT.is_symlink() or not SOURCE.is_dir():
    fail("SOURCE_MISSING")
if subprocess.run(["pgrep", "-f", "[E]ditor/Unity.*-projectPath"],
                  capture_output=True).returncode==0:
    fail("EDITOR_RUNNING")
registered=subprocess.run(
    ["git","-C",str(SOURCE),"worktree","list","--porcelain"],
    capture_output=True,text=True,timeout=40)
if registered.returncode:
    fail("GIT_WORKTREE_REGISTRY_ERROR")
planned=[]
for prefix in OLD_PREFIXES:
    matches=[p for p in ROOT.iterdir()
             if p.name.startswith(prefix) and p.is_dir() and not p.is_symlink()]
    if len(matches)!=1:
        fail("REVIEW_WORKTREE_UNAVAILABLE")
    target=matches[0]
    if not re.fullmatch(r"[a-f0-9]{40}",target.name):
        fail("INVALID_COMMIT_FOLDER")
    if target.resolve().parent!=ROOT.resolve():
        fail("INVALID_WORKTREE_PATH")
    head=subprocess.run(["git","-C",str(target),"rev-parse","HEAD"],
        capture_output=True,text=True,timeout=40)
    if head.returncode or head.stdout.strip()!=target.name:
        fail("REVIEW_COMMIT_MISMATCH")
    if "worktree "+str(target)+"\n" not in registered.stdout:
        fail("REVIEW_NOT_REGISTERED")
    library=target/"Library"
    if library.is_symlink() or not library.is_dir():
        fail("LIBRARY_NOT_ISOLATED")
    if library.resolve().parent!=target.resolve():
        fail("LIBRARY_ESCAPES_WORKTREE")
    ignored=subprocess.run(["git","-C",str(target),"check-ignore","-q","--","Library"],
        capture_output=True,timeout=40)
    if ignored.returncode:
        fail("LIBRARY_NOT_GIT_IGNORED")
    tracked=subprocess.run(["git","-C",str(target),"ls-files","--","Library"],
        capture_output=True,text=True,timeout=40)
    if tracked.returncode or tracked.stdout.strip():
        fail("LIBRARY_CONTAINS_TRACKED_ASSETS")
    planned.append((prefix,library))

def free_mib():
    v=os.statvfs("/tmp")
    return v.f_bavail*v.f_frsize//1048576

before=free_mib()
print("FOREST_SCRATCH_FREE_MIB_BEFORE="+str(before))
# All three paths have passed full safety preflight before the first removal.
for prefix,lib in planned:
    shutil.rmtree(lib)
    print("FOREST_SCRATCH_IGNORED_LIBRARY_RECLAIMED="+prefix)
after=free_mib()
print("FOREST_SCRATCH_FREE_MIB_AFTER="+str(after))
print("FOREST_SCRATCH_ORIGINAL_ASSETS_PRESERVED=yes")
if after<6000:
    fail("STILL_BELOW_MINIMUM_TEST_BUDGET")
PY
