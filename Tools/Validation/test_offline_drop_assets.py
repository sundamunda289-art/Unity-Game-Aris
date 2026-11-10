#!/usr/bin/env python3
"""License-free static checks for bundled original offline BR models and scripts.

These checks are NOT a substitute for a licensed Unity compilation, physics
test, native scene render, Android profiling or Play Mode verification.
"""
from pathlib import Path
import math
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "Assets" / "Resources"
SCRIPTS = ROOT / "Assets" / "AarisWorld" / "Scripts"

MODELS = {
    "aircraft": (ASSETS / "AarisDrop" / "CargoPlane.obj", 25, 5, 19),
    "parachute": (ASSETS / "AarisDrop" / "ElongatedParachute.obj", 8, 0, 0),
    "gloo": (ASSETS / "AarisCombat" / "GlooWall.obj", 3.6, 2.1, 0.2),
    "frag": (ASSETS / "AarisCombat" / "FragGrenade.obj", 0.18, 0.22, 0),
}


def read_obj(path):
    verts, faces, used = [], [], set()
    material = None
    for line in path.read_text(encoding="utf-8").splitlines():
        words = line.strip().split()
        if not words:
            continue
        if words[0] == "v":
            vertex = tuple(float(x) for x in words[1:4])
            if len(vertex) != 3 or not all(math.isfinite(x) for x in vertex):
                raise ValueError(f"non-finite OBJ vertex: {path}")
            verts.append(vertex)
        elif words[0] == "usemtl":
            material = words[1]
            used.add(material)
        elif words[0] == "f":
            indices = [int(x.split("/")[0]) for x in words[1:]]
            if len(indices) != 3:
                raise ValueError(f"Only triangles allowed in {path}")
            faces.append(indices)
    if not verts or not faces:
        raise ValueError(f"Empty OBJ mesh: {path}")
    for face in faces:
        if not all(1 <= index <= len(verts) for index in face):
            raise ValueError(f"OBJ index outside vertex range in {path}")
    sizes = [max(v[a] for v in verts) - min(v[a] for v in verts) for a in range(3)]
    return verts, faces, used, sizes


