#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Enables Unity's Android frame pacing for every player build path.
    /// This smooths presentation cadence without changing render scale,
    /// authored geometry, textures, foliage or shadow fidelity.
    /// It is not a claim of higher throughput or a replacement for profiling.
    /// </summary>
    internal sealed class AarisAndroidFramePacingBuild : IPreprocessBuildWithReport
    {
        public int callbackOrder { get { return -1000; } }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android)
                return;
            if (!PlayerSettings.Android.optimizedFramePacing)
                PlayerSettings.Android.optimizedFramePacing = true;
        }
    }
}
#endif
