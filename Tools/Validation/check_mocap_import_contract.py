#!/usr/bin/env python3
"""Lock the animation-only 100STYLE Humanoid hierarchy expected by Unity."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
META = (ROOT / "Assets/Resources/AarisMocap/mocap_locomotion.fbx.meta").read_text()
FBX = ROOT / "Assets/Resources/AarisMocap/mocap_locomotion.fbx"
ATTR = (ROOT / "Assets/Resources/AarisMocap/ATTRIBUTION.txt").read_text()
WF = (ROOT / ".github/workflows/aaris-vendor-100style-mocap.yml").read_text()

assert FBX.is_file() and 50_000 < FBX.stat().st_size < 5_000_000
assert "100STYLE dataset" in ATTR and "CC BY 4.0" in ATTR
assert "animationType: 3" in META and "avatarSetup: 1" in META
assert "    - name: mocap_locomotion\n      parentName:" in META
assert "    - name: mixamorig:Hips\n      parentName: mocap_locomotion" in META
assert "parentName: BamenMilitarySoldierRig" not in META
assert "    - name: BamenMilitarySoldierMesh\n" not in META
assert "    - name: BamenMilitarySoldierRig\n" not in META
for clip in ("mocap_walk", "mocap_jog", "mocap_run", "mocap_crouch"):
    assert clip in WF
assert "Unity rejects the Humanoid Avatar" in WF
print("PASS 100STYLE animation-only Humanoid root contract")
