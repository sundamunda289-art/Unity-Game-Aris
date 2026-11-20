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
    public static class Time { public static float unscaledTime; }
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
        internal static bool TryClaimOptionalModelBuild() { return true; }
        internal static void ReserveNativeTeardownFrame() { }
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
    private static void Frame(AarisAdditiveChunkStreamer manager,
        Transform player, float time, float x)
    {
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

        SceneManager.Reset();
        var target = new Transform();
        var manager = Make(target);
        Frame(manager, target, 1f, 210f);
        Check(SceneManager.loadCount == 0, "no eager load outside the margin");
        Frame(manager, target, 1.3f, 200f);
        Check(SceneManager.loadCount == 1, "predictive prefetch should load one chunk");
        Check(SceneManager.pending.priority == -1, "lower native async priority");
        Frame(manager, target, 1.4f, 190f);
        Check(SceneManager.loadCount == 1, "never start concurrent loads");
        SceneManager.Finish();
        Frame(manager, target, 1.5f, 190f);
        Frame(manager, target, 1.8f, 230f);
        Check(SceneManager.unloadCount == 0, "hysteresis retains the scene");
        Frame(manager, target, 2.1f, 300f);
        Check(SceneManager.unloadCount == 1, "unload beyond retention margin");
        SceneManager.Finish();
        Frame(manager, target, 2.2f, 300f);
        Check(SceneManager.loadCount == 1, "distant scene should remain unloaded");

        // Externally loaded scenes are never retired or double-loaded.
        SceneManager.Reset();
        SceneManager.loaded[Path] = true;
        target = new Transform();
        manager = Make(target);
        Frame(manager, target, 4f, 600f);
        Frame(manager, target, 4.5f, 0f);
        Check(SceneManager.unloadCount == 0 && SceneManager.loadCount == 0,
            "only unload scenes owned by this manager");
        Console.WriteLine("PASS: " + checks + " checked assertions (real production sources)");
        return 0;
    }
}
