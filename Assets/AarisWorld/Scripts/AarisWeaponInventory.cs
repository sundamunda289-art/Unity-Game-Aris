using System.Collections.Generic;
using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// Two physical weapon slots: one held and one back-holstered.
    /// Picking up a third weapon drops only the replaced hand slot, retaining
    /// magazine/reload state. Fist mode leaves both guns visibly holstered.
    /// Model roots stay under the player to avoid imported bone-scale errors.
    /// </summary>
    // Run after AarisCharacterVisual (order +200): its Playables update
    // the hand bones before this class places the gun at that pose.
    [DefaultExecutionOrder(310)]
    [DisallowMultipleComponent, RequireComponent(typeof(AarisBattleActions))]
    public sealed class AarisWeaponInventory : MonoBehaviour
    {
        private AarisEquippedGun equipped, stowed;
        private Transform weaponRoot, stowedRoot;
        private AarisBattleActions combat;
        private AarisThirdPersonPlayer motor;
        private AarisWeaponLootWorld lootWorld;
        private Transform hand, chest, hips, leftUpperArm, leftForearm, leftHand;
        private readonly List<Animator> socketAnimators = new List<Animator>(4);
        private const float DrawSeconds = 0.18f;
        private const float StowSeconds = 0.22f;

        internal enum SocketKind { Unplaced, Hands, RightBack, LeftBack }

        internal struct SocketMotion
        {
            internal SocketKind Kind;
            internal bool Active, AroundShoulders;
            internal float Started, Progress;
            internal Vector3 StartCenter;
            internal Quaternion StartRotation;
        }
        private Transform leftThumb, leftMiddle;
        private Transform rightUpperArm, rightForearm;
        private readonly Transform[] rightGripDigits = new Transform[12];
        private readonly Transform[] leftGripDigits = new Transform[12];
        private bool fingerRigResolved;
        private CharacterController capsule;
        private float nextRigSearch;
        private bool aiming, fistMode;
        private float sprintReadyBlend;
        // Lightweight third-person weapon inertia. These are visual-only
        // values: hitscan authority stays camera-aligned and no Rigidbody or
        // Animation Rigging graph is added for the local player's firearm.
        private Quaternion previousAimRotation;
        private bool weaponMotionReady;
        private Vector3 smoothedWeaponSway;
        private float visualRecoilDegrees;
        private float lastVisualShotTime = -10f;
        private static Material smgFinish, assaultFinish, battleFinish,
            sniperFinish, shotgunFinish;
        // One shared URP PBR material per locally vendored textured firearm.
        // The same imported maps and base finish are reused for held, stowed
        // and floor instances. The floor finish changes the tint and smoothness.
        private static readonly Dictionary<string, Material> pbrFinishCache =
            new Dictionary<string, Material>(8, System.StringComparer.Ordinal);
        // One converted URP material per imported flat-shaded source slot.
        // Reused by every pickup/equip instance so extra visual separation
        // does not become per-gun memory churn.
        private static readonly Dictionary<Material, Material> weaponFinishCache =
            new Dictionary<Material, Material>();
        private static readonly Dictionary<Material, Material> groundFinishCache =
            new Dictionary<Material, Material>(12);
        // A complete humanoid carries the equipped gun at low-ready while sprinting;
        // incomplete rigs keep the established safe back-holster fallback.
        public bool IsSprintLowReady { get; private set; }
        public bool IsSprintHolstered { get; private set; }

        public AarisEquippedGun Current => equipped;
        public bool Aiming => aiming;
        public bool HasGun => equipped != null && equipped.Configured && !fistMode;
        internal int ActiveScopePower => aiming && HasGun ? equipped.Spec.scopePower : 0;
        // Optics reflect the same paused/vehicle/flight handoff as real fire input.
        internal bool CanShowCombatReticle => HasGun && AcceptsPlayerWeaponInput;
        public bool HasStowedGun => stowed != null && stowed.Configured;
        public bool HasAnyGun => equipped != null && equipped.Configured;
        public bool FistMode => fistMode;
        public bool RequiresExchangeDrop => equipped != null && stowed != null;
        // Do not leave a seemingly live PICK while the input owner is paused.
        public bool PickupAvailable => AcceptsPlayerWeaponInput &&
            ((lootWorld != null && lootWorld.HasPickupInRange) ||
             AarisDeathCrateWorld.HasPickupNear(transform));

        private static bool HasAuthoredTexture(Material material)
        {
            if (material == null) return false;
            Texture texture = null;
            if (material.HasProperty("_BaseMap"))
                texture = material.GetTexture("_BaseMap");
            if (texture == null && material.mainTexture != null)
                texture = material.mainTexture;
            return texture != null && texture != Texture2D.whiteTexture;
        }

        // Current bundled CC0 firearms are textureless/flat-shaded. Give those
        // meshes a physically plausible shared URP finish, while leaving any
        // future authored PBR/textured replacement completely untouched.
        private static Material CreateWeaponFinish(ref Material cached,
            string label, Color baseColor, float metallic, float smoothness)
        {
            if (cached != null) return cached;
            Shader shader = AarisShaderCache.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = AarisShaderCache.Find("Universal Render Pipeline/Simple Lit");
            if (shader == null) return null;
            cached = new Material(shader) { name = "AarisWeaponFinish_" + label };
            if (cached.HasProperty("_BaseColor"))
                cached.SetColor("_BaseColor", baseColor);
            else cached.color = baseColor;
            if (cached.HasProperty("_Metallic")) cached.SetFloat("_Metallic", metallic);
            if (cached.HasProperty("_Smoothness")) cached.SetFloat("_Smoothness", smoothness);
            cached.enableInstancing = true;
            return cached;
        }

        private static Material CreatePbrFinish(string label,
            string resourceFolder, string stem)
        {
            if (string.IsNullOrEmpty(resourceFolder) || string.IsNullOrEmpty(stem))
                return null;
            string cacheKey = resourceFolder + "/" + stem;
            if (pbrFinishCache.TryGetValue(cacheKey, out Material cached) &&
                cached != null)
                return cached;

            Texture2D baseColor = Resources.Load<Texture2D>(
                resourceFolder + "/" + stem + "_Base_Color");
            Texture2D normal = Resources.Load<Texture2D>(
                resourceFolder + "/" + stem + "_Normal");
            Texture2D metallicSmoothness = Resources.Load<Texture2D>(
                resourceFolder + "/" + stem + "_MetallicSmoothness");
            if (baseColor == null || normal == null || metallicSmoothness == null)
                return null;

            Shader shader = AarisShaderCache.Find("Aaris/Mobile Subject Lit");
            if (shader == null || !shader.isSupported)
                shader = AarisShaderCache.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader =
                AarisShaderCache.Find("Universal Render Pipeline/Simple Lit");
            if (shader == null) return null;
            var material = new Material(shader)
            {
                name = "AarisWeaponPBR_" + label,
                enableInstancing = true
            };
            if (material.HasProperty("_BaseMap"))
                material.SetTexture("_BaseMap", baseColor);
            else material.mainTexture = baseColor;
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", new Color(1.32f, 1.32f, 1.32f, 1f));
            if (material.HasProperty("_BumpMap"))
            {
                material.SetTexture("_BumpMap", normal);
                material.EnableKeyword("_NORMALMAP");
                if (material.HasProperty("_BumpScale"))
                    material.SetFloat("_BumpScale", 1f);
            }
            if (material.HasProperty("_MetallicGlossMap"))
            {
                material.SetTexture("_MetallicGlossMap", metallicSmoothness);
                material.EnableKeyword("_METALLICSPECGLOSSMAP");
                if (material.HasProperty("_Metallic"))
                    material.SetFloat("_Metallic", 1f);
                if (material.HasProperty("_Smoothness"))
                    material.SetFloat("_Smoothness", 1f);
                if (material.HasProperty("_SmoothnessTextureChannel"))
                    material.SetFloat("_SmoothnessTextureChannel", 0f);
            }
            if (material.HasProperty("_WorkflowMode"))
                material.SetFloat("_WorkflowMode", 1f);
            pbrFinishCache[cacheKey] = material;
            return material;
        }

        private static Material AuthoredPbrFinish(AarisWeaponSpec spec,
            Material source)
        {
            if (spec == null || string.IsNullOrEmpty(spec.modelResource))
                return null;
            string sourceName = source != null ? source.name : string.Empty;
            switch (spec.modelResource)
            {
                case "AarisWeapons/PBR/M4A1/M4A1":
                    return CreatePbrFinish("M4A1",
                        "AarisWeapons/PBR/M4A1", "M4A1");
                case "AarisWeapons/PBR/GreaseGun/Grease_Gun":
                    return CreatePbrFinish("GreaseGun",
                        "AarisWeapons/PBR/GreaseGun", "Grease_Gun");
                case "AarisWeapons/PBR/Shotgun/Shotgun":
                    return CreatePbrFinish("Shotgun",
                        "AarisWeapons/PBR/Shotgun", "Shotgun");
                case "AarisWeapons/PBR/BattleRifle/BattleRifle":
                    bool drum = sourceName.IndexOf("Drum",
                        System.StringComparison.OrdinalIgnoreCase) >= 0;
                    return CreatePbrFinish(drum ? "BattleRifle Drum" :
                            "BattleRifle Finish",
                        "AarisWeapons/PBR/BattleRifle",
                        drum ? "BattleRifle_Drum" : "BattleRifle_Finish");
                case "AarisWeapons/PBR/Sniper/Sniper":
                    bool optic = sourceName.IndexOf("Optic",
                        System.StringComparison.OrdinalIgnoreCase) >= 0;
                    return CreatePbrFinish(optic ? "Sniper Optic" :
                            "Sniper Finish",
                        "AarisWeapons/PBR/Sniper",
                        optic ? "Sniper_Optic" : "Sniper_Finish");
                default:
                    return null;
            }
        }

        private static Material WeaponFinish(AarisWeaponSpec spec)
        {
            string category = spec != null && spec.category != null
                ? spec.category.ToUpperInvariant() : "";
            string weaponId = spec != null && spec.id != null
                ? spec.id.ToUpperInvariant() : "";
            if (category.Contains("SMG"))
                return CreateWeaponFinish(ref smgFinish, "SMG",
                    new Color(.095f, .11f, .12f), .46f, .31f);
            if (category.Contains("SNIP"))
                return CreateWeaponFinish(ref sniperFinish, "Sniper",
                    new Color(.085f, .105f, .115f), .52f, .28f);
            if (category.Contains("SHOT"))
                return CreateWeaponFinish(ref shotgunFinish, "Shotgun",
                    new Color(.13f, .125f, .115f), .48f, .25f);
            if (weaponId.Contains("BATTLE"))
                return CreateWeaponFinish(ref battleFinish, "BattleRifle",
                    new Color(.12f, .125f, .12f), .58f, .27f);
            return CreateWeaponFinish(ref assaultFinish, "AssaultRifle",
                new Color(.105f, .115f, .115f), .55f, .29f);
        }

        private static Material ConvertedWeaponFinish(
            AarisWeaponSpec spec, Material source)
        {
            if (source == null) return WeaponFinish(spec);
            if (HasAuthoredTexture(source))
            {
                // Imported textured weapons can use legacy Standard shaders.
                // Preserve any already-correct URP material, and convert
                // others ONCE, with all authored texture maps and UVs intact.
                string shaderName = source.shader != null ?
                    source.shader.name : string.Empty;
                if (shaderName == "Universal Render Pipeline/Lit" ||
                    shaderName == "Universal Render Pipeline/Simple Lit")
                    return source;
                if (weaponFinishCache.TryGetValue(source, out Material textured) &&
                    textured != null) return textured;
                Shader urp = AarisShaderCache.Find("Universal Render Pipeline/Lit");
                if (urp == null) return source;
                var converted = new Material(urp)
                {
                    name = "AarisWeaponTexturedURP_" + source.name,
                    enableInstancing = true
                };
                string texProperty = source.HasProperty("_BaseMap") &&
                    source.GetTexture("_BaseMap") != null ?
                    "_BaseMap" : "_MainTex";
                Texture diffuse = source.HasProperty(texProperty) ?
                    source.GetTexture(texProperty) : source.mainTexture;
                if (diffuse != null && converted.HasProperty("_BaseMap"))
                {
                    converted.SetTexture("_BaseMap", diffuse);
                    if (source.HasProperty(texProperty))
                    {
                        converted.SetTextureScale("_BaseMap",
                            source.GetTextureScale(texProperty));
                        converted.SetTextureOffset("_BaseMap",
                            source.GetTextureOffset(texProperty));
                    }
                }
                Color tint = source.HasProperty("_BaseColor") ?
                    source.GetColor("_BaseColor") :
                    source.HasProperty("_Color") ? source.GetColor("_Color") :
                    Color.white;
                converted.SetColor("_BaseColor", tint);
                if (source.HasProperty("_BumpMap") &&
                    source.GetTexture("_BumpMap") != null)
                {
                    converted.SetTexture("_BumpMap", source.GetTexture("_BumpMap"));
                    if (source.HasProperty("_BumpScale") &&
                        converted.HasProperty("_BumpScale"))
                        converted.SetFloat("_BumpScale", Mathf.Clamp(
                            source.GetFloat("_BumpScale"), .2f, 1.5f));
                    converted.EnableKeyword("_NORMALMAP");
                }
                // If legacy art has baked cavity shading, retain it instead
                // of turning concave bodywork into shiny flat-looking metal.
                // No new texture is created; this is a one-time shared
                // conversion used by both held and floor loot visuals.
                if (source.HasProperty("_OcclusionMap") &&
                    source.GetTexture("_OcclusionMap") != null &&
                    converted.HasProperty("_OcclusionMap"))
                {
                    converted.SetTexture("_OcclusionMap",
                        source.GetTexture("_OcclusionMap"));
                    converted.EnableKeyword("_OCCLUSIONMAP");
                    if (source.HasProperty("_OcclusionStrength") &&
                        converted.HasProperty("_OcclusionStrength"))
                        converted.SetFloat("_OcclusionStrength", Mathf.Clamp01(
                            source.GetFloat("_OcclusionStrength")));
                }
                if (source.HasProperty("_MetallicGlossMap") &&
                    source.GetTexture("_MetallicGlossMap") != null)
                {
                    converted.SetTexture("_MetallicGlossMap",
                        source.GetTexture("_MetallicGlossMap"));
                    converted.EnableKeyword("_METALLICSPECGLOSSMAP");
                }
                converted.SetFloat("_Metallic", source.HasProperty("_Metallic")
                    ? Mathf.Clamp01(source.GetFloat("_Metallic")) : .18f);
                converted.SetFloat("_Smoothness", source.HasProperty("_Smoothness")
                    ? Mathf.Clamp01(source.GetFloat("_Smoothness")) :
                    source.HasProperty("_Glossiness") ?
                    Mathf.Clamp01(source.GetFloat("_Glossiness")) : .34f);
                weaponFinishCache[source] = converted;
                return converted;
            }
            if (weaponFinishCache.TryGetValue(source, out Material cached) &&
                cached != null) return cached;

            Material fallback = WeaponFinish(spec);
            Shader shader = AarisShaderCache.Find("Aaris/Mobile Subject Lit");
            if (shader == null || !shader.isSupported)
                shader = AarisShaderCache.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader =
                AarisShaderCache.Find("Universal Render Pipeline/Simple Lit");
            if (shader == null) return fallback;

            Color authored = Color.white;
            if (source.HasProperty("_BaseColor"))
                authored = source.GetColor("_BaseColor");
            else if (source.HasProperty("_Color"))
                authored = source.GetColor("_Color");
            float spread = Mathf.Max(authored.r, Mathf.Max(authored.g, authored.b)) -
                Mathf.Min(authored.r, Mathf.Min(authored.g, authored.b));
            float brightness = Mathf.Max(authored.r,
                Mathf.Max(authored.g, authored.b));
            Color baseColor = (spread > .025f || brightness < .82f)
                ? authored : fallback != null && fallback.HasProperty("_BaseColor")
                    ? fallback.GetColor("_BaseColor") : new Color(.11f,.12f,.12f,1f);

            string label = source.name.ToLowerInvariant();
            bool polymer = label.Contains("grip") || label.Contains("stock") ||
                label.Contains("poly") || label.Contains("rubber") ||
                label.Contains("plastic") || label.Contains("brown") ||
                label.Contains("wood") || label.Contains("bole");
            bool optic = label.Contains("scope") || label.Contains("glass") ||
                label.Contains("lens");
            bool metal = label.Contains("metal") || label.Contains("barrel") ||
                label.Contains("receiver") || label.Contains("rail") ||
                label.Contains("bolt") || label.Contains("trigger");
            float metallic = polymer ? .03f : optic ? .12f : metal ? .68f : .42f;
            float smoothness = polymer ? .20f : optic ? .70f : metal ? .30f : .26f;

            var result = new Material(shader)
            {
                name = "AarisWeaponSlot_" + source.name,
                enableInstancing = true
            };
            if (result.HasProperty("_BaseColor"))
                result.SetColor("_BaseColor", baseColor);
            else result.color = baseColor;
            if (result.HasProperty("_Metallic"))
                result.SetFloat("_Metallic", metallic);
            if (result.HasProperty("_Smoothness"))
                result.SetFloat("_Smoothness", smoothness);
            weaponFinishCache[source] = result;
            return result;
        }

        // The CC0 M4A1 source is authored with barrel along local -Z
        // (native import audit: Barrel z=-32.25, Stock z=+21.52), while all
        // gameplay grip/muzzle math deliberately uses logical +Z. Correct only
        // the imported visual child so IK, muzzle and support-rail axes remain
        // stable for every weapon.
        internal static Quaternion ImportedModelRotation(AarisWeaponSpec spec)
        {
            bool reverseForward = spec != null &&
                (spec.modelResource == "AarisWeapons/PBR/M4A1/M4A1" ||
                 spec.modelResource == "AarisWeapons/PBR/GreaseGun/Grease_Gun" ||
                 spec.modelResource == "AarisWeapons/PBR/Shotgun/Shotgun" ||
                 spec.modelResource == "AarisWeapons/PBR/BattleRifle/BattleRifle" ||
                 spec.modelResource == "AarisWeapons/PBR/Sniper/Sniper");
            return reverseForward
                ? Quaternion.Euler(0f, 180f, 0f)
                : Quaternion.identity;
        }

        private static Material GroundPickupFinish(Material finish)
        {
            if (groundFinishCache.TryGetValue(finish, out var cached) && cached != null)
                return cached;
            Shader subjectShader = AarisShaderCache.Find("Aaris/Mobile Subject Lit");
            if (subjectShader == null || finish.shader != subjectShader ||
                finish.GetFloat("_Surface") > .5f)
                return finish;

            // Retain the authored metal and polymer response. The shared
            // subject shader already supplies a restrained grazing lobe.
            var ground = new Material(finish) { name = finish.name + " Ground" };
            Color tint = finish.GetColor("_BaseColor");
            ground.SetColor("_BaseColor", new Color(tint.r * 1.24f,
                tint.g * 1.24f, tint.b * 1.24f, tint.a));
            ground.SetFloat("_Smoothness", finish.GetFloat("_Smoothness") * .93f);
            ground.SetFloat("_ClearCoatMask", .17f);
            ground.SetFloat("_ClearCoatSmoothness", .45f);
            groundFinishCache[finish] = ground;
            return ground;
        }

        internal static void ApplyTextureAwareWeaponFinish(AarisWeaponSpec spec,
            Renderer[] renderers, bool groundPickup = false)
        {
            if (renderers == null || renderers.Length == 0) return;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                Material[] source = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < source.Length; i++)
                {
                    Material finish = AuthoredPbrFinish(spec, source[i]) ??
                        ConvertedWeaponFinish(spec, source[i]);
                    if (finish == null) continue;
                    if (groundPickup) finish = GroundPickupFinish(finish);
                    if (finish == source[i]) continue;
                    source[i] = finish;
                    changed = true;
                }
                if (changed) renderer.sharedMaterials = source;
            }
        }

        private void Awake()
        {
            combat = GetComponent<AarisBattleActions>();
            motor = GetComponent<AarisThirdPersonPlayer>();
            capsule = GetComponent<CharacterController>();
        }

        public void BindLootWorld(AarisWeaponLootWorld world) => lootWorld = world;

        // Both touchscreen and keyboard actions share the same on-foot authority.
        // Paused time, suspended combat and vehicle seats must not mutate ammo,
        // stances or floor loot through a still-visible HUD callback.
        private bool AcceptsPlayerWeaponInput =>
            isActiveAndEnabled && Time.timeScale > 0f &&
            motor != null && motor.isActiveAndEnabled &&
            (combat == null || combat.isActiveAndEnabled);

        public void RequestPickup()
        {
            if (!AcceptsPlayerWeaponInput) return;
            // One PICK button for an actual nearby bot death crate or floor gun.
            // Never consume both in the same tap.
            if (AarisDeathCrateWorld.TryLootNearest(transform, this)) return;
            // Keep the same exact on-foot authority on the existing floor path.
            if (AcceptsPlayerWeaponInput && lootWorld != null)
                lootWorld.RequestPickup();
        }

        // No physical query or FBX load during the 0.25s crate hint scan.
        // Two occupied slots need an active floor world to preserve the
        // outgoing weapon before accepting another.
        internal bool CanPickupDeathCrateGun =>
            AcceptsPlayerWeaponInput &&
            (!RequiresExchangeDrop ||
             (lootWorld != null && lootWorld.isActiveAndEnabled));

        internal bool CanReceiveDeathCrateAmmo(AarisWeaponSpec spec)
        {
            if (!AcceptsPlayerWeaponInput || spec == null) return false;
            return (equipped != null && equipped.Spec != null &&
                    equipped.Spec.id == spec.id && equipped.HasReserveSpace) ||
                   (stowed != null && stowed.Spec != null &&
                    stowed.Spec.id == spec.id && stowed.HasReserveSpace);
        }

        internal bool TryPickupDeathCrateGun(AarisWeaponState incoming)
        {
            if (!CanPickupDeathCrateGun || incoming == null) return false;
            if (RequiresExchangeDrop)
                return lootWorld != null && lootWorld.TryExchangeDeathCrateGun(incoming);
            return TryPickup(incoming, out AarisWeaponState displaced) &&
                displaced == null;
        }

        internal int AddDeathCrateAmmo(AarisWeaponSpec spec, int amount)
        {
            if (!AcceptsPlayerWeaponInput || spec == null || amount <= 0) return 0;
            int added = 0;
            // Two slots can carry the same type. Fill any remaining capacity
            // in the back slot when the equipped gun's reserve reaches its cap.
            if (equipped != null && equipped.Spec != null &&
                equipped.Spec.id == spec.id)
                added += equipped.AddReserve(amount);
            if (stowed != null && stowed.Spec != null &&
                stowed.Spec.id == spec.id && added < amount)
                added += stowed.AddReserve(amount - added);
            return added;
        }

        public bool DropCurrent() => AcceptsPlayerWeaponInput &&
            lootWorld != null && lootWorld.TryDropEquipped();

        private static void SetHierarchyLayer(GameObject root, int layer)
        {
            if (root == null) return;
            foreach (Transform node in root.GetComponentsInChildren<Transform>(true))
                node.gameObject.layer = layer;
        }

        /// <summary>
        /// An atomic exchange. Preload the incoming FBX before detaching the
        /// existing gun. The previous gun is returned as a complete state
        /// snapshot so the world can leave it on the ground.
        /// </summary>
        public bool TryPickup(AarisWeaponState incoming, out AarisWeaponState dropped)
        {
            dropped = null;
            if (incoming == null || incoming.Spec == null || !incoming.Spec.Valid)
                return false;
            GameObject prefab = AarisWeaponCatalog.ModelFor(incoming.Spec);
            if (prefab == null) return false;

            var root = new GameObject("Aaris Equipped " + incoming.Spec.id);
            root.transform.SetParent(transform, false);
            var visible = Instantiate(prefab, root.transform);
            // Held/back-stowed weapons are visual children of this player.
            // Keep their render layer with the owner so player-only cameras
            // (portrait QA, scoped player passes) cannot silently cull the gun.
            // Imported weapon colliders are disabled immediately below, so
            // this does not make the visual participate in player collision.
            SetHierarchyLayer(root, gameObject.layer);
            // A multi-mesh FBX needs the bounds of ALL rendered parts.
            // Get the renderers once; this runs only when a weapon is picked up.
            var renderers = visible.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                Destroy(root);
                Debug.LogError("Aaris: gun has no visible mesh: " + incoming.Spec.id);
                return false;
            }
            foreach (var collider in visible.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;
            ApplyTextureAwareWeaponFinish(incoming.Spec, renderers);
            var mesh = visible.transform;
            mesh.localPosition = Vector3.zero;
            mesh.localRotation = ImportedModelRotation(incoming.Spec);
            // Renderer.bounds is an asynchronously updated world-space AABB.
            // Reading it again immediately after scaling the FBX caused
            // 5-9 metre guns in a real Unity Play Mode audit on the Android
            // character. Measure the source MESH corners instead and scale
            // those root-local bounds mathematically (one-off equip only).
            if (!AarisWeaponMeshBounds.TryMeasure(root.transform,
                renderers, out Bounds combinedBounds))
            {
                Destroy(root);
                Debug.LogError("Aaris: weapon has invalid mesh geometry: " +
                    incoming.Spec.id);
                return false;
            }
            float longest = Mathf.Max(combinedBounds.size.x,
                combinedBounds.size.y, combinedBounds.size.z);
            float scale = Mathf.Clamp(
                incoming.Spec.presentationLengthMetres / longest,
                0.0000001f, 10f) * incoming.Spec.handScale;
            mesh.localScale *= scale;
            // Keep the entire gun body centered a short distance forward
            // from the animated right wrist, regardless of imported FBX pivot.
            // Keep the receiver/trigger near the grip for all five real-size FBXs.
            Vector3 modelCenter = new Vector3(0f, -0.025f,
                incoming.Spec.presentationLengthMetres * 0.22f);
            mesh.localPosition = modelCenter - combinedBounds.center * scale;

            var next = root.AddComponent<AarisEquippedGun>();
            next.ModelLocalBounds = new Bounds(modelCenter, combinedBounds.size * scale);
            next.Configure(incoming);
            if (!next.Configured)
            {
                Destroy(root);
                return false;
            }

            // Commit only after the new visible FBX and weapon state are valid.
            // First replacement fills the spare/back slot without losing ammo.
            // A full inventory retains its spare and drops just the old hand gun.
            bool full = equipped != null && stowed != null;
            dropped = full ? equipped.CaptureState() : null;
            Transform removedRoot = full ? weaponRoot : null;
            if (!full && equipped != null)
            {
                stowed = equipped;
                stowedRoot = weaponRoot;
                stowed.enabled = false; // Stowed reload resolves when drawn.
            }
            ResetCombatTransition();
            equipped = next;
            weaponRoot = root.transform;
            equipped.enabled = true;
            fistMode = false;
            if (combat != null) combat.Equip(equipped);
            AttachModelsToSockets();
            if (removedRoot != null)
            {
                removedRoot.gameObject.SetActive(false);
                Destroy(removedRoot.gameObject);
            }
            Debug.Log("Aaris: equipped " + next.DisplayName +
                (dropped != null ? " (replaced hand gun dropped)" :
                 stowed != null ? " (previous gun on back)" : ""));
            return true;
        }

        /// <summary>
        /// Called only after the world validates a physical floor and
        /// reserves a drop pickup. Cannot lose a gun on invalid terrain.
        /// </summary>
        public bool TryRemove(out AarisWeaponState removed)
        {
            removed = equipped != null ? equipped.CaptureState() : null;
            if (removed == null) return false;
            ResetCombatTransition();
            Transform discarded = weaponRoot;
            equipped = stowed;
            weaponRoot = stowedRoot;
            stowed = null;
            stowedRoot = null;
            fistMode = false;
            if (equipped != null) equipped.enabled = true;
            if (combat != null) combat.Equip(equipped);
            if (discarded != null)
            {
                discarded.gameObject.SetActive(false);
                Destroy(discarded.gameObject);
            }
            AttachModelsToSockets();
            return true;
        }

        public bool Reload() => AcceptsPlayerWeaponInput &&
            HasGun && equipped.BeginReload();

        /// <summary>Unarmed punching without deleting either carried FBX.</summary>
        public bool ToggleFists()
        {
            if (!AcceptsPlayerWeaponInput || equipped == null) return false;
            ResetCombatTransition();
            fistMode = !fistMode;
            equipped.enabled = !fistMode;
            if (combat != null) combat.Equip(fistMode ? null : equipped);
            AttachModelsToSockets();
            return true;
        }

        /// <summary>Swap hand and back slots; preserve reload/ammo exactly.</summary>
        public bool SwapWeapons()
        {
            if (!AcceptsPlayerWeaponInput || stowed == null || equipped == null)
                return false;
            ResetCombatTransition();
            AarisEquippedGun oldGun = equipped;
            equipped = stowed;
            stowed = oldGun;
            Transform oldRoot = weaponRoot;
            weaponRoot = stowedRoot;
            stowedRoot = oldRoot;
            fistMode = false;
            equipped.enabled = true;
            stowed.enabled = false;
            equipped.RefreshReload();
            if (combat != null) combat.Equip(equipped);
            AttachModelsToSockets();
            return true;
        }

        public void ToggleAim()
        {
            if (!AcceptsPlayerWeaponInput) return;
            aiming = !aiming && HasGun;
            if (aiming && motor != null) motor.CancelAutoSprint();
            if (motor != null)
                motor.SetWeaponAimFov(aiming ? equipped.ScopeFov : -1f);
        }

        private void ResetCombatTransition()
        {
            if (combat != null) combat.SetFireHeld(false);
            CancelAim();
        }

        internal void CancelAim()
        {
            aiming = false;
            if (motor != null) motor.SetWeaponAimFov(-1f);
        }

        private void FindHumanoidSockets()
        {
            if (hand != null && chest != null && leftUpperArm != null &&
                leftForearm != null && leftHand != null &&
                rightUpperArm != null && rightForearm != null &&
                fingerRigResolved) return;
            if (Time.unscaledTime < nextRigSearch) return;
            nextRigSearch = Time.unscaledTime + 1f;
            GetComponentsInChildren(true, socketAnimators);
            foreach (var animator in socketAnimators)
            {
                if (animator == null || animator.avatar == null ||
                    !animator.avatar.isValid || !animator.avatar.isHuman) continue;
                hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                chest = animator.GetBoneTransform(HumanBodyBones.UpperChest);
                if (chest == null) chest = animator.GetBoneTransform(HumanBodyBones.Chest);
                if (chest == null) chest = animator.GetBoneTransform(HumanBodyBones.Spine);
                hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                leftUpperArm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                leftForearm = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
                leftThumb = animator.GetBoneTransform(HumanBodyBones.LeftThumbProximal);
                leftMiddle = animator.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
                rightUpperArm = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                rightForearm = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
                rightGripDigits[0] = animator.GetBoneTransform(HumanBodyBones.RightIndexProximal);
                rightGripDigits[1] = animator.GetBoneTransform(HumanBodyBones.RightIndexIntermediate);
                rightGripDigits[2] = animator.GetBoneTransform(HumanBodyBones.RightIndexDistal);
                rightGripDigits[3] = animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
                rightGripDigits[4] = animator.GetBoneTransform(HumanBodyBones.RightMiddleIntermediate);
                rightGripDigits[5] = animator.GetBoneTransform(HumanBodyBones.RightMiddleDistal);
                rightGripDigits[6] = animator.GetBoneTransform(HumanBodyBones.RightRingProximal);
                rightGripDigits[7] = animator.GetBoneTransform(HumanBodyBones.RightRingIntermediate);
                rightGripDigits[8] = animator.GetBoneTransform(HumanBodyBones.RightRingDistal);
                rightGripDigits[9] = animator.GetBoneTransform(HumanBodyBones.RightLittleProximal);
                rightGripDigits[10] = animator.GetBoneTransform(HumanBodyBones.RightLittleIntermediate);
                rightGripDigits[11] = animator.GetBoneTransform(HumanBodyBones.RightLittleDistal);
                leftGripDigits[0] = animator.GetBoneTransform(HumanBodyBones.LeftIndexProximal);
                leftGripDigits[1] = animator.GetBoneTransform(HumanBodyBones.LeftIndexIntermediate);
                leftGripDigits[2] = animator.GetBoneTransform(HumanBodyBones.LeftIndexDistal);
                leftGripDigits[3] = animator.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
                leftGripDigits[4] = animator.GetBoneTransform(HumanBodyBones.LeftMiddleIntermediate);
                leftGripDigits[5] = animator.GetBoneTransform(HumanBodyBones.LeftMiddleDistal);
                leftGripDigits[6] = animator.GetBoneTransform(HumanBodyBones.LeftRingProximal);
                leftGripDigits[7] = animator.GetBoneTransform(HumanBodyBones.LeftRingIntermediate);
                leftGripDigits[8] = animator.GetBoneTransform(HumanBodyBones.LeftRingDistal);
                leftGripDigits[9] = animator.GetBoneTransform(HumanBodyBones.LeftLittleProximal);
                leftGripDigits[10] = animator.GetBoneTransform(HumanBodyBones.LeftLittleIntermediate);
                leftGripDigits[11] = animator.GetBoneTransform(HumanBodyBones.LeftLittleDistal);
                fingerRigResolved = true;
                if (hand != null) break;
            }
            socketAnimators.Clear();
        }

        private Pose AnimatedTorso()
        {
            // The live shoulder and hip landmarks define the body axes
            // regardless of the avatar's bind axes or stance at first equip.
            if (chest != null && hips != null && leftUpperArm != null && rightUpperArm != null)
            {
                Vector3 side = (rightUpperArm.position - leftUpperArm.position).normalized;
                Vector3 up = Vector3.ProjectOnPlane(chest.position - hips.position, side);
                Vector3 forward = Vector3.Cross(side, up);
                if (forward.sqrMagnitude > 0.0001f)
                    return new Pose(chest.position, Quaternion.LookRotation(forward, up));
            }
            float eye = combat != null ? combat.CameraEyeHeight : 1.45f;
            float pitch = Mathf.Lerp(74f, 0f, Mathf.InverseLerp(0.48f, 1.35f, eye));
            return new Pose(transform.position + Vector3.up * (eye - 0.12f),
                transform.rotation * Quaternion.Euler(pitch, 0f, 0f));
        }

        private void PlaceOnBack(AarisEquippedGun gun, Pose torso, bool rightShoulder)
        {
            if (gun == null) return;
            // Position the model's measured centre. These offsets describe
            // clearance to the inner face of the weapon, in torso metres.
            float across, down, depth;
            switch (gun.Spec.category)
            {
                case "Pistol": across = 0.45f; down = 0.16f; depth = 0.174f; break;
                case "SMG": across = 0.49f; down = 0.18f; depth = 0.188f; break;
                case "Shotgun": across = 0.60f; down = 0.24f; depth = 0.212f; break;
                case "Sniper": across = 0.64f; down = 0.26f; depth = 0.224f; break;
                case "DMR": across = 0.59f; down = 0.23f; depth = 0.213f; break;
                default: across = 0.56f; down = 0.22f; depth = 0.201f; break;
            }
            float side = rightShoulder ? 1f : -1f;
            Vector3 barrel = new Vector3(-side * across, -Mathf.Sqrt(1f - across * across), 0f);
            // The FBX barrel is +Z and the magazine points away from the back.
            Quaternion rotation = torso.rotation * Quaternion.LookRotation(barrel, Vector3.forward);
            Vector3 front = torso.rotation * Vector3.forward;
            Bounds bounds = gun.ModelLocalBounds;
            if (!rightShoulder && stowed != null)
            {
                // Separate the actual crossed model volumes, including long
                // magazines in the forward slot.
                depth += stowed.ModelLocalBounds.size.y + 0.046f;
            }
            depth += ProjectedRadius(bounds, rotation, front);
            Vector3 center = torso.position + torso.rotation * new Vector3(side * 0.024f, -down, -depth);
            WriteSocket(gun, torso, center - rotation * bounds.center, rotation,
                rightShoulder ? SocketKind.RightBack : SocketKind.LeftBack);
            KeepSocketAboveGround(gun);
        }

        private static float ProjectedRadius(Bounds bounds, Quaternion rotation, Vector3 normal)
        {
            Vector3 axis = Quaternion.Inverse(rotation) * normal;
            Vector3 size = bounds.extents;
            return Mathf.Abs(axis.x) * size.x + Mathf.Abs(axis.y) * size.y + Mathf.Abs(axis.z) * size.z;
        }

        private void WriteSocket(AarisEquippedGun gun, Pose torso, Vector3 target,
            Quaternion rotation, SocketKind kind)
        {
            Transform item = gun.transform;
            if (item.parent != transform) item.SetParent(transform, false);
            ref SocketMotion motion = ref gun.PresentationSocket;
            Bounds bounds = gun.ModelLocalBounds;
            Quaternion intoTorso = Quaternion.Inverse(torso.rotation);
            if (motion.Kind != kind)
            {
                motion.Active = motion.Kind != SocketKind.Unplaced;
                motion.AroundShoulders = motion.Active &&
                    (motion.Kind == SocketKind.Hands || kind == SocketKind.Hands);
                motion.Kind = kind;
                motion.Started = Time.time;
                motion.Progress = 0f;
                motion.StartCenter = intoTorso * (item.TransformPoint(bounds.center) - torso.position);
                motion.StartRotation = intoTorso * item.rotation;
                // An input edge must start from the current visible pose.
                // The following evaluated frames move it around the body.
                if (motion.Active) return;
            }
            if (motion.Active)
            {
                float seconds = kind == SocketKind.Hands ? DrawSeconds : StowSeconds;
                motion.Progress = Mathf.Clamp01((Time.time - motion.Started) / seconds);
                motion.Active = motion.Progress < 1f;
            }
            if (!motion.Active)
            {
                item.SetPositionAndRotation(target, rotation);
                return;
            }

            float t = Mathf.SmoothStep(0f, 1f, motion.Progress);
            Vector3 finish = intoTorso * (target + rotation * bounds.center - torso.position);
            Vector3 center = Vector3.Lerp(motion.StartCenter, finish, t);
            Quaternion orientation = torso.rotation * Quaternion.Slerp(
                motion.StartRotation, intoTorso * rotation, t);
            if (motion.AroundShoulders)
            {
                // Draw via the right shoulder and stow via the left. The
                // moving torso owns the arc, so running cannot leave it behind.
                float side = kind == SocketKind.Hands ? 1f : -1f;
                Vector3 from = motion.StartCenter;
                float fromAngle = Mathf.Atan2(from.x, from.z) * Mathf.Rad2Deg;
                float toAngle = Mathf.Atan2(finish.x, finish.z) * Mathf.Rad2Deg;
                float angle = t <= 0.5f ? Mathf.LerpAngle(fromAngle, side * 90f, t * 2f) :
                    Mathf.LerpAngle(side * 90f, toAngle, (t - 0.5f) * 2f);
                angle *= Mathf.Deg2Rad;
                Vector3 outward = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
                float radius = Mathf.Lerp(new Vector2(from.x, from.z).magnitude,
                    new Vector2(finish.x, finish.z).magnitude, t);
                float clearanceBlend = Mathf.SmoothStep(0f, 1f, Mathf.Min(t, 1f - t) * 4f);
                float clearRadius = 0.30f + ProjectedRadius(bounds, orientation, torso.rotation * outward);
                radius = Mathf.Lerp(radius, Mathf.Max(radius, clearRadius), clearanceBlend);
                center = outward * radius + Vector3.up * (center.y + clearanceBlend * 0.075f);
            }

            item.SetPositionAndRotation(torso.position + torso.rotation * center -
                orientation * bounds.center, orientation);
        }

        private bool TryGetGroundedProneSupport(out Vector3 point, out Vector3 normal)
        {
            point = normal = Vector3.zero;
            return combat != null && capsule != null && capsule.enabled && capsule.isGrounded &&
                motor != null && motor.isActiveAndEnabled &&
                combat.MovementStance == AarisBattleActions.PlayerStance.Prone &&
                combat.TryGetProneSupportPlane(out point, out normal);
        }

        private void KeepSocketAboveGround(AarisEquippedGun gun)
        {
            SocketMotion motion = gun.PresentationSocket;
            if (motion.Active && motion.Progress <= 0f) return;
            if (!TryGetGroundedProneSupport(out Vector3 floor, out Vector3 normal)) return;
            Transform item = gun.transform;
            float clearance = WeaponGroundGap(gun, floor, normal);
            // Keep the silhouette clear while easing in the settled margin.
            // A rifle already clear of the floor should not jump on input.
            float margin = motion.Active ? Mathf.SmoothStep(0f, 0.020f, motion.Progress) : 0.020f;
            if (clearance < margin) item.position += normal * (margin - clearance);
        }

        private static float WeaponGroundGap(AarisEquippedGun gun, Vector3 floor, Vector3 normal)
        {
            Transform item = gun.transform;
            Bounds bounds = gun.ModelLocalBounds;
            return Vector3.Dot(item.TransformPoint(bounds.center) - floor, normal) -
                ProjectedRadius(bounds, item.rotation, normal);
        }

        // Chest anchoring and two-hand IK must use the SAME rig contract.
        // A partially imported Humanoid can expose a right hand/chest but
        // miss left-arm bones. In that case follow the real right-hand
        // transform instead of leaving a chest-anchored floating firearm.
        // Only cached Transform checks: no new bone lookup per frame.
        private bool HasCompleteHumanoidGripRig()
        {
            return chest != null && hand != null &&
                rightUpperArm != null && rightForearm != null &&
                leftUpperArm != null && leftForearm != null && leftHand != null;
        }

        private void AttachModelsToSockets()
        {
            if (equipped == null || weaponRoot == null) return;
            FindHumanoidSockets();
            Pose torso = AnimatedTorso();
            if (stowedRoot != null) PlaceOnBack(stowed, torso, true);
            if (fistMode || IsSprintHolstered)
            {
                PlaceOnBack(equipped, torso, stowedRoot == null);
                return;
            }
            if (weaponRoot.parent != transform)
                weaponRoot.SetParent(transform, false);
            // All five reviewed rifle/SMG FBXs point down local +Z.
            // Anchor the receiver just ahead of the chest, not to the
            // animated pistol wrist: the barrel must follow the camera
            // when firing/ADS/prone, regardless of the body animation.
            if (HasCompleteHumanoidGripRig())
            {
                Vector3 forward = transform.forward;
                Camera view = motor != null ? motor.AimCamera : null;
                if (view != null && (aiming || (combat != null &&
                    (combat.FireHeld ||
                     combat.Stance == AarisBattleActions.PlayerStance.Prone))))
                    forward = view.transform.forward;
                if (forward.sqrMagnitude < 0.01f) forward = transform.forward;
                Quaternion aimRotation = Quaternion.LookRotation(forward, Vector3.up);
                // A sprinting, armed Humanoid keeps the gun in both hands,
                // lowered slightly rather than teleporting it to the back.
                // Never apply this presentation pitch during ADS or firing.
                if (sprintReadyBlend > 0.001f)
                    aimRotation *= Quaternion.Euler(11f * sprintReadyBlend, 0f, 0f);
                // Rotation and receiver translation share the same aim-yaw basis.
                // Otherwise the solved hands chase a diagonal pivot during
                // camera yaw, especially in ADS/prone.
                Vector3 aimRight = aimRotation * Vector3.right;
                Vector3 shoulderForward = Vector3.ProjectOnPlane(
                    forward, Vector3.up);
                if (shoulderForward.sqrMagnitude < 0.01f)
                    shoulderForward = transform.forward;
                else
                    shoulderForward.Normalize();
                float aimPitch = Mathf.Clamp(Vector3.Dot(
                    forward.normalized, Vector3.up), -0.75f, 0.75f);
                Vector3 receiver = chest.position +
                    aimRight * 0.12f + shoulderForward * 0.13f -
                    Vector3.up * 0.105f + Vector3.up * aimPitch * 0.025f;
                receiver -= sprintReadyBlend *
                    (shoulderForward * 0.035f + Vector3.up * 0.08f);
                receiver = ReachableReceiver(receiver, aimRotation);
                WriteSocket(equipped, torso, receiver, aimRotation, SocketKind.Hands);
            }
            else
            {
                // Missing Humanoid avatar: retain the working legacy grip
                // instead of hiding/dropping an otherwise usable weapon.
                Quaternion grip = Quaternion.Euler(equipped.Spec.handRotation);
                Vector3 handPosition = hand != null ? hand.position + hand.rotation * equipped.Spec.handOffset :
                    transform.TransformPoint(new Vector3(.29f, 1.1f, .3f));
                Quaternion gripRotation = hand != null ? hand.rotation * grip : transform.rotation * grip;
                WriteSocket(equipped, torso, handPosition, gripRotation, SocketKind.Hands);
            }
            if (equipped.RequiresVisualDraw || !HasCompleteHumanoidGripRig())
                KeepSocketAboveGround(equipped);
        }

        // No per-frame allocations or Animation Rigging dependency: the
        // existing Humanoid bones form analytic shoulder/elbow/wrist chains.
        // Both wrists are solved AFTER the PlayableGraph (execution order +310).
        private static void ReachArm(Transform upper, Transform forearm,
            Transform wrist, Vector3 desired, Vector3 fallbackPole,
            Vector3 supportPoint, Vector3 supportNormal)
        {
            if (upper == null || forearm == null || wrist == null) return;
            Vector3 shoulder = upper.position;
            Vector3 elbow = forearm.position;
            float upperLength = Vector3.Distance(shoulder, elbow);
            float foreLength = Vector3.Distance(elbow, wrist.position);
            if (upperLength < 0.05f || foreLength < 0.05f) return;
            Vector3 toGrip = desired - shoulder;
            float rawDistance = toGrip.magnitude;
            if (rawDistance < 0.05f) return;
            Vector3 direction = toGrip / rawDistance;
            float reach = Mathf.Clamp(rawDistance,
                Mathf.Abs(upperLength - foreLength) + 0.01f,
                Mathf.Max(0.06f, upperLength + foreLength - 0.015f));
            Vector3 target = shoulder + direction * reach;
            float along = (upperLength * upperLength -
                foreLength * foreLength + reach * reach) / (2f * reach);
            float perpendicular = Mathf.Sqrt(Mathf.Max(0f,
                upperLength * upperLength - along * along));
            Vector3 pole = Vector3.ProjectOnPlane(elbow - shoulder, direction);
            if (pole.sqrMagnitude < 0.0001f)
                pole = Vector3.ProjectOnPlane(fallbackPole, direction);
            if (pole.sqrMagnitude < 0.0001f) return;
            pole.Normalize();
            Vector3 elbowCenter = shoulder + direction * along;
            if (supportNormal.sqrMagnitude > 0.5f && perpendicular > 0.0001f)
            {
                // Every exact-length elbow lies on this bend circle. Select
                // the nearest bend direction whose normal height clears the
                // real support plane; never move an elbow independently and
                // stretch either segment or detach the wrist from its grip.
                Vector3 circleUp = Vector3.ProjectOnPlane(supportNormal, direction);
                float normalSpan = circleUp.magnitude;
                if (normalSpan > 0.0001f)
                {
                    circleUp /= normalSpan;
                    float centerGap = Vector3.Dot(elbowCenter - supportPoint, supportNormal);
                    float requiredUp = Mathf.Clamp((0.046f - centerGap) /
                        (perpendicular * normalSpan), -1f, 1f);
                    float highestBracedUp = Mathf.Clamp((0.115f - centerGap) /
                        (perpendicular * normalSpan), -1f, 1f);
                    float currentUp = Mathf.Clamp(Vector3.Dot(pole, circleUp), -1f, 1f);
                    float bracedUp = Mathf.Clamp(currentUp, requiredUp, highestBracedUp);
                    if (Mathf.Abs(bracedUp - currentUp) > 0.001f)
                    {
                        Vector3 tangent = pole - circleUp * currentUp;
                        if (tangent.sqrMagnitude < 0.000001f)
                            tangent = Vector3.Cross(direction, circleUp);
                        tangent.Normalize();
                        pole = circleUp * bracedUp + tangent *
                            Mathf.Sqrt(Mathf.Max(0f, 1f - bracedUp * bracedUp));
                    }
                }
            }
            Vector3 targetElbow = elbowCenter + pole * perpendicular;
            Vector3 oldUpper = forearm.position - shoulder;
            Vector3 newUpper = targetElbow - shoulder;
            if (oldUpper.sqrMagnitude < 0.001f || newUpper.sqrMagnitude < 0.001f)
                return;
            upper.rotation = Quaternion.FromToRotation(oldUpper, newUpper) *
                upper.rotation;
            Vector3 oldForearm = wrist.position - forearm.position;
            Vector3 newForearm = target - forearm.position;
            if (oldForearm.sqrMagnitude < 0.001f ||
                newForearm.sqrMagnitude < 0.001f) return;
            forearm.rotation = Quaternion.FromToRotation(oldForearm, newForearm) *
                forearm.rotation;
        }

        // The receiver is camera/chest anchored, so on a differently
        // proportioned Humanoid the trigger point can sit beyond the right
        // arm's physical reach. The old arm solver correctly clamped its
        // chain but left a visible gap between palm and gun. Translate only
        // the weapon root, by at most 12 cm, toward the shoulder until the
        // trigger is reachable; rotation/aim direction remains untouched.
        private Vector3 ReachableReceiver(Vector3 receiver, Quaternion rotation)
        {
            if (weaponRoot == null || hand == null || rightUpperArm == null ||
                rightForearm == null) return receiver;
            float upperLength = Vector3.Distance(
                rightUpperArm.position, rightForearm.position);
            float lowerLength = Vector3.Distance(
                rightForearm.position, hand.position);
            if (upperLength < 0.05f || lowerLength < 0.05f) return receiver;
            Vector3 trigger = receiver + rotation * new Vector3(0.015f, -0.045f, -0.045f);
            Vector3 fromShoulder = trigger - rightUpperArm.position;
            float distance = fromShoulder.magnitude;
            float maximumReach = upperLength + lowerLength - 0.025f;
            if (distance <= maximumReach || distance < 0.001f) return receiver;
            float correction = Mathf.Min(distance - maximumReach, 0.12f);
            return receiver - fromShoulder / distance * correction;
        }

        // A fixed foregrip point on a long sniper/rifle can lie beyond the
        // left arm's actual Humanoid reach, leaving the palm visibly detached.
        // Intersect the gun's forward grip rail with the arm's reach sphere;
        // slide only toward the receiver, never outside the existing rail.
        // Pure pose math: no raycasts, allocations, or extra components.
        private static float ReachableSupportGripZ(Transform upper,
            Transform forearm, Transform wrist, Transform gun, float preferredZ)
        {
            Vector3 shoulder = upper.position;
            float upperLength = Vector3.Distance(shoulder, forearm.position);
            float lowerLength = Vector3.Distance(forearm.position, wrist.position);
            if (upperLength < 0.05f || lowerLength < 0.05f)
                return preferredZ;
            float maximumReach = upperLength + lowerLength - 0.025f;
            Vector3 offset = gun.TransformPoint(
                new Vector3(-0.045f, -0.035f, 0f)) - shoulder;
            Vector3 barrelDirection = gun.forward;
            float along = Vector3.Dot(offset, barrelDirection);
            float radialSquared = Mathf.Max(0f, offset.sqrMagnitude - along * along);
            float halfChordSquared = maximumReach * maximumReach - radialSquared;
            if (halfChordSquared <= 0f) return preferredZ;
            float halfChord = Mathf.Sqrt(halfChordSquared);
            float minimumReachableZ = -along - halfChord;
            float maximumReachableZ = -along + halfChord;
            const float minimumForegripZ = 0.14f;
            // No usable rail segment intersects the reach sphere. Keep the
            // existing grip rather than snapping a hand through the receiver.
            if (maximumReachableZ < minimumForegripZ ||
                minimumReachableZ > preferredZ) return preferredZ;
            return Mathf.Clamp(preferredZ,
                Mathf.Max(minimumForegripZ, minimumReachableZ), maximumReachableZ);
        }

        private void KeepProneWeaponAboveSupport(ref float foregrip,
            Vector3 floor, Vector3 normal)
        {
            // The barrel or magazine can be lower than either grip. Test
            // the stored model bounds against the same plane the torso uses.
            float lift = ReachableWeaponLift(foregrip, floor, normal, out float needed);
            Vector3 barrel = weaponRoot.forward;
            float tiltIntoGround = Vector3.Dot(barrel, normal);
            if (needed - lift > 0.003f && tiltIntoGround < -0.001f)
            {
                // In extreme downhill ADS there may be no reachable lift.
                // Pitch the drawn model just enough to clear the support
                // plane. Input aim and the authoritative shot ray keep their yaw.
                Vector3 levelBarrel = Vector3.ProjectOnPlane(barrel, normal).normalized;
                if (levelBarrel.sqrMagnitude < 0.01f)
                    levelBarrel = Vector3.ProjectOnPlane(transform.forward, normal).normalized;
                float remaining = Mathf.Asin(Mathf.Clamp01((needed - lift) /
                    Mathf.Max(0.12f, equipped.ModelLocalBounds.max.z)));
                Vector3 supportedBarrel = Vector3.RotateTowards(barrel, levelBarrel, remaining, 0f);
                weaponRoot.rotation = Quaternion.FromToRotation(barrel, supportedBarrel) * weaponRoot.rotation;
                foregrip = ReachableSupportGripZ(leftUpperArm, leftForearm, leftHand, weaponRoot, foregrip);
                lift = ReachableWeaponLift(foregrip, floor, normal, out _);
            }
            if (lift > 0.0001f) weaponRoot.position += normal * lift;
        }

        private float ReachableWeaponLift(float foregrip, Vector3 floor,
            Vector3 normal, out float needed)
        {
            Vector3 trigger = weaponRoot.TransformPoint(
                new Vector3(0.015f, -0.045f, -0.045f));
            Vector3 support = weaponRoot.TransformPoint(
                new Vector3(-0.045f, -0.035f, foregrip));
            float lowestGrip = Mathf.Min(Vector3.Dot(trigger - floor, normal),
                Vector3.Dot(support - floor, normal));
            needed = Mathf.Max(0.035f - lowestGrip, 0.018f - WeaponGroundGap(equipped, floor, normal));
            if (needed <= 0.0001f) return 0f;
            float lift = Mathf.Min(0.22f, needed);
            lift = Mathf.Min(lift, AvailableArmSupportLift(rightUpperArm,
                rightForearm, hand, trigger, normal));
            return Mathf.Min(lift, AvailableArmSupportLift(leftUpperArm,
                leftForearm, leftHand, support, normal));
        }

        private static float AvailableArmSupportLift(Transform upper,
            Transform forearm, Transform wrist, Vector3 grip, Vector3 normal)
        {
            float reach = Vector3.Distance(upper.position, forearm.position) +
                Vector3.Distance(forearm.position, wrist.position) - 0.025f;
            if (reach <= 0.05f) return 0f;
            Vector3 offset = grip - upper.position;
            float along = Vector3.Dot(offset, normal);
            float radialSquared = Mathf.Max(0f, offset.sqrMagnitude - along * along);
            float chordSquared = reach * reach - radialSquared;
            return chordSquared <= 0f ? 0f :
                Mathf.Max(0f, Mathf.Sqrt(chordSquared) - along);
        }

        private void ApplyProceduralWeaponMotion()
        {
            if (!HasGun || IsSprintHolstered || weaponRoot == null || equipped.RequiresVisualDraw)
            {
                weaponMotionReady = false;
                smoothedWeaponSway = Vector3.zero;
                visualRecoilDegrees = 0f;
                return;
            }

            float dt = Mathf.Clamp(Time.deltaTime, 0.001f, 0.10f);
            Camera view = motor != null ? motor.AimCamera : null;
            Quaternion currentAim = view != null
                ? view.transform.rotation : transform.rotation;
            Vector3 targetSway = Vector3.zero;
            if (weaponMotionReady)
            {
                Quaternion delta = currentAim * Quaternion.Inverse(previousAimRotation);
                delta.ToAngleAxis(out float angle, out Vector3 axis);
                if (angle > 180f) angle -= 360f;
                // Ignore discontinuities from respawn/camera hand-off. Normal
                // human look motion is far below 75 degrees in one render frame.
                if (Mathf.Abs(angle) < 75f && axis.sqrMagnitude > 0.0001f)
                {
                    Vector3 localAxis = transform.InverseTransformDirection(axis.normalized);
                    float angularRate = angle / dt;
                    // Loot "weight" is spawn probability. Presentation
                    // inertia uses its own kilogram-scale value so a rare gun
                    // does not magically feel heavier in the player's hands.
                    float massKg = equipped != null && equipped.Spec != null
                        ? equipped.Spec.handlingMassKg : 3.6f;
                    float mass01 = Mathf.InverseLerp(2.5f, 5.5f, massKg);
                    float inertiaScale = Mathf.Lerp(1.12f, 0.72f, mass01);
                    float adsScale = aiming ? 0.34f : 1f;
                    targetSway.x = Mathf.Clamp(-localAxis.x * angularRate * 0.010f,
                        -1.8f, 1.8f) * inertiaScale * adsScale;
                    targetSway.y = Mathf.Clamp(-localAxis.y * angularRate * 0.012f,
                        -2.2f, 2.2f) * inertiaScale * adsScale;
                    float lateralSpeed = capsule != null
                        ? Vector3.Dot(capsule.velocity, transform.right) : 0f;
                    targetSway.z = Mathf.Clamp(-lateralSpeed * 0.32f,
                        -1.5f, 1.5f) * inertiaScale * adsScale;
                }
            }
            previousAimRotation = currentAim;
            weaponMotionReady = true;

            // The fast running cadence already animates a low-ready rifle;
            // damp look-driven inertial twist so sprinting cannot whip it
            // through the chest. Recoil and the aim authority are unchanged.
            if (IsSprintLowReady) targetSway *= 0.45f;

            // Heavier firearms resist rapid direction changes a little longer,
            // while ADS shoulder contact damps the same mass more aggressively.
            float responseMassKg = equipped != null && equipped.Spec != null
                ? equipped.Spec.handlingMassKg : 3.6f;
            float responseMass01 = Mathf.InverseLerp(2.5f, 5.5f, responseMassKg);
            float swayTime = Mathf.Lerp(0.065f, 0.095f, responseMass01);
            if (aiming) swayTime *= 0.68f;
            float swayAlpha = 1f - Mathf.Exp(-dt / swayTime);
            smoothedWeaponSway = Vector3.Lerp(
                smoothedWeaponSway, targetSway, swayAlpha);

            // Couple a small receiver kick to the real accepted shot time.
            // Damage/aim remain untouched; this only prevents a visually
            // weightless gun from staying perfectly rigid while firing.
            if (combat != null && equipped != null && equipped.Spec != null &&
                combat.LastShootTime > lastVisualShotTime &&
                combat.LastShootTime > 0f &&
                Time.time - combat.LastShootTime < 0.12f)
            {
                float kick = Mathf.Clamp(
                    equipped.Spec.recoilPitchDegrees * 0.18f, 0.65f, 2.8f);
                visualRecoilDegrees = Mathf.Max(visualRecoilDegrees, kick);
                lastVisualShotTime = combat.LastShootTime;
            }
            visualRecoilDegrees = Mathf.MoveTowards(
                visualRecoilDegrees, 0f, 24f * dt);

            weaponRoot.rotation *= Quaternion.Euler(
                smoothedWeaponSway.x - visualRecoilDegrees,
                smoothedWeaponSway.y, smoothedWeaponSway.z);
            weaponRoot.position -= weaponRoot.forward *
                (visualRecoilDegrees * 0.008f);
        }

        // Preserve the reviewed 30 FPS grip response in wall-clock time.
        // Fixed degrees-per-frame makes 60/90 FPS phones snap the wrists and
        // fingers much faster than 30 FPS devices. Clamp hitch contribution so
        // a resume/GC spike cannot teleport the hand through the weapon.
        private static float GripFrameScale()
        {
            return 30f * Mathf.Clamp(Time.deltaTime, 0f, 0.05f);
        }

        private static float GripBlendAlpha(float alphaAt30Fps)
        {
            // Convert a reviewed per-30Hz-frame blend to the same exponential
            // wall-clock response at any render rate.
            float remaining = 1f - Mathf.Clamp01(alphaAt30Fps);
            return 1f - Mathf.Pow(remaining, GripFrameScale());
        }

        // Two-bone position IK still leaves one free wrist-twist degree.
        // Derive a palm basis from actual Humanoid finger bones instead of
        // assuming any model-local hand axis. The thumb follows the barrel and
        // the fingers wrap across the handguard; choose the mirrored across-axis
        // requiring the least wrist correction so different avatars do not flip.
        private void AlignSupportWristRotation()
        {
            if (leftHand == null || leftThumb == null || leftMiddle == null ||
                weaponRoot == null) return;
            Vector3 finger = leftMiddle.position - leftHand.position;
            Vector3 thumb = leftThumb.position - leftHand.position;
            if (finger.sqrMagnitude < 0.0001f || thumb.sqrMagnitude < 0.0001f)
                return;
            finger.Normalize();
            thumb = Vector3.ProjectOnPlane(thumb, finger);
            if (thumb.sqrMagnitude < 0.0001f) return;
            thumb.Normalize();

            Quaternion currentBasis = Quaternion.LookRotation(finger, thumb);
            Vector3 barrel = weaponRoot.forward;
            Vector3 across = weaponRoot.right;
            if (barrel.sqrMagnitude < 0.01f || across.sqrMagnitude < 0.01f)
                return;
            barrel.Normalize();
            across.Normalize();

            Quaternion targetBasisA = Quaternion.LookRotation(across, barrel);
            Quaternion targetBasisB = Quaternion.LookRotation(-across, barrel);
            Quaternion targetA = targetBasisA *
                Quaternion.Inverse(currentBasis) * leftHand.rotation;
            Quaternion targetB = targetBasisB *
                Quaternion.Inverse(currentBasis) * leftHand.rotation;
            Quaternion target = Quaternion.Angle(leftHand.rotation, targetA) <=
                Quaternion.Angle(leftHand.rotation, targetB) ? targetA : targetB;
            float frameScale = GripFrameScale();
            leftHand.rotation = Quaternion.RotateTowards(
                leftHand.rotation, target, (aiming ? 52f : 38f) * frameScale);
        }

        private static void BendDigitTowardGrip(Transform[] digits,
            int offset, Vector3 gripTarget, float proximalDegrees,
            float middleDegrees)
        {
            if (digits == null || offset < 0 || offset + 2 >= digits.Length) return;
            float frameScale = GripFrameScale();
            Transform proximal = digits[offset], middle = digits[offset + 1],
                distal = digits[offset + 2];
            if (proximal == null || middle == null || distal == null) return;
            Vector3 first = middle.position - proximal.position;
            Vector3 towardFirst = gripTarget - middle.position;
            if (first.sqrMagnitude > 0.000001f && towardFirst.sqrMagnitude > 0.000001f)
            {
                Vector3 desired = Vector3.Slerp(first.normalized,
                    towardFirst.normalized, 0.42f);
                Quaternion target = Quaternion.FromToRotation(
                    first.normalized, desired) * proximal.rotation;
                proximal.rotation = Quaternion.RotateTowards(
                    proximal.rotation, target, proximalDegrees * frameScale);
            }
            Vector3 second = distal.position - middle.position;
            Vector3 towardSecond = gripTarget - distal.position;
            if (second.sqrMagnitude > 0.000001f && towardSecond.sqrMagnitude > 0.000001f)
            {
                Vector3 desired = Vector3.Slerp(second.normalized,
                    towardSecond.normalized, 0.58f);
                Quaternion target = Quaternion.FromToRotation(
                    second.normalized, desired) * middle.rotation;
                middle.rotation = Quaternion.RotateTowards(
                    middle.rotation, target, middleDegrees * frameScale);
            }
        }

        private void ApplyFingerGrip(float foregrip)
        {
            if (weaponRoot == null || !fingerRigResolved) return;
            Vector3 trigger = weaponRoot.TransformPoint(
                new Vector3(0.015f, -0.045f, -0.045f));
            BendDigitTowardGrip(rightGripDigits, 0,
                trigger - weaponRoot.up * 0.006f, 8f, 11f);
            Vector3 pistolGrip = trigger - weaponRoot.up * 0.060f +
                weaponRoot.forward * 0.010f;
            BendDigitTowardGrip(rightGripDigits, 3, pistolGrip, 25f, 38f);
            BendDigitTowardGrip(rightGripDigits, 6,
                pistolGrip - weaponRoot.up * 0.010f, 28f, 42f);
            BendDigitTowardGrip(rightGripDigits, 9,
                pistolGrip - weaponRoot.up * 0.020f, 30f, 45f);
            Vector3 supportWrap = weaponRoot.TransformPoint(
                new Vector3(0.025f, -0.045f, foregrip));
            BendDigitTowardGrip(leftGripDigits, 0, supportWrap, 18f, 28f);
            BendDigitTowardGrip(leftGripDigits, 3, supportWrap, 22f, 34f);
            BendDigitTowardGrip(leftGripDigits, 6,
                supportWrap - weaponRoot.up * 0.008f, 24f, 38f);
            BendDigitTowardGrip(leftGripDigits, 9,
                supportWrap - weaponRoot.up * 0.016f, 26f, 40f);
        }

        private void AlignSupportHand()
        {
            if (!HasGun || IsSprintHolstered || weaponRoot == null ||
                !HasCompleteHumanoidGripRig()) return;
            bool drawing = equipped.RequiresVisualDraw;
            float gripWeight = drawing ? Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(0.73f, 1f, equipped.PresentationSocket.Progress)) : 1f;
            if (gripWeight <= 0.001f) return;
            // Receiver at right trigger, front grip down barrel +Z.
            // The receiver already fitted the trigger reach before its socket
            // blend. Match the left support grip to the weapon's barrel length.
            // A pistol is supported around the trigger hand, not at a rifle's
            // fore-end beyond its muzzle. The same stance-aware arm solve runs.
            float foregrip = equipped.Spec.category == "Pistol" ? -0.025f :
                Mathf.Clamp(equipped.Spec.presentationLengthMetres * 0.40f, 0.22f, 0.48f);
            foregrip = ReachableSupportGripZ(
                leftUpperArm, leftForearm, leftHand, weaponRoot, foregrip);
            if (TryGetGroundedProneSupport(out Vector3 supportPoint, out Vector3 supportNormal))
            {
                if (!drawing) KeepProneWeaponAboveSupport(ref foregrip, supportPoint, supportNormal);
            }
            else supportNormal = Vector3.zero;
            ReachArm(rightUpperArm, rightForearm, hand,
                Vector3.Lerp(hand.position,
                    weaponRoot.TransformPoint(new Vector3(0.015f, -0.045f, -0.045f)), gripWeight),
                transform.right, supportPoint, supportNormal);
            ReachArm(leftUpperArm, leftForearm, leftHand,
                Vector3.Lerp(leftHand.position,
                    weaponRoot.TransformPoint(new Vector3(-0.045f, -0.035f, foregrip)), gripWeight),
                -transform.right, supportPoint, supportNormal);
            if (!drawing) AlignSupportWristRotation();

            // Position-only IK can still leave the trigger palm visibly
            // twisted around the receiver. The catalog's handRotation is the
            // same canonical weapon-to-right-hand relation used by the legacy
            // fallback above, so invert it to recover the desired wrist world
            // rotation after the analytic arm solve. Keep a small amount of
            // authored wrist motion while hip-firing; ADS tightens the grip.
            Quaternion grip = Quaternion.Euler(equipped.Spec.handRotation);
            Quaternion desiredWrist = weaponRoot.rotation * Quaternion.Inverse(grip);
            hand.rotation = Quaternion.Slerp(hand.rotation, desiredWrist,
                GripBlendAlpha(aiming ? 0.92f : 0.76f) * gripWeight);
            if (!drawing) ApplyFingerGrip(foregrip);
        }

        private void LateUpdate()
        {
            if (equipped == null)
            {
                IsSprintLowReady = false;
                IsSprintHolstered = false;
                sprintReadyBlend = 0f;
                return;
            }
            // Never holster on a slow crouch/crawl, while ADS, or during
            // a trigger pull. The actual motor speed gates this, not just
            // a still-latched sprint HUD toggle.
            Vector3 speed = capsule != null ? capsule.velocity : Vector3.zero;
            float planarSpeedSqr = speed.x * speed.x + speed.z * speed.z;
            bool sprintIntent = motor != null && motor.IsAutoSprinting ||
                Input.GetKey(KeyCode.LeftShift);
            bool sprinting = !fistMode && !aiming && combat != null &&
                !combat.RequestsWeaponDraw &&
                combat.Stance == AarisBattleActions.PlayerStance.Standing &&
                capsule != null && capsule.isGrounded &&
                sprintIntent && planarSpeedSqr > 3.75f * 3.75f;
            FindHumanoidSockets(); // one cached lookup; resolves late-loaded avatars
            IsSprintLowReady = sprinting && HasCompleteHumanoidGripRig();
            IsSprintHolstered = sprinting && !IsSprintLowReady;
            // Wall-clock easing avoids a sudden 11-degree muzzle snap when
            // the motor crosses the sprint speed threshold. No allocations.
            float sprintAlpha = 1f - Mathf.Exp(
                -12f * Mathf.Clamp(Time.deltaTime, 0f, 0.05f));
            sprintReadyBlend = Mathf.Lerp(sprintReadyBlend,
                IsSprintLowReady ? 1f : 0f, sprintAlpha);
            if (aiming || (combat != null && combat.FireHeld))
                sprintReadyBlend = 0f;
            AttachModelsToSockets();
            // Apply inertia before solving both arms so the palms follow the
            // final visually displaced receiver/foregrip, not yesterday's pose.
            ApplyProceduralWeaponMotion();
            AlignSupportHand();
        }

        private void Update()
        {
            if (!AcceptsPlayerWeaponInput) return;
            if (Input.GetKeyDown(KeyCode.E)) RequestPickup();
            if (Input.GetKeyDown(KeyCode.G)) DropCurrent();
            if (Input.GetKeyDown(KeyCode.R)) Reload();
            if (Input.GetKeyDown(KeyCode.Alpha1)) SwapWeapons();
            if (Input.GetKeyDown(KeyCode.Alpha2)) ToggleFists();
            // Mouse-right is already used by the camera-look motor.
            // ADS on desktop uses V; mobile has the explicit ADS button.
            if (Input.GetKeyDown(KeyCode.V)) ToggleAim();
        }

        private void OnDisable()
        {
            aiming = false;
            IsSprintLowReady = false;
            IsSprintHolstered = false;
            sprintReadyBlend = 0f;
            weaponMotionReady = false;
            smoothedWeaponSway = Vector3.zero;
            visualRecoilDegrees = 0f;
            if (motor != null) motor.SetWeaponAimFov(-1f);
            if (combat != null) combat.SetFireHeld(false);
        }
    }
}
