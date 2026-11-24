using System;
using System.Collections.Generic;
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
        // Scene IO latency differs sharply between a warm and cold Android
        // launch. Predict detail arrival from observed load time, but cap
        // speculative reach and never change the actual-position unload rule.
        [SerializeField, Min(0f)] private float maxLoadAwareLeadSeconds = 3.5f;
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
        private AarisChunkSpatialIndex spatialIndex;
        // Build a large world index over multiple quiet frames. Until the last
        // cell is indexed, the original full scan remains authoritative.
        private int nextSpatialBuildIndex;
        private bool spatialReady;
        private float observedLoadSeconds, inFlightStartedAt;
        private AsyncOperation inFlight;
        private int inFlightCell = -1;
        private bool inFlightLoad;
#if UNITY_ANDROID && !UNITY_EDITOR
        // SceneManager operations consume native scene integration/retirement
        // even across different streamer components. One globally shared
        // in-flight operation prevents simultaneous additive scene transitions.
        // No GameObjects, art, terrain, resolution or collider ranges change.
        private static AsyncOperation sharedSceneOperation;
        private static int sharedSceneCompletionFrame = -1;
        private const float CameraWarmHoldSeconds = 1f;
        private const float CameraWarmRetirementBuffer = 20f;
        private int cameraWarmCell = -1;
        private float cameraWarmKeepUntil;
