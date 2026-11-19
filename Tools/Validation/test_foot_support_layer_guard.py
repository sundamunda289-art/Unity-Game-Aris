#!/usr/bin/env python3
"""Source-only foot-support regression. Native Play Mode remains authoritative."""
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
visual = (ROOT / "Assets/AarisWorld/Scripts/AarisCharacterVisual.cs").read_text()
scene = (ROOT / "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity").read_text()

method = visual.split("private float SupportedAnkleY(", 1)[1].split(
    "private void PlantUprightContact(", 1)[0]
# Default layer includes gameplay geometry and often the player's collider.
# Removing every actor-layer bit accidentally removes terrain support too.
assert "int worldMask = Physics.DefaultRaycastLayers;" in method
assert "& ~(1 << gameObject.layer)" not in method
assert "hit.collider.transform.IsChildOf(transform)" in method
assert "hit.normal.y < minWalkableNormalY" in method
assert "AarisPhysicsHitBuffer.Raycast(" in method
query_buffer = (ROOT / "Assets/AarisWorld/Scripts/AarisPhysicsHitBuffer.cs").read_text()
assert "QueryTriggerInteraction.Ignore" in query_buffer
# DefaultRaycastLayers includes layer 0 but excludes Unity Ignore Raycast (2).
mask = 0xFFFFFFFF & ~(1 << 2)
assert mask & (1 << 0)
assert not mask & (1 << 2)
assert mask & (1 << 8)
assert "m_Layer: 0" in scene
# World locks must be cleared on paused/teleported rig handoffs.
pause = visual.split("internal void SetPresentationPaused(", 1)[1].split(
    "private void LateUpdate()", 1)[0]
teleport = visual.split("if (movementDiscontinuity)", 1)[1].split(
    "float currentYaw", 1)[0]
for part in (pause, teleport):
    assert "leftUprightPlantLocked = false;" in part
    assert "rightUprightPlantLocked = false;" in part
print("PASS: support-layer contact and transient foot-lock reset")
print("NOT TESTED: native Unity sole placement or device FPS")
