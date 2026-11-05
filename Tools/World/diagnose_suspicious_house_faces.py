#!/usr/bin/env python3
"""Non-mutating LFS mesh face review; investigate possible visible house spikes.

Long faces are not necessarily broken. This only reports material-submesh
indices and the actual vertex positions behind unusually skinny/long faces.
Use a native Unity camera/LOD/material comparison before proposing removal.
"""
from __future__ import annotations

import json
import math
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE))
from unity_data import documents, pointer
from mesh_faces import arrays

TARGETS = ("bungalow_house", "pitched_roof_cottage",
           "detached_two_storey_house")
LEVELS = ("Geometry.asset", "Geometry_LOD1.asset", "Geometry_LOD2.asset")


def face_metrics(points):
    """Return lengths, altitudes, and needle aspect for array (tri,3,3)."""
    if points.ndim != 3 or points.shape[1:] != (3, 3):
        raise ValueError("Triangle positions must be Nx3x3")
    if points.size == 0:
        raise ValueError("At least one triangle required")
    if not np.isfinite(points).all():
        raise ValueError("Non-finite triangle positions")
    a = np.linalg.norm(points[:, 1] - points[:, 0], axis=1)
    b = np.linalg.norm(points[:, 2] - points[:, 1], axis=1)
    c = np.linalg.norm(points[:, 0] - points[:, 2], axis=1)
    longest = np.maximum(np.maximum(a, b), c)
    twice_area = np.linalg.norm(
        np.cross(points[:, 1] - points[:, 0],
                 points[:, 2] - points[:, 0]), axis=1)
    # Minimum height of a triangle is the altitude to its longest edge.
    min_altitude = np.divide(twice_area, longest, out=np.zeros_like(longest),
                             where=longest > 0)
    aspect = np.divide(longest, min_altitude,
                       out=np.full_like(longest, np.inf), where=min_altitude > 0)
    return longest, min_altitude, aspect


def inspect_native_mesh(path: Path) -> dict:
    if not path.is_file() or pointer(path):
        raise ValueError(f"Hydrated native LFS mesh required: {path.relative_to(ROOT)}")
    _, docs = documents(path)
    meshes = [d["data"] for d in docs if d["kind"] == "Mesh"]
    if len(meshes) != 1:
        raise ValueError("Expected exactly one native Mesh")
    positions, groups, _ = arrays(meshes[0])
    bbox = np.max(positions, axis=0) - np.min(positions, axis=0)
    diagonal = float(np.linalg.norm(bbox))
    if not diagonal or not math.isfinite(diagonal):
        raise ValueError("Invalid source bounding diagonal")
    records = []
    for slot, faces in enumerate(groups):
        if not len(faces):
            records.append({"slot":slot, "triangles":0, "long_80pct_diag":0,
                            "skinny_2m_edge_under_5cm_altitude":0,
                            "largest_needle_triangles":[]})
            continue
        pts = positions[faces].astype(np.float64)
        edges, altitudes, aspect = face_metrics(pts)
        extraordinary = edges > 0.8 * diagonal
        skinny = (edges >= 2.0) & (altitudes < 0.05)
        candidates = np.nonzero(skinny | extraordinary)[0]
        # Only emit bounded, read-only evidence; never auto-select removals.
        ordered = sorted(candidates.tolist(),
                         key=lambda i:(float(aspect[i]),float(edges[i])),
                         reverse=True)[:4]
        records.append({
            "slot":slot, "triangles":int(len(faces)),
            "long_80pct_diag":int(np.count_nonzero(extraordinary)),
            "skinny_2m_edge_under_5cm_altitude":int(np.count_nonzero(skinny)),
            "largest_needle_triangles":[{
                "index":int(i), "longest_edge_m":round(float(edges[i]),3),
                "minimum_altitude_m":round(float(altitudes[i]),5),
                "needle_aspect":round(float(aspect[i]),2)
                    if math.isfinite(aspect[i]) else None,
                "vertices_xyz_m":np.round(pts[i],4).tolist(),
            } for i in ordered],
        })
    return {"path":path.relative_to(ROOT).as_posix(),
            "bbox_m":[round(float(v),4) for v in bbox],
            "bbox_diagonal_m":round(diagonal,3),
            "submeshes":records,
            "long_face_count":sum(x["long_80pct_diag"] for x in records),
            "skinny_face_count":sum(x["skinny_2m_edge_under_5cm_altitude"]
                                     for x in records)}


def main():
    out = []
    for model in TARGETS:
        out.append({"model":model, "levels":[inspect_native_mesh(
            ROOT / "Assets/AarisWorld/Models" / model / level)
            for level in LEVELS]})
    print("AARIS_HOUSE_SUSPICIOUS_FACES=" + json.dumps({
        "status":"read_only_geometry_evidence",
        "models":out,
        "interpretation":"A long/skinny mesh face may be intentional trim, roof or wall; "
                         "it is NOT proof of a corrupt mesh. Compare Unity LOD+"
                         "shader+texture captures before native LFS edits.",
    },separators=(",",":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
