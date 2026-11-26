import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleWorld.cs").read_text()


class AndroidVehicleAsyncBodyPrepareTests(unittest.TestCase):
    def test_unity6_async_clone_is_bounded_and_immutable(self):
        self.assertIn("AsyncInstantiateOperation<GameObject> preparedBodyOperation", SOURCE)
        self.assertIn("originalImmutable = true", SOURCE)
        self.assertIn("UnityEngine.Object.InstantiateAsync(", SOURCE)
        self.assertIn("preparedBodyOperation.priority = -1;", SOURCE)
        self.assertIn("preparedBodyOperation.allowSceneActivation = false;", SOURCE)
        self.assertIn("PreparedBodyLifetimeSeconds = 12f", SOURCE)

    def test_main_thread_integration_waits_for_global_frame_admission(self):
        pump = SOURCE[SOURCE.index("private void PumpPreparedBodyClone"):
                      SOURCE.index("private void ReleasePreparedBodyPreparation")]
        wait = pump.index("operation.IsWaitingForSceneActivation()")
        gate = pump.index("AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild()", wait)
        activate = pump.index("operation.allowSceneActivation = true;", gate)
        self.assertLess(wait, gate)
        self.assertLess(gate, activate)
        self.assertIn("AsyncVehicleIntegrationBudgetMs = 1f", SOURCE)
        self.assertIn("AsyncInstantiateOperation.SetIntegrationTimeMS(", SOURCE)

    def test_prepared_body_is_hidden_and_sync_fallback_survives(self):
        self.assertIn('new GameObject("Aaris Async Vehicle Body Staging")', SOURCE)
        self.assertIn("preparedBodyStagingRoot.SetActive(false);", SOURCE)
        build = SOURCE[SOURCE.index("private bool Build(AarisVehicleRecord record,"):
                       SOURCE.index("private bool TryReuseParkedVehicle")]
        consume = build.index("TryConsumePreparedBody(source, root.transform, out mesh)")
        fallback = build.index("mesh = Instantiate(source, root.transform, false);")
        self.assertLess(consume, fallback)

    def test_low_memory_cancels_only_speculative_prepared_body(self):
        update = SOURCE[SOURCE.index("private void Update()"):
                        SOURCE.index("private AarisVehicleRecord FarthestReplaceableParkedCar")]
        low = update.index("if (lowMemoryPending)")
        release = update.index("ReleasePreparedBodyPreparation(true);", low)
        ret = update.index("return;", release)
        self.assertLess(low, release)
        self.assertLess(release, ret)
        release_method = SOURCE[SOURCE.index("private void ReleasePreparedBodyPreparation"):
                                SOURCE.index("private bool TryConsumePreparedBody")]
        self.assertIn("operation.Cancel();", release_method)
        starter = SOURCE[SOURCE.index("private bool StartPreparedBodyClone"):
                         SOURCE.index("private void PumpPreparedBodyClone")]
        self.assertIn("if (now < parkedCacheAllowedAt ||", starter)
        self.assertIn("AarisAndroidThermalGovernor.SuspendSpeculativePrefetch", starter)

    def test_visual_and_gameplay_streaming_contracts_are_unchanged(self):
        self.assertIn("private const float LoadRadius = 105f;", SOURCE)
        self.assertIn("private const float AuthoredPrewarmRadius = 150f;", SOURCE)
        self.assertIn("private const float UnloadRadius = 160f;", SOURCE)
        self.assertIn("private const int MaximumNearbyVehicles = 10;", SOURCE)
        self.assertIn("private const int TargetVehicles = 44;", SOURCE)
        self.assertIn("private const int MaximumParkedCache = 4;", SOURCE)


if __name__ == "__main__":
    unittest.main()
