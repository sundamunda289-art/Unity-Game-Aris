#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SHADER = ROOT / "Assets/TerrainDemoScene_URP/ShaderGraphs/CustomFunctions/PseudoSubsurface.hlsl"

source = SHADER.read_text(encoding="utf-8")

required = (
    "UNITY_BRANCH if (fade >= 1.0h)",
    "half farHeightMask = Height01 * Height01 * (3.0h - 2.0h * Height01);",
    "TransmissionColor = terrainColor * (0.20h + 0.80h * farHeightMask) * 0.25h;",
    "half contact = saturate(1.0h - 2.0h * Height01);",
)
for token in required:
    assert token in source, token

assert source.index("UNITY_BRANCH if (fade >= 1.0h)") < source.index(
    "half contact = saturate(1.0h - 2.0h * Height01);"
)

for height in (0.0, 0.1, 0.25, 0.5, 0.75, 1.0):
    height_mask = height * height * (3.0 - 2.0 * height)
    old_far = (0.20 + 0.80 * height_mask) * (1.0 - 0.75)
    new_far = (0.20 + 0.80 * height_mask) * 0.25
    assert abs(old_far - new_far) < 1e-12, (height, old_far, new_far)

print("AARIS_FAR_DETAIL_FADE_EQUIVALENCE_PASS")
