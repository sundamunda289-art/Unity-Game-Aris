using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// Shared lookup for the small set of runtime-resolved shaders used by
    /// streamed world systems. A failed lookup is deliberately NOT cached:
    /// optional shaders loaded later by Unity/Resources can still become valid.
    /// </summary>
    internal static class AarisShaderCache
    {
        private static readonly Dictionary<string, Shader> shaders =
            new Dictionary<string, Shader>(16, StringComparer.Ordinal);

        internal static int CachedCount => shaders.Count;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            shaders.Clear();
        }

        internal static Shader Find(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return null;
            if (shaders.TryGetValue(shaderName, out Shader cached) && cached != null)
                return cached;

            Shader resolved = Shader.Find(shaderName);
            if (resolved != null)
                shaders[shaderName] = resolved;
            return resolved;
        }
    }

    /// <summary>
    /// Android-only shader residency scheduling for budget tile-based GPUs.
    /// This never strips variants or changes materials. It only bounds how many
    /// decompressed shader chunks Unity may retain and spreads common Shader
    /// object resolution across otherwise clean rendered frames.
    /// </summary>
    internal sealed class AarisMobileShaderResidency : MonoBehaviour
    {
        private static readonly string[] CommonRuntimeShaders =
        {
            "Universal Render Pipeline/Lit",
            "Universal Render Pipeline/Simple Lit",
            "Universal Render Pipeline/Terrain/Lit",
            "Universal Render Pipeline/Unlit",
            "Universal Render Pipeline/Particles/Unlit",
            "Sprites/Default"
        };

#if UNITY_ANDROID && !UNITY_EDITOR
        private static bool booted;
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            booted = false;
#endif
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void ConfigureEarlyShaderResidency()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // The project's serialized default is unlimited (0). Unity keeps
            // decompressed variant chunks until memory pressure says otherwise.
            // Cap residency before the first scene loads on the same exact
            // budget-GPU classifier already used by streaming/terrain quality.
            if (!AarisMobileRuntime.UseConstrainedGpuStreamingBudget())
                return;

            int ramMB = SystemInfo.systemMemorySize;
            // This policy specifically targets the reported 4-6 GB class.
            // Do not constrain higher-memory devices without measured evidence.
            if (ramMB > 6144) return;
            int desiredChunkCount = ramMB > 0 && ramMB <= 4096 ? 4 : 6;
            int current = Shader.maximumChunksOverride;

            // Preserve an explicitly tighter setting. Otherwise replace
            // unlimited/default inheritance with the bounded mobile residency.
            if (current <= 0 || current > desiredChunkCount)
                Shader.maximumChunksOverride = desiredChunkCount;
#endif
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (booted || !AarisMobileRuntime.UseConstrainedGpuStreamingBudget())
                return;
            booted = true;
            var go = new GameObject("Aaris Mobile Shader Residency");
            DontDestroyOnLoad(go);
            go.AddComponent<AarisMobileShaderResidency>();
#endif
        }

        private IEnumerator Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Opening terrain/character frames are more important than optional
            // lookups. Start only after two rendered frames, then share the
            // global cross-system admission gate one shader at a time.
            yield return null;
            yield return null;

            for (int i = 0; i < CommonRuntimeShaders.Length; i++)
            {
                while (!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())
                    yield return null;
                AarisShaderCache.Find(CommonRuntimeShaders[i]);
                yield return null;
            }
#else
            yield break;
#endif
        }
    }
}
