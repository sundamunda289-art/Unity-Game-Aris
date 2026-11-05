"""Read-only quantitative mesh diagnostics for residential geometry.

Runs only after native LFS assets have been hydrated by fetch_assets.py.
Does not change a mesh, prove an interior route, or substitute for a Unity
render/physics import. Use side-by-side facts before any proposed replacement.
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

MODELS = (
    "bungalow_house",
    "detached_two_storey_house",
    "pitched_roof_cottage",
    "garage_apartment",
    "row_three_townhouses",
    "a_frame_cabin",
    "two_bedroom_garage_house",
)


def diagnose(path: Path):
    if not path.is_file() or pointer(path):
        raise ValueError(f"Native LFS mesh unavailable: {path.relative_to(ROOT)}")
    _, docs = documents(path)
    rows = [d["data"] for d in docs if d["kind"] == "Mesh"]
    if len(rows) != 1:
        raise ValueError(f"Expected exactly one Mesh: {path.relative_to(ROOT)}")
    pos, groups, _ = arrays(rows[0])
    tri = np.concatenate(groups)
    if not len(tri):
        raise ValueError("No mesh triangles")
    points = pos[tri].astype(np.float64)
    ab = points[:, 1] - points[:, 0]
    ac = points[:, 2] - points[:, 0]
    bc = points[:, 2] - points[:, 1]
    ablen = np.linalg.norm(ab, axis=1)
    aclen = np.linalg.norm(ac, axis=1)
    bclen = np.linalg.norm(bc, axis=1)
    maxedge = np.maximum(np.maximum(ablen, aclen), bclen)
    double_area = np.linalg.norm(np.cross(ab, ac), axis=1)
    bbspan = np.max(pos, axis=0)-np.min(pos,axis=0)
    diagonal = float(np.linalg.norm(bbspan))
    valid = np.isfinite(double_area).all() and np.isfinite(maxedge).all()
    if not valid:
        raise ValueError("Non-finite triangle geometry")
    result = {
        "file": path.relative_to(ROOT).as_posix(),
        "vertices": len(pos),
        "triangles": len(tri),
        "submesh_triangles": [len(group) for group in groups],
        "bbox_xyz_m": [round(float(v), 4) for v in bbspan],
        "degenerate_triangles": int(np.count_nonzero(double_area < 1e-8)),
        "short_edge_lt_1mm": int(np.count_nonzero(maxedge < .001)),
        "long_triangles_gt_half_bbox_diag": int(np.count_nonzero(maxedge > .5 * diagonal)),
        "long_triangles_gt_80pct_bbox_diag": int(np.count_nonzero(maxedge > .8 * diagonal)),
        "edge_length_median_m": round(float(np.median(maxedge)), 5),
        "edge_length_p99_m": round(float(np.percentile(maxedge,99)), 5),
        "edge_length_max_m": round(float(maxedge.max()), 5),
    }
    return result


def main():
    models = []
    for model in MODELS:
        levels = []
        for name in ("Geometry.asset", "Geometry_LOD1.asset", "Geometry_LOD2.asset"):
            levels.append(diagnose(ROOT / "Assets" / "AarisWorld" / "Models" / model / name))
        models.append({"model": model, "levels": levels})
    result={"status":"read_only_mesh_geometry_inspected","models":models,
            "limits":"Source triangles only; rendered spikes, materials and interior playability still need Unity screenshots and route probes."}
    print("AARIS_HOUSE_GEOMETRY_DIAGNOSTIC="+json.dumps(result, separators=(",",":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
