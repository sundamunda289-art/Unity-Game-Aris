// A deliberately small engine substitute: this compiles the *production*
// planner and MonoBehaviour with Mono and exercises the async ownership logic.
using System;
using System.Collections.Generic;
using System.Reflection;
using Aaris.MapReview;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityEngine
{
    [AttributeUsage(AttributeTargets.Class)] public sealed class DisallowMultipleComponent : Attribute { }
    [AttributeUsage(AttributeTargets.Field)] public sealed class SerializeField : Attribute { }
    [AttributeUsage(AttributeTargets.Field)] public sealed class MinAttribute : Attribute
    { public MinAttribute(float value) { } }
    public class MonoBehaviour { public GameObject gameObject = new GameObject(); }
    public sealed class GameObject { public Scene scene = new Scene { path = "Assets/Boot.unity", isLoaded = true }; }
    public sealed class Transform { public Vector3 position; }
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
    }
    public struct Vector3 { public float x, y, z; }
    public class AsyncOperation { public bool isDone; public int priority; }
    public static class Time { public static float unscaledTime; public static int frameCount; }
    public static class Mathf
    {
        public static float Max(float a, float b) { return Math.Max(a, b); }
    }
    public static class Debug
    {
        public static readonly List<string> warnings = new List<string>();
        public static void LogWarning(string value) { warnings.Add(value); }
    }
}
namespace UnityEngine.SceneManagement
{
    public enum LoadSceneMode { Additive }
    public struct Scene
    {
        public string path;
        public bool isLoaded;
        public bool IsValid() { return !string.IsNullOrEmpty(path); }
    }
    public static class SceneUtility
    {
        public static int GetBuildIndexByScenePath(string path)
        { return path.StartsWith("Assets/Cell") ? 1 : -1; }
    }
    public static class SceneManager
    {
        public static readonly Dictionary<string, bool> loaded = new Dictionary<string, bool>();
        public static AsyncOperation pending;
        public static string pendingPath;
        public static bool pendingLoad;
        public static int loadCount, unloadCount;
        public static Scene GetSceneByPath(string path)
        {
            bool exists = loaded.ContainsKey(path);
            return new Scene { path = exists ? path : null, isLoaded = exists && loaded[path] };
        }
        public static AsyncOperation LoadSceneAsync(string path, LoadSceneMode mode)
        {
            if (pending != null) throw new InvalidOperationException("concurrent operation");
            loadCount++;
            pendingPath = path;
            pendingLoad = true;
            return pending = new AsyncOperation();
        }
        public static AsyncOperation UnloadSceneAsync(string path)
        {
            if (pending != null) throw new InvalidOperationException("concurrent operation");
            unloadCount++;
            pendingPath = path;
            pendingLoad = false;
            return pending = new AsyncOperation();
        }
        public static void Finish()
        {
            if (pending == null) throw new InvalidOperationException("nothing loading");
            loaded[pendingPath] = pendingLoad;
            pending.isDone = true;
            pending = null;
        }
        public static void Reset()
        {
            loaded.Clear(); pending = null; loadCount = unloadCount = 0;
        }
    }
}
namespace Aaris.MapReview
{
    internal static class AarisMobileStreamingFrameGate
    {
        internal static int physicsClaims, teardownClaims, trackedSceneLoads;
        internal static void TrackAdditiveSceneLoad(AsyncOperation operation)
        { if (operation != null) trackedSceneLoads++; }
        internal static bool IsConstrainedDevice() { return true; }
        internal static bool TryClaimOptionalModelBuild() { return true; }
        internal static void ReservePhysicsStreamingFrame() { physicsClaims++; }
        internal static void ReserveNativeTeardownFrame() { teardownClaims++; }
    }
}

