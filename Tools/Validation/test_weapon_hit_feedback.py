#!/usr/bin/env python3
"""Source-level regression gates; native Unity and Android profiling remain mandatory."""
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
S = ROOT / "Assets" / "AarisWorld" / "Scripts"
GUN = (S / "AarisEquippedGun.cs").read_text()
HUD = (S / "AarisWeaponHUD.cs").read_text()
LOOT = (S / "AarisWeaponPickup.cs").read_text()

class WeaponFeedbackContract(unittest.TestCase):
    def test_only_damage_receiver_issues_hit_feedback(self):
        fn = GUN.split("internal static bool ApplyDamage(", 1)[1]
        self.assertIn("if (!(damageScratch[i] is IAarisDamageable damageable)) continue;", fn)
        self.assertIn("!health.IsDead", fn)
        self.assertIn("victim.ApplyHitDamage(damage, attacker, headshot);", fn)
        self.assertIn("damageable.ApplyDamage(damage, attacker);", fn)
        self.assertIn("canRegister = tracked.CurrentHealth < before;", fn)
        self.assertLess(fn.index("victim.ApplyHitDamage(damage, attacker, headshot);"),
                        fn.index("AarisWeaponHUD.NotifyHit(attacker, headshot);"))
        self.assertIn("!node.IsChildOf(attacker.transform)", fn)

    def test_confirmed_marker_matches_hp_loss(self):
        route = GUN.split("internal static bool ApplyDamage(Collider collider, float amount, GameObject attacker,", 1)[1]
        self.assertIn("canRegister = actualLoss > 0f;", route)
        self.assertLess(route.index("canRegister = actualLoss > 0f;"),
                        route.index("AarisWeaponHUD.NotifyHit(attacker, headshot);"))

    def test_hit_marker_prebuilt_and_nonblocking(self):
        build = HUD.split("private void CreateHitMarker(", 1)[1].split(
            "public static void NotifyHit", 1)[0]
        self.assertIn('new GameObject("Hit stroke"', build)
        self.assertIn("graphic.raycastTarget = false;", build)
        update = HUD.split("public static void NotifyHit(", 1)[1].split(
            "private void AddNearbyGunHint", 1)[0]
        for token in ("new ", "Instantiate(", "Destroy(", "FindObject", "GetComponent("):
            self.assertNotIn(token, update)
        self.assertIn("attacker != instance.inventory.gameObject", update)
        self.assertIn("now + (critical ? 0.21f : 0.13f)", update)
        self.assertIn("if (headshot) instance.criticalHitMarkerUntil", update)
        self.assertIn("now < instance.criticalHitMarkerUntil", update)
        self.assertIn("instance.hitMarkerStrokes.Length", update)
        self.assertIn("stroke.color != tint", update)
        self.assertIn("hitMarkerStrokes[i] = graphic;", build)
        self.assertIn("AarisWeaponHUD.NotifyHit(attacker, headshot);", GUN)

    def test_native_real_damage_headshot_to_marker_transition_coverage(self):
        qa = (ROOT / "Assets/AarisWorld/Editor/AarisProGunplayQA.cs").read_text()
        self.assertIn("Real headshot HP loss produces distinct critical HUD marker", qa)
        self.assertIn("Following shotgun body pellet cannot erase critical cue", qa)
        self.assertIn("Expired critical marker restores nonblocking body tint", qa)
        self.assertIn('Set(feedback, "criticalHitMarkerUntil"', qa)
        self.assertIn("strokes[i].raycastTarget = false;", qa)

    def test_pooled_tapered_muzzle_flash(self):
        build = GUN.split("private void EnsureMuzzleFlash()", 1)[1].split(
            "private void TriggerMuzzleFlash()", 1)[0]
        trigger = GUN.split("private void TriggerMuzzleFlash()", 1)[1].split(
            "private static Material CasingMaterial", 1)[0]
        self.assertIn("MuzzleFlameMesh()", build)
        self.assertIn("MeshFilter", build)
        self.assertIn("Vector3.one * 0.115f", build)
        self.assertNotIn("PrimitiveType.Quad", build)
        self.assertIn("sharedMuzzleFlameMesh", GUN)
        self.assertIn("mesh.colors32 = colors;", GUN)
        for token in ("new GameObject", "new Mesh", "Instantiate("):
            self.assertNotIn(token, trigger)

    def test_visibility_and_alpha_fade(self):
        self.assertIn('AarisShaderCache.Find("Universal Render Pipeline/Particles/Unlit")', LOOT)
        self.assertIn("shared.EnableKeyword(\"_SURFACE_TYPE_TRANSPARENT\");", LOOT)
        self.assertIn("RenderQueue.Transparent", LOOT)
        self.assertIn("beacon.enabled = false;", LOOT)
        self.assertIn("Mathf.Abs(index % 9) * 0.05f", LOOT)
        self.assertIn("rangeSq < 36f * 36f", LOOT)
        self.assertIn("rangeSq < 42f * 42f", LOOT)
        tick = LOOT.split("private void Update()", 1)[1].split("private void OnEnable()", 1)[0]
        self.assertNotIn("new Material(", tick)
        self.assertNotIn("Instantiate(", tick)

if __name__ == "__main__":
    unittest.main()
