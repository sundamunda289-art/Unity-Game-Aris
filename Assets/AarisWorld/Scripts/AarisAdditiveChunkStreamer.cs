using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Aaris.MapReview
{
    /// <summary>
    /// Opt-in additive scene streaming for artist-authored world cells.
    /// Does nothing until attached to a persistent bootstrap scene and supplied
    /// with scenes that already exist in the build profile. It never changes
    /// terrain, materials, distant silhouettes, trees or collision parameters.
    /// Keep distant silhouette proxies in the bootstrap scene, not in chunks.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AarisAdditiveChunkStreamer : MonoBehaviour
    {
        [Serializable]
        public sealed class Chunk
        {
            public string scenePath;
            public Vector2 center;
            public Vector2 halfExtents = new Vector2(128f, 128f);
        }

        [SerializeField] private Transform player;
        [SerializeField] private Chunk[] chunks = new Chunk[0];
        [SerializeField, Min(0f)] private float loadMargin = 90f;
        [SerializeField, Min(0f)] private float unloadMargin = 170f;
        [SerializeField, Min(0f)] private float lookaheadSeconds = 1.25f;
        [SerializeField, Min(1f)] private float maxPredictedSpeed = 35f;
        [SerializeField, Min(1f)] private float teleportRejectSpeed = 90f;
        [SerializeField, Min(0.05f)] private float pollSeconds = 0.25f;
        [SerializeField, Min(0f)] private float transitionCooldownSeconds = 0.25f;
        // Only speculative, not currently needed, scenes are capped on budget Android.
        // The near/visible authored cells and their bootstrap silhouette proxies stay intact.
        [SerializeField, Min(0f)] private int maxSpeculativeResidentCells = 2;

        private struct State
        {
            internal bool valid;
            internal bool externallyOwned;
            internal bool loadedByThisStreamer;
        }

        private State[] states;
        private AsyncOperation inFlight;
        private int inFlightCell = -1;
        private bool inFlightLoad;
        private bool previousSampleValid;
        // Round-robin check of one resident scene per poll; other scene
        // managers can retire an authored cell after this streamer loaded it.
        private int nextReconcileIndex;
        private Vector3 previousSample;
        private float previousSampleAt, nextPollAt, nextTransitionAt;

        private void Awake()
        {
            if (chunks == null || chunks.Length == 0) return;
            states = new State[chunks.Length];
            string hostPath = gameObject.scene.path;
            for (int i = 0; i < chunks.Length; i++)
            {
                Chunk cell = chunks[i];
                if (cell == null || string.IsNullOrEmpty(cell.scenePath) ||
                    cell.scenePath == hostPath ||
                    cell.halfExtents.x <= 0f || cell.halfExtents.y <= 0f ||
                    float.IsNaN(cell.center.x) || float.IsNaN(cell.center.y) ||
                    float.IsInfinity(cell.center.x) || float.IsInfinity(cell.center.y) ||
                    float.IsNaN(cell.halfExtents.x) || float.IsNaN(cell.halfExtents.y) ||
                    float.IsInfinity(cell.halfExtents.x) || float.IsInfinity(cell.halfExtents.y) ||
                    SceneUtility.GetBuildIndexByScenePath(cell.scenePath) < 0)
                {
                    Debug.LogWarning("AARIS_CHUNK_INVALID: chunk must be a separate built scene");
                    continue;
                }
                bool duplicate = false;
                for (int j = 0; j < i; j++)
                    if (chunks[j] != null && chunks[j].scenePath == cell.scenePath)
                    { duplicate = true; break; }
                if (duplicate)
                {
                    Debug.LogWarning("AARIS_CHUNK_DUPLICATE: " + cell.scenePath);
                    continue;
                }
                states[i].valid = true;
                Scene preloaded = SceneManager.GetSceneByPath(cell.scenePath);
                states[i].externallyOwned = preloaded.IsValid() && preloaded.isLoaded;
            }
        }

        private void OnEnable() { nextPollAt = 0f; previousSampleValid = false; nextReconcileIndex = 0; }

        private void Update()
        {
            if (states == null || player == null) return;
            float now = Time.unscaledTime;
            if (inFlight != null)
            {
                if (!inFlight.isDone) return;
                // Observe completion on a different rendered frame: don't
                // start a second scene integration/retirement on that frame.
                int finished = inFlightCell;
                if (inFlightLoad)
                {
                    Scene scene = SceneManager.GetSceneByPath(chunks[finished].scenePath);
                    states[finished].loadedByThisStreamer = scene.IsValid() && scene.isLoaded;
                }
                else states[finished].loadedByThisStreamer = false;
#if UNITY_ANDROID && !UNITY_EDITOR
                // Native renderer/physics integration and scene retirement can finish
                // on this frame. Keep later optional builders from stacking onto it.
                if (inFlightLoad)
                    AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();
                else
                    AarisMobileStreamingFrameGate.ReserveNativeTeardownFrame();
#endif
                inFlight = null;
                inFlightCell = -1;
                nextTransitionAt = now + transitionCooldownSeconds;
                return;
            }
            if (now < nextPollAt) return;
            nextPollAt = now + Mathf.Max(0.05f, pollSeconds);
            ReconcileOneLoadedChunk();

            Vector3 current = player.position;
            float projectedX = current.x, projectedZ = current.z;
            if (previousSampleValid)
                AarisChunkStreamingPolicy.Predict(current.x, current.z,
                    previousSample.x, previousSample.z, now - previousSampleAt,
                    lookaheadSeconds, maxPredictedSpeed, teleportRejectSpeed,
                    out projectedX, out projectedZ);
            previousSample = current;
            previousSampleAt = now;
            previousSampleValid = true;
            if (now < nextTransitionAt) return;

            float entrySquared = loadMargin * loadMargin;
            float exitMargin = Mathf.Max(unloadMargin, loadMargin + 1f);
            float exitSquared = exitMargin * exitMargin;
            int loadCell = -1, unloadCell = -1;
            int speculativeResidentCount = 0;
            // Numeric priority can tie the entry threshold when predicted position
            // is already inside a distant cell. Urgency must use actual position.
            bool loadIsUrgent = false;
            float nearestLoad = float.MaxValue, farthestUnload = -1f;
            for (int i = 0; i < chunks.Length; i++)
            {
                if (!states[i].valid || states[i].externallyOwned) continue;
                Chunk cell = chunks[i];
                float distance = AarisChunkStreamingPolicy.OutsideDistanceSquared(
                    current.x, current.z, cell.center.x, cell.center.y,
                    cell.halfExtents.x, cell.halfExtents.y);
                if (states[i].loadedByThisStreamer)
                {
                    // Already-resident cells beyond the urgent margin consume
                    // speculative headroom until movement or normal retirement.
                    if (distance > entrySquared && distance <= exitSquared)
                        speculativeResidentCount++;
                    // Always retain on *actual* position, including teleport.
                    // A wider exit boundary prevents load/unload oscillation.
                    if (distance > exitSquared && distance > farthestUnload)
                    { unloadCell = i; farthestUnload = distance; }
                    continue;
                }
                float predicted = AarisChunkStreamingPolicy.OutsideDistanceSquared(
                    projectedX, projectedZ, cell.center.x, cell.center.y,
                    cell.halfExtents.x, cell.halfExtents.y);
                if (distance > exitSquared ||
                    (distance > entrySquared && predicted > entrySquared)) continue;
                // Urgent current-position cells outrank speculative prefetch.
                float priority = distance <= entrySquared
                    ? distance : entrySquared + predicted;
                if (priority < nearestLoad)
                {
                    nearestLoad = priority;
                    loadCell = i;
                    loadIsUrgent = distance <= entrySquared;
                }
            }
            // One integration operation in flight across the *entire* world.
            // Essential visible cells win over memory retirement; distant
            // proxy scenery lives in the persistent scene during transitions.
            // Retire a distant owned cell before a merely speculative
            // prefetch. Otherwise rapid traversal can stack old resident
            // scenes with new predicted ones and spike Android RAM. Cells
            // needed at the player's actual position always load first.
            if (unloadCell >= 0 && (loadCell < 0 || !loadIsUrgent))
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                AarisMobileStreamingFrameGate.ReserveNativeTeardownFrame();
#endif
                StartOperation(unloadCell, false);
            }
            else if (loadCell >= 0)
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                // Bound predicted-only residency on constrained devices. A cell
                // inside the player's real load margin ALWAYS bypasses this cap.
                if (!loadIsUrgent &&
                    AarisMobileStreamingFrameGate.IsConstrainedDevice() &&
                    speculativeResidentCount >= maxSpeculativeResidentCells)
                    return;
                if (!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild()) return;
#endif
                StartOperation(loadCell, true);
            }
        }

        // Another subsystem may retire a scene we previously loaded, or an
        // externally owned scene after Awake. Without reconciliation the stale
        // ownership flag can strand visible world cells permanently unloaded.
        // At most ONE native scene lookup per poll; do not scan every scene
        // through SceneManager or allocate a collection in the frame loop.
        private void ReconcileOneLoadedChunk()
        {
            for (int scanned = 0; scanned < states.Length; scanned++)
            {
                int index = nextReconcileIndex;
                nextReconcileIndex = index + 1 == states.Length ? 0 : index + 1;
                if (!states[index].valid ||
                    (!states[index].externallyOwned && !states[index].loadedByThisStreamer))
                    continue;

                Scene scene = SceneManager.GetSceneByPath(chunks[index].scenePath);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    states[index].externallyOwned = false;
                    states[index].loadedByThisStreamer = false;
                }
                return;
            }
        }

        private void StartOperation(int index, bool load)
        {
            string path = chunks[index].scenePath;
            // Recheck only at an actual transition, not once per cell on
            // every poll. Another manager may have loaded this scene after
            // our Awake, or retired one that we previously owned.
            Scene existing = SceneManager.GetSceneByPath(path);
            if (load && existing.IsValid() && existing.isLoaded)
            {
                states[index].externallyOwned = true;
                return;
            }
            if (!load && (!existing.IsValid() || !existing.isLoaded))
            {
                states[index].loadedByThisStreamer = false;
                return;
            }
            try
            {
                AsyncOperation operation = load
                    ? SceneManager.LoadSceneAsync(path, LoadSceneMode.Additive)
                    : SceneManager.UnloadSceneAsync(path);
                if (operation == null)
                {
                    Debug.LogWarning("AARIS_CHUNK_OPERATION_UNAVAILABLE: " + path);
                    states[index].valid = false;
                    return;
                }
                operation.priority = -1;
                inFlight = operation;
                inFlightCell = index;
                inFlightLoad = load;
            }
            catch (Exception ex)
            {
                // Disable a bad cell rather than retrying its exception each poll.
                states[index].valid = false;
                Debug.LogWarning("AARIS_CHUNK_OPERATION_FAILED: " + path + " " + ex.Message);
            }
        }
    }
}
