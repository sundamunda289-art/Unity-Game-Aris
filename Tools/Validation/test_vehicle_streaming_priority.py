#!/usr/bin/env python3
"""Prevent saturated vehicle pools from hiding nearby parked cars.

A source-level contract plus a small deterministic priority model. Real
Unity WheelCollider, streamed transforms and Android frame timing remain
outside this test and require native playtesting.
"""
import math
import unittest
from pathlib import Path

WORLD = (Path(__file__).resolve().parents[2] /
         "Assets/AarisWorld/Scripts/AarisVehicleWorld.cs").read_text("utf-8")


def pick_parked_victim(cars, new_distance):
    """Mirror the distance hysteresis and immutable parked-only priority."""
    threshold = (new_distance + 20.0) ** 2
    candidate = None
    for i, (distance, parked) in enumerate(cars):
        if not parked or distance ** 2 <= threshold:
            continue
        threshold = distance ** 2
        candidate = i
    return candidate


class VehicleStreamingPriority(unittest.TestCase):
    def test_pool_full_has_bounded_parked_car_replacement(self):
        update = WORLD.split("private void Update()", 1)[1].split(
            "private AarisVehicleRecord FarthestReplaceableParkedCar(", 1)[0]
        self.assertIn("if (active >= MaximumNearbyVehicles)", update)
        self.assertIn("if (unloaded >= unloadBudgetPerPoll) break;", update)
        self.assertIn("FarthestReplaceableParkedCar(", update)
        self.assertIn("if (parkedVictim == null) break;", update)
        self.assertIn("spawnProbes < spawnProbeBudgetPerPoll", update)
        self.assertIn("built < spawnBudgetPerPoll", update)
        self.assertIn("active--;", update)
        self.assertIn("active++;", update)

    def test_validated_placement_precedes_eviction(self):
        update = WORLD.split("private void Update()", 1)[1].split(
            "private AarisVehicleRecord FarthestReplaceableParkedCar(", 1)[0]
        self.assertLess(update.index("!TryResolvePlanned(record, out Vector3 pos)"),
                        update.index("Destroy(retired.gameObject);"))
        self.assertIn("if (retired == null || !retired.CanPark)", update)
        self.assertIn("record.nextProbe = now + .3f;", update)
        self.assertIn("parkedVictim.position = retired.transform.position;", update)
        self.assertIn("parkedVictim.rotation = retired.transform.rotation;", update)
        self.assertIn("parkedVictim.nextProbe = now + 3.0f;", update)

    def test_failed_replacement_never_evicts_the_old_car(self):
        # Reproduces a real-player case: placement passes, but FBX/wheel
        # validation returns false after an authored model was imported.
        update = WORLD.split("private void Update()", 1)[1].split(
            "private AarisVehicleRecord FarthestReplaceableParkedCar(", 1)[0]
        build = update.index("if (!Build(record, pos))")
        destroy_old = update.index("Destroy(retired.gameObject);")
        self.assertLess(build, destroy_old)
        failure = update[build:destroy_old]
        self.assertIn("record.nextProbe = now + 3.0f;", failure)
        self.assertIn("continue;", failure)
        self.assertNotIn("Destroy(retired.gameObject);", failure)
        # Late physics ownership change must discard the *new* body only.
        self.assertIn("if (retired == null || !retired.CanPark)", failure)
        self.assertIn("Destroy(record.visible.gameObject);", failure)
        self.assertIn("record.visible = null;", failure)
        self.assertIn("record.nextProbe = now + .3f;", failure)
        self.assertLess(destroy_old, update.index("active--;"))
        self.assertLess(update.index("active--;", build),
                        update.index("active++;", build))
        self.assertLess(update.index("active++;"), update.index("record.previouslySpawned = true;"))

    def test_parked_replacement_does_not_mutate_damage_on_failure(self):
        update = WORLD.split("private void Update()", 1)[1].split(
            "private AarisVehicleRecord FarthestReplaceableParkedCar(", 1)[0]
        build = update.index("if (!Build(record, pos))")
        saved_state = update.index("parkedVictim.position = retired.transform.position;")
        self.assertLess(build, saved_state)
        self.assertLess(saved_state, update.index("parkedVictim.damage01 = retired.Damage01;"))
        self.assertLess(update.index("parkedVictim.damage01 = retired.Damage01;"),
                        update.index("Destroy(retired.gameObject);"))
        # No excess replacement after reaching the native per-poll limits.
        self.assertIn("unloaded >= unloadBudgetPerPoll", update)
        self.assertIn("built < spawnBudgetPerPoll", update)

    def test_authored_car_pbr_conversion_is_released_on_scene_teardown(self):
        # Runtime materials are shared for streamed cars and must not leak
        # when the level reloads. Imported shared source assets remain alive.
        cleanup = WORLD.split("private void OnDestroy()", 1)[1].split(
            "public AarisVehicleMotor NearestAvailable(", 1)[0]
        self.assertIn("foreach (Material converted in authoredMaterialCache.Values)", cleanup)
        self.assertIn("if (converted != null) Destroy(converted);", cleanup)
        self.assertIn("authoredMaterialCache.Clear();", cleanup)

    def test_nearest_ready_vehicle_wins_regardless_of_match_plan_order(self):
        update = WORLD.split("private void Update()", 1)[1].split(
            "private AarisVehicleRecord FarthestReplaceableParkedCar(", 1)[0]
        self.assertIn("float nearestDistanceSq = LoadRadius * LoadRadius;", update)
        self.assertIn("foreach (var pending in plans)", update)
        self.assertIn("pending.visible != null || now < pending.nextProbe", update)
        self.assertIn("!authoredBodyResolved[pendingType]", update)
        self.assertIn("(distanceSq == nearestDistanceSq && record != null)", update)
        self.assertIn("record = pending;", update)
        self.assertIn("if (record == null) break;", update)
        self.assertLess(update.index("record = pending;"),
                        update.index("TryResolvePlanned(record, out Vector3 pos)"))
        def nearest(records):
            best, found = 105 ** 2, None
            for index, (distance, ready) in enumerate(records):
                distance_squared = distance ** 2
                if not ready or distance_squared > best or (
                    distance_squared == best and found is not None
                ):
                    continue
                best, found = distance_squared, index
            return found
        self.assertEqual(nearest([(94, True), (12, True), (51, True)]), 1)
        self.assertEqual(nearest([(4, False), (32, True), (10, False)]), 1)
        self.assertEqual(nearest([(27, True), (27, True), (30, True)]), 0)
        self.assertIsNone(nearest([(106, True), (200, True)]))

    def test_constrained_android_physics_probes_have_soft_time_budget(self):
        # Actual Unity physics timing must still be measured on the phone.
        update = WORLD.split("private void Update()", 1)[1].split(
            "private AarisVehicleRecord FarthestReplaceableParkedCar(", 1)[0]
        self.assertIn("AndroidSpawnProbeSliceSeconds = 0.0015d;", WORLD)
        self.assertIn("double spawnProbeSliceStart = Time.realtimeSinceStartupAsDouble;", update)
        self.assertIn("spawnProbes > 0 &&", update)
        self.assertIn("AarisMobileStreamingFrameGate.IsConstrainedDevice()", update)
        self.assertIn("Time.realtimeSinceStartupAsDouble - spawnProbeSliceStart >=", update)
        self.assertIn("AndroidSpawnProbeSliceSeconds)\n                    break;", update)
        self.assertLess(update.index("spawnProbes > 0 &&"),
                        update.index("AarisVehicleRecord record = null;"))
        self.assertLess(update.index("AarisVehicleRecord record = null;"),
                        update.index("spawnProbes++;"))
        self.assertIn("nextPoll = now + .3f;", update)
        # One attempt progresses even if the previous poll was expensive.
        def allowed(attempts, seconds, constrained):
            return attempts == 0 or not constrained or seconds < .0015
        self.assertTrue(allowed(0, .250, True))
        self.assertTrue(allowed(1, .0014, True))
        self.assertFalse(allowed(1, .0015, True))
        self.assertTrue(allowed(2, .010, False))

    def test_hysteresis_and_occupied_safety(self):
        helper = WORLD.split("private AarisVehicleRecord FarthestReplaceableParkedCar(", 1)[1].split(
            "private void OnDestroy()", 1)[0]
        self.assertIn("const float PriorityMarginMetres = 20f;", helper)
        self.assertIn("if (car == null || !car.CanPark) continue;", helper)
        self.assertIn("distanceSq <= farthestDistanceSq", helper)
        self.assertEqual(pick_parked_victim([(135, True), (80, True)], 35), 0)
        self.assertIsNone(pick_parked_victim([(70, True), (100, False)], 65))
        self.assertIsNone(pick_parked_victim([(10, True), (40, True)], 25))
        self.assertEqual(pick_parked_victim([(135, True), (150, True)], 20), 1)
        self.assertIsNone(pick_parked_victim([(150, False)], 20))


if __name__ == "__main__":
    unittest.main()
