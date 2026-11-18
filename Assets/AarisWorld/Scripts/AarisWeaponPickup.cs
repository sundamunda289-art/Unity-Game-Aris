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
        // Root is already lifted from the verified floor by the loot world.
        // Keep the source mesh only 2.5 cm higher to avoid Z-fighting without
        // making ground loot look like it is levitating above the terrain.
        private const float ModelFloorClearance = 0.025f;
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
        // Visible pickups still animate every frame. Once off-screen, native
        // Renderer.isVisible checks are throttled across the streamed loot set.
        private float nextModelVisibilityCheck;
        private bool modelWasVisible;
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
            // Floor loot and held weapons share the exact cached PBR finish,
            // so picking up the AR never changes its material appearance.
            AarisWeaponInventory.ApplyTextureAwareWeaponFinish(
                state.Spec, modelRenderers);

            // Measure actual mesh geometry in the pickup root's local space.
            // Unity Renderer.bounds can lag a transform/scale change until a
            // render update, producing giant or underground guns in APKs.
            // Eight transformed corners per renderer are calculated ONCE on
            // spawn, never in the streaming Update hot path.
            model.localPosition = Vector3.zero;
            // Keep floor presentation arbitrary, but preserve the same source
            // forward correction used by the held model before measuring bounds.
            model.localRotation =
                Quaternion.Euler(0f, 0f, -75f) *
                AarisWeaponInventory.ImportedModelRotation(state.Spec);
            if (!AarisWeaponMeshBounds.TryMeasure(transform,
                modelRenderers, out Bounds modelBounds))
            {
                Debug.LogError("Aaris: invalid local floor weapon mesh bounds: " +
                    state.Spec.id);
                Destroy(visible);
                modelRenderers = null;
                enabled = false;
                return false;
            }
            float length = Mathf.Max(modelBounds.size.x,
                modelBounds.size.y, modelBounds.size.z);
            if (length < 0.001f)
            {
                Destroy(visible);
                modelRenderers = null;
                enabled = false;
                return false;
            }
            float scale = Mathf.Clamp(state.Spec.presentationLengthMetres / length,
                0.0000001f, 10f);
            model.localScale *= scale;
            // Original root-space bounds scale linearly around the FBX pivot.
            // Correct horizontal centering and leave the lowest geometry
            // only 2.5 centimetres above the pickup root's verified floor clearance.
            model.localPosition = new Vector3(
                -modelBounds.center.x * scale,
                ModelFloorClearance - modelBounds.min.y * scale,
                -modelBounds.center.z * scale);
            var visualPivot = new GameObject("Loot centered rotation pivot");
            visualPivot.transform.SetParent(transform, false);
            visualPivot.transform.localPosition = new Vector3(0f,
                ModelFloorClearance + (modelBounds.center.y - modelBounds.min.y) * scale, 0f);
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
                else if (beacon.enabled)
                {
                    // When no active gameplay camera exists (loading,
                    // transitions), never leave 40 stray transparent lines
                    // drawing on mobile until a replacement camera appears.
                    beacon.enabled = false;
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
            // Hidden loot would otherwise query every Renderer at 60-120 Hz.
            // Poll off-screen meshes at most once every 0.10s; the original
            // pending-angle accumulator preserves exact spin on reappearance.
            // A visible mesh still updates on EVERY rendered gameplay frame.
            if (!modelWasVisible && Time.unscaledTime < nextModelVisibilityCheck)
                return;
            bool visibleNow = false;
            for (int i = 0; i < modelRenderers.Length; i++)
            {
                Renderer renderer = modelRenderers[i];
                if (renderer == null || !renderer.isVisible) continue;
                visibleNow = true;
                break;
            }
            modelWasVisible = visibleNow;
            if (!visibleNow)
            {
                nextModelVisibilityCheck = Time.unscaledTime + 0.10f;
                return;
            }
            model.Rotate(0f, pendingSpinDegrees, 0f, Space.World);
            pendingSpinDegrees = 0f;
        }

        private void OnEnable()
        {
            // A formerly invisible pickup must be checked on its first
            // re-enabled frame, not inherit an old hidden polling deadline.
            modelWasVisible = false;
            nextModelVisibilityCheck = 0f;
            // A streamed pickup may be temporarily disabled and reenabled.
            if (state != null && model != null && !consumed)
                activePickups.Add(this);
        }

        private void OnDisable()
        {
            activePickups.Remove(this);
            // Beacon material is shared and owned by the global cache.
        }
    }

    /// <summary>
    /// Exact imported weapon geometry bounds, independent of Renderer.bounds
    /// update timing after Transform scaling or animation graph evaluation.
    /// Called once per new floor pickup or hand equip; zero frame hotpath cost.
    /// </summary>
    internal static class AarisWeaponMeshBounds
    {
        internal static bool TryMeasure(Transform root, Renderer[] renderers,
            out Bounds bounds)
        {
            bounds = default(Bounds);
            bool found = false;
            if (root == null || renderers == null) return false;
            foreach (var renderer in renderers)
            {
                // An imported FBX can have valid MeshFilter bounds while every
                // drawing component is disabled/culled or carries zero vertices.
                // Do not accept invisible floor loot or hand-held equipment.
                // Called only on spawn/equip, never once per gameplay frame.
                if (renderer == null || !renderer.enabled ||
                    renderer.forceRenderingOff ||
                    !renderer.gameObject.activeInHierarchy) continue;
                Bounds meshBounds;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    if (skinned.sharedMesh == null ||
                        skinned.sharedMesh.vertexCount <= 0) continue;
                    meshBounds = skinned.localBounds;
                }
                else if (renderer is MeshRenderer)
                {
                    var meshFilter = renderer.GetComponent<MeshFilter>();
                    if (meshFilter == null || meshFilter.sharedMesh == null ||
                        meshFilter.sharedMesh.vertexCount <= 0)
                        continue;
                    meshBounds = meshFilter.sharedMesh.bounds;
                }
                else continue;
                Vector3 c = meshBounds.center, e = meshBounds.extents;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 local = c + new Vector3(
                        (corner & 1) == 0 ? -e.x : e.x,
                        (corner & 2) == 0 ? -e.y : e.y,
                        (corner & 4) == 0 ? -e.z : e.z);
                    Vector3 point = root.InverseTransformPoint(
                        renderer.transform.TransformPoint(local));
                    if (!found)
                    {
                        bounds = new Bounds(point, Vector3.zero);
                        found = true;
                    }
                    else bounds.Encapsulate(point);
                }
            }
            return found && bounds.size.sqrMagnitude > 0.000001f;
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
