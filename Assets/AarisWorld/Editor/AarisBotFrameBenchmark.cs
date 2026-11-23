#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Isolated, repeatable Editor-CPU baseline for the actual 100-rival
    /// MonoBehaviour.Update scheduler. It deliberately excludes meshes, VFX,
    /// Android driver, terrain streaming, and on-device thermals.
    /// No authored scenes/assets are saved.
    /// </summary>
    [InitializeOnLoad]
    public static class AarisBotFrameBenchmark
    {
        private const string Session = "AarisBotFrameBenchmarkV1";
        private const int BaselineFrames = 200;
        private const int BaselineWarmupFrames = 35;
        private const int WarmupFrames = 55;
        private const int MaximumLoadedFrames = 16000;
        private const float MinimumLoadedGameSeconds = 1.3f;
        private static readonly string Folder =
            Path.Combine(Path.GetTempPath(), "aaris-bot-frame-benchmark");
        private static readonly BindingFlags Private =
            BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type BotType =
            typeof(AarisOfflineBRDirector).Assembly.GetType(
                "Aaris.MapReview.AarisOfflineRival", true);

        private static readonly List<float> baseline = new List<float>(BaselineFrames);
        private static readonly List<float> loaded = new List<float>(MaximumLoadedFrames);
        private static readonly List<float> baselineMain = new List<float>(BaselineFrames);
        private static readonly List<float> loadedMain = new List<float>(MaximumLoadedFrames);
        private static readonly List<long> baselineGc = new List<long>(BaselineFrames);
        private static readonly List<long> loadedGc = new List<long>(MaximumLoadedFrames);
        private static ProfilerRecorder mainRecorder, gcRecorder;
        private static Transform[] botTransforms;
        private static Vector3[] initialPositions;
        private static GameObject temporaryRoot;
        private static AarisOfflineBRDirector director;
        private static int phase, lastFrame = -1, warmCount, firstFrame;
        private static int baselineWarmCount;
        private static float loadedGameStart;
        private static bool failed;
        static AarisBotFrameBenchmark()
        {
            EditorApplication.update += Tick;
        }

        [MenuItem("Aaris/CI/Profile 100-bot scheduler (Editor CPU)")]
        public static void Run()
        {
            // A single-scene benchmark discards the currently open Editor scene.
            // Only allow dedicated CI sessions; protect interactive user work.
            if (!Application.isBatchMode)
                throw new InvalidOperationException("CI_BATCHMODE_ONLY");
            if (EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorApplication.isCompiling)
                throw new InvalidOperationException("EDITOR_BUSY");
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, "result.txt"),
                "Unity " + Application.unityVersion +
                "; Editor CPU logical-actor benchmark, NOT Android FPS\n");
            SessionState.SetBool(Session, true);
            SessionState.SetBool(Session + "Finish", false);
            SessionState.SetInt(Session + "ExitCode", 0);
            SessionState.SetFloat(Session + "Deadline",
                (float)EditorApplication.timeSinceStartup + 260f);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorApplication.isPlaying = true;
        }

        private static void Set(object instance, string field, object value)
        {
            FieldInfo member = instance.GetType().GetField(field, Private);
            if (member == null) throw new InvalidOperationException("Field " + field);
            member.SetValue(instance, value);
        }

        private static void Tick()
        {
            if (!SessionState.GetBool(Session, false)) return;
            if (SessionState.GetBool(Session + "Finish", false))
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                int exitCode = SessionState.GetInt(Session + "ExitCode", 1);
                SessionState.SetBool(Session, false);
                if (Application.isBatchMode) EditorApplication.Exit(exitCode);
                return;
            }
            if (EditorApplication.timeSinceStartup >
                SessionState.GetFloat(Session + "Deadline", float.MaxValue))
            {
                Fail("TIMEOUT");
                return;
            }
            if (!EditorApplication.isPlaying) return;
            try
            {
                if (phase == 0) { Begin(); return; }
                int frame = Time.frameCount;
                if (frame == lastFrame || frame <= 0) return;
                lastFrame = frame;
                // The benchmark disables the director's MonoBehaviour Update,
                // so drive its actual centralized dormant queue explicitly.
                if (phase >= 2 && director != null)
                    director.TickDormantRivals(Time.time);
                if (phase == 1)
                {
                    if (++baselineWarmCount <= BaselineWarmupFrames) return;
                    Capture(baseline, baselineMain, baselineGc);
                    if (baseline.Count >= BaselineFrames)
                    {
                        SpawnRivals();
                        phase = 2;
                        firstFrame = frame;
                    }
                }
                else if (phase == 2)
                {
                    if (++warmCount >= WarmupFrames)
                    {
                        loadedGameStart = Time.time;
                        phase = 3;
                    }
                }
                else if (phase == 3)
                {
                    Capture(loaded, loadedMain, loadedGc);
                    if (Time.time - loadedGameStart >= MinimumLoadedGameSeconds &&
                        loaded.Count >= 600) Complete();
                    else if (loaded.Count >= MaximumLoadedFrames)
                        Fail("NOT_ENOUGH_GAME_TIME_IN_" + loaded.Count + "_FRAMES");
                }
            }
            catch (Exception ex) { Fail(ex.GetType().Name + ": " + ex.Message); }
        }

        private static void Begin()
        {
            phase = 1;
            lastFrame = -1;
            baseline.Clear(); loaded.Clear();
            baselineMain.Clear(); loadedMain.Clear();
            baselineGc.Clear(); loadedGc.Clear();
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
            temporaryRoot = new GameObject("Temporary 100-bot CPU benchmark");
            var floor = new GameObject("QA flat physical terrain");
            floor.transform.SetParent(temporaryRoot.transform, false);
            floor.transform.position = Vector3.down * .55f;
            floor.AddComponent<BoxCollider>().size =
                new Vector3(2600f, 1f, 2600f);
            var player = new GameObject("QA player");
            player.transform.SetParent(temporaryRoot.transform, false);
            var controller = player.AddComponent<CharacterController>();
            controller.height = 1.8f;
            controller.radius = .31f;
            controller.center = Vector3.up * .9f;
            var hp = player.AddComponent<AarisOfflineHealth>();
            var cameraRoot = new GameObject("QA main camera", typeof(Camera));
            cameraRoot.transform.SetParent(player.transform, false);
            cameraRoot.transform.localPosition = Vector3.up * 1.65f;
            cameraRoot.tag = "MainCamera";
            var ending = player.AddComponent<AarisOfflineMatchEnd>();
            var root = new GameObject("QA director");
            root.transform.SetParent(temporaryRoot.transform, false);
            director = root.AddComponent<AarisOfflineBRDirector>();
            director.enabled = false;
            Set(director, "player", player.transform);
            Set(director, "playerHealth", hp);
            Set(director, "matchEnd", ending);
            Set(director, "started", true);
            Set(director, "spawnComplete", true);
            // Mathematical far movement deliberately refuses ground without
            // real TerrainData; a BoxCollider-only test silently froze 97 bots.
            // Use transient flat TerrainData without importing the game map.
            var terrainData = new TerrainData();
            terrainData.heightmapResolution = 33;
            terrainData.size = new Vector3(2600f, 10f, 2600f);
            terrainData.hideFlags = HideFlags.DontSave;
            GameObject terrainObject = Terrain.CreateTerrainGameObject(terrainData);
            terrainObject.name = "QA terrain for virtual ground sampling";
            terrainObject.transform.position = new Vector3(-1300f, 0f, -1300f);
            terrainObject.transform.SetParent(temporaryRoot.transform, true);
            var qaTerrain = terrainObject.GetComponent<Terrain>();
            qaTerrain.drawHeightmap = false; // CPU logic-only; no GPU terrain.
            qaTerrain.drawTreesAndFoliage = false;
            TerrainCollider qaCollider = terrainObject.GetComponent<TerrainCollider>();
            if (qaCollider != null) qaCollider.enabled = false;
            Set(director, "terrains", new Terrain[] { qaTerrain });
            // The production director snapshots static Terrain metadata in
            // Start/Begin; this synthetic reflection setup bypasses both.
            // Populate that SAME native cache or all 97 virtual bots freeze
            // at TryVirtualGround even though the terrain is genuinely valid.
            MethodInfo metadataSetup = typeof(AarisOfflineBRDirector).GetMethod(
                "CacheTerrainProbeMetadata", Private);
            if (metadataSetup == null)
                throw new InvalidOperationException("QA_TERRAIN_CACHE_INITIALIZER_MISSING");
            metadataSetup.Invoke(director, null);
            botTransforms = new Transform[100];
            initialPositions = new Vector3[100];
            mainRecorder = ProfilerRecorder.StartNew(
                ProfilerCategory.Internal, "Main Thread", 16);
            gcRecorder = ProfilerRecorder.StartNew(
                ProfilerCategory.Memory, "GC Allocated In Frame", 16);
            File.AppendAllText(Path.Combine(Folder, "result.txt"),
                "BASELINE started; logical bot count 0\n");
        }

        private static void SpawnRivals()
        {
            var botsField = typeof(AarisOfflineBRDirector).GetField("rivals", Private);
            if (botsField == null) throw new InvalidOperationException("No 100 bot roster");
            var roster = (Array)botsField.GetValue(director);
            if (roster.Length != 100) throw new InvalidOperationException("Roster size != 100");
            var player = temporaryRoot.transform.Find("QA player");
            var hp = player.GetComponent<AarisOfflineHealth>();
            var body = player.GetComponent<CharacterController>();
            for (int i = 0; i < 100; i++)
            {
                var go = new GameObject("QA logical rival " + i);
                go.transform.SetParent(temporaryRoot.transform, false);
                Vector3 position;
                if (i == 0) position = new Vector3(6f, 0f, 0f);
                else if (i == 1) position = new Vector3(0f, 0f, 7f);
                else if (i == 2) position = new Vector3(-8f, 0f, 0f);
                else
                {
                    int j = i - 3;
                    position = new Vector3((j % 10 - 5) * 75f,
                        0f, 230f + (j / 10) * 75f);
                }
                go.transform.position = position;
                var capsule = go.AddComponent<CharacterController>();
                capsule.height = 1.8f;
                capsule.radius = .31f;
                capsule.center = Vector3.up * .9f;
                var self = go.AddComponent<AarisOfflineHealth>();
                var brain = go.AddComponent(BotType);
                Set(brain, "owner", director);
                // The synthetic test bypasses Initialize; assign a real
                // roster slot so spatial updates match production behavior.
                Set(brain, "spatialSlot", i);
                Set(brain, "health", self);
                Set(brain, "capsule", capsule);
                Set(brain, "playerTarget", player);
                Set(brain, "playerTargetHealth", hp);
                Set(brain, "playerTargetCapsule", body);
                Set(brain, "lastSimulationTime", Time.time);
                Set(brain, "nextFarSimulation", Time.time + i * .0035f);
                Set(brain, "nextVisualCheck", float.MaxValue);
                Set(brain, "staggeredVisualDelay", float.MaxValue);
                Set(brain, "nextLootAt", float.MaxValue);
                if (i < 3)
                {
                    var marker = new GameObject("QA near-visible signal");
                    marker.transform.SetParent(go.transform, false);
                    Set(brain, "visualRoot", marker);
                }
                botTransforms[i] = go.transform;
                initialPositions[i] = position;
                roster.SetValue(brain, i);
            }
            Physics.SyncTransforms();
            File.AppendAllText(Path.Combine(Folder, "result.txt"),
                "100 lightweight rivals spawned, 3 in local engagement range\n");
        }

        private static void Capture(List<float> frameValues, List<float> cpuValues,
            List<long> gcValues)
        {
            float delta = Time.unscaledDeltaTime * 1000f;
            if (delta > 0f && delta < 3000f) frameValues.Add(delta);
            if (mainRecorder.Valid && mainRecorder.LastValue > 0)
                cpuValues.Add(mainRecorder.LastValue / 1000000f);
            if (gcRecorder.Valid && gcRecorder.LastValue >= 0)
                gcValues.Add(gcRecorder.LastValue);
        }

        private static string Distribution(List<float> values)
        {
            if (values.Count == 0) return "UNAVAILABLE";
            float[] ordered = values.ToArray();
            Array.Sort(ordered);
            double total = 0;
            foreach (float sample in ordered) total += sample;
            return "count=" + ordered.Length + " mean=" +
                (total / ordered.Length).ToString("F3") + "ms p95=" +
                ordered[(int)Mathf.Clamp(Mathf.Ceil(ordered.Length * .95f) - 1,
                    0, ordered.Length - 1)].ToString("F3") + "ms";
        }

        private static string GcStats(List<long> bytes)
        {
            if (bytes.Count == 0) return "UNAVAILABLE";
            long total = 0, max = 0;
            foreach (long value in bytes) { total += value; if (value > max) max = value; }
            return "mean=" + (total / (double)bytes.Count).ToString("F1") +
                " bytes/frame max=" + max + " bytes/frame";
        }

        private static void Complete()
        {
            float gameSeconds = Time.time - loadedGameStart;
            int moved = 0;
            for (int i = 0; i < botTransforms.Length; i++)
            {
                if (botTransforms[i] != null &&
                    (botTransforms[i].position - initialPositions[i]).sqrMagnitude > .01f)
                    moved++;
            }
            // Measure actual deactivation of far PhysX bodies rather than
            // inferring virtualization from offscreen distance alone.
            FieldInfo virtualField = BotType.GetField("virtualBody", Private);
            int virtualized = 0;
            if (virtualField == null) throw new InvalidOperationException("No virtualBody");
            for (int i = 3; i < botTransforms.Length; i++)
            {
                if (botTransforms[i] != null)
                {
                    Component brain = botTransforms[i].GetComponent(BotType);
                    if (brain != null && (bool)virtualField.GetValue(brain))
                        virtualized++;
                }
            }
            if (moved != botTransforms.Length || virtualized < 90)
            {
                Fail("REMOTE_BOTS_NOT_MOVING moved=" + moved +
                    " virtualized=" + virtualized);
                return;
            }
            string report = "RESULT=PASS\n" +
                "ENV=Unity Editor Linux Xvfb, 4 logical CPUs; NOT on-device\n" +
                "BASELINE_FRAME=" + Distribution(baseline) + "\n" +
                "100_BOT_FRAME=" + Distribution(loaded) + "\n" +
                "BASELINE_MAIN_THREAD=" + Distribution(baselineMain) + "\n" +
                "100_BOT_MAIN_THREAD=" + Distribution(loadedMain) + "\n" +
                "BASELINE_GC=" + GcStats(baselineGc) + "\n" +
                "100_BOT_GC=" + GcStats(loadedGc) + "\n" +
                "BOTS_MOVED=" + moved + "/100\n" +
                "REMOTE_VIRTUAL_BODIES=" + virtualized + "/97\n" +
                "LOADED_GAME_SECONDS=" + gameSeconds.ToString("F3") + "\n" +
                "NOTE=Includes Editor overhead; excludes Android GPU, humanoids, map and loot VFX\n";
            File.AppendAllText(Path.Combine(Folder, "result.txt"), report);
            UnityEngine.Debug.Log("AARIS_BOT_FRAME_BENCHMARK " + report);
            Finish(0);
        }

        private static void Fail(string reason)
        {
            if (!failed)
                File.AppendAllText(Path.Combine(Folder, "result.txt"),
                    "RESULT=FAIL " + reason + "\n");
            failed = true;
            Finish(1);
        }

        private static void Finish(int code)
        {
            if (mainRecorder.Valid) mainRecorder.Dispose();
            if (gcRecorder.Valid) gcRecorder.Dispose();
            SessionState.SetInt(Session + "ExitCode", code);
            SessionState.SetBool(Session + "Finish", true);
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        }
    }
}
#endif
