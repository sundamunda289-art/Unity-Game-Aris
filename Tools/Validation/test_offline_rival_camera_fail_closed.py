#!/usr/bin/env python3
"""Regression: unavailable gameplay camera never admits expensive remote rivals.

Source contracts + a deterministic admission model; not a native Unity FPS test.
"""
from pathlib import Path
import unittest

SOURCE = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text("utf-8")
DIRECTOR, RIVAL = SOURCE.split("internal sealed class AarisOfflineRival", 1)


def permitted_full_rig(distance, on_camera):
    return distance < 55 or (distance < 105 and on_camera)


def permitted_distant_mesh(distance, on_camera, full_rig=False):
    return on_camera and distance < 485 and not full_rig


def virtual_physics(distance, on_camera, has_visible_body):
    return distance >= 155 and not (on_camera and has_visible_body and distance < 485)


class RemoteCameraFailClosed(unittest.TestCase):
    def test_camera_missing_or_disabled_is_not_visible(self):
        resolve = DIRECTOR.split("private Camera RivalCameraForFrame()", 1)[1].split(
            "internal bool IsDistantRivalOnScreen(", 1)[0]
        probe = DIRECTOR.split("internal bool IsDistantRivalOnScreen(", 1)[1].split(
            "private bool RivalIntersectsFrustum(", 1)[0]
        self.assertIn("cachedRivalCamera.isActiveAndEnabled ? cachedRivalCamera : null", resolve)
        self.assertIn("if (ReferenceEquals(camera, null)) return false;", probe)
        self.assertIn("rivalCameraFrame == frame", resolve)
        self.assertIn("rivalFrustumFrame != rivalCameraFrame", probe)
        self.assertIn("GeometryUtility.CalculateFrustumPlanes(camera, rivalFrustum);", probe)

    def test_camera_swap_with_old_camera_still_enabled(self):
        method = DIRECTOR.split("private Camera RivalCameraForFrame()", 1)[1].split(
            "internal bool IsDistantRivalOnScreen(", 1)[0]
        self.assertIn("cachedRivalCamera = playerMotor.AimCamera;", method)
        self.assertIn("if (liveMain != null && liveMain.isActiveAndEnabled)", method)
        self.assertIn("cachedRivalCamera = liveMain;", method)
        self.assertEqual(method.count("Camera.main"), 1)
        self.assertLess(method.index("if (Time.unscaledTime >= nextRivalCameraLookup)"),
                        method.index("Camera liveMain = Camera.main;"))
        self.assertIn("Time.unscaledTime + 0.5f", method)

    def test_far_clipped_rivals_do_not_materialize_hidden_render_or_physics(self):
        method = DIRECTOR.split("private bool RivalIntersectsFrustum(", 1)[1].split(
            "private void WakeRivalsOnCameraChange(", 1)[0]
        self.assertIn("i == 5 ? 2f : 1.2f", method)
        self.assertIn("Vector3.Dot(plane.normal, center) + plane.distance < -margin", method)
        # Camera projection's x/y can look valid outside the far clipping
        # plane, but rendering and native receiver admission must not be.
        def admitted(depth, far_clip):
            return depth > 0 and depth <= far_clip + 2
        self.assertFalse(admitted(450, 250))
        self.assertFalse(admitted(700, 485))
        self.assertTrue(admitted(249, 250))
        self.assertTrue(admitted(251, 250))  # 2m silhouette margin

    def test_startup_remote_bodies_remain_math_only(self):
        init = RIVAL.split("internal void Initialize(", 1)[1].split(
            "private CharacterController EnsurePhysicalReceiver(", 1)[0]
        update = RIVAL.split("private void Update()", 1)[1].split(
            "private void TickVirtualCombat(", 1)[0]
        self.assertIn("if (spawnDistanceSqr >= DetailedDistance * DetailedDistance ||", init)
        self.assertIn("!spawnCameraRelevant))", init)
        self.assertIn("virtualBody = true;", init)
        self.assertIn("if (capsule != null) capsule.enabled = false;", init)
        self.assertIn("owner.IsDistantRivalOnScreen(transform.position)", init)
        self.assertIn("if (playerDistanceSqr < 55f * 55f || cameraRelevant)", update)
        self.assertIn("bool farBand = cameraRelevant &&", update)
        self.assertIn("bool needsPhysicalReceiver = !virtualBody ||", update)

        # 98 distant rivals on a delayed-camera Android startup get no rigs,
        # mesh draws, or physical receivers; close threats still appear.
        distances = [32, 50] + [200 + i * 7 for i in range(98)]
        full_rigs = sum(permitted_full_rig(d, False) for d in distances)
        distant_meshes = sum(permitted_distant_mesh(d, False) for d in distances)
        remote_receivers = sum(not virtual_physics(d, False, False)
                               for d in distances if d >= 155)
        self.assertEqual((full_rigs, distant_meshes, remote_receivers), (2, 0, 0))

    def test_camera_recovers_remote_rendering_without_combat_restart(self):
        for distance in (90, 155, 300, 480):
            self.assertFalse(permitted_full_rig(distance, False) or
                             permitted_distant_mesh(distance, False))
            close_rig = permitted_full_rig(distance, True)
            self.assertTrue(close_rig or
                            permitted_distant_mesh(distance, True, close_rig))
        self.assertFalse(virtual_physics(300, True, True))
        self.assertTrue(virtual_physics(300, False, False))
        self.assertIn("TickVirtualCombat(now, dt, playerPosition);", RIVAL)
        self.assertIn("owner.ReportRivalElimination(attacker, gameObject);", RIVAL)


if __name__ == "__main__":
    unittest.main()
