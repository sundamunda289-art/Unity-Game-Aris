#!/usr/bin/env python3
"""Source contract for frame-separated Android vehicle body/wheel imports.

This is deliberately NOT a native Unity compile or handset FPS benchmark.
"""
import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleWorld.cs").read_text(encoding="utf-8")
CATALOG = json.loads(
    (ROOT / "Assets/Resources/AarisVehicles/vehicle_catalog.json").read_text(encoding="utf-8")
)


class AuthoredWheelAsyncRegression(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.commit_body = SOURCE.split("private void CommitAuthoredBody(", 1)[1].split(
            "private void PumpAuthoredBodyLoads()", 1
        )[0]
        cls.pump = SOURCE.split("private void PumpAuthoredBodyLoads()", 1)[1].split(
            "private bool StartAuthoredBodyPrewarm(", 1
        )[0]
        cls.prefetch = SOURCE.split("private bool StartAuthoredBodyPrewarm(", 1)[1].split(
            "private uint Next()", 1
        )[0]

    def test_android_never_sync_loads_authored_wheel(self):
        self.assertIn("GameObject authoredWheel = loadedWheel;", self.commit_body)
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", self.commit_body)
        self.assertIn("#else\n                GameObject authoredWheel =\n"
                      "                    Resources.Load<GameObject>(item.authoredWheelResource);",
                      self.commit_body)
        self.assertLess(self.commit_body.index("GameObject authoredWheel = loadedWheel;"),
                        self.commit_body.index("Resources.Load<GameObject>(item.authoredWheelResource)"))
        self.assertIn("HasCompatibleUrpMaterials(authoredWheel)", self.commit_body)
        self.assertIn("item.preserveAuthoredUrpMaterials = true;", self.commit_body)

    def test_body_and_wheel_have_separate_admitted_frames(self):
        self.assertIn("authoredBodiesAwaitingWheels[i] = body;", self.pump)
        self.assertIn("AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<GameObject>(",
                      self.pump)
        self.assertIn("vehicleTypes[i].authoredWheelResource);", self.pump)
        self.assertIn("!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild()", self.pump)
        self.assertIn("!HasEmbeddedWheelSet(body)", self.pump)
        self.assertLess(self.pump.index("authoredBodiesAwaitingWheels[i] != null"),
                        self.pump.index("var request = authoredBodyRequests[i];"))
        self.assertLess(self.pump.index("authoredBodiesAwaitingWheels[i] = body;"),
                        self.pump.rindex("authoredBodyResolved[i] = true;"))

    def test_no_duplicate_body_request_while_wheel_pending(self):
        self.assertIn("private ResourceRequest[] authoredWheelRequests;", SOURCE)
        self.assertIn("private GameObject[] authoredBodiesAwaitingWheels;", SOURCE)
        self.assertIn("authoredWheelRequests = new ResourceRequest[vehicleTypes.Length];", SOURCE)
        self.assertIn("authoredBodiesAwaitingWheels = new GameObject[vehicleTypes.Length];", SOURCE)
        # The completed body request is intentionally NOT cleared when we first
        # queue its wheel. Nearest-first prefetch checks this sentinel.
        hold = self.pump.split("authoredBodiesAwaitingWheels[i] = body;", 1)[1].split(
            "#endif", 1
        )[0]
        self.assertIn("continue;", hold)
        self.assertNotIn("authoredBodyRequests[i] = null;", hold)
        self.assertIn("authoredBodyRequests[typeIndex] != null", SOURCE)
        self.assertIn("authoredBodyRequests[i] != null", self.prefetch)

    def test_wheels_count_toward_existing_two_import_limit(self):
        self.assertIn("if (inFlight >= maxConcurrentAuthoredLoads) return false;", self.prefetch)
        self.assertIn("ResourceRequest pendingWheel = authoredWheelRequests[j];", self.prefetch)
        self.assertIn("if (pendingWheel != null && !pendingWheel.isDone) inFlight++;",
                      self.prefetch)

    def test_original_visual_models_and_limits_retained(self):
        sedan = next(t for t in CATALOG["vehicles"] if t["id"] == "Sedan")
        self.assertTrue(sedan["authoredModelResource"])
        self.assertTrue(sedan["authoredWheelResource"])
        self.assertIn("MaximumNearbyVehicles = 10", SOURCE)
        self.assertIn("LoadRadius = 105f", SOURCE)
        self.assertIn("UnloadRadius = 160f", SOURCE)
        self.assertIn("wheelPrefabs[i] = authoredWheel;", self.commit_body)


if __name__ == "__main__":
    unittest.main()
