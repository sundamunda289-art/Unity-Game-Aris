// The runner injects actual production C# bodies into these controlled fixtures.
// This exercises policy and state transitions, not native Unity/PhysX or FPS.
using System;
using System.Collections.Generic;
using UnityEngine;

// The runner inserts production collider loop bodies. Record their calls to
// the shared frame gate without pretending to execute Unity/Android native work.
internal static class AarisMobileStreamingFrameGate
{
    internal static int Claimed;
    internal static void ResetClaims() { Claimed = 0; }
    internal static void ReservePhysicsStreamingFrame() { Claimed++; }
}

namespace UnityEngine
{
    public struct Vector2 { public float x, y; public Vector2(float a, float b) { x=a; y=b; } }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float a, float b, float c) { x=a; y=b; z=c; }
    }
    public struct Bounds
    {
        public Vector3 center, extents;
        public Bounds(Vector3 c, Vector3 e) { center=c; extents=e; }
        public Vector3 ClosestPoint(Vector3 p) => new Vector3(
            Math.Max(center.x-extents.x, Math.Min(center.x+extents.x,p.x)),
            Math.Max(center.y-extents.y, Math.Min(center.y+extents.y,p.y)),
            Math.Max(center.z-extents.z, Math.Min(center.z+extents.z,p.z)));
        public bool Intersects(Bounds b) =>
            Math.Abs(center.x-b.center.x) <= extents.x+b.extents.x &&
            Math.Abs(center.y-b.center.y) <= extents.y+b.extents.y &&
            Math.Abs(center.z-b.center.z) <= extents.z+b.extents.z;
    }
    public sealed class Collider
    {
        public bool state, destroyed;
        public int gets, sets;
        public bool enabled { get { gets++; if(destroyed) throw new Exception("Destroyed collider read"); return state; }
                              set { sets++; state=value; } }
        public static bool operator ==(Collider a, Collider b)
        {
            if(ReferenceEquals(a,b)) return true;
            if(ReferenceEquals(a,null)) return b.destroyed;
            if(ReferenceEquals(b,null)) return a.destroyed;
            return false;
        }
        public static bool operator !=(Collider a, Collider b) => !(a==b);
        public override bool Equals(object other) => ReferenceEquals(this,other);
        public override int GetHashCode() => base.GetHashCode();
    }
    public static class Mathf
    {
        public const float PI=(float)Math.PI;
        public static float Cos(float x)=>(float)Math.Cos(x);
        public static float Sin(float x)=>(float)Math.Sin(x);
    }
    public sealed class LineRenderer
    {
        public bool enabled;
        public int uploads;
        public Vector3[] points;
        public void SetPositions(Vector3[] p) { uploads++; points=(Vector3[])p.Clone(); }
    }
}

internal sealed class ColliderFixture
{
    internal Collider[] obstacles=new Collider[1];
    internal Bounds[] worldBounds=new Bounds[1];
    internal bool[] streamed=new bool[1];
    internal bool[] disabledByStreaming=new bool[1];
    private int query=1;
    private int[] visited=new int[1], wanted=new int[1];
    private List<int> nextActive=new List<int>();
    internal Vector3 lastPosition;
    internal bool Grid(float enterLimit,float retainLimit)
    {
        visited[0]=wanted[0]=0; nextActive.Clear();
        foreach(int i in new[]{0}) { /* GRID_BODY */ }
        return nextActive.Count==1 && wanted[0]==query;
    }
    internal bool Oversized(float enterLimit,float retainLimit)
    {
        visited[0]=wanted[0]=0; nextActive.Clear();
        foreach(int i in new[]{0}) { /* OVERSIZED_BODY */ }
        return nextActive.Count==1 && wanted[0]==query;
    }
    internal bool Probe(Collider c, Bounds probe)
    {
        int i=0;
        return /* PROBE_EXPRESSION */ false;
    }
}

internal sealed class BoundaryFixture
{
    internal LineRenderer boundary;
    private readonly Vector3[] boundaryPoints=new Vector3[65];
    private LineRenderer drawnBoundary;
    private Vector2 drawnBoundaryCenter;
    private float drawnBoundaryRadius;
    internal Vector2 center;
    internal float radius;
    internal int terrainCalls;
    private const float OceanCutoff=-37.35f;
    internal void Draw() { DrawBoundary(); }
    private bool TryGetGround(float x,float z,out float ground)
    { terrainCalls++; ground=x*.002f+z*.003f; return !(x>600f && z>600f); }
    /* DRAW_METHOD */
}