#endif
        private bool previousSampleValid;
        private AarisThirdPersonPlayer playerMotor;
        private Camera previousViewCamera;
        private Vector3 previousViewForward;
        private float previousViewAt;
        private Vector2 sampledVelocity;
        private float nextMovementSampleAt;
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
            if (player != null) playerMotor = player.GetComponent<AarisThirdPersonPlayer>();
            states = new State[chunks.Length];
            trackedResidentIndices = new int[chunks.Length];
            trackedResidentSlots = new int[chunks.Length];
            // Small authored pilots retain the original trivial scan.
            // For a genuine partitioned world, query only cells whose bounds
            // touch the player's actual unload boundary. Never skip a cell
            // because its *predicted* position fell outside the index.
            if (chunks.Length >= 32)
                spatialIndex = new AarisChunkSpatialIndex(chunks.Length);
            string hostPath = gameObject.scene.path;
            // A large authored partition can contain thousands of cells.
            // Preserve the old zero-allocation tiny-scene path, but avoid the
            // quadratic duplicate scan during cold boot of large manifests.
            // Record even invalid earlier cells: the original scan considered
            // all preceding scene paths when rejecting duplicates.
            HashSet<string> seenPaths = chunks.Length >= 16
                ? new HashSet<string>(chunks.Length, StringComparer.Ordinal)
                : null;
            for (int i = 0; i < chunks.Length; i++)
            {
                Chunk cell = chunks[i];
                bool duplicateInIndex = cell != null && seenPaths != null &&
                                        !seenPaths.Add(cell.scenePath);
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
                bool duplicate = duplicateInIndex;
                if (seenPaths == null)
                {
                    for (int j = 0; j < i; j++)
                        if (chunks[j] != null && chunks[j].scenePath == cell.scenePath)
                        { duplicate = true; break; }
                }
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

        private void OnEnable()
        {
            nextPollAt = 0f;
            nextMovementSampleAt = 0f;
            previousSampleValid = false;
            sampledVelocity = default(Vector2);
            previousViewCamera = null;
            nextReconcileIndex = 0;
        }

        private void Update()
        {
            if (states == null || player == null) return;
            float now = Time.unscaledTime;
            SampleMovement(now);
            if (inFlight != null)
            {
                if (!inFlight.isDone) return;
                // Observe completion on a different rendered frame: don't
                // start a second scene integration/retirement on that frame.
                int finished = inFlightCell;
                if (inFlightLoad)
                {
                    float observed = now - inFlightStartedAt;
                    if (observed >= 0.01f && observed < 20f)
                        observedLoadSeconds = observedLoadSeconds <= 0f
                            ? observed : observedLoadSeconds * 0.7f + observed * 0.3f;
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
                {
                    AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();
                    if (finished == cameraWarmCell)
                        cameraWarmKeepUntil = now + CameraWarmHoldSeconds;
                }
                else
                    AarisMobileStreamingFrameGate.ReserveNativeTeardownFrame();
#endif
#if UNITY_ANDROID && !UNITY_EDITOR
                if (ReferenceEquals(sharedSceneOperation, inFlight))
                {
                    sharedSceneOperation = null;
                    sharedSceneCompletionFrame = Time.frameCount;
                }
#endif
                inFlight = null;
                inFlightCell = -1;
                nextTransitionAt = now + transitionCooldownSeconds;
                return;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            // Another chunk manager may be loading or retiring a scene.
            // Observe its completion without native work in the same frame.
            // The owning streamer still reconciles its own in-flight state.
            if (sharedSceneOperation != null)
            {
                if (sharedSceneOperation.isDone)
                {
                    sharedSceneOperation = null;
                    sharedSceneCompletionFrame = Time.frameCount;
                }
                return;
            }
            if (sharedSceneCompletionFrame == Time.frameCount) return;
#endif
            // Spread one-time managed index allocations across render frames,
            // never overlap a pending scene activation or native retirement.
            // Even if a loading screen ends early, full-scan fallback prevents
            // a missing cell while this one-time warmup is still in progress.
            BuildSpatialIndexSlice();
            if (now < nextPollAt) return;
            if (now < nextTransitionAt)
            {
                // Wake at the end of the native cooldown. A check just
                // before it expires must not add another full polling gap.
                nextPollAt = nextTransitionAt;
                return;
            }
            nextPollAt = now + Mathf.Max(0.05f, pollSeconds);
            ReconcileOneLoadedChunk();

            Vector3 current = player.position;
            bool steadyView = TryGetSteadyView(now, out Vector3 viewPosition,
                out Vector2 viewDirection, out float viewSlope);
            float leadSeconds = Mathf.Max(lookaheadSeconds, Math.Min(maxLoadAwareLeadSeconds,
                observedLoadSeconds * 1.5f + 0.25f));
            float stepX = sampledVelocity.x * leadSeconds, stepZ = sampledVelocity.y * leadSeconds;
            // A scheduling poll can land between observation ticks. Guard
            // against applying a velocity from before an intervening teleport.
            float travelLimit = teleportRejectSpeed * (now - previousSampleAt);
            float sinceX = current.x - previousSample.x, sinceZ = current.z - previousSample.z;
            if (!(sinceX * sinceX + sinceZ * sinceZ <= travelLimit * travelLimit))
                stepX = stepZ = 0f;

            float entrySquared = loadMargin * loadMargin;
            float exitMargin = Mathf.Max(unloadMargin, loadMargin + 1f);
            float exitSquared = exitMargin * exitMargin;
            int loadCell = -1, unloadCell = -1;
            int viewCell = -1;
            int speculativeResidentCount = 0;
#if UNITY_ANDROID && !UNITY_EDITOR
            bool cameraWarmCanYield = false;
            float cameraRetirementMargin = loadMargin + CameraWarmRetirementBuffer;
#endif
            bool loadIsUrgent = false;
            float nearestLoad = float.MaxValue, farthestUnload = -1f;
            float nearestView = float.MaxValue;
            // Residency retirement is a sparse pass, including distant owned
            // cells that are OUTSIDE any near-player query. This is essential:
            // querying only near bins for unload would leak memory on traversal.
            for (int slot = 0; slot < trackedResidentCount; slot++)
            {
                int i = trackedResidentIndices[slot];
                if (!states[i].valid || states[i].externallyOwned ||
                    !states[i].loadedByThisStreamer) continue;
                Chunk cell = chunks[i];
                float distance = AarisChunkStreamingPolicy.OutsideDistanceSquared(
                    current.x, current.z, cell.center.x, cell.center.y,
                    cell.halfExtents.x, cell.halfExtents.y);
                if (distance > entrySquared && distance <= exitSquared)
                    speculativeResidentCount++;
#if UNITY_ANDROID && !UNITY_EDITOR
                if (i == cameraWarmCell)
                {
                    // Once it has served as required detail it gets the same
                    // retention as every other district the player visited.
                    if (distance <= entrySquared) cameraWarmCell = -1;
                    else if (distance <= exitSquared)
                    {
                        bool keepWarm = distance <= cameraRetirementMargin * cameraRetirementMargin ||
                            !steadyView || CameraWarmMayBeVisible(cell, viewPosition, viewDirection, viewSlope);
                        if (keepWarm) cameraWarmKeepUntil = now + CameraWarmHoldSeconds;
                        else cameraWarmCanYield = now >= cameraWarmKeepUntil;
                    }
                }
#endif
                if (distance > exitSquared &&
                    (distance > farthestUnload ||
                     (distance == farthestUnload && i < unloadCell)))
                { unloadCell = i; farthestUnload = distance; }
            }

            // Use one spare speculative slot for the view only when the band
            // has no owned detail already. Leave the other slot for movement.
            bool considerView = steadyView && maxSpeculativeResidentCells >= 2 &&
                                speculativeResidentCount == 0;
            float prefetchSlope = Mathf.Min(0.60f, viewSlope);
#if UNITY_ANDROID && !UNITY_EDITOR
            considerView &= !AarisAndroidThermalGovernor.ModerateThermalPressure &&
                            !AarisAndroidThermalGovernor.SuspendSpeculativePrefetch;
#endif
            // The original load condition ALWAYS required the *actual* player
            // to be inside exitMargin, even when prefetch projected farther.
            // Thus one current-position broad-phase query cannot omit a load.
            // Invalid/extreme query bounds fail open to the original full scan.
            int nearby = spatialReady
                ? spatialIndex.Query(current.x, current.z, exitMargin) : -1;
            int count = nearby >= 0 ? nearby : chunks.Length;
            for (int ordinal = 0; ordinal < count; ordinal++)
            {
                int i = nearby >= 0 ? spatialIndex.CandidateAt(ordinal) : ordinal;
                if (!states[i].valid || states[i].externallyOwned ||
                    states[i].loadedByThisStreamer) continue;
                Chunk cell = chunks[i];
                float distance = AarisChunkStreamingPolicy.OutsideDistanceSquared(
                    current.x, current.z, cell.center.x, cell.center.y,
                    cell.halfExtents.x, cell.halfExtents.y);
                if (distance > exitSquared) continue;
                bool urgent = distance <= entrySquared;
                float priority = urgent ? distance : AarisChunkStreamingPolicy.FirstApproach(
                    current.x, current.z, stepX, stepZ,
                    cell.center.x, cell.center.y, cell.halfExtents.x, cell.halfExtents.y, loadMargin);
                if (float.IsPositiveInfinity(priority))
                {
                    if (considerView && (distance < nearestView ||
                        (distance == nearestView && i < viewCell)) &&
                        AarisChunkStreamingPolicy.IntersectsViewCone(viewPosition.x, viewPosition.z,
                            viewDirection.x, viewDirection.y, prefetchSlope,
                            cell.center.x, cell.center.y, cell.halfExtents.x, cell.halfExtents.y))
                    {
                        viewCell = i;
                        nearestView = distance;
                    }
                    continue;
                }
                // Required detail ranks by current distance. Movement-only
                // candidates rank by the first contact along the route.
                if (loadCell < 0 || (urgent && !loadIsUrgent) ||
                    (urgent == loadIsUrgent && (priority < nearestLoad ||
                    (priority == nearestLoad && i < loadCell))))
                {
                    nearestLoad = priority;
                    loadCell = i;
                    loadIsUrgent = urgent;
                }
            }
            // View-only work always yields to actual position or predicted movement.
            if (loadCell < 0) loadCell = viewCell;
#if UNITY_ANDROID && !UNITY_EDITOR
            int speculativeLimit = AarisMobileStreamingFrameGate.IsConstrainedDevice()
                ? maxSpeculativeResidentCells : int.MaxValue;
            if (AarisAndroidThermalGovernor.ModerateThermalPressure)
                speculativeLimit = Math.Min(speculativeLimit, 1);
            bool speculativeLoadBlocked = !loadIsUrgent &&
                (AarisAndroidThermalGovernor.SuspendSpeculativePrefetch || speculativeResidentCount >= speculativeLimit);
            // Only make room if retiring this one held view would admit the
            // next travel load. Never recycle a cell the route still crosses.
            if (loadCell >= 0 && loadCell != viewCell && speculativeLoadBlocked && unloadCell < 0 &&
                cameraWarmCanYield && speculativeResidentCount == speculativeLimit &&
                !AarisAndroidThermalGovernor.SuspendSpeculativePrefetch)
            {
                Chunk warm = chunks[cameraWarmCell];
                if (float.IsPositiveInfinity(AarisChunkStreamingPolicy.FirstApproach(
                    current.x, current.z, stepX, stepZ,
                    warm.center.x, warm.center.y, warm.halfExtents.x, warm.halfExtents.y, loadMargin)) &&
                    AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())
                    unloadCell = cameraWarmCell;
            }
#endif
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
                if (speculativeLoadBlocked) return;
                // Real-position entry must not inherit the thermal
                // *speculative* delay. It still claims the global native slot
                // and honours essential import, physics and teardown fences.
                if (loadIsUrgent)
                {
                    if (!AarisMobileStreamingFrameGate.TryClaimUrgentSceneLoad()) return;
                }
                else if (!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild()) return;
#endif
                StartOperation(loadCell, true);
#if UNITY_ANDROID && !UNITY_EDITOR
                if (inFlight != null && loadCell == viewCell) cameraWarmCell = loadCell;
#endif
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private bool CameraWarmMayBeVisible(Chunk cell, Vector3 position, Vector2 direction, float slope)
        {
            // Enclose the perspective frustum's horizontal projection even
            // when the camera tilts. A steep view can project behind the
            // planar forward axis; keep its candidate rather than guessing.
            float vertical = slope / previousViewCamera.aspect;
            float radius = Mathf.Sqrt(slope * slope + vertical * vertical);
            float rise = Math.Abs(previousViewForward.y);
            float forward = Mathf.Sqrt(1f - rise * rise) - rise * radius;
            return !(forward > 0f) || AarisChunkStreamingPolicy.IntersectsViewCone(
                position.x, position.z, direction.x, direction.y, radius / forward,
                cell.center.x, cell.center.y, cell.halfExtents.x, cell.halfExtents.y);
        }
#endif

        private void SampleMovement(float now)
        {
            if (now < nextMovementSampleAt) return;
            nextMovementSampleAt = now + Mathf.Max(0.05f, pollSeconds);
            // Read-only sampling continues during native IO. Keep a recent
            // velocity instead of confusing a slow load with an app resume.
            Vector3 position = player.position;
            if (previousSampleValid)
                AarisChunkStreamingPolicy.SampleVelocity(position.x, position.z,
                    previousSample.x, previousSample.z, now - previousSampleAt,
                    maxPredictedSpeed, teleportRejectSpeed, out sampledVelocity.x, out sampledVelocity.y);
            previousSample = position;
            previousSampleAt = now;
            previousSampleValid = true;
        }

        private bool TryGetSteadyView(float now, out Vector3 position,
            out Vector2 direction, out float slope)
        {
            position = default(Vector3);
            direction = default(Vector2);
            slope = 0f;
            Camera view = playerMotor != null && !playerMotor.IsInAir ? playerMotor.AimCamera : null;
            if (view == null || !view.isActiveAndEnabled || view.orthographic)
            {
                previousViewCamera = null;
                return false;
            }
            Transform lens = view.transform;
            Vector3 forward = lens.forward;
            float elapsed = now - previousViewAt;
            // One normal polling interval with the same camera and a steady
            // heading prevents a fast pan or a resume from warming a district.
            bool steady = view == previousViewCamera && elapsed >= 0.05f && elapsed <= 1.5f &&
                          Vector3.Dot(forward, previousViewForward) >= 0.97f;
            previousViewCamera = view;
            previousViewForward = forward;
            previousViewAt = now;
            float planarLength = forward.x * forward.x + forward.z * forward.z;
            if (!steady || !(planarLength >= 0.36f)) return false;
            float invLength = 1f / Mathf.Sqrt(planarLength);
            direction = new Vector2(forward.x * invLength, forward.z * invLength);
            position = lens.position;
            // Protect the whole visible width for retirement. Admission uses
            // a narrower central sector so it can only warm one spare cell.
            slope = Mathf.Tan(view.fieldOfView * 0.5f * Mathf.Deg2Rad) * view.aspect;
            return slope > 0f && !float.IsInfinity(slope);
        }

        private void BuildSpatialIndexSlice()
        {
            if (spatialReady || spatialIndex == null) return;
            // Explicit per-frame limit rather than a wall-clock stopwatch,
            // which adds timer overhead and produces nondeterministic builds.
            const int MaxCellsPerRenderedFrame = 24;
            int end = Math.Min(chunks.Length,
                nextSpatialBuildIndex + MaxCellsPerRenderedFrame);
            for (int i = nextSpatialBuildIndex; i < end; i++)
            {
                if (!states[i].valid) continue;
                Chunk cell = chunks[i];
                spatialIndex.Add(i, cell.center.x, cell.center.y,
                    cell.halfExtents.x, cell.halfExtents.y);
            }
            nextSpatialBuildIndex = end;
            spatialReady = end == chunks.Length;
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
#if UNITY_ANDROID && !UNITY_EDITOR
            if (index == cameraWarmCell) cameraWarmCell = -1;
#endif
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
                // Both directions can finish in a later frame. The gate
                // fences native scene work even for earlier Update callers.
                AarisMobileStreamingFrameGate.TrackAdditiveSceneOperation(operation);
                sharedSceneOperation = operation;
#endif
                inFlight = operation;
                inFlightStartedAt = Time.unscaledTime;
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
