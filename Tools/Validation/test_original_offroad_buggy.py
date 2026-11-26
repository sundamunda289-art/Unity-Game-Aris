#!/usr/bin/env python3
"""Mobile/offline source contracts for original open-buggy art; no Unity simulation."""
from pathlib import Path
import hashlib,json,re,subprocess,sys,unittest
R=Path(__file__).resolve().parents[2]
ART=R/"Assets/Resources/AarisVehicles/Authored"
OBJ=ART/"AarisBuggy.obj"
GEN=R/"Tools/Art/generate_aaris_buggy.py"

class OriginalBuggyTests(unittest.TestCase):
    def test_topology_bounds_and_distinct_surfaces(self):
        data=OBJ.read_text().splitlines()
        verts=[tuple(map(float,s.split()[1:4])) for s in data if s.startswith("v ")]
        faces=[s for s in data if s.startswith("f ")]
        mats={s.split()[1] for s in data if s.startswith("usemtl ")}
        self.assertTrue(500<=len(verts)<=1800)
        self.assertTrue(900<=len(faces)<=2400)
        self.assertTrue(all(len(t.split())==4 for t in faces))
        self.assertEqual(mats,{"AarisPaint","AarisDark","AarisMetal",
            "AarisSeat","AarisGlass","AarisHeadlight","AarisTaillight"})
        self.assertLess(max(abs(v[0]) for v in verts),1.05)
        self.assertLess(max(abs(v[2]) for v in verts),1.85)
        self.assertTrue(min(v[1] for v in verts)>.19)
        self.assertTrue(max(v[1] for v in verts)>1.50)
        self.assertFalse(any("wheel" in s.lower() for s in data if s.startswith("o ")))

    def test_stable_meta_and_reproducible_generated_geometry(self):
        before=hashlib.sha256(OBJ.read_bytes()).hexdigest()
        subprocess.run([sys.executable,str(GEN)],check=True,capture_output=True)
        self.assertEqual(before,hashlib.sha256(OBJ.read_bytes()).hexdigest())
        guids=[]
        for name in ("AarisBuggy.obj","AarisBuggy.mtl"):
            content=(ART/(name+".meta")).read_text()
            found=re.search(r"(?m)^guid: ([a-f0-9]{32})$",content)
            self.assertIsNotNone(found)
            guids.append(found.group(1))
        self.assertEqual(len(set(guids)),2)

    def test_catalog_physics_paint_and_urp_clearcoat_contracts(self):
        cars=json.loads((R/"Assets/Resources/AarisVehicles/vehicle_catalog.json").read_text())["vehicles"]
        # New vehicle types are welcome; protect the original fleet and unique IDs.
        self.assertGreaterEqual(len(cars), 7)
        ids = [v["id"] for v in cars]
        self.assertEqual(len(ids), len(set(ids)))
        buggy=next(v for v in cars if v["id"]=="Buggy")
        self.assertEqual(buggy["authoredModelResource"],"AarisVehicles/Authored/AarisBuggy")
        self.assertEqual(buggy["modelResource"],"AarisVehicles/Models/truck-flat")
        self.assertEqual(buggy["wheelResource"],"AarisVehicles/Models/wheel-truck")
        self.assertEqual(buggy["drivetrain"],"AWD")
        self.assertTrue(8<=buggy["maxSpeedMetres"]<=28)
        source=(R/"Assets/AarisWorld/Scripts/AarisVehicleWorld.cs").read_text()
        for label in ('case "Buggy"','label.Contains("seat")',
                      'label.Contains("aarispaint")',
                      'Shader shader = paint ? AarisShaderCache.Find("Aaris/Mobile Subject Lit") : null;',
                      'result.SetFloat("_ClearCoatMask", .34f);',
                      'if (material.HasProperty("_ClearCoatMask"))'):
            self.assertIn(label,source)
        mat=(ART/"AarisCivilianSurface.mat").read_text()
        self.assertIn("  - _CLEARCOAT",mat)
        self.assertIn("_ClearCoatMask: 0.28",mat)
        self.assertIn("_ClearCoatSmoothness: 0.72",mat)
        importer=(R/"Assets/AarisWorld/Editor/AarisVehicleImportValidation.cs").read_text()
        self.assertIn('"AarisVehicles/Authored/AarisBuggy"',importer)

if __name__=="__main__":
    unittest.main()
