#!/usr/bin/env python3
"""Regression for 100 time-sliced distant AI agents with bounded travel drift."""
from pathlib import Path
import math
import unittest

SOURCE = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()


def simulate(fps, seconds, distance, speed=2.85):
    now = last = next_far = pos = 0.
    frames = 0
    while now + 1e-9 < seconds:
        now += 1. / fps
        far = distance >= 90.
        if far and now < next_far:
            continue
        dt = min(max(0., now-last), .46) if far else min(1. / fps, .12)
        last = now
        if far:
            next_far = now + .35
        pos += dt * speed
        frames += 1
    return pos, frames


def simulate_hundred_phase_ticks(fps, seconds):
    """Model 100 far rivals sharing 350ms phases, rounded to phone frames."""
    phases = [index * (.35 / 100.) for index in range(100)]
    due = [0.] * 100
    updates_per_frame = []
    total_per_bot = [0] * 100
    for frame in range(1, round(fps * seconds) + 1):
        now = frame / fps
        updates = 0
        for index in range(100):
            if now + 1e-9 < due[index]:
                continue
            updates += 1
            total_per_bot[index] += 1
            next_tick = (math.floor((now - phases[index]) / .35) + 1) * .35 + phases[index]
            if next_tick <= now + .0001:
                next_tick += .35
            due[index] = next_tick
        updates_per_frame.append(updates)
    return updates_per_frame, total_per_bot


