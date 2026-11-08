#!/usr/bin/env bash
# Read-only rescue of real Unity camera frames from the SAME existing Codespace.
# Only the bounded /tmp drop-probe outputs are included; never license or logs.
set -Eeuo pipefail
umask 077
python3 - <<'PY'
import json,re,tarfile
from pathlib import Path

root=Path("/tmp/aaris-codespaces/offline-drop-probe")
frames=root/"frames"
shots=root/"shots"
output=root/"evidence.tar.gz"
if not frames.is_dir():
    print("AARIS_DROP_FRAMES_DIRECTORY_MISSING")
    raise SystemExit(31)
images=sorted(p for p in frames.iterdir() if re.fullmatch(r"frame_[0-9]{5}\.jpg",p.name))
if len(images)<1 or len(images)>800:
    print("AARIS_DROP_FRAME_COUNT_INVALID")
    raise SystemExit(32)
allowed_phases={"InPlane","Freefall","Opening","Gliding","Landed"}
snapshots=sorted(p for p in shots.iterdir() if
    re.fullmatch(r"(InPlane|Freefall|Opening|Gliding|Landed)\.png",p.name)) if shots.is_dir() else []
selected=[*images,*snapshots]
report=root/"report.json"
if report.is_file():
    try:
        data=json.loads(report.read_text(encoding="utf-8"))
        if data.get("mode")=="offline_drop_video":
            selected.append(report)
    except (OSError,ValueError):
        pass
total=0
for f in selected:
    if (not f.is_file() or f.is_symlink() or
            f.resolve().parent not in {root.resolve(),frames.resolve(),shots.resolve()}):
        print("AARIS_DROP_FRAMES_UNSAFE_PATH")
        raise SystemExit(33)
    size=f.stat().st_size
    if size<64 or size>8*1024*1024:
        print("AARIS_DROP_FRAMES_UNSAFE_SIZE")
        raise SystemExit(34)
    total+=size
    if total>175*1024*1024:
        print("AARIS_DROP_FRAMES_ARCHIVE_LIMIT")
        raise SystemExit(35)
with tarfile.open(output,"w:gz",compresslevel=4) as archive:
    for file in selected:
        archive.add(file,arcname=str(file.relative_to(root)),recursive=False)
print("AARIS_DROP_UNITY_CAMERA_FRAMES_ARCHIVED")
print("frame_count="+str(len(images)))
print("package_bytes="+str(output.stat().st_size))
PY
