#!/usr/bin/env python3
"""Android-only grenade root pooling guard: source-level, not Play Mode proof."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
FRAG = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineFragGrenade.cs").read_text()
HUD = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineTacticalHUD.cs").read_text()


class AndroidFragVisualPool(unittest.TestCase):
    def test_bounded_pool_and_old_desktop_path(self):
        self.assertIn("private const int MaxPooledGrenades = 6;", FRAG)
        self.assertIn("new Stack<AarisOfflineFragGrenade>(MaxPooledGrenades)", FRAG)
        self.assertIn("RuntimeInitializeLoadType.SubsystemRegistration", FRAG)
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", FRAG)
        throw = FRAG.split("public static AarisOfflineFragGrenade Throw(", 1)[1].split(
            "private static bool IsUrpFinish(", 1
        )[0]
        self.assertLess(throw.index("while (idleGrenades.Count > 0)"),
                        throw.index('new GameObject("Aaris_FragGrenade")'))
        pool_branch = throw.split("while (idleGrenades.Count > 0)", 1)[1].split("#endif", 1)[0]
        self.assertIn("reused.gameObject.SetActive(true);", pool_branch)
        self.assertIn("reused.Launch(reused.body, velocity, thrower);", pool_branch)
        self.assertNotIn("new GameObject(", pool_branch)
        self.assertNotIn("Instantiate(", pool_branch)
        self.assertIn('new GameObject("Aaris_FragGrenade")', throw)

    def test_reused_physics_and_collision_owner_are_reset(self):
        launch = FRAG.split("private void Launch(", 1)[1].split(
            "private void Update()", 1
        )[0]
        for check in (
            "detonateAt = Time.time + fuseSeconds;",
            "lastBounceAudio = -10f;",
            "detonated = false;",
            "armed = true;",
            "body.linearVelocity = velocity;",
            "body.angularVelocity = new Vector3(4f, 6f, 2f);",
            "Physics.IgnoreCollision(grenadeCollider, playerCapsule, true);",
        ):
            self.assertIn(check, launch)
        detonate = FRAG.split("private void Detonate()", 1)[1].split(
            "private bool HasBlastLineOfSight(", 1
        )[0]
        self.assertIn("idleGrenades.Count < MaxPooledGrenades", detonate)
        self.assertIn("body.linearVelocity = Vector3.zero;", detonate)
        self.assertIn("body.angularVelocity = Vector3.zero;", detonate)
        self.assertIn("body.isKinematic = true;", detonate)
        self.assertIn("gameObject.SetActive(false);", detonate)
        self.assertIn("idleGrenades.Push(this);", detonate)
        self.assertIn("Destroy(gameObject);", detonate)
        self.assertLess(detonate.index("blastTargets.Clear();", detonate.index("ApplyDamage(")),
                        detonate.index("idleGrenades.Push(this);"))
        self.assertIn("damageableScratch.Clear();", detonate)
        self.assertIn("owner = null;", detonate)

    def test_async_asset_preload_keeps_original_look(self):
        prewarm = FRAG.split("internal static IEnumerator PrewarmGrenadeAssets()", 1)[1].split(
            "#endif", 1
        )[0]
        self.assertIn("WaitForSecondsRealtime(2f)", prewarm)
        self.assertIn("Resources.LoadAsync<GameObject>", prewarm)
        self.assertIn("Resources.LoadAsync<Material>", prewarm)
        self.assertIn("yield return model;", prewarm)
        self.assertIn("yield return shell;", prewarm)
        self.assertIn("yield return light;", prewarm)
        self.assertIn("yield return metal;", prewarm)
        self.assertLess(prewarm.index("yield return shell;"),
                        prewarm.index("Resources.LoadAsync<Material>(\n                \"AarisCombat/Materials/FragGrenade-OliveLight\")"))
        self.assertIn("model.priority = -1;", prewarm)
        self.assertIn("metal.priority = -1;", prewarm)
        self.assertIn("StartCoroutine(AarisOfflineFragGrenade.PrewarmGrenadeAssets());", HUD)
        self.assertIn("GameObject imported = cachedGrenadePrefab;", FRAG)
        self.assertIn('Resources.Load<GameObject>("AarisCombat/FragGrenade")', FRAG)
        self.assertIn("ResolveGrenadeFinish(slots[i])", FRAG)

    def test_explosion_authority_and_geometry_unchanged(self):
        for clause in (
            "SphereCollider>();",
            "sphere.radius = 0.13f;",
            "rigidbody.mass = 0.38f;",
            "rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;",
            "Physics.OverlapSphereNonAlloc(blast, blastRadius,",
            "Physics.RaycastNonAlloc(ray, rays, length,",
            "entry.Key.ApplyDamage(entry.Value.amount, owner);",
            "rb.AddExplosionForce(340f, blast, blastRadius, 0.25f, ForceMode.Impulse);",
            "AarisAudioDirector.GrenadeExplosion(transform.position);",
        ):
            self.assertIn(clause, FRAG)


if __name__ == "__main__":
    unittest.main()
