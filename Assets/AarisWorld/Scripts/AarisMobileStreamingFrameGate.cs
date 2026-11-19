using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// Coordinates optional 3D model integration across independent streamers.
    /// Per-streamer budgets alone can still create several heavy hierarchies in
    /// the same rendered frame. This adds one shared low-RAM Android admission
    /// slot; essential gameplay and existing collision/visibility stay live.
    /// </summary>
    internal static class AarisMobileStreamingFrameGate
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        private static int claimedFrame = -1;
        // One shared native import slot across optional model prewarmers.
        // Completed requests release the slot even when a coroutine stops.
        private static ResourceRequest optionalLoadInFlight;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            claimedFrame = -1;
            optionalLoadInFlight = null;
        }
#endif

        internal static bool TryClaimOptionalModelBuild()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Unknown memory or larger phones retain the authored per-system
            // budgets. Read only when an actual model build is imminent.
            int ramMb = SystemInfo.systemMemorySize;
            if (ramMb > 0 && ramMb <= 4608)
            {
                int frame = Time.frameCount;
                if (claimedFrame == frame) return false;
                claimedFrame = frame;
            }
#endif
            return true;
        }

        // Native terrain, tree collision and essential loot loading are never
        // delayed. Only optional 3D model/clip prewarming shares this budget.
        internal static ResourceRequest TryStartOptionalLoadAsync<T>(string path)
            where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(path)) return null;
#if UNITY_ANDROID && !UNITY_EDITOR
            int ramMb = SystemInfo.systemMemorySize;
            bool constrained = ramMb > 0 && ramMb <= 4608;
            if (constrained)
            {
                if (optionalLoadInFlight != null && !optionalLoadInFlight.isDone)
                    return null;
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
