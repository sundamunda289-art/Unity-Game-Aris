#!/usr/bin/env python3
"""Contract tests for the real read-only Unity scene partition preflight."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "Tools/Validation/world_partition_readiness.py"
spec = importlib.util.spec_from_file_location("world_partition_readiness", SCRIPT)
mod = importlib.util.module_from_spec(spec)
import sys
sys.modules[spec.name] = mod
spec.loader.exec_module(mod)


def obj(kind, uid, body):
    return "--- !u!{} &{}\n{}\n".format(kind, uid, body)


def go(uid, name):
    return obj(1, uid, "GameObject:\n  m_Name: " + name)


def transform(uid, goid, parent):
    return obj(4, uid,
               "Transform:\n  m_GameObject: {fileID: %d}\n"
               "  m_Father: {fileID: %d}" % (goid, parent))


def fixture():
    blocks = [
        go(1, "Aaris_Island_2km"), transform(2, 1, 0),
        go(3, "Districts"), transform(4, 3, 2),
        go(5, "Terrain_2x2"), transform(6, 5, 2),
        go(7, "BattleRoyaleProps"), transform(8, 7, 0),
        go(9, "FoundationsAndAprons"), transform(10, 9, 2),
        go(11, "RoadNetwork"), transform(12, 11, 2),
        go(13, "DemoDistrict"), transform(14, 13, 4),
        go(15, "House"), transform(16, 15, 14),
        go(17, "ReviewLOD_House_1"), transform(18, 17, 16),
        go(19, "MobileRuntime"), transform(20, 19, 0),
        go(26, "Terrain_1_1"), transform(27, 26, 6),
        obj(64, 21, "MeshCollider:\n  m_GameObject: {fileID: 15}"),
        obj(205, 22,
            "LODGroup:\n  m_GameObject: {fileID: 15}\n"
            "  m_LODs: [{renderers: [{fileID: 24}]}]"),
        obj(23, 24, "MeshRenderer:\n  m_GameObject: {fileID: 17}"),
        obj(114, 25,
            "MonoBehaviour:\n  m_GameObject: {fileID: 19}\n"
            "  m_AuthoritativeHouseCollider: {fileID: 21}"),
        obj(218, 28, "Terrain:\n  m_GameObject: {fileID: 26}")
    ]
    return "%YAML 1.1\n" + "".join(blocks)


class WorldPartitionReadiness(unittest.TestCase):
    def test_detects_external_collider_reference_without_lod_false_positive(self):
        scene = mod.WorldScene(fixture())
        district = scene.find_unique("DemoDistrict")
        out, incoming = scene.semantic_cross_refs(scene.descendants(district))
        self.assertEqual(len(incoming), 1)
        self.assertEqual(len(out), 0)
        self.assertEqual(next(iter(incoming)), (25, 21))
        report = scene.report()
        self.assertEqual(len(report["districts"]), 1)
        d = report["districts"][0]
        self.assertEqual(d["lod_groups"], 1)
        self.assertEqual(d["mesh_colliders"], 1)
        self.assertFalse(d["direct_move_safe"])
        self.assertFalse(report["ready_for_district_migration"])

    def test_invalid_scene_references_fail_closed(self):
        text = fixture()
        self.assertRaises(ValueError, mod.WorldScene,
                          text + go(1, "Duplicate"))
        self.assertRaises(ValueError, mod.WorldScene,
                          text.replace("m_Father: {fileID: 16}",
                                       "m_Father: {fileID: 9999}"))

    def test_strict_flag_refuses_unproven_migration(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "demo.unity"
            path.write_text(fixture())
            self.assertEqual(mod.main(["--scene", str(path), "--require-safe"]), 1)
            self.assertEqual(mod.main(["--scene", str(path)]), 0)

    def test_live_world_has_all_authored_visual_and_collision_dependencies(self):
        report = mod.audit_scene(ROOT / mod.SCENE)
        self.assertEqual(report["terrain_objects"], 4)
        self.assertEqual(report["game_objects"], 878)
        self.assertEqual(report["lod_groups"], 128)
        self.assertEqual(report["mesh_colliders"], 95)
        self.assertEqual(report["box_colliders"], 200)
        self.assertEqual(len(report["districts"]), 13)
        self.assertFalse(report["ready_for_district_migration"])
        self.assertTrue(all(not d["direct_move_safe"] for d in report["districts"]))
        self.assertTrue(all(d["lod_groups"] > 0 and d["mesh_colliders"] > 0
                            for d in report["districts"]))
        self.assertEqual(
            [r["name"] for r in report["persistent_groups"]],
            ["Terrain_2x2", "BattleRoyaleProps",
             "FoundationsAndAprons", "RoadNetwork"]
        )


if __name__ == "__main__":
    unittest.main()
