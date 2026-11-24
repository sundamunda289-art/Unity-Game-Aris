// Controlled Unity API substitute around the EXACT production component.
// Tests event-driven silhouette residency without requiring Unity Editor.
// Native Unity render/visibility, occlusion and APK testing remain mandatory.
using System;
using System.Collections.Generic;
using System.Reflection;
using Aaris.MapReview;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityEngine
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class DisallowMultipleComponent : Attribute { }
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute { }
    public enum RuntimeInitializeLoadType { SubsystemRegistration }
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute
    {
        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType loadType) { }
    }
    public class MonoBehaviour { }
    public sealed class Renderer
    {
        private bool visible = true;
        public int reads;
        public bool enabled
        {
            get { reads++; return visible; }
            set { visible = value; }
        }
    }
}

namespace UnityEngine.SceneManagement
{
    public enum LoadSceneMode { Additive }
    public struct Scene
    {
        public string path;
        public bool isLoaded;
        public int handle;
    }
    public static class SceneManager
    {
        private static readonly List<Scene> loaded = new List<Scene>();
        private static int nextHandle;
        public static event Action<Scene, LoadSceneMode> sceneLoaded;
        public static event Action<Scene> sceneUnloaded;
        public static int sceneCount { get { return loaded.Count; } }
        public static int loadSubscribers
        {
            get { return sceneLoaded == null ? 0 : sceneLoaded.GetInvocationList().Length; }
        }

        public static Scene GetSceneAt(int index) { return loaded[index]; }

        public static int RaiseLoad(string path, bool notify = true)
        {
            var scene = new Scene { path = path, isLoaded = true, handle = ++nextHandle };
            loaded.Add(scene);
            if (notify && sceneLoaded != null)
                sceneLoaded(scene, LoadSceneMode.Additive);
            return scene.handle;
        }
        public static void RaiseUnload(string path, bool pathKnown = true)
        {
            int index = loaded.FindIndex(s => s.path == path);
            if (index < 0) throw new Exception("Missing loaded fixture scene " + path);
            Scene scene = loaded[index];
            loaded.RemoveAt(index);
            scene.isLoaded = false;
            if (!pathKnown) scene.path = null;
            if (sceneUnloaded != null)
                sceneUnloaded(scene);
        }
        public static void Reset()
        {
            loaded.Clear();
            sceneLoaded = null;
            sceneUnloaded = null;
        }
    }
}

