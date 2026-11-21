// Controlled API fixtures around the ENTIRE production gate, not a policy mirror.
using System;
using System.Reflection;
using UnityEngine;
using Aaris.MapReview;

namespace UnityEngine
{
    public class Object { }
    public class GameObject : Object { }
    public class AudioClip : Object { }
    public class TextAsset : Object { }
    public class AsyncOperation { public bool isDone; public int priority; }
    public sealed class ResourceRequest : AsyncOperation { }
    public enum RuntimeInitializeLoadType { SubsystemRegistration }
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute
    {
        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType type) { }
    }
    public static class Time { public static int frameCount; public static float unscaledDeltaTime; }
    public static class Application { public static int targetFrameRate = 30; }
    public struct RefreshRate { public double value; }
    public struct Resolution { public RefreshRate refreshRateRatio; }
    public static class Screen
    {
        public static int reads;
        public static Resolution resolution;
        public static Resolution currentResolution { get { reads++; return resolution; } }
    }
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
    private static void Configure(int ram = 4096, string gpu = "Adreno (TM) 610",
        int targetFps = 30, double refreshHz = 60d)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        typeof(AarisMobileStreamingFrameGate).GetMethod("Reset",
            BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
#endif
        SystemInfo.ram = ram; SystemInfo.gpu = gpu;
        Application.targetFrameRate = targetFps;
        Screen.resolution = new Resolution {
            refreshRateRatio = new RefreshRate { value = refreshHz }
        };
        Screen.reads = 0;
        SystemInfo.memoryReads = SystemInfo.gpuReads = Resources.requests = 0;
        Frame(1);
    }
    private static ResourceRequest Load(string path = "optional-model")
    { return AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<GameObject>(path); }

