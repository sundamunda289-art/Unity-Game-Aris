#!/usr/bin/env python3
"""Source-level integration guards for PR #734 streaming + PR #736 knee/foot IK.

Source/simulation tests ONLY. They do not reproduce real Unity Mecanim,
Android thermal behavior, collision physics or camera screenshots.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "Assets/AarisWorld/Scripts"
VISUAL = (SCRIPTS / "AarisCharacterVisual.cs").read_text()
GATE = (SCRIPTS / "AarisMobileStreamingFrameGate.cs").read_text()
RUNTIME = (SCRIPTS / "AarisMobileRuntime.cs").read_text()
LOOT = (SCRIPTS / "AarisWeaponLootWorld.cs").read_text()
CAPTURE = (ROOT / "Assets/AarisWorld/Editor/AarisPoseFrames.cs").read_text()


class PoseAndStreamingIntegration(unittest.TestCase):
    def test_essential_rig_admission_and_anatomical_knee_correction_coexist(self):
        startup = VISUAL.split("private IEnumerator LoadCharacterAsynchronously()", 1)[1].split(
            "private IEnumerator PrewarmAndroidAnimationGroups(", 1)[0]
        self.assertIn("yield return PrewarmAndroidAnimationGroups(candidate);", startup)
        self.assertIn("ReserveNextFrameForEssentialRig()", startup)
        self.assertIn("if (TryInstantiateCharacter(candidate, source))", startup)
        self.assertLess(startup.index("ReserveNextFrameForEssentialRig()"),
                        startup.index("if (TryInstantiateCharacter(candidate, source))"))
        solver = VISUAL.split("private Vector3 AnatomicalKneeForward()", 1)[1].split(
            "private void CorrectReversedSwingKnee(", 1)[0]
        self.assertIn("rightThigh.position - leftThigh.position", solver)
        self.assertIn("Vector3.Cross(hipRight.normalized, Vector3.up)", solver)
        self.assertIn("return transform.forward;", solver)
        self.assertIn("Vector3 uprightKneeForward = AnatomicalKneeForward();", VISUAL)
        self.assertEqual(VISUAL.count("uprightContactWeight, uprightKneeForward,"), 2)

    def test_transitional_foot_splay_remains_symmetric_and_movement_safe(self):
        method = VISUAL.split("private void PlanBalancedLowStanceFeet(", 1)[1].split(
            "private struct AnkleSupportSample", 1)[0]
        self.assertIn("Vector3.ProjectOnPlane(\n                rightGoal - leftGoal, Vector3.up)", method)
        self.assertIn("float guard = stationary * Mathf.SmoothStep(0f, 1f,", method)
        self.assertIn("if (stanceSpread > 0.50f && guard > 0.001f)", method)
        self.assertIn("(stanceSpread - 0.50f) * 0.5f, 0.16f", method)
        self.assertIn("leftGoal += inward;", method)
        self.assertIn("rightGoal -= inward;", method)
        self.assertLess(method.index("leftGoal += inward;"),
                        method.index("float dominantSettled ="))
        self.assertIn("float correction = settledPose * stationary * dominantSettled;", method)
        self.assertNotIn("Physics.Raycast", method)
        self.assertNotIn("Resources.Load", method)
        self.assertNotIn("new GameObject", method)

    def test_knee_bend_circle_keeps_existing_floor_and_leg_length_guards(self):
        solver = VISUAL.split("private static void PlantLegToTarget(", 1)[1].split(
            "private Vector3 AnatomicalKneeForward()", 1)[0]
        self.assertIn("float maximumKneeSideMetres = 0f", solver)
        self.assertIn("float maximumSideTurnDegrees = 0f", solver)
        self.assertIn("Vector3 candidateBend = Vector3.RotateTowards(", solver)
        self.assertIn("if (candidateKnee.y >= minimumKneeWorldY - 0.002f)", solver)
        self.assertIn("float distance = Mathf.Clamp(actualDistance,", solver)
        self.assertIn("float across = Mathf.Sqrt(Mathf.Max(", solver)
        self.assertIn("foot.rotation = footRotation;", solver)
        # Only in entering prone: never retroactively change standing clip pose.
        low = VISUAL.split("float entryKneeSideTurn = 0f;", 1)[1].split(
            "// Resolve X/Z BEFORE sampling Y.", 1)[0]
        self.assertIn("if (prone && groundedNow && !visuallyAirborne &&", low)
        self.assertIn("transitioningLowStance && authoredProne &&", low)
        self.assertIn("entryKneeSideTurn = 55f * entry * settle *", low)
        self.assertEqual(VISUAL.count("kneeSideAxis, 0.26f, entryKneeSideTurn);"), 2)

    def test_rig_stage_timing_and_original_bakemesh_validation_remain(self):
        for native, field in (
            ("AndroidRigInstantiateMs", "characterRigInstantiateMs"),
            ("AndroidRigGraphSetupMs", "characterRigGraphSetupMs"),
            ("AndroidRigBakeMeshMs", "characterRigBakeMeshMs"),
        ):
            self.assertIn(native + " { get; private set; }", VISUAL)
            self.assertIn("s." + field + " = AarisCharacterVisual." + native + ";", RUNTIME)
        self.assertIn("bodySkin.BakeMesh(posed);", VISUAL)
        self.assertIn("if (posed.vertexCount == 0) return false;", VISUAL)
        self.assertIn("Destroy(posed);", VISUAL)
        self.assertIn("essentialRigFrame = Time.frameCount + 1;", GATE)
        self.assertIn("if (IsEssentialRigFrame())", GATE)
        self.assertIn("RigReservedDeferredAttempts++;", GATE)
        self.assertIn("s.optionalStreamRigFrameDeferrals =", RUNTIME)

    def test_android_only_loot_method_no_longer_leaks_into_editor_compile(self):
        marker = "        private IEnumerator InitializeAndroid()"
        prefix = LOOT[:LOOT.index(marker)].rstrip().splitlines()[-1]
        self.assertEqual(prefix, "#if UNITY_ANDROID && !UNITY_EDITOR")
        body = LOOT.split(marker, 1)[1].split(
            "        private void Initialize()", 1)[0]
        self.assertIn("yield return AarisLootDocument.LoadForAndroidAsync();", body)
        self.assertTrue(body.rstrip().endswith("#endif"))
        self.assertIn("if (AarisWeaponCatalog.TryLoad()) world.Initialize();", LOOT)

    def test_qa_capture_includes_independent_transition_angles(self):
        for method in ("CaptureTransitionPortrait()", "CaptureTransitionFrontPortrait()",
                       "CaptureTransitionBackPortrait()", "CaptureMultiAnglePortrait()"):
            self.assertIn(method, CAPTURE)
        self.assertIn('SessionState.GetFloat("pose.transitionYawDegrees",90f)', CAPTURE)
        self.assertIn('SessionState.SetFloat("pose.transitionYawDegrees",0f);', CAPTURE)
        self.assertIn('SessionState.SetFloat("pose.transitionYawDegrees",180f);', CAPTURE)
        self.assertNotIn("GraphicsSettings.defaultRenderPipeline", CAPTURE)


if __name__ == "__main__":
    unittest.main(verbosity=2)
