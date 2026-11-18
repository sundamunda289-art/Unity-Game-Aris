#!/usr/bin/env python3
"""Static regressions for cheap, grounded skid marks and impact cues.

These checks do not replace Unity WheelCollider, URP or Android Play Mode.
"""
import unittest
from pathlib import Path

SOURCE = Path(__file__).resolve().parents[2] / "Assets/AarisWorld/Scripts"
MOTOR = (SOURCE / "AarisVehicleMotor.cs").read_text("utf-8")
WORLD = (SOURCE / "AarisVehicleWorld.cs").read_text("utf-8")
HUD = (SOURCE / "AarisVehicleHUD.cs").read_text("utf-8")


class VehicleSkidAndImpact(unittest.TestCase):
    def test_shared_urp_trail_material_lifetime(self):
        self.assertIn("skidMarkMaterial = CreateSkidMarkMaterial(shader);", WORLD)
        self.assertIn("motor.ConfigureRearTyreTraces(skidMarkMaterial);", WORLD)
        self.assertIn("if (skidMarkMaterial != null) Destroy(skidMarkMaterial);", WORLD)
        src = WORLD.split("private static Material CreateSkidMarkMaterial(", 1)[1]
        self.assertIn('material.SetOverrideTag("RenderType", "Transparent");', src)
        self.assertIn('material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");', src)
        self.assertIn("material.SetFloat(\"_ZWrite\", 0f);", src)

    def test_trails_allocate_only_when_someone_drives(self):
        configure = MOTOR.split("public void ConfigureRearTyreTraces(", 1)[1].split(
            "private void EnsureRearTyreTraces()", 1)[0]
        self.assertNotIn("new GameObject(", configure)
        self.assertIn("sharedSkidMaterial = material;", configure)
        create = MOTOR.split("private void EnsureRearTyreTraces()", 1)[1].split(
            "private void StopRearTyreTraces(", 1)[0]
        self.assertIn("sharedSkidMaterial == null || rearTyreTrails[0] != null", create)
        self.assertIn('mark.AddComponent<TrailRenderer>()', create)
        self.assertIn("trail.sharedMaterial = sharedSkidMaterial;", create)
        self.assertIn("trail.time = 2.2f;", create)
        self.assertIn("trail.minVertexDistance = .20f;", create)
        self.assertIn("trail.alignment = LineAlignment.TransformZ;", create)
        self.assertIn("trail.emitting = false;", create)
        self.assertIn("ShadowCastingMode.Off;", create)
        self.assertNotIn("AddComponent<Collider>", create)
        self.assertIn("EnsureRearTyreTraces();", MOTOR.split("public bool TryTakeDriver(", 1)[1].split(
            "public void SetInput(", 1)[0])
        self.assertNotIn("EnsureRearTyreTraces();", MOTOR.split("private void FixedUpdate()", 1)[1])

    def test_reuses_real_contact_and_clears_airborne_state(self):
        physics = MOTOR.split("private void FixedUpdate()", 1)[1].split(
            "private void ApplyAckermannSteering(", 1)[0]
        antiroll = MOTOR.split("private void ApplyAntiRoll(", 1)[1].split(
            "private void OnDisable()", 1)[0]
        self.assertIn("rearContactsGrounded[0] = rearContactsGrounded[1] = false;", physics)
        self.assertIn("left.GetGroundHit(out WheelHit leftHit);", antiroll)
        self.assertIn("right.GetGroundHit(out WheelHit rightHit);", antiroll)
        self.assertIn("CacheRearTyreContact(leftIndex, leftHit, leftSlip);", antiroll)
        self.assertIn("CacheRearTyreContact(rightIndex, rightHit, rightSlip);", antiroll)
        caching = MOTOR.split("private void CacheRearTyreContact(", 1)[1].split(
            "private void SyncVisibleWheels()", 1)[0]
        for token in ("wheelIndex < 2", "driver == null", "rearTyreTrails[0] == null",
                      "hit.collider.gameObject.layer == 4",
                      "Vector3.Dot(hit.normal, Vector3.up) < .45f",
                      "rearContactPoints[i] = hit.point",
                      "rearContactNormals[i] = hit.normal"):
            self.assertIn(token, caching)
        self.assertNotIn("Physics.Raycast", MOTOR)
        self.assertNotIn("WheelCollider.GetGroundHit(", caching)

    def test_trails_only_when_wheel_slips_and_vehicle_moves(self):
        update = MOTOR.split("private void UpdateRearTyreTraces()", 1)[1].split(
            "private void CacheRearTyreContact(", 1)[0]
        for token in ("driver != null && driver.isActiveAndEnabled",
                      "body.linearVelocity.sqrMagnitude > 16f",
                      "rearContactsGrounded[i]", "rearContactSlip[i] > .35f",
                      "rearContactPoints[i] + normal * .014f",
                      "trail.emitting = false;", "trail.Clear();"):
            self.assertIn(token, update)
        self.assertNotIn("new Material(", update)
        self.assertNotIn("GetGroundHit(", update)
        self.assertIn("UpdateRearTyreTraces();", MOTOR.split(
            "private void LateUpdate()", 1)[1].split("private void OnEnable()", 1)[0])
        self.assertIn("StopRearTyreTraces(false);", MOTOR.split(
            "public void ReleaseDriver(", 1)[1].split("private void ApplyWheelForces(", 1)[0])
        self.assertIn("StopRearTyreTraces(true);", MOTOR.split("private void OnDisable()", 1)[1])

    def test_collision_lamp_and_hud_pulse_expire(self):
        collision = MOTOR.split("private void OnCollisionEnter(", 1)[1].split(
            "public void Configure(", 1)[0]
        self.assertIn("Vector3 relativeVelocity = collision.relativeVelocity;", collision)
        self.assertIn("collision.GetContact(i).normal;", collision)
        self.assertIn("Vector3.Dot(relativeVelocity, normal)", collision)
        self.assertIn("if (impactSpeed < 4f) return;", collision)
        self.assertNotIn("collision.contacts", collision)
        self.assertIn("AarisAudioDirector.VehicleCrash(transform.position, impactSpeed);", collision)
        self.assertIn("if (impactSpeed >= 7f)", collision)
        self.assertIn("impactFlashUntil = Time.time + .55f;", collision)
        self.assertIn("RefreshExteriorLights();", collision)
        light = MOTOR.split("private void RefreshExteriorLights()", 1)[1].split(
            "private void EnsureRearTyreTraces()", 1)[0]
        self.assertIn("appliedBrakeTorque >= 500f || Time.time < impactFlashUntil", light)
        self.assertIn("public bool IsRecentImpact => driver != null && Time.time < impactFlashUntil;", MOTOR)
        self.assertIn("vehicle.IsRecentImpact", HUD)
        self.assertIn("impactFlashUntil = 0f;", MOTOR.split("public void ReleaseDriver(", 1)[1])
        self.assertIn("impactFlashUntil = 0f;", MOTOR.split("private void OnDisable()", 1)[1])


if __name__ == "__main__":
    unittest.main()
