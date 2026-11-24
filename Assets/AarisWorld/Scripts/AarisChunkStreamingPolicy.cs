using System;

namespace Aaris.MapReview
{
    /// <summary>Allocation-free XZ policy for authored additive scene cells.</summary>
    internal static class AarisChunkStreamingPolicy
    {
        // Squared distance outside an AABB (zero inside). Bounds represent the
        // entire chunk, not its centre, so large tiles do not vanish at edges.
        internal static float OutsideDistanceSquared(float x, float z,
            float centerX, float centerZ, float halfX, float halfZ)
        {
            float dx = Math.Max(0f, Math.Abs(x - centerX) - halfX);
            float dz = Math.Max(0f, Math.Abs(z - centerZ) - halfZ);
            return dx * dx + dz * dz;
        }

        // Ground-plane broad phase. Test the chunk's footprint against the
        // forward and side planes rather than the centre of an irregular cell.
        internal static bool IntersectsViewCone(float x, float z, float forwardX, float forwardZ,
            float slope, float centerX, float centerZ, float halfX, float halfZ)
        {
            float offsetX = centerX - x, offsetZ = centerZ - z;
            if (!ReachesViewPlane(offsetX, offsetZ, halfX, halfZ, forwardX, forwardZ))
                return false;
            float baseX = slope * forwardX, baseZ = slope * forwardZ;
            return ReachesViewPlane(offsetX, offsetZ, halfX, halfZ, baseX + forwardZ, baseZ - forwardX) &&
                   ReachesViewPlane(offsetX, offsetZ, halfX, halfZ, baseX - forwardZ, baseZ + forwardX);
        }

        private static bool ReachesViewPlane(float x, float z, float halfX, float halfZ,
            float normalX, float normalZ)
        {
            return x * normalX + z * normalZ +
                halfX * Math.Abs(normalX) + halfZ * Math.Abs(normalZ) >= 0f;
        }

        // Reject teleports/resumes rather than extrapolating hundreds of metres.
        // This predicts *loading* only; unloading always uses actual position.
        internal static void Predict(float x, float z, float oldX, float oldZ,
            float elapsed, float leadSeconds, float speedCap,
            float teleportSpeed, out float predictedX, out float predictedZ)
        {
            predictedX = x;
            predictedZ = z;
            if (elapsed < 0.05f || elapsed > 1.5f || leadSeconds <= 0f)
                return;
            float vx = (x - oldX) / elapsed;
            float vz = (z - oldZ) / elapsed;
            float speedSquared = vx * vx + vz * vz;
            if (float.IsNaN(speedSquared) || float.IsInfinity(speedSquared) ||
                speedSquared > teleportSpeed * teleportSpeed)
                return;
            float speed = (float)Math.Sqrt(speedSquared);
            if (speed <= 1f) return;
            float ratio = Math.Min(1f, speedCap / speed);
            predictedX += vx * ratio * leadSeconds;
            predictedZ += vz * ratio * leadSeconds;
        }
    }

    /// <summary>
    /// Allocation-free per-poll broad phase for authored scene chunks.
    /// Only built for larger manifests. Each valid cell is inserted into 256 m
    /// XZ buckets; very large cells go through a separate exact AABB check.
    /// Unsafe/extreme coordinates and oversized indexes fail OPEN to the
    /// old complete scan instead of hiding necessary world geometry.
    /// </summary>
    internal sealed class AarisChunkSpatialIndex
    {
        private const double BucketMeters = 256d;
        private const int MaxBucketsPerCell = 64;
        private const int MaxQueryBuckets = 128;
        private const int MaxTotalEntries = 65536;
        private readonly System.Collections.Generic.Dictionary<long,
            System.Collections.Generic.List<int>> buckets =
            new System.Collections.Generic.Dictionary<long,
                System.Collections.Generic.List<int>>();
        private readonly System.Collections.Generic.List<LargeCell> largeCells =
            new System.Collections.Generic.List<LargeCell>();
        private readonly int[] visited;
        private readonly int[] candidates;
        private int generation, candidateCount, entryCount;
        private bool fullScan;

        private struct LargeCell
        {
            internal int index;
            internal double x, z, halfX, halfZ;
        }

