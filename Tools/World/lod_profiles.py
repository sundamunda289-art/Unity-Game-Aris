"""Scalar-only saved prefab LOD edits; no asset generation or runtime scanning."""
from __future__ import annotations

import hashlib
import math
import re
from pathlib import Path

from unity_data import WorldError

THRESHOLD = re.compile(r"(?m)^(  - screenRelativeHeight: )([^\r\n]+)$")


def rewrite_thresholds(body, values):
    matches = list(THRESHOLD.finditer(body))
    if (not isinstance(values, list) or not 2 <= len(matches) <= 8 or len(values) != len(matches) or
            any(type(v) not in (int, float) or not math.isfinite(v) or not 0 < v <= 1 for v in values) or
            any(a <= b for a, b in zip(values, values[1:]))):
        raise WorldError("Supply the same 2..8 strictly decreasing finite LOD thresholds in (0,1]")
    old = [float(m[2]) for m in matches]
    if values[-1] != old[-1]:
        raise WorldError("Preserve the final culling threshold; this operation edits intermediate LODs only")
    for m, old_value, new_value in reversed(list(zip(matches, old, values))):
        if old_value != new_value:
            body = body[:m.start(2)] + repr(float(new_value)) + body[m.end(2):]
    return body


# These pairs are the reviewed transition-only follow-up to the historical
# tree-shape contract. Reverse them in memory, then require that exact historical
# SHA256. This permits no collider, shape, bounds, renderer or GUID exceptions.
REVIEWED_TREE_LODS = {
    "Conifer/Conifer": ([0.6882781, 0.28432852, 0.05470773, 0.01], [0.6882781, 0.34, 0.10, 0.01]),
    "Cypress/Cypress_Forest_Desktop": ([0.58904696, 0.29313132, 0.02976629, 0.004869537], [0.58904696, 0.32, 0.06, 0.004869537]),
    "Pines/Pine_A/Pine_A": ([0.5, 0.25, 0.07945386, 0.01], [0.5, 0.30, 0.11, 0.01]),
    "Pines/Pine_B/Pine_B": ([0.5, 0.25, 0.08912427, 0.01], [0.5, 0.30, 0.12, 0.01]),
    "Pines/Pine_C/Pine_C": ([0.43247095, 0.17395622, 0.046491824, 0.01], [0.43247095, 0.24, 0.08, 0.01]),
    "Pines/Pine_D/Pine_D": ([0.5, 0.25, 0.056679536, 0.01], [0.5, 0.30, 0.095, 0.01]),
}
TREE_PREFIX = "Assets/TerrainDemoScene_URP/Prefabs/Trees/"


def preserved_tree_lod_only(root, path, expected_sha):
    source = (Path(root)/path).read_bytes()
    if hashlib.sha256(source).hexdigest() == expected_sha:
        return True
    # A completed explicit bake preserves geometry in native assets and retires
    # only source tags/mesh references. Reverse those exact hash-bound changes
    # before checking this older LOD-only migration. No callback is required.
    from bake_tree_core import CONTRACT, preserved_source_bytes
    if (Path(root)/CONTRACT).exists():
        source = preserved_source_bytes(root, path, source)
        if source is None:
            return False
        if hashlib.sha256(source).hexdigest() == expected_sha:
            return True
    key = path.removeprefix(TREE_PREFIX).removesuffix(".prefab")
    if not path.startswith(TREE_PREFIX) or not path.endswith(".prefab") or key not in REVIEWED_TREE_LODS:
        return False
    text = source.decode('utf-8')
    groups = list(re.finditer(r'(?ms)^--- !u!205 &[^\r\n]+\r?\n(.*?)(?=^--- !u!|\Z)', text))
    if len(groups) != 1:
        return False
    group = groups[0]
    old, new = REVIEWED_TREE_LODS[key]
    if [float(m[2]) for m in THRESHOLD.finditer(group[1])] != new:
        return False
    body = rewrite_thresholds(group[1], old)
    restored = text[:group.start(1)] + body + text[group.end(1):]
    return hashlib.sha256(restored.encode()).hexdigest() == expected_sha
