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

        private readonly RaycastHit[] forwardHits = new RaycastHit[32];
        private readonly RaycastHit[] groundHits = new RaycastHit[32];
        private readonly Collider[] overlaps = new Collider[48];
        private AarisThirdPersonPlayer motor;
        private AarisOfflineDrop drop;
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
                if (drop == null) drop = GetComponent<AarisOfflineDrop>();
                var vehicle = GetComponent<AarisVehicleSession>();
                return enabled && carriedWalls > 0 && (drop == null || drop.HasLanded) &&
                    (vehicle == null || !vehicle.IsDriving);
            }
        }

        private void Awake()
        {
            motor = GetComponent<AarisThirdPersonPlayer>();
            drop = GetComponent<AarisOfflineDrop>();
        }

        private void Start()
        {
            ghost = AarisGlooWallVisuals.Create("GlooWall_Ghost_Local", true);
            ghostRenderers = ghost.GetComponentsInChildren<Renderer>();
            // The preview must never enter cover/ground physics queries, even
            // during its first frame when Destroy(collider) is deferred.
            foreach (var previewCollider in ghost.GetComponentsInChildren<Collider>(true))
                previewCollider.enabled = false;
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
            // UpdatePlacement alone reveals a valid, collision-free preview.
            UpdatePlacement();
        }

        public void CancelAim()
        {
            aiming = false;
            placementValid = false;
            if (ghost != null && ghost.activeSelf) ghost.SetActive(false);
        }

        public void ReleaseAndDeploy()
        {
            if (!aiming) return;
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
        }

        private void UpdatePlacement()
        {
            placementValid = false;
            try
            {
            if (motor == null || motor.AimCamera == null) return;
            Vector3 look = motor.AimCamera.transform.forward;
            Vector3 ahead = Vector3.ProjectOnPlane(look, Vector3.up).normalized;
            if (ahead.sqrMagnitude < 0.5f) ahead = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            if (ahead.sqrMagnitude < 0.5f) return;
            float desired = Mathf.Lerp(minimumDistance, Mathf.Max(maximumDistance, minimumDistance),
                Mathf.InverseLerp(-0.75f, 0.4f, look.y));
            Vector3 origin = transform.position + Vector3.up * 1.1f;
            int count = Physics.RaycastNonAlloc(new Ray(origin, ahead), forwardHits, desired,
                groundAndCoverMask, QueryTriggerInteraction.Ignore);
            if (count >= forwardHits.Length) return;
            float nearest = desired;
            for (int i = 0; i < count; i++)
            {
                var hit = forwardHits[i];
                if (hit.collider == null || hit.collider.transform.IsChildOf(transform) ||
                    hit.collider is TerrainCollider) continue;
                nearest = Mathf.Min(nearest, hit.distance - 0.65f);
            }
            if (nearest < minimumDistance) return;
            Vector3 projected = transform.position + ahead * nearest;
            int groundCount = Physics.RaycastNonAlloc(
                new Ray(new Vector3(projected.x, 1200f, projected.z), Vector3.down),
                groundHits, 2400f, groundAndCoverMask, QueryTriggerInteraction.Ignore);
            if (groundCount >= groundHits.Length) return;
            float groundY = float.NegativeInfinity;
            Collider support = null;
            Vector3 normal = Vector3.up;
            for (int i = 0; i < groundCount; i++)
            {
                var hit = groundHits[i];
                if (hit.collider == null || hit.collider.transform.IsChildOf(transform) ||
                    hit.normal.y < Mathf.Cos(40f * Mathf.Deg2Rad) ||
                    hit.point.y > transform.position.y + 2.5f) continue;
                // Prefer highest walkable support, including roof and bridge top.
                if (hit.point.y > groundY)
                {
                    groundY = hit.point.y;
                    normal = hit.normal;
                    support = hit.collider;
                }
            }
            if (support == null || groundY < -37.7f || normal.y < 0.765f) return;
            proposedPoint = new Vector3(projected.x, groundY, projected.z);
            proposedRotation = Quaternion.LookRotation(ahead, Vector3.up);

            // Full width and height clearance before consuming an item.
            int blocked = Physics.OverlapBoxNonAlloc(
                proposedPoint + Vector3.up * 1.18f,
                new Vector3(1.91f, 1.11f, 0.41f), overlaps, proposedRotation,
                groundAndCoverMask, QueryTriggerInteraction.Ignore);
            if (blocked >= overlaps.Length) return; // Fail closed on buffer saturation.
            for (int i = 0; i < blocked; i++)
            {
                Collider c = overlaps[i];
                if (c == null || c.transform.IsChildOf(transform) || c is TerrainCollider) continue;
                // Flat supporting roof/floor surface can occupy the bottom few centimetres.
                // Reject taller overlapping architecture, people and vehicles.
                if (c == support && c.bounds.max.y <= groundY + 0.075f) continue;
                return;
            }
            placementValid = true;
            if (ghost == null) return;
            ghost.transform.SetPositionAndRotation(proposedPoint, proposedRotation);
            AarisGlooWallVisuals.Tint(ghostRenderers,
                new Color(0.20f, 0.78f, 0.98f, 0.40f));
            }
            finally
            {
                // No deactivate/reactivate cycle on every aiming frame.
                // Every invalid early return still hides a stale ghost.
                if (ghost != null && ghost.activeSelf != placementValid)
                    ghost.SetActive(placementValid);
            }
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
                reveal = Mathf.Min(1f, reveal + Time.deltaTime / 0.16f);
                if (visual != null)
                    visual.localScale = new Vector3(1f, Mathf.SmoothStep(0.025f, 1f, reveal), 1f);
            }
        }

        public void ApplyDamage(float amount, GameObject attacker)
        {
            if (!initialized || float.IsNaN(amount) || float.IsInfinity(amount) || amount <= 0f) return;
            health = Mathf.Max(0f, health - amount);
            AarisGlooWallVisuals.Tint(renderers,
                Color.Lerp(new Color(0.84f, 0.25f, 0.36f), new Color(0.13f, 0.60f, 0.92f),
                    health / maximum));
            if (health <= 0f) Destroy(gameObject);
        }
    }

    internal static class AarisGlooWallVisuals
    {
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
                    Object.Destroy(c);
                foreach (var renderer in art.GetComponentsInChildren<Renderer>(true))
                {
                    var slots = renderer.sharedMaterials;
                    for (int i = 0; i < slots.Length; i++) slots[i] = material;
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
                col.center = new Vector3(0f, 1.15f, -0.06f);
                col.size = new Vector3(3.78f, 2.30f, 0.67f);
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
            if (collider != null) Object.Destroy(collider);
            cube.GetComponent<Renderer>().sharedMaterial = material;
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
