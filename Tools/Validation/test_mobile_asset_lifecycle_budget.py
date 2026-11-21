#!/usr/bin/env python3
"""Regression for per-house native mesh ownership and bounded HUD/loot polling.

Tests production source and optionally compiles the *actual* owned-mesh
component against controlled UnityEngine stubs. No real Unity frame-time claim.
"""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "Assets/AarisWorld/Scripts"
HOUSE = (SCRIPTS / "AarisModularHouse.cs").read_text()
OWNER = (SCRIPTS / "AarisOwnedMeshCleanup.cs").read_text()
DROP = (SCRIPTS / "AarisOfflineDrop.cs").read_text()
LOOT = (SCRIPTS / "AarisWeaponLootWorld.cs").read_text()


class MobileAssetLifecycleBudget(unittest.TestCase):
    def test_only_runtime_house_meshes_are_owned(self):
        self.assertIn("AarisOwnedMeshCleanup owned = home.AddComponent<AarisOwnedMeshCleanup>();", HOUSE)
        self.assertIn("PitchedRoof(t,width,depth,h,owned);", HOUSE)
        self.assertIn("CombineStaticSurfaces(t,owned);", HOUSE)
        self.assertEqual(HOUSE.count("owned.Track(mesh);"), 1)
        self.assertEqual(HOUSE.count("owned.Track(combined);"), 1)
        self.assertLess(HOUSE.index("owned.Track(mesh);"), HOUSE.index("mesh.vertices = vertices;"))
        self.assertLess(HOUSE.index("owned.Track(combined);"), HOUSE.index("combined.CombineMeshes(parts, true, true, false);"))
        self.assertNotIn("owned.Track(source)", HOUSE)
        self.assertNotIn("owned.Track(pair.Key)", HOUSE)
        self.assertIn("section.AddComponent<MeshRenderer>().sharedMaterial = pair.Key;", HOUSE)
        self.assertIn("combined.UploadMeshData(true);", HOUSE)
        self.assertIn('Destroy(mesh);', OWNER)
        self.assertIn("ownedMeshes.Clear();", OWNER)

    def test_drop_safe_area_poll_is_bounded_but_rotation_immediate(self):
        self.assertIn("private float nextSafeAreaPollAt;", DROP)
        update = DROP.split(
            "// Orientation/size changes update immediately.", 1)[1].split(
            "public void SetAction(", 1)[0]
        self.assertIn("Time.unscaledTime >= nextSafeAreaPollAt", update)
        self.assertIn("nextSafeAreaPollAt = Time.unscaledTime + 0.4f;", update)
        self.assertIn("previousScreen.x != Screen.width", update)
        self.assertIn("previousScreen.y != Screen.height", update)
        self.assertIn("ApplySafeArea();", update)
        self.assertNotIn("Screen.safeArea", update)

    def test_loot_terrain_queries_reuse_fixed_world_tiles(self):
        self.assertIn("private Terrain[] cachedTerrains;", LOOT)
        self.assertIn("Terrain[] terrains = cachedTerrains;", LOOT)
        self.assertIn("if (terrains == null || terrains.Length == 0)", LOOT)
        self.assertIn("cachedTerrains = terrains;", LOOT)
        self.assertNotIn("terrains ??= Terrain.activeTerrains;", LOOT)
        self.assertIn("TryResolveNearCandidate(c, i, terrains,", LOOT)
        self.assertIn("MaximumCandidateProbesPerFrame", LOOT)

    def test_all_live_mobile_huds_bound_safe_area_native_queries(self):
        # Each visible HUD still notices a screen rotation immediately. Inset-
        # only changes get a bounded update instead of repeated JNI calls
        # during every 30/45/60 FPS frame. Never replace safe-area anchors.
        configs = (
            ("AarisBattleHUD.cs", "UpdateSafeArea(", "previousScreen == resolution"),
            ("AarisWeaponHUD.cs", "UpdateArea(", "previousScreen == screen"),
            ("AarisVehicleHUD.cs", "UpdateSafe(", "screen == lastScreen"),
            ("AarisOfflineTacticalHUD.cs", "RefreshSafeArea()", "previousScreen == screen"),
        )
        for file_name, method, resolution_guard in configs:
            with self.subTest(hud=file_name):
                src = (SCRIPTS / file_name).read_text()
                self.assertIn("private float nextSafeAreaPollAt;", src)
                if file_name == "AarisOfflineTacticalHUD.cs":
                    block = src.split("private void RefreshSafeArea()", 1)[1].split(
                        "private void LoadMedicalSites()", 1)[0]
                else:
                    start = "private void " + method
                    block = src.split(start, 1)[1].split("private void ", 1)[0]
                self.assertIn(resolution_guard, block)
                self.assertIn("Time.unscaledTime < nextSafeAreaPollAt", block)
                self.assertIn("nextSafeAreaPollAt = Time.unscaledTime + 0.4f;", block)
                self.assertIn("Screen.safeArea", block)
                self.assertIn("safe.anchorMin" if file_name == "AarisOfflineTacticalHUD.cs"
                              else "safeRoot.anchorMin", block)
                self.assertLess(block.index("Time.unscaledTime < nextSafeAreaPollAt"),
                                block.index("Screen.safeArea"))
                self.assertLess(block.index("nextSafeAreaPollAt = Time.unscaledTime + 0.4f;"),
                                block.index("Screen.safeArea"))

    def test_owned_mesh_cleanup_runs_once_per_owned_mesh(self):
        editor = os.getenv("AARIS_UNITY_EDITOR_DATA")
        if editor:
            bin_dir = Path(editor) / "MonoBleedingEdge/bin"
            mcs, mono = str(bin_dir / "mcs"), str(bin_dir / "mono")
        else:
            mcs, mono = shutil.which("mcs"), shutil.which("mono")
        if not mcs or not mono or not Path(mcs).is_file() or not Path(mono).is_file():
            self.skipTest("Mono C# compiler unavailable; source tests still ran")
        fixture = r"""
using System;
using System.Reflection;
namespace UnityEngine {
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class DisallowMultipleComponent : Attribute { }
    public class Object {
        public static int DestroyCount;
        public static void Destroy(Object obj) { if (obj != null) DestroyCount++; }
    }
    public class MonoBehaviour : Object { }
    public sealed class Mesh : Object { }
}
public static class Driver {
    public static int Main() {
        var owner = new Aaris.MapReview.AarisOwnedMeshCleanup();
        owner.Track(new UnityEngine.Mesh());
        owner.Track(null);
        owner.Track(new UnityEngine.Mesh());
        var destroy = owner.GetType().GetMethod("OnDestroy",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (destroy == null) return 1;
        destroy.Invoke(owner, null);
        if (UnityEngine.Object.DestroyCount != 2) return 2;
        destroy.Invoke(owner, null);
        if (UnityEngine.Object.DestroyCount != 2) return 3;
        return 0;
    }
}
"""
        with tempfile.TemporaryDirectory(prefix="aaris-mesh-owned-") as tmp:
            source = Path(tmp) / "fixture.cs"
            binary = Path(tmp) / "test.exe"
            source.write_text(fixture)
            subprocess.run([mcs, "-out:" + str(binary),
                            str(SCRIPTS / "AarisOwnedMeshCleanup.cs"), str(source)],
                           check=True, timeout=45, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            subprocess.run([mono, str(binary)], check=True, timeout=45,
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE)


if __name__ == "__main__":
    unittest.main(verbosity=2)
