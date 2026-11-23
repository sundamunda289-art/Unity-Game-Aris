using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Profiling;

namespace Aaris.MapReview
{
    // Keep exact authored shapes. Terrain height/tree collisions and the player are never gated.
    [DisallowMultipleComponent, DefaultExecutionOrder(-250)]
    public sealed class AarisNearbyColliders : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static readonly ProfilerMarker k_ColliderGridIndex =
            new ProfilerMarker("Aaris.Startup.ColliderGridItem");
#endif
        [SerializeField] private Transform target;
        [SerializeField] private Collider[] obstacles;
        [SerializeField] private float activationDistance = 55;
        [SerializeField] private float cellSize = 250;
        private Bounds[] worldBounds;
        // Only supported static authored shapes are gated by the spatial grid.
        // Other collider types must remain enabled across disable/enable cycles.
        private bool[] streamed;
        private bool restoredOnDisable;
        private Coroutine deferredDeactivation;
        private Coroutine deferredIndexBuild;
        private int indexCursor;
        private bool indexReady;
        private readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
        // A single unexpectedly huge imported MeshCollider must not make one
        // Android indexing frame expand millions of cell entries. Large
        // authored shapes stay physical and are queried by their cached bounds.
        private readonly List<int> oversizedIndices = new List<int>(8);
        // Ordinary Android movement must never disable a whole settlement's
        // distant MeshColliders in one frame. Enabling nearby cover stays
        // immediate; the old distant solids retire in small native batches.
        private readonly List<int> pendingColliderRelease = new List<int>(64);
        private bool[] colliderReleaseQueued;
        private Coroutine deferredColliderRelease;
        private int colliderReleaseCursor;
        private const int AndroidReleaseUpdatesPerFrame = 12;
        private const double AndroidReleaseFrameBudgetSeconds = 0.001;
        private const int AndroidReleaseClockCheckInterval = 4;
        private const int MaxCellsPerCollider = 64;
        // Queries are usually vehicle/AI-sized. A corrupt or enormous
        // footprint must not iterate millions of grid coordinates on the
        // Android main thread. Large probes scan the bounded authored array.
        private const int MaxCellsPerProbe = 64;
        private List<int> active, nextActive;
        private int[] visited, wanted, probeVisited;
        private int query, probeQuery;
        private Vector3 lastPosition;
        private float nextCheck;
        private AarisOfflineDrop drop;
        private float nextDropRefresh;
        // Keep an already-live collider for a few extra metres so tiny player
        // movements at the activation boundary do not churn physics broadphase.
        // Never use this margin to enable a previously unloaded collider early.
        private const float ColliderReleaseMargin = 6f;
        // A 55m activation radius is much wider than the player's 3m refresh
        // step. Avoid rescanning ~1.5k static author colliders every 2m.
        private const float RefreshTravelSquared = 9f;
        // Bound native collider state changes during Android cold start.
        private const int DeferredColliderUpdatesPerFrame = 24;
        // Native mesh metadata, bounds transforms and cell inserts are split
        // across Android frames, rather than concentrated in a single Awake.
        private const int AndroidIndexUpdatesPerFrame = 96;
        // Count budgets cannot reflect variable MeshCollider import cost.
        // Use a soft wall-clock budget too, checking only after small batches
        // to avoid paying for a native clock read on every indexed object.
        private const double AndroidIndexFrameBudgetSeconds = 0.002;
        private const int AndroidIndexClockCheckInterval = 8;
        private const double AndroidDeactivationBudgetSeconds = 0.0015;
        private const int AndroidDeactivationClockCheckInterval = 4;

