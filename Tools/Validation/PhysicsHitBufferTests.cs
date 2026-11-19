// Runs the production query policy against a controlled Physics backend.
// This is a unit test, not native PhysX, a Unity scene or Android FPS proof.
using System;
using System.Collections.Generic;
using Aaris.MapReview;

namespace UnityEngine
{
    public struct Ray { public int token; }
    public struct RaycastHit { public int id; public float distance; }
    public enum QueryTriggerInteraction { UseGlobal, Ignore, Collide }
    public static class Physics
    {
        public static RaycastHit[] Source = new RaycastHit[0];
        public static int NonAllocCalls, AllCalls;
        public static bool ExpectSphere;
        private static void Validate(Ray ray, float radius, float distance,
            int mask, QueryTriggerInteraction trigger, bool sphere)
        {
            if (ray.token != 123 || distance != 17.25f || mask != 513 ||
                trigger != QueryTriggerInteraction.Ignore || sphere != ExpectSphere ||
                (sphere && radius != .17f)) throw new Exception("Query shape changed");
        }
        private static int Fill(RaycastHit[] output)
        {
            NonAllocCalls++;
            int count = Math.Min(Source.Length, output.Length);
            Array.Copy(Source, output, count);
            return count;
        }
        private static RaycastHit[] All()
        {
            AllCalls++;
            return (RaycastHit[])Source.Clone();
        }
        public static int RaycastNonAlloc(Ray ray, RaycastHit[] output,
            float distance, int mask, QueryTriggerInteraction trigger)
        { Validate(ray, 0, distance, mask, trigger, false); return Fill(output); }
        public static RaycastHit[] RaycastAll(Ray ray, float distance,
            int mask, QueryTriggerInteraction trigger)
        { Validate(ray, 0, distance, mask, trigger, false); return All(); }
        public static int SphereCastNonAlloc(Ray ray, float radius, RaycastHit[] output,
            float distance, int mask, QueryTriggerInteraction trigger)
        { Validate(ray, radius, distance, mask, trigger, true); return Fill(output); }
        public static RaycastHit[] SphereCastAll(Ray ray, float radius, float distance,
            int mask, QueryTriggerInteraction trigger)
        { Validate(ray, radius, distance, mask, trigger, true); return All(); }
    }
}

internal static class PhysicsHitBufferTests
{
    private static int checks;
    private static UnityEngine.RaycastHit[] Contacts(int count, Random random)
    {
        var hits = new UnityEngine.RaycastHit[count];
        for (int i = 0; i < count; i++)
            hits[i] = new UnityEngine.RaycastHit { id = i, distance = count - i + .25f };
        for (int i = count - 1; i > 0; i--)
        { int j = random.Next(i + 1); var t = hits[i]; hits[i] = hits[j]; hits[j] = t; }
        return hits;
    }
    private static int Query(bool sphere, ref UnityEngine.RaycastHit[] buffer,
        int limit, out UnityEngine.RaycastHit[] hits)
    {
        UnityEngine.Physics.ExpectSphere = sphere;
        return sphere
            ? AarisPhysicsHitBuffer.SphereCast(new UnityEngine.Ray { token = 123 },
                .17f, 17.25f, 513, ref buffer, limit, out hits)
            : AarisPhysicsHitBuffer.Raycast(new UnityEngine.Ray { token = 123 },
                17.25f, 513, ref buffer, limit, out hits);
    }
    private static void CheckComplete(UnityEngine.RaycastHit[] hits, int count)
    {
        var source = UnityEngine.Physics.Source;
        if (count != source.Length) throw new Exception("Incomplete physics query accepted");
        var expected = new HashSet<int>();
        foreach (var hit in source) expected.Add(hit.id);
        float actualNearest = float.PositiveInfinity, expectedNearest = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            if (!expected.Remove(hits[i].id)) throw new Exception("Missing or duplicate hit");
            actualNearest = Math.Min(actualNearest, hits[i].distance);
            expectedNearest = Math.Min(expectedNearest, source[i].distance);
        }
        if (expected.Count != 0 || actualNearest != expectedNearest)
            throw new Exception("Nearest hit or exhaustive contents changed");
        checks++;
    }
    private static void BoundariesAndReuse(bool sphere, int initial, int limit)
    {
        var random = new Random(20261119 + initial);
        foreach (int count in new[] { 0, 1, initial - 1, initial, initial + 1,
                                      limit - 1, limit, limit + 1, 1024 })
        {
            var buffer = new UnityEngine.RaycastHit[initial];
            var original = buffer;
            UnityEngine.Physics.Source = Contacts(count, random);
            UnityEngine.Physics.NonAllocCalls = UnityEngine.Physics.AllCalls = 0;
            UnityEngine.RaycastHit[] hits;
            int used = Query(sphere, ref buffer, limit, out hits);
            CheckComplete(hits, used);
            if (count < initial && !ReferenceEquals(buffer, original))
                throw new Exception("Sparse scene unnecessarily promoted");
            if (buffer.Length > limit) throw new Exception("Retention limit exceeded");
            if (UnityEngine.Physics.AllCalls != (count >= limit ? 1 : 0))
                throw new Exception("Exactly-full or over-cap fallback changed");
            // The next dense query goes straight into the retained buffer.
            var warm = buffer;
            UnityEngine.Physics.NonAllocCalls = UnityEngine.Physics.AllCalls = 0;
            used = Query(sphere, ref buffer, limit, out hits);
            CheckComplete(hits, used);
            if (!ReferenceEquals(buffer, warm) || UnityEngine.Physics.NonAllocCalls != 1)
                throw new Exception("Warm query repeated small-buffer retry or allocation");
            // Native queries can leave stale entries beyond their returned count.
            UnityEngine.Physics.Source = Contacts(0, random);
            used = Query(sphere, ref buffer, limit, out hits);
            CheckComplete(hits, used);
        }
        // 1,200 consecutive crowded frames/pellets must reuse the same array.
        var retained = new UnityEngine.RaycastHit[initial];
        UnityEngine.Physics.Source = Contacts(initial + 1, random);
        UnityEngine.RaycastHit[] complete;
        Query(sphere, ref retained, limit, out complete);
        var warmBuffer = retained;
        UnityEngine.Physics.NonAllocCalls = UnityEngine.Physics.AllCalls = 0;
        for (int i = 0; i < 1200; i++)
        {
            int n = Query(sphere, ref retained, limit, out complete);
            CheckComplete(complete, n);
            if (!ReferenceEquals(warmBuffer, retained) || !ReferenceEquals(complete, retained))
                throw new Exception("Warm path allocated a new result buffer");
        }
        if (UnityEngine.Physics.NonAllocCalls != 1200 || UnityEngine.Physics.AllCalls != 0)
            throw new Exception("Repeated queries regressed");
    }
    public static void Main()
    {
        BoundariesAndReuse(false, 12, 128); // Two feet share one owner buffer.
        BoundariesAndReuse(false, 48, 256); // Gun camera + muzzle + pellets.
        BoundariesAndReuse(false, 72, 256); // Aerial support.
        BoundariesAndReuse(true, 64, 256);  // Aerial camera.
        Console.WriteLine("PHYSICS_HIT_BUFFER_UNIT_PASS checks=" + checks);
        Console.WriteLine("WARM_QUERIES=4800 ALL_RESULT_ALLOCATIONS=0 after one promotion per caller");
        Console.WriteLine("Controlled backend only; native PhysX and device FPS remain untested.");
    }
}
