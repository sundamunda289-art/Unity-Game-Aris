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
        # The four intentionally interactive houses must instantiate a
        # real physical leaf in a genuine wall hole. The default Create()
        # path for other doorless combat homes stays unchanged.
        self.assertIn("transform, interactionTarget, prefab,", expansion)
        self.assertIn("includePanels: true)", expansion)
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
        self.assertRegex(modular, r"if \(includePanels\)\s+door = Match")

    def test_interactive_door_swings_and_keeps_real_collider(self):
        modular = MODULAR.read_text(encoding="utf-8")
        panel = (SCRIPTS / "AarisBreakablePanel.cs").read_text(encoding="utf-8")
        front = modular.split("private static void Front(", 1)[1].split(
            "private static void Back(", 1)[0]
        self.assertIn('new GameObject("AutomaticBreakableDoorHinge")', front)
        self.assertIn('GameObject leaf = Box(hinge.transform,"RealDoorLeaf",', front)
        self.assertIn("leaf.transform,leaf.GetComponent<BoxCollider>(),player,true,85f,root);", front)
        self.assertIn("private const float NearRadius = 3.2f;", panel)
        self.assertIn("transform.localRotation = Quaternion.Euler(0f, currentAngle, 0f);", panel)
        self.assertIn("if (!shouldOpen && Mathf.Abs(currentAngle) > .01f && ClosingBlocked())", panel)
        self.assertIn("if (solid != null) solid.enabled = false;", panel)

    def test_interactive_door_sounds_use_packaged_clips_and_no_frame_allocations(self):
        panel = (SCRIPTS / "AarisBreakablePanel.cs").read_text(encoding="utf-8")
        packaged = RESOURCES / "SFX/CC0"
        self.assertTrue((packaged / "impactMetal_light_000.ogg").is_file())
        self.assertTrue((packaged / "impactSoft_heavy_000.ogg").is_file())
        self.assertIn('Resources.Load<AudioClip>(', panel)
        self.assertIn('"SFX/CC0/impactMetal_light_000"', panel)
        self.assertIn('"SFX/CC0/impactSoft_heavy_000"', panel)
        self.assertIn("if (!soundsLoaded)", panel)
        self.assertIn("soundSource.spatialBlend = 1f;", panel)
        self.assertIn("if (shouldOpen != lastRequestedOpen)", panel)
        self.assertIn("soundSource.PlayOneShot(cue,", panel)
        self.assertIn("lastRequestedOpen = shouldOpen;", panel)

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


    def test_saved_scene_house_colliders_can_reenter_streaming(self):
        # Regression for 56 native MeshColliders serialized with m_Enabled:0
        # while AarisNearbyColliders.Awake() intentionally skips disabled
        # colliders. Such walls would remain permanently non-solid in play.
        scene = (ROOT / "Assets/TerrainDemoScene_URP/Scenes/"
                 "TerrainDemoScene.unity").read_text(encoding="utf-8")
        sections = re.split(r"(?=^--- !u!)", scene, flags=re.MULTILINE)
        gameobjects = {}
        colliders = []
        streamer = None
        for section in sections:
            match = re.match(r"--- !u!(\d+) &([-\d]+)", section)
            if not match:
                continue
            component_type, object_id = int(match.group(1)), match.group(2)
            owner = re.search(r"(?m)^  m_GameObject: \{fileID: ([-\d]+)\}", section)
            if component_type == 1:
                name = re.search(r"(?m)^  m_Name: (.*)$", section)
                if name:
                    gameobjects[object_id] = name.group(1)
            elif component_type == 64 and owner:
                colliders.append((object_id, owner.group(1), section))
            elif component_type == 114 and (
                "guid: f99aebde001f56119c583c593f64c271" in section
            ):
                streamer = section

        self.assertIsNotNone(streamer, "Missing authored collider streamer")
        tracked = set(re.findall(
            r"(?m)^  - \{fileID: ([-\d]+)\}", streamer
        ))
        tracked_meshes = [
            (object_id, gameobjects.get(go, "<unknown>"), section)
            for object_id, go, section in colliders if object_id in tracked
        ]
        self.assertGreaterEqual(len(tracked_meshes), 90)
        disabled = [
            (name, object_id) for object_id, name, section in tracked_meshes
            if re.search(r"(?m)^  m_Enabled: 0$", section)
        ]
        self.assertEqual(disabled, [], f"Phantom house mesh colliders: {disabled}")
        missing_mesh = [
            (name, object_id) for object_id, name, section in tracked_meshes
            if "m_Mesh: {fileID: 0}" in section or
               "m_Mesh: {fileID: 0," in section
        ]
        self.assertEqual(missing_mesh, [])
        self.assertIn("target: {fileID: 201417}", streamer)
        runtime = (SCRIPTS / "AarisNearbyColliders.cs").read_text(encoding="utf-8")
        self.assertIn("if (c == null || !c.enabled) continue;", runtime)
        self.assertIn("if (!c.enabled) c.enabled = true;", runtime)
        # Rebuilding the map must not silently revert #505's native fix.
        # Gate ALL distant colliders at runtime, only AFTER the streamer
        # has indexed their local geometry in Awake.
        generator = (ROOT / "Tools/MapBuild/mobile_review.py").read_text(
            encoding="utf-8")
        self.assertIn("d['m_Enabled']=1", generator)
        self.assertIn("initially_disabled=0", generator)
        self.assertNotIn("d['m_Enabled']=int(dx*dx+dz*dz<=55*55)", generator)

    def test_native_house_baker_preserves_unsaved_editor_scenes(self):
        src = (ROOT / "Assets/AarisWorld/Editor/AarisGpt6OpenEntrySceneBaker.cs").read_text(encoding="utf-8")
        self.assertIn("SceneManager.sceneCount", src)
        self.assertIn("SceneManager.GetSceneAt(i)", src)
        self.assertIn("openScene.IsValid() && openScene.isDirty", src)
        self.assertIn("UNSAVED_OPEN_SCENE_BLOCKED", src)
        self.assertLess(src.index("openScene.isDirty"),
                        src.index("EditorSceneManager.OpenScene"))



if __name__ == "__main__":
    unittest.main(verbosity=2)