        private void Awake()
        {
            if (target == null || obstacles == null) { enabled = false; return; }
            worldBounds = new Bounds[obstacles.Length];
            streamed = new bool[obstacles.Length];
            visited = new int[obstacles.Length]; wanted = new int[obstacles.Length];
            probeVisited = new int[obstacles.Length];
            colliderReleaseQueued = new bool[obstacles.Length];
            active = new List<int>(obstacles.Length); nextActive = new List<int>(obstacles.Length);
            cellSize = Mathf.Max(32, cellSize);
            if (Application.platform == RuntimePlatform.Android)
            {
                // Keep the original solid world active until every collider
                // has been indexed. Never allow partial-grid streaming.
                BeginDeferredGridBuild();
                return;
            }
            for (int i = 0; i < obstacles.Length; i++)
                IndexCollider(i);
            FinishIndexBuild();
        }

        private void BeginDeferredGridBuild()
        {
            if (deferredIndexBuild != null) StopCoroutine(deferredIndexBuild);
            deferredIndexBuild = StartCoroutine(BuildGridAcrossFrames());
        }

        private IEnumerator BuildGridAcrossFrames()
        {
            // Don't compete with scene activation, player rig and URP state
            // during the first Android Awake. Untouched colliders stay solid.
            yield return null;
            AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();
            int processed = 0;
            double frameStartedAt = Time.realtimeSinceStartupAsDouble;
            while (indexCursor < obstacles.Length)
            {
                IndexCollider(indexCursor);
                // Advance before yielding, so disable/re-enable cannot add
                // the same collider to the same cell more than once.
                indexCursor++;
                processed++;
                if (processed >= AndroidIndexUpdatesPerFrame ||
                    (processed % AndroidIndexClockCheckInterval == 0 &&
                     Time.realtimeSinceStartupAsDouble - frameStartedAt >=
                     AndroidIndexFrameBudgetSeconds))
                {
                    processed = 0;
                    yield return null;
                    AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();
                    frameStartedAt = Time.realtimeSinceStartupAsDouble;
                }
            }
            // The final index batch may itself take the full soft budget.
            // Do not combine it with RefreshNow's native collider toggles
            // and the first deferred-deactivation batch in one frame.
            yield return null;
            deferredIndexBuild = null;
            FinishIndexBuild();
        }

