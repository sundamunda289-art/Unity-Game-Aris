using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Real licensed Unity Editor Play Mode photos for already-authored local
    /// house and tower prefab variants; no fake schematic, no scene write.
    /// This lives in the selected feature branch, runs in isolated /tmp, and
    /// records actual placement/skipped statistics and image variation.
    /// </summary>
    [InitializeOnLoad]
    public static class AarisSettlementExpansionProbe
    {
        private const string ScenePath =
            "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";
        private const string Folder = "/tmp/aaris-codespaces/map-expansion-probe";
        private const string State = "aaris.map.expansion.probe";
        private const string Began = "aaris.map.expansion.began";
        private const string ExitCode = "aaris.map.expansion.exit";
        private const string ErrorCount = "aaris.map.expansion.errors";
        private const string ErrorKind = "aaris.map.expansion.errorkind";

        [Serializable]
        private sealed class Report
        {
            public string status;
            public string reason;
            public string scene;
            public string unityVersion;
            public int terrainTiles;
            public int placed;
            public int skippedTerrain;
            public int skippedTrees;
            public int skippedObstacle;
            public int skippedResources;
            public int imageCount;
            public float minPixelDeviation;
            public int runtimeErrors;
            public string errorKind;
        }

        static AarisSettlementExpansionProbe()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;
        }

        private static void OnLog(string msg, string stack, LogType kind)
        {
            if (SessionState.GetString(State, "") != "running" ||
                (kind != LogType.Error && kind != LogType.Assert &&
                 kind != LogType.Exception)) return;
            SessionState.SetInt(ErrorCount, SessionState.GetInt(ErrorCount, 0) + 1);
            if (!string.IsNullOrEmpty(SessionState.GetString(ErrorKind, ""))) return;
            SessionState.SetString(ErrorKind,
                (msg != null && msg.Contains("NullReferenceException")) ? "NULL_REFERENCE" :
                (msg != null && msg.Contains("MissingReferenceException")) ? "MISSING_REFERENCE" :
                (msg != null && msg.Contains("AARIS_EXPANSION_")) ? "EXPANSION_ERROR" :
                "OTHER_UNITY_ERROR");
        }

        [MenuItem("Aaris/CI/Real Map Expansion Photographs")]
        public static void Verify()
        {
            Directory.CreateDirectory(Folder);
            SessionState.SetString(State, "");
            SessionState.SetInt(ErrorCount, 0);
            SessionState.SetString(ErrorKind, "");
            try
            {
                if (!File.Exists(ScenePath)) throw new InvalidOperationException("NATIVE_SCENE_NOT_FOUND");
                byte[] start = new byte[75];
                using (var f = File.OpenRead(ScenePath)) f.Read(start, 0, start.Length);
                if (System.Text.Encoding.ASCII.GetString(start).StartsWith(
                    "version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal))
                    throw new InvalidOperationException("LFS_SCENE_NOT_HYDRATED");
                var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                if (!scene.isLoaded || scene.path != ScenePath)
                    throw new InvalidOperationException("SCENE_NOT_OPEN");
                if (UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None).Length != 4)
                    throw new InvalidOperationException("FOUR_TERRAIN_TILES_REQUIRED");
                if (Camera.main == null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                    throw new InvalidOperationException("NATIVE_GRAPHICS_CAMERA_MISSING");
                SessionState.SetString(State, "starting");
                SessionState.SetFloat(Began, (float)EditorApplication.timeSinceStartup);
                EditorApplication.isPlaying = true;
                Debug.Log("AARIS_MAP_EXPANSION_UNITY_PLAY_REQUESTED");
            }
            catch (Exception ex)
            {
                Save(new Report { status = "failed", reason = ex is InvalidOperationException
                    ? ex.Message : ex.GetType().Name, scene = ScenePath });
                EditorApplication.Exit(1);
            }
        }

        private static void Save(Report report)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, "report.json"),
                JsonUtility.ToJson(report, true));
        }

        private static void Tick()
        {
            string state = SessionState.GetString(State, "");
            if (state == "stopping")
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    int exitCode = SessionState.GetInt(ExitCode, 1);
                    SessionState.SetString(State, "");
                    EditorApplication.Exit(exitCode);
                }
                return;
            }
            if (state != "starting" && state != "running") return;
            float elapsed = (float)EditorApplication.timeSinceStartup -
                SessionState.GetFloat(Began, 0f);
            if (elapsed > 185f)
            {
                End(false, "NATIVE_EXPANSION_TIMED_OUT");
                return;
            }
            if (!EditorApplication.isPlaying) return;
            if (state == "starting")
            {
                SessionState.SetString(State, "running");
                SessionState.SetFloat(Began, (float)EditorApplication.timeSinceStartup);
                return;
            }
            if (elapsed < 5f || Time.frameCount < 40) return;
            try
            {
                var world = UnityEngine.Object.FindFirstObjectByType<AarisSettlementExpansion>();
                if (world == null) throw new InvalidOperationException("EXPANSION_BOOT_NOT_FOUND");
                if (Camera.main == null) throw new InvalidOperationException("MAIN_CAMERA_NOT_FOUND");
                var report = new Report {
                    status = "failed",
                    reason = "NOT_VALIDATED",
                    scene = SceneManager.GetActiveScene().path,
                    unityVersion = Application.unityVersion,
                    terrainTiles = Terrain.activeTerrains.Length,
                    placed = world.Placed,
                    skippedTerrain = world.SkippedTerrain,
                    skippedTrees = world.SkippedTrees,
                    skippedObstacle = world.SkippedObstacle,
                    skippedResources = world.SkippedResources,
                    runtimeErrors = SessionState.GetInt(ErrorCount, 0),
                    errorKind = SessionState.GetString(ErrorKind, "")
                };
                // Distinct camera viewpoints to detect incorrect roof, terrain,
                // tower/collision visibility and material colouring.
                string[] names = {
                    "ARX_01_OldVillage_detached",
                    "ARX_03_ValleySettlement_detached",
                    "ARX_05_ClockTowerTown_detached",
                    "ARX_07_CoastalHamlet_detached",
                    "ARX_09_LakesideHamlet_cottage",
                    "ARX_13_HighlandObservatory_watchtower",
                    "ARX_14_ForestLookout_watchtower",
                    "ARX_15_SouthernAirfield_watchtower"
                };
                Camera cam = Camera.main;
                Vector3 position = cam.transform.position;
                Quaternion rotation = cam.transform.rotation;
                float smallest = float.PositiveInfinity;
                try
                {
                    foreach (string name in names)
                    {
                        var site = GameObject.Find(name);
                        if (site == null) continue;
                        Renderer[] rendered = site.GetComponentsInChildren<Renderer>(true);
                        if (rendered == null || rendered.Length == 0) continue;
                        Bounds target = rendered[0].bounds;
                        for (int i = 1; i < rendered.Length; i++)
                            if (rendered[i].enabled) target.Encapsulate(rendered[i].bounds);
                        float distance = Mathf.Clamp(target.size.magnitude * 1.7f, 22f, 58f);
                        Vector3 focus = target.center + Vector3.up * 1.2f;
                        cam.transform.position = focus + new Vector3(distance * 0.66f,
                            distance * 0.34f, -distance * 0.76f);
                        cam.transform.LookAt(focus, Vector3.up);
                        float deviation = Capture(cam, name + ".png");
                        smallest = Mathf.Min(smallest, deviation);
                        report.imageCount++;
                    }
                }
                finally
                {
                    cam.transform.SetPositionAndRotation(position, rotation);
                }
                report.minPixelDeviation = float.IsInfinity(smallest) ? 0f : smallest;
                bool passed = report.scene == ScenePath &&
                    report.terrainTiles == 4 &&
                    report.placed + report.skippedTerrain + report.skippedTrees +
                        report.skippedObstacle == 5 && report.skippedResources == 0 &&
                    report.imageCount == names.Length &&
                    report.minPixelDeviation >= 6f &&
                    report.runtimeErrors == 0;
                report.status = passed ? "passed" : "failed";
                report.reason = passed ? "ACTUAL_UNITY_BAKED_HOMES_AND_8_PHOTOS_VERIFIED" :
                    "PLACEMENT_IMAGE_OR_RUNTIME_ASSERTION_FAILED";
                Save(report);
                End(passed, report.reason, false);
            }
            catch (Exception ex)
            {
                End(false, ex is InvalidOperationException ? ex.Message : ex.GetType().Name);
            }
        }

        private static float Capture(Camera cam, string filename)
        {
            const int width = 1080, height = 608;
            var buffer = RenderTexture.GetTemporary(width, height, 24,
                RenderTextureFormat.ARGB32);
            var sourceTarget = cam.targetTexture;
            var active = RenderTexture.active;
            var image = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                cam.targetTexture = buffer;
                cam.Render();
                RenderTexture.active = buffer;
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                image.Apply(false);
                Color32[] pixels = image.GetPixels32();
                double sum = 0, square = 0;
                int count = 0;
                for (int i = 0; i < pixels.Length; i += 137)
                {
                    Color32 px = pixels[i];
                    double y = 0.2126 * px.r + 0.7152 * px.g + 0.0722 * px.b;
                    sum += y; square += y * y; count++;
                }
                float deviation = (float)Math.Sqrt(Math.Max(0,
                    square / count - Math.Pow(sum / count, 2)));
                byte[] png = image.EncodeToPNG();
                if (png.Length < 18000)
                    throw new InvalidOperationException("UNITY_CAMERA_IMAGE_TOO_SMALL");
                File.WriteAllBytes(Path.Combine(Folder, filename), png);
                return deviation;
            }
            finally
            {
                cam.targetTexture = sourceTarget;
                RenderTexture.active = active;
                UnityEngine.Object.DestroyImmediate(image);
                RenderTexture.ReleaseTemporary(buffer);
            }
        }

        private static void End(bool pass, string reason, bool makeReport = true)
        {
            if (SessionState.GetString(State, "") == "stopping") return;
            if (makeReport)
                Save(new Report { status = "failed", reason = reason, scene = ScenePath,
                    runtimeErrors = SessionState.GetInt(ErrorCount, 0),
                    errorKind = SessionState.GetString(ErrorKind, "") });
            Debug.Log(pass ? "AARIS_MAP_EXPANSION_REAL_UNITY_PASSED" :
                "AARIS_MAP_EXPANSION_REAL_UNITY_FAILED:" + reason);
            SessionState.SetInt(ExitCode, pass ? 0 : 1);
            SessionState.SetString(State, "stopping");
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        }
    }
}
