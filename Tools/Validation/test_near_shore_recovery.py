"""Regression guards for the near-shore fallback in offline parachute mode."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "Assets/AarisWorld/Scripts/AarisOfflineDrop.cs"


class OceanShoreRecoveryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = SOURCE.read_text(encoding="utf-8")
        cls.recovery = cls.source.split("private void RecoverFromOceanFall()", 1)[1].split(
            "private bool TryGetDistanceToSupport(", 1)[0]

    def test_nearby_shore_precedes_original_safe_spawn_fallback(self):
        recovery = self.recovery
        self.assertIn("capsule.enabled = false;", recovery)
        self.assertIn("TryFindNearbyShore(waterPosition, out Vector3 shore)", recovery)
        self.assertLess(recovery.index("capsule.enabled = false;"),
                        recovery.index("TryFindNearbyShore(waterPosition"))
        self.assertIn("transform.SetPositionAndRotation(shore, cachedPlayerRotation);", recovery)
        self.assertIn("transform.SetPositionAndRotation(preFlightGroundPosition, cachedPlayerRotation);", recovery)
        self.assertIn("Land();", recovery)
        self.assertIn("AARIS_DROP_OCEAN_RECOVERY_NEAR_SHORE", recovery)

    def test_shore_search_is_bounded_and_only_during_recovery(self):
        self.assertEqual(self.source.count("TryFindNearbyShore(waterPosition, out Vector3 shore)"), 1)
        self.assertIn("float[] radii = { 0f, 8f, 16f, 32f, 64f, 96f, 144f, 192f };",
                      self.recovery)
        self.assertIn("const int directions = 16;", self.recovery)
        self.assertIn("ring < radii.Length", self.recovery)
        self.assertIn("direction < count", self.recovery)

    def test_only_walkable_unobstructed_above_water_native_terrain(self):
        for token in (
            "tile.terrainData == null", "!tile.gameObject.activeInHierarchy",
            "TerrainCollider terrainCollider = tile.GetComponent<TerrainCollider>();",
            "terrainCollider == null || !terrainCollider.enabled",
            "ground < SeaSurfaceHeight + 1.5f", "GetInterpolatedNormal(u, v).y < minNormalY",
            "Physics.CheckCapsule(", "QueryTriggerInteraction.Ignore",
            "capsule.skinWidth + 0.12f", "shore = candidate;",
        ):
            with self.subTest(token=token):
                self.assertIn(token, self.recovery)


if __name__ == "__main__":
    unittest.main()
