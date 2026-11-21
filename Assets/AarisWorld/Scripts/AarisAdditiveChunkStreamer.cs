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
        // Sparse resident roster: the cold, nonresident world costs O(1) to
        // reconcile, rather than scanning every authored cell each poll.
        // Slot + 1 uses zero as the untracked sentinel (no HashSet/GC).
        private int[] trackedResidentIndices, trackedResidentSlots;
        private int trackedResidentCount, nextReconcileIndex;
        private Vector3 previousSample;
        private float previousSampleAt, nextPollAt, nextTransitionAt;

        private void Awake()
        {
            if (chunks == null || chunks.Length == 0) return;
            states = new State[chunks.Length];
            trackedResidentIndices = new int[chunks.Length];
            trackedResidentSlots = new int[chunks.Length];
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
                if (states[i].externallyOwned) TrackResident(i);
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
                    if (states[finished].loadedByThisStreamer) TrackResident(finished);
                    else ForgetResident(finished);
                }
                else
                {
                    states[finished].loadedByThisStreamer = false;
                    ForgetResident(finished);
                }
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

        // Track only loaded/external cells, not all authored cells. The
        // roster is allocated once in Awake and uses swap-remove without GC.
        private void TrackResident(int index)
        {
            if (trackedResidentSlots[index] != 0) return;
            trackedResidentIndices[trackedResidentCount] = index;
            trackedResidentSlots[index] = ++trackedResidentCount;
        }

        private void ForgetResident(int index)
        {
            int slot = trackedResidentSlots[index] - 1;
            if (slot < 0) return;
            int last = trackedResidentIndices[--trackedResidentCount];
            trackedResidentIndices[slot] = last;
            trackedResidentSlots[last] = slot + 1;
            trackedResidentSlots[index] = 0;
            // Deleting the just-checked slot swaps a new index into it.
            // Revisit that slot on the next poll rather than skipping it.
            if (slot < nextReconcileIndex) nextReconcileIndex--;
            if (nextReconcileIndex >= trackedResidentCount) nextReconcileIndex = 0;
        }

        // Another manager may retire a scene we previously owned. Recheck
        // exactly one resident scene per poll, including externally owned
        // cells, so it can reload immediately when the player is nearby.
        // With zero resident chunks this is O(1), even for huge world grids.
        private void ReconcileOneLoadedChunk()
        {
            if (trackedResidentCount == 0) return;
            if (nextReconcileIndex >= trackedResidentCount) nextReconcileIndex = 0;
            int index = trackedResidentIndices[nextReconcileIndex++];
            if (nextReconcileIndex >= trackedResidentCount) nextReconcileIndex = 0;
            if (!states[index].valid)
            {
                ForgetResident(index);
                return;
            }
            Scene scene = SceneManager.GetSceneByPath(chunks[index].scenePath);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                states[index].externallyOwned = false;
                states[index].loadedByThisStreamer = false;
                ForgetResident(index);
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
                TrackResident(index);
                return;
            }
            if (!load && (!existing.IsValid() || !existing.isLoaded))
            {
                states[index].loadedByThisStreamer = false;
                ForgetResident(index);
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
#if UNITY_ANDROID && !UNITY_EDITOR
                // Scene integration may finish on a later render frame. Keep
                // unrelated optional imports/diagnostics off this native work
                // without pausing allowSceneActivation or Unity's IO queue.
                if (load) AarisMobileStreamingFrameGate.TrackAdditiveSceneLoad(operation);
#endif
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