internal static class RuntimeNativeWorkTests
{
    private static int checks, skippedStateReads;
    private static void Check(bool ok,string message)
    { checks++; if(!ok) throw new Exception(message); }
    private static void Colliders()
    {
        var rng=new Random(20261119);
        for(int sample=0;sample<12000;sample++)
        {
            var bounds=new Bounds(new Vector3((float)(rng.NextDouble()*600-300),
                (float)(rng.NextDouble()*80-40),(float)(rng.NextDouble()*600-300)),
                new Vector3((float)(rng.NextDouble()*80),20,(float)(rng.NextDouble()*80)));
            var player=new Vector3((float)(rng.NextDouble()*120-60),0,
                (float)(rng.NextDouble()*120-60));
            foreach(bool oversized in new[]{false,true})
                foreach(int mode in new[]{0,1,2,3}) // null, destroyed, off, on
                {
                    Collider c=mode==0 ? null : new Collider { destroyed=mode==1,state=mode==3 };
                    var f=new ColliderFixture { lastPosition=player };
                    f.obstacles[0]=c; f.worldBounds[0]=bounds;
                    f.disabledByStreaming[0]=mode==2;
                    Vector3 q=bounds.ClosestPoint(player);
                    float dx=q.x-player.x, dz=q.z-player.z;
                    float d2=dx*dx+dz*dz;
                    bool expected=mode>=2 && d2 <= (mode==3 ? 61f*61f : 55f*55f);
                    AarisMobileStreamingFrameGate.ResetClaims();
                    bool accepted=oversized ? f.Oversized(55f*55f,61f*61f) :
                                             f.Grid(55f*55f,61f*61f);
                    Check(accepted==expected,"Activation/hysteresis changed");
                    // A no-op scan must not claim the optional streamer's
                    // frame. Exactly one originally-off solid entering range
                    // should reserve native physics before it is enabled.
                    int expectedClaims=mode==2 && expected ? 1 : 0;
                    Check(AarisMobileStreamingFrameGate.Claimed==expectedClaims,
                          "Collider change/idle frame admission mismatch");
                    if(mode>=2)
                    {
                        Check(c.state==(expected || mode==3),"Collider state changed incorrectly");
                        Check(f.disabledByStreaming[0]==(mode==2 && !expected),
                            "Reactivated cover is no longer disabled by streaming");
                        if(d2>61f*61f) { Check(c.gets==0 && c.sets==0,"Far native state read"); skippedStateReads++; }
                    }
                    foreach(bool managed in new[]{false,true})
                    {
                        f.streamed[0]=managed;
                        if(mode!=0) { c.gets=c.sets=0; }
                        var probe=new Bounds(player,new Vector3(1,1,1));
                        bool intersects=bounds.Intersects(probe);
                        bool blocked=managed && intersects && mode>=2 && !c.state;
                        Check(f.Probe(c,probe)==blocked,"Inactive-cover decision changed");
                        if(mode>=2 && !intersects) Check(c.gets==0,"Nonintersecting native state read");
                    }
                }
        }
        // Exact hysteresis boundaries and elevated/long structures.
        foreach(float distance in new[]{0f,55f,55.001f,61f,61.001f})
            foreach(bool active in new[]{false,true})
            {
                var f=new ColliderFixture();
                f.obstacles[0]=new Collider { state=active };
                f.disabledByStreaming[0]=!active;
                f.worldBounds[0]=new Bounds(new Vector3(distance,200,0),new Vector3(0,1,0));
                Check(f.Grid(55f*55f,61f*61f)==(distance<=(active?61f:55f)),"Boundary changed");
            }
        var externallyDisabled = new ColliderFixture();
        externallyDisabled.obstacles[0] = new Collider { state=false };
        AarisMobileStreamingFrameGate.ResetClaims();
        Check(!externallyDisabled.Grid(55f*55f, 61f*61f) &&
            !externallyDisabled.obstacles[0].state &&
            AarisMobileStreamingFrameGate.Claimed == 0,
            "Streaming must not resurrect an intentionally disabled collider");
    }
    private static void Ring()
    {
        var f=new BoundaryFixture { boundary=new LineRenderer(),center=new Vector2(10,-30),radius=400 };
        f.Draw();
        Check(f.boundary.uploads==1 && f.terrainCalls==65,"Initial ring incomplete");
        for(int i=0;i<65;i++)
        {
            Vector3 p=f.boundary.points[i];
            double angle=i*Math.PI*2/64;
            Check(Math.Abs(p.x-(10+Math.Cos(angle)*400))<.001,"Ring X changed");
            Check(Math.Abs(p.z-(-30+Math.Sin(angle)*400))<.001,"Ring Z changed");
            Check(Math.Abs(p.y-(p.x*.002f+p.z*.003f+1.2f))<.00001,"Ring height changed");
        }
        for(int i=0;i<1000;i++) f.Draw();
        Check(f.terrainCalls==65 && f.boundary.uploads==1,"Stationary ring repeated native work");
        f.boundary.enabled=false; f.Draw();
        Check(f.boundary.enabled && f.terrainCalls==65,"Cached ring did not restore visibility");
        f.center=new Vector2(10.000001f,-30); f.Draw();
        Check(f.terrainCalls==130 && f.boundary.uploads==2,"Small real movement was suppressed");
        f.radius=500; f.Draw();
        Check(f.terrainCalls==195 && f.boundary.uploads==3,"Radius change not drawn");
        f.radius=1000; f.Draw();
        Check(!f.boundary.enabled && f.terrainCalls==195,"Out-of-map ring did work");
        f.radius=500; f.Draw();
        Check(f.boundary.enabled && f.terrainCalls==195,"Hidden unchanged ring did not reuse points");
        f.boundary=new LineRenderer(); f.Draw();
        Check(f.boundary.uploads==1 && f.terrainCalls==260,"Replacement renderer reused stale cache");
        f.boundary=null; f.Draw();
        Check(f.terrainCalls==260,"Missing renderer sampled terrain");
    }
    public static void Main()
    {
        Colliders(); Ring();
        Console.WriteLine("RUNTIME_NATIVE_WORK_UNIT_PASS checks="+checks);
        Console.WriteLine("FAR_CANDIDATES_WITH_ZERO_STATE_READS="+skippedStateReads);
        Console.WriteLine("STATIONARY_RING_REPEATS=1000 EXTRA_TERRAIN_SAMPLES=0 EXTRA_UPLOADS=0");
        Console.WriteLine("Controlled API fixtures; no native Unity/PhysX or Android FPS claim.");
    }
}
