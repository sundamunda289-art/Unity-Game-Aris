// Compile together with the production AarisTreeClearanceIndex.cs, outside
// Assets. This executes the real index against an independent brute-force oracle.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Aaris.MapReview;

internal static class TreeClearanceIndexTests
{
    private struct Tree
    {
        internal float x, z, margin;
        internal Tree(float x, float z, float margin)
        { this.x = x; this.z = z; this.margin = margin; }
    }

    private static int checks;
    private static bool Brute(List<Tree> trees, float x, float z, float hx, float hz)
    {
        foreach (Tree tree in trees)
            if (Math.Abs(tree.x - x) < hx + tree.margin &&
                Math.Abs(tree.z - z) < hz + tree.margin) return true;
        return false;
    }

    private static void Equal(AarisTreeClearanceIndex index, List<Tree> trees,
        float x, float z, float hx, float hz)
    {
        bool expected = Brute(trees, x, z, hx, hz);
        bool actual = index.Blocks(x, z, hx, hz);
        if (actual != expected)
            throw new Exception("Clearance mismatch at " + x + "," + z);
        checks++;
    }

    private static void Insert(AarisTreeClearanceIndex index, List<Tree> trees,
        float x, float z, float margin)
    {
        trees.Add(new Tree(x, z, margin));
        index.Add(x, z, margin);
    }

    private static void Boundaries()
    {
        var empty = new AarisTreeClearanceIndex();
        Equal(empty, new List<Tree>(), 0, 0, 20, 20);
        // Negative cells, world/tile seams, yaw-projected extents, strict
        // touching edges, and tree margins that reach into adjacent cells.
        foreach (float edge in new[] { -1000f, -64f, -32f, 0f, 32f, 64f, 1000f })
            foreach (float margin in new[] { .5f, 1.5f, 3.8f })
            {
                var trees = new List<Tree>();
                var index = new AarisTreeClearanceIndex();
                Insert(index, trees, edge, -edge, margin);
                foreach (float hx in new[] { 3f, 10f, 37.35534f })
                    foreach (float delta in new[] { -.0001f, 0f, .0001f })
                        foreach (int sign in new[] { -1, 1 })
                        {
                            Equal(index, trees, edge + sign * (hx + margin + delta),
                                -edge, hx, 10f);
                            Equal(index, trees, edge, -edge + sign * (hx + margin + delta),
                                10f, hx);
                        }
                if (index.Blocks(edge + 4f, -edge, 3.5f, 4f) !=
                    Brute(trees, edge + 4f, -edge, 3.5f, 4f))
                    throw new Exception("Touching edge changed");
            }
        // Invalid samples did not block anything in the previous predicate.
        var invalid = new AarisTreeClearanceIndex();
        var bad = new List<Tree>();
        Insert(invalid, bad, float.NaN, 0, 1);
        Insert(invalid, bad, 0, float.PositiveInfinity, 1);
        Insert(invalid, bad, 0, 0, float.NaN);
        Equal(invalid, bad, 0, 0, 20, 20);
    }

    private static void ForestParityAndWork()
    {
        var rng = new Random(20261119);
        var index = new AarisTreeClearanceIndex();
        var trees = new List<Tree>();
        for (int i = 0; i < 7500; i++)
            Insert(index, trees, (float)(rng.NextDouble() * 2000 - 1000),
                (float)(rng.NextDouble() * 2000 - 1000),
                (float)(.5 + rng.NextDouble() * 3.3));
        // Trees exactly on seams are not assigned to the wrong world cell.
        Insert(index, trees, 0, -32, 3.8f);
        Insert(index, trees, -32, 0, .5f);
        Insert(index, trees, 0, 0, 1.5f);
        for (int i = 0; i < 20000; i++)
        {
            float x = (float)(rng.NextDouble() * 1930 - 965);
            float z = (float)(rng.NextDouble() * 1930 - 965);
            double yaw = rng.NextDouble() * Math.PI * 2;
            float width = (float)(2 + rng.NextDouble() * 48);
            float depth = (float)(2 + rng.NextDouble() * 48);
            float cos = (float)Math.Abs(Math.Cos(yaw));
            float sin = (float)Math.Abs(Math.Sin(yaw));
            float hx = (width * cos + depth * sin) * .5f + 2f;
            float hz = (width * sin + depth * cos) * .5f + 2f;
            Equal(index, trees, x, z, hx, hz);
            if (i % 20 == 0)
                foreach (float shift in new[] { -26f, -18f, 18f, 26f })
                    Equal(index, trees, x + shift, z - shift, hx, hz);
        }
        // Inspect the real data structure: every source sample occurs once.
        // The sparse-world test also establishes fewer candidate comparisons,
        // independently of noisy wall-clock benchmark results on a shared VM.
        var cells = (IDictionary)typeof(AarisTreeClearanceIndex)
            .GetField("cells", BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(index);
        int stored = 0, largestCell = 0;
        foreach (IList bucket in cells.Values)
        { stored += bucket.Count; largestCell = Math.Max(largestCell, bucket.Count); }
        if (stored != trees.Count) throw new Exception("Tree snapshot lost samples");
        // Largest footprint queries at most 6x6 cells including guards.
        if (largestCell * 36 >= trees.Count)
            throw new Exception("Sparse forest broadphase did not reduce candidate work");

        int expectedHits = 0, actualHits = 0;
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 5000; i++)
            if (Brute(trees, (i * 37 % 1930) - 965, (i * 61 % 1930) - 965, 5, 5))
                expectedHits++;
        double bruteMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        for (int i = 0; i < 5000; i++)
            if (index.Blocks((i * 37 % 1930) - 965, (i * 61 % 1930) - 965, 5, 5))
                actualHits++;
        double gridMs = clock.Elapsed.TotalMilliseconds;
        if (expectedHits != actualHits) throw new Exception("Benchmark predicates diverged");
        Console.WriteLine("FOREST_SAMPLES=" + stored + " CELLS=" + cells.Count +
            " MAX_BUCKET=" + largestCell);
        Console.WriteLine("CPU_SYNTHETIC_BENCH brute_ms=" + bruteMs.ToString("F2") +
            " grid_ms=" + gridMs.ToString("F2") + " queries=5000");
    }

    public static void Main()
    {
        Boundaries();
        ForestParityAndWork();
        Console.WriteLine("TREE_CLEARANCE_PRODUCTION_CS_PASS comparisons=" + checks);
        Console.WriteLine("Algorithm evidence only; not Unity Play Mode or Android FPS.");
    }
}
