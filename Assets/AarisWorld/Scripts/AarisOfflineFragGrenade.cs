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

        private static Material grenadeMaterial;
        private static PhysicsMaterial bounceMaterial;
        private readonly Collider[] overlaps = new Collider[96];
        private readonly RaycastHit[] rays = new RaycastHit[48];
        private readonly HashSet<IAarisDamageable> damaged = new HashSet<IAarisDamageable>();
        private Rigidbody body;
        private GameObject owner;
        private float detonateAt;
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
            // Destroy is deferred to frame end. Disable imported/model
            // colliders immediately so one throw cannot briefly collide
            // with its own visual or create an extra blast obstacle.
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
            foreach (var renderer in visual.GetComponentsInChildren<Renderer>(true))
            {
                var slots = renderer.sharedMaterials;
                for (int i = 0; i < slots.Length; i++) slots[i] = grenadeMaterial;
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

        private void Detonate()
        {
            detonated = true;
            armed = false;
            int count = Physics.OverlapSphereNonAlloc(transform.position, blastRadius,
                overlaps, damageMask, QueryTriggerInteraction.Ignore);
            if (count >= overlaps.Length)
            {
                Debug.LogWarning("Aaris frag: target buffer saturated; explosion damage safely cancelled.");
                Destroy(gameObject);
                return;
            }
            damaged.Clear();
            Vector3 blast = transform.position;
            for (int i = 0; i < count; i++)
            {
                var collider = overlaps[i];
                if (collider == null || collider.transform.IsChildOf(transform)) continue;
                IAarisDamageable target = FindDamageable(collider);
                if (target == null || damaged.Contains(target)) continue;
                var targetComponent = target as Component;
                if (targetComponent == null) continue;
                Vector3 near = collider.ClosestPoint(blast);
                Vector3 delta = near - blast;
                float distance = delta.magnitude;
                if (distance > blastRadius || !HasBlastLineOfSight(blast, near, targetComponent.transform))
                    continue;
                // At the rim, damage drops to zero. Direct hits carry a minimum fraction.
                float t = Mathf.Clamp01(distance / blastRadius);
                float amount = centerDamage * (1f - t * t);
                if (amount <= 0.05f) continue;
                damaged.Add(target);
                target.ApplyDamage(amount, owner);
                var rb = collider.attachedRigidbody;
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
            int hits = Physics.RaycastNonAlloc(new Ray(origin, delta / length), rays,
                length, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (hits >= rays.Length) return false;
            for (int i = 0; i < hits; i++)
            {
                Collider c = rays[i].collider;
                if (c == null || c.transform.IsChildOf(transform) ||
                    c.transform.IsChildOf(target) || target.IsChildOf(c.transform))
                    continue;
                // Any solid obstruction before the victim blocks this explosion ray.
                if (rays[i].distance < length - 0.02f) return false;
            }
            return true;
        }

        private static IAarisDamageable FindDamageable(Collider collider)
        {
            for (Transform t = collider.transform; t != null; t = t.parent)
            {
                var components = t.GetComponents<MonoBehaviour>();
                for (int i = 0; i < components.Length; i++)
                    if (components[i] is IAarisDamageable damageable)
                        return damageable;
            }
            return null;
        }
    }
}
