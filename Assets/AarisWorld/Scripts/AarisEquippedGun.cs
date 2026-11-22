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
        private int magazine, reserve;
        private float reloadUntil, nextFire;
        private uint spreadSeed;
        private bool reloading;
        // One tiny shared-material quad per equipped weapon. It is created
        // at equip/configuration time and only toggled on accepted shots;
        // automatic fire never allocates particles, lights or GameObjects.
        private Renderer muzzleFlashRenderer;
        private Transform muzzleFlashTransform;
        private float muzzleFlashUntil;
        private static Material sharedMuzzleFlashMaterial;
        private static Mesh sharedMuzzleFlameMesh;
        // One recycled shell visual per weapon. This deliberately avoids
        // Rigidbody/collider shell storms at 780 RPM on 2 GB Android phones.
        private Renderer casingRenderer;
        private Transform casingTransform;
        private Vector3 casingVelocity, casingAngularVelocity;
        private float casingUntil, casingEjectAt = -1f;
        private static Material sharedBrassMaterial, sharedShotgunShellMaterial;
        // One pre-created impact emitter per equipped gun. Emit() reuses the
        // native particle buffer; automatic fire never creates impact objects.
        private ParticleSystem impactParticles;
        private Transform impactTransform;
        private static Material sharedImpactParticleMaterial;
        // One reused view-aligned tracer per gun, never a new projectile or
        // LineRenderer allocation in the high-rate firing loop.
        // Hitscan remains the single authoritative damage/collision path.
        private LineRenderer tracerRenderer;
        private Vector3 tracerStart, tracerEnd;
        private float tracerStartedAt, tracerDuration, tracerDistance;
        private int tracerShotCounter;
        public bool Configured => spec != null;
        public bool Automatic => spec != null && spec.automatic;
        public float SecondsBetweenShots => spec != null ? spec.ShotInterval : 1f;
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
            nextFire = state.NextShotAt;
            reloadUntil = state.ReloadUntil;
            reloading = state.Reloading;
            EnsureMuzzleFlash();
            EnsureCasingVisual();
            EnsureImpactParticles();
            EnsureTracer();
            RefreshReload();
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
        }

        private void TriggerMuzzleFlash()
        {
            if (muzzleFlashRenderer == null || muzzleFlashTransform == null ||
                spec == null) return;
            float size = spec.category == "Shotgun" ? 0.19f :
                spec.category == "Sniper" ? 0.15f :
                spec.category == "SMG" ? 0.09f : 0.115f;
            // Slight size/roll variance avoids a frozen decal look. This uses
            // Unity's scalar RNG only; no temporary meshes or particles.
            float stretch = UnityEngine.Random.Range(0.80f, 1.22f);
            muzzleFlashTransform.localScale =
                new Vector3(size * stretch, size * stretch, size * stretch);
            muzzleFlashTransform.localRotation =
                Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f));
            muzzleFlashUntil = Time.time +
                Mathf.Clamp(spec.ShotInterval * 0.42f, 0.022f, 0.045f);
            muzzleFlashRenderer.enabled = true;
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

        private void EnsureCasingVisual()
        {
            if (casingRenderer != null || spec == null) return;
            bool shotgun = spec.category == "Shotgun";
            Material material = CasingMaterial(shotgun);
            if (material == null) return;
            GameObject casing = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            casing.layer = gameObject.layer;
            casing.name = shotgun ? "Aaris Pooled Shotgun Shell" :
                "Aaris Pooled Brass Casing";
            casing.transform.SetParent(transform, false);
            casing.transform.localPosition = Vector3.zero;
            casing.transform.localRotation = Quaternion.identity;
            casing.transform.localScale = shotgun
                ? new Vector3(0.009f, 0.024f, 0.009f)
                : new Vector3(0.006f, 0.015f, 0.006f);
            Collider shellCollider = casing.GetComponent<Collider>();
            if (shellCollider != null)
            {
                shellCollider.enabled = false;
                Destroy(shellCollider);
            }
            casingRenderer = casing.GetComponent<Renderer>();
            casingTransform = casing.transform;
            casingRenderer.sharedMaterial = material;
            casingRenderer.shadowCastingMode =
                UnityEngine.Rendering.ShadowCastingMode.Off;
            casingRenderer.receiveShadows = false;
            casingRenderer.enabled = false;
        }

        private void ScheduleCasingEjection()
        {
            if (casingRenderer == null || spec == null) return;
            float delay = spec.category == "Shotgun" ? 0.31f :
                spec.category == "Sniper" ? 0.29f : 0.025f;
            casingEjectAt = Time.time + delay;
        }

        private void EjectCasingNow()
        {
            casingEjectAt = -1f;
            if (casingRenderer == null || casingTransform == null || spec == null)
                return;
            // The weapon root uses local +Z as the barrel axis. The ejection
            // port sits just right/up of the receiver, well behind the muzzle.
            Vector3 port = transform.TransformPoint(new Vector3(
                0.052f, 0.032f, spec.presentationLengthMetres * 0.14f));
            casingTransform.SetParent(null, true);
            casingTransform.position = port;
            casingTransform.rotation = transform.rotation *
                Quaternion.Euler(90f, UnityEngine.Random.Range(0f, 360f), 0f);
            float sideSpeed = UnityEngine.Random.Range(2.2f, 3.2f);
            float upSpeed = UnityEngine.Random.Range(1.15f, 1.75f);
            float backSpeed = UnityEngine.Random.Range(0.12f, 0.48f);
            casingVelocity = transform.right * sideSpeed +
                transform.up * upSpeed - transform.forward * backSpeed;
            casingAngularVelocity = new Vector3(
                UnityEngine.Random.Range(520f, 980f),
                UnityEngine.Random.Range(760f, 1320f),
                UnityEngine.Random.Range(420f, 860f));
            casingUntil = Time.time +
                (spec.category == "Shotgun" ? 0.42f : 0.32f);
            casingRenderer.enabled = true;
        }

        private void HideCasing()
        {
            casingEjectAt = -1f;
            if (casingRenderer != null) casingRenderer.enabled = false;
            if (casingTransform != null && casingTransform.parent != transform)
            {
                casingTransform.SetParent(transform, false);
                casingTransform.localPosition = Vector3.zero;
                casingTransform.localRotation = Quaternion.identity;
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

        // A short moving streak makes bullet direction visible without changing
        // hit registration, spawning physics bodies, or adding Update allocations.
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
        }

        private void TriggerTracer(Vector3 muzzle, Vector3 impact)
        {
            if (tracerRenderer == null) return;
            Vector3 flight = impact - muzzle;
            float length = flight.magnitude;
            if (length < 0.12f) return;
            // Cap the visible streak; long-distance hits retain their actual
            // damage result while distant tracer pixels do not overdraw terrain.
            tracerDistance = Mathf.Min(length, 64f);
            tracerStart = muzzle;
            tracerEnd = muzzle + flight * (tracerDistance / length);
            tracerStartedAt = Time.time;
            tracerDuration = Mathf.Clamp(tracerDistance / 310f, 0.025f, 0.21f);
            tracerRenderer.SetPosition(0, tracerStart);
            tracerRenderer.SetPosition(1, tracerStart);
            tracerRenderer.enabled = true;
        }

        private void TickTracer()
        {
            if (tracerRenderer == null || !tracerRenderer.enabled) return;
            float head = (Time.time - tracerStartedAt) / tracerDuration;
            if (head >= 1f)
            {
                tracerRenderer.enabled = false;
                return;
            }
            head = Mathf.Clamp01(head);
            float tail = Mathf.Max(0f, head - 2.1f / tracerDistance);
            Vector3 segment = tracerEnd - tracerStart;
            tracerRenderer.SetPosition(0, tracerStart + segment * tail);
            tracerRenderer.SetPosition(1, tracerStart + segment * head);
        }

        private void Update()
        {
            RefreshReload();
            TickTracer();
            if (muzzleFlashRenderer != null && muzzleFlashRenderer.enabled &&
                Time.time >= muzzleFlashUntil)
                muzzleFlashRenderer.enabled = false;
            if (casingEjectAt >= 0f && Time.time >= casingEjectAt)
                EjectCasingNow();
            if (casingRenderer != null && casingRenderer.enabled &&
                casingTransform != null)
            {
                float dt = Mathf.Min(Time.deltaTime, 0.05f);
                casingVelocity += Physics.gravity * dt;
                casingTransform.position += casingVelocity * dt;
                casingTransform.Rotate(casingAngularVelocity * dt, Space.World);
                if (Time.time >= casingUntil) HideCasing();
            }
        }

        private void OnDisable()
        {
            if (muzzleFlashRenderer != null)
                muzzleFlashRenderer.enabled = false;
            if (tracerRenderer != null) tracerRenderer.enabled = false;
            HideCasing();
        }

        private void OnDestroy()
        {
            // Active casings are temporarily detached into world space. Do not
            // leave that pooled visual behind if its owning gun is destroyed.
            if (casingTransform != null && casingTransform.parent != transform)
                Destroy(casingTransform.gameObject);
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
            AarisAudioDirector.ReloadStarted(transform.position);
            return true;
        }

        private bool FirstExternalHit(Ray ray, float range, Transform owner,
            out RaycastHit nearest)
        {
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
            nextFire = Time.time + spec.ShotInterval;
            TriggerMuzzleFlash();
            ScheduleCasingEjection();
            if (cachedOwner != owner)
            {
                cachedOwner = owner;
                cachedActions = owner.GetComponent<AarisBattleActions>();
                cachedMotor = owner.GetComponent<AarisThirdPersonPlayer>();
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
            float assistCone = spec.scopePower == 4 ? 1.4f :
                spec.scopePower == 2 ? 2.3f : 3.7f;
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
                // Damage selection may keep the camera reticle's hit when both
                // rays resolve to the same damageable object, but the audiovisual
                // contact must still occur at the FIRST physical muzzle surface.
                Vector3 impactPoint = destination;
                Vector3 impactNormal = targetFound ? target.normal : -look;
                Vector3 path = destination - muzzle;
                float distance = path.magnitude;
                if (!muzzleBlocked && distance > 0.025f)
                {
                    bool muzzleHitFound = FirstExternalHit(
                        new Ray(muzzle, path / distance), distance + 0.05f,
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
                }
                else if (!muzzleBlocked)
                {
                    // Degenerate convergence cannot authorize camera-only damage.
                    targetFound = false;
                }
                // One tracer on a bolt/semi shot, every third automatic shot.
                // Repeated pellets share the visual, rather than spawning 8 trails.
                if (pellet == 0 && (!spec.automatic || ++tracerShotCounter % 3 == 0))
                    TriggerTracer(muzzle, impactPoint);
                if (targetFound)
                {
                    // A shotgun's first pellet can miss while a later one
                    // hits. Emit one impact cue for the first genuine hit,
                    // not only if pellet zero happens to connect.
                    if (!impactSoundEmitted)
                    {
                        AarisAudioDirector.BulletImpact(impactPoint);
                        TriggerImpactParticles(impactPoint, impactNormal);
                        impactSoundEmitted = true;
                    }
                    ApplyDamage(target.collider, spec.damage, owner, target.point);
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

        internal static void ApplyDamage(Collider collider, float amount, GameObject attacker)
        {
            ApplyDamage(collider, amount, attacker, null);
        }

        internal static void ApplyDamage(Collider collider, float amount, GameObject attacker,
            Vector3? hitPosition)
        {
            if (collider == null) return;
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
                    if (damageable is AarisOfflineHealth victim && hitPosition.HasValue)
                    {
                        Bounds bounds = collider.bounds;
                        // Respect the current collision stance (standing, crouching,
                        // prone), rather than assuming a standing 1.8m capsule.
                        // Only the receiver's own full-body collider can
                        // use height-based head detection. A raised hand,
                        // forearm or shoulder child collider is not a head,
                        // even when struck in its upper 21 percent. Authored
                        // Head/HeadCollider child hitboxes remain authoritative.
                        headshot = IsAuthoredHeadHit(collider, node) ||
                            (collider.transform == node &&
                             bounds.size.y > .28f && hitPosition.Value.y >=
                                bounds.min.y + bounds.size.y * .79f);
                        if (headshot) damage *= 1.75f;
                        float before = victim.CurrentHealth;
                        victim.ApplyHitDamage(damage, attacker, headshot);
                        float actualLoss = Mathf.Max(0f, before - victim.CurrentHealth);
                        if (actualLoss > 0f && attacker != null &&
                            !node.IsChildOf(attacker.transform))
                            AarisCombatFeedbackHUD.NotifyOutgoingDamage(attacker, actualLoss, headshot,
                                hitPosition ?? collider.bounds.center);
                    }
                    else damageable.ApplyDamage(damage, attacker);
                    if (canRegister && attacker != null &&
                        !node.IsChildOf(attacker.transform))
                        AarisWeaponHUD.NotifyHit(attacker);
                    return;
                }
            }
            damageScratch.Clear();
        }
    }

    public interface IAarisDamageable
    {
        void ApplyDamage(float amount, GameObject attacker);
    }
}
