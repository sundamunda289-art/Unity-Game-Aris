#!/usr/bin/env python3
"""Fetch movement-only public mocap sources for an isolated Unity QA gym.

Downloads into a temporary directory; never edits the production map,
legacy character resources, or source prefab. Export is blocked until all
files have been checked, licenses copied, and retargeting passes Unity.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import urllib.request
import zipfile

# Derivative motion-only GLBs of the original 100STYLE CC BY 4.0 dataset.
# This curated set contains no mesh/textures or other gameplay assets.
CLIPS = (
    "ArmsBySide_ID",
    "ArmsBySide_FW",
    "ArmsBySide_BW",
    "ArmsBySide_SW",
    "ArmsBySide_FR",
    "Crouched_ID",
    "Crouched_FW",
    "Crouched_BW",
    "Crouched_SW",
)
HF = ("https://huggingface.co/datasets/codepawl/100style-glb/"
      "resolve/main/skeleton/{name}/{name}_skeleton.glb")
ACCAD = "https://accad.osu.edu/sites/accad.osu.edu/files/Male1_bvh.zip"
MAX_CLIP_BYTES = 15_000_000
MAX_ACCAD_BYTES = 250_000_000


def fetch(url: str, target: Path, maximum: int):
    req = urllib.request.Request(url, headers={
        "User-Agent": "Aaris-Locomotion-Research/1.0"
    })
    with urllib.request.urlopen(req, timeout=100) as response, target.open("wb") as out:
        total = 0
        while True:
            piece = response.read(1024 * 1024)
            if not piece:
                break
            total += len(piece)
            if total > maximum:
                raise ValueError("Source exceeds safety size limit")
            out.write(piece)
    if total < 1000:
        raise ValueError("Source unexpectedly small")


def parse_glb(path: Path):
    # Validate nonempty actual glTF animation data; HTML placeholder is forbidden.
    data = path.read_bytes()
    if len(data) < 1000 or data[:4] != b"glTF":
        raise ValueError(f"{path.name}: invalid GLB header")
    magic, version, length = struct.unpack_from("<III", data, 0)
    if version != 2 or length != len(data):
        raise ValueError(f"{path.name}: malformed GLB length")
    json_size, json_type = struct.unpack_from("<II", data, 12)
    if json_type != 0x4E4F534A or json_size > len(data) - 20:
        raise ValueError(f"{path.name}: missing JSON chunk")
    obj = json.loads(data[20:20 + json_size])
    animations = obj.get("animations", [])
    if not animations or not obj.get("skins") or len(obj.get("nodes", [])) < 20:
        raise ValueError(f"{path.name}: not a skinned skeletal motion")
    if not any(a.get("channels") for a in animations):
        raise ValueError(f"{path.name}: animation has no channels")
    return {
        "name": path.stem,
        "bytes": len(data),
        "sha256": hashlib.sha256(data).hexdigest(),
        "animations": len(animations),
        "nodes": len(obj["nodes"]),
        "skins": len(obj["skins"]),
    }


def inspect_accad(path: Path):
    # We DO NOT import raw marker files or assume they are Unity-ready.
    with zipfile.ZipFile(path) as z:
        files = [x for x in z.namelist() if not x.endswith("/")]
        if not files or len(files) > 500:
            raise ValueError("Unexpected ACCAD archive structure")
        if not any(x.lower().endswith(".bvh") for x in files):
            raise ValueError("ACCAD archive has no BVH movements")
        details = [{
            "file": Path(x).name, "bytes": z.getinfo(x).file_size
        } for x in files if re.search(r"crawl|lie|lying|prone|crouch|stand|rise", x, re.I)]
    return {
        "archive_sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        "total_files": len(files),
        "candidate_stance_movements": details,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", default="/tmp/aaris-new-mocap")
    parser.add_argument("--inspect-accad", action="store_true")
    args = parser.parse_args()
    out = Path(args.output).resolve()
    out.mkdir(parents=True, exist_ok=True)
    manifest = {
        "source": "100STYLE motion-only GLB skeleton derivatives",
        "upstream": "https://www.ianxmason.com/100style/",
        "license": "CC BY 4.0",
        "credit": "100STYLE Dataset, Ian Mason, Sebastian Starke, Taku Komura",
        "clips": [],
    }
    for name in CLIPS:
        file = out / (name + ".glb")
        if not file.exists():
            fetch(HF.format(name=name), file, MAX_CLIP_BYTES)
        details = parse_glb(file)
        manifest["clips"].append(details)
        print("AARIS_NEW_MOCAP_SOURCE_PASS", name, details["bytes"],
              details["sha256"][:12], flush=True)
    if args.inspect_accad:
        archive = out / "ACCAD_Male1_bvh.zip"
        if not archive.exists():
            fetch(ACCAD, archive, MAX_ACCAD_BYTES)
        manifest["accad"] = inspect_accad(archive)
        manifest["accad"]["license"] = "CC BY 3.0"
        manifest["accad"]["source"] = "https://accad.osu.edu/research/motion-lab/mocap-system-and-data"
        print("AARIS_ACCAD_SOURCE_PASS",
              manifest["accad"]["total_files"],
              len(manifest["accad"]["candidate_stance_movements"]), flush=True)
        for candidate in manifest["accad"]["candidate_stance_movements"]:
            print("AARIS_ACCAD_STANCE_CANDIDATE",
                  candidate["file"], candidate["bytes"], flush=True)
    (out / "source-manifest.json").write_text(
        json.dumps(manifest, indent=2, sort_keys=True) + "\n")
    print("AARIS_NEW_MOCAP_NINE_CLIPS_VERIFIED")


if __name__ == "__main__":
    main()