        internal AarisChunkSpatialIndex(int cellCount)
        {
            visited = new int[cellCount];
            candidates = new int[cellCount];
        }

        private static long BucketKey(int x, int z)
        {
            return ((long)x << 32) | (uint)z;
        }

        private static bool ToRange(double min, double max, out int first, out int last)
        {
            first = last = 0;
            if (double.IsNaN(min) || double.IsNaN(max) ||
                double.IsInfinity(min) || double.IsInfinity(max) ||
                min > max) return false;
            double lo = Math.Floor(min / BucketMeters);
            double hi = Math.Floor(max / BucketMeters);
            // Keep cast and loop arithmetic safe, even on malformed scenes
            // or a player teleported far outside the intended world.
            if (lo < -1000000000d || hi > 1000000000d) return false;
            first = (int)lo;
            last = (int)hi;
            return true;
        }

        internal void Add(int index, float centerX, float centerZ,
            float halfX, float halfZ)
        {
            if (fullScan) return;
            int x0, x1, z0, z1;
            if (!ToRange((double)centerX - halfX, (double)centerX + halfX, out x0, out x1) ||
                !ToRange((double)centerZ - halfZ, (double)centerZ + halfZ, out z0, out z1))
            {
                fullScan = true;
                buckets.Clear();
                largeCells.Clear();
                return;
            }
            long nx = (long)x1 - x0 + 1;
            long nz = (long)z1 - z0 + 1;
            if (nx * nz > MaxBucketsPerCell)
            {
                largeCells.Add(new LargeCell
                {
                    index = index, x = centerX, z = centerZ,
                    halfX = halfX, halfZ = halfZ
                });
                return;
            }
            if (entryCount + nx * nz > MaxTotalEntries)
            {
                fullScan = true;
                buckets.Clear();
                largeCells.Clear();
                return;
            }
            for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
            {
                long key = BucketKey(x, z);
                System.Collections.Generic.List<int> list;
                if (!buckets.TryGetValue(key, out list))
                {
                    list = new System.Collections.Generic.List<int>(4);
                    buckets.Add(key, list);
                }
                list.Add(index);
            }
            entryCount += (int)(nx * nz);
        }

        private void AddCandidate(int index)
        {
            if (visited[index] == generation) return;
            visited[index] = generation;
            candidates[candidateCount++] = index;
        }

        /// <summary>
        /// Returns >= 0 candidate count, or -1 to request complete scanning.
        /// The query is an AABB BROAD PHASE; caller still checks exact radial
        /// hysteresis and predicted distances before admitting any scene load.
        /// </summary>
        internal int Query(float x, float z, float radius)
        {
            if (fullScan || radius < 0f) return -1;
            int x0, x1, z0, z1;
            if (!ToRange((double)x - radius, (double)x + radius, out x0, out x1) ||
                !ToRange((double)z - radius, (double)z + radius, out z0, out z1))
                return -1;
            long nx = (long)x1 - x0 + 1;
            long nz = (long)z1 - z0 + 1;
            if (nx * nz > MaxQueryBuckets) return -1;

            if (generation == int.MaxValue)
            {
                Array.Clear(visited, 0, visited.Length);
                generation = 1;
            }
            else generation++;
            candidateCount = 0;
            for (int xBin = x0; xBin <= x1; xBin++)
            for (int zBin = z0; zBin <= z1; zBin++)
            {
                System.Collections.Generic.List<int> list;
                if (!buckets.TryGetValue(BucketKey(xBin, zBin), out list))
                    continue;
                for (int i = 0; i < list.Count; i++)
                    AddCandidate(list[i]);
            }
            // Extremely large authored cells are not replicated across a huge
            // number of hash buckets. Their bounds are queried exactly.
            for (int i = 0; i < largeCells.Count; i++)
            {
                LargeCell cell = largeCells[i];
                if (Math.Abs((double)x - cell.x) <= (double)radius + cell.halfX &&
                    Math.Abs((double)z - cell.z) <= (double)radius + cell.halfZ)
                    AddCandidate(cell.index);
            }
            return candidateCount;
        }

        internal int CandidateAt(int ordinal) { return candidates[ordinal]; }
    }

}
