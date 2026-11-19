"""Standalone static mesh and runtime checks for original parked-bike scenery."""
import re
import unittest
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
ASSET = ROOT / "Assets/Resources/AarisVehicles/Authored"
SCRIPT = ROOT / "Assets/AarisWorld/Scripts/AarisRoadsideBikeScenery.cs"

class BikeVisuals(unittest.TestCase):
    def test_mesh_has_correct_rims_tyres_materials_and_bounds(self):
        lines = (ASSET / "AarisRoadBike.obj").read_text().splitlines()
        vertices = [tuple(map(float, l.split()[1:])) for l in lines if l.startswith("v ")]
        triangles = [l for l in lines if l.startswith("f ")]
        materials = {l.split()[1] for l in lines if l.startswith("usemtl ")}
        self.assertTrue(3000 <= len(vertices) <= 5000)
        self.assertTrue(6000 <= len(triangles) <= 8500)
        self.assertEqual(materials, {"Paint", "Tyre", "Rim", "Engine",
                                     "Seat", "Headlamp", "TailLamp", "Amber"})
        self.assertIn("mtllib AarisRoadBike.mtl", lines)
        self.assertTrue(all(len(f.split()) == 4 for f in triangles))
        for axis, (lower, upper) in enumerate(((-.6, .6), (0, 1.6), (-1.3, 1.3))):
            self.assertGreaterEqual(min(v[axis] for v in vertices), lower)
            self.assertLessEqual(max(v[axis] for v in vertices), upper)

    def test_unity_metadata_is_stable_unique(self):
        files = [ASSET / "AarisRoadBike.obj", ASSET / "AarisRoadBike.mtl", SCRIPT]
        ids = []
        for asset in files:
            self.assertTrue(asset.is_file())
            meta = Path(str(asset) + ".meta")
            match = re.search(r"(?m)^guid: ([0-9a-f]{32})$", meta.read_text())
            self.assertIsNotNone(match)
            ids.append(match.group(1))
        self.assertEqual(len(ids), len(set(ids)))
        self.assertIn("Original", (ROOT / "Tools/Art/generate_road_bike.py").read_text())

    def test_body_paint_variants_preserve_wheel_and_light_materials(self):
        source = SCRIPT.read_text()
        self.assertIn("int[] fallbackOrder = {1,2,3,4,0,7,5,6};", source)
        mesh = (ASSET / "AarisRoadBike.obj").read_text()
        sections = [line.split()[1] for line in mesh.splitlines()
                    if line.startswith("usemtl ")]
        mapping = {"Paint": 0, "Tyre": 1, "Rim": 2, "Engine": 3,
                   "Seat": 4, "Headlamp": 5, "TailLamp": 6, "Amber": 7}
        expected = [mapping[label] for label in sections]
        self.assertEqual(expected, [1,2,3,4,0,7,5,6])
        self.assertIn("private readonly Material[] paintVariants = new Material[4];", source)
        self.assertIn("paintVariants[0] = surfaces[0];", source)
        self.assertIn("paintVariants[i] = new Material(surfaces[0])", source)
        self.assertIn("StablePaintIndex(point.id)", source)
        self.assertIn("hash = (hash ^ c) * 16777619u;", source)
        self.assertIn("kind == 0 ? paintVariants[paint] : surfaces[kind]", source)
        self.assertIn("renderer.sharedMaterials = layouts[paintIndex][r];", source)
        self.assertIn("for (int i = 1; i < paintVariants.Length; i++)", source)
        self.assertNotIn("new Material(surfaces[0])", source.split(
            "private GameObject CreateBike(", 1)[1])

    def test_mobile_streaming_is_bounded_and_ground_validated(self):
        code = SCRIPT.read_text()
        for marker in ("MaximumVisibleBikes = 3", "LoadRadius = 45f",
                       "UnloadRadius = 66f", "index++ % 2 == 0",
                       "nextPoll = now + .4f", "Physics.RaycastNonAlloc(",
                       "Physics.OverlapBoxNonAlloc(", "HasPotentialInactiveObstacle(",
                       "root.AddComponent<BoxCollider>()",
                       "AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<GameObject>(ModelResource)",
                       "Destroy(material);"):
            self.assertIn(marker, code)
        self.assertNotIn("AddComponent<Rigidbody>", code)
        self.assertNotIn("AddComponent<WheelCollider>", code)
        self.assertNotIn("AarisVehicleMotor", code)
        update = code.split("private void Update()", 1)[1].split(
            "private bool TryParkingSpot(", 1)[0]
        self.assertEqual(update.count("CreateBike("), 1)
        self.assertIn("if (visibleCount >= MaximumVisibleBikes)", update)

    def test_full_pool_prefers_closer_bikes_without_churn(self):
        update = SCRIPT.read_text().split("private void Update()", 1)[1].split(
            "private bool TryParkingSpot(", 1)[0]
        for marker in ("BikePlan parkedVictim = null;",
                       "const float PriorityMarginMetres = 8f;",
                       "if (parkedVictim == null) return;",
                       "if (distanceSq <= farthestDistanceSq) continue;"):
            self.assertIn(marker, update)
        def victim(distances, nearest):
            cutoff, choice = (nearest + 8) ** 2, None
            for i, d in enumerate(distances):
                if d*d > cutoff:
                    cutoff, choice = d*d, i
            return choice
        self.assertEqual(victim([60, 54, 30], 12), 0)
        self.assertIsNone(victim([29, 31, 40], 35))
        self.assertIsNone(victim([18, 23, 25], 20))
        self.assertEqual(victim([51, 51, 40], 20), 0)

    def test_failed_bike_build_preserves_existing_bikes(self):
        update = SCRIPT.read_text().split("private void Update()", 1)[1].split(
            "private bool TryParkingSpot(", 1)[0]
        self.assertLess(update.index("if (!TryParkingSpot("),
                        update.index("GameObject candidate = parkedVictim != null"))
        self.assertLess(update.index("if (candidate == null) return;"),
                        update.index("parkedVictim.visible = null;"))
        self.assertLess(update.index("parkedVictim.visible = null;"),
                        update.index("nearest.visible = candidate;"))
        self.assertNotIn("Destroy(parkedVictim.visible);", update)
        self.assertIn("parkedVictim.nextProbe = now + 3f;", update)

    def test_automotive_coat_is_paint_only_and_variants_shared(self):
        source = SCRIPT.read_text()
        setup = source.split("private void SetupMaterials()", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertIn('surfaces[0].SetFloat("_ClearCoatMask", .27f)', setup)
        self.assertIn('surfaces[0].SetFloat("_ClearCoatSmoothness", .75f)', setup)
        self.assertEqual(setup.count('EnableKeyword("_CLEARCOAT")'), 1)
        self.assertIn("paintVariants[i] = new Material(surfaces[0])", setup)
        self.assertNotIn("new Texture2D(", source)
        self.assertNotIn("new Material(", source.split("private void Update()", 1)[1].split(
            "private void OnDestroy()", 1)[0])

    def test_parked_model_must_have_active_real_geometry(self):
        source = SCRIPT.read_text()
        gate = source.split("private static bool HasVisibleBikeMesh(", 1)[1].split(
            "private GameObject CreateBike(", 1)[0]
        for field in ("!renderer.enabled", "renderer.forceRenderingOff",
                      "!renderer.gameObject.activeInHierarchy",
                      "filter.sharedMesh.vertexCount > 0",
                      "skinned.sharedMesh.vertexCount > 0"):
            self.assertIn(field, gate)
        self.assertIn("if (!HasVisibleBikeMesh(renderers))", source)
        self.assertLess(source.index("if (!HasVisibleBikeMesh(renderers))"),
                        source.index("root.AddComponent<BoxCollider>()"))

    def test_static_kickstand_lean_and_mobile_lod_keep_physics_safe(self):
        source = SCRIPT.read_text()
        builder = source.split("private GameObject CreateBike(", 1)[1].split(
            "private void OnDestroy()", 1)[0]
        self.assertIn("imported.transform.localRotation *= Quaternion.Euler(0f, 0f, 3.5f)", builder)
        self.assertIn("root.AddComponent<LODGroup>()", builder)
        self.assertIn("lod.SetLODs(new[] { new LOD(.012f, renderers) });", builder)
        self.assertIn("lod.RecalculateBounds();", builder)
        self.assertIn("SystemInfo.systemMemorySize <= 3072", builder)
        self.assertIn("renderer.shadowCastingMode = ShadowCastingMode.Off;", builder)
        self.assertIn("root.AddComponent<BoxCollider>()", builder)
        self.assertNotIn("AddComponent<Rigidbody>", builder)
        self.assertNotIn("AddComponent<WheelCollider>", builder)

    def test_crowded_parking_queries_are_complete_and_keep_safety_checks(self):
        source = SCRIPT.read_text()
        parking = source.split("private bool TryParkingSpot(", 1)[1].split(
            "private static int SurfaceIndex(", 1)[0]
        # A nonallocating buffer reaching capacity is not a complete set of
        # unsorted physics hits. Bikes must not vanish at dense road crossings.
        for required in (
            "private readonly RaycastHit[] denseSupportHits = new RaycastHit[96];",
            "private readonly Collider[] denseBlockingHits = new Collider[96];",
        ):
            self.assertIn(required, source)
        for required in (
            "RaycastHit[] floorHits = supportHits;",
            "if (count >= supportHits.Length)",
            "if (count >= denseSupportHits.Length)",
            "Physics.RaycastAll(origin, Vector3.down,",
            "RaycastHit hit = floorHits[i];",
            "Collider[] foundColliders = blockingHits;",
            "if (obstacles >= blockingHits.Length)",
            "if (obstacles >= denseBlockingHits.Length)",
            "Physics.OverlapBox(center, half, rotation,",
            "Collider collider = foundColliders[i];",
            "if (hit.collider == null || hit.normal.y < .965f)",
            "HasPotentialInactiveObstacle(center,half,rotation)",
            "if (collider != null && !(collider is TerrainCollider))",
        ):
            self.assertIn(required, parking)
        self.assertNotIn("if (count >= supportHits.Length) continue;", parking)
        self.assertNotIn("if (obstacles >= blockingHits.Length) continue;", parking)
        self.assertLess(parking.index("Physics.RaycastAll("),
                        parking.index("float nearestError = 1.5f;"))
        self.assertLess(parking.index("Physics.OverlapBox(center, half, rotation,"),
                        parking.index("bool blocked = false;"))


if __name__ == "__main__":
    unittest.main()
