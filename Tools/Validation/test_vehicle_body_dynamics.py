#!/usr/bin/env python3
"""Static regression checks for subtle visual body motion on live wheel physics.

Unity scene play mode, art screenshots and Android FPS must be tested separately.
"""
import math
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MOTOR = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleMotor.cs").read_text()
WORLD = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleWorld.cs").read_text()


class VehicleBodyDynamics(unittest.TestCase):
    def test_visual_pivot_mounted_after_authored_wheels_detached(self):
        build = WORLD.split("private bool Build(", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertIn("motor.ConfigureBodyDynamics(mesh.transform);", build)
        self.assertLess(build.index("if (!wheelsAttached)"),
                        build.index("motor.ConfigureBodyDynamics(mesh.transform);"))
        self.assertLess(build.index("motor.ConfigureBodyDynamics(mesh.transform);"),
                        build.index("motor.ConfigureExteriorLights(lampBounds,"))
        self.assertLess(build.index("motor.ConfigureBodyDynamics(mesh.transform);"),
                        build.index("motor.ConfigureBodyWear(mesh.transform"))
        setup = MOTOR.split("public void ConfigureBodyDynamics(", 1)[1].split(
            "private void SampleBodyDynamics()", 1)[0]
        for token in (
            "bodyModel.IsChildOf(transform)", '"Visual Body Suspension Pivot"',
            "pivot.transform.SetParent(transform, false);",
            "pivot.transform.localPosition = Vector3.zero;",
            "pivot.transform.localRotation = Quaternion.identity;",
            "pivot.transform.localScale = Vector3.one;",
            "bodyModel.SetParent(pivot.transform, true);",
            "visualBodyPivot = pivot.transform;"):
            self.assertIn(token, setup)
        for token in ("AddComponent<Rigidbody>", "AddComponent<WheelCollider>",
                      "new Material(", "new Mesh(", "new Collider("):
            self.assertNotIn(token, setup)
        self.assertIn("wheelVisual.SetParent(transform, true);", MOTOR)
        self.assertIn("visualWheels[i] = instance.transform;", MOTOR)

    def test_sway_uses_physics_acceleration_and_wheel_contact_not_input(self):
        sample = MOTOR.split("private void SampleBodyDynamics()", 1)[1].split(
            "private void UpdateVisualBodyDynamics()", 1)[0]
        for token in (
            "Vector3 velocity = body.linearVelocity;",
            "(velocity - previousPhysicsVelocity) /",
            "Mathf.Max(.005f, Time.fixedDeltaTime)",
            "previousPhysicsVelocity = velocity;",
            "driver != null && driver.isActiveAndEnabled",
            "int grounded = groundedWheelCount;",
            "grounded < 2", "groundedAxleMask != 3",
            "Vector3.Dot(transform.up, Vector3.up) < .45f",
            "transform.InverseTransformDirection(acceleration)",
            "float.IsNaN(local.x)", "float.IsInfinity(local.x)",
            "float.IsNaN(local.y)", "float.IsInfinity(local.z)"):
            self.assertIn(token, sample)
        self.assertNotIn("wheels[i].isGrounded", sample)
        self.assertIn("visualPitchTarget = Mathf.Clamp(-local.z * .18f, -1.8f, 1.8f);", sample)
        self.assertIn("visualRollTarget = Mathf.Clamp(local.x * .18f, -1.8f, 1.8f);", sample)
        self.assertIn("visualJounceTarget = Mathf.Clamp(-local.y * .0025f, -.022f, .022f);", sample)
        for forbidden in ("Physics.Raycast", "GetGroundHit", "Rigidbody.AddForce",
                          "linearVelocity =", "new GameObject(", "new Mesh("):
            self.assertNotIn(forbidden, sample)
        for accel in (-1000, -11, -1, 0, 1, 11, 1000):
            pitch = max(-1.8, min(1.8, -accel * .18))
            roll = max(-1.8, min(1.8, accel * .18))
            self.assertLessEqual(abs(pitch), 1.8)
            self.assertLessEqual(abs(roll), 1.8)
        self.assertIn("SampleBodyDynamics();", MOTOR.split(
            "private void FixedUpdate()", 1)[1].split(
            "private void ApplyAckermannSteering(", 1)[0])

    def test_sway_smooths_at_actual_mobile_frame_rate(self):
        visual = MOTOR.split("private void UpdateVisualBodyDynamics()", 1)[1].split(
            "public void ConfigureBodyWear(", 1)[0]
        for token in (
            "Mathf.Clamp(Time.deltaTime, 0f, .12f)",
            "1f - Mathf.Exp(-8f * dt)",
            "Quaternion.Euler(", "Quaternion.Slerp(",
            "visualBodyPivot.localRotation",
            "visualBodyPivot.localPosition",
            "Mathf.Lerp(pos.y, visualJounceTarget, blend)"):
            self.assertIn(token, visual)
        self.assertNotIn("Time.fixedDeltaTime", visual)
        for fps in (10, 15, 30, 60, 120):
            dt = min(.12, 1.0 / fps)
            alpha = 1 - math.exp(-8 * dt)
            self.assertGreater(alpha, 0)
            self.assertLess(alpha, 1)
        late = MOTOR.split("private void LateUpdate()", 1)[1].split(
            "private void OnEnable()", 1)[0]
        self.assertLess(late.index("SyncVisibleWheels();"),
                        late.index("UpdateVisualBodyDynamics();"))
        self.assertLess(late.index("UpdateVisualBodyDynamics();"),
                        late.index("UpdateExhaust();"))

    def test_lights_and_exhaust_follow_only_body_not_wheels(self):
        cfg = MOTOR.split("public void ConfigureExteriorLights(", 1)[1].split(
            "private void EnsureRoadHeadlight()", 1)[0]
        self.assertIn(
            "visualBodyPivot != null ? visualBodyPivot : transform", cfg)
        # Cosmetic LOD adds a single child under the swaying body pivot.
        # All lights must still follow the body, never physics wheel roots.
        self.assertIn("lampDetailRoot.transform.SetParent(lampChassis, false);", cfg)
        self.assertIn("Transform lampMount = lampDetailRoot.transform;", cfg)
        for term in (
            'CreateLampLens(lampMount, "Headlight Bezel "',
            'CreateLampLens(lampMount, "Taillight Bezel "',
            "frontLamps[side] = CreateLampLens(lampMount,",
            "rearLamps[side] = CreateLampLens(lampMount,",
            "reversingLamps[side] = CreateLampLens(lampMount,",
        ):
            self.assertIn(term, cfg)
        lamp = MOTOR.split("private void EnsureRoadHeadlight()", 1)[1].split(
            "private void RefreshExteriorLights()", 1)[0]
        self.assertIn("Transform mount = frontLamps[0].transform.parent;", lamp)
        self.assertIn("beamObject.transform.SetParent(mount, false);", lamp)
        self.assertIn("frontLamps[0].transform.localPosition", lamp)
        exhaust = MOTOR.split("private void EnsureExhaustEmitter()", 1)[1].split(
            "private void StopExhaust(", 1)[0]
        self.assertIn(
            "visualBodyPivot != null ? visualBodyPivot : transform, false", exhaust)
        self.assertIn("main.maxParticles = 24;", exhaust)

    def test_exit_and_disable_zero_cosmetic_targets(self):
        take = MOTOR.split("public bool TryTakeDriver(", 1)[1].split(
            "public void SetInput(", 1)[0]
        self.assertIn("previousPhysicsVelocity = body.linearVelocity;", take)
        self.assertIn("hasPreviousPhysicsVelocity = true;", take)
        release = MOTOR.split("public void ReleaseDriver(", 1)[1].split(
            "private void ApplyWheelForces(", 1)[0]
        self.assertIn(
            "visualPitchTarget = visualRollTarget = visualJounceTarget = 0f;", release)
        disable = MOTOR.split("private void OnDisable()", 1)[1]
        self.assertIn(
            "visualPitchTarget = visualRollTarget = visualJounceTarget = 0f;", disable)
        self.assertIn("visualBodyPivot.localRotation = Quaternion.identity;", disable)
        self.assertIn("visualBodyPivot.localPosition = Vector3.zero;", disable)


    def test_parked_visual_settles_without_repeated_wheel_pose_queries(self):
        late = MOTOR.split("private void LateUpdate()", 1)[1].split("private void OnEnable()", 1)[0]
        for token in ("bool bodyNeedsToSettle = visualBodyPivot != null",
                      "Quaternion.Angle(visualBodyPivot.localRotation,",
                      "parkedSleeping && sleepingWheelPoseCurrent && !bodyNeedsToSettle",
                      "if (!parkedSleeping || !sleepingWheelPoseCurrent)"):
            self.assertIn(token, late)
        self.assertLess(late.index("if (!parkedSleeping || !sleepingWheelPoseCurrent)"),
                        late.index("SyncVisibleWheels();"))
        self.assertLess(late.index("SyncVisibleWheels();"),
                        late.index("UpdateVisualBodyDynamics();"))


if __name__ == "__main__":
    unittest.main()
