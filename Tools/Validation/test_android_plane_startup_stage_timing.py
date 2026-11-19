#!/usr/bin/env python3
"""One-time plane loading stage diagnostics; static gate, not Unity profiling."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2] / "Assets/AarisWorld/Scripts"
PLANE = (ROOT / "AarisOfflineDropVisuals.cs").read_text(encoding="utf-8")
RUNTIME = (ROOT / "AarisMobileRuntime.cs").read_text(encoding="utf-8")
DROP = (ROOT / "AarisOfflineDrop.cs").read_text(encoding="utf-8")

class PlaneStartupTelemetry(unittest.TestCase):
    def test_measures_resource_resolution_and_instance_separately(self):
        create = PLANE.split("internal static GameObject CreatePlane(", 1)[1].split(
            "/// <summary>", 1)[0]
        for field in ("PlaneModelResolveMs", "PlaneInstantiateMs",
                      "PlaneTotalCreationMs", "PlaneHighDetailSelected"):
            self.assertIn(field, PLANE)
        self.assertIn("Time.realtimeSinceStartupAsDouble", create)
        self.assertLess(create.index('Resources.Load<GameObject>("AarisDrop/CargoPlaneRealistic")'),
                        create.index("PlaneModelResolveMs = "))
        self.assertLess(create.index("PlaneModelResolveMs = "),
                        create.index("UnityEngine.Object.Instantiate(prefab, position, rotation);"))
        self.assertLess(create.index("UnityEngine.Object.Instantiate(prefab, position, rotation);"),
                        create.index("PlaneInstantiateMs = "))
        self.assertLess(create.index('root.name = "Aaris_CargoPlane";'),
                        create.index("PlaneTotalCreationMs = "))
        self.assertIn("PlaneInstantiateMs = -1f;", create)

    def test_original_high_quality_plane_not_substituted_or_removed(self):
        self.assertIn('Resources.Load<GameObject>("AarisDrop/CargoPlaneRealistic")', PLANE)
        self.assertIn('prefab = Resources.Load<GameObject>("AarisDrop/CargoPlane")', PLANE)
        self.assertIn("bool highDetailTwin = prefab != null;", PLANE)
        self.assertIn("var slots = renderer.sharedMaterials;", PLANE)
        self.assertIn("AddAircraftMarkerLights(root.transform);", PLANE)
        self.assertIn("out propellers);", DROP)
        self.assertIn("GetPlaneCameraPose(out Vector3 initialPosition,", DROP)

    def test_reset_reports_unknown_until_aircraft_created(self):
        begin = PLANE.split("private static void ResetPlaneTimings()", 1)[1].split(
            "private static Material Paint(", 1)[0]
        self.assertIn("[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]", PLANE)
        self.assertIn("PlaneModelResolveMs = PlaneInstantiateMs = PlaneTotalCreationMs = -1f;", begin)
        self.assertIn("PlaneHighDetailSelected = false;", begin)

    def test_existing_json_emits_phase_diagnostics(self):
        sample = RUNTIME.split("private sealed class Sample", 1)[1].split(
            "private int warningCount", 1)[0]
        record = RUNTIME.split("private void Record(string reason)", 1)[1].split(
            "private static string InitializeLogFile", 1)[0]
        for name in ("planeModelResolveMs", "planeInstantiateMs",
                     "planeTotalCreationMs", "planeHighDetailSelected"):
            self.assertIn(name, sample)
            self.assertIn(name + " = AarisOfflineDropVisuals.", record)
        self.assertIn("planeModelResolveMs = -1f", sample)
        self.assertIn("planeTotalCreationMs = -1f", sample)
        self.assertIn("planeInstantiateMs = -1f", sample)

    def test_no_timing_in_high_frequency_update(self):
        hot = DROP.split("private void Update()", 1)[1].split(
            "private void OnApplicationPause(", 1)[0]
        self.assertNotIn("PlaneModelResolveMs", hot)
        self.assertNotIn("PlaneInstantiateMs", hot)
        self.assertNotIn("PlaneTotalCreationMs", hot)

if __name__ == "__main__":
    unittest.main(verbosity=2)
