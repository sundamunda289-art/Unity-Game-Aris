using System.Collections.Generic;
using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// Weapon state follows the actual physical gun between hand and floor.
    /// Ammo and reload deadlines must survive drop/pickup and never duplicate.
    /// </summary>
    public sealed class AarisWeaponState
    {
        public readonly AarisWeaponSpec Spec;
        public readonly int Magazine, Reserve;
        public readonly float ReloadUntil, NextShotAt;
        public readonly bool Reloading;
        public readonly uint SpreadSeed;

        public AarisWeaponState(AarisWeaponSpec spec, uint seed)
            : this(spec, spec != null ? spec.magazineSize : 0,
                spec != null ? spec.reserveAmmo : 0, 0f, 0f, false, seed) { }

        public AarisWeaponState(AarisWeaponSpec spec, int magazine, int reserve,
            float reloadUntil, float nextShotAt, bool reloading, uint seed)
        {
            Spec = spec;
            Magazine = magazine;
            Reserve = reserve;
            ReloadUntil = reloadUntil;
            NextShotAt = nextShotAt;
            Reloading = reloading;
            SpreadSeed = seed;
        }
    }

    /// <summary>
    /// One implementation for all locally configured weapon types.
    /// Emits camera-aligned hit scans, checks line of sight from the stance
    /// muzzle and applies damage only via the game-side IAarisDamageable hook.
    /// Single-player prototype; online sessions need server authority.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AarisEquippedGun : MonoBehaviour
    {
        private RaycastHit[] rayHits = new RaycastHit[48];
        // Reuse one scratch list during repeated shotgun damage traces.
        // Unity gameplay/Physics events are dispatched on the main thread.
        private static readonly List<MonoBehaviour> damageScratch =
            new List<MonoBehaviour>(8);
        private AarisWeaponSpec spec;
        // The weapon can be exchanged or dropped. Cache native component
        // lookups by owning GameObject, never by gun or a stale static player.
        private GameObject cachedOwner;
        private AarisBattleActions cachedActions;
        private AarisThirdPersonPlayer cachedMotor;
        private AarisWeaponInventory cachedInventory;
        private int magazine, reserve;
        private float reloadUntil, nextFire;
        private uint spreadSeed;
        private bool reloading, automaticCadence;
        // One tiny shared-material quad per equipped weapon. It is created
        // at equip/configuration time and only toggled on accepted shots;
        // automatic fire never allocates particles, lights or GameObjects.
        private Renderer muzzleFlashRenderer;
        private Transform muzzleFlashTransform;
        // One short, shadowless local light; only active during a real shot.
        private Light muzzleLight;
        private float muzzleFlashUntil;
        private static Material sharedMuzzleFlashMaterial;
        private static Mesh sharedMuzzleFlameMesh;
        private static RaycastHit[] casingGroundHits = new RaycastHit[16];
        // Ten renderer-only shell slots survive automatic fire long enough
        // to hit the ground. No Rigidbody or Collider instances are simulated.
        // The original primary slot is retained for save/test compatibility.
        private const int ExtraShellSlots = 9;
        private struct ShellState
        {
            internal Renderer renderer;
            internal Transform pose;
            internal Vector3 velocity, angularVelocity;
            internal float groundY, until;
            internal bool rested;
        }
        private readonly ShellState[] shellTrail = new ShellState[ExtraShellSlots];
        private int shellCursor;
        private Renderer casingRenderer;
        private Transform casingTransform;
        private Vector3 casingVelocity, casingAngularVelocity;
        private float casingUntil;
        // Each accepted round owns its delayed ejection. A single deadline
        // was overwritten when the next 780-RPM shot arrived before Update on
        // a slow phone, silently deleting the preceding shell. A fixed ring
        // needs no per-shot allocations and never changes damage timing.
        private const int MaximumPendingCasingEjections = 16;
        private readonly float[] casingDeadlines =
            new float[MaximumPendingCasingEjections];
        private int casingDeadlineHead, pendingCasingEjections;
        // Renderer-only brass must never consume UnityEngine.Random state:
        // visual speed/spin variation is independent of gameplay and audio.
        private uint casingVisualSeed = 0xA15C0DEu;
        // Visuals must not advance the global Unity or gameplay spread RNG.
        private uint muzzleVisualSeed = 0xF1A5C1u;
        private float NextMuzzleVisualRange(float minimum, float maximum)
        {
            muzzleVisualSeed = unchecked(
                muzzleVisualSeed * 1664525u + 1013904223u);
            float unit = (muzzleVisualSeed & 0x00FFFFFFu) *
                (1f / 16777216f);
            return minimum + (maximum - minimum) * unit;
        }
        private float NextCasingVisualRange(float minimum, float maximum)
        {
            casingVisualSeed = unchecked(
                casingVisualSeed * 1664525u + 1013904223u);
            float unit = (casingVisualSeed & 0x00FFFFFFu) *
                (1f / 16777216f);
            return minimum + (maximum - minimum) * unit;
        }
        private float casingGroundY;
        private bool casingRested;
        private static Material sharedBrassMaterial, sharedShotgunShellMaterial;
        private static Mesh sharedCasingMesh;
        // One pre-created impact emitter per equipped gun. Emit() reuses the
        // native particle buffer; automatic fire never creates impact objects.
        private ParticleSystem impactParticles;
        private Transform impactTransform;
        private static Material sharedImpactParticleMaterial;
        // Three tiny view-aligned tracer slots retain every accepted automatic
        // shot long enough to travel, even at 780 RPM. Slots are built on equip;
        // no projectile or LineRenderer allocation occurs in the fire loop.
        // Hitscan alone remains authoritative for damage and cover collision.
        private const int ExtraTracerSlots = 2;
        private struct TracerState
        {
            internal LineRenderer renderer;
            internal Vector3 start, end;
            internal float startedAt, duration, distance;
        }
        private readonly TracerState[] tracerTrail = new TracerState[ExtraTracerSlots];
        private int tracerCursor;
        private LineRenderer tracerRenderer;
        private Vector3 tracerStart, tracerEnd;
        private float tracerStartedAt, tracerDuration, tracerDistance;
        public bool Configured => spec != null;
        public bool Automatic => spec != null && spec.automatic;
        public float SecondsBetweenShots => spec != null ? spec.ShotInterval : 1f;
        internal float NextFireTime => nextFire;
        internal void BreakAutomaticCadence() => automaticCadence = false;
        internal static float AimAssistCone(int activeScope) =>
            activeScope == 4 ? 1.4f : activeScope == 2 ? 2.3f : 3.7f;
        public int Magazine { get { RefreshReload(); return magazine; } }
        public int Reserve { get { RefreshReload(); return reserve; } }
        public bool Reloading { get { RefreshReload(); return reloading; } }
        public string DisplayName => spec != null ? spec.displayName : "Unarmed";
        public float ScopeFov => spec != null && spec.scopePower > 0 ? spec.adsFov : 60f;
        public string ModelResource => spec != null ? spec.modelResource : null;
        public AarisWeaponSpec Spec => spec;

        public void Configure(AarisWeaponSpec weapon, uint matchSeed) =>
            Configure(new AarisWeaponState(weapon, matchSeed ^ 0x7F4A7C15u));

        public void Configure(AarisWeaponState state)
        {
            if (state == null || state.Spec == null || !state.Spec.Valid)
            {
                Debug.LogError("Aaris: invalid weapon state configured.");
                enabled = false;
                return;
            }
            spec = state.Spec;
            magazine = Mathf.Clamp(state.Magazine, 0, spec.magazineSize);
            reserve = Mathf.Max(0, state.Reserve);
            spreadSeed = state.SpreadSeed;
            casingVisualSeed = state.SpreadSeed ^ 0xA15C0DEu;
            muzzleVisualSeed = state.SpreadSeed ^ 0xF1A5C1u;
            nextFire = state.NextShotAt;
            BreakAutomaticCadence();
            reloadUntil = state.ReloadUntil;
            reloading = state.Reloading;
            EnsureMuzzleFlash();
            EnsureCasingVisual();
            EnsureImpactParticles();
            EnsureTracer();
            ApplyTracerWeaponProfile();
            RefreshReload();
        }

        // Cheap exact capacity probe for a nearby crate's PICK hint.
        // A completed reload can free reserve space between hint polls.
        internal bool HasReserveSpace
        {
            get
            {
                RefreshReload();
                return spec != null && reserve <
                    Mathf.Max(spec.reserveAmmo * 2, spec.magazineSize * 3);
            }
        }

        // Exact ammo transfer for death-crate loot; never generates cartridges.
        internal int AddReserve(int amount)
        {
            if (spec == null || amount <= 0) return 0;
            RefreshReload();
            int cap = Mathf.Max(spec.reserveAmmo * 2, spec.magazineSize * 3);
            int received = Mathf.Min(amount, Mathf.Max(0, cap - reserve));
            reserve += received;
            return received;
        }

        public AarisWeaponState CaptureState()
        {
            RefreshReload();
            if (spec == null) return null;
            return new AarisWeaponState(spec, magazine, reserve,
                reloadUntil, nextFire, reloading, spreadSeed);
        }

        private static Material MuzzleFlashMaterial()
        {
            if (sharedMuzzleFlashMaterial != null) return sharedMuzzleFlashMaterial;
            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) return null;
            var material = new Material(shader)
            {
                name = "Aaris Shared Muzzle Flash",
                enableInstancing = true
            };
            Color flash = new Color(2.6f, 1.24f, 0.24f, 1f);
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", flash);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", flash);
            material.SetOverrideTag("RenderType", "Transparent");
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 2f);
            if (material.HasProperty("_SrcBlend"))
                material.SetFloat("_SrcBlend",
                    (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend"))
                material.SetFloat("_DstBlend",
                    (float)UnityEngine.Rendering.BlendMode.One);
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            sharedMuzzleFlashMaterial = material;
            return material;
        }

        // Shared tapered flame can be reused by close-range rival slots.
        internal static Mesh SharedMuzzleFlameMesh() => MuzzleFlameMesh();

        internal static Material SharedGunfireGlowMaterial() => MuzzleFlashMaterial();

        // A casing must land on the floor, never on its shooter's capsule or
        // weapon attachments. Retained NonAlloc storage is shared by nearby
        // bot and player shells; this runs once per ejection, not per frame.
        internal static bool TryFindCasingGround(Vector3 port, Transform shooter,
            out RaycastHit landing)
        {
            landing = default(RaycastHit);
            int count = AarisPhysicsHitBuffer.Raycast(
                new Ray(port + Vector3.up * .12f, Vector3.down), 5.5f,
                Physics.DefaultRaycastLayers, ref casingGroundHits, 256,
                out RaycastHit[] hits);
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = hits[i];
                Collider support = hit.collider;
                // A nearby live rival's head/shoulder or a moving car can
                // be the first downward hit. Neither is a stable landing
                // floor for ballistic shells. The same no-attachment rule
                // applies to the player and shared rival casing pools.
                if (support == null || hit.normal.y <= .45f ||
                    hit.distance >= nearest ||
                    (shooter != null && support.transform.IsChildOf(shooter)) ||
                    support.attachedRigidbody != null ||
                    support.GetComponentInParent<CharacterController>() != null)
                    continue;
                nearest = hit.distance;
                landing = hit;
            }
            return landing.collider != null;
        }

        // Shared ten-vertex geometry replaces an opaque-looking yellow quad.
        // Built once; no mesh, texture, particle or object allocation per shot.
        private static Mesh MuzzleFlameMesh()
        {
            if (sharedMuzzleFlameMesh != null) return sharedMuzzleFlameMesh;
            const int spokes = 8;
            var vertices = new Vector3[spokes + 2];
            var colors = new Color32[spokes + 2];
            var triangles = new int[spokes * 6];
            vertices[0] = Vector3.zero; // bright barrel core
            vertices[1] = new Vector3(0f, 0f, 1.7f); // transparent flame tip
            colors[0] = new Color32(255, 248, 182, 220);
            colors[1] = new Color32(255, 72, 12, 0);
            for (int i = 0; i < spokes; i++)
            {
                float angle = i * Mathf.PI * 2f / spokes;
                float radius = (i & 1) == 0 ? .54f : .29f;
                vertices[i + 2] = new Vector3(
                    Mathf.Cos(angle) * radius,
                    Mathf.Sin(angle) * radius,
                    .31f + (i % 3) * .05f);
                colors[i + 2] = (i & 1) == 0
                    ? new Color32(255, 155, 33, 160)
                    : new Color32(255, 224, 105, 205);
                int next = 2 + (i + 1) % spokes;
                int t = i * 6;
                triangles[t] = 0;
                triangles[t + 1] = i + 2;
                triangles[t + 2] = next;
                triangles[t + 3] = 1;
                triangles[t + 4] = next;
                triangles[t + 5] = i + 2;
            }
            var mesh = new Mesh { name = "Aaris Shared Tapered Muzzle Flame" };
            mesh.vertices = vertices;
            mesh.colors32 = colors;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            sharedMuzzleFlameMesh = mesh;
            return mesh;
        }

        private void EnsureMuzzleFlash()
        {
            if (muzzleFlashRenderer != null || spec == null) return;
            Material material = MuzzleFlashMaterial();
            if (material == null) return;
            GameObject flash = new GameObject("Aaris Pooled Muzzle Flash",
                typeof(MeshFilter), typeof(MeshRenderer));
            flash.GetComponent<MeshFilter>().sharedMesh = MuzzleFlameMesh();
            flash.layer = gameObject.layer;
            flash.name = "Aaris Pooled Muzzle Flash";
            flash.transform.SetParent(transform, false);
            flash.transform.localPosition =
                new Vector3(0f, 0f, spec.presentationLengthMetres * 0.62f);
            flash.transform.localRotation = Quaternion.identity;
            // A disabled visual is also included in bounds QA and culling.
            // Never leave a 1.7-unit mesh at the default unit scale.
            flash.transform.localScale = Vector3.one * 0.115f;
            muzzleFlashRenderer = flash.GetComponent<MeshRenderer>();
            muzzleFlashTransform = flash.transform;
            muzzleFlashRenderer.sharedMaterial = material;
            muzzleFlashRenderer.shadowCastingMode =
                UnityEngine.Rendering.ShadowCastingMode.Off;
            muzzleFlashRenderer.receiveShadows = false;
            muzzleFlashRenderer.enabled = false;
            // Point-light shadows cost six cubemap faces in URP. Never cast any.
            var lightObject = new GameObject("Aaris Shadowless Barrel Burst");
            lightObject.transform.SetParent(flash.transform, false);
            lightObject.transform.localPosition = Vector3.zero;
            muzzleLight = lightObject.AddComponent<Light>();
            muzzleLight.type = LightType.Point;
            muzzleLight.color = new Color(1f, 0.60f, 0.25f);
            muzzleLight.range = 2.3f;
            muzzleLight.intensity = 1.5f;
            muzzleLight.shadows = LightShadows.None;
            muzzleLight.renderMode = LightRenderMode.ForcePixel;
            muzzleLight.enabled = false;
        }

        private void TriggerMuzzleFlash()
        {
            if (muzzleFlashRenderer == null || muzzleFlashTransform == null ||
                spec == null) return;
            float size = spec.category == "Pistol" ? 0.065f :
                spec.category == "Shotgun" ? 0.19f :
                spec.category == "Sniper" ? 0.15f :
                spec.category == "SMG" ? 0.09f : 0.115f;
            // Per-gun visual variance without global combat RNG mutation
            // or per-shot native allocations.
            float stretch = NextMuzzleVisualRange(0.80f, 1.22f);
            muzzleFlashTransform.localScale =
                new Vector3(size * stretch, size * stretch, size * stretch);
            muzzleFlashTransform.localRotation =
                Quaternion.Euler(0f, 0f, NextMuzzleVisualRange(0f, 360f));
            muzzleFlashUntil = Time.time +
                Mathf.Clamp(spec.ShotInterval * 0.42f, 0.022f, 0.045f);
            muzzleFlashRenderer.enabled = true;
            if (muzzleLight != null) muzzleLight.enabled = true;
        }

        private static Material CasingMaterial(bool shotgun)
        {
            Material cached = shotgun ? sharedShotgunShellMaterial : sharedBrassMaterial;
            if (cached != null) return cached;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) return null;
            var material = new Material(shader)
            {
                name = shotgun ? "Aaris Shared Shotgun Shell" : "Aaris Shared Brass Casing",
                enableInstancing = true
            };
            Color baseColor = shotgun
                ? new Color(0.38f, 0.035f, 0.025f, 1f)
                : new Color(0.55f, 0.34f, 0.075f, 1f);
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", baseColor);
            else if (material.HasProperty("_Color"))
                material.SetColor("_Color", baseColor);
            if (material.HasProperty("_Metallic"))
                material.SetFloat("_Metallic", shotgun ? 0.12f : 0.78f);
            if (material.HasProperty("_Smoothness"))
                material.SetFloat("_Smoothness", shotgun ? 0.24f : 0.42f);
            if (shotgun) sharedShotgunShellMaterial = material;
            else sharedBrassMaterial = material;
            return material;
        }


        // Unity CreatePrimitive(Cylinder) creates a native Collider that must be
        // destroyed for every pooled shell. One UV/normal-complete, sealed mesh
        // is instead shared by player and nearby bot ejections.
        // Geometry matches the built-in cylinder: height 2, radius .5.
        internal static Mesh SharedCasingMesh()
        {
            if (sharedCasingMesh != null) return sharedCasingMesh;
            const int sides = 12;
            var vertices = new Vector3[sides * 4 + 2];
            var normals = new Vector3[vertices.Length];
            var uvs = new Vector2[vertices.Length];
            var triangles = new int[sides * 12];
            int topCenter = sides * 4;
            int bottomCenter = topCenter + 1;
            vertices[topCenter] = Vector3.up;
            vertices[bottomCenter] = Vector3.down;
            normals[topCenter] = Vector3.up;
            normals[bottomCenter] = Vector3.down;
            uvs[topCenter] = uvs[bottomCenter] = new Vector2(.5f, .5f);
            for (int i = 0; i < sides; i++)
            {
                float angle = i * Mathf.PI * 2f / sides;
                float x = Mathf.Cos(angle), z = Mathf.Sin(angle);
                Vector3 radial = new Vector3(x, 0f, z);
                int top = i * 2, bottom = top + 1;
                int topRim = sides * 2 + i, bottomRim = sides * 3 + i;
                Vector3 rim = radial * .5f;
                vertices[top] = vertices[topRim] = rim + Vector3.up;
                vertices[bottom] = vertices[bottomRim] = rim + Vector3.down;
                normals[top] = normals[bottom] = radial;
                normals[topRim] = Vector3.up;
                normals[bottomRim] = Vector3.down;
                float u = (float)i / sides;
                uvs[top] = new Vector2(u, 1f);
                uvs[bottom] = new Vector2(u, 0f);
                uvs[topRim] = uvs[bottomRim] =
                    new Vector2(x * .5f + .5f, z * .5f + .5f);
                int next = (i + 1) % sides;
                int j = i * 6;
                triangles[j] = top;
                triangles[j + 1] = next * 2;
                triangles[j + 2] = bottom;
                triangles[j + 3] = next * 2;
                triangles[j + 4] = next * 2 + 1;
                triangles[j + 5] = bottom;
                j = sides * 6 + i * 6;
                // Flat cap normals and independent UV rings avoid dark,
                // open-ended brass tubes at close camera distances.
                triangles[j] = topCenter;
                triangles[j + 1] = sides * 2 + next;
                triangles[j + 2] = topRim;
                triangles[j + 3] = bottomCenter;
                triangles[j + 4] = bottomRim;
                triangles[j + 5] = sides * 3 + next;
            }
            var mesh = new Mesh { name = "Aaris Shared Closed Bullet Casing" };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            sharedCasingMesh = mesh;
            return mesh;
        }

        private void EnsureCasingVisual()
        {
            if (casingRenderer != null || spec == null) return;
            bool shotgun = spec.category == "Shotgun";
            Material material = CasingMaterial(shotgun);
            if (material == null) return;
            GameObject casing = new GameObject(
                shotgun ? "Aaris Pooled Shotgun Shell" : "Aaris Pooled Brass Casing",
                typeof(MeshFilter), typeof(MeshRenderer));
            casing.layer = gameObject.layer;
            casing.GetComponent<MeshFilter>().sharedMesh = SharedCasingMesh();
            casing.transform.SetParent(transform, false);
            casing.transform.localPosition = Vector3.zero;
            casing.transform.localRotation = Quaternion.identity;
            casing.transform.localScale = shotgun
                ? new Vector3(0.009f, 0.024f, 0.009f)
                : new Vector3(0.006f, 0.015f, 0.006f);
            casingRenderer = casing.GetComponent<Renderer>();
            casingTransform = casing.transform;
            casingRenderer.sharedMaterial = material;
            casingRenderer.shadowCastingMode =
                UnityEngine.Rendering.ShadowCastingMode.Off;
            casingRenderer.receiveShadows = false;
            casingRenderer.enabled = false;
            for (int i = 0; i < ExtraShellSlots; i++)
            {
                // Allocate one time when a gun is equipped, not at 780 RPM.
                var extra = new GameObject("Aaris Pooled Casing " + (i + 2),
                    typeof(MeshFilter), typeof(MeshRenderer));
                extra.layer = gameObject.layer;
                extra.GetComponent<MeshFilter>().sharedMesh = SharedCasingMesh();
                extra.transform.SetParent(transform, false);
                extra.transform.localScale = casing.transform.localScale;
                var renderer = extra.GetComponent<Renderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode =
                    UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.enabled = false;
                shellTrail[i] = new ShellState
                {
                    renderer = renderer,
                    pose = extra.transform
                };
            }
        }

        private void ScheduleCasingEjection()
        {
            if (casingRenderer == null || spec == null) return;
            float delay = spec.category == "Shotgun" ? 0.31f :
                spec.category == "Sniper" ? 0.29f : 0.025f;
            // The ring is larger than any plausible pending burst for all
            // authored fire rates. On pathological saturation, release the
            // oldest shell instead of silently losing the cartridge.
            if (pendingCasingEjections == casingDeadlines.Length)
            {
                casingDeadlineHead = (casingDeadlineHead + 1) %
                    MaximumPendingCasingEjections;
                pendingCasingEjections--;
                EjectCasingNow();
            }
            int tail = (casingDeadlineHead + pendingCasingEjections) %
                MaximumPendingCasingEjections;
            casingDeadlines[tail] = Time.time + delay;
            pendingCasingEjections++;
        }

        private void TickCasingEjections()
        {
            // A new shot may schedule its own deadline before this component's
            // Update in the same frame. Never overwrite the older shot.
            // The deadlines are FIFO because every shot in a weapon category
            // uses the same mechanical delay. No array shifts or GC.
            while (pendingCasingEjections > 0 &&
                Time.time >= casingDeadlines[casingDeadlineHead])
            {
                casingDeadlineHead = (casingDeadlineHead + 1) %
                    MaximumPendingCasingEjections;
                pendingCasingEjections--;
                EjectCasingNow();
            }
        }

        private void EjectCasingNow()
        {
            if (casingRenderer == null || casingTransform == null || spec == null)
                return;
            // Each accepted shot claims the next prebuilt visual. At 780 RPM
            // a casing has ~770ms before reuse, enough to reach ground.
            int index = shellCursor++ % (ExtraShellSlots + 1);
            ShellState slot = index == 0 ? default(ShellState) : shellTrail[index - 1];
            Renderer visual = index == 0 ? casingRenderer : slot.renderer;
            Transform pose = index == 0 ? casingTransform : slot.pose;
            if (visual == null || pose == null) return;

            Vector3 port = transform.TransformPoint(new Vector3(
                0.052f, 0.032f, spec.presentationLengthMetres * 0.14f));
            pose.SetParent(null, true);
            pose.position = port;
            pose.rotation = transform.rotation *
                Quaternion.Euler(90f, NextCasingVisualRange(0f, 360f), 0f);
            float sideSpeed = NextCasingVisualRange(2.2f, 3.2f);
            float upSpeed = NextCasingVisualRange(1.15f, 1.75f);
            float backSpeed = NextCasingVisualRange(0.12f, 0.48f);
            Vector3 velocity = transform.right * sideSpeed +
                transform.up * upSpeed - transform.forward * backSpeed;
            Vector3 angular = new Vector3(
                NextCasingVisualRange(520f, 980f),
                NextCasingVisualRange(760f, 1320f),
                NextCasingVisualRange(420f, 860f));
            float groundY;
            // One physics query per ejected shell; never a per-frame raycast.
            if (TryFindCasingGround(port,
                cachedOwner != null ? cachedOwner.transform : transform,
                out RaycastHit landing))
                groundY = landing.point.y + 0.012f;
            else
                groundY = cachedOwner != null
                    ? cachedOwner.transform.position.y + 0.012f
                    : port.y - 1.15f;
            groundY = Mathf.Min(groundY, port.y - 0.07f);
            float until = Time.time +
                (spec.category == "Shotgun" ? 1.1f : 0.85f);
            if (index == 0)
            {
                casingVelocity = velocity;
                casingAngularVelocity = angular;
                casingGroundY = groundY;
                casingUntil = until;
                casingRested = false;
            }
            else
            {
                slot.velocity = velocity;
                slot.angularVelocity = angular;
                slot.groundY = groundY;
                slot.until = until;
                slot.rested = false;
                shellTrail[index - 1] = slot;
            }
            visual.enabled = true;
        }

        private void HideCasing()
        {
            // Only expire the oldest primary slot; other shots keep flying.
            if (casingRenderer != null) casingRenderer.enabled = false;
            casingRested = false;
            if (casingTransform != null && casingTransform.parent != transform)
            {
                casingTransform.SetParent(transform, false);
                casingTransform.localPosition = Vector3.zero;
                casingTransform.localRotation = Quaternion.identity;
            }
        }

        private void HideAllCasings()
        {
            pendingCasingEjections = 0;
            casingDeadlineHead = 0;
            HideCasing();
            for (int i = 0; i < shellTrail.Length; i++)
            {
                ShellState slot = shellTrail[i];
                if (slot.renderer != null) slot.renderer.enabled = false;
                if (slot.pose != null && slot.pose.parent != transform)
                    slot.pose.SetParent(transform, false);
                slot.rested = false;
                shellTrail[i] = slot;
            }
        }

        private static Material ImpactParticleMaterial()
        {
            if (sharedImpactParticleMaterial != null)
                return sharedImpactParticleMaterial;
            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) return null;
            var material = new Material(shader)
            {
                name = "Aaris Shared Bullet Impact Spark",
                enableInstancing = true
            };
            Color spark = new Color(1f, 0.72f, 0.34f, 0.78f);
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", spark);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", spark);
            material.SetOverrideTag("RenderType", "Transparent");
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_SrcBlend"))
                material.SetFloat("_SrcBlend",
                    (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend"))
                material.SetFloat("_DstBlend",
                    (float)UnityEngine.Rendering.BlendMode.One);
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            sharedImpactParticleMaterial = material;
            return material;
        }

        private void EnsureImpactParticles()
        {
            if (impactParticles != null) return;
            Material material = ImpactParticleMaterial();
            if (material == null) return;
            GameObject fx = new GameObject("Aaris Pooled Bullet Impact");
            fx.transform.SetParent(transform, false);
            impactTransform = fx.transform;
            impactParticles = fx.AddComponent<ParticleSystem>();
            var main = impactParticles.main;
            main.loop = false;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 12;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.055f, 0.12f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.3f, 2.6f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.012f, 0.028f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, .82f, .45f, .88f),
                new Color(.62f, .58f, .50f, .22f));
            main.gravityModifier = 0.35f;
            var emission = impactParticles.emission;
            emission.enabled = false;
            var shape = impactParticles.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 34f;
            shape.radius = 0.008f;
            var renderer = fx.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.velocityScale = 0.08f;
            renderer.lengthScale = 0.22f;
            renderer.shadowCastingMode =
                UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sharedMaterial = material;
            impactParticles.Play(false);
        }

        private void TriggerImpactParticles(Vector3 point, Vector3 normal)
        {
            if (impactParticles == null || impactTransform == null) return;
            if (normal.sqrMagnitude < 0.5f) normal = Vector3.up;
            normal.Normalize();
            Vector3 referenceUp = Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.92f
                ? Vector3.forward : Vector3.up;
            impactTransform.SetPositionAndRotation(point + normal * 0.012f,
                Quaternion.LookRotation(normal, referenceUp));
            if (!impactParticles.isPlaying) impactParticles.Play(false);
            impactParticles.Emit(spec != null && spec.pellets > 1 ? 5 : 3);
        }

        // One shared material and three prebuilt LineRenderers. The primary
        // renderer is retained for existing scene/tool introspection.
        private void EnsureTracer()
        {
            if (tracerRenderer != null) return;
            Material material = MuzzleFlashMaterial();
            if (material == null) return;
            GameObject tracer = new GameObject("Aaris Reused Bullet Tracer");
            tracer.layer = gameObject.layer;
            tracer.transform.SetParent(transform, false);
            tracerRenderer = tracer.AddComponent<LineRenderer>();
            tracerRenderer.sharedMaterial = material;
            tracerRenderer.useWorldSpace = true;
            tracerRenderer.positionCount = 2;
            tracerRenderer.widthMultiplier = 0.012f;
            tracerRenderer.alignment = LineAlignment.View;
            tracerRenderer.startColor = new Color(1f, .87f, .52f, .82f);
            tracerRenderer.endColor = new Color(1f, .68f, .3f, .12f);
            tracerRenderer.shadowCastingMode =
                UnityEngine.Rendering.ShadowCastingMode.Off;
            tracerRenderer.receiveShadows = false;
            tracerRenderer.enabled = false;
            for (int i = 0; i < tracerTrail.Length; i++)
            {
                var extra = new GameObject("Aaris Reused Bullet Tracer " + (i + 2));
                extra.layer = gameObject.layer;
                extra.transform.SetParent(transform, false);
                var line = extra.AddComponent<LineRenderer>();
                line.sharedMaterial = material;
                line.useWorldSpace = true;
                line.positionCount = 2;
                line.widthMultiplier = tracerRenderer.widthMultiplier;
                line.alignment = LineAlignment.View;
                line.startColor = tracerRenderer.startColor;
                line.endColor = tracerRenderer.endColor;
                line.shadowCastingMode =
                    UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                line.enabled = false;
                tracerTrail[i] = new TracerState { renderer = line };
            }
        }

        // Thin weapon-class-specific streaks and short shadowless light bursts.
        // Apply at equip/reconfigure, not for each round or each mobile frame.
        private void ApplyTracerWeaponProfile()
        {
            if (tracerRenderer == null || spec == null) return;
            float width = spec.category == "Pistol" ? .007f :
                spec.category == "SMG" ? .008f :
                spec.category == "Sniper" ? .011f :
                spec.category == "Shotgun" ? .010f :
                spec.category == "DMR" ? .0095f :
                spec.id == "BattleRifle" ? .0105f : .009f;
            tracerRenderer.widthMultiplier = width;
            for (int i = 0; i < tracerTrail.Length; i++)
                if (tracerTrail[i].renderer != null)
                    tracerTrail[i].renderer.widthMultiplier = width;
            if (muzzleLight != null)
            {
                float intensity = spec.category == "Pistol" ? .85f :
                    spec.category == "SMG" ? 1.05f :
                    spec.category == "Sniper" ? 1.95f :
                    spec.category == "Shotgun" ? 1.75f :
                    spec.category == "DMR" ? 1.55f :
                    spec.id == "BattleRifle" ? 1.65f : 1.35f;
                muzzleLight.intensity = intensity;
                muzzleLight.range = spec.category == "Pistol" ? 1.55f :
                    spec.category == "SMG" ? 1.85f : 2.3f;
            }
        }

        private void TriggerTracer(Vector3 muzzle, Vector3 impact)
        {
            if (tracerRenderer == null) return;
            // Follow the animated barrel at the moment of shooting; never
            // move older tracers when the gun or stance changes.
            if (muzzleFlashTransform != null && cachedOwner != null &&
                transform.IsChildOf(cachedOwner.transform))
                muzzle = muzzleFlashTransform.position;
            Vector3 flight = impact - muzzle;
            float length = flight.magnitude;
            if (length < 0.12f) return;
            // Full physical path, not the former 64m sniper/DMR cut-off.
            // Only a tiny moving 2.1m segment is drawn by pooled renderers.
            float distance = length;
            Vector3 end = impact;
            // A low-FPS mobile frame must still display the thin streak once.
            // Hitscan and HP are unaffected by this visual lifetime.
            float frameFloor = Mathf.Clamp(Time.smoothDeltaTime * 1.3f,
                0.055f, 0.12f);
            float duration = Mathf.Clamp(Mathf.Max(distance / 1250f,
                frameFloor), 0.055f, 0.21f);
            int index = tracerCursor++ % (ExtraTracerSlots + 1);
            if (index == 0)
            {
                tracerStart = muzzle;
                tracerEnd = end;
                tracerDistance = distance;
                tracerStartedAt = Time.time;
                tracerDuration = duration;
                tracerRenderer.SetPosition(0, tracerStart);
                tracerRenderer.SetPosition(1, Vector3.MoveTowards(
                    tracerStart, tracerEnd, Mathf.Min(2.1f, tracerDistance)));
                tracerRenderer.enabled = true;
            }
            else
            {
                TracerState shot = tracerTrail[index - 1];
                if (shot.renderer == null) return;
                shot.start = muzzle;
                shot.end = end;
                shot.distance = distance;
                shot.startedAt = Time.time;
                shot.duration = duration;
                shot.renderer.SetPosition(0, muzzle);
                shot.renderer.SetPosition(1, Vector3.MoveTowards(
                    muzzle, end, Mathf.Min(2.1f, distance)));
                shot.renderer.enabled = true;
                tracerTrail[index - 1] = shot;
            }
        }

        private void TickTracer()
        {
            // Each slot advances independently while a held automatic weapon
            // sends another shot. There is no per-frame allocation or physics.
            if (tracerRenderer != null && tracerRenderer.enabled)
            {
                float elapsed = Time.time - tracerStartedAt;
                if (elapsed >= tracerDuration)
                    tracerRenderer.enabled = false;
                else
                {
                    float head = Mathf.Clamp01((elapsed +
                        Mathf.Clamp(Time.deltaTime, .001f, .016f)) / tracerDuration);
                    float tail = Mathf.Max(0f, head - 2.1f / tracerDistance);
                    Vector3 segment = tracerEnd - tracerStart;
                    tracerRenderer.SetPosition(0, tracerStart + segment * tail);
                    tracerRenderer.SetPosition(1, tracerStart + segment * head);
                }
            }
            for (int i = 0; i < tracerTrail.Length; i++)
            {
                TracerState shot = tracerTrail[i];
                if (shot.renderer == null || !shot.renderer.enabled) continue;
                float elapsed = Time.time - shot.startedAt;
                if (elapsed >= shot.duration)
                {
                    shot.renderer.enabled = false;
                    continue;
                }
                float head = Mathf.Clamp01((elapsed +
                    Mathf.Clamp(Time.deltaTime, .001f, .016f)) / shot.duration);
                float tail = Mathf.Max(0f, head - 2.1f / shot.distance);
                Vector3 segment = shot.end - shot.start;
                shot.renderer.SetPosition(0, shot.start + segment * tail);
                shot.renderer.SetPosition(1, shot.start + segment * head);
            }
        }

        private void Update()
        {
            RefreshReload();
            TickTracer();
            if (muzzleFlashRenderer != null && muzzleFlashRenderer.enabled &&
                Time.time >= muzzleFlashUntil)
            {
                muzzleFlashRenderer.enabled = false;
                if (muzzleLight != null) muzzleLight.enabled = false;
            }
            TickCasingEjections();
            if (casingRenderer != null && casingRenderer.enabled &&
                casingTransform != null)
            {
                // Visual ballistic motion must not run at half speed on
                // 10-15 FPS phones. Keep an upper bound for extreme hitches.
                float dt = Mathf.Min(Time.deltaTime, 0.12f);
                if (!casingRested)
                {
                    casingVelocity += Physics.gravity * dt;
                    Vector3 next = casingTransform.position + casingVelocity * dt;
                    if (next.y <= casingGroundY && casingVelocity.y < 0f)
                    {
                        next.y = casingGroundY;
                        casingVelocity.y = -casingVelocity.y * 0.28f;
                        casingVelocity.x *= 0.58f;
                        casingVelocity.z *= 0.58f;
                        casingAngularVelocity *= 0.45f;
                        if (casingVelocity.y < 0.5f) casingRested = true;
                    }
                    casingTransform.position = next;
                    casingTransform.Rotate(casingAngularVelocity * dt, Space.World);
                }
                if (Time.time >= casingUntil) HideCasing();
            }
            // All shells share one budgeted Update: ten tiny transforms,
            // zero native physics bodies and zero per-frame ray queries.
            for (int i = 0; i < shellTrail.Length; i++)
            {
                ShellState slot = shellTrail[i];
                if (slot.renderer == null || !slot.renderer.enabled ||
                    slot.pose == null) continue;
                if (Time.time >= slot.until)
                {
                    slot.renderer.enabled = false;
                    if (slot.pose.parent != transform)
                        slot.pose.SetParent(transform, false);
                    slot.rested = false;
                    shellTrail[i] = slot;
                    continue;
                }
                if (slot.rested) continue;
                // Visual ballistic motion must not run at half speed on
                // 10-15 FPS phones. Keep an upper bound for extreme hitches.
                float dt = Mathf.Min(Time.deltaTime, 0.12f);
                slot.velocity += Physics.gravity * dt;
                Vector3 next = slot.pose.position + slot.velocity * dt;
                if (next.y <= slot.groundY && slot.velocity.y < 0f)
                {
                    next.y = slot.groundY;
                    slot.velocity.y = -slot.velocity.y * 0.28f;
                    slot.velocity.x *= 0.58f;
                    slot.velocity.z *= 0.58f;
                    slot.angularVelocity *= 0.45f;
                    if (slot.velocity.y < 0.5f) slot.rested = true;
                }
                slot.pose.position = next;
                slot.pose.Rotate(slot.angularVelocity * dt, Space.World);
                shellTrail[i] = slot;
            }
        }

        private void OnDisable()
        {
            BreakAutomaticCadence();
            if (muzzleFlashRenderer != null)
                muzzleFlashRenderer.enabled = false;
            if (muzzleLight != null) muzzleLight.enabled = false;
            if (tracerRenderer != null) tracerRenderer.enabled = false;
            for (int i = 0; i < tracerTrail.Length; i++)
                if (tracerTrail[i].renderer != null)
                    tracerTrail[i].renderer.enabled = false;
            HideAllCasings();
        }

        private void OnDestroy()
        {
            // Active casings are temporarily detached into world space. Do not
            // leave that pooled visual behind if its owning gun is destroyed.
            if (casingTransform != null && casingTransform.parent != transform)
                Destroy(casingTransform.gameObject);
            for (int i = 0; i < shellTrail.Length; i++)
            {
                Transform pose = shellTrail[i].pose;
                if (pose != null && pose.parent != transform)
                    Destroy(pose.gameObject);
            }
        }

        // Even an unequipped weapon must complete its reload on schedule.
        // Unity stops calling Update on inactive inventory slot GameObjects.
        public void RefreshReload()
        {
            if (!reloading || spec == null || Time.time < reloadUntil) return;
            int transfer = Mathf.Min(spec.magazineSize - magazine, reserve);
            magazine += transfer;
            reserve -= transfer;
            reloading = false;
        }

        public bool BeginReload()
        {
            RefreshReload();
            if (spec == null || reloading || magazine >= spec.magazineSize || reserve == 0)
                return false;
            reloadUntil = Time.time + spec.reloadSeconds;
            reloading = true;
            BreakAutomaticCadence();
            AarisAudioDirector.ReloadStarted(transform.position);
            return true;
        }

        private bool FirstExternalHit(Ray ray, float range, Transform owner,
            out RaycastHit nearest)
        {
            if (!Physics.Raycast(ray, out nearest, range,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return false;
            if (!nearest.collider.transform.IsChildOf(owner)) return true;
            nearest = default(RaycastHit);
            int count = AarisPhysicsHitBuffer.Raycast(ray, range,
                Physics.DefaultRaycastLayers, ref rayHits, 256,
                out RaycastHit[] candidates);
            float minDistance = float.PositiveInfinity;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                Collider collider = candidates[i].collider;
                if (collider == null || collider.transform.IsChildOf(owner)) continue;
                if (candidates[i].distance >= minDistance) continue;
                minDistance = candidates[i].distance;
                nearest = candidates[i];
                found = true;
            }
            return found;
        }

        public bool TryFire(Camera camera, GameObject owner)
        {
            RefreshReload();
            if (spec == null || camera == null || owner == null || reloading ||
                Time.time < nextFire) return false;
            if (magazine <= 0)
            {
                BeginReload();
                return false;
            }
            // Consume exactly one cartridge per trigger event, including
            // multi-pellet shotguns; reload and cooldown live in weapon state.
            magazine--;
            float interval = spec.ShotInterval;
            // Carry frame lateness during a held burst so the rate doesn't
            // round down to whole frames. Discard debt older than two shots.
            nextFire = automaticCadence
                ? Mathf.Max(nextFire + interval, Time.time - interval)
                : Time.time + interval;
            automaticCadence = spec.automatic;
            TriggerMuzzleFlash();
            ScheduleCasingEjection();
            if (cachedOwner != owner)
            {
                cachedOwner = owner;
                cachedActions = owner.GetComponent<AarisBattleActions>();
                cachedMotor = owner.GetComponent<AarisThirdPersonPlayer>();
                cachedInventory = owner.GetComponent<AarisWeaponInventory>();
            }
            float muzzleHeight = cachedActions != null
                ? Mathf.Max(0.28f, cachedActions.CameraEyeHeight - 0.15f) : 1.3f;
            Vector3 muzzleAnchor = owner.transform.position + Vector3.up * muzzleHeight;
            Vector3 muzzle = muzzleAnchor + owner.transform.forward * 0.24f;
            // PhysX rays do not hit a collider containing their origin. Check
            // the short receiver-to-muzzle segment first so a thin wall cannot
            // be bypassed when the barrel starts inside or just beyond it.
            // Shared by all shotgun pellets; no per-frame query or new buffer.
            bool muzzleBlocked = FirstExternalHit(
                new Ray(muzzleAnchor, owner.transform.forward), 0.24f,
                owner.transform, out RaycastHit closeCover);
            AarisAudioDirector.GunShot(spec.category, spec.id, muzzle);

            // Bounded sight-assist is shared with the red HUD reticle; never
            // aim through cover. Actual bullets still use the muzzle obstruction ray.
            Vector3 shotForward = camera.transform.forward;
            int activeScope = cachedInventory != null && cachedInventory.Current == this
                ? cachedInventory.ActiveScopePower : 0;
            float assistCone = AimAssistCone(activeScope);
            bool assistedLock = AarisOfflineBRDirector.TryAcquireAimTarget(
                camera, owner.transform, spec.rangeMetres, assistCone,
                out Vector3 assisted);
            if (assistedLock)
            {
                // A confirmed RED reticle must correspond to the actual shot
                // ray. Partial Slerp at 4x can miss a 0.62m rival capsule even
                // when the scope says locked. Weapon spread and first physical
                // muzzle obstruction still govern the final pellet impacts.
                shotForward = assisted;
            }
            // The red target indicator must give reliable body hits, not
            // promise a lock while long-range random spread misses a 0.62m
            // capsule. Keep unassisted accuracy untouched and preserve the
            // shotgun's multi-pellet pattern. Cover, muzzle parallax, ammo,
            // recoil and actual PhysX impacts remain authoritative.
            float shotSpreadDegrees = !assistedLock ? spec.spreadDegrees :
                spec.pellets > 1 ? spec.spreadDegrees * 0.65f :
                Mathf.Min(spec.spreadDegrees * 0.30f,
                    14f / Mathf.Max(1f, spec.rangeMetres));
            bool impactSoundEmitted = false;
            AarisNearbyColliders cover = cachedMotor != null ? cachedMotor.CombatCover : null;
            for (int pellet = 0; pellet < spec.pellets; pellet++)
            {
                Vector2 spread = new Vector2(AarisWeaponRandom.Next01(ref spreadSeed) * 2f - 1f,
                    AarisWeaponRandom.Next01(ref spreadSeed) * 2f - 1f);
                spread = Vector2.ClampMagnitude(spread, 1f) * shotSpreadDegrees;
                Vector3 look = Quaternion.AngleAxis(spread.x, camera.transform.up) *
                    Quaternion.AngleAxis(-spread.y, camera.transform.right) * shotForward;
                Ray aim = new Ray(camera.transform.position, look);
                RaycastHit target = closeCover;
                bool targetFound = muzzleBlocked || FirstExternalHit(aim, spec.rangeMetres,
                    owner.transform, out target);
                Vector3 destination = targetFound ? target.point : aim.GetPoint(spec.rangeMetres);
                // The first physical muzzle surface controls damage and VFX.
                Vector3 impactPoint = destination;
                Vector3 impactNormal = targetFound ? target.normal : -look;
                bool hitStreamedCover = false;
                Vector3 path = destination - muzzle;
                float distance = path.magnitude;
                if (!muzzleBlocked && distance > 0.025f)
                {
                    Ray muzzleRay = new Ray(muzzle, path / distance);
                    bool muzzleHitFound = FirstExternalHit(
                        muzzleRay, distance + 0.05f,
                        owner.transform, out RaycastHit muzzleHit);
                    // A camera-only ray hit must never award physical damage.
                    targetFound = muzzleHitFound;
                    if (muzzleHitFound)
                    {
                        // Camera and muzzle can hit DIFFERENT heights on the same
                        // enemy. Actual impact controls headshot AND visuals.
                        impactPoint = muzzleHit.point;
                        impactNormal = muzzleHit.normal;
                        target = muzzleHit;
                        targetFound = true;
                    }
                    float length = muzzleHitFound ? muzzleHit.distance : distance;
                    if (cover != null && cover.TryGetInactiveCoverHit(muzzleRay,
                        length, out float coverDistance))
                    {
                        impactPoint = muzzleRay.GetPoint(coverDistance);
                        impactNormal = -muzzleRay.direction;
                        targetFound = false;
                        hitStreamedCover = true;
                    }
                }
                else if (!muzzleBlocked)
                {
                    // Degenerate convergence cannot authorize camera-only damage.
                    targetFound = false;
                }
                // Every real round gets a streak, including full-auto.
                // A shotgun's pellets share one tracer; three pooled slots
                // preserve overlapping automatic rounds until they expire.
                if (pellet == 0 && !muzzleBlocked)
                    TriggerTracer(muzzle, impactPoint);
                if ((targetFound || hitStreamedCover) && !impactSoundEmitted)
                {
                    // A shotgun's first pellet can miss while a later one
                    // hits. Emit one impact cue for the first genuine hit,
                    // not only if pellet zero happens to connect.
                    AarisAudioDirector.BulletImpact(impactPoint);
                    TriggerImpactParticles(impactPoint, impactNormal);
                    impactSoundEmitted = true;
                }
                if (targetFound)
                {
                    ApplyDamage(target.collider, spec.damage, owner, target.point,
                        spec.headshotMultiplier);
                }
            }
            if (cachedMotor != null) cachedMotor.ApplyWeaponRecoil(spec.recoilPitchDegrees,
                (AarisWeaponRandom.Next01(ref spreadSeed) - 0.5f) *
                spec.recoilPitchDegrees * 0.4f);
            if (magazine == 0) BeginReload();
            return true;
        }

        // Authored rigs may put a tiny collider on the Head bone. A 20cm head
        // hitbox fails the height heuristic below, even when PhysX actually hit
        // the head. Match only explicit bone/hitbox names under the victim root;
        // never promote arbitrary body colliders or scenery to headshots.
        private static bool IsAuthoredHeadHit(Collider hit, Transform receiver)
        {
            if (hit == null || receiver == null) return false;
            for (Transform part = hit.transform;
                part != null && part != receiver; part = part.parent)
            {
                string label = part.name;
                if (string.Equals(label, "Head", System.StringComparison.OrdinalIgnoreCase) ||
                    label.EndsWith(":Head", System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(label, "HeadHitbox", System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(label, "HeadCollider", System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        internal static bool ApplyDamage(Collider collider, float amount, GameObject attacker)
        {
            return ApplyDamage(collider, amount, attacker, null);
        }

        internal static bool ApplyDamage(Collider collider, float amount, GameObject attacker,
            Vector3? hitPosition, float headshotMultiplier = 1.75f)
        {
            if (collider == null || amount <= 0f || float.IsNaN(amount) ||
                float.IsInfinity(amount)) return false;
            if (attacker != null && collider.transform.IsChildOf(attacker.transform))
                return false;
            for (Transform node = collider.transform; node != null; node = node.parent)
            {
                // GetComponents<T>(List<T>) fills the reusable list without
                // allocating a new managed array for every bullet/pellet.
                damageScratch.Clear();
                node.GetComponents(damageScratch);
                for (int i = 0; i < damageScratch.Count; i++)
                {
                    if (!(damageScratch[i] is IAarisDamageable damageable)) continue;
                    damageScratch.Clear();
                    // No marker for an already defeated actor or a self-hit.
                    bool canRegister = !(damageable is AarisOfflineHealth health) ||
                        !health.IsDead;
                    bool headshot = false;
                    float damage = amount;
                    if (damageable is AarisOfflineHealth victim)
                    {
                        // Unpositioned melee receives real HP feedback, but no
                        // unearned headshot without a physical hit coordinate.
                        if (hitPosition.HasValue)
                        {
                            Bounds bounds = collider.bounds;
                            // Head threshold uses the current standing,
                            // crouched or prone capsule, not a fixed height.
                            // An arm/shoulder child cannot become a headshot.
                            // Authored HeadCollider/Head bones still qualify.
                            headshot = IsAuthoredHeadHit(collider, node) ||
                                (collider.transform == node &&
                                 bounds.size.y > .28f && hitPosition.Value.y >=
                                    bounds.min.y + bounds.size.y * .79f);
                        }
                        if (headshot) damage *= Mathf.Clamp(headshotMultiplier, 1f, 4f);
                        float before = victim.CurrentHealth;
                        victim.ApplyHitDamage(damage, attacker, headshot);
                        float actualLoss = Mathf.Max(0f, before - victim.CurrentHealth);
                        // Mark damage only when HP really decreases.
                        canRegister = actualLoss > 0f;
                        if (actualLoss > 0f && attacker != null &&
                            !node.IsChildOf(attacker.transform))
                            AarisCombatFeedbackHUD.NotifyOutgoingDamage(attacker, actualLoss, headshot,
                                hitPosition ?? collider.bounds.center);
                    }
                    else
                    {
                        // Confirm only real HP loss for Gloo Walls and panels.
                        IAarisDamageableWithHealth tracked =
                            damageable as IAarisDamageableWithHealth;
                        float before = tracked != null ? tracked.CurrentHealth : 0f;
                        damageable.ApplyDamage(damage, attacker);
                        if (tracked != null) canRegister = tracked.CurrentHealth < before;
                    }
                    if (canRegister && attacker != null &&
                        !node.IsChildOf(attacker.transform))
                        AarisWeaponHUD.NotifyHit(attacker, headshot);
                    return canRegister;
                }
            }
            damageScratch.Clear();
            return false;
        }
    }

    public interface IAarisDamageable
    {
        void ApplyDamage(float amount, GameObject attacker);
    }

    // Optional authoritative HP feedback; legacy damageables still work.
    public interface IAarisDamageableWithHealth : IAarisDamageable
    {
        float CurrentHealth { get; }
    }
}
