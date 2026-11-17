"""Regression contracts for the offline plane/drop flight HUD.

Runs without Unity. Native compile and gameplay validation remain separate.
"""
from pathlib import Path
import math
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineDrop.cs").read_text(encoding="utf-8")


def model_clearance(plane_y, x, z, tiles, sea_y=-38.07552):
    """Mirror only the tile-boundary/world-Y math in the C# plane HUD."""
    support = sea_y
    for tx, tz, tw, td, terrain_world_y in tiles:
        if tx <= x <= tx + tw and tz <= z <= tz + td:
            support = max(support, terrain_world_y)
    return max(0.0, plane_y - support)


class FlightTelemetryContracts(unittest.TestCase):
    def test_aircraft_hud_fills_before_eject(self):
        flight_start = SOURCE.index("if (phase == DropPhase.InPlane)", SOURCE.index("private void Update()"))
        ejection = SOURCE.index("if (Input.GetKeyDown(KeyCode.Space)", flight_start)
        section = SOURCE[flight_start:ejection]
        self.assertIn("if (hud != null && hud.TelemetryDue)", section)
        self.assertIn("hud.SetTelemetry(SampleAircraftClearance(plane.transform.position)", section)
        self.assertIn("planeSpeed * 3.6f, phase", section)

    def test_no_per_frame_terrain_or_physics_poll(self):
        flight_start = SOURCE.index("if (phase == DropPhase.InPlane)", SOURCE.index("private void Update()"))
        ejection = SOURCE.index("if (Input.GetKeyDown(KeyCode.Space)", flight_start)
        section = SOURCE[flight_start:ejection]
        self.assertLess(section.index("hud.TelemetryDue"), section.index("SampleAircraftClearance("))
        sampler = SOURCE.split("private float SampleAircraftClearance(Vector3 aircraftPosition)", 1)[1].split(
            "private bool TryGetDistanceToSupport", 1)[0]
        self.assertNotIn("Physics.", sampler)
        self.assertNotIn("new ", sampler)
        self.assertIn("tile.SampleHeight(aircraftPosition)", sampler)
        self.assertIn("origin.y +", sampler)
        self.assertIn("SeaSurfaceHeight", sampler)

    def test_flight_speed_is_metres_per_second_to_kmph(self):
        self.assertIn("planeSpeed * 3.6f, phase", SOURCE)
        self.assertIn("Mathf.Sqrt(horizontalVelocity.sqrMagnitude +", SOURCE)
        self.assertIn("downwardSpeed * downwardSpeed) * 3.6f", SOURCE)
        self.assertAlmostEqual(76.0 * 3.6, 273.6)
        self.assertAlmostEqual(math.hypot(34, 25) * 3.6, 151.9168194, places=5)

    def test_world_height_and_sea_floor_boundaries(self):
        tile = [(-1000, -1000, 1000, 1000, -20.0)]
        self.assertAlmostEqual(model_clearance(400, -500, -500, tile), 420.0)
        self.assertAlmostEqual(model_clearance(400, 1500, 1500, tile), 438.07552)
        self.assertEqual(model_clearance(-100, -500, -500, tile), 0)
        # Native terrain far below the water plane must not produce sea-floor altitude.
        self.assertAlmostEqual(model_clearance(300, -500, -500,
                                              [(-1000, -1000, 1000, 1000, -120)]),
                               338.07552)


if __name__ == "__main__":
    unittest.main()
