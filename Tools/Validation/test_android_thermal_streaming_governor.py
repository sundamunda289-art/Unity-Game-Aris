#!/usr/bin/env python3
"""Android ADPF thermal streaming regression. Source contracts, not handset FPS."""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "Assets/AarisWorld/Scripts"
THERMAL = (SRC / "AarisAndroidThermalGovernor.cs").read_text()
RUNTIME = (SRC / "AarisMobileRuntime.cs").read_text()
WORLD = (SRC / "AarisAdditiveChunkStreamer.cs").read_text()
CARS = (SRC / "AarisVehicleWorld.cs").read_text()
GATE = (SRC / "AarisMobileStreamingFrameGate.cs").read_text()
ADMISSION_FIXTURE = (ROOT / "Tools/Validation/StreamingAdmissionTests.cs").read_text()
META = (SRC / "AarisAndroidThermalGovernor.cs.meta").read_text()


def thermal_policy(statuses):
    # Exact small decision-state mirror of ObserveStatus in production.
    cooldown = 0
    suspend = False
    results = []
    for status in statuses:
        if status < 0 or status > 6:
            results.append(suspend)
            continue
        if status >= 3:
            suspend = True
            cooldown = 0
        elif status >= 2:
            cooldown = 0
        elif suspend:
            cooldown += 1
            if cooldown >= 3:
                suspend = False
                cooldown = 0
        results.append(suspend)
    return results


