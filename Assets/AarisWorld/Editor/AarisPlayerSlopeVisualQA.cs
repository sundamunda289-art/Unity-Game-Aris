using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Aaris.MapReview.Editor
{
    /// <summary>Real original-scene Play Mode, imported rig and camera renders.</summary>
    [InitializeOnLoad]
    public static class AarisPlayerSlopeVisualQA
    {
        private const string State = "Aaris.PlayerSlopeVisualQA";
        private static double started;
        private static float phaseStarted;
        private static int phase, scenario, runtimeErrors;
        private static int lastTickFrame = -1;
        private static GameObject ramp;
        private static Vector3 supportPoint;
        private static Camera captureCamera;
        private static readonly List<Pose> poses = new List<Pose>();
        private static readonly List<string> fixtureLog = new List<string>();
        private static readonly float[] Slopes = { 0f, 12f, -12f, 24f, 12f, 0f, 12f };
        private static readonly float[] Yaws = { 0f, 0f, 0f, 0f, 90f, 0f, 90f };
        private static readonly bool[] Armed = { true, true, true, true, true, false, false };

        [Serializable] private sealed class Pose
        {
            public float slopeDegrees, yawDegrees, torsoNormalDot, hipsGroundGap, headGroundGap;
            public float leftFootGap, rightFootGap, leftHandGap, rightHandGap;
            public float leftElbowGap, rightElbowGap, triggerGripErrorMetres, supportGripErrorMetres;
            public float capsuleFloorGap, visualRootGap, standingShoeOffset, proneSupportDrop;
            public float cachedSupportPointError, cachedSupportNormalDot, pelvisClearanceEnvelope;
            public float chestGroundGap, leftElbowMaximumGap, rightElbowMaximumGap, capsuleHeight, capsuleRadius;
            public Vector3 visualLocalPosition, cachedSupportPoint, cachedSupportNormal, capsuleWorldCenter;
            public bool armed, inventoryHasGun, inventoryFistMode;
            public string weaponId, sideImage, frontImage, failure;
        }
        [Serializable] private sealed class Report
        {
            public string status, failure;
            public int runtimeErrors;
            public Pose[] poses;
            public string[] fixtureLog;
        }

        static AarisPlayerSlopeVisualQA() { EditorApplication.update += Tick; }

        public static void Run()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Batch mode required");
            EditorSceneManager.OpenScene(AarisExplorerBuild.ScenePath, OpenSceneMode.Single);
            SessionState.SetString(State, "starting");
            SessionState.SetFloat(State + ".deadline", (float)EditorApplication.timeSinceStartup + 480f);
            EditorApplication.isPlaying = true;
        }

        private static string OutputDirectory =>
            Environment.GetEnvironmentVariable("AARIS_GAMEPLAY_QA_DIR") ?? "/tmp/aaris-gameplay-qa";

        private static void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                runtimeErrors++;
        }

        private static void Tick()
        {
            string state = SessionState.GetString(State, "");
            if (state.Length == 0) return;
            if (state == "passed" || state == "failed")
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                SessionState.EraseString(State);
                EditorApplication.Exit(state == "passed" ? 0 : 1);
                return;
            }
            if (EditorApplication.timeSinceStartup > SessionState.GetFloat(State + ".deadline", 0f))
            { Finish("failed", "Play Mode slope deadline"); return; }
            if (!EditorApplication.isPlaying) return;
            if (Time.frameCount == lastTickFrame) return;
            lastTickFrame = Time.frameCount;
            try
            {
                if (state == "starting")
                {
                    SessionState.SetString(State, "running");
                    started = EditorApplication.timeSinceStartup;
                    phase = scenario = runtimeErrors = 0;
                    poses.Clear();
                    fixtureLog.Clear();
                    Application.logMessageReceived += OnLog;
                }
                var drop = Object.FindFirstObjectByType<AarisOfflineDrop>();
                var player = drop != null ? drop.GetComponent<AarisThirdPersonPlayer>() :
                    Object.FindFirstObjectByType<AarisThirdPersonPlayer>();
                if (player == null || drop == null) return;
                float age = (float)(EditorApplication.timeSinceStartup - started);
                if (!drop.HasLanded)
                {
                    if (drop.Phase == AarisOfflineDrop.DropPhase.InPlane && age > 1.6f)
                        drop.OnAction();
                    else if (drop.Phase == AarisOfflineDrop.DropPhase.Freefall && age > 3f)
                        drop.OnAction();
                    else if (drop.Phase == AarisOfflineDrop.DropPhase.Gliding && phase == 0)
                    {
                        foreach (Terrain terrain in Terrain.activeTerrains)
                        {
                            Vector3 p = player.transform.position, o = terrain.transform.position;
                            Vector3 size = terrain.terrainData.size;
                            if (p.x < o.x || p.z < o.z || p.x > o.x + size.x || p.z > o.z + size.z) continue;
                            var controller = player.GetComponent<CharacterController>();
                            controller.enabled = false;
                            p.y = o.y + terrain.SampleHeight(p) + 3f;
                            player.transform.position = p;
                            controller.enabled = true;
                            phase = 1;
                            break;
                        }
                    }
                    return;
                }
                var visual = player.GetComponent<AarisCharacterVisual>();
                var battle = player.GetComponent<AarisBattleActions>();
                if (visual == null || !visual.HasWorkingHumanoid || battle == null) return;
                string artDirectory = Environment.GetEnvironmentVariable("AARIS_LANDED_ART_DIR");
                if (!string.IsNullOrEmpty(artDirectory))
                {
                    if (phase < 5)
                    {
                        AarisMobileSubjectVisualQA.PrepareAirfieldPortrait(player);
                        phase = 5;
                        phaseStarted = Time.unscaledTime;
                    }
                    else if (phase == 5 && Time.unscaledTime - phaseStarted > 2f &&
                        player.GetComponent<CharacterController>().isGrounded)
                    {
                        AarisMobileSubjectVisualQA.PlaceLandedLoot(player);
                        phase = 6;
                        phaseStarted = Time.unscaledTime;
                    }
                    else if (phase == 6 && Time.unscaledTime - phaseStarted > .5f)
                    {
                        AarisMobileSubjectVisualQA.CaptureLandedSubjects(player, artDirectory);
                        Finish(runtimeErrors == 0 ? "passed" : "failed", runtimeErrors == 0 ? "" : "Map capture errors");
                    }
                    return;
                }
                if (phase <= 1)
                {
                    SetupRamp(player, battle);
                    phase = 2;
                    phaseStarted = Time.unscaledTime;
                    return;
                }
                if (phase == 2 && Time.unscaledTime - phaseStarted > 0.7f)
                {
                    if (!battle.TrySetStance(AarisBattleActions.PlayerStance.Prone))
                        throw new InvalidOperationException("Prone rejected on visual QA support");
                    phase = 3;
                    phaseStarted = Time.unscaledTime;
                    return;
                }
                if (phase == 3 && Time.unscaledTime - phaseStarted > 2.5f)
                {
                    Capture(player, battle);
                    scenario++;
                    if (scenario == Slopes.Length)
                    {
                        if (runtimeErrors != 0) throw new InvalidOperationException("Runtime errors during slope Play Mode");
                        int failedPoses = 0;
                        foreach (Pose pose in poses)
                            if (!string.IsNullOrEmpty(pose.failure)) failedPoses++;
                        Finish(failedPoses == 0 ? "passed" : "failed",
                            failedPoses == 0 ? "" :
                            failedPoses + " actual poses failed contact/grip gates; see each pose failure");
                    }
                    else
                    {
                        Object.Destroy(ramp);
                        ramp = null;
                        phase = 1;
                    }
                }
            }
            catch (Exception error) { Finish("failed", error.Message); }
        }

        private static void SetupRamp(AarisThirdPersonPlayer player, AarisBattleActions battle)
        {
            Vector3 target = new Vector3(-765.31f, 0f, 80.87f);
            bool found = false;
            foreach (Terrain terrain in Terrain.activeTerrains)
            {
                Vector3 o = terrain.transform.position, size = terrain.terrainData.size;
                if (target.x < o.x || target.z < o.z || target.x > o.x + size.x || target.z > o.z + size.z) continue;
                target.y = o.y + terrain.SampleHeight(target) + 4f;
                found = true;
                break;
            }
            if (!found) throw new InvalidOperationException("Original terrain survey coordinate missing");
            supportPoint = target;
            ramp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ramp.name = "Editor-only slope pose support";
            ramp.transform.rotation = Quaternion.Euler(Slopes[scenario], 0f, 0f);
            ramp.transform.localScale = new Vector3(8f, 0.5f, 8f);
            ramp.transform.position = target - ramp.transform.up * 0.25f;
            var cap = player.GetComponent<CharacterController>();
            cap.enabled = false;
            player.transform.SetPositionAndRotation(target + Vector3.up * 0.15f,
                Quaternion.Euler(0f, Yaws[scenario], 0f));
            cap.enabled = true;
            player.ResumeFromAerialDrop();
            player.CancelAutoSprint();
            player.SetEditorLocomotionQAInput(Vector2.zero);
            var inventory = player.GetComponent<AarisWeaponInventory>();
            if (inventory == null)
                throw new InvalidOperationException("Original scene's inventory component missing");
            string before = inventory.Current != null && inventory.Current.Spec != null
                ? inventory.Current.Spec.id : "none";
            fixtureLog.Add("scenario=" + scenario + " before=" + before +
                " hasGun=" + inventory.HasGun + " fistMode=" + inventory.FistMode);
            // Scene loot/equip timing is not a deterministic starting fixture.
            // Use the real bundled rifle, catalog and normal inventory exchange
            // path; no fake mesh, pose asset or hand transform is substituted.
            if (before != "AssaultRifle")
            {
                AarisWeaponSpec spec = AarisWeaponCatalog.Get("AssaultRifle");
                if (spec == null || !inventory.TryPickup(
                    new AarisWeaponState(spec, 73419u), out AarisWeaponState displaced))
                    throw new InvalidOperationException("Production AssaultRifle equip failed");
                fixtureLog.Add("scenario=" + scenario + " productionEquip=AssaultRifle");
            }
            if (inventory.HasGun != Armed[scenario] && !inventory.ToggleFists())
                throw new InvalidOperationException("Actual armed/unarmed action rejected");
            string equippedState = "scenario=" + scenario + " after=" + inventory.Current.Spec.id +
                " hasGun=" + inventory.HasGun + " fistMode=" + inventory.FistMode;
            fixtureLog.Add(equippedState);
            Debug.Log("AARIS_SLOPE_FIXTURE " + equippedState);
            Physics.SyncTransforms();
            cap.Move(Vector3.down * 0.20f);
            // The initial standing setup is checked against the new clear
            // ramp. Subsequent prone transitions use the normal runtime path.
            var method = typeof(AarisBattleActions).GetMethod("TryStandForJump",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (!(bool)method.Invoke(battle, null)) throw new InvalidOperationException("Standing setup rejected");
        }

        private static void Capture(AarisThirdPersonPlayer player, AarisBattleActions battle)
        {
            if (battle.IsStanceTransitioning || battle.Stance != AarisBattleActions.PlayerStance.Prone)
                throw new InvalidOperationException("Prone did not settle in actual Play frames");
            var rig = player.GetComponentInChildren<Animator>();
            if (rig == null || !rig.isHuman) throw new InvalidOperationException("Imported Humanoid missing");
            var inventory = player.GetComponent<AarisWeaponInventory>();
            var visual = player.GetComponent<AarisCharacterVisual>();
            Vector3 normal = ramp.transform.up;
            T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(owner);
            Transform visualRoot = Field<GameObject>(visual, "visual").transform;
            var cap = player.GetComponent<CharacterController>();
            float Gap(HumanBodyBones bone)
            {
                Transform t = rig.GetBoneTransform(bone);
                if (t == null) throw new InvalidOperationException("Required Humanoid bone missing");
                return Vector3.Dot(t.position - supportPoint, normal);
            }
            float MaximumElbowGap(HumanBodyBones upperBone, HumanBodyBones lowerBone, HumanBodyBones handBone)
            {
                Vector3 shoulder = rig.GetBoneTransform(upperBone).position;
                Vector3 elbow = rig.GetBoneTransform(lowerBone).position;
                Vector3 wrist = rig.GetBoneTransform(handBone).position;
                float upper = Vector3.Distance(shoulder, elbow), lower = Vector3.Distance(elbow, wrist);
                Vector3 line = wrist - shoulder;
                float distance = line.magnitude;
                if (distance < 0.001f) return float.NaN;
                Vector3 axis = line / distance;
                float along = (upper * upper - lower * lower + distance * distance) / (2f * distance);
                float radius = Mathf.Sqrt(Mathf.Max(0f, upper * upper - along * along));
                return Vector3.Dot(shoulder + axis * along - supportPoint, normal) +
                    radius * Vector3.ProjectOnPlane(normal, axis).magnitude;
            }
            var pose = new Pose {
                slopeDegrees = Slopes[scenario],
                yawDegrees = Yaws[scenario],
                armed = battle.IsArmed,
                inventoryHasGun = inventory.HasGun,
                inventoryFistMode = inventory.FistMode,
                weaponId = inventory.Current.Spec.id,
                capsuleFloorGap = Vector3.Dot(player.transform.TransformPoint(cap.center -
                    Vector3.up * cap.height * 0.5f) - supportPoint, normal),
                visualRootGap = Vector3.Dot(visualRoot.position - supportPoint, normal),
                visualLocalPosition = visualRoot.localPosition,
                standingShoeOffset = Field<float>(visual, "standingShoeOffset"),
                proneSupportDrop = Field<float>(visual, "smoothedProneSupportDrop"),
                cachedSupportPointError = Vector3.Dot(Field<Vector3>(battle, "proneGroundPoint") -
                    supportPoint, normal),
                cachedSupportNormalDot = Vector3.Dot(Field<Vector3>(battle, "proneGroundNormal"), normal),
                cachedSupportPoint = Field<Vector3>(battle, "proneGroundPoint"),
                cachedSupportNormal = Field<Vector3>(battle, "proneGroundNormal"),
                capsuleHeight = cap.height, capsuleRadius = cap.radius,
                capsuleWorldCenter = player.transform.TransformPoint(cap.center),
                pelvisClearanceEnvelope = Mathf.Clamp(Vector3.Distance(
                    rig.GetBoneTransform(HumanBodyBones.LeftUpperLeg).position,
                    rig.GetBoneTransform(HumanBodyBones.RightUpperLeg).position) * 0.42f, 0.13f, 0.17f),
                torsoNormalDot = Mathf.Abs(Vector3.Dot((rig.GetBoneTransform(HumanBodyBones.Head).position -
                    rig.GetBoneTransform(HumanBodyBones.Hips).position).normalized, normal)),
                hipsGroundGap = Gap(HumanBodyBones.Hips), headGroundGap = Gap(HumanBodyBones.Head),
                leftFootGap = Gap(HumanBodyBones.LeftFoot), rightFootGap = Gap(HumanBodyBones.RightFoot),
                leftHandGap = Gap(HumanBodyBones.LeftHand), rightHandGap = Gap(HumanBodyBones.RightHand),
                leftElbowGap = Gap(HumanBodyBones.LeftLowerArm), rightElbowGap = Gap(HumanBodyBones.RightLowerArm)
            };
            pose.chestGroundGap = Gap(HumanBodyBones.Chest);
            pose.leftElbowMaximumGap = MaximumElbowGap(HumanBodyBones.LeftUpperArm,
                HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand);
            pose.rightElbowMaximumGap = MaximumElbowGap(HumanBodyBones.RightUpperArm,
                HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand);
            if (pose.armed != Armed[scenario] || inventory.HasGun != pose.armed)
                throw new InvalidOperationException("Final armed/unarmed authority mismatch");
            if (pose.armed)
            {
                var gun = inventory.Current;
                Transform root = gun.transform;
                Vector3 left = rig.GetBoneTransform(HumanBodyBones.LeftHand).position;
                float preferred = gun.Spec.category == "Pistol" ? -0.025f :
                    Mathf.Clamp(gun.Spec.presentationLengthMetres * 0.40f, 0.22f, 0.48f);
                float rail = gun.Spec.category == "Pistol" ? preferred :
                    Mathf.Clamp(root.InverseTransformPoint(left).z, 0.14f, preferred);
                pose.triggerGripErrorMetres = Vector3.Distance(
                    rig.GetBoneTransform(HumanBodyBones.RightHand).position,
                    root.TransformPoint(new Vector3(0.015f, -0.045f, -0.045f)));
                pose.supportGripErrorMetres = Vector3.Distance(left,
                    root.TransformPoint(new Vector3(-0.045f, -0.035f, rail)));
            }
            Directory.CreateDirectory(OutputDirectory);
            pose.sideImage = Path.Combine(OutputDirectory, "prone-slope-" + scenario + "-side.png");
            pose.frontImage = Path.Combine(OutputDirectory, "prone-slope-" + scenario + "-front.png");
            Render(player, new Vector3(3.4f, 1.3f, 0.2f), pose.sideImage);
            Render(player, new Vector3(0.2f, 1.6f, 3.6f), pose.frontImage);
            poses.Add(pose);
            if (pose.torsoNormalDot > 0.40f || pose.hipsGroundGap < -0.035f || pose.hipsGroundGap > 0.19f ||
                pose.headGroundGap < 0f || pose.leftFootGap < 0.015f || pose.rightFootGap < 0.015f ||
                pose.leftFootGap > 0.22f || pose.rightFootGap > 0.22f ||
                pose.leftHandGap < -0.035f || pose.rightHandGap < -0.035f ||
                pose.leftElbowGap < 0.005f || pose.rightElbowGap < 0.005f)
                pose.failure = "Actual prone bones violate slope contact envelope";
            if (pose.armed && (pose.triggerGripErrorMetres > 0.045f ||
                pose.supportGripErrorMetres > 0.045f))
                pose.failure = (string.IsNullOrEmpty(pose.failure) ? "" : pose.failure + "; ") +
                    "Prone support correction detached a hand from its gun grip";
            if (!string.IsNullOrEmpty(pose.failure))
                Debug.LogWarning("AARIS_SLOPE_POSE_FAIL scenario=" + scenario + " " + pose.failure);
        }

        private static void Render(AarisThirdPersonPlayer player, Vector3 offset, string path)
        {
            if (captureCamera == null)
            {
                captureCamera = new GameObject("Editor-only slope portrait camera").AddComponent<Camera>();
                captureCamera.enabled = false;
                captureCamera.fieldOfView = 40f;
                captureCamera.nearClipPlane = 0.04f;
                captureCamera.farClipPlane = 500f;
            }
            Vector3 target = player.transform.position + Vector3.up * 0.40f;
            captureCamera.transform.SetPositionAndRotation(target + offset, Quaternion.LookRotation(-offset));
            var rt = new RenderTexture(960, 640, 24, RenderTextureFormat.ARGB32);
            var png = new Texture2D(960, 640, TextureFormat.RGB24, false);
            RenderTexture prior = RenderTexture.active;
            try
            {
                captureCamera.targetTexture = rt;
                captureCamera.Render();
                RenderTexture.active = rt;
                png.ReadPixels(new Rect(0, 0, 960, 640), 0, 0);
                png.Apply();
                File.WriteAllBytes(path, png.EncodeToPNG());
            }
            finally
            {
                captureCamera.targetTexture = null;
                RenderTexture.active = prior;
                Object.DestroyImmediate(png);
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        private static void Finish(string status, string failure)
        {
            Application.logMessageReceived -= OnLog;
            Directory.CreateDirectory(OutputDirectory);
            File.WriteAllText(Path.Combine(OutputDirectory, "player-slope-visual.json"),
                JsonUtility.ToJson(new Report { status = status, failure = failure,
                    runtimeErrors = runtimeErrors, poses = poses.ToArray(),
                    fixtureLog = fixtureLog.ToArray() }, true));
            Debug.Log("AARIS_PLAYER_SLOPE_VISUAL_" + status.ToUpperInvariant() +
                " poses=" + poses.Count + " errors=" + runtimeErrors + " reason=" + failure);
            SessionState.SetString(State, status);
            EditorApplication.isPlaying = false;
        }
    }
}