        private void IndexCollider(int i)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using var profilerScope = k_ColliderGridIndex.Auto();
#endif
            Collider c = obstacles[i];
            if (c == null || !c.enabled) return;
            // Intentionally disabled authored colliders must stay off.
            // Collider.bounds is empty if its collider is disabled.
            Bounds local;
            if (c is MeshCollider mc && mc.sharedMesh != null) local = mc.sharedMesh.bounds;
            else if (c is BoxCollider box) local = new Bounds(box.center, box.size);
            else { worldBounds[i] = c.bounds; return; }
            streamed[i] = true;
            Matrix4x4 m = c.transform.localToWorldMatrix;
            Vector3 e = local.extents;
            Vector3 x = m.MultiplyVector(new Vector3(e.x, 0, 0));
            Vector3 y = m.MultiplyVector(new Vector3(0, e.y, 0));
            Vector3 z = m.MultiplyVector(new Vector3(0, 0, e.z));
            Vector3 worldSize = 2 * new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
                Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y), Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
            worldBounds[i] = new Bounds(m.MultiplyPoint3x4(local.center), worldSize);
            Bounds bounds = worldBounds[i];
            Vector3 min = bounds.min, max = bounds.max;
            // Invalid or astronomically distant imports remain solid instead
            // of overflowing cell coordinates or silently losing collision.
            // The authored 2 km world is far inside this generous safe range.
            if (!Finite(min) || !Finite(max) ||
                Mathf.Abs(min.x) > 1000000f || Mathf.Abs(max.x) > 1000000f ||
                Mathf.Abs(min.z) > 1000000f || Mathf.Abs(max.z) > 1000000f)
            {
                streamed[i] = false;
                return;
            }
            int firstX = Cell(min.x), lastX = Cell(max.x);
            int firstZ = Cell(min.z), lastZ = Cell(max.z);
            long width = (long)lastX - firstX + 1;
            long depth = (long)lastZ - firstZ + 1;
            // The existing per-frame budget counts *colliders*, not grid
            // cells. Prevent a single huge collider from dominating a frame.
            // An overflow collider is still considered for activation, LOS
            // and placement via oversizedIndices in both query paths.
            if (width <= 0 || depth <= 0 ||
                width > MaxCellsPerCollider || depth > MaxCellsPerCollider ||
                width * depth > MaxCellsPerCollider)
            {
                oversizedIndices.Add(i);
                return;
            }
            // A long pier or building belongs to every intersected cell.
            // Long loop counters cannot wrap at int.MaxValue.
            for (long xCell = firstX; xCell <= lastX; xCell++)
                for (long zCell = firstZ; zCell <= lastZ; zCell++)
                {
                    long key = Key((int)xCell, (int)zCell);
                    if (!cells.TryGetValue(key, out List<int> entries))
                    { entries = new List<int>(); cells.Add(key, entries); }
                    entries.Add(i);
                }
        }

        private static bool Finite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                   !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                   !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private void FinishIndexBuild()
        {
            // Only a complete grid can gate physics and answer inactive
            // collider probes. Start live-player deactivation afterwards.
            indexReady = true;
            RefreshNow();
            BeginDeferredDeactivation();
        }

        private void BeginDeferredDeactivation()
        {
            if (deferredDeactivation != null) StopCoroutine(deferredDeactivation);
            deferredDeactivation = StartCoroutine(DeactivateDistantAcrossFrames());
        }

        private IEnumerator DeactivateDistantAcrossFrames()
        {
            // A distant collider remains solid until its turn. We never
            // deactivate terrain, authored-off or unsupported collider types.
            int processed = 0;
            bool constrainTime = Application.platform == RuntimePlatform.Android;
            // FinishIndexBuild's RefreshNow has just synchronized live cover.
            // Avoid following that work with the first batch of native toggles
            // in the same Android render frame; Editor keeps its old cadence.
            if (constrainTime) yield return null;
            if (constrainTime)
                AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();
            double frameStartedAt = constrainTime ? Time.realtimeSinceStartupAsDouble : 0d;
            // One native Transform read per coroutine slice. Unity resumes
            // coroutines on the main thread; no target movement can interleave
            // with individual collider checks inside this slice.
            Vector3 framePosition = target != null ? target.position : default(Vector3);
            float safeDistance = Mathf.Max(0f, activationDistance) +
                ColliderReleaseMargin;
            float safeDistanceSquared = safeDistance * safeDistance;
            for (int i = 0; i < obstacles.Length; i++)
            {
                if (target == null) { deferredDeactivation = null; yield break; }
                Collider c = obstacles[i];
                if (streamed[i] && c != null && c.enabled)
                {
                    // Recheck the *live* target; a teleport or moving player
                    // must retain all buildings within the 55m safety region.
                    Vector3 nearest = worldBounds[i].ClosestPoint(framePosition);
                    float dx = nearest.x - framePosition.x;
                    float dz = nearest.z - framePosition.z;
                    if (dx * dx + dz * dz > safeDistanceSquared)
                        c.enabled = false;
                }
                processed++;
                if (processed >= DeferredColliderUpdatesPerFrame ||
                    (constrainTime && processed % AndroidDeactivationClockCheckInterval == 0 &&
                     Time.realtimeSinceStartupAsDouble - frameStartedAt >=
                     AndroidDeactivationBudgetSeconds))
                {
                    processed = 0;
                    yield return null;
                    // A teleport/move between frames gets fresh coordinates
                    // before any collider in the next batch is disabled.
                    framePosition = target != null ? target.position : default(Vector3);
                    safeDistance = Mathf.Max(0f, activationDistance) +
                        ColliderReleaseMargin;
                    safeDistanceSquared = safeDistance * safeDistance;
                    if (constrainTime)
                        AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();
                    if (constrainTime)
                        frameStartedAt = Time.realtimeSinceStartupAsDouble;
                }
            }
            deferredDeactivation = null;
        }

        private int Cell(float value) => Mathf.FloorToInt(value / cellSize);
        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;
        private void Start()
        {
            if (target != null) drop = target.GetComponent<AarisOfflineDrop>();
        }

        private void Update()
        {
            if (target == null)
            {
                // When the player disappears, stop streaming immediately.
                // OnDisable restores only the originally active colliders,
                // preventing distant structures from remaining non-solid.
                enabled = false;
                return;
            }
            if (!indexReady) return;
            // Scene-load bootstraps can attach the flight component AFTER
            // this streamer's Start. Retry no more than once per unscaled
            // second until present, then avoid repeated native lookups.
            if (drop == null && Time.unscaledTime >= nextDropRefresh)
            {
                nextDropRefresh = Time.unscaledTime + 1f;
                drop = target.GetComponent<AarisOfflineDrop>();
            }
            if (drop != null && drop.IsRidingAircraft) return;
            // Refresh after three metres of movement (and at least every second).
            // Geometry stays active 55m away; 3m travel cannot leave a
            // player without nearby solid walls. Teleports call RefreshNow().
            // Only horizontal movement changes nearby X/Z collider cells.
            // Vertical jumps and falls still get the periodic 1 Hz safety poll.
            Vector3 current = target.position;
            float dx = current.x - lastPosition.x;
            float dz = current.z - lastPosition.z;
            if (dx * dx + dz * dz >= RefreshTravelSquared ||
                Time.unscaledTime >= nextCheck) RefreshNow(false);
        }
        // Explicit teleport and startup calls still synchronize immediately.
        // Periodic polls avoid a global physics sync if no collider changed.
        public void RefreshNow()
        {
            // Other systems may retain a reference after this streamer was
            // disabled. OnDisable restored the original solid colliders;
            // do not secretly gate them again while Update is inactive.
            if (!isActiveAndEnabled) return;
            if (!indexReady) return;
            RefreshNow(true);
        }

        private void RefreshNow(bool forcePhysicsSync)
        {
            if (!indexReady || target == null || worldBounds == null) return;
            // Forced startup/teleport refreshes can invoke Physics.SyncTransforms:
            // keep their original native-frame reservation. Routine movement
            // refreshes reserve only if a collider is actually re-enabled;
            // unchanged cached bounds do not need a native-toggle frame slot.
            if (forcePhysicsSync)
                AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();
            lastPosition = target.position;
            nextCheck = Time.unscaledTime + 1f;
            float enterDistance = Mathf.Max(0f, activationDistance);
            float retainDistance = enterDistance + ColliderReleaseMargin;
            float enterLimit = enterDistance * enterDistance;
            float retainLimit = retainDistance * retainDistance;
            if (query == int.MaxValue) { Array.Clear(visited, 0, visited.Length); Array.Clear(wanted, 0, wanted.Length); query = 0; }
            query++;
            nextActive.Clear();
            for (int xCell = Cell(lastPosition.x - retainDistance); xCell <= Cell(lastPosition.x + retainDistance); xCell++)
                for (int zCell = Cell(lastPosition.z - retainDistance); zCell <= Cell(lastPosition.z + retainDistance); zCell++)
            {
                if (!cells.TryGetValue(Key(xCell, zCell), out List<int> entries)) continue;
                foreach (int i in entries)
                {
                    if (visited[i] == query) continue;
                    visited[i] = query;
                    // XZ distance supports long piers and elevated towers without early deactivation.
                    Vector3 q = worldBounds[i].ClosestPoint(lastPosition);
                    float dx = q.x - lastPosition.x, dz = q.z - lastPosition.z;
                    float distanceSquared = dx * dx + dz * dz;
                    // Even an already-enabled collider cannot be retained
                    // outside this radius. Reject using managed cached bounds
                    // before querying Unity Object/Collider native state.
                    if (distanceSquared > retainLimit) continue;
                    Collider c = obstacles[i];
                    if (c == null) continue;
                    // Existing active colliders have a wider exit radius.
                    // New colliders still enter at the authored distance.
                    float limit = c.enabled ? retainLimit : enterLimit;
                    if (distanceSquared > limit) continue;
                    wanted[i] = query;
                    if (!c.enabled)
                    {
                        // Claim before PhysX receives a newly enabled collider.
                        // An unchanged nearby wall never blocks optional model work.
                        AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();
                        c.enabled = true;
                    }
                    nextActive.Add(i);
                }
            }
            // Large authored structures are intentionally absent from cells.
            // Scan just this small overflow list with the same exact XZ
            // distances, hysteresis and collision-enable rules as grid hits.
            foreach (int i in oversizedIndices)
            {
                if (visited[i] == query) continue;
                visited[i] = query;
                Vector3 q = worldBounds[i].ClosestPoint(lastPosition);
                float dx = q.x - lastPosition.x, dz = q.z - lastPosition.z;
                float distanceSquared = dx * dx + dz * dz;
                if (distanceSquared > retainLimit) continue;
                Collider c = obstacles[i];
                if (c == null) continue;
                float limit = c.enabled ? retainLimit : enterLimit;
                if (distanceSquared > limit) continue;
                wanted[i] = query;
                if (!c.enabled)
                {
                    // Claim before PhysX receives a newly enabled collider.
                    // An unchanged nearby wall never blocks optional model work.
                    AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();
                    c.enabled = true;
                }
                nextActive.Add(i);
            }
            bool androidMovementRefresh = !forcePhysicsSync &&
                Application.platform == RuntimePlatform.Android;
            foreach (int i in active)
                if (wanted[i] != query && obstacles[i] != null && obstacles[i].enabled)
                {
                    if (androidMovementRefresh)
                    {
                        // Keep wall physics available until the worker verifies
                        // the *latest* player position. A return/teleport can
                        // reacquire the collider before this retirement runs.
                        if (!colliderReleaseQueued[i])
                        {
                            colliderReleaseQueued[i] = true;
                            pendingColliderRelease.Add(i);
                        }
                    }
                    else obstacles[i].enabled = false;
                }
            List<int> swap = active; active = nextActive; nextActive = swap;
            if (forcePhysicsSync)
            {
                // Explicit refresh/teleport retains the original all-at-once
                // guarantee. Pending previous-frame releases cannot escape
                // the new position's wanted set or linger indefinitely.
                FlushPendingColliderReleases();
            }
            else if (androidMovementRefresh && deferredColliderRelease == null &&
                     colliderReleaseCursor < pendingColliderRelease.Count)
                deferredColliderRelease = StartCoroutine(ReleaseDistantAcrossFrames());
            // Collider.enabled toggles do not require a global Transform sync.
            // Only startup/teleport callers explicitly request the flush; leave
            // regular 2-metre / 0.25-second streaming checks non-blocking.
            // Renderer visibility, terrain and native tree collisions are unchanged.
            if (forcePhysicsSync) Physics.SyncTransforms();
        }
        private void FlushPendingColliderReleases()
        {
            if (deferredColliderRelease != null)
            {
                StopCoroutine(deferredColliderRelease);
                deferredColliderRelease = null;
            }
            for (int n = colliderReleaseCursor; n < pendingColliderRelease.Count; n++)
            {
                int i = pendingColliderRelease[n];
                colliderReleaseQueued[i] = false;
                // A collider reacquired by the latest spatial refresh is live.
                if (wanted[i] != query && obstacles[i] != null && obstacles[i].enabled)
                    obstacles[i].enabled = false;
            }
            pendingColliderRelease.Clear();
            colliderReleaseCursor = 0;
        }

        private IEnumerator ReleaseDistantAcrossFrames()
        {
            // An ordinary movement refresh already enabled nearby cover. Give
            // its expensive native collider toggles their own rendered frames.
            yield return null;
            // A stale retirement queue may contain only already-reacquired
            // walls. Do not reserve scarce low-end import time until native
            // collider state actually changes. One claim per changed frame.
            bool reservedReleaseFrame = false;
            int processed = 0;
            double frameStartedAt = Time.realtimeSinceStartupAsDouble;
            // Snapshot once per rendered batch, not once for each native
            // collider toggle. Refresh after every yield to handle teleports.
            Vector3 framePosition = target != null ? target.position : default(Vector3);
            float retain = Mathf.Max(0f, activationDistance) +
                ColliderReleaseMargin;
            float retainSquared = retain * retain;
            while (colliderReleaseCursor < pendingColliderRelease.Count)
            {
                int i = pendingColliderRelease[colliderReleaseCursor++];
                colliderReleaseQueued[i] = false;
                Collider collider = obstacles[i];
                if (target != null && collider != null && collider.enabled &&
                    wanted[i] != query)
                {
                    Vector3 closest = worldBounds[i].ClosestPoint(framePosition);
                    float dx = closest.x - framePosition.x;
                    float dz = closest.z - framePosition.z;
                    if (dx * dx + dz * dz > retainSquared)
                    {
                        if (!reservedReleaseFrame)
                        {
                            AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();
                            reservedReleaseFrame = true;
                        }
                        collider.enabled = false;
                    }
                }
                processed++;
                if (processed >= AndroidReleaseUpdatesPerFrame ||
                    (processed % AndroidReleaseClockCheckInterval == 0 &&
                     Time.realtimeSinceStartupAsDouble - frameStartedAt >=
                     AndroidReleaseFrameBudgetSeconds))
                {
                    processed = 0;
                    yield return null;
                    framePosition = target != null ? target.position : default(Vector3);
                    retain = Mathf.Max(0f, activationDistance) +
                        ColliderReleaseMargin;
                    retainSquared = retain * retain;
                    reservedReleaseFrame = false;
                    frameStartedAt = Time.realtimeSinceStartupAsDouble;
                }
            }
            pendingColliderRelease.Clear();
            colliderReleaseCursor = 0;
            deferredColliderRelease = null;
        }

        // Parked cars can stream as far as 105 m away, while authored house/wall
        // colliders only activate near the player. A remote Physics overlap query
        // cannot see the disabled geometry. Reject such candidate locations using
        // cached world bounds until the real collider enters the active radius.
        // This check does NOT toggle a collider or allocate per probe.
        internal bool HasPotentialInactiveObstacle(Vector3 center,
            Vector3 halfExtents, Quaternion rotation)
        {
            if (!indexReady) return false;
            if (!isActiveAndEnabled || obstacles == null || streamed == null ||
                worldBounds == null) return false;
            Vector3 extents;
            // AI cover, player pickups and many spawn probes use EXACT
            // identity rotation. Avoid three quaternion-vector transforms per
            // probe without changing the bounds or streamed-collider rules.
            if (rotation.x == 0f && rotation.y == 0f &&
                rotation.z == 0f && rotation.w == 1f)
            {
                extents = new Vector3(Mathf.Abs(halfExtents.x),
                    Mathf.Abs(halfExtents.y), Mathf.Abs(halfExtents.z));
            }
            else
            {
                Vector3 right = rotation * (Vector3.right * halfExtents.x);
                Vector3 up = rotation * (Vector3.up * halfExtents.y);
                Vector3 forward = rotation * (Vector3.forward * halfExtents.z);
                extents = new Vector3(
                    Mathf.Abs(right.x) + Mathf.Abs(up.x) + Mathf.Abs(forward.x),
                    Mathf.Abs(right.y) + Mathf.Abs(up.y) + Mathf.Abs(forward.y),
                    Mathf.Abs(right.z) + Mathf.Abs(up.z) + Mathf.Abs(forward.z));
            }
            Bounds probe = new Bounds(center, 2f * extents);
            Vector3 probeMin = probe.min, probeMax = probe.max;
            // A broken/overflowing transform is not evidence of a clear
            // vehicle/AI spawn or safe exit. Reject it without grid traversal.
            if (!Finite(probeMin) || !Finite(probeMax)) return true;
            int firstX = Cell(probeMin.x), lastX = Cell(probeMax.x);
            int firstZ = Cell(probeMin.z), lastZ = Cell(probeMax.z);
            long width = (long)lastX - firstX + 1;
            long depth = (long)lastZ - firstZ + 1;
            if (width <= 0 || depth <= 0 ||
                width > MaxCellsPerProbe || depth > MaxCellsPerProbe ||
                width * depth > MaxCellsPerProbe)
            {
                // Do not enumerate giant rectangular grids. Scan only the
                // serialized authored obstacle set (including oversized
                // grid members), preserving exact 3D bounds intersection.
                for (int i = 0; i < obstacles.Length; i++)
                {
                    if (!streamed[i] || !worldBounds[i].Intersects(probe))
                        continue;
                    Collider candidate = obstacles[i];
                    if (candidate != null && !candidate.enabled)
                        return true;
                }
                return false;
            }
            // The same large wall can occupy several spatial cells. Check it only
            // once per placement probe, without allocating a HashSet in combat.
            if (probeQuery == int.MaxValue)
            {
                Array.Clear(probeVisited, 0, probeVisited.Length);
                probeQuery = 0;
            }
            int probeStamp = ++probeQuery;
            for (int xCell = firstX; xCell <= lastX; xCell++)
                for (int zCell = firstZ; zCell <= lastZ; zCell++)
                {
                    if (!cells.TryGetValue(Key(xCell, zCell), out List<int> entries)) continue;
                    foreach (int i in entries)
                    {
                        if (probeVisited[i] == probeStamp) continue;
                        probeVisited[i] = probeStamp;
                        Collider c = obstacles[i];
                        if (worldBounds[i].Intersects(probe) &&
                            streamed[i] && c != null && !c.enabled)
                            return true;
                    }
                }
            // The same large bridge/wall must be visible to placement and
            // rival-cover probes even when it spans more than 64 grid cells.
            foreach (int i in oversizedIndices)
            {
                if (probeVisited[i] == probeStamp) continue;
                probeVisited[i] = probeStamp;
                Collider c = obstacles[i];
                if (worldBounds[i].Intersects(probe) &&
                    streamed[i] && c != null && !c.enabled)
                    return true;
            }
            return false;
        }

        private void OnEnable()
        {
            // OnDisable temporarily restores all authored colliders. Without a
            // rescan, toggling this component leaves every distant building
            // collidable forever even though its grid still tracks only the
            // previously active indices.
            // When the target has gone away (e.g. scene teardown), keep
            // authored colliders enabled rather than disabling every wall.
            if (!restoredOnDisable || target == null || worldBounds == null ||
                streamed == null || active == null || nextActive == null ||
                obstacles == null) return;
            restoredOnDisable = false;
            if (!indexReady)
            {
                // An interrupted Android index resumes from its saved cursor.
                // No collider was gated while indexing was incomplete.
                BeginDeferredGridBuild();
                return;
            }
            active.Clear();
            nextActive.Clear();
            // OnDisable restored the original solid world. Rebuild only the
            // near active set now, then gate distant physics incrementally.
            RefreshNow();
            BeginDeferredDeactivation();
        }

        private void OnDisable()
        {
            if (deferredIndexBuild != null)
            {
                StopCoroutine(deferredIndexBuild);
                deferredIndexBuild = null;
            }
            if (deferredDeactivation != null)
            {
                StopCoroutine(deferredDeactivation);
                deferredDeactivation = null;
            }
            if (deferredColliderRelease != null)
            {
                StopCoroutine(deferredColliderRelease);
                deferredColliderRelease = null;
            }
            pendingColliderRelease.Clear();
            colliderReleaseCursor = 0;
            if (colliderReleaseQueued != null)
                Array.Clear(colliderReleaseQueued, 0, colliderReleaseQueued.Length);
            if (obstacles == null || streamed == null) return;
            restoredOnDisable = active != null;
            // Only restore colliders that this streamer disabled itself.
            // An authored-off collider or unsupported collider must retain
            // its original state, even when the component is toggled.
            for (int i = 0; i < obstacles.Length; i++)
                if (streamed[i] && obstacles[i] != null)
                    obstacles[i].enabled = true;
        }
    }
}
