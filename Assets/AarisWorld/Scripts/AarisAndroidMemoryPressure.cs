using System;
using UnityEngine;

namespace Aaris.MapReview
{
    // Low-frequency Android system-memory signal. This does not inspect user
    // data or allocate per frame; it reuses ActivityManager.MemoryInfo and is
    // sampled only when the mobile runtime asks for a pressure update.
    internal static class AarisAndroidMemoryPressure
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        private const float PollSeconds = 5f;
        private const float RetryAfterFailureSeconds = 30f;
        private const long MinimumSoftHeadroomBytes = 128L * 1024L * 1024L;
        private static AndroidJavaObject activityManager;
        private static AndroidJavaObject memoryInfo;
        private static float nextSampleAt;
        private static bool latestPressure;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            if (activityManager != null) activityManager.Dispose();
            if (memoryInfo != null) memoryInfo.Dispose();
            activityManager = null;
            memoryInfo = null;
            nextSampleAt = 0f;
            latestPressure = false;
        }

        private static bool EnsureJavaObjects()
        {
            if (activityManager != null && memoryInfo != null) return true;
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (AndroidJavaObject activity =
                player.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                if (activity == null) return false;
                activityManager = activity.Call<AndroidJavaObject>(
                    "getSystemService", "activity");
            }
            if (activityManager == null) return false;
            memoryInfo = new AndroidJavaObject("android.app.ActivityManager$MemoryInfo");
            return memoryInfo != null;
        }

        internal static bool TrySample(out bool pressure)
        {
            pressure = latestPressure;
            float now = Time.unscaledTime;
            if (now < nextSampleAt) return false;
            nextSampleAt = now + PollSeconds;
            try
            {
                if (!EnsureJavaObjects())
                {
                    nextSampleAt = now + RetryAfterFailureSeconds;
                    latestPressure = pressure = false;
                    return true;
                }

                activityManager.Call("getMemoryInfo", memoryInfo);
                long available = memoryInfo.Get<long>("availMem");
                long threshold = memoryInfo.Get<long>("threshold");
                bool low = memoryInfo.Get<bool>("lowMemory");

                // Android's threshold is the system's low-memory boundary.
                // Start reclaim slightly before that cliff, never merely
                // because the device happens to use most of its RAM normally.
                long softHeadroom = threshold > 0
                    ? threshold + Math.Max(threshold / 2L, MinimumSoftHeadroomBytes)
                    : 0L;
                latestPressure = low ||
                    (available > 0L && softHeadroom > 0L && available <= softHeadroom);
                pressure = latestPressure;
                return true;
            }
            catch (Exception)
            {
                nextSampleAt = now + RetryAfterFailureSeconds;
                latestPressure = pressure = false;
                return true;
            }
        }
#else
        internal static bool TrySample(out bool pressure)
        {
            pressure = false;
            return false;
        }
#endif
    }
}
