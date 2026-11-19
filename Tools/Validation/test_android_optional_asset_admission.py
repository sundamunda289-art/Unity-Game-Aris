#!/usr/bin/env python3
"""Static regression checks for optional Android model IO admission.

These tests verify source invariants; they are not a Unity compile or a
Snapdragon 680 runtime benchmark.
"""
from pathlib import Path
import unittest

SRC = Path(__file__).resolve().parents[2] / "Assets/AarisWorld/Scripts"
GATE = (SRC / "AarisMobileStreamingFrameGate.cs").read_text()
CARS = (SRC / "AarisVehicleWorld.cs").read_text()
BIKES = (SRC / "AarisRoadsideBikeScenery.cs").read_text()
RIVALS = (SRC / "AarisOfflineBRDirector.cs").read_text()
WEAPONS = (SRC / "AarisWeaponCatalog.cs").read_text()


class OptionalAssetAdmission(unittest.TestCase):
    def test_budget_is_android_only_and_preserves_fast_phones(self):
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", GATE)
        self.assertIn("bool constrained = ramMb > 0 && ramMb <= 4608;", GATE)
        self.assertIn("optionalLoadInFlight != null && !optionalLoadInFlight.isDone", GATE)
        self.assertIn("if (!TryClaimOptionalModelBuild()) return null;", GATE)
        self.assertIn("optionalLoadInFlight = request;", GATE)
        self.assertIn("optionalLoadInFlight = null;", GATE)

    def test_admission_preserves_unmodified_assets_and_priority(self):
        self.assertIn("Resources.LoadAsync<T>(path)", GATE)
        self.assertIn("request.priority = -1;", GATE)
        self.assertNotIn("QualitySettings", GATE)
        self.assertNotIn("TerrainData", GATE)
        self.assertNotIn("renderScale", GATE)
        self.assertNotIn("mipmapLimit", GATE)

    def test_vehicle_startup_and_proximity_imports_share_admission(self):
        self.assertIn(
            "while ((request = AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<GameObject>(path)) == null)",
            CARS)
        self.assertIn("TryStartOptionalLoadAsync<GameObject>(\n                item.authoredModelResource)", CARS)
        self.assertIn("if (request == null) return false;", CARS)
        self.assertIn("TryStartOptionalLoadAsync<GameObject>(\n                        item.fallbackAuthoredModelResource)", CARS)
        self.assertIn("if (fallback == null) continue;", CARS)
        self.assertIn("authoredBodyRequests[i] = fallback;", CARS)

    def test_completed_vehicle_request_is_not_lost_on_busy_frame(self):
        chunk = CARS.split("private void PumpAuthoredBodyLoads()", 1)[1].split(
            "private bool StartAuthoredBodyPrewarm(", 1)[0]
        self.assertLess(chunk.index("if (!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())"),
                        chunk.index("authoredBodyRequests[i] = null;"))
        self.assertLess(chunk.index("if (fallback == null) continue;"),
                        chunk.index("authoredBodyRequests[i] = fallback;"))
        self.assertIn("else\n                {\n                    authoredBodyRequests[i] = null;", chunk)

    def test_bike_and_rival_prewarm_share_global_slot(self):
        self.assertIn(
            "bikePrewarm = AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<GameObject>(ModelResource);",
            BIKES)
        self.assertIn("if (bikePrewarm == null) return;", BIKES)
        self.assertIn("RivalVisualResourcePaths[i])) == null)", RIVALS)
        self.assertIn("TryStartOptionalLoadAsync<AnimationClip>(idlePath)", RIVALS)
        self.assertIn("TryStartOptionalLoadAsync<AnimationClip>(runPath)", RIVALS)
        self.assertIn("yield return request;", RIVALS)

    def test_vehicle_setup_is_budgeted_after_fallback_prewarm(self):
        boot = CARS.split("private IEnumerator SetupAfterBudgetAndroidPrewarm(", 1)[1].split(
            "private bool Setup()", 1)[0]
        self.assertLess(
            boot.index("while (!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())"),
            boot.index("if (!Setup(preloadedPoints, text))"))
        self.assertIn("yield return null;", boot)
        self.assertIn("AttachSessionAndHud(motor, this);", boot)

    def test_rival_prefab_native_inspection_waits_for_a_free_frame(self):
        prewarm = RIVALS.split("private IEnumerator PrewarmRivalVisuals()", 1)[1].split(
            "private static bool IsUsableRivalClip(", 1)[0]
        self.assertLess(prewarm.index("GameObject candidate = request.asset as GameObject;"),
                        prewarm.index("while (!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())"))
        self.assertLess(prewarm.index("while (!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())"),
                        prewarm.index("if (HasRenderableRivalPrefab(candidate))"))
        self.assertIn("if (candidate != null)", prewarm)

    def test_essential_weapon_preload_never_waits_for_optional_gate(self):
        # The player must not lose access to gameplay-critical weapons if an
        # optional vehicle/bike/rival request is still in flight.
        self.assertIn("Resources.LoadAsync<GameObject>(spec.modelResource)", WEAPONS)
        self.assertNotIn("TryStartOptionalLoadAsync", WEAPONS)


if __name__ == "__main__":
    unittest.main(verbosity=2)