#if UNITY_ANDROID && !UNITY_EDITOR
    private static void PhysicsCatchupAdmission()
    {
        Configure(4096, "Adreno (TM) 610");
        Frame(20);
        AarisMobileStreamingFrameGate.ReservePhysicsCatchupFrame(2);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Normal 30 FPS physics cadence incorrectly throttled");

        Frame(21);
        AarisMobileStreamingFrameGate.ReservePhysicsCatchupFrame(3);
        AarisMobileStreamingFrameGate.ReservePhysicsCatchupFrame(4);
        Check(AarisMobileStreamingFrameGate.PhysicsCatchupDeferredFrames == 1,
            "Same-frame repeated physics signal double counted");
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "First three-tick catchup was not protected");
        Check(AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<GameObject>(
            "optional-model") == null, "Model import bypassed catchup frame");

        Frame(22);
        AarisMobileStreamingFrameGate.ReservePhysicsCatchupFrame(5);
        Check(!AarisMobileStreamingFrameGate.TryClaimNonessentialDiagnosticFrame(),
            "Diagnostic bypassed catchup frame");

        Frame(23);
        AarisMobileStreamingFrameGate.ReservePhysicsCatchupFrame(3);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Sustained catchup starved streaming on its third frame");

        Frame(24);
        AarisMobileStreamingFrameGate.ReservePhysicsCatchupFrame(1);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Normal physics failed to recover streaming");

        Frame(25);
        AarisMobileStreamingFrameGate.ReservePhysicsCatchupFrame(3);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Recovered catchup streak did not restart safety fence");
        Check(AarisMobileStreamingFrameGate.PhysicsCatchupDeferredFrames == 3,
            "Catchup deferral counter drifted");

        Configure(6144, "Adreno 610");
        Frame(30);
        AarisMobileStreamingFrameGate.ReservePhysicsCatchupFrame(3);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "6GB Adreno 610 incorrectly bypassed physics fence");

        Configure(8192, "Adreno 730");
        Frame(31);
        AarisMobileStreamingFrameGate.ReservePhysicsCatchupFrame(6);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Fast Android GPU unexpectedly throttled");
        Check(AarisMobileStreamingFrameGate.PhysicsCatchupDeferredFrames == 0,
            "Subsystem reset leaked physics catchup state");
    }

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

    private static void EssentialAudioFence()
    {
        // Actual production gate with mocked native Unity async completion.
        Configure(6144, "Adreno (TM) 610");
        ResourceRequest cue = Resources.LoadAsync<AudioClip>("SFX/ar_shot");
        Check(cue != null && Resources.requests == 1,
            "Direct gameplay audio was suppressed");
        AarisMobileStreamingFrameGate.TrackEssentialAudioLoad(cue);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Optional model overlapped direct audio start");
        for (int frame = 2; frame <= 4; frame++)
        {
            Frame(frame);
            Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
                "Optional model overlapped essential audio in flight");
            Check(!AarisMobileStreamingFrameGate.TryClaimNonessentialDiagnosticFrame(),
                "Diagnostics overlapped essential audio in flight");
            Check(Load() == null, "Optional import collided with audio");
        }
        cue.isDone = true;
        Frame(5);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Audio completion frame admitted optional model");
        Frame(6);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Audio completion permanently blocked visuals");
        Check(AarisMobileStreamingFrameGate.EssentialAudioDeferredAttempts >= 4,
            "Missing essential audio deferral evidence");
        Configure(6144, "Adreno 610");
        Frame(7);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Scene reset retained old essential audio request");
        Configure(8192, "Adreno 730");
        AarisMobileStreamingFrameGate.TrackEssentialAudioLoad(
            Resources.LoadAsync<AudioClip>("SFX/ar_shot"));
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "High-end Android was throttled by essential audio");
    }

    private static void AdditiveSceneFence()
    {
        // Production gate under controlled native operation states. Multiple
        // streaming systems must each retain their own in-flight reservation.
        Configure(6144, "Adreno 610");
        var first = new AsyncOperation();
        AarisMobileStreamingFrameGate.TrackAdditiveSceneLoad(first);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() &&
              Load() == null, "Scene started with overlapping FBX import");
        var second = new AsyncOperation();
        AarisMobileStreamingFrameGate.TrackAdditiveSceneLoad(second);
        for (int frame = 2; frame <= 4; frame++)
        {
            Frame(frame);
            Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() &&
                  !AarisMobileStreamingFrameGate.TryClaimNonessentialDiagnosticFrame(),
                "In-flight authored scene collided with optional work");
        }
        first.isDone = true;
        Frame(5);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Scene completion allowed same-frame native integration");
        Frame(6);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Second unfinished scene lost its own fence");
        second.isDone = true;
        Frame(7);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() &&
              Load() == null, "Last scene completion allowed concurrent import");
        Frame(8);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Scene completion permanently starved authored detail");
        Check(AarisMobileStreamingFrameGate.AdditiveSceneDeferredAttempts >= 6,
            "Deferred scene/import evidence missing");
        Configure(6144, "Adreno 610");
        Frame(2);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() &&
              AarisMobileStreamingFrameGate.AdditiveSceneDeferredAttempts == 0,
            "Subsystem reset retained stale scene fences");
        Configure(8192, "Adreno 730");
        AarisMobileStreamingFrameGate.TrackAdditiveSceneLoad(new AsyncOperation());
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "High-end Android GPU was unnecessarily throttled");
    }

    private static void EssentialWeaponFence()
    {
        Configure(6144, "Adreno 610");
        var definitions = Resources.LoadAsync<TextAsset>("weapon_catalog");
        AarisMobileStreamingFrameGate.TrackEssentialWeaponLoad(definitions);
        Check(Resources.requests == 1, "Direct catalog request missing");
        Frame(2);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() &&
              Load() == null, "Optional import overlapped weapon catalog");
        definitions.isDone = true;
        Frame(3);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Catalog completion frame reused");
        Frame(4);
        var gun = Resources.LoadAsync<GameObject>("gun");
        AarisMobileStreamingFrameGate.TrackEssentialWeaponLoad(gun);
        Frame(5);
        Check(!AarisMobileStreamingFrameGate.TryClaimNonessentialDiagnosticFrame(),
            "Gun model import overlapped diagnostic");
        gun.isDone = true;
        Frame(6);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Gun integration frame reused");
        Frame(7);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Completed weapon import starved visuals");
        Check(AarisMobileStreamingFrameGate.EssentialWeaponDeferredAttempts >= 3,
            "Essential weapon deferral count missing");

        Configure(6144, "Adreno 610");
        var audio = Resources.LoadAsync<AudioClip>("shot");
        AarisMobileStreamingFrameGate.TrackEssentialAudioLoad(audio);
        var weapon = Resources.LoadAsync<GameObject>("gun");
        AarisMobileStreamingFrameGate.TrackEssentialWeaponLoad(weapon);
        Frame(2);
        weapon.isDone = true;
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Overlap allowed optional import");
        Frame(3);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Unfinished audio was ignored");
        audio.isDone = true;
        Frame(4);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Audio completion bypassed native fence");
        Frame(5);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Overlapping completed imports blocked forever");

        Configure(8192, "Adreno 730");
        AarisMobileStreamingFrameGate.TrackEssentialWeaponLoad(
            Resources.LoadAsync<GameObject>("gun"));
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "High-end phone throttled");
        Configure(6144, "Adreno 610");
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Static reset retained weapon import");
    }

    private static void EssentialLootAndJsonParseFence()
    {
        Configure(6144, "Adreno 610");
        var manifest = Resources.LoadAsync<TextAsset>("loot_candidates");
        AarisMobileStreamingFrameGate.TrackEssentialLootManifestLoad(manifest);
        Frame(2);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() &&
              Load() == null && !AarisMobileStreamingFrameGate.TryClaimNonessentialDiagnosticFrame(),
            "Loot manifest import overlapped optional models or diagnostics");
        manifest.isDone = true;
        Frame(3);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Loot completion frame was reused");
        Frame(4);
        Check(AarisMobileStreamingFrameGate.ReserveNextFrameForEssentialJsonParse(),
            "Essential parse reservation missing");
        Frame(5);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() &&
              Load() == null && !AarisMobileStreamingFrameGate.TryClaimNonessentialDiagnosticFrame(),
            "Managed JSON parse overlapped optional native work");
        // A second reservation on the first parse frame cannot erase it.
        Check(AarisMobileStreamingFrameGate.ReserveNextFrameForEssentialJsonParse(),
            "Second parse reservation failed");
        Frame(6);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Concurrent parse reservation overwritten");
        Frame(7);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Completed parse starved optional models");
        Check(AarisMobileStreamingFrameGate.EssentialLootDeferredAttempts >= 2 &&
              AarisMobileStreamingFrameGate.EssentialJsonParseDeferredAttempts >= 3,
            "Missing essential manifest and parse deferral counters");

        Configure(6144, "Adreno 610");
        var audio = Resources.LoadAsync<AudioClip>("SFX");
        AarisMobileStreamingFrameGate.TrackEssentialAudioLoad(audio);
        var gun = Resources.LoadAsync<GameObject>("gun");
        AarisMobileStreamingFrameGate.TrackEssentialWeaponLoad(gun);
        var loot = Resources.LoadAsync<TextAsset>("loot");
        AarisMobileStreamingFrameGate.TrackEssentialLootManifestLoad(loot);
        Frame(2);
        audio.isDone = gun.isDone = true;
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Simultaneous audio, gun and loot load admitted cosmetics");
        Frame(3);
        loot.isDone = true;
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Third essential completion bypassed shared fence");
        Frame(4);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Three completed essential imports blocked forever");

        Configure(8192, "Adreno 730");
        AarisMobileStreamingFrameGate.TrackEssentialLootManifestLoad(
            Resources.LoadAsync<TextAsset>("loot"));
        Check(!AarisMobileStreamingFrameGate.ReserveNextFrameForEssentialJsonParse() &&
              AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "High-end device throttled by JSON parser");
        Configure(6144, "Adreno 610");
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Subsystem reset retained manifest or parse reservation");
    }

    private static void LootTeardownFence()
    {
        Configure(6144, "Adreno 610");
        Frame(12);
        AarisMobileStreamingFrameGate.ReserveNativeTeardownFrame();
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Destroyed loot collided with optional model build");
        Check(!AarisMobileStreamingFrameGate.TryClaimNonessentialDiagnosticFrame(),
            "Destroyed loot collided with diagnostic snapshot");
        Frame(13);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() &&
              !AarisMobileStreamingFrameGate.TryClaimNonessentialDiagnosticFrame(),
            "Native loot teardown settle frame admitted optional work");
        Frame(14);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Completed loot teardown starved nearby models");

        Configure(8192, "Adreno 730");
        Frame(12);
        AarisMobileStreamingFrameGate.ReserveNativeTeardownFrame();
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "High-end device unnecessarily throttled by teardown");
        Configure(6144, "Adreno 610");
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Subsystem reset retained previous teardown reservation");
    }

    private static void DiagnosticImportFence()
    {
        // This regression executes the actual production gate with controlled
        // Unity ResourceRequest states, not a mirrored Python policy.
        Configure(6144, "Adreno 610");
        ResourceRequest import = Load();
        Check(import != null, "Optional import not admitted");
        Frame(2);
        Check(!AarisMobileStreamingFrameGate.TryClaimNonessentialDiagnosticFrame(),
            "Profiler/JSON snapshot overlapped unfinished native import");
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Other optional model overlapped unfinished import");
        import.isDone = true;
        Frame(3);
        Check(!AarisMobileStreamingFrameGate.TryClaimNonessentialDiagnosticFrame(),
            "Profiler/JSON snapshot reused native integration completion frame");
        Frame(4);
        Check(AarisMobileStreamingFrameGate.TryClaimNonessentialDiagnosticFrame(),
            "Profiler/JSON snapshot failed to resume after import completion");
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Diagnostic and model claimed the same frame");
        Frame(5);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Optional build starved after diagnostic frame");
        // High-end Android and non-Android policies must remain untouched.
        Configure(8192, "Adreno 730");
        Check(Load() != null, "Fast-device import missing");
        Frame(2);
        Check(AarisMobileStreamingFrameGate.TryClaimNonessentialDiagnosticFrame(),
            "High-end Android diagnostic incorrectly throttled");
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
        // A sustained 40 ms frame is under the old 50 ms threshold, but has
        // already missed 30 FPS. Share one decision across every requester.
        Configure();
        int moderateAdmitted = 0;
        for (int frame = 1; frame <= 120; frame++)
        {
            Frame(frame, 0.040f);
            for (int caller = 0; caller < 6; caller++)
                if (AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild()) moderateAdmitted++;
        }
        Check(moderateAdmitted == 60, "Moderate pressure must admit every other attempt-frame");
        Check(AarisMobileStreamingFrameGate.PressureDeferredFrames == 60,
            "Moderate pressure counted duplicate callers or lost a deferral");
        Frame(121);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Healthy frame must recover from moderate pressure without extra delay");
        Configure();
        Frame(1, 0.050f);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "50 ms moderate pressure allowed an optional native import");
        Frame(2, 0.051f);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(), "Slow frame unprotected");
        Frame(3);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(), "Recovery retained stale decision");

        // A 500 ms resume/native hitch must not be followed immediately by
        // fresh cosmetic model construction, even when frame pacing recovers.
        // This is a bounded three-frame cooldown; authored visual assets
        // remain eligible on the next attempt.
        Configure();
        Frame(1, 0.500f);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Long hitch admitted optional integration");
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Second caller bypassed long-hitch admission");
        for (int frame = 2; frame <= 4; frame++)
        {
            Frame(frame, 0.016f);
            Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
                "Post-hitch recovery admitted early");
        }
        Check(AarisMobileStreamingFrameGate.PressureDeferredFrames == 4,
            "Long hitch cooldown counted a frame more than once");
        Frame(5, 0.016f);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Long hitch cooldown delayed optional detail indefinitely");

        // Every rendered frame may be a long hitch on a thermal or memory-
        // pressured Android handset. The 3-frame cooldown previously moved
        // forward forever, making original-detail optional models disappear.
        Configure();
        int longHitchAdmitted = 0;
        for (int frame = 1; frame <= 70; frame++)
        {
            Frame(frame, 0.500f);
            bool accepted = AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild();
            if (accepted) longHitchAdmitted++;
            Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
                "More than one long-hitch optional integration per frame");
        }
        Check(longHitchAdmitted == 10,
            "Continuous long hitches permanently starved optional detail");
        Check(AarisMobileStreamingFrameGate.PressureDeferredFrames == 60,
            "Long-hitch deferrals were not capped or counted once per frame");
        Frame(71, 0.016f);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Recovered healthy frame retained long-hitch starvation");

        // Several consecutive native hitches still receive a recovery window
        // after the most recent one, unless the bounded fairness cap expires.
        Configure();
        Frame(1, 0.500f);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(), "First long hitch allowed");
        Frame(2, 0.500f);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(), "Second long hitch allowed");
        for (int frame = 3; frame <= 5; frame++)
        {
            Frame(frame, 0.016f);
            Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
                "Final long hitch recovery window ended early");
        }
        Frame(6, 0.016f);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Recovery after consecutive hitches did not resume");

        // A 249 ms severe frame uses the original bounded policy; it must
        // not accidentally inherit the larger resume cooldown.
        Configure();
        Frame(1, 0.249f);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Severe near-threshold frame admitted");
        Frame(2, 0.016f);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Near-threshold frame incorrectly entered long-hitch cooldown");
    }

    private static void RefreshAwarePressure()
    {
        // 60 Hz / 60 FPS: 20 ms is a missed frame budget. Defer optional
        // integrations every other attempt, without starving them.
        Configure(targetFps: 60, refreshHz: 60d);
        for (int f = 1; f <= 12; f++)
        {
            Frame(f, 0.020f);
            Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() == (f % 2 == 0),
                "60 FPS did not fence optional work on 20 ms frames");
        }
        Check(Screen.reads == 1, "Display refresh queried on hot path");
        // At 30 FPS the same 20 ms is healthy and must not delay assets.
        Configure(targetFps: 30, refreshHz: 60d);
        for (int f = 1; f <= 12; f++)
        {
            Frame(f, 0.020f);
            Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
                "30 FPS healthy frame incorrectly deferred");
        }
        // Android 90 Hz displays pace a 60 FPS request at 45 FPS. A normal
        // 23 ms presentation must not masquerade as a missed 60 FPS frame.
        Configure(targetFps: 60, refreshHz: 90d);
        Frame(1, 0.023f);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "90 Hz display effective 45 FPS cadence ignored");
        Frame(2, 0.026f);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "45 FPS pressure failed to defer import");
        Frame(3, 0.026f);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "45 FPS sustained pressure starved import");
        // Two severe deferrals, then an admitted recovery frame.
        Configure(targetFps: 60, refreshHz: 60d);
        for (int f = 1; f <= 3; f++)
        {
            Frame(f, 0.030f);
            Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() == (f == 3),
                "Severe 60 FPS budget recovery was not bounded");
        }
        // A user quality setting can change FPS without waiting 120 frames.
        Configure(targetFps: 60, refreshHz: 60d);
        Frame(1, 0.020f);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Prechange 60 FPS pressure not detected");
        Application.targetFrameRate = 30;
        Frame(2, 0.020f);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "FPS target change retained stale frame budget");
        // OS refresh-rate changes are resampled at a bounded cadence.
        Configure(targetFps: 60, refreshHz: 90d);
        Frame(1, 0.020f);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Expected healthy 45 FPS admission");
        Screen.resolution = new Resolution {
            refreshRateRatio = new RefreshRate { value = 60d }
        };
        Frame(121, 0.020f);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Refresh-rate change was not resampled");
        Check(Screen.reads == 2, "Display refresh polling cost is unbounded");
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
            if (gpu == "Adreno 610")
            {
                Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() &&
                    Load() == null, "Constrained GPU overlapped post-cleanup native settle");
                Frame(4);
            }
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
            if (gpu == "Adreno 610")
            {
                Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() &&
                    Load() == null,
                    "Already-complete cleanup lost constrained next-frame protection");
                Frame(14);
            }
            Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
                "Already-complete cleanup held work beyond the settle frame");
        }
        Configure(6144);
        Check(AarisMobileStreamingFrameGate.ReserveNextFrameForEssentialRig(), "6 GB rig unprotected");
        Frame(2);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() && Load() == null,
            "Optional work overlapped reserved rig frame");
        Frame(3);
        Check(Load() != null, "Reservation did not expire");
        Configure(6144);
        Check(AarisMobileStreamingFrameGate.ReserveNextFrameForEssentialAircraft(),
            "Constrained aircraft frame not protected");
        // Reservation affects only the following frame: loading/updating the
        // aircraft remains essential and is not queued behind optional models.
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Aircraft reservation blocked its own preparation frame");
        Frame(2);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() && Load() == null,
            "Optional model overlapped high-detail aircraft Instantiate");
        Check(Resources.requests == 0, "Aircraft frame dispatched a native optional import");
        Frame(3);
        Check(Load() != null, "Aircraft reservation starved future models");
        // When a second essential system reserves the next frame during a
        // first reservation, both fences must survive independently.
        Configure(6144);
        Check(AarisMobileStreamingFrameGate.ReserveNextFrameForEssentialRig(),
            "Character rig reservation missing");
        Frame(2);
        Check(AarisMobileStreamingFrameGate.ReserveNextFrameForEssentialAircraft(),
            "Overlapping aircraft reservation missing");
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Aircraft overwrote current character-rig reservation");
        Frame(3);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Character-rig reservation overwrote aircraft reservation");
        Frame(4);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Consecutive essential frames kept optional admission blocked");
        // Two consecutive requests of the SAME essential kind are not
        // independent unless the already-due current frame is retained.
        // Old single-slot logic overwrote frame 41 with frame 42 here.
        Configure(6144, "Adreno 610");
        Frame(40);
        Check(AarisMobileStreamingFrameGate.ReserveNextFrameForEssentialRig(),
            "First rig reservation missing");
        Frame(41);
        Check(AarisMobileStreamingFrameGate.ReserveNextFrameForEssentialRig(),
            "Consecutive rig reservation missing");
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() &&
              !AarisMobileStreamingFrameGate.TryClaimNonessentialDiagnosticFrame(),
            "Second rig reservation erased current-frame rig protection");
        Frame(42);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() && Load() == null,
            "Consecutive rig frame admitted optional native import");
        Frame(43);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Rig overlap protection never expired");

        // Aircraft can also be restarted/respawned across adjacent frames;
        // duplicate same-frame calls must not lose the previous slot.
        Configure(6144, "Adreno 610");
        Frame(60);
        Check(AarisMobileStreamingFrameGate.ReserveNextFrameForEssentialAircraft(),
            "First aircraft reservation missing");
        Check(AarisMobileStreamingFrameGate.ReserveNextFrameForEssentialAircraft(),
            "Duplicate aircraft reservation failed");
        Frame(61);
        Check(AarisMobileStreamingFrameGate.ReserveNextFrameForEssentialAircraft(),
            "Second aircraft reservation missing");
        Check(AarisMobileStreamingFrameGate.ReserveNextFrameForEssentialAircraft(),
            "Duplicate follow-up aircraft reservation failed");
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Aircraft current-frame fence erased by its next-frame reservation");
        Frame(62);
        Check(!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild() && Load() == null,
            "Overlapping aircraft frame admitted optional native work");
        Frame(63);
        Check(AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild(),
            "Aircraft overlap fence blocked later visual streaming");

        Configure(8192, "Adreno 730");
        Check(!AarisMobileStreamingFrameGate.ReserveNextFrameForEssentialRig(), "Fast rig delayed");
        Check(!AarisMobileStreamingFrameGate.ReserveNextFrameForEssentialAircraft(),
            "Fast aircraft unnecessarily reserved a frame");
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
        Hardware(); ImportFence(); EssentialAudioFence(); AdditiveSceneFence(); DiagnosticImportFence(); EssentialWeaponFence(); EssentialLootAndJsonParseFence(); Pressure(); RefreshAwarePressure(); PhysicsCatchupAdmission(); CleanupRigAndReset(); LootTeardownFence();
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
