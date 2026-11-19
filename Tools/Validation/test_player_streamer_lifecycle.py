#!/usr/bin/env python3
"""Source lifecycle regressions; Unity Play Mode remains unverified."""
import unittest
from pathlib import Path

SRC = Path(__file__).resolve().parents[2] / "Assets/AarisWorld/Scripts"
MOTOR = (SRC / "AarisThirdPersonPlayer.cs").read_text("utf-8")
STREAMER = (SRC / "AarisNearbyColliders.cs").read_text("utf-8")

class PlayerStreamerLifecycle(unittest.TestCase):
    def test_motor_disable_releases_held_controls(self):
        off = MOTOR[MOTOR.index("private void OnDisable()"):
                    MOTOR.index("private void PlaceCamera(bool instant)")]
        self.assertIn("ClearInput();", off)
        self.assertIn("RestorePlayerCameraVisibility();", off)
        self.assertLess(off.index("ClearInput();"), off.index("RestorePlayerCameraVisibility();"))
        cleanup = MOTOR[MOTOR.index("private void ClearInput()"):MOTOR.index("private void Layout()")]
        for token in ("moveFinger = lookFinger = -1;", "moveInput = Vector2.zero;",
                      "jumpUntil = -10f;", "jumpButtonHeld = false;",
                      "running = false;", "horizontalVelocity = Vector3.zero;"):
            self.assertIn(token, cleanup)
        self.assertIn("if (battle != null) battle.SetFireHeld(false);", cleanup)

    def test_vehicle_and_flight_handoffs_still_reset(self):
        vehicle = (SRC / "AarisVehicleSession.cs").read_text("utf-8")
        drop = (SRC / "AarisOfflineDrop.cs").read_text("utf-8")
        for code in (vehicle, drop):
            self.assertIn("footMotor.enabled = false;", code)
            self.assertIn("footMotor.enabled = true;", code)
        self.assertIn("footMotor.ResetCameraAfterVehicle();", vehicle)
        self.assertIn("footMotor.ResumeFromAerialDrop();", drop)

    def test_target_destruction_restores_original_colliders(self):
        update = STREAMER[STREAMER.index("private void Update()"):
                          STREAMER.index("public void RefreshNow()")]
        self.assertIn("if (target == null)", update)
        self.assertIn("enabled = false;", update)
        self.assertLess(update.index("if (target == null)"), update.index("enabled = false;"))
        off = STREAMER[STREAMER.index("private void OnDisable()"):]
        self.assertIn("if (streamed[i] && obstacles[i] != null)", off)
        self.assertIn("obstacles[i].enabled = true;", off)
        self.assertIn("if (c == null || !c.enabled) return;", STREAMER)

    def test_late_drop_component_uses_bounded_lookup_before_streaming(self):
        update = STREAMER[STREAMER.index("private void Update()"):
                          STREAMER.index("public void RefreshNow()")]
        self.assertIn("if (drop == null && Time.unscaledTime >= nextDropRefresh)", update)
        self.assertIn("nextDropRefresh = Time.unscaledTime + 1f;", update)
        self.assertIn("drop = target.GetComponent<AarisOfflineDrop>();", update)
        self.assertIn("if (drop != null && drop.IsRidingAircraft) return;", update)
        self.assertLess(update.index("drop = target.GetComponent<AarisOfflineDrop>();"),
                        update.index("drop != null && drop.IsRidingAircraft"))
        self.assertLess(update.index("drop != null && drop.IsRidingAircraft"),
                        update.index("RefreshNow(false);"))
        next_poll, lookups = 0.0, 0
        for frame in range(1, 1201):
            t = frame / 120.0
            if t >= next_poll:
                next_poll = t + 1.0
                lookups += 1
        self.assertGreaterEqual(lookups, 9)
        self.assertLessEqual(lookups, 11)


    def test_disabled_streamer_rejects_external_teleport_refresh(self):
        # OnDisable restores authored solid colliders. Public RefreshNow
        # must not re-disable them when an old vehicle/player reference
        # invokes it before the streamer has been re-enabled.
        method = STREAMER[STREAMER.index("public void RefreshNow()"):
                          STREAMER.index("private void RefreshNow(bool forcePhysicsSync)")]
        self.assertIn("if (!isActiveAndEnabled) return;", method)
        self.assertIn("RefreshNow(true);", method)
        self.assertLess(method.index("if (!isActiveAndEnabled) return;"),
                        method.index("RefreshNow(true);"))
        self.assertIn("if (forcePhysicsSync) Physics.SyncTransforms();", STREAMER)


if __name__ == "__main__":
    unittest.main(verbosity=2)
