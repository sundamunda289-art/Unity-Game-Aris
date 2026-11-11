using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// Passive match loot. It does not block the player, and the source FBX
    /// lives permanently in Assets/Resources/AarisWeapons/Models.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AarisWeaponPickup : MonoBehaviour
    {
        private AarisWeaponState state;
        private int candidateIndex;
        private Transform model;
        private Renderer[] modelRenderers;
        private float pendingSpinDegrees;
        private LineRenderer beacon;
        private bool reserved, consumed;

        public AarisWeaponSpec Spec => state != null ? state.Spec : null;
        public AarisWeaponState State => state;
        public bool IsConsumed => consumed;
        public bool TryReserve()
        {
            if (reserved || consumed || state == null) return false;
            reserved = true;
            return true;
        }
        public void ReleaseReservation()
        {
            if (!consumed) reserved = false;
        }
        public void CommitConsumed()
        {
            consumed = true;
            reserved = false;
        }
        // Non-negative = baked match node; negative = a player-dropped gun.
        public int CandidateIndex => candidateIndex;

        public bool Initialize(AarisWeaponState gunState, int index)
        {
            if (gunState == null || gunState.Spec == null ||
                !gunState.Spec.Valid) return false;
            state = gunState; // Immutable readonly snapshot, no mutable ammo alias.
            candidateIndex = index;
            reserved = consumed = false;
            GameObject prefab = AarisWeaponCatalog.ModelFor(state.Spec);
            if (prefab == null)
            {
                Debug.LogError("Missing local weapon FBX: " + state.Spec.modelResource);
                enabled = false;
                return false;
            }
            var visible = Instantiate(prefab, transform);
            if (visible.GetComponentInChildren<Renderer>(true) == null)
            {
                Destroy(visible);
                enabled = false;
                return false;
            }
            model = visible.transform;
            // Cache all mesh renderers once; the loot rotates only when at
            // least one of them is visible in a camera view.
            modelRenderers = visible.GetComponentsInChildren<Renderer>(true);
            model.name = "Local " + state.Spec.displayName + " Model";
            // Loot never interferes with player or map physics.
            foreach (var collider in visible.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;

            // Fit different artist FBX units to one gameplay pickup size.
            var renderer = visible.GetComponentInChildren<Renderer>();
            if (renderer != null && renderer.bounds.size.magnitude > 0.001f)
            {
                float length = Mathf.Max(renderer.bounds.size.x,
                    renderer.bounds.size.y, renderer.bounds.size.z);
                float scale = Mathf.Clamp(0.82f / length, 0.001f, 10f);
                model.localScale *= scale;
            }
            model.localPosition = new Vector3(0f, 0.15f, 0f);
            model.localRotation = Quaternion.Euler(0f, 0f, -75f);

            // Small nonphysical highlight for dark grass; no bright 50m
            // spotlight and no additional real-time mobile lighting.
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader != null)
            {
                var lineObject = new GameObject("Loot subtle beacon");
                lineObject.transform.SetParent(transform, false);
                beacon = lineObject.AddComponent<LineRenderer>();
                beacon.useWorldSpace = false;
                beacon.sharedMaterial = AarisLootBeaconMaterial.Get(shader);
                beacon.positionCount = 2;
                beacon.SetPosition(0, new Vector3(0f, 0.12f, 0f));
                beacon.SetPosition(1, new Vector3(0f, 1.65f, 0f));
                beacon.startWidth = 0.035f;
                beacon.endWidth = 0.004f;
                beacon.startColor = new Color(0.35f, 0.9f, 1f, 0.5f);
                beacon.endColor = new Color(0.35f, 0.9f, 1f, 0.04f);
                beacon.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                beacon.receiveShadows = false;
            }
            return true;
        }

        private void Update()
        {
            if (model == null || Time.deltaTime <= 0f) return;
            // Off-screen pickups must not dirty Transform hierarchies every
            // Android frame. Keep elapsed rotation so the mesh resumes at
            // exactly the same orientation when it becomes visible again.
            pendingSpinDegrees += 19f * Time.deltaTime;
            if (pendingSpinDegrees >= 360f)
                pendingSpinDegrees = Mathf.Repeat(pendingSpinDegrees, 360f);
            if (modelRenderers == null) return;
            for (int i = 0; i < modelRenderers.Length; i++)
            {
                Renderer renderer = modelRenderers[i];
                if (renderer == null || !renderer.isVisible) continue;
                model.Rotate(0f, pendingSpinDegrees, 0f, Space.World);
                pendingSpinDegrees = 0f;
                break;
            }
        }

        private void OnDisable()
        {
            // Beacon material is shared and owned by the global cache.
        }
    }

    internal static class AarisLootBeaconMaterial
    {
        private static Material shared;
        internal static Material Get(Shader shader)
        {
            if (shared == null)
            {
                shared = new Material(shader) { name = "Aaris Loot Beacon Shared" };
                if (shared.HasProperty("_BaseColor"))
                    shared.SetColor("_BaseColor", Color.white);
            }
            return shared;
        }
    }
}
