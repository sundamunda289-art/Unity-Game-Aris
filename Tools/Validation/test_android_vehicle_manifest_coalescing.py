#!/usr/bin/env python3
"""One shared Android vehicle road-plan import for cars and parked bikes.

These are static/source + deterministic request-coalescing checks; they do
not emulate Unity import cost, GPU draw calls, native timing or an APK.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
CARS = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleWorld.cs").read_text()
BIKES = (ROOT / "Assets/AarisWorld/Scripts/AarisRoadsideBikeScenery.cs").read_text()


def simulate_shared_import(car_frame, bike_frame, imported_frame):
    """Two consumers request one pending document on different render frames."""
    pending = False
    requests_started = 0
    seen = {}
    for frame in range(1, max(car_frame, bike_frame, imported_frame) + 2):
        for consumer, arrived in (("car", car_frame), ("bike", bike_frame)):
            if arrived == frame:
                if not pending:
                    pending = True
                    requests_started += 1
            if frame >= arrived and frame >= imported_frame:
                seen[consumer] = frame
    return requests_started, seen


class AndroidVehicleManifestCoalescing(unittest.TestCase):
    def test_single_request_is_shared_and_reset_for_new_play_session(self):
        loader = CARS.split("internal static class AarisVehiclePointManifest", 1)[1].split(
            "internal sealed class AarisVehicleType", 1)[0]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", loader)
        self.assertIn("private static ResourceRequest pending;", loader)
        self.assertIn("private static TextAsset cached;", loader)
        self.assertIn("internal static TextAsset AndroidCached => cached;", loader)
        self.assertIn("RuntimeInitializeLoadType.SubsystemRegistration", loader)
        self.assertIn("pending = null;", loader)
        self.assertIn("cached = null;", loader)
        self.assertIn("if (cached != null) yield break;", loader)
        self.assertIn("if (pending == null)", loader)
        self.assertEqual(loader.count("Resources.LoadAsync<TextAsset>("), 1)
        self.assertIn('"AarisVehicles/vehicle_candidates"', loader)
        self.assertIn("pending.priority = 0;", loader)
        self.assertIn("yield return pending;", loader)
        self.assertIn("cached = pending.asset as TextAsset;", loader)

    def test_android_cars_use_async_manifests_and_never_retry_sync(self):
        boot = CARS.split("private IEnumerator SetupAfterBudgetAndroidPrewarm(", 1)[1].split(
            "private bool Setup()", 1)[0]
        self.assertIn("yield return AarisVehiclePointManifest.LoadAndroidAsync();", boot)
        self.assertIn("TextAsset preloadedPoints = AarisVehiclePointManifest.AndroidCached;", boot)
        self.assertIn('Resources.LoadAsync<TextAsset>("AarisVehicles/vehicle_catalog")', boot)
        self.assertIn("catalogueRequest.priority = 0;", boot)
        self.assertLess(boot.index("LoadAndroidAsync()"),
                        boot.index("Resources.LoadAsync<TextAsset>"))
        self.assertLess(boot.index("yield return catalogueRequest;"),
                        boot.index("if (!Setup(preloadedPoints, text))"))
        self.assertIn("if (!Setup(preloadedPoints, text))", boot)
        self.assertNotIn("Resources.Load<TextAsset>", boot)

        editor = CARS.split("private bool Setup()", 1)[1].split(
            "private bool Setup(TextAsset points, TextAsset catalog)", 1)[0]
        self.assertIn('Resources.Load<TextAsset>("AarisVehicles/vehicle_candidates")', editor)
        self.assertIn('Resources.Load<TextAsset>("AarisVehicles/vehicle_catalog")', editor)
        setup = CARS.split("private bool Setup(TextAsset points, TextAsset catalog)", 1)[1]
        self.assertIn("if (points == null || catalog == null)", setup)
        self.assertIn("JsonUtility.FromJson<AarisVehiclePointsFile>(points.text)", setup)
        self.assertIn("JsonUtility.FromJson<AarisVehicleTypesFile>(catalog.text)", setup)
        self.assertIn("file.hubCount != 45", setup)
        self.assertIn("file.candidates.Length < 70", setup)
        self.assertIn("vehicleTypes = definitions.vehicles;", setup)

    def test_android_roadside_bikes_join_without_synchronous_load(self):
        start = BIKES.split("private void Start()", 1)[1].split(
            "private void SetupMaterials()", 1)[0]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", start)
        self.assertIn("StartCoroutine(LoadRoadPlanAfterAndroidImport());", start)
        self.assertIn("yield return AarisVehiclePointManifest.LoadAndroidAsync();", start)
        self.assertIn("LoadRoadPlans(AarisVehiclePointManifest.AndroidCached);", start)
        self.assertIn("if (!isActiveAndEnabled) yield break;", start)
        self.assertIn('#else\n            LoadRoadPlans(Resources.Load<TextAsset>("AarisVehicles/vehicle_candidates"));', start)
        self.assertLess(start.index("yield return null; // native request completion"),
                        start.index("LoadRoadPlans(AarisVehiclePointManifest.AndroidCached);"))
        self.assertIn("file.schema != 1 || file.sizeMetres != 2000", start)
        self.assertIn('point.kind != "roadside"', start)
        self.assertIn("index++ % 2 == 0", start)
        self.assertIn("MaximumVisibleBikes = 3", BIKES)
        self.assertIn("AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<GameObject>(ModelResource)", BIKES)

    def test_simulation_shares_one_request_even_with_same_frame_and_reversed_order(self):
        for car in range(1, 9):
            for bike in range(1, 9):
                for loaded in (3, 8, 12):
                    requests, seen = simulate_shared_import(car, bike, loaded)
                    self.assertEqual(requests, 1)
                    self.assertIn("car", seen)
                    self.assertIn("bike", seen)
                    self.assertGreaterEqual(seen["car"], max(car, loaded))
                    self.assertGreaterEqual(seen["bike"], max(bike, loaded))

    def test_no_changes_to_terrain_quality_or_roadside_physics(self):
        self.assertIn("private const float LoadRadius = 45f;", BIKES)
        self.assertIn("private const float PrewarmRadius = 110f;", BIKES)
        self.assertIn("private const int TargetVehicles = 44;", CARS)
        self.assertIn("private const int MaximumNearbyVehicles = 10;", CARS)
        self.assertIn("TryParkingSpot(nearest.point, out Vector3 spot)", BIKES)
        self.assertNotIn("QualitySettings", BIKES)
        self.assertNotIn("TerrainData.SetTreeInstances", CARS)


if __name__ == "__main__":
    unittest.main(verbosity=2)
