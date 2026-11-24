// Controlled C# substitute: runner injects the REAL production probe body.
// Unity/PhysX/GPU timings and native scene compilation are not simulated.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x=x; this.y=y; this.z=z; }
        public static Vector3 right => new Vector3(1, 0, 0);
        public static Vector3 up => new Vector3(0, 1, 0);
        public static Vector3 forward => new Vector3(0, 0, 1);
        public static Vector3 operator *(Vector3 v, float k) => new Vector3(v.x*k, v.y*k, v.z*k);
        public static Vector3 operator *(float k, Vector3 v) => v*k;
    }
    public struct Quaternion
    {
        public float x => 0f;
        public float z => 0f;
        public float y, w;
        public static Quaternion identity => new Quaternion { w=1 };
        public static Quaternion Yaw(double angle) =>
            new Quaternion { y=(float)Math.Sin(angle/2), w=(float)Math.Cos(angle/2) };
        public static Vector3 operator *(Quaternion q, Vector3 v)
        {
            float cos = 1f - 2f * q.y * q.y;
            float sin = 2f * q.w * q.y;
            return new Vector3(cos*v.x+sin*v.z, v.y, -sin*v.x+cos*v.z);
        }
    }
    public struct Bounds
    {
        public Vector3 center, extents;
        public Bounds(Vector3 c, Vector3 size) { center=c; extents=size*0.5f; }
        public Vector3 min => new Vector3(center.x-extents.x,center.y-extents.y,center.z-extents.z);
        public Vector3 max => new Vector3(center.x+extents.x,center.y+extents.y,center.z+extents.z);
        public bool Intersects(Bounds b) =>
            Math.Abs(center.x-b.center.x) <= extents.x+b.extents.x &&
            Math.Abs(center.y-b.center.y) <= extents.y+b.extents.y &&
            Math.Abs(center.z-b.center.z) <= extents.z+b.extents.z;
    }
    public static class Mathf
    {
        public static float Abs(float f) => Math.Abs(f);
        public static int FloorToInt(float f) => (int)Math.Floor(f);
    }
    public sealed class Collider { public bool enabled; }
}

internal sealed class ProbeFixture
{
    internal bool isActiveAndEnabled = true, indexReady = true;
    internal Collider[] obstacles;
    internal bool[] streamed;
    internal Bounds[] worldBounds;
    internal float cellSize = 250f;
    internal int[] probeVisited;
    internal int probeQuery;
    internal readonly Dictionary<long,List<int>> cells = new Dictionary<long,List<int>>();
    internal readonly List<int> oversizedIndices = new List<int>();
    private const int MaxCellsPerProbe = /* PROBE_CAP */;
    private const int MaxCellsPerCollider = 64;

    public ProbeFixture(int count)
    {
        obstacles=new Collider[count]; streamed=new bool[count];
        worldBounds=new Bounds[count]; probeVisited=new int[count];
    }
    private int Cell(float value) => Mathf.FloorToInt(value / cellSize);
    private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;
    private static bool Finite(Vector3 value) { /* FINITE_BODY */ }

