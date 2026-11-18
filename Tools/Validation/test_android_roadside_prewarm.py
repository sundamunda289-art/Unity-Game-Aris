"""Static checks; native Unity/Android QA must be run separately."""
import re
import unittest
from pathlib import Path

CODE = (Path(__file__).resolve().parents[2] / "Assets/AarisWorld/Scripts/AarisRoadsideBikeScenery.cs").read_text()

class RoadsideTests(unittest.TestCase):
    def test_no_eager_prefab(self):
        boot = CODE.split("private void Start()", 1)[1].split("private void SetupMaterials()", 1)[0]
        self.assertNotIn("Resources.Load<GameObject>(ModelResource)", boot)
        self.assertNotIn("SetupMaterials();", boot)

    def test_request_once(self):
        self.assertEqual(CODE.count("Resources.LoadAsync<GameObject>(ModelResource)"), 1)
        self.assertIn("if (bikePrewarm == null)", CODE)
        self.assertIn("if (!bikePrewarm.isDone) return;", CODE)
        self.assertIn("bikeSource = bikePrewarm.asset as GameObject;", CODE)

    def test_original_art(self):
        self.assertIn("AarisVehicles/Authored/AarisRoadBike", CODE)
        self.assertIn("Instantiate(bikeSource, root.transform, false);", CODE)

    def test_streaming_hysteresis(self):
        def distance(name):
            m = re.search("private const float " + name + r" = ([0-9.]+)f;", CODE)
            self.assertIsNotNone(m)
            return float(m[1])
        self.assertGreater(distance("PrewarmRadius"),distance("LoadRadius"))
        self.assertGreater(distance("UnloadRadius"),distance("LoadRadius"))

if __name__ == "__main__":
    unittest.main()