class AndroidThermalStreamingTests(unittest.TestCase):
    def test_thermal_api_is_android_only_and_rate_limited(self):
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", THERMAL)
        self.assertIn('new AndroidJavaClass("android.os.Build$VERSION")', THERMAL)
        self.assertIn('int sdk = version.GetStatic<int>("SDK_INT");', THERMAL)
        self.assertIn("if (sdk < 29)", THERMAL)
        self.assertIn('new AndroidJavaClass("com.unity3d.player.UnityPlayer")', THERMAL)
        self.assertIn('activity.Call<AndroidJavaObject>("getSystemService", "power")', THERMAL)
        self.assertIn('powerManager.Call<int>("getCurrentThermalStatus")', THERMAL)
        self.assertIn("private const float SampleIntervalSeconds = 5f;", THERMAL)
        self.assertIn("private const int HeadroomPollPeriod = 3;", THERMAL)
        self.assertIn("supportsHeadroom = sdk >= 30;", THERMAL)
        self.assertIn('powerManager.Call<float>("getThermalHeadroom", 15)', THERMAL)
        self.assertIn("statusPolls++ % HeadroomPollPeriod == 0", THERMAL)
        self.assertIn("float.IsNaN(headroom)", THERMAL)
        self.assertIn("catch (AndroidJavaException)", THERMAL)
        self.assertIn("yield return delay;", THERMAL)
        for forbidden in ("void Update(", "void FixedUpdate(", "InvokeRepeating(", "AndroidJavaProxy"):
            self.assertNotIn(forbidden, THERMAL)

    def test_cold_start_and_lifecycle_are_safe(self):
        start = RUNTIME.split("private IEnumerator Start()", 1)[1]
        self.assertLess(start.index("yield return null;"), start.index(
            "gameObject.AddComponent<AarisAndroidThermalGovernor>();"))
        self.assertIn("if (GetComponent<AarisAndroidThermalGovernor>() == null)", start)
        self.assertIn("[DisallowMultipleComponent]", THERMAL)
        self.assertIn("if (active != null && active != this)", THERMAL)
        self.assertIn("private void OnDestroy()", THERMAL)
        self.assertIn("powerManager.Dispose();", THERMAL)
        self.assertIn("if (active == this)", THERMAL)
        self.assertIn("RuntimeInitializeLoadType.SubsystemRegistration", THERMAL)
        self.assertIn("ThermalStatus = -1;", THERMAL)
        self.assertIn("SuspendSpeculativePrefetch = false;", THERMAL)
        self.assertRegex(META, r"(?m)^guid: [0-9a-f]{32}$")

    def test_thermal_pressure_has_hysteresis_and_recovers(self):
        self.assertIn("private const int SevereStatus = 3;", THERMAL)
        self.assertIn("private const int CoolRecoverySamples = 3;", THERMAL)
        self.assertIn("if (status < 0 || status > 6) return;", THERMAL)
        self.assertIn("if (status >= SevereStatus || ForecastThermalHeadroom >= 1f)", THERMAL)
        self.assertIn("if (status >= 2 || ForecastThermalHeadroom >= 0.95f)", THERMAL)
        self.assertIn("++coolSamples >= CoolRecoverySamples", THERMAL)
        self.assertEqual(thermal_policy([0, 2, 3, 2, 1, 0, 1, 3, 0, 0, 0]),
                         [False, False, True, True, True, True, False, True, True, True, False])
        self.assertEqual(thermal_policy([6, -1, 7, 2, 0, 0, 0]),
                         [True, True, True, True, True, True, False])

    def test_unavailable_thermal_api_fails_open_without_crash_loop(self):
        self.assertIn("if (!InitializePowerManager()) yield break;", THERMAL)
        self.assertIn("if (!SampleThermalStatus()) yield break;", THERMAL)
        self.assertIn("DisableUnsupported(ex);", THERMAL)
        self.assertIn("enabled = false;", THERMAL)
        self.assertIn('Debug.LogWarning("AARIS_THERMAL_UNAVAILABLE: "', THERMAL)
        self.assertNotIn("while (true)", THERMAL)

    def test_native_chunk_prefetch_only_near_geometry_unchanged(self):
        android_block = WORLD.split("else if (loadCell >= 0)", 1)[1].split("#endif", 1)[0]
        self.assertIn("if (speculativeLoadBlocked) return;", android_block)
        budget = WORLD.split("int speculativeLimit =", 1)[1].split("// Only make room", 1)[0]
        self.assertIn("? maxSpeculativeResidentCells : int.MaxValue", budget)
        self.assertIn("AarisAndroidThermalGovernor.ModerateThermalPressure", budget)
        self.assertIn("speculativeLimit = Math.Min(speculativeLimit, 1)", budget)
        self.assertIn("bool speculativeLoadBlocked = !loadIsUrgent &&", budget)
        self.assertIn("AarisAndroidThermalGovernor.SuspendSpeculativePrefetch", budget)
        self.assertIn("speculativeResidentCount >= speculativeLimit", budget)
        self.assertIn("if (!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild()) return;", android_block)
        self.assertIn("if (!AarisMobileStreamingFrameGate.TryClaimUrgentSceneLoad()) return;", android_block)
        self.assertIn("if (loadIsUrgent)", android_block)
        # Original actual-position policy always overrides optional thermal prefetch.
        self.assertIn("bool urgent = distance <= entrySquared;", WORLD)
        self.assertIn("loadIsUrgent = urgent;", WORLD)
        self.assertIn("if (unloadCell >= 0 && (loadCell < 0 || !loadIsUrgent))", WORLD)
        self.assertIn("SceneManager.LoadSceneAsync(path, LoadSceneMode.Additive)", WORLD)
        self.assertIn("SceneManager.UnloadSceneAsync(path)", WORLD)

    def test_predicted_car_import_is_the_only_vehicle_path_modified(self):
        self.assertIn("if (closestType < 0 && predictedPrewarmPosition != playerPosition)", CARS)
        self.assertIn("if (!AarisAndroidThermalGovernor.SuspendSpeculativePrefetch)", CARS)
        self.assertIn("if (closestType >= 0) StartAuthoredBodyPrewarm(closestType);", CARS)
        self.assertIn("if (!Build(record, pos))", CARS)
        self.assertIn("TryResolvePlanned(record, out Vector3 pos)", CARS)
        self.assertIn("FarthestReplaceableParkedCar(", CARS)

    def test_diagnostics_report_real_status_not_inferred_temperature(self):
        self.assertIn("public int androidThermalStatus = -1;", RUNTIME)
        self.assertIn("public float androidForecastThermalHeadroom = -1f;", RUNTIME)
        self.assertIn("s.androidForecastThermalHeadroom = AarisAndroidThermalGovernor.ForecastThermalHeadroom;", RUNTIME)
        self.assertIn("public bool speculativePrefetchSuspended, moderateThermalPressure;", RUNTIME)
        self.assertIn("s.androidThermalStatus = AarisAndroidThermalGovernor.ThermalStatus;", RUNTIME)
        self.assertIn("s.speculativePrefetchSuspended = AarisAndroidThermalGovernor.SuspendSpeculativePrefetch;", RUNTIME)
        self.assertIn("s.moderateThermalPressure = AarisAndroidThermalGovernor.ModerateThermalPressure;", RUNTIME)

    def test_shared_native_gate_uses_one_cached_os_thermal_decision(self):
        admission = GATE.split("internal static bool TryClaimOptionalModelBuild()", 1)[1].split(
            "internal static ResourceRequest TryStartOptionalLoadAsync", 1)[0]
        self.assertIn("AarisAndroidThermalGovernor.SuspendSpeculativePrefetch", admission)
        self.assertIn("AarisAndroidThermalGovernor.ModerateThermalPressure", admission)
        self.assertIn("frame - lastThermalModelAdmissionFrame < thermalGap", admission)
        self.assertIn("lastThermalModelAdmissionFrame = frame;", admission)
        self.assertIn("ThermalSpacingDeferredAttempts++;", admission)
        self.assertIn("ThermalSpacingDeferredAttempts = 0;", GATE)
        self.assertIn("private static int lastThermalModelAdmissionFrame = -1;", GATE)
        self.assertIn("internal static bool TryClaimUrgentSceneLoad()", GATE)
        self.assertIn("return TryClaimOptionalModelBuild(true);", GATE)
        self.assertIn("if (!urgentScene && thermalGap > 0", GATE)
        self.assertIn("private static void ThermalAdmissionSpacing()", ADMISSION_FIXTURE)
        self.assertIn("ThermalAdmissionSpacing();", ADMISSION_FIXTURE)
        self.assertIn("AarisAndroidThermalGovernor.SuspendSpeculativePrefetch = false;", ADMISSION_FIXTURE)
        for forbidden in ("getCurrentThermalStatus", "getThermalHeadroom", "AndroidJavaClass",
                          "Resources.UnloadUnusedAssets", "QualitySettings.", "Screen.SetResolution"):
            self.assertNotIn(forbidden, admission)

    def test_shared_thermal_pressure_is_diagnosable(self):
        self.assertIn("public int optionalStreamThermalSpacingDeferrals;", RUNTIME)
        self.assertIn("s.optionalStreamThermalSpacingDeferrals =", RUNTIME)
        self.assertIn("AarisMobileStreamingFrameGate.ThermalSpacingDeferredAttempts;", RUNTIME)

    def test_no_visual_quality_or_terrain_mutations(self):
        for token in ("renderScale", "shadowDistance =", "treeDistance =",
                      "detailObjectDistance =", "msaaSampleCount =", "SetResolution(",
                      "TerrainData.", "qualityLevel", "Destroy(",
                      "Resources.UnloadUnusedAssets"):
            self.assertNotIn(token, THERMAL) if token != "Destroy(" else self.assertNotIn(" Destroy(", THERMAL)
        self.assertNotIn("PlayerSettings.", THERMAL)


if __name__ == "__main__":
    unittest.main(verbosity=2)
