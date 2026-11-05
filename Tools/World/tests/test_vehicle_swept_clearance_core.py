"""Geometry-only regression tests: no claim of Unity vehicle physics."""
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "Tools" / "World"))
from smart_residential_clearance import ClearanceError
from vehicle_swept_clearance import audit, swept_aabb


class SweptVehicleEnvelopeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.layout = json.loads((ROOT/"Docs/MAP_LAYOUT.json").read_text())
        cls.manifest = json.loads((ROOT/"Tools/World/reviewed-residential-vehicle-spawns-20261105.json").read_text())

    def test_axis_aligned_swept_envelope_includes_full_car_length(self):
        self.assertEqual(swept_aabb([10,0,20],[10,30],width_m=2,
                         length_m=4,margin_m=0.5), [8.5,17.5,11.5,32.5])
        self.assertEqual(swept_aabb([10,0,20],[0,20],width_m=2,
                         length_m=4,margin_m=0.5), [-2.5,18.5,12.5,21.5])

    def test_real_45_spawns_all_fit_source_car_envelope(self):
        result=audit(self.layout)
        self.assertEqual(result["status"],"2d_source_envelopes_clear")
        self.assertEqual(result["tested_spawns"],45)
        self.assertEqual(result["relocated_garage_spawns"],17)
        self.assertEqual(result["tested_buildings"],59)
        self.assertEqual({r["spawn"] for r in result["routes"] if r["relocated"]},
                         {r["spawn_name"] for r in self.manifest["rows"]})
        self.assertGreater(result["minimum_envelope_to_building_gap_m"],1.0)

    def test_oversized_vehicle_cannot_silently_pass(self):
        with self.assertRaisesRegex(ClearanceError,"intersects building"):
            audit(self.layout,vehicle_length_m=20)

    def test_future_house_scale_what_if_and_bad_inputs(self):
        report=audit(self.layout,desired_house_scales={
            "ClockTowerTown_garage_apartment_06":1.15})
        self.assertEqual(report["tested_spawns"],45)
        for bad in (float("nan"),-1,0):
            with self.assertRaises(ClearanceError):
                audit(self.layout,vehicle_width_m=bad)
        for bad in (float("nan"),-0.1):
            with self.assertRaises(ClearanceError):
                audit(self.layout,vehicle_margin_m=bad)
        with self.assertRaises(ClearanceError):
            swept_aabb([1,0,2],[3,4])
        with self.assertRaises(ClearanceError):
            swept_aabb([1,0,2],[1,2])


if __name__ == "__main__":
    unittest.main()
