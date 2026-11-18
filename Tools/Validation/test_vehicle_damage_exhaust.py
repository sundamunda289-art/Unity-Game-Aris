#!/usr/bin/env python3
"""Source-level guards for damage persistence and bounded mobile exhaust VFX.

This complements (does not replace) Unity Play Mode/Android rendering tests.
"""
import math
import unittest
from pathlib import Path

SRC = Path(__file__).resolve().parents[2] / "Assets/AarisWorld/Scripts"
WORLD = (SRC / "AarisVehicleWorld.cs").read_text("utf-8")
MOTOR = (SRC / "AarisVehicleMotor.cs").read_text("utf-8")
HUD = (SRC / "AarisVehicleHUD.cs").read_text("utf-8")
AUDIO = (SRC / "AarisAudioDirector.cs").read_text("utf-8")


class VehicleDamageExhaust(unittest.TestCase):
    def test_damage_cooldown_and_max_condition(self):
        collision = MOTOR.split("private void OnCollisionEnter(", 1)[1].split(
            "public void Configure(", 1)[0]
        self.assertIn("Vector3 relativeVelocity = collision.relativeVelocity;", collision)
        self.assertIn("collision.GetContact(i).normal;", collision)
        self.assertIn("Vector3.Dot(relativeVelocity, normal)", collision)
        self.assertIn("if (impactSpeed < 4f) return;", collision)
        self.assertNotIn("collision.relativeVelocity.magnitude", collision)
        self.assertIn("if (impactSpeed >= 7f)", collision)
        self.assertIn("if (Time.time >= nextDamageAt)", collision)
        self.assertIn("nextDamageAt = Time.time + .75f;", collision)
        self.assertIn("Mathf.Max(0f, impactSpeed - 7f) * .011f", collision)
        self.assertIn("0f, .85f", collision)
        self.assertNotIn("Destroy(gameObject)", collision)
        self.assertNotIn("body.linearVelocity =", collision)
        wear = 0.0
        for impact in [7, 8, 14, 20, 30, 45, 100]:
            wear = max(0.0, min(.85, wear + max(0.0, impact - 7) * .011))
        self.assertTrue(0.0 < wear <= .85)
        self.assertIn("drive *= Mathf.Lerp(1f, .72f, damage01);", MOTOR)
        self.assertIn("public float Damage01 => damage01;", MOTOR)

    def test_streaming_preserves_damage_for_both_unload_paths(self):
        self.assertIn("public float damage01;", WORLD)
        self.assertEqual(WORLD.count(".damage01 = "), 2)
        self.assertIn("record.damage01 = record.visible.Damage01;", WORLD)
        self.assertIn("parkedVictim.damage01 = retired.Damage01;", WORLD)
        build = WORLD.split("private bool Build(", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertIn("motor.RestoreStreamedDamage(record.damage01);", build)
        self.assertLess(build.index("motor.ConfigureExhaust("),
                        build.index("motor.RestoreStreamedDamage("))
        restore = MOTOR.split("public void RestoreStreamedDamage(", 1)[1].split(
            "private void EnsureExhaustEmitter()", 1)[0]
        self.assertIn("float.IsNaN(savedDamage)", restore)
        self.assertIn("float.IsInfinity(savedDamage)", restore)
        self.assertIn("Mathf.Clamp(savedDamage, 0f, .85f)", restore)

    def test_particle_shader_optional_and_shared(self):
        self.assertIn('Shader.Find("Universal Render Pipeline/Particles/Unlit")', WORLD)
        self.assertIn("if (smokeShader != null)", WORLD)
        self.assertIn("exhaustMaterial = CreateExhaustMaterial(smokeShader);", WORLD)
        self.assertIn("if (exhaustMaterial != null) Destroy(exhaustMaterial);", WORLD)
        constructor = MOTOR.split("private void EnsureExhaustEmitter()", 1)[1].split(
            "private void StopExhaust(", 1)[0]
        self.assertIn("if (sharedExhaustMaterial == null || exhaustParticles != null)", constructor)
        self.assertIn('nozzle.AddComponent<ParticleSystem>()', constructor)
        self.assertIn("main.maxParticles = 24;", constructor)
        self.assertIn("main.playOnAwake = false;", constructor)
        self.assertIn("main.simulationSpace = ParticleSystemSimulationSpace.World;", constructor)
        self.assertIn("renderer.sharedMaterial = sharedExhaustMaterial;", constructor)
        self.assertIn("ParticleSystemStopBehavior.StopEmittingAndClear", constructor)
        self.assertIn("ParticleSystemRenderMode.Billboard", constructor)
        self.assertNotIn("AddComponent<Collider>", constructor)
        boarding = MOTOR.split("public bool TryTakeDriver(", 1)[1].split(
            "public void SetInput(", 1)[0]
        self.assertIn("EnsureExhaustEmitter();", boarding)
        self.assertNotIn("EnsureExhaustEmitter();", MOTOR.split(
            "private void FixedUpdate()", 1)[1])

    def test_smoke_budget_pause_exit_and_foot_handoff(self):
        update = MOTOR.split("private void UpdateExhaust()", 1)[1].split(
            "private void SyncVisibleWheels()", 1)[0]
        self.assertIn("Time.unscaledTime + .16f", update)
        self.assertIn("driver == null || !driver.isActiveAndEnabled", update)
        self.assertIn("1.4f + load * 3.5f + damage01 * 10f", update)
        self.assertIn("emission.rateOverTime = emissionRate;", update)
        self.assertIn("main.startColor = Color.Lerp(", update)
        for forbidden in ("new Material(", "new GameObject(", "Instantiate(",
                          "GetGroundHit(", "Physics.Raycast("):
            self.assertNotIn(forbidden, update)
        release = MOTOR.split("public void ReleaseDriver(", 1)[1].split(
            "private void ApplyWheelForces(", 1)[0]
        self.assertIn("StopExhaust(false);", release)
        ondisable = MOTOR.split("private void OnDisable()", 1)[1]
        self.assertIn("StopExhaust(true);", ondisable)
        self.assertIn("StopExhaust(true);", MOTOR.split(
            "private void OnEnable()", 1)[1].split(
            "public bool TryTakeDriver(", 1)[0])
        self.assertIn("UpdateExhaust();", MOTOR.split(
            "private void LateUpdate()", 1)[1].split(
            "private void OnEnable()", 1)[0])

    def test_damage_hud_does_not_take_pointer_events(self):
        self.assertIn("private Text conditionCaption;", HUD)
        self.assertIn('"Vehicle Damage Condition"', HUD)
        self.assertIn("conditionCaption.raycastTarget = false;", HUD)
        self.assertIn("conditionCaption.enabled = false;", HUD)
        self.assertIn("bool damaged = vehicle.Damage01 > .025f;", HUD)
        self.assertIn("Mathf.RoundToInt((1f - vehicle.Damage01) * 100f)", HUD)
        self.assertIn("Time.unscaledTime + .125f", HUD)
        self.assertIn("vehicle.IsServiceBraking", HUD)
        self.assertIn("vehicle.IsRecentImpact", HUD)

    def test_existing_engine_audio_responds_to_damage_without_second_loop(self):
        drive = AUDIO.split("private void TickVehicle(", 1)[1].split(
            "private float FootfallSpacing(", 1)[0]
        self.assertIn("float wear = car.Damage01;", drive)
        self.assertIn("engineLoad * 0.11f - wear * .12f", drive)
        self.assertEqual(drive.count("AdjustLoop(engineLoop, engineVolume, targetPitch, dt);"), 1)
        self.assertNotIn("MakeSource(", drive)
        self.assertNotIn("new AudioSource(", drive)


if __name__ == "__main__":
    unittest.main()