    internal void Add(int i, Bounds b, bool on, bool spatial)
    {
        obstacles[i]=new Collider { enabled=on };
        worldBounds[i]=b;
        streamed[i]=spatial;
        if (!spatial) return;
        int x0=Cell(b.min.x), x1=Cell(b.max.x);
        int z0=Cell(b.min.z), z1=Cell(b.max.z);
        long width=(long)x1-x0+1, depth=(long)z1-z0+1;
        if(width>MaxCellsPerCollider || depth>MaxCellsPerCollider ||
           width*depth>MaxCellsPerCollider)
        { oversizedIndices.Add(i); return; }
        for (int x=x0; x<=x1; x++)
          for(int z=z0; z<=z1; z++)
          {
              long key=Key(x,z);
              if (!cells.TryGetValue(key,out List<int> list))
              { list=new List<int>();cells.Add(key,list); }
              list.Add(i);
          }
    }
    internal bool Probe(Vector3 center, Vector3 halfExtents, Quaternion rotation)
    { /* PROBE_BODY */ }
    internal bool Exhaustive(Vector3 center, Vector3 halfExtents, Quaternion rotation)
    {
        Vector3 right=rotation*(Vector3.right*halfExtents.x);
        Vector3 up=rotation*(Vector3.up*halfExtents.y);
        Vector3 forward=rotation*(Vector3.forward*halfExtents.z);
        Vector3 e=new Vector3(Math.Abs(right.x)+Math.Abs(up.x)+Math.Abs(forward.x),
            Math.Abs(right.y)+Math.Abs(up.y)+Math.Abs(forward.y),
            Math.Abs(right.z)+Math.Abs(up.z)+Math.Abs(forward.z));
        Bounds probe=new Bounds(center,2f*e);
        for (int i=0;i<obstacles.Length;i++)
           if (streamed[i] && worldBounds[i].Intersects(probe) &&
               obstacles[i]!=null && !obstacles[i].enabled)
               return true;
        return false;
    }
}
internal static class BoundedProbeTests
{
    static int checks;
    static void Check(bool ok,string why)
    { checks++; if(!ok) throw new Exception(why); }
    static float Range(Random r,float lo,float hi) =>
        lo+(float)r.NextDouble()*(hi-lo);
    public static void Main()
    {
        var rng=new Random(20261121);
        var fixture=new ProbeFixture(400);
        for (int i=0;i<fixture.obstacles.Length;i++)
        {
            float x=Range(rng,-4500,4500), z=Range(rng,-4500,4500);
            float hx=Range(rng,.1f,120f), hz=Range(rng,.1f,120f);
            if (i%71==0) { hx=3000f;hz=1200f; }
            var box=new Bounds(new Vector3(x,0,z),
                new Vector3(hx*2f,Range(rng,1f,20f),hz*2f));
            fixture.Add(i,box,i%4==0,i%19!=0);
        }
        for(int i=0;i<1500;i++)
        {
            var p=new Vector3(Range(rng,-4500,4500),0,Range(rng,-4500,4500));
            float r=Range(rng,.1f,400f);
            var half=new Vector3(r,Range(rng,.1f,5f),r*.7f);
            var rotation=Quaternion.Yaw(Range(rng,-3.14f,3.14f));
            Check(fixture.Probe(p,half,rotation)==fixture.Exhaustive(p,half,rotation),
                "small rotated vehicle/AI clearance differs from exhaustive");
        }
        // At 250 m/cell this covers > (6,400,000/250)^2 cells. An
        // unbounded nested grid walk would effectively hang the fixture.
        for(int i=0;i<150;i++)
        {
            float x=i%2==0?0f:30000000f;
            var p=new Vector3(x,0,0);
            var half=new Vector3(3200000f,15f,3200000f);
            Check(fixture.Probe(p,half,Quaternion.identity)==
                  fixture.Exhaustive(p,half,Quaternion.identity),
                "large probe missed disabled authored bounds");
        }
        Check(fixture.Probe(new Vector3(float.NaN,0,0),
            new Vector3(1,1,1),Quaternion.identity),
            "invalid probe should fail closed");
        Check(fixture.Probe(new Vector3(0,0,0),
            new Vector3(float.PositiveInfinity,1,1),Quaternion.identity),
            "infinite probe should fail closed");
        fixture.indexReady=false;
        Check(!fixture.Probe(new Vector3(0,0,0),
            new Vector3(1,1,1),Quaternion.identity),
            "pre-index probe must retain previous fail-solid behavior");
        Console.WriteLine("BOUNDED_COLLIDER_PROBE_PASS checks="+checks);
        Console.WriteLine("PRODUCTION_CSHARP_METHOD_TEST; no Unity Editor/Android FPS claim");
    }
}
