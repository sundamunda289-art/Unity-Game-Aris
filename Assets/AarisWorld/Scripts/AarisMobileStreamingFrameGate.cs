using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// Coordinates optional 3D model and nonessential Foley audio imports across streamers.
    /// Per-streamer budgets alone can still create several heavy hierarchies in
    /// the same rendered frame. This adds one shared low-RAM Android admission
    /// slot; essential gameplay and existing collision/visibility stay live.
    /// </summary>
    internal static class AarisMobileStreamingFrameGate
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        private static int claimedFrame = -1;
        // Essential player rig construction is one high-cost main-thread
        // operation (Instantiate + PlayableGraph + validation BakeMesh).
        // Reserve its NEXT frame before yielding; optional models/materials
        // must not begin another import or hierarchy build on that frame.
        private static int essentialRigFrame = -1;
        internal static int RigReservedDeferredAttempts { get; private set; }
        // One shared native import slot across optional model prewarmers.
        // Completed requests release the slot even when a coroutine stops.
        private static ResourceRequest optionalLoadInFlight;
        // Unity's critical-memory asset cleanup can run for many rendered
        // frames. Starting optional imports/hierarchies while it is still
        // unloading competes for native memory and the same main-thread time.
        // The cleanup itself is never blocked; essential scene/gameplay work
        // never enters this optional admission gate.
        private static AsyncOperation unusedAssetCleanup;
        // Monotonic attempted-admission counters. Reading three ints only
        // during existing 15-second diagnostics does not allocate, poll native
        // hierarchies, or add logging to the model-streaming hot path.
        internal static int CleanupDeferredAttempts { get; private set; }
        internal static int SameFrameDeferredAttempts { get; private set; }
        internal static int ImportDeferredAttempts { get; private set; }

        internal static void DeferOptionalWorkDuringAssetCleanup(AsyncOperation cleanup)
        {
            if (cleanup != null && !cleanup.isDone)
                unusedAssetCleanup = cleanup;
        }

        private static bool IsAssetCleanupPending()
        {
            if (unusedAssetCleanup == null) return false;
            if (!unusedAssetCleanup.isDone) return true;
            // Do not hold a completed Unity operation or block work forever.
            unusedAssetCleanup = null;
            return false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            claimedFrame = -1;
            essentialRigFrame = -1;
            RigReservedDeferredAttempts = 0;
            optionalLoadInFlight = null;
            unusedAssetCleanup = null;
            CleanupDeferredAttempts = SameFrameDeferredAttempts = ImportDeferredAttempts = 0;
        }
#endif

        // Called by the essential player visual AFTER animation imports,
        // immediately before its final one-frame yield. False on larger RAM
        // devices retains their current full-speed behavior.
        internal static bool ReserveNextFrameForEssentialRig()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            int ramMb = SystemInfo.systemMemorySize;
            if (ramMb > 0 && ramMb <= 4608)
            {
                essentialRigFrame = Time.frameCount + 1;
                return true;
            }
#endif
            return false;
        }

        private static bool IsEssentialRigFrame()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return Time.frameCount == essentialRigFrame;
#else
            return false;
#endif
        }

        internal static bool TryClaimOptionalModelBuild()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (IsEssentialRigFrame())
            {
                RigReservedDeferredAttempts++;
                return false;
            }
            // Even a larger phone can report critical memory: keep all optional
            // native hierarchy construction off the unused-asset cleanup pass.
            if (IsAssetCleanupPending())
            {
                CleanupDeferredAttempts++;
                return false;
            }
            // Unknown memory or larger phones retain the authored per-system
            // budgets. Read only when an actual model build is imminent.
            int ramMb = SystemInfo.systemMemorySize;
            if (ramMb > 0 && ramMb <= 4608)
            {
                int frame = Time.frameCount;
                if (claimedFrame == frame)
                {
                    SameFrameDeferredAttempts++;
                    return false;
                }
                claimedFrame = frame;
            }
#endif
            return true;
        }

        // Native terrain, tree collision, essential loot and first-use sound
        // cues are never delayed. Optional 3D models, clips and Foley variants
        // share the same admission budget on constrained Android hardware.
        internal static ResourceRequest TryStartOptionalLoadAsync<T>(string path)
            where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(path)) return null;
#if UNITY_ANDROID && !UNITY_EDITOR
            int ramMb = SystemInfo.systemMemorySize;
            bool constrained = ramMb > 0 && ramMb <= 4608;
            if (IsAssetCleanupPending())
            {
                CleanupDeferredAttempts++;
                return null;
            }
            if (IsEssentialRigFrame())
            {
                RigReservedDeferredAttempts++;
                return null;
            }
            if (constrained)
            {
                if (optionalLoadInFlight != null && !optionalLoadInFlight.isDone)
                {
                    ImportDeferredAttempts++;
                    return null;
                }
                // Model imports and optional hierarchy/material integration
                // must not both be admitted in the same low-RAM frame.
                if (!TryClaimOptionalModelBuild()) return null;
            }
#endif
            ResourceRequest request = Resources.LoadAsync<T>(path);
            request.priority = -1;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (constrained) optionalLoadInFlight = request;
#endif
            return request;
        }
    }
}
