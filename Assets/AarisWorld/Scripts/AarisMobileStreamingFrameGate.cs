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

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            claimedFrame = -1;
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
    }
}
