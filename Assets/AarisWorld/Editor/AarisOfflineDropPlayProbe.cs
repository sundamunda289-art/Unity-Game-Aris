using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Real Unity 6000 Play Mode camera frames, not a mock or schematic.
    /// A closed-loop editor test observes actual aircraft/eject/canopy/touchdown states.
    /// Run in the isolated Git LFS worktree through the existing licensed Codespace.
    /// </summary>
    [InitializeOnLoad]
    public static class AarisOfflineDropPlayProbe
    {
        private const string Scene = "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";
        private const string Root = "/tmp/aaris-codespaces/offline-drop-probe";
        private const string State = "aaris.offline.drop.probe.state";
        private const string Began = "aaris.offline.drop.probe.began";
        private const string LastFrame = "aaris.offline.drop.probe.lastframe";
        private const string EjectedAt = "aaris.offline.drop.probe.ejectedat";
        private const string DeployIssued = "aaris.offline.drop.probe.deployissued";
        private const string FrameCount = "aaris.offline.drop.probe.frames";
        private const string ErrorCount = "aaris.offline.drop.probe.errors";
        private const string ErrorCode = "aaris.offline.drop.probe.errorcode";
        private const string ErrorSource = "aaris.offline.drop.probe.errorsource";
        private const string ExitCode = "aaris.offline.drop.probe.exit";
        private const string SeenPrefix = "aaris.offline.drop.probe.phase.";
        private const int Width = 960, Height = 540;
        private const int MaxFrames = 800;

        [Serializable]
        private sealed class Report
        {
            public string mode = "offline_drop_video";
            public string status = "failed";
            public string reason = "NOT_FINISHED";
            public string unityVersion;
            public string graphicsDevice;
            public int terrainTiles;
            public int frames;
            public int errors;
            public string errorCode;
            public string errorSource;
            public bool observedInPlane;
            public bool observedFreefall;
            public bool observedOpening;
            public bool observedGliding;
            public bool observedLanded;
            public bool existingMotorRestored;
            public bool canopyActuallyRendered;
            public float maxPixelVariation;
            public float observedSeconds;
        }

        static AarisOfflineDropPlayProbe()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;
        }

        [MenuItem("Aaris/CI/Verify Offline Drop With Real Video")]
        public static void Record()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Path.Combine(Root, "frames"));
            Directory.CreateDirectory(Path.Combine(Root, "shots"));
            SessionState.SetString(State, "");
            SessionState.SetInt(FrameCount, 0);
            SessionState.SetInt(ErrorCount, 0);
            SessionState.SetString(ErrorCode, "");
            SessionState.SetString(ErrorSource, "");
            SessionState.SetFloat(Began, 0f);
            SessionState.SetFloat(LastFrame, 0f);
            SessionState.SetFloat(EjectedAt, 0f);
            SessionState.SetBool(DeployIssued, false);
            foreach (string phase in Enum.GetNames(typeof(AarisOfflineDrop.DropPhase)))
                SessionState.SetBool(SeenPrefix + phase, false);

            try
            {
                if (!File.Exists(Scene)) throw new InvalidOperationException("SCENE_NOT_FOUND");
                byte[] beginning = new byte[75];
                using (FileStream f = File.OpenRead(Scene)) f.Read(beginning, 0, beginning.Length);
                if (System.Text.Encoding.ASCII.GetString(beginning).StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal))
                    throw new InvalidOperationException("SCENE_STILL_LFS_POINTER");

                var loaded = EditorSceneManager.OpenScene(Scene, OpenSceneMode.Single);
                if (!loaded.isLoaded || loaded.path != Scene)
                    throw new InvalidOperationException("NATIVE_SCENE_OPEN_FAILED");
                if (UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None).Length != 4)
                    throw new InvalidOperationException("NATIVE_TERRAIN_COUNT_INVALID");
                if (UnityEngine.Object.FindFirstObjectByType<AarisThirdPersonPlayer>() == null ||
                    Camera.main == null)
                    throw new InvalidOperationException("SAVED_PLAYER_CAMERA_MISSING");
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                    throw new InvalidOperationException("NULL_GPU");
                SessionState.SetString(State, "starting");
                SessionState.SetFloat(Began, (float)EditorApplication.timeSinceStartup);
                EditorApplication.isPlaying = true;
                Debug.Log("AARIS_OFFLINE_DROP_VIDEO_PLAYMODE_REQUESTED");
            }
            catch (Exception ex)
            {
                Report report = InitialReport();
                report.reason = SafeReason(ex);
                Save(report);
                EditorApplication.Exit(1);
            }
        }

        private static Report InitialReport()
        {
            return new Report {
                unityVersion = Application.unityVersion,
                graphicsDevice = SystemInfo.graphicsDeviceType.ToString(),
                terrainTiles = UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None).Length,
                frames = SessionState.GetInt(FrameCount, 0),
                errors = SessionState.GetInt(ErrorCount, 0),
                errorCode = SessionState.GetString(ErrorCode, ""),
                errorSource = SessionState.GetString(ErrorSource, ""),
                observedInPlane = Seen("InPlane"),
                observedFreefall = Seen("Freefall"),
                observedOpening = Seen("Opening"),
                observedGliding = Seen("Gliding"),
                observedLanded = Seen("Landed"),
                maxPixelVariation = SessionState.GetFloat("aaris.offline.drop.probe.variation", 0f)
            };
        }

        private static void OnLog(string message, string stack, LogType type)
        {
            if (SessionState.GetString(State, "") != "running" || 
                (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)) return;
            SessionState.SetInt(ErrorCount, SessionState.GetInt(ErrorCount, 0) + 1);
            if (!string.IsNullOrEmpty(SessionState.GetString(ErrorCode, ""))) return;
            string code = message != null && message.Contains("NullReference") ? "NULL_REFERENCE" :
                message != null && message.Contains("MissingReference") ? "MISSING_REFERENCE" :
                message != null && message.Contains("Exception") ? "RUNTIME_EXCEPTION" :
                "UNITY_LOG_ERROR";
            SessionState.SetString(ErrorCode, code);
            // Report ONLY a whitelisted subsystem label; never raw exception
            // messages, call stacks or paths from the user's Codespace.
            string[] modules = {
                "AarisOfflineDropVisuals", "AarisOfflineDrop",
                "AarisOfflineTacticalHUD", "AarisThirdPersonPlayer",
                "AarisCharacterVisual", "AarisWeaponLootWorld",
                "AarisVehicleWorld", "AarisBattleActions",
                "AarisCodespaceSceneProbe", "UnityEngine.UI"
            };
            string source = "OTHER";
            foreach (string module in modules)
                if (!string.IsNullOrEmpty(stack) &&
                    stack.IndexOf(module, StringComparison.Ordinal) >= 0)
                {
                    source = module;
                    break;
                }
            SessionState.SetString(ErrorSource, source);
        }

        private static bool Seen(string phase) => SessionState.GetBool(SeenPrefix + phase, false);
        private static void Save(Report report)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "report.json"), JsonUtility.ToJson(report, true));
        }
        private static string SafeReason(Exception ex)
        {
            if (ex is InvalidOperationException) return ex.Message.Length < 80 ? ex.Message : "INVALID_OPERATION";
            if (ex is IOException) return "FILE_IO_ERROR";
            return ex.GetType().Name;
        }

        private static void Tick()
        {
            string state = SessionState.GetString(State, "");
            if (state == "stopping")
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    int exit = SessionState.GetInt(ExitCode, 1);
                    SessionState.SetString(State, "");
                    EditorApplication.Exit(exit);
                }
                return;
            }
            if (state != "starting" && state != "running") return;
            double now = EditorApplication.timeSinceStartup;
            if (now - SessionState.GetFloat(Began, 0f) > 205f)
            {
                Finish(false, "PLAYMODE_DEADLINE_EXCEEDED");
                return;
            }
            if (!EditorApplication.isPlaying) return;
            if (state == "starting")
            {
                SessionState.SetString(State, "running");
                SessionState.SetFloat(Began, (float)now);
                SessionState.SetFloat(LastFrame, (float)now - 0.15f);
                SessionState.SetFloat("aaris.offline.drop.probe.variation", 0f);
                return;
            }

            var drop = UnityEngine.Object.FindFirstObjectByType<AarisOfflineDrop>();
            if (drop == null)
            {
                if (now - SessionState.GetFloat(Began, 0) > 8)
                    Finish(false, "AERIAL_CONTROLLER_NOT_INSTALLED");
                return;
            }

            string name = drop.Phase.ToString();
            bool newlySeen = !Seen(name);
            if (newlySeen) SessionState.SetBool(SeenPrefix + name, true);

            // First capture the ACTUAL phase, then simulate player button taps.
            // Otherwise the "InPlane" screenshot could be mislabelled freefall.
            if (now - SessionState.GetFloat(LastFrame, 0f) >= 0.125 ||
                newlySeen)
            {
                SessionState.SetFloat(LastFrame, (float)now);
                try
                {
                    Camera cam = Camera.main;
                    if (cam == null) throw new InvalidOperationException("PLAY_CAMERA_MISSING");
                    if (newlySeen && (drop.IsFreefall || drop.Phase == AarisOfflineDrop.DropPhase.Gliding))
                        VerifyAerialPlayerVisibility(drop, cam);
                    int frame = SessionState.GetInt(FrameCount, 0);
                    if (frame >= MaxFrames) throw new InvalidOperationException("VIDEO_FRAME_LIMIT");
                    float dev = Render(cam, frame, newlySeen ? name : null);
                    SessionState.SetInt(FrameCount, frame + 1);
                    SessionState.SetFloat("aaris.offline.drop.probe.variation",
                        Mathf.Max(dev, SessionState.GetFloat("aaris.offline.drop.probe.variation", 0)));
                }
                catch (Exception ex)
                {
                    Finish(false, SafeReason(ex));
                    return;
                }
            }

            // The real game requires TWO separate taps: EJECT while in the
            // plane and OPEN after a short period of freefall. The previous
            // probe only simulated EJECT and could not test manual deployment.
            if (drop.Phase == AarisOfflineDrop.DropPhase.InPlane &&
                now - SessionState.GetFloat(Began, 0f) > 2.1f)
            {
                drop.OnAction();
                if (drop.Phase == AarisOfflineDrop.DropPhase.Freefall)
                    SessionState.SetFloat(EjectedAt, (float)now);
            }
            if (drop.Phase == AarisOfflineDrop.DropPhase.Freefall &&
                SessionState.GetFloat(EjectedAt, 0f) <= 0f)
                SessionState.SetFloat(EjectedAt, (float)now);
            if (drop.Phase == AarisOfflineDrop.DropPhase.Freefall &&
                !SessionState.GetBool(DeployIssued, false) &&
                now - SessionState.GetFloat(EjectedAt, (float)now) > 1.1f)
            {
                SessionState.SetBool(DeployIssued, true);
                drop.OnAction(); // Tests the same OPEN action used by mobile HUD.
            }

            // Retain the REAL video even if a single existing, nonfatal game
            // script logs an exception. The final assertion still FAILS if
            // any runtime errors occurred. Stop only on repeated error storms.
            if (SessionState.GetInt(ErrorCount, 0) >= 30)
            {
                Finish(false, "REPEATED_GAMEPLAY_RUNTIME_ERRORS");
                return;
            }

            if (drop.HasLanded && now - SessionState.GetFloat(Began, 0) > 3f &&
                Seen("Opening") && Seen("Gliding"))
            {
                // Allow at least a few on-foot frames after landing.
                if (SessionState.GetInt(FrameCount, 0) < 40) return;
                var motor = drop.GetComponent<AarisThirdPersonPlayer>();
                var controller = drop.GetComponent<CharacterController>();
                bool restore = motor != null && motor.enabled && controller != null && controller.enabled;
                Report result = InitialReport();
                result.existingMotorRestored = restore;
                result.canopyActuallyRendered = File.Exists(Path.Combine(Root, "shots", "Gliding.png"));
                result.observedSeconds = (float)(now - SessionState.GetFloat(Began, 0));
                bool completed = restore && result.canopyActuallyRendered &&
                    result.observedInPlane && result.observedFreefall &&
                    result.observedOpening && result.observedGliding &&
                    result.observedLanded && result.maxPixelVariation >= 6f &&
                    result.terrainTiles == 4 && result.errors == 0;
                result.status = completed ? "passed" : "failed";
                result.reason = completed ? "REAL_UNITY_OFFLINE_DROP_ALL_PHASES_PASSED" : "DROP_PHASE_OR_RESTORE_ASSERTION_FAILED";
                Save(result);
                Finish(completed, result.reason, false);
            }
        }

        private static void VerifyAerialPlayerVisibility(AarisOfflineDrop drop, Camera camera)
        {
            var player = drop.GetComponent<AarisCharacterVisual>();
            if (player == null || !player.HasWorkingHumanoid)
                throw new InvalidOperationException("AERIAL_PLAYER_HUMANOID_MISSING");

            Bounds pose = new Bounds(drop.transform.position + Vector3.up * 0.9f, Vector3.one * 3f);
            Plane[] frustum = GeometryUtility.CalculateFrustumPlanes(camera);
            var baked = new Mesh();
            try
            {
                foreach (var skin in player.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    if (!skin.enabled || skin.sharedMesh == null ||
                        (camera.cullingMask & (1 << skin.gameObject.layer)) == 0 ||
                        !GeometryUtility.TestPlanesAABB(frustum, skin.bounds))
                        continue;

                    // Confirm the evaluated vertices are near the capsule too.
                    // Valid culling bounds alone do not rule out an offset rig.
                    skin.BakeMesh(baked);
                    if (baked.vertexCount > 0 && pose.Contains(
                        skin.transform.TransformPoint(baked.bounds.center)))
                        return;
                }
            }
            finally
            {
                UnityEngine.Object.Destroy(baked);
            }
            throw new InvalidOperationException("AERIAL_PLAYER_MESH_OUTSIDE_CAMERA_BOUNDS");
        }

        private static float Render(Camera cam, int frame, string firstPhase)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture target = RenderTexture.GetTemporary(Width, Height, 24, RenderTextureFormat.ARGB32);
            RenderTexture originalTarget = cam.targetTexture;
            Texture2D image = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            try
            {
                cam.targetTexture = target;
                cam.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
                image.Apply(false);
                // Screen content check: actual moving scene, not a static schematic.
                Color32[] pixels = image.GetPixels32();
                double sum = 0, sq = 0;
                const int step = 127;
                int n = 0;
                for (int i = 0; i < pixels.Length; i += step)
                {
                    var p = pixels[i];
                    double y = 0.2126 * p.r + 0.7152 * p.g + 0.0722 * p.b;
                    sum += y; sq += y * y; n++;
                }
                float deviation = (float)Math.Sqrt(Math.Max(0, sq / n - (sum / n) * (sum / n)));
                byte[] jpeg = image.EncodeToJPG(76);
                if (jpeg.Length < 3500) throw new InvalidOperationException("RENDER_FRAME_TOO_SMALL");
                File.WriteAllBytes(Path.Combine(Root, "frames", "frame_" + frame.ToString("D5") + ".jpg"), jpeg);
                if (!string.IsNullOrEmpty(firstPhase))
                    File.WriteAllBytes(Path.Combine(Root, "shots", firstPhase + ".png"), image.EncodeToPNG());
                return deviation;
            }
            finally
            {
                cam.targetTexture = originalTarget;
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(image);
                RenderTexture.ReleaseTemporary(target);
            }
        }

        private static void Finish(bool success, string code, bool write = true)
        {
            if (SessionState.GetString(State, "") == "stopping") return;
            if (write)
            {
                Report report = InitialReport();
                report.reason = code;
                report.observedSeconds = (float)(EditorApplication.timeSinceStartup - SessionState.GetFloat(Began, 0f));
                Save(report);
            }
            Debug.Log(success ? "AARIS_OFFLINE_DROP_VIDEO_PROBE_PASSED" : "AARIS_OFFLINE_DROP_VIDEO_PROBE_FAILED:" + code);
            SessionState.SetInt(ExitCode, success ? 0 : 1);
            SessionState.SetString(State, "stopping");
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        }
    }
}
