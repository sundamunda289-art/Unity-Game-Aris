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
        private Transform hand, chest, leftUpperArm, leftForearm, leftHand;
        private Transform leftThumb, leftMiddle;
        private Transform rightUpperArm, rightForearm;
        private readonly Transform[] rightGripDigits = new Transform[12];
        private readonly Transform[] leftGripDigits = new Transform[12];
        private bool fingerRigResolved;
        private CharacterController capsule;
        private float nextRigSearch;
        private bool aiming, fistMode;
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
        // The cache is keyed by Resources folder and is reused by every held,
        // stowed and floor instance; no per-pickup material copies are made.
        private static readonly Dictionary<string, Material> pbrFinishCache =
            new Dictionary<string, Material>(8, System.StringComparer.Ordinal);
        // One converted URP material per imported flat-shaded source slot.
        // Reused by every pickup/equip instance so extra visual separation
        // does not become per-gun memory churn.
        private static readonly Dictionary<Material, Material> weaponFinishCache =
            new Dictionary<Material, Material>();
        public bool IsSprintHolstered { get; private set; }

        public AarisEquippedGun Current => equipped;
        public bool Aiming => aiming;
        public bool HasGun => equipped != null && equipped.Configured && !fistMode;
        public bool HasStowedGun => stowed != null && stowed.Configured;
        public bool HasAnyGun => equipped != null && equipped.Configured;
        public bool FistMode => fistMode;
        public bool RequiresExchangeDrop => equipped != null && stowed != null;
        public bool PickupAvailable => lootWorld != null && lootWorld.HasPickupInRange;

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
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Simple Lit");
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

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader =
                Shader.Find("Universal Render Pipeline/Simple Lit");
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
                material.SetColor("_BaseColor", Color.white);
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
            if (HasAuthoredTexture(source)) return source;
            if (weaponFinishCache.TryGetValue(source, out Material cached) &&
                cached != null) return cached;

            Material fallback = WeaponFinish(spec);
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader =
                Shader.Find("Universal Render Pipeline/Simple Lit");
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

        internal static void ApplyTextureAwareWeaponFinish(AarisWeaponSpec spec,
            Renderer[] renderers)
        {
            if (renderers == null || renderers.Length == 0) return;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                Material[] source = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < source.Length; i++)
                {
                    Material authoredPbr = AuthoredPbrFinish(spec, source[i]);
                    if (authoredPbr != null)
                    {
                        if (source[i] != authoredPbr)
                        {
                            source[i] = authoredPbr;
                            changed = true;
                        }
                        continue;
                    }
                    Material converted = ConvertedWeaponFinish(spec, source[i]);
                    if (converted == null || converted == source[i]) continue;
                    source[i] = converted;
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
        public void RequestPickup() { if (lootWorld != null) lootWorld.RequestPickup(); }
        public bool DropCurrent() => lootWorld != null && lootWorld.TryDropEquipped();

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
            mesh.localPosition = new Vector3(0f, -0.025f,
                incoming.Spec.presentationLengthMetres * 0.22f) -
                combinedBounds.center * scale;

            var next = root.AddComponent<AarisEquippedGun>();
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

        public bool Reload() => HasGun && equipped.BeginReload();

        /// <summary>Unarmed punching without deleting either carried FBX.</summary>
        public bool ToggleFists()
        {
            if (equipped == null) return false;
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
            if (stowed == null || equipped == null) return false;
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
            aiming = !aiming && HasGun;
            if (aiming && motor != null) motor.CancelAutoSprint();
            if (motor != null)
                motor.SetWeaponAimFov(aiming ? equipped.ScopeFov : -1f);
        }

        private void ResetCombatTransition()
        {
            if (combat != null) combat.SetFireHeld(false);
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
            foreach (var animator in GetComponentsInChildren<Animator>(true))
            {
                if (animator == null || animator.avatar == null ||
                    !animator.avatar.isValid || !animator.avatar.isHuman) continue;
                hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                chest = animator.GetBoneTransform(HumanBodyBones.UpperChest);
                if (chest == null) chest = animator.GetBoneTransform(HumanBodyBones.Chest);
                if (chest == null) chest = animator.GetBoneTransform(HumanBodyBones.Spine);
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
        }

        private void PlaceOnBack(Transform item, bool rightShoulder)
        {
            if (item == null) return;
            if (item.parent != transform) item.SetParent(transform, false);
            // World-metre sockets and owner-space rotation avoid 0.01/100x FBX
            // rig transforms. Two guns cross on the back in Fist mode.
            Vector3 origin = chest != null ? chest.position :
                transform.TransformPoint(new Vector3(0f, 1.35f, 0f));
            float side = rightShoulder ? 1f : -1f;
            item.SetPositionAndRotation(
                origin + transform.rotation * new Vector3(side * .17f, .02f, -.21f),
                transform.rotation * Quaternion.Euler(12f, side * 12f, side * 63f));
        }

        private void AttachModelsToSockets()
        {
            if (equipped == null || weaponRoot == null) return;
            FindHumanoidSockets();
            if (stowedRoot != null) PlaceOnBack(stowedRoot, true);
            if (fistMode || IsSprintHolstered)
            {
                PlaceOnBack(weaponRoot, stowedRoot != null ? false : true);
                return;
            }
            if (weaponRoot.parent != transform)
                weaponRoot.SetParent(transform, false);
            // All five reviewed rifle/SMG FBXs point down local +Z.
            // Anchor the receiver just ahead of the chest, not to the
            // animated pistol wrist: the barrel must follow the camera
            // when firing/ADS/prone, regardless of the body animation.
            if (chest != null && hand != null && rightUpperArm != null &&
                rightForearm != null)
            {
                Vector3 forward = transform.forward;
                Camera view = motor != null ? motor.AimCamera : null;
                if (view != null && (aiming || (combat != null &&
                    (combat.FireHeld ||
                     combat.Stance == AarisBattleActions.PlayerStance.Prone))))
                    forward = view.transform.forward;
                if (forward.sqrMagnitude < 0.01f) forward = transform.forward;
                Quaternion aimRotation = Quaternion.LookRotation(forward, Vector3.up);
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
                weaponRoot.SetPositionAndRotation(receiver, aimRotation);
            }
            else
            {
                // Missing Humanoid avatar: retain the working legacy grip
                // instead of hiding/dropping an otherwise usable weapon.
                Quaternion grip = Quaternion.Euler(equipped.Spec.handRotation);
                if (hand != null)
                    weaponRoot.SetPositionAndRotation(
                        hand.position + hand.rotation * equipped.Spec.handOffset,
                        hand.rotation * grip);
                else
                    weaponRoot.SetPositionAndRotation(
                        transform.TransformPoint(new Vector3(.29f, 1.1f, .3f)),
                        transform.rotation * grip);
            }
        }

        // No per-frame allocations or Animation Rigging dependency: the
        // existing Humanoid bones form analytic shoulder/elbow/wrist chains.
        // Both wrists are solved AFTER the PlayableGraph (execution order +310).
        private static void ReachArm(Transform upper, Transform forearm,
            Transform wrist, Vector3 desired, Vector3 fallbackPole)
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
            Vector3 targetElbow = shoulder + direction * along +
                pole.normalized * perpendicular;
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
        private void FitTriggerGripToArmReach()
        {
            if (weaponRoot == null || hand == null || rightUpperArm == null ||
                rightForearm == null) return;
            float upperLength = Vector3.Distance(
                rightUpperArm.position, rightForearm.position);
            float lowerLength = Vector3.Distance(
                rightForearm.position, hand.position);
            if (upperLength < 0.05f || lowerLength < 0.05f) return;
            Vector3 trigger = weaponRoot.TransformPoint(
                new Vector3(0.015f, -0.045f, -0.045f));
            Vector3 fromShoulder = trigger - rightUpperArm.position;
            float distance = fromShoulder.magnitude;
            float maximumReach = upperLength + lowerLength - 0.025f;
            if (distance <= maximumReach || distance < 0.001f) return;
            float correction = Mathf.Min(distance - maximumReach, 0.12f);
            weaponRoot.position -= fromShoulder / distance * correction;
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

        private void ApplyProceduralWeaponMotion()
        {
            if (!HasGun || IsSprintHolstered || weaponRoot == null)
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
                    float weightScale = equipped != null && equipped.Spec != null
                        ? Mathf.Lerp(1.12f, 0.72f,
                            Mathf.InverseLerp(2f, 8f, equipped.Spec.weight))
                        : 1f;
                    float adsScale = aiming ? 0.34f : 1f;
                    targetSway.x = Mathf.Clamp(-localAxis.x * angularRate * 0.010f,
                        -1.8f, 1.8f) * weightScale * adsScale;
                    targetSway.y = Mathf.Clamp(-localAxis.y * angularRate * 0.012f,
                        -2.2f, 2.2f) * weightScale * adsScale;
                    float lateralSpeed = capsule != null
                        ? Vector3.Dot(capsule.velocity, transform.right) : 0f;
                    targetSway.z = Mathf.Clamp(-lateralSpeed * 0.32f,
                        -1.5f, 1.5f) * weightScale * adsScale;
                }
            }
            previousAimRotation = currentAim;
            weaponMotionReady = true;

            float swayAlpha = 1f - Mathf.Exp(-dt / (aiming ? 0.055f : 0.080f));
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
            leftHand.rotation = Quaternion.RotateTowards(
                leftHand.rotation, target, aiming ? 52f : 38f);
        }

        private static void BendDigitTowardGrip(Transform[] digits,
            int offset, Vector3 gripTarget, float proximalDegrees,
            float middleDegrees)
        {
            if (digits == null || offset < 0 || offset + 2 >= digits.Length) return;
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
                    proximal.rotation, target, proximalDegrees);
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
                    middle.rotation, target, middleDegrees);
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
            if (!HasGun || IsSprintHolstered || hand == null ||
                weaponRoot == null || leftUpperArm == null ||
                leftForearm == null || leftHand == null ||
                rightUpperArm == null || rightForearm == null) return;
            // Receiver at right trigger, front grip down barrel +Z.
            // First make the trigger physically reachable on this Humanoid;
            // then match the left support grip to the weapon's barrel length.
            FitTriggerGripToArmReach();
            float foregrip = Mathf.Clamp(
                equipped.Spec.presentationLengthMetres * 0.40f, 0.22f, 0.48f);
            foregrip = ReachableSupportGripZ(
                leftUpperArm, leftForearm, leftHand, weaponRoot, foregrip);
            ReachArm(rightUpperArm, rightForearm, hand,
                weaponRoot.TransformPoint(new Vector3(0.015f, -0.045f, -0.045f)),
                transform.right);
            ReachArm(leftUpperArm, leftForearm, leftHand,
                weaponRoot.TransformPoint(new Vector3(-0.045f, -0.035f, foregrip)),
                -transform.right);
            AlignSupportWristRotation();

            // Position-only IK can still leave the trigger palm visibly
            // twisted around the receiver. The catalog's handRotation is the
            // same canonical weapon-to-right-hand relation used by the legacy
            // fallback above, so invert it to recover the desired wrist world
            // rotation after the analytic arm solve. Keep a small amount of
            // authored wrist motion while hip-firing; ADS tightens the grip.
            Quaternion grip = Quaternion.Euler(equipped.Spec.handRotation);
            Quaternion desiredWrist = weaponRoot.rotation * Quaternion.Inverse(grip);
            hand.rotation = Quaternion.Slerp(hand.rotation, desiredWrist,
                aiming ? 0.92f : 0.76f);
            ApplyFingerGrip(foregrip);
        }

        private void LateUpdate()
        {
            if (equipped == null)
            {
                IsSprintHolstered = false;
                return;
            }
            // Never holster on a slow crouch/crawl, while ADS, or during
            // a trigger pull. The actual motor speed gates this, not just
            // a still-latched sprint HUD toggle.
            Vector3 speed = capsule != null ? capsule.velocity : Vector3.zero;
            float planarSpeedSqr = speed.x * speed.x + speed.z * speed.z;
            bool sprintIntent = motor != null && motor.IsAutoSprinting ||
                Input.GetKey(KeyCode.LeftShift);
            IsSprintHolstered = !fistMode && !aiming && combat != null &&
                !combat.FireHeld &&
                combat.Stance == AarisBattleActions.PlayerStance.Standing &&
                capsule != null && capsule.isGrounded &&
                sprintIntent && planarSpeedSqr > 3.75f * 3.75f;
            AttachModelsToSockets();
            // Apply inertia before solving both arms so the palms follow the
            // final visually displaced receiver/foregrip, not yesterday's pose.
            ApplyProceduralWeaponMotion();
            AlignSupportHand();
        }

        private void Update()
        {
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
            weaponMotionReady = false;
            smoothedWeaponSway = Vector3.zero;
            visualRecoilDegrees = 0f;
            if (motor != null) motor.SetWeaponAimFov(-1f);
            if (combat != null) combat.SetFireHeld(false);
        }
    }
}
