using System.Collections.Generic;
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
        // One bounded registration per successfully initialized pickup. The HUD can
        // discover nearby visible guns without any scene-wide Find call in Update.
        private static readonly HashSet<AarisWeaponPickup> activePickups =
            new HashSet<AarisWeaponPickup>();
        internal static IEnumerable<AarisWeaponPickup> ActivePickups => activePickups;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ClearPickupRegistry() => activePickups.Clear();

        private AarisWeaponState state;
        private int candidateIndex;
        private Transform model;
        private Renderer[] modelRenderers;
        private float pendingSpinDegrees;
        private LineRenderer beacon;
        private Camera beaconObserver;
        private float nextBeaconVisibilityCheck;
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
            // One hierarchy traversal per streamed pickup: keep its renderers
            // for sizing, centering and visibility-gated spin. On low-memory
            // phones a separate GetComponentInChildren scan was unnecessary.
            modelRenderers = visible.GetComponentsInChildren<Renderer>(true);
            if (modelRenderers.Length == 0)
            {
                Destroy(visible);
                modelRenderers = null;
                enabled = false;
                return false;
            }
            model = visible.transform;
            model.name = "Local " + state.Spec.displayName + " Model";
            // Loot never interferes with player or map physics.
            foreach (var collider in visible.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;

            // Use the renderer array already cached above; requesting the
            // entire FBX hierarchy a second time allocated an extra array
            // on every streamed pickup, especially expensive during bursts
            // on a 2 GB Android phone. Bounds still include every mesh.
            Bounds modelBounds = modelRenderers[0].bounds;
            for (int i = 1; i < modelRenderers.Length; i++)
                modelBounds.Encapsulate(modelRenderers[i].bounds);
            float length = Mathf.Max(modelBounds.size.x,
                modelBounds.size.y, modelBounds.size.z);
            if (length > 0.001f)
                model.localScale *= Mathf.Clamp(0.82f / length, 0.001f, 10f);

            model.localPosition = Vector3.zero;
            model.localRotation = Quaternion.Euler(0f, 0f, -75f);
            // FBX pivots need not sit inside the mesh. Recompute bounds after
            // rotation/scaling and keep the complete pickup above the floor.
            modelBounds = modelRenderers[0].bounds;
            for (int i = 1; i < modelRenderers.Length; i++)
                modelBounds.Encapsulate(modelRenderers[i].bounds);
            Vector3 delta = new Vector3(
                transform.position.x - modelBounds.center.x,
                transform.position.y + 0.08f - modelBounds.min.y,
                transform.position.z - modelBounds.center.z);
            model.position += delta;
            // World-space spin must pivot around the real visible gun center,
            // not the arbitrary FBX import origin (which can be far away).
            // One lightweight Transform per pickup, never a per-frame Bounds scan.
            var visualPivot = new GameObject("Loot centered rotation pivot");
            visualPivot.transform.SetParent(transform, false);
            visualPivot.transform.position = modelBounds.center + delta;
            model.SetParent(visualPivot.transform, true);
            model = visualPivot.transform;

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
            // Share the main camera reference; never search the scene every frame.
            beaconObserver = Camera.main;
            activePickups.Add(this);
            return true;
        }

        private void Update()
        {
            // The subtle beam matters only around reachable loot. Far beams
            // consume a transparent draw call per pickup on tile-based mobile GPUs.
            // Hysteresis and a 0.45s sampling interval prevent toggle thrash while
            // leaving the actual gun models, collision, persistence and pickup
            // range completely unchanged. No new materials or scene objects.
            if (beacon != null && Time.unscaledTime >= nextBeaconVisibilityCheck)
            {
                nextBeaconVisibilityCheck = Time.unscaledTime + 0.45f;
                if (beaconObserver == null || !beaconObserver.isActiveAndEnabled)
                    beaconObserver = Camera.main;
                if (beaconObserver != null && beaconObserver.isActiveAndEnabled)
                {
                    float rangeSq = (beaconObserver.transform.position -
                        transform.position).sqrMagnitude;
                    bool show = beacon.enabled ? rangeSq < 36f * 36f :
                        rangeSq < 31f * 31f;
                    if (beacon.enabled != show) beacon.enabled = show;
                }
            }
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
            activePickups.Remove(this);
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
