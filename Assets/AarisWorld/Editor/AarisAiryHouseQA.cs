#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Aaris.MapReview.Editor
{
    [InitializeOnLoad]
    public static class AarisAiryHouseQA
    {
        private const string WorldScene = "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";
        private const string ChunkFolder = "Assets/AarisWorld/Scenes/Chunks/";
        private const string PersistentCoverPrefix = "__AarisPersistentCover_";
        private const string ScatterPrefix = "AiryScatter_";
        private const string ExpansionPrefix = "ARX_";
        private const string OutputVariable = "AARIS_AIRY_HOUSE_QA_OUTPUT";
        private const string ShotsVariable = "AARIS_AIRY_HOUSE_QA_SHOTS";
        private const string HomesVariable = "AARIS_AIRY_HOUSE_QA_HOMES";
        private const string PlayState = "aaris.airyhouse.play.state";
        private const string PlayOutput = "aaris.airyhouse.play.output";
        private const string PlayDeadline = "aaris.airyhouse.play.deadline";
        private const string PlayErrorCount = "aaris.airyhouse.play.errors";
        private const string PlayExitCode = "aaris.airyhouse.play.exit";
        private const int Width = 1280;
        private const int Height = 720;
        private const int ExpectedNativeHomes = 91;
        private const int ExpectedExpansionHomes = 16;
        private const int ExpectedScatterHomes = 21;
        private const int ResolverProbeCalls = 10000;
        private static readonly string[] OriginalHomeNames =
        {
            "a_frame_cabin", "bungalow_house", "pitched_roof_cottage",
            "detached_two_storey_house", "garage_apartment",
            "two_bedroom_garage_house", "row_three_townhouses",
            "warehouse_loading_dock", "OpenDetached", "TownHall",
            "Harbour", "Airfield", "ParcelDepot", "CanalTown", "HanokVillage"
        };
        private static WalkSession walk;

        static AarisAiryHouseQA()
        {
            EditorApplication.update += TickPlay;
            Application.logMessageReceived += RecordPlayError;
        }

        [Serializable]
        private struct Shot
        {
            public string name;
            public Vector3 eye;
            public Vector3 focus;
            public float fieldOfView;

            public Shot(string name, Vector3 anchor, Vector3 eyeOffset, Vector3 focusOffset,
                float fieldOfView = 55f)
            {
                this.name = name;
                eye = anchor + eyeOffset;
                focus = anchor + focusOffset;
                this.fieldOfView = fieldOfView;
            }
        }

        [Serializable]
        private sealed class ShotList
        {
            public Shot[] shots;
        }

        [Serializable]
        private struct Frame
        {
            public Shot shot;
            public string file;
            public string status;
            public float luminanceDeviation;
            public float transparentFraction;
            public float magentaFraction;
        }

        [Serializable]
        private sealed class CaptureReport
        {
            public string scene = WorldScene;
            public string mode = "Edit Mode";
            public string unityVersion = Application.unityVersion;
            public string graphicsDevice = SystemInfo.graphicsDeviceType.ToString();
            public string status = "failed";
            public string error;
            public int terrainTiles;
            public int detailScenes;
            public int width = Width;
            public int height = Height;
            public Frame[] frames;
        }

        [Serializable]
        private sealed class WalkReport
        {
            public string mode = "Play Mode";
            public string unityVersion = Application.unityVersion;
            public string status = "failed";
            public string error;
            public int runtimeErrors;
            public List<WalkResult> homes = new List<WalkResult>();
        }

        [Serializable]
        private sealed class WalkResult
        {
            public string home;
            public string status = "failed";
            public int waypoints;
            public int uphillWaypointsReached;
            public int downhillWaypointsReached;
            public int indoorSockets;
            public int roofSockets;
            public int supportedSockets;
            public float startY;
            public float roofY;
            public float finishY;
            public List<Frame> frames = new List<Frame>();
        }

        [Serializable]
        private sealed class NativeMapReport
        {
            public string scene = WorldScene;
            public string status = "failed";
            public int detailScenes;
            public int originalHomes;
            public int savedExpansionHomes;
            public int scatterHomes;
            public int indoorSockets;
            public int roofSockets;
            public int manifestSockets;
            public int manifestsWithPhysicalSupport;
            public int resolverProbeCalls;
            public long resolverAllocatedBytes = -1;
            public string resolverProbeIssue;
            public bool sameSeedReproduced;
            public int changedSocketPositions;
            public int changedItemsAtSharedPositions;
            public string[] lootItemNames;
            public SeedResult[] seedRuns;
            public List<NativeHomeResult> homes = new List<NativeHomeResult>();
            public List<string> issues = new List<string>();
        }

        [Serializable]
        private sealed class SeedResult
        {
            public string seed;
            public int stockedSockets;
            public int[] itemCounts;
        }

        private delegate bool ResolveSocket<T>(T socket, out Pose placement);

        [Serializable]
        private sealed class NativeHomeResult
        {
            public string name;
            public string scene;
            public string collisionMesh;
            public Vector3 position;
            public float rotationY;
            public int waypoints;
            public int indoorSockets;
            public int roofSockets;
        }

        private sealed class WalkSession
        {
            public readonly WalkReport report = new WalkReport();
            public AarisThirdPersonPlayer player;
            public CharacterController capsule;
            public AarisNearbyColliders nearby;
            public string[] homes;
            public int homeIndex;
            public Vector3[] route;
            public Quaternion inputHeading;
            public int nextStep;
            public int stepDirection;
            public int lastFrame;
            public double stepDeadline;
            public double settleUntil;
            public WalkResult current;
        }

        // World-space anchors from the saved map layout intentionally ignore
        // renderer bounds and scene instance rotation. The same camera shows
        // movement of the facade, roof or courtyard between the two revisions.
        private static Shot[] DefaultShots()
        {
            var valleyCottage = new Vector3(-474.4f, 15.409f, -185f);
            var valleyDetached = new Vector3(-365.6f, 15.409f, -185f);
            var oldBungalow = new Vector3(-472.4f, 1.712f, -633.4f);
            var oldVillage = new Vector3(-530f, 1.712f, -590f);
            var clockTower = new Vector3(350f, -11.937f, 220f);
            var highland = new Vector3(570f, 23.759f, -630f);
            var forest = new Vector3(-650f, 6.766f, -125f);
            return new[]
            {
                new Shot("01-valley-cottage-exterior", valleyCottage,
                    new Vector3(-12f, 6f, -17f), new Vector3(0f, 2.8f, 0f)),
                new Shot("02-valley-cottage-player-eye", valleyCottage,
                    new Vector3(0f, 1.65f, -10.8f), new Vector3(0f, 1.75f, 1f), 68f),
                new Shot("03-valley-two-storey-exterior", valleyDetached,
                    new Vector3(16f, 8.5f, -19f), new Vector3(0f, 3.5f, 0f)),
                new Shot("04-valley-two-storey-roof", valleyDetached,
                    new Vector3(12f, 17f, 12f), new Vector3(0f, 5.2f, 0f)),
                new Shot("05-old-village-bungalow", oldBungalow,
                    new Vector3(14f, 6.5f, 18f), new Vector3(0f, 2.5f, 0f)),
                new Shot("06-old-village-compound", oldVillage,
                    new Vector3(22f, 145f, -28f), new Vector3(0f, 2f, 0f), 68f),
                new Shot("07-clock-tower-compound", clockTower,
                    new Vector3(76f, 132f, -70f), new Vector3(0f, 3f, 0f), 63f),
                new Shot("08-highland-approach", highland,
                    new Vector3(48f, 78f, -76f), new Vector3(0f, 2f, 0f), 63f),
                new Shot("09-forest-lookout", forest,
                    new Vector3(22f, 33f, -39f), new Vector3(0f, 7f, 0f), 62f)
            };
        }

        private static Shot[] ReadShots()
        {
            string path = Environment.GetEnvironmentVariable(ShotsVariable);
            Shot[] shots = string.IsNullOrWhiteSpace(path) ? DefaultShots() :
                JsonUtility.FromJson<ShotList>(File.ReadAllText(path))?.shots;
            if (shots == null || shots.Length == 0)
                throw new InvalidOperationException("At least one camera shot is required.");

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Shot shot in shots)
            {
                if (string.IsNullOrWhiteSpace(shot.name) ||
                    Path.GetFileName(shot.name) != shot.name ||
                    shot.name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    shot.name == "." || shot.name == ".." || !names.Add(shot.name) ||
                    !(shot.fieldOfView >= 20f && shot.fieldOfView <= 105f) ||
                    !(Vector3.Distance(shot.eye, shot.focus) > 0.50f))
                    throw new InvalidOperationException("Invalid or repeated camera shot.");
            }
            return shots;
        }

        private static string OutputDirectory()
        {
            string output = Environment.GetEnvironmentVariable(OutputVariable);
            if (string.IsNullOrWhiteSpace(output))
                output = "/tmp/aaris-airy-house-qa";
            Directory.CreateDirectory(output);
            return output;
        }

        private static void CheckEditorScenes()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Open the capture harness from Edit Mode.");
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isDirty)
                    throw new InvalidOperationException("Save the open scenes before starting capture.");
            }
        }

        private static int OpenDetailScenes()
        {
            int loaded = 0;
            foreach (EditorBuildSettingsScene entry in EditorBuildSettings.scenes)
            {
                if (!entry.enabled || !entry.path.StartsWith(ChunkFolder, StringComparison.Ordinal))
                    continue;
                Scene detail = EditorSceneManager.OpenScene(entry.path, OpenSceneMode.Additive);
                if (!detail.IsValid() || !detail.isLoaded)
                    throw new InvalidOperationException("Could not open detail scene " + entry.path);
                loaded++;
            }
            if (loaded == 0)
                throw new InvalidOperationException("The build scene list has no detail scenes.");
            return loaded;
        }

        private static void HideFallbackSilhouettes(Scene world, List<Renderer> hidden)
        {
            // The fallback component swaps these renderers in Play Mode. Do
            // the equivalent for temporarily opened additive scenes in Edit Mode.
            foreach (GameObject root in world.GetRootGameObjects())
            {
                foreach (AarisChunkFallbackVisibility fallback in
                    root.GetComponentsInChildren<AarisChunkFallbackVisibility>(true))
                {
                    var serialized = new SerializedObject(fallback);
                    string path = serialized.FindProperty("chunkScenePath").stringValue;
                    if (!SceneManager.GetSceneByPath(path).isLoaded) continue;
                    SerializedProperty renderers = serialized.FindProperty("silhouetteRenderers");
                    for (int i = 0; i < renderers.arraySize; i++)
                    {
                        Renderer renderer =
                            renderers.GetArrayElementAtIndex(i).objectReferenceValue as Renderer;
                        if (renderer == null || renderer.forceRenderingOff) continue;
                        hidden.Add(renderer);
                        renderer.forceRenderingOff = true;
                    }
                }
            }
        }

        [MenuItem("Aaris/Houses/Capture matched camera views")]
        public static void Capture()
        {
            CheckEditorScenes();
            string output = OutputDirectory();
            var report = new CaptureReport();
            SceneSetup[] previous = EditorSceneManager.GetSceneManagerSetup();
            var hidden = new List<Renderer>();
            GameObject cameraObject = null;

            try
            {
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                    throw new InvalidOperationException("A graphics device is required for camera capture.");
                Shot[] shots = ReadShots();
                report.frames = new Frame[shots.Length];
                Scene world = EditorSceneManager.OpenScene(WorldScene, OpenSceneMode.Single);
                report.detailScenes = OpenDetailScenes();
                SceneManager.SetActiveScene(world);
                report.terrainTiles = Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None).Length;
                if (report.terrainTiles != 4)
                    throw new InvalidOperationException("Expected four native terrain tiles.");
                HideFallbackSilhouettes(world, hidden);

                cameraObject = new GameObject("Aaris house capture camera") {
                    hideFlags = HideFlags.HideAndDontSave
                };
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.aspect = (float)Width / Height;
                camera.nearClipPlane = 0.075f;
                camera.farClipPlane = 2400f;
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.backgroundColor = new Color(0.19f, 0.25f, 0.29f, 1f);
                camera.allowHDR = false;

                for (int i = 0; i < shots.Length; i++)
                {
                    Shot shot = shots[i];
                    var frame = new Frame { shot = shot, file = shot.name + ".png", status = "failed" };
                    try
                    {
                        camera.fieldOfView = shot.fieldOfView;
                        camera.transform.position = shot.eye;
                        camera.transform.LookAt(shot.focus, Vector3.up);
                        Render(camera, Path.Combine(output, frame.file), ref frame);
                        frame.status = "passed";
                        Debug.Log("AARIS_AIRY_HOUSE_FRAME " + frame.file);
                    }
                    finally { report.frames[i] = frame; }
                }
                report.status = "passed";
                Debug.Log("AARIS_AIRY_HOUSE_CAPTURE_PASS frames=" + shots.Length +
                    " detailScenes=" + report.detailScenes);
            }
            catch (Exception error)
            {
                report.error = error.Message;
                throw;
            }
            finally
            {
                if (cameraObject != null) Object.DestroyImmediate(cameraObject);
                foreach (Renderer renderer in hidden)
                    if (renderer != null) renderer.forceRenderingOff = false;
                File.WriteAllText(Path.Combine(output, "capture-report.json"),
                    JsonUtility.ToJson(report, true));
                if (!Application.isBatchMode) EditorSceneManager.RestoreSceneManagerSetup(previous);
            }
        }

        private static void CollectHouseRoots(Scene scene, Scene world,
            Dictionary<string, GameObject> homes, Dictionary<string, MeshCollider> covers)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (scene != world)
                {
                    foreach (string name in OriginalHomeNames)
                    {
                        if (root.name.IndexOf("_" + name + "_", StringComparison.Ordinal) < 0) continue;
                        homes.Add(root.name, root);
                        break;
                    }
                }
                foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                {
                    if (child.name.StartsWith(ScatterPrefix, StringComparison.Ordinal) ||
                        IsExpansionHouse(child.name))
                        homes.Add(child.name, child.gameObject);
                    if (scene != world || !child.name.StartsWith(PersistentCoverPrefix,
                        StringComparison.Ordinal)) continue;
                    MeshCollider cover = child.GetComponent<MeshCollider>();
                    if (cover != null)
                        covers.Add(child.name.Substring(PersistentCoverPrefix.Length), cover);
                }
            }
        }

        private static bool IsExpansionHouse(string name) =>
            name.StartsWith(ExpansionPrefix, StringComparison.Ordinal) &&
            (name.EndsWith("_cottage", StringComparison.Ordinal) ||
             name.EndsWith("_bungalow", StringComparison.Ordinal) ||
             name.EndsWith("_detached", StringComparison.Ordinal));

        private static HashSet<Collider> ReadStreamedColliders()
        {
            var result = new HashSet<Collider>();
            var nearby = Object.FindFirstObjectByType<AarisNearbyColliders>();
            if (nearby == null) return result;
            var source = new SerializedObject(nearby);
            SerializedProperty obstacles = source.FindProperty("obstacles");
            for (int i = 0; i < obstacles.arraySize; i++)
            {
                var collider = obstacles.GetArrayElementAtIndex(i).objectReferenceValue as Collider;
                if (collider != null) result.Add(collider);
            }
            return result;
        }

        private static void CheckWalkSurfaces(Vector3[] route, string name)
        {
            for (int leg = 1; leg < route.Length; leg++)
            {
                Vector3 start = route[leg - 1], end = route[leg];
                Vector3 move = end - start;
                float run = new Vector2(move.x, move.z).magnitude;
                if (run < .02f || Mathf.Atan2(Mathf.Abs(move.y), run) * Mathf.Rad2Deg >= 43f)
                    throw new InvalidOperationException("Stair segment exceeds the physical rival's slope at " + name);

                int intervals = Mathf.CeilToInt(run / .45f);
                for (int step = 0; step <= intervals; step++)
                {
                    Vector3 sample = Vector3.Lerp(start, end, (float)step / intervals);
                    if (!Physics.Raycast(sample + Vector3.up * .34f, Vector3.down, out RaycastHit hit,
                        .70f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) ||
                        hit.normal.y < .73f || Mathf.Abs(sample.y - hit.point.y) > .30f ||
                        (leg > 1 && hit.collider is TerrainCollider))
                        throw new InvalidOperationException("Unsupported stair route at " + name +
                            " near " + sample.ToString("F2"));
                }
            }
        }

        /// <summary>Checks every partitioned house, its persistent collision and baked scatter.</summary>
        [MenuItem("Aaris/Houses/Audit house copies, stairs and loot sockets")]
        public static void Audit()
        {
            CheckEditorScenes();
            string output = OutputDirectory();
            SceneSetup[] previous = EditorSceneManager.GetSceneManagerSetup();
            var report = new NativeMapReport();
            try
            {
                Scene world = EditorSceneManager.OpenScene(WorldScene, OpenSceneMode.Single);
                report.detailScenes = OpenDetailScenes();
                SceneManager.SetActiveScene(world);
                var houses = new Dictionary<string, GameObject>(StringComparer.Ordinal);
                var covers = new Dictionary<string, MeshCollider>(StringComparer.Ordinal);
                for (int i = 0; i < SceneManager.sceneCount; i++)
                    CollectHouseRoots(SceneManager.GetSceneAt(i), world, houses, covers);
                HashSet<Collider> streamed = ReadStreamedColliders();
                Physics.SyncTransforms();
                var names = new List<string>(houses.Keys);
                names.Sort(StringComparer.Ordinal);
                foreach (string name in names)
                {
                    GameObject home = houses[name];
                    var result = new NativeHomeResult
                    {
                        name = name, scene = home.scene.path, position = home.transform.position,
                        rotationY = home.transform.eulerAngles.y
                    };
                    report.homes.Add(result);
                    bool scatter = name.StartsWith(ScatterPrefix, StringComparison.Ordinal);
                    bool priorExpansion = IsExpansionHouse(name);
                    if (scatter) report.scatterHomes++;
                    else if (priorExpansion) report.savedExpansionHomes++;
                    else report.originalHomes++;
                    try
                    {
                        if (home.GetComponentInChildren<AarisBreakablePanel>(true) != null)
                            throw new InvalidOperationException(name + " has an interactive door or window panel.");
                        if (home.GetComponentInChildren<LODGroup>(true) == null)
                            throw new InvalidOperationException(name + " has no house LOD group.");
                        MeshCollider collider;
                        if (scatter || priorExpansion)
                        {
                            collider = home.GetComponentInChildren<MeshCollider>(true);
                            if (collider == null) covers.TryGetValue(name, out collider);
                        }
                        else if (!covers.TryGetValue(name, out collider) || !streamed.Contains(collider) ||
                                 home.GetComponentInChildren<Collider>(true) != null)
                            throw new InvalidOperationException(name + " has an invalid persistent collider binding.");
                        if (collider == null || !streamed.Contains(collider) || !collider.enabled ||
                            collider.isTrigger || collider.convex || collider.sharedMesh == null)
                            throw new InvalidOperationException(name + " has no live static collision mesh.");

                        result.collisionMesh = AssetDatabase.GetAssetPath(collider.sharedMesh);
                        Vector3[] route = ReadRoute(home);
                        result.waypoints = route.Length;
                        CheckWalkSurfaces(route, name);
                        var socketReport = new WalkResult();
                        CheckSocketFloors(home, socketReport);
                        result.indoorSockets = socketReport.indoorSockets;
                        result.roofSockets = socketReport.roofSockets;
                        report.indoorSockets += result.indoorSockets;
                        report.roofSockets += result.roofSockets;
                    }
                    catch (InvalidOperationException error) { report.issues.Add(error.Message); }
                }
                if (report.originalHomes != ExpectedNativeHomes)
                    report.issues.Add("Expected " + ExpectedNativeHomes + " partitioned house, barrack and compound roots.");
                if (report.savedExpansionHomes != ExpectedExpansionHomes)
                    report.issues.Add("Expected all " + ExpectedExpansionHomes + " prior expansion homes saved in the world.");
                if (report.scatterHomes != ExpectedScatterHomes)
                    report.issues.Add("Expected " + ExpectedScatterHomes + " saved scatter houses.");
                AuditLootManifest(report);
                if (report.issues.Count != 0)
                    throw new InvalidOperationException("House audit recorded " + report.issues.Count + " issue(s).");
                report.status = "passed";
                Debug.Log("AARIS_AIRY_HOUSE_AUDIT_PASS originals=" + report.originalHomes +
                    " expansion=" + report.savedExpansionHomes + " savedScatter=" + report.scatterHomes);
            }
            finally
            {
                File.WriteAllText(Path.Combine(output, "native-map-report.json"), JsonUtility.ToJson(report, true));
                if (!Application.isBatchMode) EditorSceneManager.RestoreSceneManagerSetup(previous);
            }
        }

        private static void AuditLootManifest(NativeMapReport report)
        {
            // Editor and game assemblies remain separate. Invoke the production
            // planner without touching its static current-match state.
            Assembly runtime = typeof(AarisWeaponLootWorld).Assembly;
            Type documentType = runtime.GetType("Aaris.MapReview.AarisLootDocument", true);
            Type plannerType = runtime.GetType("Aaris.MapReview.AarisHouseLoot", true);
            const BindingFlags member = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            TextAsset source = AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Assets/Resources/AarisWeapons/loot_candidates.json");
            if (source == null)
            {
                report.issues.Add("The loot candidate manifest could not be loaded.");
                return;
            }
            object document = JsonUtility.FromJson(source.text, documentType);
            var sockets = (Array)documentType.GetField("houseSockets", member).GetValue(document);
            if (sockets == null || sockets.Length == 0)
            {
                report.issues.Add("Saved house loot manifest is empty.");
                return;
            }
            report.manifestSockets = sockets.Length;
            if (report.manifestSockets != report.indoorSockets + report.roofSockets)
                report.issues.Add("The saved scene and loot manifest socket totals differ.");

            Type socketType = sockets.GetType().GetElementType();
            FieldInfo origin = socketType.GetField("origin", member);
            FieldInfo id = socketType.GetField("slot", member);
            MethodInfo resolve = plannerType.GetMethod("TryResolve", BindingFlags.Static | BindingFlags.NonPublic);
            var stableIds = new HashSet<int>();
            var origins = new Dictionary<string, int>(StringComparer.Ordinal);
            object supportedSocket = null;
            foreach (object socket in sockets)
            {
                string name = (string)origin.GetValue(socket);
                if (!stableIds.Add((int)id.GetValue(socket)))
                    report.issues.Add("Duplicate loot socket ID for " + name);
                origins.TryGetValue(name, out int prior);
                origins[name] = prior + 1;
                if ((bool)resolve.Invoke(null, new[] { socket, (object)default(Pose) }))
                {
                    report.manifestsWithPhysicalSupport++;
                    if (supportedSocket == null) supportedSocket = socket;
                }
                else report.issues.Add("Production loot support check failed for " + name);
            }
            foreach (NativeHomeResult home in report.homes)
            {
                if (!origins.TryGetValue(home.name, out int count) ||
                    count != home.indoorSockets + home.roofSockets)
                    report.issues.Add("Loot manifest does not match the native sockets for " + home.name);
            }
            if (origins.Count != report.homes.Count)
                report.issues.Add("The loot manifest has a different number of named houses.");
            MeasureResolver(report, resolve, socketType, supportedSocket);

            ConstructorInfo planner = plannerType.GetConstructor(member, null,
                new[] { sockets.GetType(), typeof(uint) }, null);
            FieldInfo items = plannerType.GetField("Items", member);
            report.lootItemNames = Enum.GetNames(items.FieldType.GetElementType());
            const uint firstSeed = 0x10203040u;
            const uint secondSeed = 0x60215020u;
            int[] first = PlanSeed(planner, items, sockets, firstSeed);
            int[] repeat = PlanSeed(planner, items, sockets, firstSeed);
            int[] second = PlanSeed(planner, items, sockets, secondSeed);
            report.sameSeedReproduced = true;
            for (int i = 0; i < first.Length; i++)
            {
                if (first[i] != repeat[i]) report.sameSeedReproduced = false;
                if ((first[i] == 0) != (second[i] == 0)) report.changedSocketPositions++;
                if (first[i] != 0 && second[i] != 0 && first[i] != second[i])
                    report.changedItemsAtSharedPositions++;
            }
            report.seedRuns = new[]
            {
                CountSeed(first, firstSeed, report.lootItemNames.Length),
                CountSeed(second, secondSeed, report.lootItemNames.Length)
            };
            int expectedStocked = Mathf.RoundToInt(sockets.Length * .70f);
            foreach (SeedResult run in report.seedRuns)
                if (run.stockedSockets != expectedStocked)
                    report.issues.Add("Seed " + run.seed + " does not stock 70 percent of the manifest.");
            if (!report.sameSeedReproduced || report.changedSocketPositions == 0 ||
                report.changedItemsAtSharedPositions == 0)
                report.issues.Add("Seeded socket selection did not reproduce or vary correctly.");
        }

        private static void MeasureResolver(NativeMapReport report, MethodInfo resolve,
            Type socketType, object supportedSocket)
        {
            if (supportedSocket == null)
            {
                report.resolverProbeIssue = "No physically supported manifest socket was available.";
                return;
            }

            try
            {
                MethodInfo measure = typeof(AarisAiryHouseQA).GetMethod(
                    nameof(MeasureWarmedResolver), BindingFlags.Static | BindingFlags.NonPublic);
                report.resolverAllocatedBytes = (long)measure.MakeGenericMethod(socketType)
                    .Invoke(null, new[] { resolve, supportedSocket });
                report.resolverProbeCalls = ResolverProbeCalls;
                Debug.Log("AARIS_AIRY_HOUSE_SOCKET_GC probes=" + report.resolverProbeCalls +
                    " managedBytes=" + report.resolverAllocatedBytes);
            }
            catch (Exception error)
            {
                report.resolverProbeIssue = error.GetBaseException().GetType().Name;
            }
        }

        private static long MeasureWarmedResolver<T>(MethodInfo method, object supportedSocket)
        {
            var resolve = (ResolveSocket<T>)Delegate.CreateDelegate(typeof(ResolveSocket<T>), method);
            T socket = (T)supportedSocket;
            for (int i = 0; i < 128; i++)
                if (!resolve(socket, out _))
                    throw new InvalidOperationException("The selected socket stopped resolving during warmup.");

            int succeeded = 0;
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < ResolverProbeCalls; i++)
                if (resolve(socket, out _)) succeeded++;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            if (succeeded != ResolverProbeCalls)
                throw new InvalidOperationException("The selected socket stopped resolving during measurement.");
            return allocated;
        }

        private static int[] PlanSeed(ConstructorInfo constructor, FieldInfo items, Array sockets, uint seed)
        {
            object state = constructor.Invoke(new object[] { sockets, seed });
            var plan = (Array)items.GetValue(state);
            var values = new int[plan.Length];
            for (int i = 0; i < values.Length; i++) values[i] = Convert.ToInt32(plan.GetValue(i));
            return values;
        }

        private static SeedResult CountSeed(int[] plan, uint seed, int itemKinds)
        {
            var result = new SeedResult { seed = seed.ToString("X8"), itemCounts = new int[itemKinds] };
            foreach (int kind in plan)
            {
                result.itemCounts[kind]++;
                if (kind != 0) result.stockedSockets++;
            }
            return result;
        }

        // An executeMethod entry point for an isolated Unity project. Do not
        // pass -quit. The completion callback exits with the Play result code.
        public static void VerifyPlay()
        {
            CheckEditorScenes();
            string output = OutputDirectory();
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new InvalidOperationException("A graphics device is required for Play Mode capture.");
            Scene world = EditorSceneManager.OpenScene(WorldScene, OpenSceneMode.Single);
            OpenDetailScenes();
            SceneManager.SetActiveScene(world);

            SessionState.SetString(PlayOutput, output);
            SessionState.SetInt(PlayErrorCount, 0);
            SessionState.SetFloat(PlayDeadline, (float)EditorApplication.timeSinceStartup + 480f);
            SessionState.SetString(PlayState, "starting");
            EditorApplication.isPlaying = true;
        }

        private static void RecordPlayError(string message, string trace, LogType type)
        {
            string phase = SessionState.GetString(PlayState, "");
            if ((phase == "starting" || phase == "walking" || phase == "stopping") &&
                (type == LogType.Error || type == LogType.Assert || type == LogType.Exception))
                SessionState.SetInt(PlayErrorCount, SessionState.GetInt(PlayErrorCount, 0) + 1);
        }

        private static void TickPlay()
        {
            string phase = SessionState.GetString(PlayState, "");
            if (phase == "") return;
            double now = EditorApplication.timeSinceStartup;
            if (phase == "stopping")
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    ExitPlayProbe();
                return;
            }

            try
            {
                if (now > SessionState.GetFloat(PlayDeadline, 0f))
                    throw new InvalidOperationException("Timed out waiting for the stair Play Mode probe.");
                if (!EditorApplication.isPlaying || Time.frameCount < 15) return;
                if (walk == null)
                {
                    walk = StartWalking();
                    SessionState.SetString(PlayState, "walking");
                }
                if (walk.lastFrame == Time.frameCount) return;
                walk.lastFrame = Time.frameCount;

                if (walk.current == null)
                {
                    if (walk.homeIndex >= walk.homes.Length)
                    {
                        FinishPlay("");
                        return;
                    }
                    BeginWalk(walk, now);
                    return;
                }
                AdvanceWalk(walk, now);
            }
            catch (Exception error) { FinishPlay(error.Message); }
        }

        private static WalkSession StartWalking()
        {
            var player = Object.FindFirstObjectByType<AarisThirdPersonPlayer>();
            if (player == null || player.AimCamera == null)
                throw new InvalidOperationException("The native player or follow camera is missing.");
            var capsule = player.GetComponent<CharacterController>();
            if (capsule == null)
                throw new InvalidOperationException("The native player capsule is missing.");

            // A ground-start traversal fixture. Keep the actual locomotion
            // motor, world colliders and loaded-scene routing in Play Mode.
            var director = Object.FindFirstObjectByType<AarisOfflineBRDirector>();
            if (director != null) director.enabled = false;
            var drop = player.GetComponent<AarisOfflineDrop>();
            if (drop != null) drop.enabled = false;
            player.enabled = true;
            string configured = Environment.GetEnvironmentVariable(HomesVariable);
            string[] homes = string.IsNullOrWhiteSpace(configured)
                ? new[]
                {
                    "ValleySettlement_pitched_roof_cottage_02",
                    "OldVillage_bungalow_house_05",
                    "ValleySettlement_detached_two_storey_house_03",
                    "FreightDistrict_warehouse_loading_dock_02"
                }
                : configured.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (homes.Length == 0)
                throw new InvalidOperationException("Select at least one house route.");

            return new WalkSession
            {
                player = player,
                capsule = capsule,
                nearby = Object.FindFirstObjectByType<AarisNearbyColliders>(),
                homes = homes
            };
        }

        private static Transform FindContainer(GameObject home, string name)
        {
            foreach (Transform child in home.GetComponentsInChildren<Transform>(true))
                if (child.name == name) return child;
            return null;
        }

        private static Vector3[] ReadRoute(GameObject home)
        {
            Transform path = FindContainer(home, "WalkRoute");
            if (path == null || path.childCount < 2)
                throw new InvalidOperationException(home.name + " is missing an ordered stair route.");
            var children = new List<Transform>(path.childCount);
            foreach (Transform child in path) children.Add(child);
            children.Sort((a, b) => StringComparer.Ordinal.Compare(a.name, b.name));
            var route = new Vector3[children.Count];
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i].name != "Step" + (i + 1).ToString("00"))
                    throw new InvalidOperationException(home.name + " has a misnumbered stair route.");
                route[i] = children[i].position;
            }
            if (route[route.Length - 1].y < route[0].y + 1f)
                throw new InvalidOperationException(home.name + " has no upper stair destination.");
            return route;
        }

        private static bool IsSupportedSocket(Transform socket)
        {
            Vector3 position = socket.position;
            if (!Physics.Raycast(position + Vector3.up * .32f, Vector3.down,
                out RaycastHit hit, .55f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return false;
            if (Mathf.Abs(hit.point.y - position.y) > .18f || hit.normal.y < .90f ||
                hit.collider is TerrainCollider || hit.collider.attachedRigidbody != null)
                return false;
            return !Physics.CheckSphere(hit.point + Vector3.up * .25f, .17f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        }

        private static void CheckSocketFloors(GameObject home, WalkResult result)
        {
            Transform sockets = FindContainer(home, "LootSpawnSockets");
            if (sockets == null || sockets.childCount < 2 || sockets.childCount > 6)
                throw new InvalidOperationException(home.name + " requires two to six loot sockets.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Transform socket in sockets)
            {
                if (!names.Add(socket.name))
                    throw new InvalidOperationException(home.name + " has repeated loot socket IDs.");
                if (socket.name.StartsWith("Indoor", StringComparison.Ordinal)) result.indoorSockets++;
                else if (socket.name.StartsWith("Roof", StringComparison.Ordinal)) result.roofSockets++;
                else throw new InvalidOperationException(home.name + " has an unclassified loot socket.");
                if (IsSupportedSocket(socket)) result.supportedSockets++;
            }
            if (result.supportedSockets != sockets.childCount)
                throw new InvalidOperationException(home.name + " has unsupported or obstructed loot sockets.");
        }

        private static void BeginWalk(WalkSession session, double now)
        {
            string name = session.homes[session.homeIndex].Trim();
            var result = new WalkResult { home = name };
            session.report.homes.Add(result);
            session.current = result;
            GameObject home = GameObject.Find(name);
            if (home == null)
                throw new InvalidOperationException("Selected house is absent in Play Mode: " + name);
            if (home.GetComponentInChildren<AarisBreakablePanel>(true) != null)
                throw new InvalidOperationException(name + " still has a door or breakable pane.");
            session.route = ReadRoute(home);
            Vector3 direction = session.route[1] - session.route[0];
            direction.y = 0f;
            if (direction.sqrMagnitude < .04f)
                throw new InvalidOperationException(name + " has a vertical first stair step.");
            session.inputHeading = Quaternion.LookRotation(direction, Vector3.up);
            session.capsule.enabled = false;
            session.player.transform.SetPositionAndRotation(session.route[0] + Vector3.up * .09f,
                session.inputHeading);
            session.capsule.enabled = true;
            session.player.ResumeFromAerialDrop();
            session.player.CancelAutoSprint();
            session.player.SetEditorLocomotionQAInput(Vector2.zero);
            AarisBattleActions battle = session.player.GetComponent<AarisBattleActions>();
            if (battle != null && !battle.TryStand())
                throw new InvalidOperationException("The player could not stand at " + name);
            if (session.nearby != null) session.nearby.RefreshNow();
            Physics.SyncTransforms();
            CheckSocketFloors(home, result);
            result.waypoints = session.route.Length;
            result.startY = session.route[0].y;
            session.nextStep = 0;
            session.stepDirection = 1;
            session.settleUntil = now + .5;
            session.stepDeadline = now + 22.0;
        }

        private static void AdvanceWalk(WalkSession session, double now)
        {
            if (now < session.settleUntil) return;
            WalkResult result = session.current;
            int step = session.nextStep;
            Vector3 position = session.player.transform.position;
            Vector3 target = session.route[step];
            if (now > session.stepDeadline)
                throw new InvalidOperationException(result.home + " blocked near Step" + (step + 1) +
                    " at " + position.ToString("F2") + " while approaching " + target.ToString("F2"));
            Vector3 direction = target - position;
            float heightError = Mathf.Abs(direction.y);
            direction.y = 0f;
            if (direction.sqrMagnitude <= .28f * .28f && heightError <= .37f)
            {
                bool first = step == 0;
                bool last = step == session.route.Length - 1;
                if ((first || last) && !session.capsule.isGrounded) return;
                if (session.stepDirection > 0)
                {
                    result.uphillWaypointsReached++;
                    if (first) CapturePlayer(session, "approach");
                    else if (last)
                    {
                        result.roofY = position.y;
                        session.player.SetEditorLocomotionQAInput(Vector2.zero);
                        CapturePlayer(session, "roof");
                        session.stepDirection = -1;
                        session.settleUntil = now + .35;
                    }
                    else if (step == session.route.Length / 2) CapturePlayer(session, "stairs");
                }
                else
                {
                    result.downhillWaypointsReached++;
                    if (first)
                    {
                        result.finishY = position.y;
                        result.status = "passed";
                        session.player.SetEditorLocomotionQAInput(Vector2.zero);
                        Debug.Log("AARIS_AIRY_HOUSE_WALK_PASS " + result.home);
                        session.homeIndex++;
                        session.current = null;
                        return;
                    }
                }
                session.nextStep += session.stepDirection;
                session.stepDeadline = now + 22.0;
                return;
            }
            Vector3 local = Quaternion.Inverse(session.inputHeading) * direction.normalized;
            float speed = Mathf.Clamp(direction.magnitude * .62f, .16f, .9f);
            session.player.SetEditorLocomotionQAInput(new Vector2(local.x, local.z) * speed);
        }

        private static void CapturePlayer(WalkSession session, string angle)
        {
            Camera camera = session.player.AimCamera;
            Vector3 eye = camera.transform.position;
            string label = session.current.home + "-" + angle;
            var frame = new Frame
            {
                shot = new Shot(label, eye, Vector3.zero, camera.transform.forward, camera.fieldOfView),
                file = label + ".png",
                status = "failed"
            };
            try
            {
                Render(camera, Path.Combine(SessionState.GetString(PlayOutput, ""), frame.file), ref frame);
                frame.status = "passed";
            }
            finally { session.current.frames.Add(frame); }
        }

        private static void FinishPlay(string error)
        {
            if (walk != null && walk.player != null) walk.player.ClearEditorLocomotionQAInput();
            WalkReport report = walk == null ? new WalkReport() : walk.report;
            report.error = error;
            string output = SessionState.GetString(PlayOutput, "");
            File.WriteAllText(Path.Combine(output, "play-report.json"), JsonUtility.ToJson(report, true));
            SessionState.SetInt(PlayExitCode, string.IsNullOrEmpty(error) ? 0 : 1);
            SessionState.SetString(PlayState, "stopping");
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        }

        private static void ExitPlayProbe()
        {
            string output = SessionState.GetString(PlayOutput, "");
            string path = Path.Combine(output, "play-report.json");
            WalkReport report = JsonUtility.FromJson<WalkReport>(File.ReadAllText(path));
            int code = SessionState.GetInt(PlayExitCode, 1);
            report.runtimeErrors = SessionState.GetInt(PlayErrorCount, 0);
            if (report.runtimeErrors > 0) code = 1;
            report.status = code == 0 ? "passed" : "failed";
            File.WriteAllText(path, JsonUtility.ToJson(report, true));
            SessionState.SetString(PlayState, "");
            walk = null;
            if (code == 0) Debug.Log("AARIS_AIRY_HOUSE_PLAY_PASS homes=" + report.homes.Count);
            else Debug.LogError("AARIS_AIRY_HOUSE_PLAY_FAILED report=" + path);
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        private static void Render(Camera camera, string path, ref Frame frame)
        {
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture target = RenderTexture.GetTemporary(Width, Height, 24,
                RenderTextureFormat.ARGB32);
            var image = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = target;
                GL.Clear(true, true, Color.clear);
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
                image.Apply(false);

                Color32[] pixels = image.GetPixels32();
                double sum = 0d, squares = 0d;
                int count = 0, transparent = 0, magenta = 0, black = 0, white = 0;
                for (int i = 0; i < pixels.Length; i += 113)
                {
                    Color32 pixel = pixels[i];
                    double luminance = .2126 * pixel.r + .7152 * pixel.g + .0722 * pixel.b;
                    sum += luminance;
                    squares += luminance * luminance;
                    count++;
                    if (pixel.a <= 5) transparent++;
                    if (pixel.r > 195 && pixel.g < 95 && pixel.b > 195) magenta++;
                    if (pixel.r < 18 && pixel.g < 18 && pixel.b < 18) black++;
                    if (pixel.r > 242 && pixel.g > 242 && pixel.b > 242) white++;
                }
                double mean = sum / count;
                frame.luminanceDeviation = (float)Math.Sqrt(Math.Max(0d, squares / count - mean * mean));
                frame.transparentFraction = (float)transparent / count;
                frame.magentaFraction = (float)magenta / count;
                if (frame.luminanceDeviation < 6f || frame.transparentFraction > .93f ||
                    black > count * .93f || white > count * .93f)
                    throw new InvalidOperationException("Invalid camera readback for " + frame.file);
                if (magenta >= Math.Max(4, count / 1000))
                    throw new InvalidOperationException("Possible shader error in " + frame.file);

                byte[] png = image.EncodeToPNG();
                if (png.Length < 8192)
                    throw new InvalidOperationException("Unexpectedly small camera image " + frame.file);
                File.WriteAllBytes(path, png);
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                Object.DestroyImmediate(image);
                RenderTexture.ReleaseTemporary(target);
            }
        }
    }
}
#endif
