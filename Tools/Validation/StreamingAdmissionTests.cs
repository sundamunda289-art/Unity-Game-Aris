// Controlled API fixtures around the ENTIRE production gate, not a policy mirror.
using System;
using System.Reflection;
using UnityEngine;
using Aaris.MapReview;

namespace UnityEngine
{
    public class Object { }
    public class GameObject : Object { }
    public class AsyncOperation { public bool isDone; public int priority; }
    public sealed class ResourceRequest : AsyncOperation { }
    public enum RuntimeInitializeLoadType { SubsystemRegistration }
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute
    {
        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType type) { }
    }
    public static class Time { public static int frameCount; public static float unscaledDeltaTime; }
    public static class SystemInfo
    {
        public static int ram, memoryReads, gpuReads;
        public static string gpu;
        public static int systemMemorySize { get { memoryReads++; return ram; } }
        public static string graphicsDeviceName { get { gpuReads++; return gpu; } }
    }
    public static class Resources
    {
        public static int requests;
        public static ResourceRequest LoadAsync<T>(string path) where T : Object
        { requests++; return new ResourceRequest(); }
    }
}

namespace Aaris.MapReview
{
    public static class AarisMobileRuntime
    {
        /* GPU_POLICY */
    }
}

internal static class StreamingAdmissionTests
{
    private static int checks;
    private static void Check(bool ok, string message)
    { checks++; if (!ok) throw new Exception(message); }
    private static void Frame(int frame, float delta = 0.016f)
    { Time.frameCount = frame; Time.unscaledDeltaTime = delta; }
    private static void Configure(int ram = 4096, string gpu = "Adreno (TM) 610")
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        typeof(AarisMobileStreamingFrameGate).GetMethod("Reset",
            BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
#endif
        SystemInfo.ram = ram; SystemInfo.gpu = gpu;
        SystemInfo.memoryReads = SystemInfo.gpuReads = Resources.requests = 0;
        Frame(1);
    }
    private static ResourceRequest Load(string path = "optional-model")
    { return AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<GameObject>(path); }

#if UNITY_ANDROID && !UNITY_EDITOR
    private static void Hardware()
    {
        foreach (int ram in new[] { 0, -1, 4096, 6144, 8192 })
            foreach (string gpu in new[] { "Adreno 610", "Adreno (TM) 610",
                "Mali_G52", "Mali-G57 MC2", "PowerVR Rogue GE8320" })
            {
                Configure(ram, gpu);
                Check(AarisMobileStreamingFrameGate.IsConstrainedDevice(), "Weak GPU bypassed budget");
                Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(), "First slot missing");
                Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(), "Duplicate frame slot");
                int memoryReads = SystemInfo.memoryReads, gpuReads = SystemInfo.gpuReads;
                for (int f = 2; f <= 100; f++)
                {
                    Frame(f);
                    Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(), "Fast frame stalled");
                }
                Check(SystemInfo.memoryReads == memoryReads && SystemInfo.gpuReads == gpuReads,
                    "Hot path repeated native device queries");
            }
        foreach (string gpu in new[] { "Adreno 730", "Adreno 6100", "Adreno (TM) 610X",
            "Mali-G57 MC4", "Mali-G520 MC2", "PowerVR GE83200", "unknown", "" })
            foreach (int ram in new[] { 0, 6144, 8192 })
            {
                Configure(ram, gpu);
                Check(!AarisMobileStreamingFrameGate.IsConstrainedDevice(), "Fast/unknown GPU misclassified");
                Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() &&
                    AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(), "Fast device throttled");
                Check(Load() != null && Load() != null, "Fast imports throttled");
            }
        Configure(2048, "Adreno 730");
        Check(AarisMobileStreamingFrameGate.IsConstrainedDevice(), "Low RAM safety lost");
    }

    private static void ImportFence()
    {
        Configure(6144);
        ResourceRequest request = Load();
        Check(request != null && request.priority == -1, "Async import missing priority");
        for (int f = 1; f <= 20; f++)
        {
            Frame(f);
            Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(), "Build overlapped import");
            Check(Load() == null, "Concurrent import admitted");
        }
        Check(Resources.requests == 1, "Rejected imports still touched native API");
        Frame(21); request.isDone = true;
        for (int caller = 0; caller < 8; caller++)
        {
            Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(), "Completion frame reused");
            Check(Load() == null, "New import on integration frame");
        }
        Frame(22);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(), "Completed import held forever");
        Frame(23);
        Check(Load() != null, "Optional imports did not resume");
        // Essential systems use Resources directly and must never wait here.
        Check(Resources.LoadAsync<GameObject>("essential-weapon") != null,
            "Essential loader intercepted");
        Configure();
        Check(Load(null) == null && Load("") == null && Resources.requests == 0, "Invalid path imported");
    }

    private static void Pressure()
    {
        Configure();
        int admitted = 0;
        for (int frame = 1; frame <= 300; frame++)
        {
            Frame(frame, 0.080f);
            for (int caller = 0; caller < 8; caller++)
                if (AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild()) admitted++;
        }
        Check(admitted == 100, "Pressure starved work or consumed recovery twice in one frame");
        Check(AarisMobileStreamingFrameGate.PressureDeferredFrames == 200, "Pressure frame evidence wrong");
        Frame(301, 0.08f);
        Check(Load() == null && Resources.requests == 0, "Pressure import bypass");
        Frame(302);
        Check(Load() != null, "Healthy frame failed to recover immediately");
        Configure();
        Frame(1, 0.050f);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(), "50 ms boundary deferred");
        Frame(2, 0.051f);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(), "Slow frame unprotected");
        Frame(3);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(), "Recovery retained stale decision");
    }

    private static void CleanupRigAndReset()
    {
        foreach (string gpu in new[] { "Adreno 610", "Adreno 730" })
        {
            Configure(8192, gpu);
            var cleanup = new AsyncOperation();
            AarisMobileStreamingFrameGate.DeferOptionalWorkDuringAssetCleanup(cleanup);
            Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() && Load() == null,
                "Cleanup allowed optional work");
            cleanup.isDone = true;
            Frame(2);
            Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() && Load() == null,
                "Cleanup completion frame reused for optional integration");
            Frame(3);
            Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(), "Cleanup held forever");
        }
        // An already-complete cleanup is not a no-op: its current frame
        // may still be saturated with native asset integration. Exercise the
        // production gate on both a fast and a constrained Android GPU.
        foreach (string gpu in new[] { "Adreno 610", "Adreno 730" })
        {
            Configure(8192, gpu);
            Frame(12);
            var immediate = new AsyncOperation { isDone = true };
            AarisMobileStreamingFrameGate.DeferOptionalWorkDuringAssetCleanup(immediate);
            Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() && Load() == null,
                "Already-complete cleanup frame admitted optional native work");
            Check(Resources.requests == 0, "Cleanup frame dispatched a native import");
            Frame(13);
            Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
                "Already-complete cleanup blocked the next frame");
        }
        Configure(6144);
        Check(AarisMobileStreamingFrameGate.ReserveNextFrameForEssentialRig(), "6 GB rig unprotected");
        Frame(2);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() && Load() == null,
            "Optional work overlapped reserved rig frame");
        Frame(3);
        Check(Load() != null, "Reservation did not expire");
        Configure(8192, "Adreno 730");
        Check(!AarisMobileStreamingFrameGate.ReserveNextFrameForEssentialRig(), "Fast rig delayed");
        Check(AarisMobileStreamingFrameGate.ImportDeferredAttempts == 0 &&
              AarisMobileStreamingFrameGate.ImportSettleDeferredAttempts == 0 &&
              AarisMobileStreamingFrameGate.PressureDeferredFrames == 0 &&
              AarisMobileStreamingFrameGate.RigReservedDeferredAttempts == 0,
            "Subsystem reset leaked state");
        Check(Load() != null, "Reset retained old in-flight request");
    }
#endif

    public static void Main()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        Hardware(); ImportFence(); Pressure(); CleanupRigAndReset();
        Console.WriteLine("STREAMING_ADMISSION_ANDROID_PASS checks=" + checks);
#else
        Configure();
        for (int f = 1; f <= 100; f++)
        {
            Frame(f, 0.5f);
            Check(!AarisMobileStreamingFrameGate.IsConstrainedDevice(), "Non-Android policy changed");
            Check(!AarisMobileStreamingFrameGate.ReserveNextFrameForEssentialRig(), "Non-Android rig delayed");
            Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() &&
                  AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(), "Non-Android build delayed");
            Check(Load() != null && Load() != null, "Non-Android import delayed");
        }
        Check(SystemInfo.memoryReads == 0 && SystemInfo.gpuReads == 0, "Non-Android queried device");
        Console.WriteLine("STREAMING_ADMISSION_NON_ANDROID_PASS checks=" + checks);
#endif
        Console.WriteLine("Controlled API fixture; no Unity/PhysX timing or Android FPS claim.");
    }
}
