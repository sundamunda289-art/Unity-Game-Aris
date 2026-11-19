#!/usr/bin/env python3
"""Rig first-frame admission regression; deterministic source checks only.

Only optional 3D/material/Foley imports defer. Essential player creation,
animation graph, mesh validation and source geometry remain unchanged.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "Assets/AarisWorld/Scripts"
GATE = (SRC / "AarisMobileStreamingFrameGate.cs").read_text()
RIG = (SRC / "AarisCharacterVisual.cs").read_text()
DIAG = (SRC / "AarisMobileRuntime.cs").read_text()


def simulate(reservation_frame, attempts):
    """Reservation at frame N prevents new optional work only at N+1."""
    reserved_at = reservation_frame + 1
    results = []
    for f in attempts:
        results.append((f, f != reserved_at))
    return results


class EssentialCharacterRigFrame(unittest.TestCase):
    def test_constrained_android_owns_only_one_next_frame_reservation(self):
        reserve = GATE.split("internal static bool ReserveNextFrameForEssentialRig()", 1)[1].split(
            "private static bool IsEssentialRigFrame()", 1)[0]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", reserve)
        self.assertIn("int ramMb = SystemInfo.systemMemorySize;", reserve)
        self.assertIn("ramMb > 0 && ramMb <= 4608", reserve)
        self.assertIn("essentialRigFrame = Time.frameCount + 1;", reserve)
        self.assertIn("return true;", reserve)
        self.assertIn("return false;", reserve)
        self.assertIn("return Time.frameCount == essentialRigFrame;", GATE)
        self.assertIn("essentialRigFrame = -1;", GATE)
        self.assertIn("RigReservedDeferredAttempts = 0;", GATE)

    def test_both_optional_hierarchy_and_native_import_admission_yield(self):
        models = GATE.split("internal static bool TryClaimOptionalModelBuild()", 1)[1].split(
            "internal static ResourceRequest TryStartOptionalLoadAsync<T>", 1)[0]
        imports = GATE.split("internal static ResourceRequest TryStartOptionalLoadAsync<T>", 1)[1]
        for block in (models, imports):
            self.assertIn("if (IsEssentialRigFrame())", block)
            self.assertIn("RigReservedDeferredAttempts++;", block)
            self.assertIn("return false;" if block is models else "return null;", block)
        self.assertLess(models.index("if (IsEssentialRigFrame())"),
                        models.index("if (IsAssetCleanupPending())"))
        self.assertLess(imports.index("if (IsEssentialRigFrame())"),
                        imports.index("if (constrained)"))
        self.assertIn("Resources.LoadAsync<T>(path)", imports)
        self.assertIn("request.priority = -1;", imports)
        self.assertNotIn("Resources.UnloadUnusedAssets()", GATE)

    def test_character_rig_waits_one_frame_only_on_constrained_android(self):
        routine = RIG.split("private IEnumerator LoadCharacterAsynchronously()", 1)[1].split(
            "private IEnumerator PrewarmAndroidAnimationGroups", 1)[0]
        self.assertIn("yield return PrewarmAndroidAnimationGroups(candidate);", routine)
        self.assertIn("ReserveNextFrameForEssentialRig())", routine)
        self.assertIn("if (!isActiveAndEnabled) yield break;", routine)
        self.assertIn("if (TryInstantiateCharacter(candidate, source))", routine)
        self.assertLess(routine.index("yield return PrewarmAndroidAnimationGroups(candidate);"),
                        routine.index("ReserveNextFrameForEssentialRig())"))
        self.assertLess(routine.index("ReserveNextFrameForEssentialRig())"),
                        routine.index("if (TryInstantiateCharacter(candidate, source))"))
        self.assertIn("yield return null;", routine)
        self.assertIn("FinishVisualSetup();", routine)
        self.assertIn("yield break;", routine)
        self.assertIn("Resources.LoadAsync<GameObject>(candidate)", routine)
        self.assertIn("GameObject source = request.asset as GameObject;", routine)

    def test_original_rig_validation_and_pose_still_run(self):
        inst = RIG.split("private bool TryInstantiateCharacter(", 1)[1].split(
            "private bool TryPrepareAnimatedBody()", 1)[0]
        check = RIG.split("private bool TryPrepareAnimatedBody()", 1)[1].split(
            "private bool ConfigureDrawableSkin(", 1)[0]
        self.assertIn("visual = Instantiate(source, transform, false);", inst)
        self.assertIn("DisablePrimaryImportHelpers(resourcePath);", inst)
        self.assertIn("FitVisualToController(resourcePath)", inst)
        self.assertIn("ConfigureDrawableSkin(resourcePath)", inst)
        self.assertIn("if (TryPrepareAnimatedBody()) return true;", inst)
        self.assertIn("ReleaseAnimationGraph();", inst)
        self.assertIn("SetupAnimations();", check)
        self.assertIn("graph.Evaluate(0f);", check)
        self.assertIn("bodySkin.BakeMesh(posed);", check)
        self.assertIn("Destroy(posed);", check)
        self.assertIn("skinTransform.TransformPoint(posed.bounds.center)", check)
        self.assertNotIn("QualitySettings.skinWeights", RIG)
        self.assertNotIn("Shader.WarmupAllShaders", RIG)

    def test_diagnostics_separate_native_rig_stages_from_gpu_evidence(self):
        for prop, field in (
            ("AndroidRigInstantiateMs", "characterRigInstantiateMs"),
            ("AndroidRigGraphSetupMs", "characterRigGraphSetupMs"),
            ("AndroidRigBakeMeshMs", "characterRigBakeMeshMs"),
        ):
            self.assertIn("internal static float " + prop + " { get; private set; } = -1f;", RIG)
            self.assertIn("public float " + field + " = -1f;", DIAG)
            self.assertIn("s." + field + " = AarisCharacterVisual." + prop + ";", DIAG)
        self.assertIn("public int optionalStreamRigFrameDeferrals;", DIAG)
        self.assertIn(
            "s.optionalStreamRigFrameDeferrals =\n                AarisMobileStreamingFrameGate.RigReservedDeferredAttempts;", DIAG)
        self.assertIn("AndroidRigInstantiateMs = AndroidRigGraphSetupMs = AndroidRigBakeMeshMs = -1f;", RIG)
        self.assertEqual(RIG.count("Time.realtimeSinceStartupAsDouble") >= 10, True)

    def test_one_frame_only_does_not_drop_deferred_optional_jobs(self):
        results = simulate(50, [49, 50, 51, 52, 53, 54])
        self.assertEqual(results, [
            (49, True), (50, True), (51, False),
            (52, True), (53, True), (54, True)])
        for frame in range(1, 50):
            r = simulate(frame, [frame, frame + 1, frame + 2])
            self.assertEqual(r, [
                (frame, True), (frame + 1, False), (frame + 2, True)])


if __name__ == "__main__":
    unittest.main(verbosity=2)