internal static class AdditiveChunkStreamingTests
{
    private const string Path = "Assets/CellNorth.unity";
    private static int checks;
    private static void Check(bool value, string message)
    {
        checks++;
        if (!value) throw new Exception(message);
    }
    private static void Set(object instance, string field, object value)
    {
        typeof(AarisAdditiveChunkStreamer).GetField(field,
            BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, value);
    }
    private static void Invoke(AarisAdditiveChunkStreamer instance, string method)
    {
        typeof(AarisAdditiveChunkStreamer).GetMethod(method,
            BindingFlags.NonPublic | BindingFlags.Instance).Invoke(instance, null);
    }
    private static int ResidentCount(AarisAdditiveChunkStreamer instance)
    {
        return (int)typeof(AarisAdditiveChunkStreamer).GetField(
            "trackedResidentCount", BindingFlags.NonPublic | BindingFlags.Instance)
            .GetValue(instance);
    }
    private static AarisAdditiveChunkStreamer Make(Transform player)
    {
        var manager = new AarisAdditiveChunkStreamer();
        Set(manager, "player", player);
        Set(manager, "chunks", new[] { new AarisAdditiveChunkStreamer.Chunk
        {
            scenePath = Path, center = new Vector2(0, 0),
            halfExtents = new Vector2(100, 100)
        }});
        Invoke(manager, "Awake");
        Invoke(manager, "OnEnable");
        return manager;
    }
    private static AarisAdditiveChunkStreamer MakeMultiple(
        Transform player, params AarisAdditiveChunkStreamer.Chunk[] entries)
    {
        var manager = new AarisAdditiveChunkStreamer();
        Set(manager, "player", player);
        Set(manager, "chunks", entries);
        Invoke(manager, "Awake");
        Invoke(manager, "OnEnable");
        return manager;
    }

    private static void ResetScene()
    {
        SceneManager.Reset();
        Time.frameCount = 0;
#if UNITY_ANDROID && !UNITY_EDITOR
        // Mimic a fresh Android play session between unrelated test cases.
        // Runtime statics persist only within one session.
        typeof(AarisAdditiveChunkStreamer).GetField("sharedSceneOperation",
            BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, null);
        typeof(AarisAdditiveChunkStreamer).GetField("sharedSceneCompletionFrame",
            BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, -1);
#endif
    }

