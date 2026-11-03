"""Source-only regression guards, not a Unity rendering or physics run."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[3]
HOUSES = (
    "a_frame_cabin", "bungalow_house", "detached_two_storey_house",
    "garage_apartment", "pitched_roof_cottage", "row_three_townhouses",
    "two_bedroom_garage_house",
)


class LowRamWorldSourceContracts(unittest.TestCase):
    def test_saved_lods_preserve_distant_level(self):
        paths = [
            ROOT / f"Assets/AarisWorld/Models/{name}/{name}.prefab"
            for name in HOUSES
        ]
        paths.append(ROOT / "Assets/AarisWorld/Props/RockCover/RockCover.prefab")
        for path in paths:
            with self.subTest(prefab=path.name):
                source = path.read_text(encoding="utf-8")
                heights = re.findall(r"^  - screenRelativeHeight: ([0-9.]+)$", source, re.M)
                self.assertEqual(heights, ["0.28", "0.095", "0.0025"])
                self.assertIn("  m_AnimateCrossFading: 1", source)
                self.assertIn("  m_FadeMode: 1", source)

    def test_mobile_detail_caps_are_restorable(self):
        src = (ROOT / "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs").read_text(encoding="utf-8")
        for fragment in (
            "terrainDetailDistanceCap = constrained ? 55f : 80f;",
            "terrainDetailDensityCap = constrained ? 0.58f : 0.78f;",
            "terrain.detailObjectDistance = Mathf.Min(originalDetailDistances[i], terrainDetailDistanceCap);",
            "terrain.detailObjectDensity = Mathf.Min(originalDetailDensities[i], terrainDetailDensityCap);",
            "terrain.detailObjectDistance = originalDetailDistances[i];",
            "terrain.detailObjectDensity = originalDetailDensities[i];",
            "Mathf.Min(currentBudget, Mathf.Max(48f, currentBudget - 32f))",
        ):
            with self.subTest(fragment=fragment):
                self.assertIn(fragment, src)

    def test_collider_manager_recovers_after_reenable(self):
        src = (ROOT / "Assets/AarisWorld/Scripts/AarisNearbyColliders.cs").read_text(encoding="utf-8")
        for fragment in (
            "private void OnEnable()",
            "restoreProximityStateOnEnable = true;",
            "active.Clear();",
            "nextActive.Clear();",
            "if (c is TerrainCollider || c is CharacterController)",
            "if (indexed[i] && obstacles[i] != null) obstacles[i].enabled = false;",
        ):
            with self.subTest(fragment=fragment):
                self.assertIn(fragment, src)

if __name__ == "__main__":
    unittest.main()
