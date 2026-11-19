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
        private const int MaxCellsPerCollider = 64;
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

        private void Awake()
        {
            if (target == null || obstacles == null) { enabled = false; return; }
            worldBounds = new Bounds[obstacles.Length];
            streamed = new bool[obstacles.Length];
            visited = new int[obstacles.Length]; wanted = new int[obstacles.Length];
            probeVisited = new int[obstacles.Length];
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
            int processed = 0;
            while (indexCursor < obstacles.Length)
            {
                IndexCollider(indexCursor);
                // Advance before yielding, so disable/re-enable cannot add
                // the same collider to the same cell more than once.
                indexCursor++;
                if (++processed >= AndroidIndexUpdatesPerFrame)
                {
                    processed = 0;
                    yield return null;
                }
            }
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
            for (int i = 0; i < obstacles.Length; i++)
            {
                if (target == null) { deferredDeactivation = null; yield break; }
                Collider c = obstacles[i];
                if (streamed[i] && c != null && c.enabled)
                {
                    // Recheck the *live* target; a teleport or moving player
                    // must retain all buildings within the 55m safety region.
                    Vector3 position = target.position;
                    Vector3 nearest = worldBounds[i].ClosestPoint(position);
                    float dx = nearest.x - position.x, dz = nearest.z - position.z;
                    float safeDistance = Mathf.Max(0f, activationDistance) +
                        ColliderReleaseMargin;
                    if (dx * dx + dz * dz > safeDistance * safeDistance)
                        c.enabled = false;
                }
                if (++processed >= DeferredColliderUpdatesPerFrame)
                {
                    processed = 0;
                    yield return null;
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
            if ((target.position - lastPosition).sqrMagnitude >= RefreshTravelSquared ||
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
                    Collider c = obstacles[i];
                    if (c == null) continue;
                    // XZ distance supports long piers and elevated towers without early deactivation.
                    Vector3 q = worldBounds[i].ClosestPoint(lastPosition);
                    float dx = q.x - lastPosition.x, dz = q.z - lastPosition.z;
                    // Existing active colliders have a wider exit radius.
                    // New colliders still enter at the authored distance.
                    float limit = c.enabled ? retainLimit : enterLimit;
                    if (dx * dx + dz * dz > limit) continue;
                    wanted[i] = query;
                    if (!c.enabled) c.enabled = true;
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
                Collider c = obstacles[i];
                if (c == null) continue;
                Vector3 q = worldBounds[i].ClosestPoint(lastPosition);
                float dx = q.x - lastPosition.x, dz = q.z - lastPosition.z;
                float limit = c.enabled ? retainLimit : enterLimit;
                if (dx * dx + dz * dz > limit) continue;
                wanted[i] = query;
                if (!c.enabled) c.enabled = true;
                nextActive.Add(i);
            }
            foreach (int i in active)
                if (wanted[i] != query && obstacles[i] != null && obstacles[i].enabled)
                { obstacles[i].enabled = false; }
            List<int> swap = active; active = nextActive; nextActive = swap;
            // Collider.enabled toggles do not require a global Transform sync.
            // Only startup/teleport callers explicitly request the flush; leave
            // regular 2-metre / 0.25-second streaming checks non-blocking.
            // Renderer visibility, terrain and native tree collisions are unchanged.
            if (forcePhysicsSync) Physics.SyncTransforms();
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
            Vector3 right = rotation * (Vector3.right * halfExtents.x);
            Vector3 up = rotation * (Vector3.up * halfExtents.y);
            Vector3 forward = rotation * (Vector3.forward * halfExtents.z);
            Vector3 extents = new Vector3(
                Mathf.Abs(right.x) + Mathf.Abs(up.x) + Mathf.Abs(forward.x),
                Mathf.Abs(right.y) + Mathf.Abs(up.y) + Mathf.Abs(forward.y),
                Mathf.Abs(right.z) + Mathf.Abs(up.z) + Mathf.Abs(forward.z));
            Bounds probe = new Bounds(center, 2f * extents);
            // The same large wall can occupy several spatial cells. Check it only
            // once per placement probe, without allocating a HashSet in combat.
            if (probeQuery == int.MaxValue)
            {
                Array.Clear(probeVisited, 0, probeVisited.Length);
                probeQuery = 0;
            }
            int probeStamp = ++probeQuery;
            for (int xCell = Cell(probe.min.x); xCell <= Cell(probe.max.x); xCell++)
                for (int zCell = Cell(probe.min.z); zCell <= Cell(probe.max.z); zCell++)
                {
                    if (!cells.TryGetValue(Key(xCell, zCell), out List<int> entries)) continue;
                    foreach (int i in entries)
                    {
                        if (probeVisited[i] == probeStamp) continue;
                        probeVisited[i] = probeStamp;
                        Collider c = obstacles[i];
                        if (streamed[i] && c != null && !c.enabled &&
                            worldBounds[i].Intersects(probe))
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
                if (streamed[i] && c != null && !c.enabled &&
                    worldBounds[i].Intersects(probe))
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
