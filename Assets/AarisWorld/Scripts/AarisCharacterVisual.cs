using System;
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
        private const string PrimaryModel = "AarisCharacters/Primary/RealisticSoldier/RealisticSoldier";
        private const string PreviousPrimaryModel = "AarisCharacters/Primary/Player";
        private const string FallbackModel = "AarisCharacters/Kenney/characterMedium";
        private const string KenneyClips = "AarisCharacters/Kenney/";
        private const string PrimaryClips = "AarisCharacters/Primary/Animations/";
        private const float TargetHeightMetres = 1.78f;

        [SerializeField, Range(-180f, 180f)] private float modelYawOffset;
        [SerializeField, Range(0.1f, 0.8f)] private float blendResponseSeconds = 0.2f;
        [SerializeField] private float fullRunSpeed = 4.8f;
        [SerializeField, Range(0f, 8f)] private float sprintForwardLeanDegrees = 4f;
        [SerializeField, Range(0f, 6f)] private float strafeBankDegrees = 3f;
        [SerializeField, Range(0f, 0.12f)] private float landingCompressionMetres = 0.055f;

        private CharacterController controller;
        private GameObject visual;
        private MeshRenderer[] placeholderRenderers;
        private Animator animator;
        private Material runtimeSkin;
        private PlayableGraph graph;
        private AnimationMixerPlayable mixer;
        private AnimationMixerPlayable weaponUpperMixer;
        private AnimationLayerMixerPlayable layered;
        private AvatarMask upperBodyMask;
        private AnimationClipPlayable readyHoldPlayable;
        private AarisWeaponInventory weaponInventory;
        private float readyPoseBlend;
        private bool graphReady;
        public bool HasWorkingHumanoid => graphReady && visual != null && animator != null;
        private float runBlend;
        private float airborneBlend;
        // Ignore one-frame grounded loss across terrain seams without delaying a deliberate jump.
        private float lastReliableGroundContact;
        private float walkRunBlend;
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
        private Transform balanceSpineBone;
        private const float ProneChestCorrectionMaxDegrees = 24f;
        private const float ProneHeadColliderInset = 0.10f;
        // The imported soldier's animated standing bounds are shorter than
        // its static FBX bounds. Calibrate once against the ACTUAL idle mesh.
        private SkinnedMeshRenderer bodySkin;
        private bool groundedRigCalibrated;
        private float standingShoeOffset;
        private readonly RaycastHit[] ankleSupportHits = new RaycastHit[12];
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
        private bool realisticSoldierLoaded;

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
            // Saved scenes can retain the old 6.5 m/s Inspector value even
            // after the motor's pace was corrected. Keep lean and foot timing
            // calibrated to the actual 4.8 m/s unarmed sprint ceiling.
            fullRunSpeed = Mathf.Min(fullRunSpeed, 4.8f);
            lastReliableGroundContact = Time.unscaledTime;
        }

        private void Start()
        {
            var source = Resources.Load<GameObject>(PrimaryModel);
            if (source == null)
                source = Resources.Load<GameObject>(PreviousPrimaryModel);
            primaryLoaded = source != null;
            realisticSoldierLoaded = source != null && source.name == "RealisticSoldier";
            if (source == null)
                source = Resources.Load<GameObject>(FallbackModel);
            if (source == null)
            {
                Debug.LogWarning("Aaris: no imported 3D character yet. Existing humanoid remains visible. Install the CC0 character pack.");
                enabled = false;
                return;
            }

            visual = Instantiate(source, transform, false);
            visual.name = "AarisRiggedCharacterVisual";
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.Euler(0f, modelYawOffset, 0f);
            SetLayerRecursively(visual.transform, gameObject.layer);

            animator = visual.GetComponentInChildren<Animator>(true);
            if (animator == null)
                animator = visual.AddComponent<Animator>();

            if (animator.avatar == null)
            {
                Avatar[] avatars = Resources.LoadAll<Avatar>(
                    source.name == "RealisticSoldier" ? PrimaryModel :
                    source.name == "Player" ? PreviousPrimaryModel : FallbackModel);
                for (int i = 0; i < avatars.Length; i++)
                {
                    if (avatars[i] != null && avatars[i].isValid && avatars[i].isHuman)
                    {
                        animator.avatar = avatars[i];
                        break;
                    }
                }
            }
            if (animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
            {
                Debug.LogWarning("Aaris: imported model has no valid Humanoid Avatar; keeping the original player visible.");
                Destroy(visual);
                visual = null;
                enabled = false;
                return;
            }

            if (!FitVisualToController())
            {
                Debug.LogWarning("Aaris: imported character has no usable render bounds; keeping original model.");
                Destroy(visual);
                visual = null;
                enabled = false;
                return;
            }

            TryApplySkin();
            bodySkin = visual.GetComponentInChildren<SkinnedMeshRenderer>(true);
            actions = GetComponent<AarisBattleActions>();
            playerMotor = GetComponent<AarisThirdPersonPlayer>();
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
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
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

        private bool FitVisualToController()
        {
            var renderers = visual.GetComponentsInChildren<Renderer>(true);
            Bounds combined = new Bounds();
            bool hasBounds = false;
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                if (!hasBounds) { combined = renderer.bounds; hasBounds = true; }
                else combined.Encapsulate(renderer.bounds);
            }
            if (!hasBounds || combined.size.y < 0.01f || combined.size.y > 1000f)
                return false;

            float scale = Mathf.Clamp(TargetHeightMetres / combined.size.y, 0.01f, 100f);
            visual.transform.localScale = Vector3.one * scale;
            // FBX pivots vary between packages. Align the bottom of the mesh with
            // the CharacterController's ground point, without moving the collider.
            // Renderer bounds follow the visual Transform directly; no physics
            // shape moved, so a global Physics.SyncTransforms() would be wasted.
            combined = new Bounds();
            hasBounds = false;
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                if (!hasBounds) { combined = renderer.bounds; hasBounds = true; }
                else combined.Encapsulate(renderer.bounds);
            }
            if (hasBounds)
                visual.transform.position += Vector3.up * (transform.position.y - combined.min.y);
            return true;
        }

        private void TryApplySkin()
        {
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

        private void SetupAnimations()
        {
            idleClip = FindClip("idle", "idle");
            walkClip = FindClip("walk", "walk") ?? FindClip("run", "run");
            runClip = FindClip("run", "run");
            sprintClip = FindClip("sprint", "sprint") ?? runClip;
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
            crouchClip = FindClip("crouch", "crouch") ?? idleClip;
            crouchWalkClip = FindClip("crouch_walk", "crouch") ?? crouchClip;
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
            // Only the locally vendored Pistol_Shoot action currently exists:
            // sample its early aiming pose as a modest weapon-ready visual
            // fallback. A dedicated rifle idle/reload clip is still needed
            // for true production-quality grip and left-hand support.
            readyHoldPlayable = AnimationClipPlayable.Create(graph, shootClip);
            readyHoldPlayable.SetSpeed(0d);
            readyHoldPlayable.SetTime(shootClip.length * 0.16d);
            // Keep one-shot clips short enough to react promptly to attacks
            // without blending over multiple shots in the same hold.
            punchPlayable.SetSpeed(0d);
            shootPlayable.SetSpeed(Mathf.Clamp(shootClip.length / 0.2f, 0.7f, 4.0f));
            jumpLandPlayable.SetSpeed(Mathf.Clamp(jumpLand.length / 0.3f, 0.7f, 3.0f));
            jumpStartPlayable.SetSpeed(Mathf.Clamp(jumpStart.length / 0.23f, 0.7f, 4.0f));
            // Stance FBX clips are transitions into a pose, not continuous gaits.
            // Freeze on the settled position: frame zero can still be standing.
            crouchPlayable.SetSpeed(0d);
            // KayKit source manifest marks frame 10 of the 24-frame Crouching
            // action as the settled pose. Sampling 87% held a later motion
            // frame instead, leaving an unnatural crouch at rest.
            crouchPlayable.SetTime(crouchClip.length * (10d / 24d));
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
            float weight = requested <= WeightWriteEpsilon ? 0f : Mathf.Clamp01(requested);
            if (Mathf.Abs(previous[index] - weight) <= WeightWriteEpsilon) return;
            output.SetInputWeight(index, weight);
            previous[index] = weight;
        }

        private void WriteLayerWeightIfChanged(float requested)
        {
            float weight = requested <= WeightWriteEpsilon ? 0f : Mathf.Clamp01(requested);
            if (Mathf.Abs(lastLayerWeight - weight) <= WeightWriteEpsilon) return;
            layered.SetInputWeight(1, weight);
            lastLayerWeight = weight;
        }

        // Analytical two-bone knee solve: keeps the feet above ground after
        // lowering the crouched hip. No collision queries or heap allocation.
        private static void PlantCrouchLeg(Transform thigh, Transform shin,
            Transform foot, float ankleWorldY, Vector3 forward, float weight)
        {
            if (thigh == null || shin == null || foot == null) return;
            Vector3 hip = thigh.position;
            Vector3 knee = shin.position;
            Vector3 ankle = foot.position;
            float upper = (knee - hip).magnitude;
            float lower = (ankle - knee).magnitude;
            if (upper < 0.05f || lower < 0.05f) return;
            Vector3 targetFoot = ankle;
            targetFoot.y = Mathf.Lerp(ankle.y, ankleWorldY, Mathf.Clamp01(weight));
            Vector3 line = targetFoot - hip;
            float actualDistance = line.magnitude;
            if (actualDistance < 0.01f) return;
            float distance = Mathf.Clamp(actualDistance,
                Mathf.Abs(upper - lower) + 0.005f, upper + lower - 0.005f);
            Vector3 axis = line / actualDistance;
            Vector3 bend = Vector3.ProjectOnPlane(forward, axis);
            if (bend.sqrMagnitude < 0.0001f)
                bend = Vector3.ProjectOnPlane(Vector3.up, axis);
            if (bend.sqrMagnitude < 0.0001f) return;
            bend.Normalize();
            float along = (upper * upper - lower * lower + distance * distance) /
                (2f * distance);
            float across = Mathf.Sqrt(Mathf.Max(0f, upper * upper - along * along));
            Vector3 kneeGoal = hip + axis * along + bend * across;
            Vector3 oldUpper = shin.position - thigh.position;
            Vector3 newUpper = kneeGoal - thigh.position;
            if (oldUpper.sqrMagnitude < 0.0001f || newUpper.sqrMagnitude < 0.0001f)
                return;
            Quaternion footRotation = foot.rotation;
            thigh.rotation = Quaternion.FromToRotation(oldUpper, newUpper) *
                thigh.rotation;
            Vector3 oldLower = foot.position - shin.position;
            Vector3 newLower = targetFoot - shin.position;
            if (oldLower.sqrMagnitude > 0.0001f && newLower.sqrMagnitude > 0.0001f)
                shin.rotation = Quaternion.FromToRotation(oldLower, newLower) *
                    shin.rotation;
            foot.rotation = footRotation;
        }

        // One non-allocating ground sample per foot on low stances; unlike
        // motor-bottom height this works on bridge and sloped terrain.
        private float SupportedAnkleY(Transform foot, float clearance)
        {
            float fallback = transform.position.y + clearance;
            if (foot == null) return fallback;
            Vector3 origin = foot.position + Vector3.up * 0.70f;
            int worldMask = Physics.DefaultRaycastLayers & ~(1 << gameObject.layer);
            int count = Physics.RaycastNonAlloc(new Ray(origin, Vector3.down),
                ankleSupportHits, 1.70f, worldMask, QueryTriggerInteraction.Ignore);
            float closest = float.PositiveInfinity;
            float floor = fallback;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = ankleSupportHits[i];
                if (hit.collider == null || hit.normal.y < 0.55f ||
                    hit.collider.transform.IsChildOf(transform) ||
                    hit.distance >= closest) continue;
                closest = hit.distance;
                floor = hit.point.y + clearance;
            }
            return floor;
        }

        private static void LoopPlayable(AnimationClipPlayable playable, AnimationClip clip, double cycle)
        {
            if (clip == null || clip.length < 0.02f) return;
            double seconds = cycle * clip.length;
            playable.SetTime(seconds - Math.Floor(seconds / clip.length) * clip.length);
        }

        private void LateUpdate()
        {
            if (visual == null || controller == null) return;
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
            // The collision capsule responds immediately to a stance tap.
            // Entering a lower pose should catch up promptly, while standing
            // back up eases slightly longer so shoulders do not snap upward.
            // Exponential time constants preserve feel from 10 to 120 FPS.
            float crouchAlpha = 1f - Mathf.Exp(-dt / (crouching ? 0.085f : 0.15f));
            float proneAlpha = 1f - Mathf.Exp(-dt / (prone ? 0.085f : 0.15f));
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
            motionHistoryReady = true;
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
            // Only the reviewed soldier gets this one-time runtime scale fit.
            bool stableSoldier = realisticSoldierLoaded && graphReady &&
                groundedNow && !visuallyAirborne &&
                (aerialDrop == null || aerialDrop.HasLanded) &&
                actions != null &&
                actions.Stance == AarisBattleActions.PlayerStance.Standing &&
                effectiveCrouchBlend < 0.01f &&
                effectiveProneBlend < 0.01f && planar < 0.15f;
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
            float crouchLowering = (realisticSoldierLoaded ? RealisticSoldierCrouchRigLowering : CrouchRigLowering) * crouchRigWeight;
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
            if (groundedRigCalibrated && stableSoldier &&
                leftFootBone != null && rightFootBone != null)
            {
                float lowestAnkle = Mathf.Min(leftFootBone.position.y,
                    rightFootBone.position.y);
                float error = transform.position.y + 0.045f - lowestAnkle;
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
            if (!visuallyAirborne && graphReady && crouchRigWeight > 0.005f)
            {
                PlantCrouchLeg(leftThigh, leftShin, leftFootBone,
                    SupportedAnkleY(leftFootBone, PlantedAnkleMetres),
                    transform.forward, crouchRigWeight);
                PlantCrouchLeg(rightThigh, rightShin, rightFootBone,
                    SupportedAnkleY(rightFootBone, PlantedAnkleMetres),
                    transform.forward, crouchRigWeight);
            }
            float proneRigWeight = !visuallyAirborne && graphReady &&
                authoredProne ? effectiveProneBlend : 0f;
            if (proneRigWeight > 0.005f)
            {
                PlantCrouchLeg(leftThigh, leftShin, leftFootBone,
                    SupportedAnkleY(leftFootBone, ProneAnkleClearanceMetres),
                    transform.forward, proneRigWeight);
                PlantCrouchLeg(rightThigh, rightShin, rightFootBone,
                    SupportedAnkleY(rightFootBone, ProneAnkleClearanceMetres),
                    transform.forward, proneRigWeight);
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
                balanceSpineBone.localRotation *= Quaternion.Euler(bodyPitch, 0f, bodyRoll);
            }

            // The same support decision now drives gait phase, airborne pose,
            // acceleration pitch and lateral bank without extra physics probes.

            if (graphReady)
            {
                float horizontal = planar;
                // Hide the pistol-ready upper-body pose when the real rifle is
                // sprint-holstered, so both arms retain their natural run cycle.
                bool carrying = actions != null && actions.IsArmed &&
                    (weaponInventory == null || !weaponInventory.IsSprintHolstered);
                float movementTarget = Mathf.InverseLerp(0.08f, 1.2f, horizontal);
                // Both arms and legs must reach their complete authored gait
                // at the actual motor cap: 4.4 m/s armed, 4.8 m/s unarmed.
                // Previous 4.8 m/s sprint threshold was unreachable with a gun
                // and blended running feet against two different source cycles.
                // Walking at jogging speed made the walk FBX cycle nearly five
                // footfalls per second at 3.4 m/s. Blend into the real run
                // clip earlier so cadence and stride length stay human-scale.
                float runTarget = Mathf.InverseLerp(2.0f, 3.4f, horizontal);
                float sprintTarget = Mathf.InverseLerp(4.0f,
                    carrying ? 4.4f : 4.8f, horizontal);
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
                // The old >5 m/s sprint check was unreachable after the natural
                // pace cap (4.4 m/s with a gun), locking the torso in a
                // rigid weapon-ready pose throughout the running cycle.
                float releaseArms = Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(3.2f, 4.4f, horizontal));
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
                    LoopPlayable(runPlayable, runClip, locomotionPhase);
                    LoopPlayable(sprintPlayable, sprintClip, locomotionPhase);
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
            if (placeholderRenderers != null)
                foreach (var renderer in placeholderRenderers)
                    if (IsLegacyPlaceholderRenderer(renderer)) renderer.enabled = true;
        }
    }
}
