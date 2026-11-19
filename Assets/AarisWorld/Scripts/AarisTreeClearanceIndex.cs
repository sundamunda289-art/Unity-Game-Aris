using System;
using System.Collections.Generic;

namespace Aaris.MapReview
{
    // Temporary CPU-only broadphase for settlement placement, not a renderer
    // or a TerrainData edit. Each tree is retained in exactly one world cell.
    internal sealed class AarisTreeClearanceIndex
    {
        private const float CellSize = 32f;
        internal const float MaximumMargin = 3.8f;

        private readonly struct Sample
        {
            internal readonly float x, z, margin;
            internal Sample(float x, float z, float margin)
            { this.x = x; this.z = z; this.margin = margin; }
        }

        private readonly Dictionary<long, List<Sample>> cells =
            new Dictionary<long, List<Sample>>();

        private static int Cell(float position) => (int)Math.Floor(position / CellSize);
        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        internal void Add(float x, float z, float margin)
        {
            // Invalid source coordinates cannot satisfy the original exact
            // predicate either. Never turn NaN into a valid grid coordinate.
            if (float.IsNaN(x) || float.IsInfinity(x) ||
                float.IsNaN(z) || float.IsInfinity(z) ||
                float.IsNaN(margin) || float.IsInfinity(margin)) return;
            long key = Key(Cell(x), Cell(z));
            if (!cells.TryGetValue(key, out List<Sample> entries))
            { entries = new List<Sample>(4); cells.Add(key, entries); }
            // The caller uses the same 0.5..3.8 m native tree-width margin.
            entries.Add(new Sample(x, z, margin));
        }

        internal bool Blocks(float x, float z, float halfX, float halfZ)
        {
            // Expand by the largest authored tree margin, then one guard cell
            // for float rounding at cell edges. Keep the original strict <
            // test below: broadphase candidates never become false obstacles.
            int firstX = Cell(x - halfX - MaximumMargin) - 1;
            int lastX = Cell(x + halfX + MaximumMargin) + 1;
            int firstZ = Cell(z - halfZ - MaximumMargin) - 1;
            int lastZ = Cell(z + halfZ + MaximumMargin) + 1;
            for (int cx = firstX; cx <= lastX; cx++)
                for (int cz = firstZ; cz <= lastZ; cz++)
                {
                    if (!cells.TryGetValue(Key(cx, cz), out List<Sample> trees)) continue;
                    for (int i = 0; i < trees.Count; i++)
                    {
                        Sample tree = trees[i];
                        if (Math.Abs(tree.x - x) < halfX + tree.margin &&
                            Math.Abs(tree.z - z) < halfZ + tree.margin)
                            return true;
                    }
                }
            return false;
        }
    }
}