    private static void Frame(AarisAdditiveChunkStreamer manager,
        Transform player, float time, float x)
    {
        Time.frameCount++;
        Time.unscaledTime = time;
        player.position = new Vector3 { x = x, z = 0 };
        Invoke(manager, "Update");
    }
    public static int Main()
    {
        // Exact AABB bounds, strict entry/exit hysteresis, no GC in policy.
        Check(AarisChunkStreamingPolicy.OutsideDistanceSquared(100, 5, 0, 0, 100, 100) == 0f,
            "cell edge must stay inside");
        Check(AarisChunkStreamingPolicy.OutsideDistanceSquared(130, 140, 0, 0, 100, 100) == 2500f,
            "outside distance should include both axes");
        float px, pz;
        AarisChunkStreamingPolicy.Predict(200, 0, 210, 0, .3f, 1.25f, 35, 90, out px, out pz);
        Check(px < 200 && px > 150, "prefetch should follow observed movement");
        AarisChunkStreamingPolicy.Predict(-300, 0, 200, 0, .3f, 1.25f, 35, 90, out px, out pz);
        Check(px == -300, "teleport cannot trigger predictive prefetch");
        AarisChunkStreamingPolicy.Predict(200, 0, 210, 0, 5f, 1.25f, 35, 90, out px, out pz);
        Check(px == 200, "resume gap should disable extrapolation");

        ResetScene();
        var target = new Transform();
        var manager = Make(target);
        Frame(manager, target, 1f, 210f);
        Check(SceneManager.loadCount == 0, "no eager load outside the margin");
        Frame(manager, target, 1.3f, 200f);
        Check(SceneManager.loadCount == 1, "predictive prefetch should load one chunk");
        Check(SceneManager.pending.priority == -1, "lower native async priority");
#if UNITY_ANDROID && !UNITY_EDITOR
        Check(AarisMobileStreamingFrameGate.trackedSceneLoads == 1,
            "scene launch must register its native loading window");
#endif
        Frame(manager, target, 1.4f, 190f);
        Check(SceneManager.loadCount == 1, "never start concurrent loads");
        SceneManager.Finish();
        Frame(manager, target, 1.5f, 190f);
#if UNITY_ANDROID && !UNITY_EDITOR
        Check(AarisMobileStreamingFrameGate.physicsClaims == 1,
            "completed native scene integration reserves a separate frame");
#endif
        Frame(manager, target, 1.8f, 230f);
        Check(SceneManager.unloadCount == 0, "hysteresis retains the scene");
        Frame(manager, target, 2.1f, 300f);
        Check(SceneManager.unloadCount == 1, "unload beyond retention margin");
        SceneManager.Finish();
        Frame(manager, target, 2.2f, 300f);
#if UNITY_ANDROID && !UNITY_EDITOR
        Check(AarisMobileStreamingFrameGate.teardownClaims == 2,
            "native scene unload reserves start and completion frames");
#endif
        Check(SceneManager.loadCount == 1, "distant scene should remain unloaded");

        // A zero speculative residency budget blocks only optional prefetch.
        // A truly near chunk still loads at its original entry distance.
#if UNITY_ANDROID && !UNITY_EDITOR
        ResetScene();
        target = new Transform();
        manager = Make(target);
        Set(manager, "maxSpeculativeResidentCells", 0);
        Frame(manager, target, 3f, 210f);
        Frame(manager, target, 3.3f, 200f);
        Check(SceneManager.loadCount == 0, "speculative prefetch respects budget");
        Frame(manager, target, 3.6f, 180f);
        Check(SceneManager.loadCount == 1, "near-player scene bypasses speculative cap");

        // Regression: a predicted point INSIDE the cell has outside-distance 0.
        // Its numeric priority ties entrySquared, but the actual player is still
        // outside the entry margin. It must remain speculative and respect cap=0.
        ResetScene();
        target = new Transform();
        manager = Make(target);
        Set(manager, "maxSpeculativeResidentCells", 0);
        Set(manager, "lookaheadSeconds", 4f);
        Frame(manager, target, 5f, 230f);
        Frame(manager, target, 5.3f, 210f);
        Check(SceneManager.loadCount == 0,
            "predicted-inside cell must not bypass speculative budget on equality");
        Frame(manager, target, 5.6f, 180f);
        Check(SceneManager.loadCount == 1,
            "actual near-player distance still overrides speculative budget");
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
        // Two independent scene managers must not start competing native
        // additive activations on the same low-end Android render frame.
        ResetScene();
        var parallelTarget = new Transform();
        var firstManager = Make(parallelTarget);
        var secondManager = MakeMultiple(parallelTarget,
            new AarisAdditiveChunkStreamer.Chunk
            {
                scenePath = "Assets/CellEast.unity",
                center = new Vector2(500, 0),
                halfExtents = new Vector2(100, 100)
            });
        Frame(firstManager, parallelTarget, 60f, 0f);
        Check(SceneManager.loadCount == 1, "first manager begins one scene operation");
        // Different manager Update in SAME rendered frame.
        parallelTarget.position = new Vector3 { x = 500, z = 0 };
        Invoke(secondManager, "Update");
        Check(SceneManager.loadCount == 1, "second manager must not start concurrent load");
        SceneManager.Finish();
        Frame(secondManager, parallelTarget, 60.3f, 500f);
        Check(SceneManager.loadCount == 1, "completion observation reserves its frame");
        Frame(firstManager, parallelTarget, 60.4f, 500f);
        Frame(secondManager, parallelTarget, 60.7f, 500f);
        Check(SceneManager.loadCount == 2, "second manager loads after first integration");
        SceneManager.Finish();
        Frame(secondManager, parallelTarget, 61f, 500f);
        Check(SceneManager.loadCount == 2, "no extra duplicate loads");
#endif

        // Externally loaded scenes are never retired or double-loaded.
        ResetScene();
        SceneManager.loaded[Path] = true;
        target = new Transform();
        manager = Make(target);
        Frame(manager, target, 4f, 600f);
        Frame(manager, target, 4.5f, 0f);
        Check(SceneManager.unloadCount == 0 && SceneManager.loadCount == 0,
            "only unload scenes owned by this manager");
        // One far resident scene must be retired before an optional
        // predicted-only cell is admitted: bounded peak resident memory.
        ResetScene();
        target = new Transform();
        manager = MakeMultiple(target,
            new AarisAdditiveChunkStreamer.Chunk {
                scenePath = Path, center = new Vector2(0, 0),
                halfExtents = new Vector2(100, 100)
            },
            new AarisAdditiveChunkStreamer.Chunk {
                scenePath = "Assets/CellEast.unity", center = new Vector2(570, 0),
                halfExtents = new Vector2(100, 100)
            });
        Frame(manager, target, 1f, 0f);
        Check(SceneManager.loadCount == 1, "current-position cell should load");
        SceneManager.Finish();
        Frame(manager, target, 1.1f, 0f);
        Set(manager, "previousSampleValid", true);
        Set(manager, "previousSample", new Vector3 { x = 333, z = 0 });
        Set(manager, "previousSampleAt", 1.0f);
        Set(manager, "nextPollAt", 0f);
        Set(manager, "nextTransitionAt", 0f);
        Frame(manager, target, 1.3f, 350f);
        Check(SceneManager.unloadCount == 1 && SceneManager.loadCount == 1,
            "stale resident should retire before speculative prefetch");
        SceneManager.Finish();
        Frame(manager, target, 1.4f, 350f);
        Frame(manager, target, 1.7f, 400f);
        Check(SceneManager.loadCount == 2,
            "actual-position urgent cell must still load next");

        // An external loader can win the race AFTER the streamer's Awake.
        // Never duplicate its load or later unload someone else's scene.
        ResetScene();
        target = new Transform();
        manager = Make(target);
        SceneManager.loaded[Path] = true;
        Frame(manager, target, 4f, 0f);
        Frame(manager, target, 4.5f, 700f);
        Check(SceneManager.loadCount == 0 && SceneManager.unloadCount == 0,
            "externally loaded cell must remain externally owned");

        // Another manager can retire a scene formerly loaded by us.
        // Avoid Unity's redundant unload and allow a later clean reload.
        ResetScene();
        target = new Transform();
        manager = Make(target);
        Frame(manager, target, 5f, 0f);
        SceneManager.Finish();
        Frame(manager, target, 5.1f, 0f);
        SceneManager.loaded[Path] = false;
        Frame(manager, target, 5.5f, 500f);
        Check(SceneManager.unloadCount == 0,
            "already absent scene should not be unloaded again");
        Frame(manager, target, 5.8f, 0f);
        Check(SceneManager.loadCount == 2,
            "externally retired scene should be reloadable");

        // A different manager can unload a scene while the player stays
        // inside it. The next poll must reload that visible cell, not wait
        // for the player to leave the hysteresis zone and come back.
        ResetScene();
        target = new Transform();
        manager = Make(target);
        Frame(manager, target, 9f, 0f);
        SceneManager.Finish();
        Frame(manager, target, 9.1f, 0f);
        SceneManager.loaded[Path] = false;
        Frame(manager, target, 9.5f, 0f);
        Check(SceneManager.loadCount == 2,
            "externally retired near scene must be admitted again");

        // An externally owned scene can disappear too; ownership must be
        // rechecked before excluding it from the next near-player poll.
        ResetScene();
        SceneManager.loaded[Path] = true;
        target = new Transform();
        manager = Make(target);
        Frame(manager, target, 10f, 0f);
        Check(SceneManager.loadCount == 0, "externally owned scene stays untouched");
        SceneManager.loaded[Path] = false;
        Frame(manager, target, 10.4f, 0f);
        Check(SceneManager.loadCount == 1,
            "retired externally owned scene must become streamable");

        // A large chunk manifest uses an indexed duplicate check during Awake.
        // The original behavior rejected a valid path that repeated even an
        // earlier *invalid* cell, and treats paths with different case separately.
        ResetScene();
        target = new Transform();
        var duplicateCells = new AarisAdditiveChunkStreamer.Chunk[18];
        for (int i = 0; i < duplicateCells.Length; i++)
            duplicateCells[i] = new AarisAdditiveChunkStreamer.Chunk {
                scenePath = "Assets/CellUnique" + i + ".unity",
                center = new Vector2(i * 4000f, 0f),
                halfExtents = new Vector2(100f, 100f)
            };
        duplicateCells[0].scenePath = "Assets/CellInvalidFirst.unity";
        duplicateCells[0].halfExtents = new Vector2(0f, 100f);
        duplicateCells[1].scenePath = duplicateCells[0].scenePath;
        duplicateCells[2].scenePath = "Assets/CellShared.unity";
        duplicateCells[3].scenePath = "Assets/CellShared.unity";
        duplicateCells[4].scenePath = "Assets/CellCase.unity";
        duplicateCells[5].scenePath = "Assets/Cellcase.unity";
        SceneManager.loaded["Assets/CellShared.unity"] = true;
        SceneManager.loaded["Assets/CellCase.unity"] = true;
        SceneManager.loaded["Assets/Cellcase.unity"] = true;
        int warningsBeforeIndex = Debug.warnings.Count;
        manager = MakeMultiple(target, duplicateCells);
        Check(Debug.warnings.Count == warningsBeforeIndex + 3,
            "large-manifest index must preserve invalid and duplicate warnings");
        Check(ResidentCount(manager) == 3,
            "indexed dedupe must keep one resident owner plus case-distinct cells");

        // Real open worlds can contain thousands of authored subscenes.
        // Compare bucket retrieval against the EXACT outside-AABB oracle,
        // including large fallback cells, negative coordinates, and teleports.
        const int cellCount = 1025;
        var spatial = new AarisChunkSpatialIndex(cellCount);
        var random = new Random(20261121);
        var xs = new float[cellCount];
        var zs = new float[cellCount];
        var halfXs = new float[cellCount];
        var halfZs = new float[cellCount];
        for (int i = 0; i < cellCount; i++)
        {
            xs[i] = random.Next(-7000, 7000);
            zs[i] = random.Next(-7000, 7000);
            halfXs[i] = random.Next(20, 240);
            halfZs[i] = random.Next(20, 240);
            if (i == cellCount - 1)
            {
                xs[i] = 0f; zs[i] = 0f;
                halfXs[i] = 2000f; halfZs[i] = 1900f;
            }
            spatial.Add(i, xs[i], zs[i], halfXs[i], halfZs[i]);
        }
        var found = new bool[cellCount];
        for (int step = 0; step < 160; step++)
        {
            float x = random.Next(-8500, 8500);
            float z = random.Next(-8500, 8500);
            float radius = random.Next(1, 300);
            int hits = spatial.Query(x, z, radius);
            Check(hits >= 0 && hits <= cellCount,
                "ordinary large-world query must use bounded spatial bins");
            Array.Clear(found, 0, found.Length);
            for (int j = 0; j < hits; j++)
            {
                int index = spatial.CandidateAt(j);
                Check(index >= 0 && index < cellCount && !found[index],
                    "no duplicate or invalid cell from overlapping index bins");
                found[index] = true;
            }
            for (int i = 0; i < cellCount; i++)
            {
                float distance = AarisChunkStreamingPolicy.OutsideDistanceSquared(
                    x, z, xs[i], zs[i], halfXs[i], halfZs[i]);
                Check(distance > radius * radius || found[i],
                    "spatial index must never omit a near/urgent valid chunk");
            }
        }
        Check(spatial.Query(0f, 0f, 100000000f) == -1,
            "huge radius must fail open to the original complete scan");
        var pathological = new AarisChunkSpatialIndex(1);
        pathological.Add(0, 1e38f, 1e38f, 1e38f, 1e38f);
        Check(pathological.Query(0f, 0f, 150f) == -1,
            "extreme authored bounds must fail open to complete scan");

        // A 512-cell empty world used to scan every state on every poll.
        // Residency reconciliation must be independent of unloaded cell count.
        ResetScene();
        target = new Transform();
        var emptyCells = new AarisAdditiveChunkStreamer.Chunk[512];
        for (int i = 0; i < emptyCells.Length; i++)
            emptyCells[i] = new AarisAdditiveChunkStreamer.Chunk {
                scenePath = "Assets/CellEmpty" + i + ".unity",
                center = new Vector2(i * 4000f, 0f),
                halfExtents = new Vector2(100f, 100f)
            };
        manager = MakeMultiple(target, emptyCells);
        Check(ResidentCount(manager) == 0, "empty authored grid must have no resident entries");
        for (int i = 0; i < 20; i++)
            Frame(manager, target, 30f + i * .3f, -1000000f);
        Check(ResidentCount(manager) == 0 && SceneManager.loadCount == 0,
            "empty world reconciliation must stay inactive and allocation-free");

        // Sparse externally owned scenes must reconcile in resident order,
        // not by scanning hundreds of inactive authored chunk definitions.
        ResetScene();
        SceneManager.loaded["Assets/CellEmpty2.unity"] = true;
        SceneManager.loaded["Assets/CellEmpty509.unity"] = true;
        manager = MakeMultiple(target, emptyCells);
        Check(ResidentCount(manager) == 2,
            "preloaded sparse scenes need both tracked resident slots");
        SceneManager.loaded["Assets/CellEmpty2.unity"] = false;
        Frame(manager, target, 50f, -1000000f);
        Check(ResidentCount(manager) == 1,
            "externally retired scene must leave the resident index");
        SceneManager.loaded["Assets/CellEmpty509.unity"] = false;
        Frame(manager, target, 50.3f, -1000000f);
        Check(ResidentCount(manager) == 0,
            "swap-remove must not skip the remaining external resident");
        Frame(manager, target, 50.6f, -1000000f);
        Check(SceneManager.unloadCount == 0,
            "an external scene must never be unloaded by this streamer");

        Console.WriteLine("PASS: " + checks + " checked assertions (real production sources)");
        return 0;
    }
}
