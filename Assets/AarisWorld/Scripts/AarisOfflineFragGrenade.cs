using System.Collections.Generic;
using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>Local offline grenade with deterministic fuse and line-of-sight explosion.</summary>
    [DisallowMultipleComponent]
    public sealed class AarisOfflineFragGrenade : MonoBehaviour
    {
        [SerializeField, Min(0.1f)] private float fuseSeconds = 3.0f;
        [SerializeField, Min(0.1f)] private float blastRadius = 5.0f;
        [SerializeField, Min(0f)] private float centerDamage = 135f;
        [SerializeField] private LayerMask damageMask = ~0;

        private static Material grenadeMaterial, oliveShell, oliveLight, darkMetal;
        private static bool grenadeFinishesLoaded;
        private static PhysicsMaterial bounceMaterial;
        private readonly Collider[] overlaps = new Collider[96];
        private readonly RaycastHit[] rays = new RaycastHit[48];
        private readonly Dictionary<IAarisDamageable, (float amount, Collider collider)> blastTargets =
            new Dictionary<IAarisDamageable, (float amount, Collider collider)>();
        private readonly List<MonoBehaviour> damageableScratch = new List<MonoBehaviour>(8);
        private Rigidbody body;
        private GameObject owner;
        private float detonateAt, lastBounceAudio = -10f;
        private bool armed, detonated;

        public static AarisOfflineFragGrenade Throw(Vector3 origin, Vector3 velocity, GameObject thrower)
        {
            var grenade = new GameObject("Aaris_FragGrenade");
            grenade.transform.position = origin;
            var sphere = grenade.AddComponent<SphereCollider>();
            sphere.radius = 0.13f;
            if (bounceMaterial == null)
                bounceMaterial = new PhysicsMaterial("AarisFrag_Bounce")
                {
                    dynamicFriction = 0.43f,
                    staticFriction = 0.62f,
                    bounciness = 0.33f,
                    bounceCombine = PhysicsMaterialCombine.Maximum
                };
            sphere.sharedMaterial = bounceMaterial;
            GameObject imported = Resources.Load<GameObject>("AarisCombat/FragGrenade");
            GameObject visual = imported != null
                ? Instantiate(imported, grenade.transform, false)
                : GameObject.CreatePrimitive(PrimitiveType.Sphere);
            visual.name = "LocalGrenadeModel";
            visual.transform.SetParent(grenade.transform, false);
            if (imported == null) visual.transform.localScale = Vector3.one * 0.25f;
            // Destroy is deferred until end-of-frame. Immediately disable imported
            // visual colliders so only the physics sphere can bounce on the first step.
            foreach (var collider in visual.GetComponentsInChildren<Collider>(true))
            {
                collider.enabled = false;
                Destroy(collider);
            }
            if (grenadeMaterial == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Standard");
                grenadeMaterial = new Material(shader);
                grenadeMaterial.color = new Color(0.16f, 0.25f, 0.17f);
            }
            // Preserve authored shell and metal finishes; do not flatten all slots.
            if (imported != null && !grenadeFinishesLoaded)
            {
                oliveShell = Resources.Load<Material>("AarisCombat/Materials/FragGrenade-OliveShell");
                oliveLight = Resources.Load<Material>("AarisCombat/Materials/FragGrenade-OliveLight");
                darkMetal = Resources.Load<Material>("AarisCombat/Materials/FragGrenade-DarkMetal");
                grenadeFinishesLoaded = true;
            }
            foreach (var renderer in visual.GetComponentsInChildren<Renderer>(true))
            {
                var slots = renderer.sharedMaterials;
                for (int i = 0; i < slots.Length; i++)
                    slots[i] = imported != null ? ResolveGrenadeFinish(slots[i]) : grenadeMaterial;
                renderer.sharedMaterials = slots;
            }
            var rigidbody = grenade.AddComponent<Rigidbody>();
            rigidbody.mass = 0.38f;
            rigidbody.linearDamping = 0.1f;
            rigidbody.angularDamping = 0.55f;
            rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
            var script = grenade.AddComponent<AarisOfflineFragGrenade>();
            script.Launch(rigidbody, velocity, thrower);
            return script;
        }

        private static bool IsUrpFinish(Material material) =>
            material != null && material.shader != null &&
            material.shader.name.StartsWith("Universal Render Pipeline/", System.StringComparison.Ordinal);

        private static Material ResolveGrenadeFinish(Material authored)
        {
            Material saved = null;
            if (authored != null)
            {
                switch (authored.name)
                {
                    case "OliveShell": case "FragGrenade-OliveShell": saved = oliveShell; break;
                    case "OliveLight": case "FragGrenade-OliveLight": saved = oliveLight; break;
                    case "DarkMetal": case "FragGrenade-DarkMetal": saved = darkMetal; break;
                }
            }
            if (IsUrpFinish(saved)) return saved;
            return IsUrpFinish(authored) ? authored : grenadeMaterial;
        }

        private void Launch(Rigidbody rigidbody, Vector3 velocity, GameObject thrower)
        {
            body = rigidbody;
            owner = thrower;
            detonateAt = Time.time + fuseSeconds;
            armed = true;
            body.linearVelocity = velocity;
            body.angularVelocity = new Vector3(4f, 6f, 2f);
            if (owner != null)
            {
                var playerCapsule = owner.GetComponent<CharacterController>();
                var grenadeCollider = GetComponent<Collider>();
                if (playerCapsule != null && grenadeCollider != null)
                    Physics.IgnoreCollision(grenadeCollider, playerCapsule, true);
            }
        }

        private void Update()
        {
            if (armed && !detonated && Time.time >= detonateAt) Detonate();
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!armed || detonated || Time.time - lastBounceAudio < 0.16f) return;
            lastBounceAudio = Time.time;
            AarisAudioDirector.GrenadeBounce(transform.position, collision.relativeVelocity.magnitude);
        }

        private void Detonate()
        {
            detonated = true;
            armed = false;
            AarisAudioDirector.GrenadeExplosion(transform.position);
            Vector3 blast = transform.position;
            int count = Physics.OverlapSphereNonAlloc(blast, blastRadius,
                overlaps, damageMask, QueryTriggerInteraction.Ignore);
            Collider[] candidates = overlaps;
            // A dense cluster of house, foliage or player colliders can fill the
            // unsorted NonAlloc array. Never drop the entire explosion or damage an
            // arbitrary subset: allocate a full query only on this rare overflow.
            if (count >= overlaps.Length)
            {
                candidates = Physics.OverlapSphere(blast, blastRadius,
                    damageMask, QueryTriggerInteraction.Ignore);
                count = candidates.Length;
            }
            blastTargets.Clear();
            for (int i = 0; i < count; i++)
            {
                var collider = candidates[i];
                if (collider == null || collider.transform.IsChildOf(transform)) continue;
                IAarisDamageable target = FindDamageable(collider);
                if (target == null) continue;
                var targetComponent = target as Component;
                if (targetComponent == null) continue;
                Vector3 near = collider.ClosestPoint(blast);
                Vector3 delta = near - blast;
                float distance = delta.magnitude;
                if (distance > blastRadius) continue;
                float t = Mathf.Clamp01(distance / blastRadius);
                float amount = centerDamage * (1f - t * t);
                if (amount <= 0.05f) continue;
                // Overlap order is not a gameplay rule. Use the strongest exposed
                // collider per damageable, not whichever limb was returned first.
                if (blastTargets.TryGetValue(target, out var previous) &&
                    (amount < previous.amount || (amount == previous.amount &&
                        collider.GetInstanceID() >= previous.collider.GetInstanceID()))) continue;
                if (!HasBlastLineOfSight(blast, near, targetComponent.transform)) continue;
                blastTargets[target] = (amount, collider);
            }
            // Snapshot exposure before applying any damage: destroying cover during
            // notification must not change who was exposed to this same explosion.
            foreach (var entry in blastTargets)
            {
                if ((entry.Key as Component) == null) continue;
                Collider collider = entry.Value.collider;
                var rb = collider != null ? collider.attachedRigidbody : null;
                entry.Key.ApplyDamage(entry.Value.amount, owner);
                if (rb != null && !rb.isKinematic)
                    rb.AddExplosionForce(340f, blast, blastRadius, 0.25f, ForceMode.Impulse);
            }
            // External prefab/VFX may replace this locally; combat remains independent.
            Destroy(gameObject);
        }

        private bool HasBlastLineOfSight(Vector3 origin, Vector3 destination, Transform target)
        {
            Vector3 delta = destination - origin;
            if (delta.sqrMagnitude < 0.0001f) return true;
            float length = delta.magnitude;
            // Damage candidates may be filtered by damageMask, but solid world cover
            // must still occlude the blast even when it is not damageable.
            Ray ray = new Ray(origin, delta / length);
            int hits = Physics.RaycastNonAlloc(ray, rays, length, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            RaycastHit[] candidates = rays;
            // Do not treat a full (unsorted) buffer as proof of cover. Retrieve
            // every hit before applying the same solid-cover exclusion rules.
            if (hits >= rays.Length)
            {
                candidates = Physics.RaycastAll(ray, length,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                hits = candidates.Length;
            }
            for (int i = 0; i < hits; i++)
            {
                RaycastHit hit = candidates[i];
                Collider c = hit.collider;
                if (c == null || c.transform.IsChildOf(transform) ||
                    c.transform.IsChildOf(target) || target.IsChildOf(c.transform))
                    continue;
                // Any solid obstruction before the victim blocks this explosion ray.
                if (hit.distance < length - 0.02f) return false;
            }
            return true;
        }

        private IAarisDamageable FindDamageable(Collider collider)
        {
            for (Transform t = collider.transform; t != null; t = t.parent)
            {
                // Detonation can inspect dozens of overlapping colliders. Reuse
                // one list instead of allocating a MonoBehaviour[] per hierarchy
                // level, keeping the rare explosion path GC-stable on mobile.
                damageableScratch.Clear();
                t.GetComponents(damageableScratch);
                for (int i = 0; i < damageableScratch.Count; i++)
                    if (damageableScratch[i] is IAarisDamageable damageable)
                        return damageable;
            }
            return null;
        }
    }
}
