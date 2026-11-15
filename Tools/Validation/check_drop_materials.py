#!/usr/bin/env python3
"""Static regression for the offline cargo-plane presentation materials."""

from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
DROP = ROOT / "Assets/Resources/AarisDrop"
MATS = DROP / "Materials"

URP_LIT_GUID = "933532a4fcc9baf4fa0491de14d08ed7"
METAL_NORMAL_GUID = "1e667989b45456ba8e8cda1597b81513"

def text(name: str) -> str:
    return (MATS / name).read_text(encoding="utf-8")

plane = (DROP / "CargoPlane.obj").read_text(encoding="utf-8")
vertices = sum(1 for line in plane.splitlines() if line.startswith("v "))
faces = sum(1 for line in plane.splitlines() if line.startswith("f "))
assert vertices >= 900, vertices
assert faces >= 1500, faces

hull = text("CargoPlane-HulledMetal.mat")
wing = text("CargoPlane-WingPanels.mat")
engine = text("CargoPlane-EngineDark.mat")
safety = text("CargoPlane-SafetyAccent.mat")
cockpit = text("CargoPlane-DarkCockpit.mat")

for material in (hull, wing, engine, safety, cockpit):
    assert URP_LIT_GUID in material
    assert "RenderType: Opaque" in material

for material, expected_scale in (
    (hull, 0.12),
    (wing, 0.16),
    (engine, 0.10),
    (safety, 0.08),
):
    assert "- _NORMALMAP" in material
    assert METAL_NORMAL_GUID in material
    match = re.search(r"- _BumpScale: ([0-9.]+)", material)
    assert match
    actual = float(match.group(1))
    assert abs(actual - expected_scale) < 1e-6, (actual, expected_scale)
    assert 0.05 <= actual <= 0.20

# Cockpit stays smooth/glazed; do not stamp a rough metal normal onto glass.
assert METAL_NORMAL_GUID not in cockpit
assert "- _Smoothness: 0.82" in cockpit

visuals = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineDropVisuals.cs").read_text(
    encoding="utf-8"
)
for saved_name in (
    "CargoPlane-HulledMetal",
    "CargoPlane-DarkCockpit",
    "CargoPlane-WingPanels",
    "CargoPlane-EngineDark",
    "CargoPlane-SafetyAccent",
):
    assert saved_name in visuals

print(
    "PASS cargo plane: authored mesh + URP metal surface detail; "
    "shared normal map adds no new texture asset"
)
