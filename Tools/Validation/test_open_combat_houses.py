#!/usr/bin/env python3
"""Source and serialized-prefab contracts for offline doorless combat homes.

These are license-free static checks. They do not replace Unity Play Mode
collision probes, native map baking or real Android frame-time measurements.
"""
import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "Assets/AarisWorld/Scripts"
RESOURCES = ROOT / "Assets/Resources"
MODULAR = SCRIPTS / "AarisModularHouse.cs"
EXPANSION = SCRIPTS / "AarisSettlementExpansion.cs"
PLAN = RESOURCES / "AarisExpansion/WorldExpansionPlan.json"
CATALOG = RESOURCES / "AarisExpansion/AarisExpansionCatalog.asset"
PREFABS = RESOURCES / "AarisBuildings"


def guarded_body(source, guard):
    """Return the exact C# brace block belonging to a known if statement."""
    where = source.index(guard)
    opening = source.index("{", where + len(guard))
    depth = 1
    i = opening + 1
    while depth:
        if i >= len(source):
            raise AssertionError("Unclosed guarded block")
        if source[i] == "{":
            depth += 1
        elif source[i] == "}":
            depth -= 1
        i += 1
    return source[opening + 1 : i - 1]


class OpenCombatHouseContracts(unittest.TestCase):
    def test_four_modular_sites_have_empty_openings_by_default(self):
        modular = MODULAR.read_text(encoding="utf-8")
        expansion = EXPANSION.read_text(encoding="utf-8")
        plan = json.loads(PLAN.read_text(encoding="utf-8"))
        interactive = [s for s in plan["sites"] if s.get("interactive")]
        self.assertEqual(len(interactive), 4)
        self.assertEqual({s["prefabKey"] for s in interactive},
                         {"cottage", "bungalow", "detached"})
        self.assertIn("GameObject originalPrefab,bool includePanels = false)",
                      modular)
        self.assertIn("!Prepare(includePanels)", modular)
        self.assertIn("Front(t,width,depth,h,player,includePanels);", modular)
        self.assertIn("Back(t,width,depth,h,includePanels);", modular)
        self.assertIn("AarisModularHouse.Create(position, rotation", expansion)
        self.assertIn("transform, interactionTarget, prefab)", expansion)
        self.assertNotIn("transform, interactionTarget, prefab, true)", expansion)

        window = modular.split("private static void Window(", 1)[1].split(
            "private static void Front(", 1)[0]
        door = modular.split("private static void Front(", 1)[1].split(
            "private static void Back(", 1)[0]
        self.assertIn('new GameObject(id + "_WindowBreakable")',
                      guarded_body(window, "if (includePanels)"))
        self.assertIn('new GameObject("AutomaticBreakableDoorHinge")',
                      guarded_body(door, "if (includePanels)"))
        self.assertIn("AddComponent<AarisBreakablePanel>()",
                      guarded_body(window, "if (includePanels)"))
        self.assertIn("AddComponent<AarisBreakablePanel>()",
                      guarded_body(door, "if (includePanels)"))
        self.assertIn("Window(root,x,z,i==0?\"FrontLeft\":\"FrontRight\",includePanels);",
                      door)
        self.assertIn('Window(root,0f,z,"Rear",includePanels);', modular)
        # A false default means no transparent glass mesh/collider or door
        # panel/hinge is created on the actual default four-site path.
        self.assertIn("if (includePanels)", modular.split(
            "private static bool Prepare(", 1)[1].split(
            "private static Material Match(", 1)[0])
        self.assertIn("if (includePanels)\n                door = Match", modular)

    def test_openings_retain_walls_and_fit_player(self):
        src = MODULAR.read_text(encoding="utf-8")
        self.assertIn("WallSection(root,left,right,DoorHeight,height,z);", src)
        self.assertIn("WallSection(root,left,right,0f,Sill,z);", src)
        self.assertIn("WallSection(root,left,right,Sill+WindowHeight,height,z);",
                      src)
        self.assertIn("private const float DoorWidth = 1.52f, DoorHeight = 2.34f;",
                      src)
        self.assertGreater(1.52, 2 * 0.35, "Player capsule must fit doorway")
        self.assertGreater(2.34, 1.78, "Player must fit below door lintel")
        self.assertIn("FlatRoofWithRealCollider", src)
        self.assertIn("FloorWithRealCollider", src)
        self.assertIn("WestSolidWall", src)
        self.assertIn("EastSolidWall", src)

    def test_existing_local_cc0_prefabs_are_stored_and_physically_bounded(self):
        catalog = CATALOG.read_text(encoding="utf-8")
        self.assertEqual(len(json.loads(PLAN.read_text())["sites"]), 21)
        for name, key in (("OpenCottage", "openCottage"),
                          ("OpenBungalow", "openBungalow"),
                          ("OpenDetached", "openDetached")):
            with self.subTest(name=name):
                prefab = PREFABS / (name + ".prefab")
                text = prefab.read_text(encoding="utf-8")
                self.assertTrue(text.startswith("%YAML"))
                self.assertNotIn("version https://git-lfs", text)
                self.assertEqual(text.count("MeshRenderer:"), 1)
                self.assertEqual(text.count("MeshFilter:"), 1)
                self.assertEqual(text.count("MeshCollider:"), 1)
                self.assertIn("m_Convex: 0", text)
                self.assertNotIn("RealDoorLeaf", text)
                self.assertNotIn("ShootableGlass", text)
                meta = (PREFABS / (name + ".prefab.meta")).read_text()
                guid = re.search(r"(?m)^guid:\s*([0-9a-f]{32})$", meta)
                self.assertIsNotNone(guid)
                self.assertRegex(catalog,
                    rf"(?m)^  {key}: \{{fileID: \d+, guid: {guid.group(1)}, type: 3\}}$")


if __name__ == "__main__":
    unittest.main(verbosity=2)
