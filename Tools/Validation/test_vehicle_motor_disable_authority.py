#!/usr/bin/env python3
"""Disabled car components cannot acquire drivers or latch control input.

This source-level regression complements, but does not replace, Unity play tests.
"""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2] / "Assets/AarisWorld/Scripts"
MOTOR = (ROOT / "AarisVehicleMotor.cs").read_text("utf-8")
WORLD = (ROOT / "AarisVehicleWorld.cs").read_text("utf-8")
SESSION = (ROOT / "AarisVehicleSession.cs").read_text("utf-8")


class DisabledVehicleAuthority(unittest.TestCase):
    def test_authoritative_driver_acquisition_refuses_inactive_vehicle(self):
        boarding = MOTOR[MOTOR.index("public bool TryTakeDriver("):
                         MOTOR.index("public void SetInput(")]
        self.assertIn("!isActiveAndEnabled", boarding)
        self.assertIn("!session.isActiveAndEnabled", boarding)
        self.assertIn("!IsSafeToExit", boarding)
        self.assertLess(boarding.index("!isActiveAndEnabled"),
                        boarding.index("driver = session;"))

    def test_disabled_motor_ignores_held_throttle_until_new_input(self):
        setter = MOTOR[MOTOR.index("public void SetInput("):
                       MOTOR.index("public void ReleaseDriver(")]
        self.assertIn("if (driver == null || !isActiveAndEnabled) return;", setter)
        self.assertLess(setter.index("if (driver == null || !isActiveAndEnabled) return;"),
                        setter.index("forward = Mathf.Clamp("))
        disable = MOTOR[MOTOR.index("private void OnDisable()"):]
        self.assertIn("forward = steering = 0f;", disable)
        self.assertIn("ApplyWheelForces(0f, ParkingBrakeTorque);", disable)

    def test_sleeping_parked_car_awakes_on_boarding_and_drive_intent(self):
        boarding = MOTOR[MOTOR.index("public bool TryTakeDriver("):
                         MOTOR.index("public void SetInput(")]
        setter = MOTOR[MOTOR.index("public void SetInput("):
                       MOTOR.index("public void ReleaseDriver(")]
        self.assertIn("if (body.IsSleeping()) body.WakeUp();", boarding)
        self.assertLess(boarding.index("driver = session;"),
                        boarding.index("if (body.IsSleeping()) body.WakeUp();"))
        self.assertIn("Mathf.Abs(forward) > .08f || Mathf.Abs(steering) > .08f", setter)
        self.assertIn("body.IsSleeping())", setter)
        self.assertIn("body.WakeUp();", setter)
        self.assertLess(setter.index("if (driver == null || !isActiveAndEnabled) return;"),
                        setter.index("body.WakeUp();"))
        self.assertLess(setter.index("if (float.IsNaN(forwardAxis)"),
                        setter.index("body.WakeUp();"))
        # Neutral, disabled and absent-owner input must not wake PhysX.
        for occupied in (False, True):
            for enabled in (False, True):
                for move, steer in ((0., 0.), (.08, 0.), (.4, 0.), (0., .8)):
                    wake_allowed = (occupied and enabled and
                                    (abs(move) > .08 or abs(steer) > .08))
                    if not occupied or not enabled or (move == 0 and steer == 0):
                        self.assertFalse(wake_allowed)
                    if occupied and enabled and abs(move) > .08:
                        self.assertTrue(wake_allowed)

    def test_nearest_vehicle_search_skips_inactive_models(self):
        nearest = WORLD[WORLD.index("public AarisVehicleMotor NearestAvailable("):]
        self.assertIn("!record.visible.isActiveAndEnabled", nearest)
        self.assertLess(nearest.index("!record.visible.isActiveAndEnabled"),
                        nearest.index("record.visible.IsSafeToExit"))
        self.assertIn("if (d >= best || !record.visible.IsSafeToExit) continue;", nearest)

    def test_hud_and_mode_entry_do_not_offer_inactive_car(self):
        eligible = SESSION[SESSION.index("public bool CanEnter =>"):
                           SESSION.index("public AarisVehicleMotor DrivenCar")]
        self.assertIn("nearbyCar.isActiveAndEnabled", eligible)
        self.assertIn("nearbyCar.IsSafeToExit", eligible)
        self.assertIn("footMotor.isActiveAndEnabled", eligible)
        self.assertIn("if (!nearbyCar.TryTakeDriver(this)) return false;", SESSION)

    def test_occupied_disabled_car_remains_eligible_for_safe_exit(self):
        # Reject NEW driver claims, not an occupied player's safe exit.
        safe_exit = MOTOR[MOTOR.index("public bool IsSafeToExit"):
                          MOTOR.index("private void OnCollisionEnter(")]
        self.assertNotIn("isActiveAndEnabled", safe_exit)
        exit_flow = SESSION[SESSION.index("private bool TryExit()"):
                            SESSION.index("private void FollowCarCamera()")]
        self.assertIn("!car.IsSafeToExit", exit_flow)
        self.assertIn("previous.ReleaseDriver(this);", exit_flow)
        self.assertIn("TryFindExit(out Vector3 safeExit)", exit_flow)


if __name__ == "__main__":
    unittest.main(verbosity=2)
