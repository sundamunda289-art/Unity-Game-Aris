"""Zero-allocation XZ collider refresh pacing source and geometry contract.

This is NOT a native Unity Android FPS or C# compilation test.
"""
from pathlib import Path
import unittest

SRC = (Path(__file__).resolve().parents[2] /
       "Assets/AarisWorld/Scripts/AarisNearbyColliders.cs").read_text()


def due(now, deadline, origin, current):
    dx = current[0] - origin[0]
    dz = current[2] - origin[2]
    return dx * dx + dz * dz >= 9.0 or now >= deadline


class ColliderXZTests(unittest.TestCase):
    def test_vertical_only_does_not_rebuild_cells(self):
        self.assertFalse(due(0.5, 1, (0, 0, 0), (0, 900, 0)))
        self.assertFalse(due(0.5, 1, (0, 900, 0), (0, -900, 0)))

    def test_horizontal_and_periodic_safety(self):
        self.assertFalse(due(.5, 1, (0, 0, 0), (2, 99, 2)))
        self.assertTrue(due(.5, 1, (0, 0, 0), (3, 99, 0)))
        self.assertTrue(due(1, 1, (0, 0, 0), (0, 900, 0)))

    def test_exact_source_guard_and_no_visual_changes(self):
        body = SRC.split("private void Update()", 1)[1].split(
            "public void RefreshNow()", 1)[0]
        self.assertIn("Vector3 current = target.position;", body)
        self.assertIn("dx * dx + dz * dz >= RefreshTravelSquared", body)
        self.assertIn("Time.unscaledTime >= nextCheck) RefreshNow(false);", body)
        self.assertNotIn("(target.position - lastPosition).sqrMagnitude", body)
        self.assertIn("private const float RefreshTravelSquared = 9f;", SRC)
        self.assertIn("if (forcePhysicsSync) Physics.SyncTransforms();", SRC)


if __name__ == "__main__":
    unittest.main()
