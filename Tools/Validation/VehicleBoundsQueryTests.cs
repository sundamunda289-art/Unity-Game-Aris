// Injected actual AarisVehicleWorld source, compiled with managed Unity stubs.
// Not a Unity Editor/IL2CPP build and not a device FPS or image-quality test.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x,y,z;
        public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}
        public static Vector3 operator +(Vector3 a,Vector3 b)=>
            new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);
        public static Vector3 operator -(Vector3 a,Vector3 b)=>
            new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);
        public static Vector3 operator *(Vector3 a,float f)=>
            new Vector3(a.x*f,a.y*f,a.z*f);
    }
    public struct Matrix4x4
    {
        public float m00,m01,m02,m10,m11,m12,m20,m21,m22;
        public static Matrix4x4 identity => new Matrix4x4{
            m00=1,m11=1,m22=1 };
        public static Matrix4x4 operator *(Matrix4x4 a,Matrix4x4 b)=>a;
        public Vector3 MultiplyPoint3x4(Vector3 p)=>p;
    }
    public struct Bounds
    {
        public Vector3 center,extents;
        public Bounds(Vector3 center,Vector3 size)
        {this.center=center;extents=size*.5f;}
        public Vector3 size=>extents*2f;
        public Vector3 min=>center-extents;
        public Vector3 max=>center+extents;
        public void Encapsulate(Bounds other)
        {
            Vector3 a=min,b=max,c=other.min,d=other.max;
            Vector3 lower=new Vector3(Math.Min(a.x,c.x),
                Math.Min(a.y,c.y),Math.Min(a.z,c.z));
            Vector3 upper=new Vector3(Math.Max(b.x,d.x),
                Math.Max(b.y,d.y),Math.Max(b.z,d.z));
            center=(lower+upper)*.5f;
            extents=(upper-lower)*.5f;
        }
    }
    public static class Mathf {public static float Abs(float n)=>Math.Abs(n);}
    public sealed class GameObject { public bool activeInHierarchy=true; }
    public sealed class Mesh
    {
        public int vertexCount=30;
        public Bounds bounds=new Bounds(new Vector3(0,0,0),new Vector3(2,2,2));
    }
    public sealed class MeshRenderer
    {
        public bool enabled=true,forceRenderingOff;
        public GameObject gameObject=new GameObject();
    }
    public sealed class MeshFilter
    {
        public Mesh sharedMesh;
        public Transform transform;
        public MeshRenderer visual;
        public T GetComponent<T>() where T:class=>visual as T;
    }
    public sealed class SkinnedMeshRenderer
    {
        public bool enabled=true,forceRenderingOff;
        public GameObject gameObject=new GameObject();
        public Mesh sharedMesh;
        public Bounds localBounds;
        public Transform transform;
    }
    public sealed class Transform
    {
        public Matrix4x4 worldToLocalMatrix=Matrix4x4.identity;
        public Matrix4x4 localToWorldMatrix=Matrix4x4.identity;
        public readonly List<MeshFilter> filters=new List<MeshFilter>();
        public readonly List<SkinnedMeshRenderer> skins=new List<SkinnedMeshRenderer>();
        public static int filterQueries,skinQueries;
        public bool throwOnSkin;
        public void GetComponentsInChildren<T>(bool includeInactive,List<T> results)
        {
            results.Clear();
            if(typeof(T)==typeof(MeshFilter))
            {
                filterQueries++;
                foreach(var item in filters)results.Add((T)(object)item);
            }
            else if(typeof(T)==typeof(SkinnedMeshRenderer))
            {
                skinQueries++;
                if(throwOnSkin)throw new InvalidOperationException("stub skin scan failed");
                foreach(var item in skins)results.Add((T)(object)item);
            }
            else throw new Exception("unexpected type");
        }
    }
}
namespace Aaris.MapReview
{
    public static class AarisVehicleWorld
    {
        /* PRODUCTION_BOUNDS_FIELDS */
        /* PRODUCTION_BOUNDS_METHOD */
        /* PRODUCTION_ACCUMULATE_METHOD */
        public static bool BuffersClear =>
            (visualBoundsFilters==null || visualBoundsFilters.Count==0) &&
            (visualBoundsSkins==null || visualBoundsSkins.Count==0);
    }
}
internal static class VehicleBoundsMethodTests
{
    private static int checks;
    private static void Check(bool ok,string why)
    {checks++;if(!ok)throw new Exception(why);}
    private static bool Near(float a,float b)=>Math.Abs(a-b)<.0001f;
    private static MeshFilter StaticMesh(Transform source,Vector3 center,
        Vector3 size,bool enabled=true)
    {
        var filter=new MeshFilter{
            sharedMesh=new Mesh{bounds=new Bounds(center,size)},
            transform=source,
            visual=new MeshRenderer{enabled=enabled}
        };
        source.filters.Add(filter);
        return filter;
    }
    public static void Main()
    {
        var source=new Transform();
        var f=StaticMesh(source,new Vector3(0,0,0),new Vector3(2,2,2));
        StaticMesh(source,new Vector3(999,0,0),new Vector3(180,80,80),false);
        var skin=new SkinnedMeshRenderer{
            sharedMesh=new Mesh(),enabled=true,
            gameObject=new GameObject(),transform=source,
            localBounds=new Bounds(new Vector3(5,0,0),new Vector3(2,2,2))
        };
        source.skins.Add(skin);
        source.skins.Add(new SkinnedMeshRenderer{
            sharedMesh=new Mesh(),enabled=false,
            gameObject=new GameObject(),transform=source,
            localBounds=new Bounds(new Vector3(700,0,0),new Vector3(200,2,2))
        });
        for(int i=0;i<80;i++)
        {
            Bounds bound;
            Check(Aaris.MapReview.AarisVehicleWorld.TryGetVisualBoundsLocal(
                source,source,out bound),"visible filter/skin lost");
            Check(Near(bound.center.x,2.5f)&&Near(bound.size.x,7f),
                  "visible projected geometry inflated or omitted");
            Check(Near(bound.size.y,2f)&&Near(bound.size.z,2f),
                  "mesh axes or authored skin bounds changed");
            Check(Aaris.MapReview.AarisVehicleWorld.BuffersClear,
                  "component references survived a bounds query");
        }
        Check(Transform.filterQueries==80&&Transform.skinQueries==80,
              "unexpected repeated component searches");
        source.throwOnSkin=true;
        bool threw=false;
        try {
            Bounds bad;
            Aaris.MapReview.AarisVehicleWorld.TryGetVisualBoundsLocal(
                source,source,out bad);
        }
        catch(InvalidOperationException) {threw=true;}
        Check(threw,"failed query did not propagate");
        Check(Aaris.MapReview.AarisVehicleWorld.BuffersClear,
              "exception retained imported mesh references");
        source.throwOnSkin=false;
        f.sharedMesh.vertexCount=0;
        skin.enabled=false;
        Bounds hidden;
        Check(!Aaris.MapReview.AarisVehicleWorld.TryGetVisualBoundsLocal(
            source,source,out hidden),"invisible geometry reported as rendered");
        Check(Aaris.MapReview.AarisVehicleWorld.BuffersClear,
              "empty result left stale objects");
        Check(!Aaris.MapReview.AarisVehicleWorld.TryGetVisualBoundsLocal(
            null,source,out hidden),"invalid local axes accepted");
        f.sharedMesh.vertexCount=20;
        skin.enabled=true;
        Check(Aaris.MapReview.AarisVehicleWorld.TryGetVisualBoundsLocal(
            source,source,out hidden),"recovery after exception failed");
        Check(Near(hidden.size.x,7f),"recovered bounds corrupted");
        Console.WriteLine("VEHICLE_BOUNDS_PRODUCTION_BODY_PASS checks="+checks);
        Console.WriteLine("Controlled Unity API stubs; native Editor and Adreno FPS not tested.");
    }
}