class OfflineCombatAssetChecks(unittest.TestCase):
    def test_original_3d_models_are_complete_and_local(self):
        for name, (path, min_x, min_y, min_z) in MODELS.items():
            with self.subTest(model=name):
                self.assertTrue(path.is_file(), str(path))
                self.assertTrue(path.with_suffix(".mtl").is_file())
                self.assertTrue((Path(str(path) + ".meta")).is_file())
                self.assertTrue((Path(str(path.with_suffix(".mtl")) + ".meta")).is_file())
                lines = path.read_text(encoding="utf-8").splitlines()
                self.assertIn(f"mtllib {path.stem}.mtl", lines)
                vertices, faces, materials, bounds = read_obj(path)
                self.assertGreaterEqual(len(faces), 100)
                self.assertTrue(materials)
                self.assertGreater(bounds[0], min_x)
                self.assertGreater(bounds[1], min_y)
                self.assertGreater(bounds[2], min_z)

    def test_parachute_is_elongated_and_round_ended_not_rectangular(self):
        verts, _, _, bounds = read_obj(MODELS["parachute"][0])
        self.assertGreater(bounds[0] / max(bounds[2], 0.1), 2.8)
        near_tip = [abs(z) for x, y, z in verts if abs(x) > 4.3]
        center = [abs(z) for x, y, z in verts if abs(x) < 0.25]
        self.assertTrue(near_tip and center)
        self.assertLess(max(near_tip), max(center) * 0.65)

    def test_script_api_and_damage_compatibility(self):
        contents = {
            key: (SCRIPTS / key).read_text(encoding="utf-8")
            for key in [
                "AarisOfflineDrop.cs", "AarisOfflineDropVisuals.cs",
                "AarisOfflineGlooWall.cs", "AarisOfflineFragGrenade.cs",
                "AarisOfflineHealth.cs", "AarisOfflineTacticalHUD.cs",
                "AarisThirdPersonPlayer.cs", "AarisEquippedGun.cs"
            ]
        }
        self.assertIn("ResumeFromAerialDrop()", contents["AarisThirdPersonPlayer.cs"])
        self.assertIn("footMotor.ResumeFromAerialDrop()", contents["AarisOfflineDrop.cs"])
        self.assertIn("CollisionFlags.Below", contents["AarisOfflineDrop.cs"])
        self.assertIn("Terrain.activeTerrains", contents["AarisOfflineDrop.cs"])
        self.assertIn("IAarisDamageable", contents["AarisEquippedGun.cs"])
        self.assertIn("IAarisDamageable", contents["AarisOfflineHealth.cs"])
        self.assertIn("IAarisDamageable", contents["AarisOfflineGlooWall.cs"])
        self.assertIn("Physics.OverlapBoxNonAlloc", contents["AarisOfflineGlooWall.cs"])
        self.assertIn("MaterialPropertyBlock", contents["AarisOfflineGlooWall.cs"])
        self.assertIn("Physics.OverlapSphereNonAlloc", contents["AarisOfflineFragGrenade.cs"])
        for name in ("CargoPlane", "ElongatedParachute", "GlooWall", "FragGrenade"):
            self.assertTrue(any(name in source for source in contents.values()), name)
        for key, source in contents.items():
            self.assertNotIn("NetworkBehaviour", source)
            self.assertNotIn("Unity.Netcode", source)
            self.assertNotIn("https://", source)
            self.assertNotIn("http://", source)

    def test_parachute_model_alignment_and_android_legacy_pose(self):
        drop = (SCRIPTS / "AarisOfflineDrop.cs").read_text("utf-8")
        visuals = (SCRIPTS / "AarisOfflineDropVisuals.cs").read_text("utf-8")
        motor = (SCRIPTS / "AarisThirdPersonPlayer.cs").read_text("utf-8")
        rig = (SCRIPTS / "AarisCharacterVisual.cs").read_text("utf-8")
        self.assertIn("wingBounds.Encapsulate(wing.bounds)", visuals)
        self.assertIn("model.transform.position += target - wingBounds.center", visuals)
        self.assertIn("ApplyLegacyFallbackPose(true);", drop)
        self.assertIn("ApplyLegacyFallbackPose(false);", drop)
        self.assertIn("RestoreLegacyAerialPose()", drop)
        self.assertIn("SetCanopyOpeningScale(InitialCanopyScale)", drop)
        self.assertIn("SetCanopyOpeningScale(s)", drop)
        self.assertIn("4.35f - 2.8f * s", drop)
        self.assertIn("HasWorkingHumanoid", rig)
        self.assertIn("SetLegacyAerialPose(bool beneathCanopy)", motor)
        self.assertIn("if (footMotor != null && (characterVisual == null || !characterVisual.HasWorkingHumanoid))", drop)
        self.assertIn("footMotor.ResumeFromAerialDrop();", drop)

    def test_pc_combat_keys_do_not_trigger_two_actions(self):
        # Regression: V used to aim AND deploy cover; G dropped the gun
        # while throwing a grenade. Mobile touch actions remain unchanged.
        wall = (SCRIPTS / "AarisOfflineGlooWall.cs").read_text("utf-8")
        tactical = (SCRIPTS / "AarisOfflineTacticalHUD.cs").read_text("utf-8")
        weapons = (SCRIPTS / "AarisWeaponInventory.cs").read_text("utf-8")
        bindings = {
            "cover": re.search(r"Input\.GetKeyDown\(KeyCode\.(\w+)\)\) BeginAim", wall),
            "frag": re.search(r"Input\.GetKeyDown\(KeyCode\.(\w+)\)\) ThrowFrag", tactical),
            "aim": re.search(r"Input\.GetKeyDown\(KeyCode\.(\w+)\)\) ToggleAim", weapons),
            "drop": re.search(r"Input\.GetKeyDown\(KeyCode\.(\w+)\)\) DropCurrent", weapons),
        }
        for action, match in bindings.items():
            self.assertIsNotNone(match, f"Missing desktop input for {action}")
        keys = {name: match.group(1) for name, match in bindings.items()}
        self.assertEqual(keys, {"cover": "B", "frag": "Q", "aim": "V", "drop": "G"})
        self.assertEqual(len(set(keys.values())), len(keys))
        self.assertIn("Input.GetKeyUp(KeyCode.B)", wall)

    def test_wall_touch_hold_recovers_after_vehicle_and_focus_change(self):
        tactical = (SCRIPTS / "AarisOfflineTacticalHUD.cs").read_text("utf-8")
        cover = (SCRIPTS / "AarisOfflineGlooWall.cs").read_text("utf-8")
        self.assertIn("if (wallTouch != null) wallTouch.CancelHold();", tactical)
        self.assertIn("holding = int.MinValue;", tactical)
        self.assertIn("if (paused) CancelHold();", tactical)
        self.assertIn("if (!focused) CancelHold();", tactical)
        self.assertIn("if (paused) CancelAim();", cover)

    def test_cover_damage_ignores_nan_and_infinity(self):
        wall = (SCRIPTS / "AarisOfflineGlooWall.cs").read_text("utf-8")
        self.assertIn("float.IsNaN(amount)", wall)
        self.assertIn("float.IsInfinity(amount)", wall)

    def test_offline_healing_rejects_non_finite_amounts(self):
        health = (SCRIPTS / "AarisOfflineHealth.cs").read_text("utf-8")
        restore = health.split("public void RestoreHealth(float amount)", 1)[1]
        restore = restore.split("}", 1)[0]
        self.assertIn("float.IsNaN(amount)", restore)
        self.assertIn("float.IsInfinity(amount)", restore)

    def test_frag_blast_obstacles_not_filtered_as_damage_targets(self):
        frag = (SCRIPTS / "AarisOfflineFragGrenade.cs").read_text("utf-8")
        # Candidate filtering is intentional; collision occlusion needs world geometry.
        self.assertIn("overlaps, damageMask, QueryTriggerInteraction.Ignore", frag)
        self.assertIn("length, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore", frag)
        self.assertNotIn("length, damageMask, QueryTriggerInteraction.Ignore", frag)


    def test_new_unity_guids_are_distinct(self):
        files = [
            *ASSETS.joinpath("AarisDrop").glob("*.meta"),
            *ASSETS.joinpath("AarisCombat").glob("*.meta"),
            *SCRIPTS.glob("AarisOffline*.cs.meta")
        ]
        found = set()
        for path in files:
            content = path.read_text(encoding="utf-8")
            match = re.search(r"^guid:\s*([a-f0-9]{32})$", content, re.M)
            self.assertIsNotNone(match, str(path))
            self.assertNotIn(match.group(1), found, str(path))
            found.add(match.group(1))


if __name__ == "__main__":
    unittest.main(verbosity=2)
