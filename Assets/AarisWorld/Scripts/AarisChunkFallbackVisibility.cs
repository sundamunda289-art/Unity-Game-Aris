using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Aaris.MapReview
{
    /// <summary>
    /// Preserves the authored last-LOD silhouette while its full-detail
    /// scene is not resident. The original collider stays in the persistent
    /// world on a separately indexed, mesh-identical physics proxy.
    /// No per-frame polling, reflection, allocation or quality downgrade.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AarisChunkFallbackVisibility : MonoBehaviour
    {
        [SerializeField] private string chunkScenePath;
        [SerializeField] private Renderer[] silhouetteRenderers = new Renderer[0];
        private static readonly Dictionary<string, ChunkVisuals> chunks =
            new Dictionary<string, ChunkVisuals>(StringComparer.Ordinal);
        private static readonly Dictionary<int, ChunkVisuals> loadedScenes =
            new Dictionary<int, ChunkVisuals>();
        private ChunkVisuals registeredChunk;

        private sealed class ChunkVisuals
        {
            internal readonly string Path;
            internal readonly List<AarisChunkFallbackVisibility> Members =
                new List<AarisChunkFallbackVisibility>();
            internal readonly List<int> LoadedHandles = new List<int>(1);

            internal ChunkVisuals(string path) { Path = path; }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRegistry()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneUnloaded -= HandleSceneUnloaded;
            chunks.Clear();
            loadedScenes.Clear();
        }

        private void OnEnable()
        {
            if (string.IsNullOrEmpty(chunkScenePath))
            {
                ShowSilhouette(true);
                return;
            }
            if (chunks.Count == 0)
            {
                SceneManager.sceneLoaded += HandleSceneLoaded;
                SceneManager.sceneUnloaded += HandleSceneUnloaded;
            }
            if (!chunks.TryGetValue(chunkScenePath, out registeredChunk))
            {
                registeredChunk = new ChunkVisuals(chunkScenePath);
                chunks.Add(chunkScenePath, registeredChunk);
                // A manager can be enabled after one or more instances of its
                // detail scene. Seed only on the first registration per path.
                int sceneCount = SceneManager.sceneCount;
                for (int i = 0; i < sceneCount; i++)
                {
                    Scene scene = SceneManager.GetSceneAt(i);
                    if (scene.isLoaded && scene.path == chunkScenePath)
                        TrackLoadedScene(registeredChunk, scene.handle);
                }
            }
            registeredChunk.Members.Add(this);
            ShowSilhouette(registeredChunk.LoadedHandles.Count == 0);
        }

        private void OnDisable()
        {
            if (registeredChunk != null)
            {
                registeredChunk.Members.Remove(this);
                if (registeredChunk.Members.Count == 0)
                {
                    foreach (int handle in registeredChunk.LoadedHandles)
                        loadedScenes.Remove(handle);
                    chunks.Remove(registeredChunk.Path);
                    if (chunks.Count == 0)
                    {
                        SceneManager.sceneLoaded -= HandleSceneLoaded;
                        SceneManager.sceneUnloaded -= HandleSceneUnloaded;
                    }
                }
                registeredChunk = null;
            }
            // A disabled manager releases its part of the distant skyline.
            ShowSilhouette(true);
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            string path = scene.path;
            if (!string.IsNullOrEmpty(path) &&
                chunks.TryGetValue(path, out ChunkVisuals chunk))
                TrackLoadedScene(chunk, scene.handle);
        }

        private static void HandleSceneUnloaded(Scene scene)
        {
            // Unity can invalidate the path before it sends sceneUnloaded.
            // The event handle still identifies the exact loaded instance.
            int handle = scene.handle;
            if (!loadedScenes.TryGetValue(handle, out ChunkVisuals chunk)) return;
            loadedScenes.Remove(handle);
            chunk.LoadedHandles.Remove(handle);
            if (chunk.LoadedHandles.Count == 0)
                foreach (AarisChunkFallbackVisibility member in chunk.Members)
                    member.ShowSilhouette(true);
        }

        private static void TrackLoadedScene(ChunkVisuals chunk, int handle)
        {
            if (loadedScenes.ContainsKey(handle)) return;
            loadedScenes.Add(handle, chunk);
            chunk.LoadedHandles.Add(handle);
            if (chunk.LoadedHandles.Count == 1)
                foreach (AarisChunkFallbackVisibility member in chunk.Members)
                    member.ShowSilhouette(false);
        }

        private void ShowSilhouette(bool visible)
        {
            if (silhouetteRenderers == null) return;
            for (int i = 0; i < silhouetteRenderers.Length; i++)
            {
                Renderer renderer = silhouetteRenderers[i];
                if (renderer != null && renderer.enabled != visible)
                    renderer.enabled = visible;
            }
        }
    }
}