internal static class WorldChunkFallbackVisibilityTests
{
    private static int checks;
    private static void Assert(bool value, string reason)
    {
        checks++;
        if (!value) throw new Exception(reason);
    }
    private static void Set(object instance, string name, object value)
    {
        FieldInfo field = instance.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) throw new Exception("Missing production field " + name);
        field.SetValue(instance, value);
    }
    private static void Call(object instance, string name)
    {
        MethodInfo method = instance.GetType().GetMethod(name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null) throw new Exception("Missing lifecycle " + name);
        method.Invoke(instance, null);
    }
    public static int Main()
    {
        const string target = "Assets/AarisWorld/Scenes/Chunks/Aaris_ForestLookout_Detail.unity";
        const string other = "Assets/Other_Detail.unity";

        SceneManager.Reset();
        var originalFar = new Renderer();
        var secondFar = new Renderer();
        var c = new AarisChunkFallbackVisibility();
        Set(c, "chunkScenePath", target);
        Set(c, "silhouetteRenderers", new [] { originalFar, secondFar, null });
        Call(c, "OnEnable");
        Assert(originalFar.enabled && secondFar.enabled, "Unloaded chunk has no silhouette");

        SceneManager.RaiseLoad(other);
        Assert(originalFar.enabled && secondFar.enabled, "Unrelated scene affected silhouette");
        SceneManager.RaiseLoad(target);
        Assert(!originalFar.enabled && !secondFar.enabled,
               "Loaded high-detail chunk duplicated distant LOD");
        SceneManager.RaiseUnload(other);
        Assert(!originalFar.enabled && !secondFar.enabled,
               "Unrelated unload changed full-detail visibility");
        SceneManager.RaiseUnload(target);
        Assert(originalFar.enabled && secondFar.enabled,
               "Scene unloading left a distant silhouette hole");

        SceneManager.RaiseLoad(target);
        Assert(!originalFar.enabled && !secondFar.enabled,
               "Full detail was not restored on reload");
        SceneManager.RaiseUnload(target, pathKnown: false);
        Assert(originalFar.enabled && secondFar.enabled,
               "Native unload invalidated Scene.path and hid last-LOD silhouette");

        SceneManager.RaiseLoad(target);
        Call(c, "OnDisable");
        Assert(originalFar.enabled && secondFar.enabled,
               "Disabling pilot manager erased original distant landmark");
        SceneManager.RaiseUnload(target);
        SceneManager.RaiseLoad(target);
        Assert(originalFar.enabled, "Disabled manager still subscribed to scene events");

        Call(c, "OnEnable");
        Assert(!originalFar.enabled && !secondFar.enabled,
               "Re-enabled manager failed to detect already loaded chunk");
        SceneManager.RaiseUnload(target);
        Assert(originalFar.enabled && secondFar.enabled,
               "Re-enabled manager missed unload event");
        Call(c, "OnDisable");

        SceneManager.Reset();
        c = new AarisChunkFallbackVisibility();
        var emergencySilhouette = new Renderer { enabled = false };
        Set(c, "silhouetteRenderers", new [] { emergencySilhouette });
        Call(c, "OnEnable");
        Assert(emergencySilhouette.enabled, "Missing scene path must fail visibly, not invisibly");
        Call(c, "OnDisable");

        SceneManager.Reset();
        SceneManager.RaiseLoad(target, notify: false);
        c = new AarisChunkFallbackVisibility();
        var alreadyLoaded = new Renderer();
        Set(c, "chunkScenePath", target);
        Set(c, "silhouetteRenderers", new [] { alreadyLoaded });
        Call(c, "OnEnable");
        Assert(!alreadyLoaded.enabled, "Cold enable duplicated a resident full-detail chunk");
        Call(c, "OnDisable");
        Assert(alreadyLoaded.enabled, "Disable must release distant renderer");

        SceneManager.Reset();
        // Mirror the production manifest: 94 distant building components
        // across 13 scene paths. An unrelated load must visit no renderers.
        var members = new AarisChunkFallbackVisibility[94];
        var silhouettes = new Renderer[members.Length];
        SceneManager.RaiseLoad(target + 0, notify: false);
        SceneManager.RaiseLoad(target + 0, notify: false);
        for (int i = 0; i < members.Length; i++)
        {
            members[i] = new AarisChunkFallbackVisibility();
            silhouettes[i] = new Renderer();
            Set(members[i], "chunkScenePath", target + (i % 13));
            Set(members[i], "silhouetteRenderers", new [] { silhouettes[i] });
            Call(members[i], "OnEnable");
        }
        Assert(SceneManager.loadSubscribers == 1, "One registration per building is expensive");
        int beforeUnrelated = silhouettes[1].reads;
        SceneManager.RaiseLoad(other);
        SceneManager.RaiseUnload(other, pathKnown: false);
        Assert(silhouettes[1].reads == beforeUnrelated, "Unrelated event touched skyline");
        SceneManager.RaiseUnload(target + 0, pathKnown: false);
        Assert(!silhouettes[0].enabled, "First of two unloads duplicated a visible building");
        SceneManager.RaiseUnload(target + 0, pathKnown: false);
        Assert(silhouettes[0].enabled, "Last unload failed exact handle handoff");
        beforeUnrelated = silhouettes[1].reads;
        SceneManager.RaiseLoad(target + 2);
        Assert(!silhouettes[2].enabled && silhouettes[1].reads == beforeUnrelated,
               "Loading district two affected a different district");
        foreach (var member in members) Call(member, "OnDisable");
        Assert(SceneManager.loadSubscribers == 0, "Last registration failed to unsubscribe");

        Console.WriteLine("WORLD_CHUNK_FALLBACK_RUNTIME_PASS checks=" + checks);
        Console.WriteLine("Controlled API fixture only; no native Unity FPS or render screenshot claim.");
        return 0;
    }
}
