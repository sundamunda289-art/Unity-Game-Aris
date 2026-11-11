using System;
using System.Collections.Generic;
using UnityEngine;

namespace Aaris.MapReview
{
    // Keep exact authored shapes. Terrain height/tree collisions and the player are never gated.
    [DisallowMultipleComponent, DefaultExecutionOrder(-250)]
    public sealed class AarisNearbyColliders : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private Collider[] obstacles;
        [SerializeField] private float activationDistance = 55;
        [SerializeField] private float cellSize = 250;
        private Bounds[] worldBounds;
        // Only supported static authored shapes are gated by the spatial grid.
        // Other collider types must remain enabled across disable/enable cycles.
        private bool[] streamed;
        private bool restoredOnDisable;
        private readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
        private List<int> active, nextActive;
        private int[] visited, wanted;
        private int query;
        private Vector3 lastPosition;
        private float nextCheck;

        private void Awake()
        {
            if (target == null || obstacles == null) { enabled = false; return; }
            worldBounds = new Bounds[obstacles.Length];
            streamed = new bool[obstacles.Length];
            visited = new int[obstacles.Length]; wanted = new int[obstacles.Length];
            active = new List<int>(obstacles.Length); nextActive = new List<int>(obstacles.Length);
            cellSize = Mathf.Max(32, cellSize);
            for (int i = 0; i < obstacles.Length; i++)
            {
                Collider c = obstacles[i];
                if (c == null) continue;
                // Collider.bounds is empty while disabled. Read only local bounds metadata;
                // never temporarily enable/cook every distant building just to cache bounds.
                Bounds local;
                if (c is MeshCollider mc && mc.sharedMesh != null) local = mc.sharedMesh.bounds;
                else if (c is BoxCollider box) local = new Bounds(box.center, box.size);
                else { c.enabled = true; worldBounds[i] = c.bounds; continue; }
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
                // An object belongs to EVERY cell touched by its bounds, not just its pivot.
                // This keeps long piers and buildings collidable across cell boundaries.
                for (int xCell = Cell(bounds.min.x); xCell <= Cell(bounds.max.x); xCell++)
                    for (int zCell = Cell(bounds.min.z); zCell <= Cell(bounds.max.z); zCell++)
                    {
                        long key = Key(xCell, zCell);
                        if (!cells.TryGetValue(key, out List<int> entries))
                        { entries = new List<int>(); cells.Add(key, entries); }
                        entries.Add(i);
                    }
                c.enabled = false;
            }
            RefreshNow();
        }
        private int Cell(float value) => Mathf.FloorToInt(value / cellSize);
        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;
        private void Update()
        {
            if (target == null) return;
            if ((target.position - lastPosition).sqrMagnitude >= 4 || Time.unscaledTime >= nextCheck) RefreshNow(false);
        }
        // Explicit teleport and startup calls still synchronize immediately.
        // Periodic polls avoid a global physics sync if no collider changed.
        public void RefreshNow() => RefreshNow(true);

        private void RefreshNow(bool forcePhysicsSync)
        {
            if (target == null || worldBounds == null) return;
            lastPosition = target.position;
            nextCheck = Time.unscaledTime + .25f;
            float limit = activationDistance * activationDistance;
            if (query == int.MaxValue) { Array.Clear(visited, 0, visited.Length); Array.Clear(wanted, 0, wanted.Length); query = 0; }
            query++;
            nextActive.Clear();
            for (int xCell = Cell(lastPosition.x - activationDistance); xCell <= Cell(lastPosition.x + activationDistance); xCell++)
                for (int zCell = Cell(lastPosition.z - activationDistance); zCell <= Cell(lastPosition.z + activationDistance); zCell++)
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
                    if (dx * dx + dz * dz > limit) continue;
                    wanted[i] = query;
                    if (!c.enabled) c.enabled = true;
                    nextActive.Add(i);
                }
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
        private void OnEnable()
        {
            // OnDisable temporarily restores all authored colliders. Without a
            // rescan, toggling this component leaves every distant building
            // collidable forever even though its grid still tracks only the
            // previously active indices.
            if (!restoredOnDisable || streamed == null || active == null ||
                nextActive == null || obstacles == null) return;
            restoredOnDisable = false;
            for (int i = 0; i < obstacles.Length; i++)
                if (streamed[i] && obstacles[i] != null)
                    obstacles[i].enabled = false;
            active.Clear();
            nextActive.Clear();
            // Restore nearby collisions immediately before the next player Move.
            RefreshNow();
        }

        private void OnDisable()
        {
            if (obstacles == null) return;
            restoredOnDisable = streamed != null && active != null;
            foreach (Collider c in obstacles) if (c != null) c.enabled = true;
        }
    }
}
