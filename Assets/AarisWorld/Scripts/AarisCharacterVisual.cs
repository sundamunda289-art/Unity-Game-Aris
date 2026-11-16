using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Aaris.MapReview
{
    /// <summary>
    /// Presentation-only adapter. The saved AarisThirdPersonPlayer continues to own
    /// input, camera, physics and terrain traversal. Imported characters never add
    /// another collider, rigidbody or movement controller.
    /// </summary>
    [DisallowMultipleComponent, RequireComponent(typeof(CharacterController))]
    [DefaultExecutionOrder(200)]
    public sealed class AarisCharacterVisual : MonoBehaviour
    {
        private const string PrimaryModel = "AarisCharacters/Primary/TacticalSoldier/TacticalSoldier";
        private const string PreviousPrimaryModel = "AarisCharacters/Primary/RealisticSoldier/RealisticSoldier";
        private const string LegacyPrimaryModel = "AarisCharacters/Primary/Player";
        private const string FallbackModel = "AarisCharacters/Kenney/characterMedium";
        private const string KenneyClips = "AarisCharacters/Kenney/";
        private const string PrimaryClips = "AarisCharacters/Primary/Animations/";
        private const string MocapLocomotion = "AarisMocap/mocap_locomotion";
        private const float TargetHeightMetres = 1.78f;

        [SerializeField, Range(-180f, 180f)] private float modelYawOffset;
        [SerializeField, Range(0.1f, 0.8f)] private float blendResponseSeconds = 0.2f;
        [SerializeField] private float fullRunSpeed = 6.5f;
        [SerializeField, Range(0f, 8f)] private float sprintForwardLeanDegrees = 4f;
        [SerializeField, Range(0f, 6f)] private float strafeBankDegrees = 3f;
        [SerializeField, Range(0f, 0.12f)] private float landingCompressionMetres = 0.055f;

        private CharacterController controller;
        private GameObject visual;
        private MeshRenderer[] placeholderRenderers;
        private Animator animator;
        private Material runtimeSkin;
        // One local player, one authored material conversion per source slot.
        // These are shared by all submeshes of this character and destroyed
        // with the visual adapter; no per-frame or per-renderer material churn.
        private readonly Dictionary<Material, Material> realisticSkinMaterials =
            new Dictionary<Material, Material>();
        private PlayableGraph graph;
        private AnimationMixerPlayable mixer;
        private AnimationMixerPlayable weaponUpperMixer;
        private AnimationLayerMixerPlayable layered;
        private AvatarMask upperBodyMask;
        private AnimationClipPlayable readyHoldPlayable;
        private AarisWeaponInventory weaponInventory;
        private float readyPoseBlend;
        private bool graphReady;
        private bool presentationPaused;
        public bool HasWorkingHumanoid => graphReady && visual != null && animator != null;
        private float runBlend;
        private float airborneBlend;
        // Ignore one-frame grounded loss across terrain seams without delaying a deliberate jump.
        private float lastReliableGroundContact;
        private float walkRunBlend;
        private float runPhaseOffset, sprintPhaseOffset;
        private float sprintBlend;
        private AnimationClipPlayable idlePlayable, walkPlayable, runPlayable, jumpPlayable,
            fallPlayable, crouchPlayable, crouchWalkPlayable, jumpStartPlayable,
            jumpLandPlayable, punchPlayable, shootPlayable, sprintPlayable, pronePlayable, crawlPlayable;
        private AnimationClip crouchClip, crouchWalkClip, punchClip, shootClip;
        private AnimationClip sprintClip, proneClip, crawlClip;
        private int lastVisualAttackSequence;
        private float seenTakeoff = -10f, seenTouchdown = -10f;
        private AarisBattleActions actions;
        private AarisThirdPersonPlayer playerMotor;
        private AarisOfflineDrop aerialDrop;
        // Optional scripts can join after Start, but never probe every animation frame.
        private float nextOptionalComponentRefresh;
        private Transform punchArm;
        private Transform leftThigh, leftShin, leftFootBone;
        private Transform rightThigh, rightShin, rightFootBone;
        private Transform proneChestBone, proneHeadBone;
        private Transform balanceSpineBone, locomotionHipsBone;
        private const float ProneChestCorrectionMaxDegrees = 24f;
        private const float ProneHeadColliderInset = 0.10f;
        // The imported soldier's animated standing bounds are shorter than
        // its static FBX bounds. Calibrate once against the ACTUAL idle mesh.
        private SkinnedMeshRenderer bodySkin;
        private bool groundedRigCalibrated;
        private float standingShoeOffset;
        private readonly RaycastHit[] ankleSupportHits = new RaycastHit[12];
        // Stance-foot world locks remove visible contact-foot skating while
        // preserving authored swing motion and CharacterController authority.
        private Vector3 leftUprightPlantWorld, rightUprightPlantWorld;
        private bool leftUprightPlantLocked, rightUprightPlantLocked;
        private const float UprightPlantEnterGapMetres = 0.065f;
        private const float UprightPlantReleaseGapMetres = 0.115f;
        private const float UprightPlantBreakDistanceMetres = 0.18f;
        private const float CrouchRigLowering = 0.50f;
        // New rig's crouched Humanoid head was 1.34m above terrain against
        // a 1.14m capsule. Extra 23cm retains headroom; the existing two-bone
        // knee IK preserves shoe/ground contact after lowering the hip.
        private const float RealisticSoldierCrouchRigLowering = 0.73f;
        private const float ProneRigLowering = 0.22f;
        // The Bamen tactical rig retargets its crawling pose ~42 cm higher
        // than the previous superhero rig. Native hand/foot bone measurements
        // confirm this additional visual-only lowering keeps feet above ground.
        private const float RealisticSoldierProneRigLowering = 0.64f;
        // Upright Humanoid ankle is much closer to the sole than the retargeted
        // crouch ankle. Keeping both at 13cm visibly floats standing boots.
        private const float UprightAnkleClearanceMetres = 0.075f;
        private const float PlantedAnkleMetres = 0.13f;
        private const float ProneAnkleClearanceMetres = 0.105f;
        private Vector3 restingLocalPosition;
        private Quaternion restingLocalRotation;
        private float crouchBlend, proneBlend;
        private float smoothedStrafeBank;
        // Small, presentation-only anticipation for starts, stops and direction pivots.
        // No physics probes, animation allocations or root-motion ownership changes.
        private float smoothedAccelerationPitch;
        private float smoothedTurnBank;
        // Lightweight orientation warping: turn the pelvis toward real capsule
        // travel while the upper body keeps weapon/camera-facing authority.
        private float smoothedOrientationWarpYaw;
        private Vector3 lastPlanarVelocity;
        private float lastFacingYaw;
        private bool motionHistoryReady;
        private Vector3 lastMotorPosition;
        private AnimationClip idleClip, walkClip, runClip;
        private float locomotionPhase, crouchPhase, crawlPhase;
        private float gaitDirection = 1f; // Stable contact phase for backpedal.
        // Avoid repeated managed-to-native Playables calls for settled poses.
        // Each cached value mirrors the weight last sent to the native graph.
        private const float WeightWriteEpsilon = 0.00025f;
        private readonly float[] lastBaseWeights = new float[12];
        private readonly float[] lastUpperWeights = new float[3];
        private float lastLayerWeight;
        // NaN forces the first gait time sample after graph initialization.
        private float lastLocomotionPhase = float.NaN;
        private float lastCrouchPhase = float.NaN;
        private float lastCrawlPhase = float.NaN;
        private bool primaryLoaded;
        private bool tacticalSoldierLoaded;
        private bool realisticSoldierLoaded;
        private bool naturalMocapLoaded;
        internal bool UsingNaturalMocap => naturalMocapLoaded;
        private bool CalibratedSoldierLoaded =>
            tacticalSoldierLoaded || realisticSoldierLoaded;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AttachToExistingPlayer()
        {
            EnsureForPlayer(FindFirstObjectByType<AarisThirdPersonPlayer>());
        }

        internal static void EnsureForPlayer(AarisThirdPersonPlayer player)
        {
            // Preserve the checked-in scene and player prefab, including all original
            // camera references and controller tuning. No scene editor drag-and-drop.
            if (player != null && player.GetComponent<AarisCharacterVisual>() == null)
                player.gameObject.AddComponent<AarisCharacterVisual>();
        }

        private void Awake()
        {
            controller = GetComponent<CharacterController>();
            // Keep a sane fallback for scenes whose motor is attached later.
            // Start() replaces this with the motor's authoritative sprint cap.
            fullRunSpeed = Mathf.Clamp(fullRunSpeed, 4.0f, 6.5f);
            lastReliableGroundContact = Time.unscaledTime;
        }

        private void Start()
        {
            if (!TryInstantiateCharacter(PrimaryModel) &&
                !TryInstantiateCharacter(PreviousPrimaryModel) &&
                !TryInstantiateCharacter(LegacyPrimaryModel) &&
                !TryInstantiateCharacter(FallbackModel))
            {
                Debug.LogWarning("Aaris: no usable imported humanoid. Keeping original player visible.");
                enabled = false;
                return;
            }

            TryApplySkin();
            actions = GetComponent<AarisBattleActions>();
            playerMotor = GetComponent<AarisThirdPersonPlayer>();
            if (playerMotor != null)
                fullRunSpeed = playerMotor.UnarmedSprintSpeedMetresPerSecond;
            weaponInventory = GetComponent<AarisWeaponInventory>();
            aerialDrop = GetComponent<AarisOfflineDrop>();
            restingLocalPosition = visual.transform.localPosition;
            restingLocalRotation = visual.transform.localRotation;
            punchArm = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            leftThigh = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            leftShin = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            leftFootBone = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            rightThigh = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            rightShin = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
            rightFootBone = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            proneChestBone = animator.GetBoneTransform(HumanBodyBones.Chest);
            proneHeadBone = animator.GetBoneTransform(HumanBodyBones.Head);
            balanceSpineBone = animator.GetBoneTransform(HumanBodyBones.Spine);
            locomotionHipsBone = animator.GetBoneTransform(HumanBodyBones.Hips);
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            // Unity's Humanoid solver can stabilize feet while Playable clips
            // blend between gait/stance states. Our later terrain-aware analytic
            // leg solve remains authoritative for exact floor height/slope.
            // This closes the transition-only ankle drift without extra physics.
            animator.stabilizeFeet = true;
            SetupAnimations();

            // Fail-safe player presentation: a model with missing/corrupt
            // imported animation clips must NOT hide the saved primitive
            // character and leave an unanimated T-pose on Android.
            if (!graphReady)
            {
                Debug.LogError("Aaris: imported animation clips invalid. Keeping legacy player visible.");
                Destroy(visual);
                visual = null;
                enabled = false;
                return;
            }

            // Hide legacy primitive renderers only after the full Humanoid
            // Playables graph is proven valid. Collision, camera and saved
            // hierarchy are left untouched.
            placeholderRenderers = GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < placeholderRenderers.Length; i++)
                if (IsLegacyPlaceholderRenderer(placeholderRenderers[i]) &&
                    !placeholderRenderers[i].transform.IsChildOf(visual.transform))
                    placeholderRenderers[i].enabled = false;
            Debug.Log("Aaris: locally rigged Humanoid locomotion ready.");
        }

        // Only the saved primitive player pieces are placeholders. A weapon
        // equipped before this Start() can already be a child of the player;
        // hiding all child MeshRenderers makes that real weapon invisible.
        private static bool IsLegacyPlaceholderRenderer(MeshRenderer renderer)
        {
            return renderer != null &&
                renderer.gameObject.name.StartsWith("AarisPlayer_", StringComparison.Ordinal);
        }

        private bool TryInstantiateCharacter(string resourcePath)
        {
            var source = Resources.Load<GameObject>(resourcePath);
            if (source == null) return false;

            visual = Instantiate(source, transform, false);
            visual.name = "AarisRiggedCharacterVisual";
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.Euler(0f, modelYawOffset, 0f);
            SetLayerRecursively(visual.transform, gameObject.layer);
            DisablePrimaryImportHelpers(resourcePath);

            animator = visual.GetComponentInChildren<Animator>(true);
            if (animator == null)
                animator = visual.AddComponent<Animator>();

            if (animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
            {
                Avatar[] avatars = Resources.LoadAll<Avatar>(resourcePath);
                for (int i = 0; i < avatars.Length; i++)
                {
                    if (avatars[i] != null && avatars[i].isValid && avatars[i].isHuman)
                    {
                        animator.avatar = avatars[i];
                        break;
                    }
                }
            }
            if (animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman &&
                FitVisualToController(resourcePath) && ConfigureDrawableSkin(resourcePath))
            {
                primaryLoaded = resourcePath != FallbackModel;
                tacticalSoldierLoaded = resourcePath == PrimaryModel;
                realisticSoldierLoaded = resourcePath == PreviousPrimaryModel;
                return true;
            }
            visual.SetActive(false);
            Destroy(visual);
            visual = null;
            animator = null;
            return false;
        }

        private bool ConfigureDrawableSkin(string resourcePath)
        {
            bodySkin = null;
            foreach (var skin in visual.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!skin.enabled || !skin.gameObject.activeInHierarchy ||
                    skin.sharedMesh == null || skin.sharedMesh.vertexCount == 0)
                    continue;

                if (bodySkin == null) bodySkin = skin;
                if (resourcePath != PrimaryModel) continue;

                // TacticalSoldier imports with bounds expressed around its original
                // bind-space origin. Use the fitted player's envelope in the skin's
                // root-bone space so culling follows the actor through every pose.
                Transform boundsRoot = skin.rootBone != null ? skin.rootBone : skin.transform;
                Vector3 centre = transform.position + Vector3.up * (TargetHeightMetres * 0.5f);
                const float poseExtent = 2f;
                Bounds poseBounds = new Bounds(boundsRoot.InverseTransformPoint(centre), Vector3.zero);
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 offset = new Vector3(
                        (corner & 1) == 0 ? -poseExtent : poseExtent,
                        (corner & 2) == 0 ? -poseExtent : poseExtent,
                        (corner & 4) == 0 ? -poseExtent : poseExtent);
                    poseBounds.Encapsulate(boundsRoot.InverseTransformPoint(centre + offset));
                }
                skin.updateWhenOffscreen = false;
                skin.localBounds = poseBounds;
            }
            return bodySkin != null;
        }

        private void DisablePrimaryImportHelpers(string resourcePath)
        {
            if (resourcePath != PrimaryModel || visual == null) return;

            // The licensed TacticalSoldier FBX contains a root-level Icosphere
            // import helper that is not part of the skinned Humanoid. Keeping it
            // active stretches presentation bounds between the prefab origin and
            // the real skinned mesh thousands of bind-space units away.
            Transform helper = visual.transform.Find("Icosphere");
            if (helper == null) return;

            Renderer helperRenderer = helper.GetComponent<Renderer>();
            if (helperRenderer != null) helperRenderer.enabled = false;
            helper.gameObject.SetActive(false);
        }

        private bool TryFitHumanoidBonesToController()
        {
            // Some valid skinned GLB/FBX rigs carry extreme bind-space vertex
            // coordinates even though their Humanoid skeleton is normal metric
            // scale. Renderer.bounds can therefore be unusable before the first
            // Animator evaluation. Measure a tightly-guarded Human skeleton
            // instead of accepting an arbitrary AABB.
            if (visual == null || animator == null || animator.avatar == null ||
                !animator.avatar.isValid || !animator.avatar.isHuman)
                return false;

            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            Transform leftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            Transform rightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (head == null || leftFoot == null || rightFoot == null)
                return false;

            float feetY = Mathf.Min(leftFoot.position.y, rightFoot.position.y);
            float boneSpan = head.position.y - feetY;
            if (float.IsNaN(boneSpan) || float.IsInfinity(boneSpan) ||
                boneSpan < 0.80f || boneSpan > 2.50f)
                return false;

            float targetBoneSpan = TargetHeightMetres - 0.14f;
            float scale = Mathf.Clamp(targetBoneSpan / boneSpan, 0.50f, 2.00f);
            visual.transform.localScale *= scale;

            // Re-evaluate after scaling and align the full foot anchor. The
            // TacticalSoldier's Humanoid bones are metric, but its authored
            // skinned root is also offset in X/Z. Vertical-only correction can
            // leave the visible actor tens of metres beside/behind the capsule.
            Vector3 footAnchor = (leftFoot.position + rightFoot.position) * 0.5f;
            footAnchor.y = Mathf.Min(leftFoot.position.y, rightFoot.position.y);
            Vector3 targetFootAnchor = transform.position +
                Vector3.up * UprightAnkleClearanceMetres;
            visual.transform.position += targetFootAnchor - footAnchor;
            return true;
        }

        private bool FitVisualToController(string resourcePath)
        {
            // TacticalSoldier's authored skinned AABBs are not a reliable scale
            // source even after its root helper is hidden (one skin still spans
            // roughly 113 bind-space units). A valid Human skeleton is the
            // authoritative metric source for this known primary asset.
            if (resourcePath == PrimaryModel && TryFitHumanoidBonesToController())
                return true;

            var renderers = visual.GetComponentsInChildren<Renderer>(true);
            Bounds combined = new Bounds();
            bool hasBounds = false;
            foreach (var renderer in renderers)
            {
                if (renderer == null || !renderer.enabled ||
                    !renderer.gameObject.activeInHierarchy) continue;
                if (!hasBounds) { combined = renderer.bounds; hasBounds = true; }
                else combined.Encapsulate(renderer.bounds);
            }
            if (!hasBounds || float.IsNaN(combined.size.y) ||
                float.IsInfinity(combined.size.y) ||
                combined.size.y < 0.01f || combined.size.y > 1000f)
                return TryFitHumanoidBonesToController();

            float scale = Mathf.Clamp(TargetHeightMetres / combined.size.y, 0.01f, 100f);
            // Bounds already include the imported root scale.
            visual.transform.localScale *= scale;
            // FBX pivots vary between packages. Align the bottom of the mesh with
            // the CharacterController's ground point, without moving the collider.
            // Renderer bounds follow the visual Transform directly; no physics
            // shape moved, so a global Physics.SyncTransforms() would be wasted.
            combined = new Bounds();
            hasBounds = false;
            foreach (var renderer in renderers)
            {
                if (renderer == null || !renderer.enabled ||
                    !renderer.gameObject.activeInHierarchy) continue;
                if (!hasBounds) { combined = renderer.bounds; hasBounds = true; }
                else combined.Encapsulate(renderer.bounds);
            }
            if (hasBounds)
                visual.transform.position += Vector3.up * (transform.position.y - combined.min.y);
            return true;
        }

        private Material BuildRealisticSoldierMaterial(Material source)
        {
            if (source == null) return null;
            if (realisticSkinMaterials.TryGetValue(source, out Material cached) &&
                cached != null) return cached;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) return source;

            Color sourceColor = Color.white;
            if (source.HasProperty("_BaseColor"))
                sourceColor = source.GetColor("_BaseColor");
            else if (source.HasProperty("_Color"))
                sourceColor = source.GetColor("_Color");
            Texture baseMap = source.HasProperty("_BaseMap")
                ? source.GetTexture("_BaseMap")
                : source.HasProperty("_MainTex") ? source.GetTexture("_MainTex") : null;
            Texture normal = source.HasProperty("_BumpMap")
                ? source.GetTexture("_BumpMap") : null;
            Texture metallicMap = source.HasProperty("_MetallicGlossMap")
                ? source.GetTexture("_MetallicGlossMap") : null;
            Texture occlusionMap = source.HasProperty("_OcclusionMap")
                ? source.GetTexture("_OcclusionMap") : null;
            Texture emissionMap = source.HasProperty("_EmissionMap")
                ? source.GetTexture("_EmissionMap") : null;
            bool hasAuthoredBase = baseMap != null && baseMap != Texture2D.whiteTexture;
            // The model importer already remaps these slots to saved URP materials.
            // Use their original shader keywords and pass state in the Android player.
            if (hasAuthoredBase && source.shader == shader) return source;

            string label = source.name ?? string.Empty;
            Color colour = sourceColor;
            float metallic = source.HasProperty("_Metallic")
                ? source.GetFloat("_Metallic") : 0.02f;
            float smoothness = source.HasProperty("_Smoothness")
                ? source.GetFloat("_Smoothness")
                : source.HasProperty("_Glossiness")
                    ? source.GetFloat("_Glossiness") : 0.18f;
            bool glass = false;

            if (!hasAuthoredBase && label.IndexOf("Clothes", StringComparison.OrdinalIgnoreCase) >= 0)
            { colour = new Color(.13f,.16f,.12f,sourceColor.a); metallic = 0f; smoothness = .14f; }
            else if (!hasAuthoredBase && label.IndexOf("Fabric", StringComparison.OrdinalIgnoreCase) >= 0)
            { colour = new Color(.28f,.23f,.15f,sourceColor.a); metallic = 0f; smoothness = .10f; }
            else if (!hasAuthoredBase && label.IndexOf("Armor", StringComparison.OrdinalIgnoreCase) >= 0)
            { colour = new Color(.18f,.17f,.11f,sourceColor.a); metallic = .04f; smoothness = .32f; }
            else if (!hasAuthoredBase && label.IndexOf("Elements", StringComparison.OrdinalIgnoreCase) >= 0)
            { colour = new Color(.06f,.07f,.06f,sourceColor.a); metallic = .06f; smoothness = .35f; }
            else if (!hasAuthoredBase && label.IndexOf("Iron", StringComparison.OrdinalIgnoreCase) >= 0)
            { colour = new Color(.16f,.17f,.16f,sourceColor.a); metallic = .80f; smoothness = .50f; }
            else if (!hasAuthoredBase && label.IndexOf("Metal", StringComparison.OrdinalIgnoreCase) >= 0)
            { colour = new Color(.24f,.25f,.24f,sourceColor.a); metallic = .75f; smoothness = .55f; }
            else if (!hasAuthoredBase && label.IndexOf("Flesh", StringComparison.OrdinalIgnoreCase) >= 0)
            { colour = new Color(.42f,.29f,.20f,sourceColor.a); metallic = 0f; smoothness = .35f; }
            else if (!hasAuthoredBase && label.IndexOf("Shoes", StringComparison.OrdinalIgnoreCase) >= 0)
            { colour = new Color(.05f,.045f,.04f,sourceColor.a); metallic = 0f; smoothness = .18f; }
            else if (!hasAuthoredBase && label.IndexOf("Boots", StringComparison.OrdinalIgnoreCase) >= 0)
            { colour = new Color(.06f,.05f,.04f,sourceColor.a); metallic = 0f; smoothness = .20f; }
            else if (!hasAuthoredBase && label.IndexOf("Glass", StringComparison.OrdinalIgnoreCase) >= 0)
            { colour = new Color(.02f,.03f,.035f,.52f); metallic = .12f; smoothness = .88f; glass = true; }
            else if (!hasAuthoredBase && label.IndexOf("Skin", StringComparison.OrdinalIgnoreCase) >= 0)
            { colour = new Color(.04f,.045f,.04f,sourceColor.a); metallic = 0f; smoothness = .16f; }
            else if (baseMap == null && Mathf.Min(sourceColor.r,
                Mathf.Min(sourceColor.g, sourceColor.b)) > .72f)
            { colour = new Color(.14f,.15f,.13f,sourceColor.a); metallic = .03f; smoothness = .24f; }

            var result = new Material(shader)
            {
                name = "Aaris Soldier PBR " + source.name,
                enableInstancing = true
            };
            if (result.HasProperty("_BaseColor")) result.SetColor("_BaseColor", colour);
            if (baseMap != null && result.HasProperty("_BaseMap"))
            {
                result.SetTexture("_BaseMap", baseMap);
                string sourceMapProperty = source.HasProperty("_BaseMap")
                    ? "_BaseMap" : "_MainTex";
                result.SetTextureScale("_BaseMap", source.GetTextureScale(sourceMapProperty));
                result.SetTextureOffset("_BaseMap", source.GetTextureOffset(sourceMapProperty));
            }
            if (normal != null && result.HasProperty("_BumpMap"))
            {
                result.SetTexture("_BumpMap", normal);
                if (source.HasProperty("_BumpScale") && result.HasProperty("_BumpScale"))
                    result.SetFloat("_BumpScale", source.GetFloat("_BumpScale"));
                result.EnableKeyword("_NORMALMAP");
            }
            if (metallicMap != null && result.HasProperty("_MetallicGlossMap"))
            {
                result.SetTexture("_MetallicGlossMap", metallicMap);
                result.EnableKeyword("_METALLICSPECGLOSSMAP");
            }
            if (occlusionMap != null && result.HasProperty("_OcclusionMap"))
            {
                result.SetTexture("_OcclusionMap", occlusionMap);
                result.EnableKeyword("_OCCLUSIONMAP");
            }
            if (emissionMap != null && result.HasProperty("_EmissionMap"))
            {
                result.SetTexture("_EmissionMap", emissionMap);
                if (source.HasProperty("_EmissionColor") &&
                    result.HasProperty("_EmissionColor"))
                    result.SetColor("_EmissionColor", source.GetColor("_EmissionColor"));
                result.EnableKeyword("_EMISSION");
            }
            if (result.HasProperty("_Metallic")) result.SetFloat("_Metallic", Mathf.Clamp01(metallic));
            if (result.HasProperty("_Smoothness")) result.SetFloat("_Smoothness", Mathf.Clamp01(smoothness));
            if (glass)
            {
                result.SetOverrideTag("RenderType", "Transparent");
                if (result.HasProperty("_Surface")) result.SetFloat("_Surface", 1f);
                if (result.HasProperty("_SrcBlend"))
                    result.SetFloat("_SrcBlend",
                        (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                if (result.HasProperty("_DstBlend"))
                    result.SetFloat("_DstBlend",
                        (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                if (result.HasProperty("_ZWrite")) result.SetFloat("_ZWrite", 0f);
                result.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                result.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            realisticSkinMaterials[source] = result;
            return result;
        }

        private void ApplyRealisticSoldierMaterials()
        {
            foreach (var renderer in visual.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Material[] source = renderer.sharedMaterials;
                if (source == null || source.Length == 0) continue;
                Material[] converted = new Material[source.Length];
                bool changed = false;
                for (int i = 0; i < source.Length; i++)
                {
                    converted[i] = BuildRealisticSoldierMaterial(source[i]);
                    if (converted[i] == null) converted[i] = source[i];
                    changed |= converted[i] != source[i];
                }
                if (changed) renderer.sharedMaterials = converted;
            }
        }

        private void TryApplySkin()
        {
            if (tacticalSoldierLoaded || realisticSoldierLoaded)
            {
                ApplyRealisticSoldierMaterials();
                return;
            }
            if (primaryLoaded)
            {
                // The bundled character includes normals, but its untextured body and hair
                // import in flat white. Keep authored colors/textures on replacement models.
                int baseMapId = Shader.PropertyToID("_BaseMap");
                int baseColorId = Shader.PropertyToID("_BaseColor");
                var properties = new MaterialPropertyBlock();
                foreach (var renderer in visual.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var materials = renderer.sharedMaterials;
                    for (int i = 0; i < materials.Length; i++)
                    {
                        var material = materials[i];
                        if (material == null || !material.HasProperty(baseColorId) || !material.HasProperty(baseMapId)) continue;
                        var texture = material.GetTexture(baseMapId);
                        if (texture != null && texture != Texture2D.whiteTexture) continue;
                        var original = material.GetColor(baseColorId);
                        if (Mathf.Min(original.r, Mathf.Min(original.g, original.b)) < 0.75f) continue;

                        Color tint;
                        if (material.name.IndexOf("Superhero_Male", StringComparison.OrdinalIgnoreCase) >= 0)
                            tint = new Color(0.20f, 0.29f, 0.39f, original.a);
                        else if (material.name.IndexOf("MI_Hair", StringComparison.OrdinalIgnoreCase) >= 0)
                            tint = new Color(0.14f, 0.12f, 0.11f, original.a);
                        else continue;

                        renderer.GetPropertyBlock(properties, i);
                        properties.SetColor(baseColorId, tint);
                        renderer.SetPropertyBlock(properties, i);
                    }
                }
                return;
            }
            var skins = Resources.LoadAll<Texture2D>(KenneyClips);
            Texture2D chosen = null;
            int bestScore = int.MinValue;
            foreach (var tex in skins)
            {
                if (tex == null) continue;
                string n = tex.name.ToLowerInvariant();
                int score = (n.Contains("human") ? 8 : 0)
                    + (n.Contains("male") ? 3 : 0)
                    + (n.Contains("casual") ? 2 : 0)
                    - (n.Contains("zombie") ? 20 : 0);
                if (score > bestScore) { chosen = tex; bestScore = score; }
            }
            if (chosen == null || bestScore < 0) return;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) return;
            runtimeSkin = new Material(shader) { name = "AarisKenneyRuntimeSkin" };
            if (runtimeSkin.HasProperty("_BaseMap")) runtimeSkin.SetTexture("_BaseMap", chosen);
            else runtimeSkin.mainTexture = chosen;
            foreach (var skinned in visual.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                skinned.sharedMaterial = runtimeSkin;
        }

        private static void SetLayerRecursively(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++)
                SetLayerRecursively(t.GetChild(i), layer);
        }

        private AnimationClip FindClip(string name, string preferred)
        {
            // Unity imports each local FBX as an AnimationClip subasset.
            string location = primaryLoaded ? PrimaryClips + name : KenneyClips + name;
            var clips = Resources.LoadAll<AnimationClip>(location);
            AnimationClip fallback = null;
            foreach (var clip in clips)
            {
                if (clip == null || clip.legacy || !clip.humanMotion || clip.length < 0.02f) continue;
                string label = clip.name.ToLowerInvariant();
                if (label.Contains("preview") || label.Contains("pose")) continue;
                if (label.Contains(preferred)) return clip;
                if (fallback == null) fallback = clip;
            }
            return fallback;
        }

        private static bool MatchesMocapClipName(string importedName,
            string exactName)
        {
            if (string.IsNullOrEmpty(importedName) || string.IsNullOrEmpty(exactName))
                return false;
            if (string.Equals(importedName, exactName,
                StringComparison.OrdinalIgnoreCase)) return true;

            // Unity FBX imports can expose authored actions as
            // "Armature|clip". Match only the final namespace component:
            // broad EndsWith/Contains would accept unrelated look-alike clips.
            int separator = importedName.LastIndexOf('|');
            int leafLength = importedName.Length - separator - 1;
            return separator >= 0 && leafLength == exactName.Length &&
                string.Compare(importedName, separator + 1, exactName, 0,
                    exactName.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }

        private static AnimationClip FindMocapClip(AnimationClip[] clips,
            string exactName)
        {
            if (clips == null || clips.Length == 0) return null;
            foreach (var clip in clips)
            {
                if (clip == null || clip.legacy || !clip.humanMotion ||
                    clip.length < 0.02f) continue;
                if (MatchesMocapClipName(clip.name, exactName)) return clip;
            }
            return null;
        }

        private void SetupAnimations()
        {
            idleClip = FindClip("idle", "idle");

            // Use the single local 100STYLE container only as a complete
            // locomotion set. Mixing one or two mocap clips with older KayKit
            // gait clips produces mismatched contacts/arm cadence, so any
            // missing or invalid Humanoid clip falls back to the reviewed set.
            AnimationClip[] naturalClips = CalibratedSoldierLoaded
                ? Resources.LoadAll<AnimationClip>(MocapLocomotion)
                : Array.Empty<AnimationClip>();
            AnimationClip naturalWalk = FindMocapClip(naturalClips, "mocap_walk");
            AnimationClip naturalJog = FindMocapClip(naturalClips, "mocap_jog");
            AnimationClip naturalRun = FindMocapClip(naturalClips, "mocap_run");
            AnimationClip naturalCrouch = FindMocapClip(naturalClips, "mocap_crouch");
            naturalMocapLoaded = naturalWalk != null && naturalJog != null &&
                naturalRun != null && naturalCrouch != null;

            runPhaseOffset = sprintPhaseOffset = 0f;
            runClip = naturalMocapLoaded ? naturalJog : FindClip("run", "run");
            walkClip = naturalMocapLoaded ? naturalWalk :
                (FindClip("walk", "walk") ?? runClip);
            sprintClip = naturalMocapLoaded ? naturalRun :
                (FindClip("sprint", "sprint") ?? runClip);
            // KayKit Running_A starts two preview samples later than walk.
            // The 100STYLE actions share one authored dataset/container, so
            // they must not inherit this source-specific phase correction.
            if (!naturalMocapLoaded && primaryLoaded && walkClip != runClip)
            {
                runPhaseOffset = -2f / 60f;
                sprintPhaseOffset = sprintClip == runClip ? runPhaseOffset : 0f;
            }
            proneClip = FindClip("prone", "prone");
            crawlClip = FindClip("crawl", "crawl");
            if (proneClip == null || crawlClip == null)
                Debug.LogWarning("Aaris: authored prone/crawl motion unavailable; using a low crouch fallback instead of tilting the entire character.");
            // Each FBX has one reviewed humanoid motion: never use the
            // Jump_Land clip for takeoff or airborne looping.
            AnimationClip jump = FindClip("jump", "jump");
            AnimationClip jumpStart = FindClip("jump_start", "jump") ?? jump;
            AnimationClip jumpLand = FindClip("jump_land", "jump") ?? jump;
            AnimationClip fall = FindClip("fall", "fall");
            crouchClip = naturalMocapLoaded ? naturalCrouch :
                (FindClip("crouch", "crouch") ?? idleClip);
            crouchWalkClip = naturalMocapLoaded ? naturalCrouch :
                (FindClip("crouch_walk", "crouch") ?? crouchClip);
            punchClip = FindClip("punch", "punch");
            shootClip = FindClip("shoot", "shoot");
            if (punchClip == null) punchClip = idleClip;
            if (shootClip == null) shootClip = idleClip;
            if (idleClip == null || walkClip == null || runClip == null)
                return;
            if (jump == null) jump = idleClip;
            if (jumpStart == null) jumpStart = jump;
            if (jumpLand == null) jumpLand = jump;
            if (fall == null) fall = jump;

            graph = PlayableGraph.Create("AarisHumanoidLocomotion");
            graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            // The upper-body attack layer preserves the independently blended gait.
            mixer = AnimationMixerPlayable.Create(graph, 12);
            weaponUpperMixer = AnimationMixerPlayable.Create(graph, 3);
            layered = AnimationLayerMixerPlayable.Create(graph, 2);
            idlePlayable = AnimationClipPlayable.Create(graph, idleClip);
            walkPlayable = AnimationClipPlayable.Create(graph, walkClip);
            runPlayable = AnimationClipPlayable.Create(graph, runClip);
            jumpPlayable = AnimationClipPlayable.Create(graph, jump);
            fallPlayable = AnimationClipPlayable.Create(graph, fall);
            crouchPlayable = AnimationClipPlayable.Create(graph, crouchClip);
            crouchWalkPlayable = AnimationClipPlayable.Create(graph, crouchWalkClip);
            jumpStartPlayable = AnimationClipPlayable.Create(graph, jumpStart);
            jumpLandPlayable = AnimationClipPlayable.Create(graph, jumpLand);
            punchPlayable = AnimationClipPlayable.Create(graph, punchClip);
            shootPlayable = AnimationClipPlayable.Create(graph, shootClip);
            sprintPlayable = AnimationClipPlayable.Create(graph, sprintClip);
            pronePlayable = AnimationClipPlayable.Create(graph, proneClip ?? crouchClip);
            crawlPlayable = AnimationClipPlayable.Create(graph, crawlClip ?? crouchWalkClip);
            // Do not contaminate rifle carry with a frozen Pistol_Shoot
            // shoulder pose. The analytic two-arm solver runs after this graph
            // and already places both hands on the real weapon. Use a neutral
            // authored idle sample only to steady the upper body; the actual
            // shoot clip remains isolated to real shot events below.
            readyHoldPlayable = AnimationClipPlayable.Create(graph, idleClip);
            readyHoldPlayable.SetSpeed(0d);
            readyHoldPlayable.SetTime(0d);
            // Keep one-shot clips short enough to react promptly to attacks
            // without blending over multiple shots in the same hold.
            punchPlayable.SetSpeed(0d);
            shootPlayable.SetSpeed(Mathf.Clamp(shootClip.length / 0.2f, 0.7f, 4.0f));
            jumpLandPlayable.SetSpeed(Mathf.Clamp(jumpLand.length / 0.3f, 0.7f, 3.0f));
            jumpStartPlayable.SetSpeed(Mathf.Clamp(jumpStart.length / 0.23f, 0.7f, 4.0f));
            // Stance FBX clips are transitions into a pose, not continuous gaits.
            // Freeze on the settled position: frame zero can still be standing.
            crouchPlayable.SetSpeed(0d);
            // 100STYLE crouch is an actual crouched locomotion cycle, so its
            // cycle boundary is a valid feet-down hold instead of KayKit's
            // kneeling transition frame. Legacy clips keep their reviewed hold.
            crouchPlayable.SetTime(naturalMocapLoaded
                ? 0d : crouchClip.length * (10d / 24d));
            // The 24-frame KayKit Crawling source uses frame 0 as its prone
            // hold. Do not freeze a mid-crawl pose as the player's prone idle.
            // Only the missing-clip crouch fallback uses the crouch hold frame.
            pronePlayable.SetSpeed(0d);
            pronePlayable.SetTime(proneClip != null
                ? 0d : crouchClip.length * (10d / 24d));
            // Airborne looping is especially distracting when the jump clip
            // contains landing frames. Hold distinct ascent/descent samples.
            jumpPlayable.SetSpeed(0d);
            jumpPlayable.SetTime(jump.length * 0.39d);
            fallPlayable.SetSpeed(0d);
            fallPlayable.SetTime(fall.length * 0.72d);
            idlePlayable.SetApplyFootIK(true);
            walkPlayable.SetApplyFootIK(true);
            runPlayable.SetApplyFootIK(true);
            sprintPlayable.SetApplyFootIK(true);
            crouchWalkPlayable.SetApplyFootIK(true);
            // All gait clips use distance-driven SetTime in LateUpdate.
            // Freeze their independent clocks so the graph cannot advance
            // them a second time and introduce foot-slide at variable FPS.
            idlePlayable.SetSpeed(0d);
            walkPlayable.SetSpeed(0d);
            runPlayable.SetSpeed(0d);
            crouchWalkPlayable.SetSpeed(0d);
            sprintPlayable.SetSpeed(0d);
            pronePlayable.SetSpeed(0d);
            crawlPlayable.SetSpeed(0d);
            var baseInputs = new[]
            {
                idlePlayable, walkPlayable, runPlayable,
                jumpPlayable, fallPlayable, crouchPlayable,
                crouchWalkPlayable, jumpStartPlayable, jumpLandPlayable,
                sprintPlayable, pronePlayable, crawlPlayable
            };
            for (int i = 0; i < baseInputs.Length; i++)
            {
                graph.Connect(baseInputs[i], 0, mixer, i);
                float startingWeight = i == 0 ? 1f : 0f;
                mixer.SetInputWeight(i, startingWeight);
                lastBaseWeights[i] = startingWeight;
            }
            graph.Connect(readyHoldPlayable, 0, weaponUpperMixer, 0);
            graph.Connect(punchPlayable, 0, weaponUpperMixer, 1);
            graph.Connect(shootPlayable, 0, weaponUpperMixer, 2);
            weaponUpperMixer.SetInputWeight(0, 1f);
            weaponUpperMixer.SetInputWeight(1, 0f);
            weaponUpperMixer.SetInputWeight(2, 0f);
            lastUpperWeights[0] = 1f;
            lastUpperWeights[1] = lastUpperWeights[2] = 0f;

            // Explicitly mask root/legs and ALL foot IK, preserving running
            // and jumping foot cycles beneath firing/punching upper body.
            upperBodyMask = new AvatarMask();
            for (int i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++)
            {
                AvatarMaskBodyPart part = (AvatarMaskBodyPart)i;
                bool active = part == AvatarMaskBodyPart.Body ||
                    part == AvatarMaskBodyPart.LeftArm ||
                    part == AvatarMaskBodyPart.RightArm ||
                    part == AvatarMaskBodyPart.LeftFingers ||
                    part == AvatarMaskBodyPart.RightFingers;
                upperBodyMask.SetHumanoidBodyPartActive(part, active);
            }
            graph.Connect(mixer, 0, layered, 0);
            graph.Connect(weaponUpperMixer, 0, layered, 1);
            layered.SetInputWeight(0, 1f);
            layered.SetInputWeight(1, 0f);
            lastLayerWeight = 0f;
            lastLocomotionPhase = lastCrouchPhase = lastCrawlPhase = float.NaN;
            layered.SetLayerMaskFromAvatarMask(1, upperBodyMask);
            var output = AnimationPlayableOutput.Create(graph, "AarisHumanoidOutput", animator);
            output.SetSourcePlayable(layered);
            graphReady = true;
            graph.Play();
        }

        // Do not cross the Unity native boundary to set an unchanged mixer weight.
        // Tiny residual blend tails snap to zero; the threshold is 0.025% and
        // therefore does not alter visible gait, recoil, or stance transitions.
        private static void WriteWeightIfChanged(AnimationMixerPlayable output,
            float[] previous, int index, float requested)
        {
            if (!output.IsValid() || previous == null || index < 0 ||
                index >= previous.Length || index >= output.GetInputCount()) return;
            float weight = requested <= WeightWriteEpsilon ? 0f : Mathf.Clamp01(requested);
            if (Mathf.Abs(previous[index] - weight) <= WeightWriteEpsilon) return;
            output.SetInputWeight(index, weight);
            previous[index] = weight;
        }

        private void WriteLayerWeightIfChanged(float requested)
        {
            if (!layered.IsValid() || layered.GetInputCount() <= 1) return;
            float weight = requested <= WeightWriteEpsilon ? 0f : Mathf.Clamp01(requested);
            if (Mathf.Abs(lastLayerWeight - weight) <= WeightWriteEpsilon) return;
            layered.SetInputWeight(1, weight);
            lastLayerWeight = weight;
        }

        // Analytical two-bone knee solve shared by vertical planting and
        // bounded world-space stance-foot locking. No collision queries or heap allocation.
        private static void PlantLegToTarget(Transform thigh, Transform shin,
            Transform foot, Vector3 desiredFootWorld, Vector3 forward, float weight)
        {
            if (thigh == null || shin == null || foot == null || weight <= 0f) return;
            Vector3 hip = thigh.position;
            Vector3 knee = shin.position;
            Vector3 ankle = foot.position;
            float upper = (knee - hip).magnitude;
            float lower = (ankle - knee).magnitude;
            if (upper < 0.05f || lower < 0.05f) return;
            Vector3 targetFoot = Vector3.Lerp(
                ankle, desiredFootWorld, Mathf.Clamp01(weight));
            Vector3 line = targetFoot - hip;
            float actualDistance = line.magnitude;
            if (actualDistance < 0.01f) return;
            float distance = Mathf.Clamp(actualDistance,
                Mathf.Abs(upper - lower) + 0.005f, upper + lower - 0.005f);
            Vector3 axis = line / actualDistance;
            targetFoot = hip + axis * distance;
            // Transport the animated bend plane with the ankle axis. Projecting
            // the old knee directly onto the new axis can flip a nearly straight
            // knee to the back when ground contact moves the ankle.
            Vector3 animatedAxis = (ankle - hip).normalized;
            Vector3 currentKnee = knee - hip;
            Vector3 bend = Vector3.ProjectOnPlane(currentKnee, animatedAxis);
            bend = Quaternion.FromToRotation(animatedAxis, axis) * bend;
            Vector3 forwardBend = Vector3.ProjectOnPlane(forward, axis);
            if (bend.sqrMagnitude < 0.0001f ||
                Vector3.Dot(bend, forwardBend) < 0f)
                bend = forwardBend;
            if (bend.sqrMagnitude < 0.0001f)
                bend = Vector3.ProjectOnPlane(Vector3.up, axis);
            if (bend.sqrMagnitude < 0.0001f) return;
            bend.Normalize();
            float along = (upper * upper - lower * lower + distance * distance) /
                (2f * distance);
            float across = Mathf.Sqrt(Mathf.Max(
                0f, upper * upper - along * along));
            Vector3 kneeGoal = hip + axis * along + bend * across;
            Vector3 oldUpper = shin.position - thigh.position;
            Vector3 newUpper = kneeGoal - thigh.position;
            if (oldUpper.sqrMagnitude < 0.0001f ||
                newUpper.sqrMagnitude < 0.0001f) return;
            Quaternion footRotation = foot.rotation;
            thigh.rotation = Quaternion.FromToRotation(oldUpper, newUpper) *
                thigh.rotation;
            Vector3 oldLower = foot.position - shin.position;
            Vector3 newLower = targetFoot - shin.position;
            if (oldLower.sqrMagnitude > 0.0001f &&
                newLower.sqrMagnitude > 0.0001f)
                shin.rotation = Quaternion.FromToRotation(oldLower, newLower) *
                    shin.rotation;
            foot.rotation = footRotation;
        }

        private void CorrectReversedSwingKnee(Transform thigh, Transform shin,
            Transform foot)
        {
            Vector3 axis = (foot.position - thigh.position).normalized;
            Vector3 forwardBend = Vector3.ProjectOnPlane(transform.forward, axis);
            if (forwardBend.sqrMagnitude < 0.01f) return;
            Vector3 bend = Vector3.ProjectOnPlane(shin.position - thigh.position, axis);
            if (Vector3.Dot(bend, forwardBend.normalized) < -0.015f)
                PlantLegToTarget(thigh, shin, foot, foot.position, transform.forward, 1f);
        }

        private static void PlantCrouchLeg(Transform thigh, Transform shin,
            Transform foot, float ankleWorldY, Vector3 forward, float weight)
        {
            if (foot == null) return;
            Vector3 targetFoot = foot.position;
            targetFoot.y = ankleWorldY;
            PlantLegToTarget(thigh, shin, foot, targetFoot, forward, weight);
        }

        // One non-allocating ground sample per foot on low stances; unlike
        // motor-bottom height this works on bridge and sloped terrain.
        private float SupportedAnkleY(Transform foot, float clearance,
            out Vector3 supportNormal)
        {
            supportNormal = Vector3.up;
            // A ray miss is not a floor. At ledges, gaps or non-walkable
            // surfaces, preserve the authored unsupported foot instead of
            // inventing a support plane at the motor origin.
            if (foot == null) return float.NaN;
            Vector3 origin = foot.position + Vector3.up * 0.70f;
            int worldMask = Physics.DefaultRaycastLayers & ~(1 << gameObject.layer);
            Ray ray = new Ray(origin, Vector3.down);
            int count = Physics.RaycastNonAlloc(ray,
                ankleSupportHits, 1.70f, worldMask, QueryTriggerInteraction.Ignore);
            RaycastHit[] hits = ankleSupportHits;
            if (count >= ankleSupportHits.Length)
            {
                // A full NonAlloc buffer is unordered and may omit the nearest
                // bridge/step surface. Allocate only on rare saturation rather
                // than letting a crowded prop cluster make the boot float.
                hits = Physics.RaycastAll(ray, 1.70f, worldMask,
                    QueryTriggerInteraction.Ignore);
                count = hits.Length;
            }
            float closest = float.PositiveInfinity;
            float floor = float.NaN;
            // Plant only on surfaces the authoritative CharacterController can
            // actually stand/walk on. The old fixed normal.y >= 0.55 gate
            // accepted ~57-degree faces even though the saved player capsule
            // uses a 46-degree slope limit, making boots stick to steep rock
            // sides while the body correctly slid away. A small tolerance
            // absorbs triangle-normal noise at road/terrain seams.
            float slopeLimit = controller != null ? controller.slopeLimit : 46f;
            float minWalkableNormalY = Mathf.Cos(
                Mathf.Clamp(slopeLimit + 3f, 0f, 89f) * Mathf.Deg2Rad);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = hits[i];
                if (hit.collider == null || hit.normal.y < minWalkableNormalY ||
                    hit.collider.transform.IsChildOf(transform) ||
                    hit.distance >= closest) continue;
                closest = hit.distance;
                floor = hit.point.y + clearance;
                supportNormal = hit.normal;
            }
            return floor;
        }

        // Upright locomotion previously relied only on clip Foot IK. Keep a
        // bounded world-space stance-foot lock near real support so the planted
        // boot does not skate while the root advances; release for swing/teleport.
        // Existing support hits are reused, so this adds no new physics queries.
        private struct AnkleSupportSample
        {
            public Transform foot;
            public Vector3 footPosition, normal;
            public float height;
        }

        private void PlantUprightContact(Transform thigh, Transform shin,
            Transform foot, float planarSpeed, float uprightWeight,
            AnkleSupportSample coarseSupport, ref bool plantLocked, ref Vector3 plantWorld)
        {
            if (thigh == null || shin == null || foot == null ||
                uprightWeight <= 0.001f)
            {
                plantLocked = false;
                return;
            }

            // The whole-rig correction already sampled the lower boot on
            // this frame. Reuse it only while the exact ray origin still
            // matches, including after pelvis warping or a root correction.
            Vector3 supportNormal = coarseSupport.normal;
            float supportY = coarseSupport.foot == foot &&
                coarseSupport.footPosition.Equals(foot.position)
                ? coarseSupport.height
                : SupportedAnkleY(foot, UprightAnkleClearanceMetres, out supportNormal);
            // Never invent a floor at ledges/gaps. An unsupported foot keeps
            // authored motion and any stale world lock is discarded immediately.
            if (float.IsNaN(supportY))
            {
                plantLocked = false;
                CorrectReversedSwingKnee(thigh, shin, foot);
                return;
            }

            float gap = foot.position.y - supportY;
            float contactWeight = gap <= 0f ? 1f :
                1f - Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(0.025f, 0.14f, gap));
            if (contactWeight <= 0.001f)
            {
                plantLocked = false;
                CorrectReversedSwingKnee(thigh, shin, foot);
                return;
            }

            float speedWeight = Mathf.Lerp(0.95f, 0.62f,
                Mathf.InverseLerp(0.8f, fullRunSpeed, planarSpeed));
            float solveWeight = gap <= 0f ? 1f : speedWeight;
            float finalWeight = contactWeight * solveWeight *
                Mathf.Clamp01(uprightWeight);

            if (gap > UprightPlantReleaseGapMetres || finalWeight < 0.16f)
                plantLocked = false;
            if (!plantLocked && gap <= UprightPlantEnterGapMetres &&
                finalWeight >= 0.58f)
            {
                plantWorld = foot.position;
                plantWorld.y = supportY;
                plantLocked = true;
            }

            Vector3 targetFoot = foot.position;
            targetFoot.y = supportY;
            if (plantLocked)
            {
                // The surface height may change under the same stance point,
                // but horizontal contact remains world-locked until swing.
                plantWorld.y = supportY;
                Vector3 horizontalError = Vector3.ProjectOnPlane(
                    plantWorld - foot.position, transform.up);
                float errorDistance = horizontalError.magnitude;
                if (errorDistance > UprightPlantBreakDistanceMetres)
                {
                    plantLocked = false;
                }
                else
                {
                    float speed01 = Mathf.InverseLerp(
                        2.8f, fullRunSpeed, planarSpeed);
                    float maxCorrection = Mathf.Lerp(0.105f, 0.072f, speed01);
                    targetFoot += Vector3.ClampMagnitude(
                        horizontalError, maxCorrection);
                }
            }

            PlantLegToTarget(thigh, shin, foot, targetFoot, transform.forward,
                finalWeight);
            AlignFootToSupport(foot, supportNormal, finalWeight, 32f, 0.85f);
        }

        private void AlignFootToSupport(Transform foot, Vector3 supportNormal,
            float solveWeight, float maxDegrees, float blendScale)
        {
            if (foot == null || supportNormal.y >= 0.999f ||
                solveWeight <= 0.001f) return;
            Quaternion slopeTilt = Quaternion.FromToRotation(
                transform.up, supportNormal);
            slopeTilt = Quaternion.RotateTowards(
                Quaternion.identity, slopeTilt, maxDegrees);
            foot.rotation = Quaternion.Slerp(foot.rotation,
                slopeTilt * foot.rotation,
                Mathf.Clamp01(solveWeight * blendScale));
        }

        private static void LoopPlayable(AnimationClipPlayable playable, AnimationClip clip, double cycle)
        {
            if (!playable.IsValid() || clip == null || clip.length < 0.02f) return;
            double seconds = cycle * clip.length;
            playable.SetTime(seconds - Math.Floor(seconds / clip.length) * clip.length);
        }

        internal void SetPresentationPaused(bool paused)
        {
            if (presentationPaused == paused || !graphReady) return;
            presentationPaused = paused;
            motionHistoryReady = false;
            if (paused) graph.Stop();
            else graph.Play();
        }

        private void LateUpdate()
        {
            if (presentationPaused || visual == null || controller == null) return;
            // Missing optional components are retried at most once per second,
            // instead of issuing native GetComponent calls at 60/120 Hz.
            if ((actions == null || aerialDrop == null ||
                 (weaponInventory == null && actions != null && actions.IsArmed)) &&
                Time.unscaledTime >= nextOptionalComponentRefresh)
            {
                nextOptionalComponentRefresh = Time.unscaledTime + 1f;
                if (actions == null) actions = GetComponent<AarisBattleActions>();
                if (aerialDrop == null) aerialDrop = GetComponent<AarisOfflineDrop>();
                if (weaponInventory == null && actions != null && actions.IsArmed)
                    weaponInventory = GetComponent<AarisWeaponInventory>();
            }

            float dt = Mathf.Min(Time.deltaTime, 0.15f);
            float alpha = 1f - Mathf.Exp(-dt / Mathf.Max(0.04f, blendResponseSeconds));
            bool crouching = actions != null &&
                actions.Stance == AarisBattleActions.PlayerStance.Crouching;
            bool prone = actions != null &&
                actions.Stance == AarisBattleActions.PlayerStance.Prone;
            // The collision capsule responds immediately for safety, while
            // the rendered body changes stance on human-scale timing. A prone
            // body needs appreciably longer than a crouch so lying down/getting
            // up reads as weight transfer instead of an 85 ms pose morph.
            // Exponential time constants preserve the same feel from 10-120 FPS.
            float crouchAlpha = 1f - Mathf.Exp(-dt / (crouching ? 0.11f : 0.16f));
            float proneAlpha = 1f - Mathf.Exp(-dt / (prone ? 0.17f : 0.24f));
            crouchBlend = Mathf.Lerp(crouchBlend, crouching ? 1f : 0f, crouchAlpha);
            proneBlend = Mathf.Lerp(proneBlend, prone ? 1f : 0f, proneAlpha);

            bool authoredProne = proneClip != null && crawlClip != null;
            Vector3 velocity = controller.velocity;
            // Share the existing 130 ms terrain-contact grace between foot
            // timing and balance. A one-frame stair seam is not a jump and
            // must not abruptly drop strafe/turn lean. A real takeoff bypasses
            // the grace immediately through the motor's takeoff timestamp.
            bool groundedNow = controller.isGrounded;
            float now = Time.unscaledTime;
            if (groundedNow) lastReliableGroundContact = now;
            float jumpAge = playerMotor != null ? now - playerMotor.LastJumpStartedTime : 10f;
            bool deliberateJump = jumpAge >= 0f && jumpAge < 0.65f;
            bool visuallyAirborne = !groundedNow &&
                (deliberateJump || now - lastReliableGroundContact > 0.13f);
            // A crouch-to-jump action stands the capsule instantly, but its
            // old animation blends linger. Release that pose during takeoff
            // to avoid crouched limbs mixing with the airborne jump cycle.
            float takeoffStanceSuppression = deliberateJump &&
                actions != null &&
                actions.Stance == AarisBattleActions.PlayerStance.Standing
                ? Mathf.Clamp01(jumpAge / 0.12f) : 0f;
            float effectiveCrouchBlend = crouchBlend * (1f - takeoffStanceSuppression);
            float effectiveProneBlend = proneBlend * (1f - takeoffStanceSuppression);
            float planar = new Vector2(velocity.x, velocity.z).magnitude;
            float leaning = Mathf.InverseLerp(3.4f, fullRunSpeed, planar) *
                sprintForwardLeanDegrees * (primaryLoaded ? 0.8f : 1f) * (1f - proneBlend);
            // The upper body shifts its centre of mass into lateral strafes.
            // This is visual-only; weapon aim and the physical capsule keep
            // their authoritative yaw and position. Fade it out while prone
            // or airborne so the body never rolls through nearby cover.
            float strafeSpeed = Vector3.Dot(velocity, transform.right);
            float bankTarget = !visuallyAirborne
                ? -Mathf.Clamp(strafeSpeed / Mathf.Max(1f, fullRunSpeed), -1f, 1f) *
                    strafeBankDegrees * (1f - proneBlend)
                : 0f;
            smoothedStrafeBank = Mathf.Lerp(smoothedStrafeBank, bankTarget, alpha);

            // A speed-only lean cannot convey the weight transfer at takeoff,
            // braking or while changing direction. Sample real capsule motion,
            // never joystick intention: rejected wall movement cannot fake a lean.
            // Initialise the history first so scene spawn/teleport has no yaw spike.
            Vector3 planarVelocity = new Vector3(velocity.x, 0f, velocity.z);
            // Teleport, respawn and vehicle hand-off are NOT acceleration or a
            // human-scale pivot. Reset the sampled gait balance before drawing;
            // a normal 4.8 m/s frame (even at 10 FPS) is far below two metres.
            Vector3 motorPosition = transform.position;
            Vector3 frameTravel = motorPosition - lastMotorPosition;
            bool movementDiscontinuity = !controller.enabled ||
                (motionHistoryReady && frameTravel.sqrMagnitude > 4f);
            // Sample actual capsule displacement BEFORE updating motion history.
            // A long Android frame may exceed the capped blend delta, but the
            // feet still have to cover the entire collision-resolved distance.
            // Teleports/vehicle handoffs must never wind the gait forward.
            float resolvedGroundTravelMetres = motionHistoryReady && !movementDiscontinuity
                ? Mathf.Sqrt(frameTravel.x * frameTravel.x + frameTravel.z * frameTravel.z)
                : 0f;
            if (movementDiscontinuity)
            {
                motionHistoryReady = false;
                smoothedAccelerationPitch = 0f;
                smoothedTurnBank = 0f;
                smoothedOrientationWarpYaw = 0f;
                // A respawn or vehicle hand-off must also drop the previous
                // lateral strafe lean. Otherwise the new stationary pose can
                // remain banked for several frames despite zero local motion.
                smoothedStrafeBank = 0f;
            }
            float currentYaw = transform.eulerAngles.y;
            float forwardAcceleration = 0f;
            float yawRate = 0f;
            // dt is deliberately capped for stable visual blend smoothing,
            // but acceleration and yaw samples require the REAL frame interval.
            float motionDt = Time.deltaTime;
            if (motionHistoryReady && motionDt > 0.0001f)
            {
                forwardAcceleration = Vector3.Dot(
                    (planarVelocity - lastPlanarVelocity) / motionDt, transform.forward);
                yawRate = Mathf.DeltaAngle(lastFacingYaw, currentYaw) / motionDt;
            }
            motionHistoryReady = controller.enabled;
            lastPlanarVelocity = planarVelocity;
            lastFacingYaw = currentYaw;
            lastMotorPosition = motorPosition;
            // Suppress standing balance corrections while crouched, prone or
            // airborne: these poses already have authored limb/body alignment.
            float standingShare = !visuallyAirborne
                ? 1f - Mathf.Clamp01(crouchBlend + proneBlend) : 0f;
            float accelerationPitchTarget =
                Mathf.Clamp(forwardAcceleration / 13f, -1f, 1f) *
                2.2f * standingShare;
            float turnBankTarget =
                -Mathf.Clamp(yawRate / 240f, -1f, 1f) * 2.5f *
                Mathf.InverseLerp(0.8f, 3.4f, planar) * standingShare;
            smoothedAccelerationPitch = Mathf.Lerp(smoothedAccelerationPitch,
                accelerationPitchTarget, 1f - Mathf.Exp(-dt / 0.10f));
            smoothedTurnBank = Mathf.Lerp(smoothedTurnBank,
                turnBankTarget, 1f - Mathf.Exp(-dt / 0.13f));

            // Orientation warping fills the largest remaining gap of a
            // forward-only locomotion set: sideways combat movement. Bend the
            // lower body toward the ACTUAL capsule velocity, not stick intent.
            // Backpedal keeps the existing reversed gait instead of twisting
            // the pelvis nearly 180 degrees. Sprint is already forward-only.
            float orientationWarpTarget = 0f;
            if (standingShare > 0.001f && planar > 0.15f)
            {
                float travelForward = Vector3.Dot(planarVelocity, transform.forward);
                float travelSide = Vector3.Dot(planarVelocity, transform.right);
                float travelAngle = Mathf.Atan2(travelSide, travelForward) *
                    Mathf.Rad2Deg;
                float absTravelAngle = Mathf.Abs(travelAngle);
                float backwardFade = 1f - Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(110f, 170f, absTravelAngle));
                orientationWarpTarget = Mathf.Clamp(
                    travelAngle * 0.50f, -44f, 44f) *
                    backwardFade * standingShare;
            }
            smoothedOrientationWarpYaw = Mathf.Lerp(
                smoothedOrientationWarpYaw, orientationWarpTarget,
                1f - Mathf.Exp(-dt / 0.085f));
            // Keep the grounded mesh root upright. Pitching the entire rig
            // pivots the feet away from the terrain, especially on slopes.
            // The standing balance lean is applied to the humanoid spine below.
            Quaternion desiredRotation = restingLocalRotation;
            // Visual-only skydiving pose; never tilt the physical CharacterController.
            // Choose the final rotation before touching the hierarchy: the
            // aerial override previously caused two native Transform writes.
            if (aerialDrop != null && !aerialDrop.HasLanded)
            {
                float aerialPitch = aerialDrop.IsFreefall ? 64f :
                    aerialDrop.IsUnderCanopy ? 6f : 0f;
                desiredRotation = restingLocalRotation *
                    Quaternion.Euler(aerialPitch, 0f, 0f);
            }
            Transform visualRoot = visual.transform;
            // An unchanged standing or idle pose should not dirty the
            // character hierarchy on every mobile render frame.
            if (visualRoot.localRotation != desiredRotation)
                visualRoot.localRotation = desiredRotation;

            float landingAge = playerMotor != null
                ? Time.unscaledTime - playerMotor.LastLandingTime
                : 10f;
            // Smaller steps compress less than a hard landing. Keep the visual
            // offset under 7 cm, without extra physics queries or allocations.
            float landingImpact = playerMotor != null ? playerMotor.LastLandingImpactSpeed : 0f;
            float impactBlend = Mathf.Clamp01((landingImpact - 1f) / 9f);
            float landingDip = landingAge >= 0f && landingAge <= 0.2f
                ? Mathf.Sin(Mathf.PI * landingAge / 0.2f) * landingCompressionMetres *
                    Mathf.Lerp(0.55f, 1.15f, impactBlend)
                : 0f;
            // Real 6000.3.26f1 PlayMode measurement: standing soldier shoes
            // floated 0.75m while capsule bottom was only 0.035m above the
            // terrain. The static import fit cannot calibrate animated poses.
            // Both validated soldier rigs get this one-time runtime scale fit.
            bool uprightGroundedSoldier = CalibratedSoldierLoaded && graphReady &&
                groundedNow && !visuallyAirborne &&
                (aerialDrop == null || aerialDrop.HasLanded) &&
                actions != null &&
                actions.Stance == AarisBattleActions.PlayerStance.Standing &&
                effectiveCrouchBlend < 0.01f &&
                effectiveProneBlend < 0.01f;
            // Bone-span scale calibration still waits for a true idle pose.
            // Vertical shoe grounding below is safe while moving because it
            // samples the lower (stance) foot against the actual world floor.
            bool stableSoldier = uprightGroundedSoldier && planar < 0.15f;
            if (!groundedRigCalibrated && stableSoldier && bodySkin != null &&
                proneHeadBone != null && leftFootBone != null &&
                rightFootBone != null)
            {
                // A SkinnedMeshRenderer.bounds AABB can include unused
                // animation extremes (native prone bounds even crossed the
                // ground by 0.6m). Calibrate against measured HEAD/ANKLES,
                // not an inaccurate mesh AABB or unanimated T-pose bounds.
                float boneSpan = proneHeadBone.position.y -
                    Mathf.Min(leftFootBone.position.y, rightFootBone.position.y);
                if (boneSpan > 0.80f && boneSpan < 2.50f)
                {
                    // 1.78m humanoid includes footwear/helmet; the head-to-
                    // ankle bone span should be slightly shorter (1.64m).
                    float scaleFactor = Mathf.Clamp(
                        (TargetHeightMetres - 0.14f) / boneSpan, 0.85f, 1.5f);
                    if (Mathf.Abs(scaleFactor - 1f) > 0.025f)
                        visualRoot.localScale *= scaleFactor;
                    groundedRigCalibrated = true;
                }
            }
            // The imported crouch clip leaves the head above the short collision
            // capsule. Replant the knees after lowering this visual-only rig.
            // Missing prone clips must not activate the old 80-degree root-tilt
            // illusion; use a lowered crouch until the actual FBX imports work.
            float crouchRigWeight = authoredProne
                ? effectiveCrouchBlend * (1f - effectiveProneBlend)
                : Mathf.Clamp01(effectiveCrouchBlend + effectiveProneBlend);
            // Legacy clips needed a large visual root drop because their
            // retargeted crouch stayed too tall. The natural mocap is already
            // physically crouched; applying that legacy drop would bury the
            // hips. Capsule headroom + real foot support remain authoritative.
            float crouchLowering = naturalMocapLoaded ? 0f :
                (realisticSoldierLoaded ? RealisticSoldierCrouchRigLowering :
                    CrouchRigLowering) * crouchRigWeight;
            // Source crawling hips/ankles float above the floor by about 22 cm.
            float proneLowering = primaryLoaded && proneClip != null &&
                crawlClip != null ?
                (realisticSoldierLoaded ? RealisticSoldierProneRigLowering : ProneRigLowering) * effectiveProneBlend : 0f;
            // Preserve calibrated standing-ground offset through takeoff:
            // switching it off in the air would instantly teleport the model
            // upwards by the original ~0.75m shoe gap.
            float uprightWeight = 1f - Mathf.Clamp01(
                effectiveCrouchBlend + effectiveProneBlend);
            Vector3 desiredPosition = restingLocalPosition +
                new Vector3(0f, -landingDip - crouchLowering -
                    proneLowering + standingShoeOffset * uprightWeight, 0f);
            if (visualRoot.localPosition != desiredPosition)
                visualRoot.localPosition = desiredPosition;
            AnkleSupportSample coarseSupport = default;
            if (uprightGroundedSoldier &&
                leftFootBone != null && rightFootBone != null)
            {
                // Coarse whole-rig recovery must not wait for an idle frame.
                // A player who runs immediately after landing can otherwise
                // keep a 20-40cm floating Humanoid forever. Use the lower
                // stance foot, sample the real Terrain/MeshCollider beneath
                // it, and only move the whole rig for a clearly global error.
                Transform stanceFoot = leftFootBone.position.y <= rightFootBone.position.y
                    ? leftFootBone : rightFootBone;
                float supportY = SupportedAnkleY(stanceFoot,
                    UprightAnkleClearanceMetres, out Vector3 supportNormal);
                coarseSupport = new AnkleSupportSample
                {
                    foot = stanceFoot, footPosition = stanceFoot.position,
                    height = supportY, normal = supportNormal
                };
                float stanceGap = stanceFoot.position.y - supportY;
                if (!float.IsNaN(stanceGap) && Mathf.Abs(stanceGap) > 0.07f)
                {
                    float error = -stanceGap;
                    float adjustment = Mathf.Clamp(error, -0.35f, 0.18f) *
                        Mathf.Clamp01(dt * 14f);
                    float corrected = Mathf.Clamp(
                        standingShoeOffset + adjustment, -1.4f, 0.30f);
                    if (Mathf.Abs(corrected - standingShoeOffset) > 0.00025f)
                    {
                        visualRoot.localPosition += Vector3.up *
                            (corrected - standingShoeOffset);
                        standingShoeOffset = corrected;
                    }
                }
            }
            // Match the stance headroom to the physical 1.14m/0.62m
            // capsule after any retargeting scale adjustment. Never move
            // collisions/camera; the skeleton and its IK are visual-only.
            float lowPose = Mathf.Clamp01(effectiveCrouchBlend + effectiveProneBlend);
            if (!visuallyAirborne && graphReady && lowPose > 0.01f &&
                proneHeadBone != null)
            {
                float capsuleTop = transform.TransformPoint(controller.center +
                    Vector3.up * controller.height * 0.5f).y;
                float headInset = Mathf.Lerp(0.10f, 0.20f,
                    Mathf.Clamp01(effectiveProneBlend));
                float excess = proneHeadBone.position.y -
                    (capsuleTop - headInset);
                if (excess > 0.005f)
                    visualRoot.localPosition += Vector3.down *
                        Mathf.Min(excess, 0.45f) * lowPose;
            }
            // Rotate the lower-body parent before terrain planting. The
            // existing analytic leg solve then restores exact shoe contact on
            // slopes/steps, so orientation compensation cannot introduce float.
            if (!visuallyAirborne && graphReady && locomotionHipsBone != null &&
                Mathf.Abs(smoothedOrientationWarpYaw) > 0.01f)
            {
                locomotionHipsBone.rotation =
                    Quaternion.AngleAxis(smoothedOrientationWarpYaw, transform.up) *
                    locomotionHipsBone.rotation;
            }

            // Resolve each knee once when crouch and prone blend together.
            // Previously the second pass twisted an already-solved lower leg,
            // causing foot jitter and bent knees during rapid stance switching.
            // The per-frame collision queries and allocations cannot increase.
            float proneRigWeight = authoredProne ? effectiveProneBlend : 0f;
            float totalRigWeight = crouchRigWeight + proneRigWeight;
            float ankleRigWeight = !visuallyAirborne && graphReady
                ? Mathf.Clamp01(totalRigWeight) : 0f;
            bool useUprightContact = !visuallyAirborne && graphReady &&
                groundedNow && lowPose < 0.02f;
            if (!useUprightContact && ankleRigWeight > 0.005f)
            {
                float proneShare = proneRigWeight / Mathf.Max(totalRigWeight, 0.0001f);
                float ankleClearance = Mathf.Lerp(PlantedAnkleMetres,
                    ProneAnkleClearanceMetres, proneShare);
                Vector3 kneeForward = proneShare < 0.5f ? transform.forward : Vector3.zero;
                float leftAnkleY = SupportedAnkleY(leftFootBone,
                    ankleClearance, out Vector3 leftSupportNormal);
                float rightAnkleY = SupportedAnkleY(rightFootBone,
                    ankleClearance, out Vector3 rightSupportNormal);
                if (!float.IsNaN(leftAnkleY))
                    PlantCrouchLeg(leftThigh, leftShin, leftFootBone,
                        leftAnkleY, kneeForward, ankleRigWeight);
                if (!float.IsNaN(rightAnkleY))
                    PlantCrouchLeg(rightThigh, rightShin, rightFootBone,
                        rightAnkleY, kneeForward, ankleRigWeight);
                // Crouch/prone used to correct only ankle height, leaving the
                // sole visually cutting through a sloped road or bridge deck.
                // Reuse the exact two support normals sampled above; no extra
                // Physics query is introduced by this low-stance correction.
                if (!float.IsNaN(leftAnkleY))
                    AlignFootToSupport(leftFootBone, leftSupportNormal,
                        ankleRigWeight, 26f, 0.72f);
                if (!float.IsNaN(rightAnkleY))
                    AlignFootToSupport(rightFootBone, rightSupportNormal,
                        ankleRigWeight, 26f, 0.72f);
            }
            // Standing/walk/run need world-space contact too. Apply the same
            // analytic leg solve only to a near-ground stance foot; high swing
            // feet remain untouched, so stride length and cadence stay authored.
            // Give upright contact priority near standing so both modes
            // never rotate the same knee in one frame.
            if (useUprightContact)
            {
                float uprightContactWeight = Mathf.Clamp01(1f - lowPose * 4f);
                PlantUprightContact(leftThigh, leftShin, leftFootBone,
                    planar, uprightContactWeight, coarseSupport, ref leftUprightPlantLocked,
                    ref leftUprightPlantWorld);
                PlantUprightContact(rightThigh, rightShin, rightFootBone,
                    planar, uprightContactWeight, coarseSupport, ref rightUprightPlantLocked,
                    ref rightUprightPlantWorld);
            }
            else
            {
                leftUprightPlantLocked = false;
                rightUprightPlantLocked = false;
            }

            // The authored crawling clip places the head higher than the
            // 62cm prone capsule while its ankles already touch the terrain.
            // Pivot the chest gently forward instead of sinking the entire
            // humanoid rig and burying the shoes. Only animation transforms
            // change; this never adjusts collisions or camera authority.
            if (!visuallyAirborne && graphReady && authoredProne &&
                effectiveProneBlend > 0.01f &&
                proneChestBone != null && proneHeadBone != null)
            {
                Vector3 headFromChest = proneHeadBone.position - proneChestBone.position;
                Vector3 horizontal = Vector3.ProjectOnPlane(headFromChest, Vector3.up);
                if (horizontal.sqrMagnitude > 0.0144f && headFromChest.y > 0f)
                {
                    float capTop = transform.TransformPoint(controller.center +
                        Vector3.up * controller.height * 0.5f).y;
                    float desiredHeadY = capTop - ProneHeadColliderInset;
                    float desiredDeltaY = Mathf.Min(headFromChest.y,
                        desiredHeadY - proneChestBone.position.y);
                    Vector3 wanted = horizontal + Vector3.up * desiredDeltaY;
                    if (wanted.sqrMagnitude > 0.0144f)
                    {
                        Quaternion tilt = Quaternion.FromToRotation(headFromChest, wanted);
                        tilt = Quaternion.Slerp(Quaternion.identity, tilt,
                            effectiveProneBlend);
                        tilt = Quaternion.RotateTowards(Quaternion.identity, tilt,
                            ProneChestCorrectionMaxDegrees);
                        proneChestBone.rotation = tilt * proneChestBone.rotation;
                    }
                }
            }

            // Additive spine balance keeps shoes and hips fixed under the
            // locomotion animation. Never apply this in crouch, prone or air.
            if (!visuallyAirborne && graphReady && balanceSpineBone != null &&
                standingShare > 0.001f)
            {
                float bodyPitch = Mathf.Clamp(leaning + smoothedAccelerationPitch,
                    -8f, 8f) * standingShare;
                float bodyRoll = Mathf.Clamp(smoothedStrafeBank + smoothedTurnBank,
                    -6f, 6f) * standingShare;
                // Counter most pelvis yaw in world-up space so rifle/ADS and
                // camera-facing torso stay stable while legs travel laterally.
                if (Mathf.Abs(smoothedOrientationWarpYaw) > 0.01f)
                    balanceSpineBone.rotation =
                        Quaternion.AngleAxis(-smoothedOrientationWarpYaw * 0.78f,
                            transform.up) * balanceSpineBone.rotation;
                balanceSpineBone.localRotation *= Quaternion.Euler(bodyPitch, 0f, bodyRoll);
            }

            // The same support decision now drives gait phase, airborne pose,
            // acceleration pitch and lateral bank without extra physics probes.

            if (graphReady)
            {
                float horizontal = planar;
                // Hide the pistol-ready upper-body pose when the real rifle is
                // sprint-holstered, so both arms retain their natural run cycle.
                bool armed = actions != null && actions.IsArmed;
                bool carrying = armed &&
                    (weaponInventory == null || !weaponInventory.IsSprintHolstered);
                float movementTarget = Mathf.InverseLerp(0.08f, 1.2f, horizontal);
                // Read the real motor ceilings instead of inferring sprint speed
                // from whether the gun is visually holstered. Holstering used to
                // flip an armed runner to the unarmed animation threshold, so the
                // sprint clip could never reach full weight at the armed speed cap.
                float jogCeiling = playerMotor != null
                    ? playerMotor.JogSpeedMetresPerSecond : 4.2f;
                float sprintCeiling = playerMotor != null
                    ? (armed ? playerMotor.ArmedSprintSpeedMetresPerSecond :
                        playerMotor.UnarmedSprintSpeedMetresPerSecond)
                    : (armed ? 5.7f : 6.5f);
                float runStart = Mathf.Max(1.8f, jogCeiling * 0.55f);
                float sprintStart = Mathf.Max(jogCeiling * 0.95f,
                    sprintCeiling * 0.72f);
                float runTarget = Mathf.InverseLerp(runStart, jogCeiling, horizontal);
                float sprintTarget = Mathf.InverseLerp(
                    sprintStart, sprintCeiling, horizontal);
                float airTarget = visuallyAirborne ? 1f : 0f;
                runBlend = Mathf.Lerp(runBlend, movementTarget, alpha);
                walkRunBlend = Mathf.Lerp(walkRunBlend, runTarget, alpha);
                sprintBlend = Mathf.Lerp(sprintBlend, sprintTarget, alpha);
                airborneBlend = Mathf.Lerp(airborneBlend, airTarget, alpha);

                // Rapid crouch/prone changes can overlap during exponential
                // smoothing. Normalize to one stance share, including during
                // an interrupted transition; never exceed the base mixer sum.
                float stanceTotal = effectiveCrouchBlend + effectiveProneBlend;
                float pose = Mathf.Clamp01(stanceTotal);
                float stanceScale = stanceTotal > 1f ? pose / stanceTotal : 1f;
                float crouchWeight = authoredProne ? effectiveCrouchBlend * stanceScale : pose;
                float proneWeight = authoredProne ? effectiveProneBlend * stanceScale : 0f;
                float crouchMoving = Mathf.InverseLerp(0.06f, 0.85f, horizontal);
                float crawlMoving = Mathf.InverseLerp(0.05f, 0.55f, horizontal);
                float grounded = (1f - airborneBlend) * (1f - pose);
                // The old binary velocity cut snapped between the frozen
                // ascent and fall poses near the jump apex. Blend both poses
                // continuously over the apex to preserve limbs and balance.
                float risingPose = Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(-1.75f, 0.75f, velocity.y));

                float takeoffAge = playerMotor != null
                    ? Time.unscaledTime - playerMotor.LastJumpStartedTime : 10f;
                float touchdownAge = playerMotor != null
                    ? Time.unscaledTime - playerMotor.LastLandingTime : 10f;
                float startWeight = airborneBlend > 0.02f && takeoffAge >= 0f &&
                    takeoffAge < 0.23f
                    ? Mathf.Clamp01((0.23f - takeoffAge) / 0.14f) : 0f;
                float landWeight = controller.isGrounded && touchdownAge >= 0f &&
                    touchdownAge < 0.28f
                    ? Mathf.Clamp01((0.28f - touchdownAge) / 0.18f) : 0f;
                float lastPunchAge = actions != null ? Time.time - actions.LastPunchTime : 10f;
                float lastShootAge = actions != null ? Time.time - actions.LastShootTime : 10f;
                float punchDuration = AarisBattleActions.PunchVisualDuration;
                float punchWeight = lastPunchAge >= 0f && lastPunchAge < punchDuration
                    ? Mathf.Min(Mathf.Clamp01(lastPunchAge / 0.07f),
                                Mathf.Clamp01((punchDuration - lastPunchAge) / 0.14f)) *
                        Mathf.Lerp(0.96f, 0.45f, proneBlend) : 0f;
                if (punchWeight > 0f)
                    punchPlayable.SetTime(punchClip.length * Mathf.Clamp01(lastPunchAge / punchDuration));
                float shootWeight = lastShootAge >= 0f && lastShootAge < 0.2f
                    ? 0.65f * Mathf.Min(Mathf.Clamp01(lastShootAge / 0.035f),
                                       Mathf.Clamp01((0.2f - lastShootAge) / 0.07f)) : 0f;

                // Strictly normalize mutually exclusive animation priorities.
                // Landing and a newly queued jump can overlap in the same
                // 0.28s window. Their weights must NOT sum above 1.
                if (startWeight > 0f) landWeight = 0f; // New takeoff wins.
                float primary = Mathf.Clamp01(startWeight + landWeight);
                float baseWeight = 1f - primary;
                if (shootWeight > 0f) punchWeight = 0f;
                // Upper body is an independent override layer; firing does
                // not steal weight from any locomotion/feet clips.
                float punchUpper = punchWeight * (1f - primary);
                float shootUpper = shootWeight * (1f - primary);
                float attackUpper = Mathf.Max(punchUpper, shootUpper);
                bool aim = weaponInventory != null && weaponInventory.Aiming;
                // Release the ready pose progressively through the fast jog;
                // sprint holstering still removes the layer entirely.
                float releaseArms = Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(Mathf.Max(2.8f, jogCeiling * 0.75f),
                        jogCeiling, horizontal));
                float carryTarget = !carrying ? 0f : aim ? 0.63f :
                    Mathf.Lerp(0.36f, 0.15f, releaseArms);
                if (prone) carryTarget *= 0.6f;
                readyPoseBlend = Mathf.Lerp(readyPoseBlend, carryTarget, alpha);
                float layerWeight = Mathf.Clamp01(Mathf.Max(readyPoseBlend, attackUpper));
                float attackRatio = layerWeight > 0.0001f
                    ? attackUpper / layerWeight : 0f;
                WriteWeightIfChanged(mixer, lastBaseWeights, 0, baseWeight * grounded * (1f - runBlend));
                WriteWeightIfChanged(mixer, lastBaseWeights, 1, baseWeight * grounded * runBlend * (1f - walkRunBlend));
                WriteWeightIfChanged(mixer, lastBaseWeights, 2, baseWeight * grounded * runBlend * walkRunBlend * (1f - sprintBlend));
                WriteWeightIfChanged(mixer, lastBaseWeights, 3, baseWeight * airborneBlend * risingPose * (1f - pose));
                WriteWeightIfChanged(mixer, lastBaseWeights, 4, baseWeight * airborneBlend * (1f - risingPose) * (1f - pose));
                WriteWeightIfChanged(mixer, lastBaseWeights, 5, baseWeight * crouchWeight * (1f - crouchMoving));
                WriteWeightIfChanged(mixer, lastBaseWeights, 6, baseWeight * crouchWeight * crouchMoving);
                WriteWeightIfChanged(mixer, lastBaseWeights, 7, startWeight);
                WriteWeightIfChanged(mixer, lastBaseWeights, 8, landWeight);
                WriteWeightIfChanged(mixer, lastBaseWeights, 9, baseWeight * grounded * runBlend * walkRunBlend * sprintBlend);
                WriteWeightIfChanged(mixer, lastBaseWeights, 10, baseWeight * proneWeight * (1f - crawlMoving));
                WriteWeightIfChanged(mixer, lastBaseWeights, 11, baseWeight * proneWeight * crawlMoving);
                WriteWeightIfChanged(weaponUpperMixer, lastUpperWeights, 0, 1f - attackRatio);
                WriteWeightIfChanged(weaponUpperMixer, lastUpperWeights, 1,
                    layerWeight > 0.0001f ? punchUpper / layerWeight : 0f);
                WriteWeightIfChanged(weaponUpperMixer, lastUpperWeights, 2,
                    layerWeight > 0.0001f ? shootUpper / layerWeight : 0f);
                WriteLayerWeightIfChanged(layerWeight);

                if (playerMotor != null && takeoffAge >= 0f &&
                    playerMotor.LastJumpStartedTime != seenTakeoff)
                {
                    seenTakeoff = playerMotor.LastJumpStartedTime;
                    jumpStartPlayable.SetTime(0d);
                }
                if (playerMotor != null && touchdownAge >= 0f &&
                    playerMotor.LastLandingTime != seenTouchdown)
                {
                    seenTouchdown = playerMotor.LastLandingTime;
                    jumpLandPlayable.SetTime(0d);
                }
                if (actions != null && actions.AttackSequence != lastVisualAttackSequence)
                {
                    lastVisualAttackSequence = actions.AttackSequence;
                    shootPlayable.SetTime(0d);
                }

                // Keep standing gait contacts in phase across walk, run and sprint.
                // Stance gaits use their own stride distances at the same motor speed.
                // The airborne presentation already tolerates 130ms of transient
                // ground loss at terrain seams and stair lips. Use that SAME
                // decision for foot timing: a one-frame isGrounded drop must
                // not freeze a moving foot, but a real jump must stop the gait.
                if (!visuallyAirborne)
                {
                    float standStride = Mathf.Lerp(1.25f,
                        Mathf.Lerp(2.55f, 3.65f, sprintBlend), walkRunBlend);
                    float distance = resolvedGroundTravelMetres;
                    // Camera-facing combat allows real backpedalling. A forward
                    // walk clip played in forward time while travelling backwards
                    // makes the feet visibly skate. Reverse the continuous phase,
                    // never jump the sample time, so contact remains continuous
                    // through a direction change, including crouch and crawl.
                    float forwardSpeed = Vector3.Dot(velocity, transform.forward);
                    // Hysteresis prevents backward feet switching on analogue noise,
                    // strafe blends and shallow wall slides.
                    float sideSpeed = Mathf.Abs(strafeSpeed);
                    if (horizontal < 0.06f)
                        gaitDirection = 1f;
                    else if (forwardSpeed < -0.32f &&
                             -forwardSpeed > sideSpeed * 0.75f)
                        gaitDirection = -1f;
                    else if (forwardSpeed > -0.08f ||
                             sideSpeed > -forwardSpeed * 1.40f)
                        gaitDirection = 1f;
                    float phaseDirection = gaitDirection;
                    if (pose < 0.999f)
                        locomotionPhase = Mathf.Repeat(locomotionPhase +
                            phaseDirection * distance / standStride, 1f);
                    if (crouchWeight > 0.001f)
                        crouchPhase = Mathf.Repeat(crouchPhase +
                            phaseDirection * distance / 1.40f, 1f);
                    if (proneWeight > 0.001f)
                        crawlPhase = Mathf.Repeat(crawlPhase +
                            phaseDirection * distance / 0.90f, 1f);
                }
                // After a complete stop, resume the next step at a consistent
                // gait-contact sample instead of an arbitrary mid-stride pose.
                // Only re-phase an effectively invisible gait while grounded:
                // resetting an active clip would jerk the visible feet.
                if (!visuallyAirborne && horizontal < 0.045f)
                {
                    if (pose < 0.01f && runBlend < 0.01f)
                        locomotionPhase = 0f;
                    if (crouchWeight > 0.99f && crouchMoving < 0.01f)
                        crouchPhase = 0f;
                    if (proneWeight > 0.99f && crawlMoving < 0.01f)
                        crawlPhase = 0f;
                }
                // Breathing/idle must keep animating at all frame rates.
                LoopPlayable(idlePlayable, idleClip, Time.timeAsDouble / Math.Max(0.02, idleClip.length));
                // Distance-driven clips are frozen while stationary: their
                // phases do not advance, so SetTime would be identical.
                if (lastLocomotionPhase != locomotionPhase)
                {
                    LoopPlayable(walkPlayable, walkClip, locomotionPhase);
                    LoopPlayable(runPlayable, runClip, locomotionPhase + runPhaseOffset);
                    LoopPlayable(sprintPlayable, sprintClip, locomotionPhase + sprintPhaseOffset);
                    lastLocomotionPhase = locomotionPhase;
                }
                if (lastCrouchPhase != crouchPhase)
                {
                    LoopPlayable(crouchWalkPlayable, crouchWalkClip, crouchPhase);
                    lastCrouchPhase = crouchPhase;
                }
                if (lastCrawlPhase != crawlPhase)
                {
                    LoopPlayable(crawlPlayable, crawlClip, crawlPhase);
                    lastCrawlPhase = crawlPhase;
                }
            }

            // In-place upper-arm punch pulse. Only the animation skeleton is
            // affected; hit registration lives in AarisBattleActions.
            if (punchArm != null && actions != null && punchClip == idleClip)
            {
                float t = (Time.time - actions.LastPunchTime) / 0.36f;
                if (t >= 0f && t <= 1f)
                    punchArm.localRotation *= Quaternion.Euler(
                        -72f * Mathf.Sin(t * Mathf.PI), 0f, 0f);
            }
        }

        private void OnDestroy()
        {
            if (graph.IsValid()) graph.Destroy();
            if (upperBodyMask != null) Destroy(upperBodyMask);
            if (runtimeSkin != null) Destroy(runtimeSkin);
            foreach (var material in realisticSkinMaterials.Values)
                if (material != null) Destroy(material);
            realisticSkinMaterials.Clear();
            if (placeholderRenderers != null)
                foreach (var renderer in placeholderRenderers)
                    if (IsLegacyPlaceholderRenderer(renderer)) renderer.enabled = true;
        }
    }
}