class FarRivalBudget(unittest.TestCase):
    def test_source_preserves_nearby_physics_and_sight(self):
        bot = SOURCE.split("internal sealed class AarisOfflineRival", 1)[1]
        update = bot.split("private void Update()", 1)[1].split("private bool HasSight()", 1)[0]
        self.assertIn("private const float FarSimulationDistance = 90f;", bot)
        self.assertIn("private const float FarSimulationInterval = 0.35f;", bot)
        guard = update.index("if (farSimulation && now < nextFarSimulation && !justEnteredCamera &&")
        self.assertLess(guard, update.index("owner.TryZoneRetreatDirection"))
        self.assertLess(guard, update.index("capsule.Move("))
        self.assertIn("now - lastSimulationTime", update)
        self.assertIn("Mathf.Min(Time.deltaTime, 0.12f)", update)
        self.assertIn("(target.position - transform.position).sqrMagnitude < 42f * 42f && HasSight()", update)
        self.assertIn("IsPostMoveShotInRangeAndFacing(afterMove, transform.forward,", update)
        self.assertIn("owner.WouldCrossInactiveCover", update)

    def test_render_and_simulation_lods_are_independent(self):
        bot = SOURCE.split("internal sealed class AarisOfflineRival", 1)[1]
        update = bot.split("private void Update()", 1)[1].split(
            "private void UpdateAnimationBlend", 1)[0]
        self.assertIn("skin.updateWhenOffscreen = false;", bot)
        self.assertIn("animator.cullingMode = AnimatorCullingMode.CullCompletely;", bot)
        self.assertIn("playerDistanceSqr < 55f * 55f || cameraRelevant", update)
        self.assertIn("if (visible) SetDetailedRenderVisibility(cameraRelevant);", update)
        self.assertIn("if (!visible && hasAnimations && graph.IsValid()) graph.Stop();", update)
        # A 4x-visible, 300m bot needs a stationary physical hit receiver,
        # but MUST remain a math-only combat agent (no capsule.Move).
        self.assertIn("bool shouldVirtualize = playerDistanceSqr >= DetailedDistance * DetailedDistance ||", update)
        self.assertNotIn(
            "playerDistanceSqr >= DetailedDistance * DetailedDistance && !cameraRelevant", update)
        self.assertIn("private const float PhysicalReturnDistance = 145f;", bot)
        self.assertIn("PhysicalReturnDistance * PhysicalReturnDistance &&", update)
        self.assertIn("else if (virtualBody && shouldRestorePhysics)", update)
        self.assertIn("bool needsPhysicalReceiver = !virtualBody ||", update)
        self.assertIn("cameraRelevant && HasVisibleBody &&", update)
        self.assertIn("capsule.enabled = needsPhysicalReceiver;", update)
        self.assertLess(update.index("TickVirtualCombat(now, dt, playerPosition);"),
                        update.index("capsule.Move("))
        self.assertIn("hasAnimations && visualRoot != null && visualRoot.activeSelf", update)
        self.assertIn("targetHealth.ApplyDamage(damage, gameObject);", bot)
        self.assertIn("owner.ReportRivalElimination(attacker, gameObject);", bot)
        self.assertIn("if (virtualBody || Time.time < nextCoverQuery) return;", bot)

        # Control-plane model for the two independent LODs. The 485m
        # visual band is deliberately not claimed to cost zero GPU.
        def lod(distance, on_camera):
            virtual = distance >= 155 or (distance >= 90 and not on_camera)
            full = distance < 155 and (distance < 55 or on_camera)
            far_mesh = distance < 485 and on_camera and not full
            collider = not virtual or (on_camera and distance < 485)
            return virtual, full, far_mesh, collider

        self.assertEqual(lod(1000, False), (True, False, False, False))
        self.assertEqual(lod(300, False), (True, False, False, False))
        self.assertEqual(lod(300, True), (True, False, True, True))
        self.assertEqual(lod(155, True), (True, False, True, True))
        self.assertEqual(lod(154.9, True), (False, True, False, True))
        self.assertEqual(lod(100, False), (True, False, False, False))
        self.assertEqual(lod(100, True), (False, True, False, True))
        self.assertEqual(lod(30, False), (False, True, False, True))

    def test_far_movement_stays_near_realtime(self):
        for fps in (15, 30, 60, 90, 120):
            with self.subTest(fps=fps):
                far_pos, far_frames = simulate(fps, 12., 120.)
                near_pos, near_frames = simulate(fps, 12., 10.)
                self.assertAlmostEqual(far_pos, near_pos, delta=1.1)
                self.assertLess(far_frames, near_frames)
                self.assertGreater(far_frames, 20)

    def test_android_far_tick_phases_spread_cpu_and_physics(self):
        bot = SOURCE.split("internal sealed class AarisOfflineRival", 1)[1]
        self.assertIn("private const int RivalCount = 100;", SOURCE)
        self.assertIn("rival.Initialize(this, health, player, playerHealth, index);", SOURCE)
        self.assertIn("farSimulationPhase = encounterSlot * (FarSimulationInterval / 100f);", bot)
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", bot)
        self.assertIn("Mathf.Floor((now - farSimulationPhase) /", bot)
        self.assertIn("if (nextTick <= now + 0.0001f)", bot)
        self.assertIn("nextFarSimulation = nextTick;", bot)
        self.assertIn("nextFarSimulation = Time.time; // First distant update remains immediate.", bot)
        self.assertIn("nextFarSimulation = now + FarSimulationInterval;", bot)
        for fps, max_per_frame in ((15, 20), (30, 10), (60, 5), (90, 4)):
            with self.subTest(fps=fps):
                collisions, totals = simulate_hundred_phase_ticks(fps, 10)
                # The first simulated frame starts all 100, whereas gameplay spawns
                # only one actor per 170ms. Ignore its synthetic start burst.
                self.assertLessEqual(max(collisions[1:]), max_per_frame)
                for ticks in totals:
                    self.assertGreaterEqual(ticks, 28)
                    self.assertLessEqual(ticks, 31)

    def test_camera_entry_does_not_force_distant_math_combat(self):
        bot = SOURCE.split("internal sealed class AarisOfflineRival", 1)[1]
        update = bot.split("private void Update()", 1)[1].split(
            "private void TickVirtualCombat(", 1)[0]
        due = update.index("bool simulationDue = !farSimulation || now >= nextFarSimulation;")
        self.assertLess(due, update.index("if (farSimulation && now < nextFarSimulation && !justEnteredCamera &&"))
        self.assertIn("if (simulationDue)\n            {", update)
        self.assertIn("if (!simulationDue) return;", update)
        self.assertLess(update.index("if (!simulationDue) return;"),
                        update.index("TickVirtualCombat(now, dt, playerPosition);"))

        # A 100-rival phone-camera sweep may wake representation immediately,
        # but it must not increase movement, damage or the 350ms AI tick count.
        def simulate_camera_sweep(trigger_entrances):
            next_ticks = [0.] * 100
            last_on_camera = [False] * 100
            counts = [0] * 100
            presentation_wakes = 0
            for frame in range(1, 30 * 8 + 1):
                now = frame / 30.
                for slot in range(100):
                    on_camera = trigger_entrances and ((frame + slot) % 11 == 0)
                    entered = on_camera and not last_on_camera[slot]
                    last_on_camera[slot] = on_camera
                    simulation_due = now >= next_ticks[slot]
                    if not simulation_due and not entered:
                        continue
                    if entered:
                        presentation_wakes += 1
                    if simulation_due:
                        counts[slot] += 1
                        next_ticks[slot] = now + .35
            return counts, presentation_wakes

        baseline, no_wakes = simulate_camera_sweep(False)
        sweeping, wakes = simulate_camera_sweep(True)
        self.assertEqual(sweeping, baseline)
        self.assertEqual(no_wakes, 0)
        self.assertGreater(wakes, 100)

    def test_camera_exit_immediately_culls_remote_native_work(self):
        bot = SOURCE.split("internal sealed class AarisOfflineRival", 1)[1]
        update = bot.split("private void Update()", 1)[1].split(
            "private void TickVirtualCombat(", 1)[0]
        exit_block = update.split("if (cameraRelevant != lastCameraRelevant)", 1)[1].split(
            "lastCameraRelevant = cameraRelevant;", 1)[0]
        self.assertIn("if (!cameraRelevant && playerDistanceSqr >= 55f * 55f)", exit_block)
        self.assertIn("playerDistanceSqr >= 55f * 55f", exit_block)
        self.assertIn("visualRoot.SetActive(false);", exit_block)
        self.assertIn("if (hasAnimations && graph.IsValid()) graph.Stop();", exit_block)
        self.assertIn("farVisualRoot.SetActive(false);", exit_block)
        self.assertIn("if (virtualBody && capsule != null && capsule.enabled)", exit_block)
        self.assertIn("capsule.enabled = false;", exit_block)
        self.assertNotIn("new ", exit_block)
        self.assertLess(update.index("if (!cameraRelevant && playerDistanceSqr >= 55f * 55f)"),
                        update.index("if (farSimulation && now < nextFarSimulation && !justEnteredCamera &&"))
        self.assertIn("nextCameraProbe = now + 0.12f;", update)
        self.assertIn("virtualBody = true;", exit_block)
        self.assertIn("FarSimulationDistance * FarSimulationDistance", exit_block)

    def test_leaving_485m_eviction_precedes_far_tick_gate(self):
        bot = SOURCE.split("internal sealed class AarisOfflineRival", 1)[1]
        update = bot.split("private void Update()", 1)[1].split(
            "private void TickVirtualCombat(", 1)[0]
        outside = update.split(
            "// Exiting the 485m representation band must evict immediately.", 1)[1].split(
            "// Camera visibility controls rendering/physical hit receivers", 1)[0]
        for required in (
            "cameraRelevant = false;",
            "if (lastCameraRelevant) nextVisualCheck = now;",
            "visualRoot.SetActive(false);",
            "graph.Stop();",
            "farVisualRoot.SetActive(false);",
            "virtualBody = true;",
            "capsule.enabled = false;",
            "lastCameraRelevant = false;",
        ):
            self.assertIn(required, outside)
        self.assertLess(
            update.index("// Exiting the 485m representation band"),
            update.index("if (farSimulation && now < nextFarSimulation && !justEnteredCamera &&"))
        # Only renderer/collider admission is changed. The far simulation
        # scheduler and remote HP/damage path still use their original tick.
        self.assertIn("if (!simulationDue) return;", update)
        self.assertIn("TickVirtualCombat(now, dt, playerPosition);", update)

    def test_near_offcamera_renderer_gate_preserves_combat_body(self):
        bot = SOURCE.split("internal sealed class AarisOfflineRival", 1)[1]
        cache = bot.split("private void CacheDetailedRenderers()", 1)[1].split(
            "private void BuildVisual()", 1)[0]
        update = bot.split("private void Update()", 1)[1].split(
            "private void TickVirtualCombat(", 1)[0]
        # Renderer-only suppression: the actor and capsule still exist for
        # physical punches, LOS and back-of-camera 20m attackers.
        self.assertIn("GetComponentsInChildren<Renderer>(true)", cache)
        self.assertIn("renderer.forceRenderingOff = suppress ||", cache)
        self.assertIn("authoredRendererForcedOff[i]", cache)
        self.assertIn("if (suppress) graph.Stop();", cache)
        self.assertIn("else graph.Play();", cache)
        self.assertNotIn("capsule.enabled = false", cache)
        self.assertNotIn("visualRoot.SetActive(false)", cache)
        self.assertIn("if (playerDistanceSqr < 55f * 55f)", update)
        self.assertIn("SetDetailedRenderVisibility(cameraRelevant);", update)
        self.assertLess(
            update.index("SetDetailedRenderVisibility(cameraRelevant);"),
            update.index("bool farSimulation = playerDistanceSqr >="))
        self.assertIn("if (visible) SetDetailedRenderVisibility(cameraRelevant);", update)
        self.assertIn("!detailedRenderingSuppressed", update)
        self.assertIn("bool needsPhysicalReceiver = !virtualBody ||", update)
        self.assertIn("capsule.Move(", update)
        self.assertIn("TickVirtualCombat(now, dt, playerPosition);", update)

    def test_denied_camera_admission_retries_without_extra_combat(self):
        update = SOURCE.split("internal sealed class AarisOfflineRival", 1)[1]
        update = update.split("private void Update()", 1)[1]
        update = update.split("private void TickVirtualCombat", 1)[0]
        retry = update.index("bool presentationRetryDue = cameraRelevant")
        self.assertIn("!HasVisibleBody || capsule == null || !capsule.enabled", update)
        self.assertIn("!presentationRetryDue) return;", update)
        self.assertIn("if (cameraRelevant && !HasVisibleBody)", update)
        self.assertIn("nextVisualCheck = now + 0.12f;", update)
        self.assertIn("nextVisualCheck = Mathf.Min(nextVisualCheck, now + 0.12f);", update)
        self.assertLess(retry, update.index("if (simulationDue)"))
        self.assertLess(update.index("if (!simulationDue) return;"),
                        update.index("TickVirtualCombat(now, dt, playerPosition);"))
        # Model the first camera admission denied by the native budget,
        # followed by a successful retry at the next 120ms probe.
        for fps in (15, 30, 60, 120):
            next_ai = .35
            next_visual = 0.
            visible = False
            attempts = 0
            ticks = []
            baseline = []
            next_baseline = .35
            shown = None
            for frame in range(fps):
                now = frame / fps
                if now >= next_baseline:
                    baseline.append(frame)
                    next_baseline = now + .35
                simulation_due = now >= next_ai
                retry_due = not visible and now >= next_visual
                if not simulation_due and not retry_due:
                    continue
                if retry_due:
                    attempts += 1
                    if attempts == 1:
                        next_visual = now + .12
                    else:
                        visible = True
                        shown = now
                if simulation_due:
                    ticks.append(frame)
                    next_ai = now + .35
            self.assertEqual(ticks, baseline)
            self.assertEqual(attempts, 2)
            self.assertLess(shown, .20)

    def test_near_bot_is_full_frame_rate(self):
        for fps in (15, 30, 60, 120):
            with self.subTest(fps=fps):
                _, ticks = simulate(fps, 3., 35.)
                self.assertEqual(ticks, round(3 * fps))


if __name__ == "__main__":
    unittest.main()
