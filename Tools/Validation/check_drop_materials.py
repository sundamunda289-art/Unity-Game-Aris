#!/usr/bin/env python3
"""Static regression for the offline cargo-plane presentation materials."""

from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
DROP = ROOT / "Assets/Resources/AarisDrop"
MATS = DROP / "Materials"

URP_LIT_GUID = "933532a4fcc9baf4fa0491de14d08ed7"
SUBJECT_GUID = "7c1250a80a8c49bbbc05ca030a9dd635"
METAL_NORMAL_GUID = "1e667989b45456ba8e8cda1597b81513"

def text(name: str) -> str:
    return (MATS / name).read_text(encoding="utf-8")

plane = (DROP / "CargoPlane.obj").read_text(encoding="utf-8")
vertices = sum(1 for line in plane.splitlines() if line.startswith("v "))
faces = sum(1 for line in plane.splitlines() if line.startswith("f "))
assert vertices >= 900, vertices
assert faces >= 1500, faces

realistic = (DROP / "CargoPlaneRealistic.obj").read_text(encoding="utf-8")
realistic_vertices = sum(1 for line in realistic.splitlines() if line.startswith("v "))
realistic_faces = sum(1 for line in realistic.splitlines() if line.startswith("f "))
assert 80000 <= realistic_vertices <= 100000, realistic_vertices
assert 40000 <= realistic_faces <= 50000, realistic_faces
for material_name in (
    "HulledMetal", "DarkCockpit", "WingPanels", "EngineDark", "SafetyAccent"
):
    assert f"usemtl {material_name}" in realistic, material_name

source = (DROP / "CargoPlaneRealistic_SOURCE.txt").read_text(encoding="utf-8")
assert "Asset ID: 32587" in source
assert "License: CC0 1.0 Universal" in source
assert "4fc998fda721e47fef83e5615a11a62eddb947486ae87baecabb9ed1e8623da4" in source
assert "e8be17599956157d4304bfe6f083d27428f900ddfb79ab52da5aed5057148ae8" in source

hull = text("CargoPlane-HulledMetal.mat")
wing = text("CargoPlane-WingPanels.mat")
engine = text("CargoPlane-EngineDark.mat")
safety = text("CargoPlane-SafetyAccent.mat")
cockpit = text("CargoPlane-DarkCockpit.mat")

for material in (hull, wing, engine, safety, cockpit):
    shader = SUBJECT_GUID if material in (hull, wing) else URP_LIT_GUID
    assert f"m_Shader: {{fileID: 4800000, guid: {shader}, type: 3}}" in material
    if shader == SUBJECT_GUID:
        assert "_AARIS_AIRFRAME" in material and "_AircraftMarkings: 1" in material
    assert "RenderType: Opaque" in material

for material, expected_scale in (
    (hull, 0.24),
    (wing, 0.28),
    (engine, 0.10),
    (safety, 0.16),
):
    assert "- _NORMALMAP" in material
    assert METAL_NORMAL_GUID in material
    match = re.search(r"- _BumpScale: ([0-9.]+)", material)
    assert match
    actual = float(match.group(1))
    assert abs(actual - expected_scale) < 1e-6, (actual, expected_scale)
    assert 0.05 <= actual <= 0.28

# Cockpit stays smooth/glazed; do not stamp a rough metal normal onto glass.
assert METAL_NORMAL_GUID not in cockpit
assert "- _Smoothness: 0.82" in cockpit

visuals = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineDropVisuals.cs").read_text(
    encoding="utf-8"
)
assert 'Resources.Load<GameObject>("AarisDrop/CargoPlaneRealistic")' in visuals
assert 'new float[] { -4f, 4f }' in visuals
assert "rotorScale = highDetailTwin ? 1.17f : 1f" in visuals

for saved_name in (
    "CargoPlane-HulledMetal",
    "CargoPlane-DarkCockpit",
    "CargoPlane-WingPanels",
    "CargoPlane-EngineDark",
    "CargoPlane-SafetyAccent",
):
    assert saved_name in visuals

print(
    "PASS cargo plane: high-detail CC0 flight mesh + fallback mesh + "
    "shared URP metal detail; no runtime network dependency"
)
