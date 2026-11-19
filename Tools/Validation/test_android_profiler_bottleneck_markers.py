#!/usr/bin/env python3
"""Source contracts for Unity 6 optional CPU profiler attribution markers.

A static test cannot prove native Unity compile, player frames or GPU origin.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "Assets" / "AarisWorld" / "Scripts"

# Each method is synchronous; never keep an AutoScope alive across a
# coroutine yield, which would misleadingly measure elapsed frame time.
MARKERS = {
    "AarisCharacterVisual.cs":
        ("private bool TryInstantiateCharacter(", "k_CharacterRig",
         "Aaris.Startup.CharacterRigInstantiate"),
    "AarisVehicleWorld.cs":
        ("private bool Build(AarisVehicleRecord record,", "k_VehicleBuild",
         "Aaris.Streaming.VehicleBuild"),
    "AarisNearbyColliders.cs":
        ("private void IndexCollider(int i)", "k_ColliderGridIndex",
         "Aaris.Startup.ColliderGridItem"),
    "AarisSettlementExpansion.cs":
        ("private bool TreesBlock(AarisExpansionSite site,", "k_TreeClearance",
         "Aaris.Startup.TreeClearance"),
    "AarisOfflineDropVisuals.cs":
        ("internal static GameObject CreatePlane(", "k_PlaneCreate",
         "Aaris.Startup.PlaneCreate"),
    "AarisMobileRuntime.cs":
        ("private void Record(string reason)", "k_DiagnosticsRecord",
         "Aaris.Diagnostics.Record"),
}


class AndroidBottleneckProfilerMarkers(unittest.TestCase):
    def test_every_phase_has_one_unique_label_and_compiles_out_of_release(self):
        labels = set()
        for filename, (method, field, label) in MARKERS.items():
            with self.subTest(phase=filename):
                code = (SCRIPTS / filename).read_text(encoding="utf-8")
                self.assertIn("using Unity.Profiling;", code)
                self.assertEqual(code.count(f'new ProfilerMarker("{label}")'), 1)
                self.assertEqual(code.count(f"using var profilerScope = {field}.Auto();"), 1)
                # No marker scope is active in a normal Release APK; Development
                # Build or Editor only. This test also catches accidental
                # always-on sampling while optimizing 4GB Android devices.
                declaration = (
                    "#if UNITY_EDITOR || DEVELOPMENT_BUILD\n"
                    f"        private static readonly ProfilerMarker {field} =\n"
                    f'            new ProfilerMarker("{label}");\n'
                    "#endif"
                )
                self.assertIn(declaration, code)
                index = code.index(method)
                method_start = code.index("{", index)
                prefix = code[method_start:method_start + 165]
                self.assertIn(
                    "#if UNITY_EDITOR || DEVELOPMENT_BUILD\n"
                    f"            using var profilerScope = {field}.Auto();\n"
                    "#endif",
                    prefix
                )
                self.assertNotIn("yield return", prefix)
                self.assertNotIn(label, labels)
                labels.add(label)

    def test_no_profiling_samples_wrap_unbounded_coroutines(self):
        for filename, (method, field, _) in MARKERS.items():
            with self.subTest(file=filename):
                code = (SCRIPTS / filename).read_text(encoding="utf-8")
                # The only AutoScope in each file belongs to its synchronous
                # method, avoiding begin/end across multiple rendered frames.
                self.assertEqual(code.count(".Auto();"), 1)
                i = code.index(method)
                scope = code.index(f"using var profilerScope = {field}.Auto();")
                self.assertGreater(scope, i)
                self.assertLess(scope - i, 250)

    def test_instrumented_original_runtime_paths_still_exist(self):
        character = (SCRIPTS / "AarisCharacterVisual.cs").read_text()
        car = (SCRIPTS / "AarisVehicleWorld.cs").read_text()
        colliders = (SCRIPTS / "AarisNearbyColliders.cs").read_text()
        trees = (SCRIPTS / "AarisSettlementExpansion.cs").read_text()
        plane = (SCRIPTS / "AarisOfflineDropVisuals.cs").read_text()
        diagnostics = (SCRIPTS / "AarisMobileRuntime.cs").read_text()
        self.assertIn("Instantiate(source, transform, false);", character)
        self.assertIn("private bool Build(AarisVehicleRecord record, Vector3 pos)", car)
        self.assertIn("worldBounds[i] = new Bounds(m.MultiplyPoint3x4", colliders)
        self.assertIn("IndexCollider(indexCursor);", colliders)
        self.assertIn("treeCache.TryGetValue(terrain, out AarisTreeClearanceIndex", trees)
        self.assertIn("internal static GameObject CreatePlane(", plane)
        self.assertIn('LatestReport = JsonUtility.ToJson(s);', diagnostics)
        self.assertIn('Record("frame-stall");', diagnostics)


if __name__ == "__main__":
    unittest.main(verbosity=2)
