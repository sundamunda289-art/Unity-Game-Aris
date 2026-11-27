#!/usr/bin/env python3
"""Deterministic source/pacing guard for planted loot and its beacon.

This is a Python model of the timing contract, not Unity rendering or GPU FPS.
"""
from pathlib import Path
import unittest

SRC = (Path(__file__).resolve().parents[2] /
       "Assets/AarisWorld/Scripts/AarisWeaponPickup.cs").read_text()


class LootVisibilityPacing(unittest.TestCase):
    def test_floor_pose_has_no_render_frame_transform_work(self):
        update = SRC.split("private void Update()", 1)[1].split(
            "private void OnEnable()", 1
        )[0]
        self.assertNotIn("model.Rotate(", update)
        self.assertNotIn("isVisible", update)
        self.assertIn("private const int MaximumActiveLoot = 40;", (
            Path(__file__).resolve().parents[2] /
            "Assets/AarisWorld/Scripts/AarisWeaponLootWorld.cs"
        ).read_text())

    def test_beacon_does_not_draw_without_an_active_camera(self):
        update = SRC.split("private void Update()", 1)[1].split(
            "private void OnEnable()", 1
        )[0]
        self.assertIn("if (beaconObserver == null || !beaconObserver.isActiveAndEnabled)", update)
        self.assertIn("else if (beacon.enabled)", update)
        self.assertIn("beacon.enabled = false;", update)
        self.assertIn("nextBeaconVisibilityCheck = Time.unscaledTime + 0.45f;", update)

    def test_missing_camera_lookup_shared_by_all_pickups(self):
        # Forty streamed weapons must not each call Camera.main during
        # Android focus loss / scene swap. Lookup is shared and throttled.
        self.assertIn("private static Camera sharedBeaconCamera;", SRC)
        self.assertIn("private static float nextSharedCameraLookup;", SRC)
        self.assertIn("if (Time.unscaledTime < nextSharedCameraLookup) return null;", SRC)
        self.assertIn("nextSharedCameraLookup = Time.unscaledTime + 0.45f;", SRC)
        self.assertIn("sharedBeaconCamera = null;", SRC)
        self.assertIn("nextSharedCameraLookup = 0f;", SRC)
        self.assertEqual(SRC.count("Camera.main"), 1)
        self.assertEqual(SRC.count("beaconObserver = FindBeaconCamera();"), 2)
        for fps in (15, 30, 60, 90, 120):
            next_lookup, calls = 0.0, 0
            for frame in range(2 * fps):
                now = frame / fps
                for _ in range(40):
                    if now < next_lookup:
                        continue
                    next_lookup = now + 0.45
                    calls += 1
            self.assertLessEqual(calls, 5, fps)


if __name__ == "__main__":
    unittest.main(verbosity=2)
