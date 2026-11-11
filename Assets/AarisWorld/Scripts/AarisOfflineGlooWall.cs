using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>One-player offline deployable cover; uses the existing weapon IAarisDamageable pipeline.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(AarisThirdPersonPlayer))]
    public sealed class AarisOfflineGlooWall : MonoBehaviour
    {
        [Header("Offline cover balance")]
        [SerializeField, Range(1, 15)] private int carriedWalls = 5;
        [SerializeField, Min(1f)] private float minimumDistance = 2.3f;
        [SerializeField, Min(2f)] private float maximumDistance = 10f;
        [SerializeField, Min(1f)] private float wallHealth = 600f;
        [SerializeField, Min(1f)] private float wallLifetime = 50f;
        [SerializeField, Min(0f)] private float deploymentCooldown = 0.6f;
        [SerializeField] private LayerMask groundAndCoverMask = ~0;

        private const float MinimumSupportNormal = 0.766f;
        private const float MaximumSupportStep = 0.45f;
        private const float SupportProbeHeight = 2.5f;

        private readonly RaycastHit[] forwardHits = new RaycastHit[32];
        private readonly RaycastHit[] groundHits = new RaycastHit[32];
        private readonly Collider[] overlaps = new Collider[48];
        private AarisThirdPersonPlayer motor;
        private AarisOfflineDrop drop;
        private AarisVehicleSession vehicle;
        private float nextOptionalComponentRefresh;
        private GameObject ghost;
        private Renderer[] ghostRenderers;
        private Vector3 proposedPoint;
        private Quaternion proposedRotation;
        private bool aiming, placementValid;
        private float nextDeployment;

        public int Count => carriedWalls;
        public bool Aiming => aiming;
        public bool CanUse
        {
            get
            {
                if (!isActiveAndEnabled || carriedWalls <= 0) return false;
                RefreshOptionalComponents();
                return (drop == null || drop.HasLanded) &&
                    (vehicle == null || !vehicle.IsDriving);
            }
        }

        // CanUse is polled by mobile HUD and held placement. Avoid repeated
        // native GetComponent calls for optional scripts on stripped prefabs.
        // Force a fresh check immediately before committing a deployment.
        private void RefreshOptionalComponents(bool force = false)
        {
            if (!force && Time.unscaledTime < nextOptionalComponentRefresh) return;
            nextOptionalComponentRefresh = Time.unscaledTime + 1f;
            if (drop == null) drop = GetComponent<AarisOfflineDrop>();
            if (vehicle == null) vehicle = GetComponent<AarisVehicleSession>();
        }

        private void Awake()
        {
            motor = GetComponent<AarisThirdPersonPlayer>();
            drop = GetComponent<AarisOfflineDrop>();
            vehicle = GetComponent<AarisVehicleSession>();
        }

        private void Start()
        {
            ghost = AarisGlooWallVisuals.Create("GlooWall_Ghost_Local", true);
            ghostRenderers = ghost.GetComponentsInChildren<Renderer>();
            ghost.SetActive(false);
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.B)) BeginAim();
            if (Input.GetKeyUp(KeyCode.B)) ReleaseAndDeploy();
            if (!aiming) return;
            if (!CanUse) { CancelAim(); return; }
            UpdatePlacement();
        }

        public void AddWalls(int count) => carriedWalls = Mathf.Clamp(carriedWalls + Mathf.Max(0, count), 0, 15);

        public void BeginAim()
        {
            if (!CanUse || Time.time < nextDeployment) return;
            aiming = true;
            placementValid = false;
            UpdatePlacement();
        }

        public void CancelAim()
        {
            aiming = false;
            placementValid = false;
            if (ghost != null) ghost.SetActive(false);
        }

        public void ReleaseAndDeploy()
        {
            if (!aiming) return;
            RefreshOptionalComponents(true); // Newly attached vehicle/drop state must veto deployment.
            // Do not trust the prior frame's preview: player and vehicles can move.
            UpdatePlacement();
            bool allowed = CanUse && placementValid && Time.time >= nextDeployment;
            Vector3 point = proposedPoint;
            Quaternion rotation = proposedRotation;
            CancelAim();
            if (!allowed) return;
            var root = AarisGlooWallVisuals.Create("GlooWall_Deployed", false);
            root.transform.SetPositionAndRotation(point, rotation);
            var cover = root.AddComponent<AarisGlooWallHealth>();
            cover.Initialize(wallHealth, wallLifetime);
            carriedWalls--;
            nextDeployment = Time.time + deploymentCooldown;
            AarisAudioDirector.GlooDeployed(point);
        }

        private void UpdatePlacement()
        {
            placementValid = false;
            if (motor == null || motor.AimCamera == null)
            {
                if (ghost != null) ghost.SetActive(false);
                return;
            }

            Vector3 look = motor.AimCamera.transform.forward;
            Vector3 ahead = Vector3.ProjectOnPlane(look, Vector3.up).normalized;
            if (ahead.sqrMagnitude < 0.5f) ahead = transform.forward;
            Vector3 player = transform.position;
            float desired = Mathf.Lerp(minimumDistance, Mathf.Max(maximumDistance, minimumDistance),
                Mathf.InverseLerp(-0.75f, 0.4f, look.y));
            // Favor the first walkable surface under the aiming reticle.
            // The old fixed-distance approximation made the wall appear
            // disconnected from where the player actually pointed.
            float nearestGround = float.PositiveInfinity;
            int cameraHits = Physics.RaycastNonAlloc(
                motor.AimCamera.transform.position, look, forwardHits,
                maximumDistance + 8f, groundAndCoverMask, QueryTriggerInteraction.Ignore);
            if (cameraHits < forwardHits.Length)
                for (int i = 0; i < cameraHits; i++)
                {
                    RaycastHit hit = forwardHits[i];
                    if (hit.collider == null ||
                        hit.collider.transform.IsChildOf(transform) ||
                        hit.collider.GetComponentInParent<AarisGlooWallHealth>() != null ||
                        hit.normal.y < MinimumSupportNormal) continue;
                    Vector3 flat = hit.point - player;
                    flat.y = 0f;
                    float targetDistance = flat.magnitude;
                    if (targetDistance >= minimumDistance * 0.7f &&
                        targetDistance <= maximumDistance + 1f &&
                        hit.distance < nearestGround)
                    {
                        nearestGround = hit.distance;
                        desired = Mathf.Clamp(targetDistance, minimumDistance, maximumDistance);
                        ahead = flat.normalized;
                    }
                }
            float distance = FindOpenDistance(player, ahead, desired);
            // The curved, highlighted face must look back toward its owner.
            proposedRotation = Quaternion.LookRotation(-ahead, Vector3.up);
            proposedPoint = player + ahead * Mathf.Max(minimumDistance, distance);

            if (TryFindSupport(proposedPoint, player.y, out float centerY))
            {
                proposedPoint.y = centerY;
                Vector3 edge = proposedRotation * Vector3.right *
                    (AarisGlooWallVisuals.CoverSize.x * 0.45f);
                if (distance >= minimumDistance &&
                    TryFindSupport(proposedPoint - edge, player.y, out float leftY) &&
                    TryFindSupport(proposedPoint + edge, player.y, out float rightY))
                {
                    float bottom = Mathf.Min(centerY, Mathf.Min(leftY, rightY));
                    float top = Mathf.Max(centerY, Mathf.Max(leftY, rightY));
                    if (top - bottom <= MaximumSupportStep)
                    {
                        proposedPoint.y = bottom;
                        placementValid = HasCoverClearance(top);
                    }
                }
            }

            if (ghost == null) return;
            ghost.transform.SetPositionAndRotation(proposedPoint, proposedRotation);
            AarisGlooWallVisuals.Tint(ghostRenderers, placementValid
                ? new Color(0.20f, 0.78f, 0.98f, 0.40f)
                : new Color(1f, 0.2f, 0.2f, 0.45f));
            if (!ghost.activeSelf) ghost.SetActive(true);
        }

        private float FindOpenDistance(Vector3 player, Vector3 ahead, float desired)
        {
            int count = Physics.RaycastNonAlloc(player + Vector3.up * 1.1f, ahead, forwardHits,
                desired, groundAndCoverMask, QueryTriggerInteraction.Ignore);
            if (count >= forwardHits.Length) return 0f;
            float nearest = desired;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = forwardHits[i];
                if (hit.collider == null || hit.collider.transform.IsChildOf(transform) ||
                    hit.collider is TerrainCollider || hit.normal.y >= MinimumSupportNormal) continue;
                nearest = Mathf.Min(nearest, hit.distance - 0.65f);
            }
            return nearest;
        }

        private bool TryFindSupport(Vector3 point, float playerY, out float height)
        {
            point.y = playerY + SupportProbeHeight;
            int count = Physics.RaycastNonAlloc(point, Vector3.down, groundHits,
                2f * SupportProbeHeight, groundAndCoverMask, QueryTriggerInteraction.Ignore);
            height = float.NegativeInfinity;
            if (count >= groundHits.Length) return false;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = groundHits[i];
                if (hit.collider == null || hit.collider.transform.IsChildOf(transform) ||
                    hit.normal.y < MinimumSupportNormal || hit.collider.attachedRigidbody != null ||
                    hit.collider.GetComponentInParent<AarisGlooWallHealth>() != null ||
                    hit.collider.GetComponentInParent<CharacterController>() != null) continue;
                height = Mathf.Max(height, hit.point.y);
            }
            return height >= -37.7f;
        }

        private bool HasCoverClearance(float supportTop)
        {
            // Exclude the shallow contact band occupied by the sampled floor.
            // The deployed box still reaches the lowest supported ground point.
            float queryBottom = supportTop + 0.075f;
            float queryTop = proposedPoint.y + AarisGlooWallVisuals.CoverSize.y + 0.04f;
            Vector3 center = proposedPoint + proposedRotation * AarisGlooWallVisuals.CoverCenter;
            center.y = (queryBottom + queryTop) * 0.5f;
            Vector3 halfSize = AarisGlooWallVisuals.CoverSize * 0.5f;
            halfSize += new Vector3(0.04f, 0f, 0.04f);
            halfSize.y = (queryTop - queryBottom) * 0.5f;
            int count = Physics.OverlapBoxNonAlloc(center, halfSize, overlaps, proposedRotation,
                groundAndCoverMask, QueryTriggerInteraction.Ignore);
            if (count >= overlaps.Length) return false;
            for (int i = 0; i < count; i++)
            {
                Collider other = overlaps[i];
                if (other != null && !other.transform.IsChildOf(transform) &&
                    !(other is TerrainCollider)) return false;
            }
            return true;
        }

        private void OnApplicationPause(bool paused) { if (paused) CancelAim(); }
        private void OnApplicationFocus(bool focused) { if (!focused) CancelAim(); }
        private void OnDisable() => CancelAim();
        private void OnDestroy() { if (ghost != null) Destroy(ghost); }
    }

    [DisallowMultipleComponent]
    public sealed class AarisGlooWallHealth : MonoBehaviour, IAarisDamageable
    {
        private float health, maximum, expiry;
        private Transform visual;
        private Renderer[] renderers;
        private float reveal;
        private bool initialized;

        public float CurrentHealth => health;

        public void Initialize(float hp, float lifetime)
        {
            maximum = Mathf.Max(1f, hp);
            health = maximum;
            expiry = Time.time + Mathf.Max(0.2f, lifetime);
            visual = transform.Find("VisualRoot");
            renderers = GetComponentsInChildren<Renderer>();
            if (visual != null) visual.localScale = new Vector3(1f, 0.025f, 1f);
            initialized = true;
        }

        private void Update()
        {
            if (!initialized) return;
            if (Time.time >= expiry) { Destroy(gameObject); return; }
            if (reveal < 1f)
            {
                reveal = Mathf.Min(1f, reveal + Time.deltaTime / 0.12f);
                if (visual != null)
                    visual.localScale = new Vector3(1f, Mathf.SmoothStep(0.025f, 1f, reveal), 1f);
            }
        }

        public void ApplyDamage(float amount, GameObject attacker)
        {
            if (!initialized || float.IsNaN(amount) || float.IsInfinity(amount) || amount <= 0f) return;
            health = Mathf.Max(0f, health - amount);
            // Preserve the authored ice and frost submaterials even after damage.
            // Damage shifts each existing layer independently toward cracked ice.
            AarisGlooWallVisuals.TintDamage(renderers, health / maximum);
            if (health <= 0f) Destroy(gameObject);
        }
    }

    internal static class AarisGlooWallVisuals
    {
        internal static readonly Vector3 CoverCenter = new Vector3(0f, 1.15f, -0.13f);
        internal static readonly Vector3 CoverSize = new Vector3(3.78f, 2.30f, 0.84f);

        private static Material solid, preview;
        private static readonly MaterialPropertyBlock block = new MaterialPropertyBlock();

        private static Material Material(bool ghost)
        {
            if (ghost && preview != null) return preview;
            if (!ghost && solid != null) return solid;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Hidden/InternalErrorShader");
            if (shader == null)
                throw new System.InvalidOperationException("AARIS_OFFLINE_URP_SHADER_UNAVAILABLE");
            var mat = new Material(shader) { name = ghost ? "GlooPreviewLocal" : "GlooCoverLocal" };
            mat.SetColor(mat.HasProperty("_BaseColor") ? "_BaseColor" : "_Color",
                new Color(0.13f, 0.60f, 0.92f, ghost ? 0.4f : 1f));
            if (ghost)
            {
                if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
                if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            if (ghost) preview = mat; else solid = mat;
            return mat;
        }

        public static GameObject Create(string name, bool ghost)
        {
            var root = new GameObject(name);
            var visual = new GameObject("VisualRoot");
            visual.transform.SetParent(root.transform, false);
            Material material = Material(ghost);
            var authoredModel = Resources.Load<GameObject>("AarisCombat/GlooWall");
            if (authoredModel != null)
            {
                var art = Object.Instantiate(authoredModel, visual.transform, false);
                art.name = "LocalCurvedIceBarrier";
                foreach (var c in art.GetComponentsInChildren<Collider>(true))
                {
                    c.enabled = false;
                    Object.Destroy(c);
                }
                // The OBJ contains ArcticBlue and FrostHighlight submeshes.
                // Using one opaque material erased its authored ice contrast.
                var ice = Resources.Load<Material>("AarisCombat/Materials/GlooWall-ArcticBlue");
                var frost = Resources.Load<Material>("AarisCombat/Materials/GlooWall-FrostHighlight");
                foreach (var renderer in art.GetComponentsInChildren<Renderer>(true))
                {
                    var slots = renderer.sharedMaterials;
                    for (int i = 0; i < slots.Length; i++)
                    {
                        if (ghost) { slots[i] = material; continue; }
                        string imported = slots[i] != null ? slots[i].name : string.Empty;
                        bool highlight = imported.IndexOf("Frost", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || (slots.Length > 1 && i > 0);
                        slots[i] = highlight ? (frost != null ? frost : material)
                            : (ice != null ? ice : material);
                    }
                    renderer.sharedMaterials = slots;
                }
            }
            else
            {
                Segment(visual.transform, "Center", Vector3.zero, Quaternion.identity,
                    new Vector3(2.55f, 2.30f, 0.37f), material);
                Segment(visual.transform, "LeftCurve", new Vector3(-1.46f, 0f, -0.25f),
                    Quaternion.Euler(0f, 24f, 0f), new Vector3(1.26f, 2.24f, 0.36f), material);
                Segment(visual.transform, "RightCurve", new Vector3(1.46f, 0f, -0.25f),
                    Quaternion.Euler(0f, -24f, 0f), new Vector3(1.26f, 2.24f, 0.36f), material);
            }
            if (!ghost)
            {
                // Unlike the visual, this collider stays FULL HEIGHT from the first frame.
                var col = root.AddComponent<BoxCollider>();
                col.center = CoverCenter;
                col.size = CoverSize;
            }
            return root;
        }

        private static void Segment(Transform parent, string name, Vector3 local,
            Quaternion rotation, Vector3 dimensions, Material material)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.SetParent(parent, false);
            cube.transform.localPosition = local + Vector3.up * (dimensions.y * 0.5f);
            cube.transform.localRotation = rotation;
            cube.transform.localScale = dimensions;
            var collider = cube.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
                Object.Destroy(collider);
            }
            cube.GetComponent<Renderer>().sharedMaterial = material;
        }

        // Damage preserves two authored material tones. Only the preview uses a
        // single color; no duplicated material instances are allocated.
        public static void TintDamage(Renderer[] renderers, float healthFraction)
        {
            if (renderers == null) return;
            float cracks = 1f - Mathf.Clamp01(healthFraction);
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                var slots = renderer.sharedMaterials;
                for (int slot = 0; slot < slots.Length; slot++)
                {
                    bool frost = slots[slot] != null && slots[slot].name.IndexOf("Frost",
                        System.StringComparison.OrdinalIgnoreCase) >= 0;
                    Color tint = frost
                        ? Color.Lerp(new Color(0.62f, 0.88f, 0.96f),
                            new Color(1f, 0.62f, 0.58f), cracks)
                        : Color.Lerp(new Color(0.14f, 0.46f, 0.78f),
                            new Color(0.84f, 0.25f, 0.36f), cracks);
                    block.Clear();
                    block.SetColor("_BaseColor", tint);
                    block.SetColor("_Color", tint);
                    renderer.SetPropertyBlock(block, slot);
                }
            }
        }

        public static void Tint(Renderer[] renderers, Color tint)
        {
            if (renderers == null) return;
            block.Clear();
            block.SetColor("_BaseColor", tint);
            block.SetColor("_Color", tint);
            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i] != null) renderers[i].SetPropertyBlock(block);
        }
    }
}
