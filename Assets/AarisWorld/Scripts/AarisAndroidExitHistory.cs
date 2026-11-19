using System;
#if UNITY_ANDROID && !UNITY_EDITOR
using System.Threading.Tasks;
using UnityEngine;
#endif

namespace Aaris.MapReview
{
    /// <summary>
    /// One-shot, non-blocking Android 11+ process-exit evidence.
    /// Reports the previous process exit, NOT the current process RAM usage.
    /// No Android service calls run in Update or on the Unity main thread.
    /// </summary>
    internal static class AarisAndroidExitHistory
    {
        internal struct Snapshot
        {
            internal string state;
            internal int reasonCode, statusCode;
            internal long timestampUtcMs, historicalPssKB, historicalRssKB;

            internal static Snapshot Unknown(string state)
            {
                return new Snapshot {
                    state = state, reasonCode = -1, statusCode = -1,
                    timestampUtcMs = -1, historicalPssKB = -1, historicalRssKB = -1
                };
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        internal static Task<Snapshot> QueryAsync()
        {
            // Unity-owned activity reference is acquired on the Unity thread.
            // All potentially slow Android binder/JNI work happens on a worker.
            AndroidJavaObject activity;
            try { activity = UnityEngine.Android.AndroidApplication.currentActivity; }
            catch (Exception) { return Task.FromResult(Snapshot.Unknown("activity-unavailable")); }
            if (activity == null)
                return Task.FromResult(Snapshot.Unknown("activity-unavailable"));
            return Task.Run(() => QueryOnAttachedWorker(activity));
        }

        private static Snapshot QueryOnAttachedWorker(AndroidJavaObject activity)
        {
            Snapshot result = Snapshot.Unknown("unavailable");
            bool attached = false;
            try
            {
                // Unity returns a negative code if the worker could not
                // attach; never issue Java calls on an unattached thread.
                if (AndroidJNI.AttachCurrentThread() < 0)
                    return Snapshot.Unknown("jni-attach-failed");
                attached = true;
                using (var sdk = new AndroidJavaClass("android.os.Build$VERSION"))
                {
                    if (sdk.GetStatic<int>("SDK_INT") < 30)
                        return Snapshot.Unknown("unsupported-api");
                }

                // Same-package process history only; no DUMP permission,
                // package enumeration, stack traces, or sensitive process data.
                string packageName = activity.Call<string>("getPackageName");
                if (string.IsNullOrEmpty(packageName))
                    return Snapshot.Unknown("package-unavailable");

                using (var manager = activity.Call<AndroidJavaObject>(
                    "getSystemService", "activity"))
                {
                    if (manager == null)
                        return Snapshot.Unknown("manager-unavailable");
                    using (var exits = manager.Call<AndroidJavaObject>(
                        "getHistoricalProcessExitReasons", packageName, 0, 1))
                    {
                        if (exits == null || exits.Call<int>("size") <= 0)
                            return Snapshot.Unknown("no-history");
                        using (var info = exits.Call<AndroidJavaObject>("get", 0))
                        {
                            if (info == null) return Snapshot.Unknown("no-history");
                            result.state = "available";
                            result.reasonCode = info.Call<int>("getReason");
                            result.statusCode = info.Call<int>("getStatus");
                            result.timestampUtcMs = info.Call<long>("getTimestamp");
                            // Android reports 0 when no historical memory
                            // sample existed, NOT a zero-memory process.
                            // Keep unknown memory distinct from this game's live PSS.
                            long lastPss = info.Call<long>("getPss");
                            long lastRss = info.Call<long>("getRss");
                            result.historicalPssKB = lastPss > 0 ? lastPss : -1L;
                            result.historicalRssKB = lastRss > 0 ? lastRss : -1L;
                        }
                    }
                }
            }
            catch (AndroidJavaException) { result = Snapshot.Unknown("jni-error"); }
            catch (Exception) { result = Snapshot.Unknown("query-error"); }
            finally
            {
                if (attached) AndroidJNI.DetachCurrentThread();
            }
            return result;
        }
#endif
    }
}
