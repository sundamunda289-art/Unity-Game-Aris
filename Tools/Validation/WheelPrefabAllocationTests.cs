// The runner injects the complete production AttachWheelModels method.
// Managed Unity API stubs only; no native Unity import or physical Android FPS.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityEngine
{
    public sealed class Material { public string name; }
    public sealed class Collider { public bool enabled = true; }
    public sealed class Renderer
    {
        public int submeshes;
        public Material[] sharedMaterials;
    }
    public sealed class WheelCollider { }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x,float y,float z) { this.x=x;this.y=y;this.z=z; }
        public static Vector3 operator -(Vector3 a,Vector3 b) =>
            new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);
        public static Vector3 operator *(Vector3 v,float n) =>
            new Vector3(v.x*n,v.y*n,v.z*n);
        public static Vector3 operator *(Quaternion q,Vector3 v) => v;
    }
    public struct Quaternion
    {
        public static Quaternion identity => new Quaternion();
        public static Quaternion Inverse(Quaternion r) => r;
    }
    public struct Bounds { public Vector3 center, size; }
    public static class Mathf
    {
        public static float Max(float a,float b) => Math.Max(a,b);
        public static float Clamp(float x,float lo,float hi) => Math.Max(lo,Math.Min(hi,x));
    }
    public sealed class Transform
    {
        public Vector3 position, localPosition;
        public Quaternion rotation;
        public Vector3 localScale = new Vector3(1,1,1);
        public bool renderable = true, boundsValid = true;
        public Bounds bounds = new Bounds { center=new Vector3(0,0,0),
            size=new Vector3(.7f,.7f,.7f) };
        public Vector3 TransformPoint(Vector3 point) => point;
    }
    public sealed class GameObject
    {
        public string name;
        public Transform transform = new Transform();
        public readonly List<Collider> colliders = new List<Collider>();
        public readonly List<Renderer> renderers = new List<Renderer>();
        public static int listQueries;
        public void GetComponentsInChildren<T>(bool inactive,List<T> results)
        {
            listQueries++;
            results.Clear();
            if(typeof(T)==typeof(Collider))
            {
                foreach(var c in colliders) results.Add((T)(object)c);
            }
            else if(typeof(T)==typeof(Renderer))
            {
                foreach(var r in renderers) results.Add((T)(object)r);
            }
            else throw new Exception("Unexpected component query: "+typeof(T));
        }
    }
}
namespace Aaris.MapReview
{
    public static class AarisVehicleWorld
    {
        public static int VisualSubmeshCount(Renderer renderer) =>
            renderer.submeshes;
        public static bool HasRenderableWheelGeometry(Transform t) =>
            t.renderable;
        public static bool TryGetVisualBoundsLocal(Transform t,out Bounds b)
        { b=t.bounds;return t.boundsValid; }
    }

    public sealed class WheelFixture
    {
        private const float WheelRadius=.39f;
        private readonly WheelCollider[] wheels = new WheelCollider[4];
        private readonly List<Collider> wheelPrefabColliders =
            new List<Collider>(8);
        private readonly List<Renderer> wheelPrefabRenderers =
            new List<Renderer>(8);
        private readonly Dictionary<int,Material[]> wheelPrefabSlotLayouts =
            new Dictionary<int,Material[]>(4);
        private Material wheelPrefabLayoutPalette;
        private readonly Transform[] visualWheels=new Transform[4];
        private readonly Quaternion[] visualWheelRotationOffsets=
            new Quaternion[4];
        private readonly Vector3[] visualWheelCenterOffsets=new Vector3[4];
        public readonly Transform transform=new Transform();
        public readonly List<GameObject> instances=new List<GameObject>();
        public int syncCalls, destroys;

        private GameObject Instantiate(GameObject source,Transform parent,bool world)
        {
            var clone = new GameObject();
            clone.transform.renderable=source.transform.renderable;
            clone.transform.boundsValid=source.transform.boundsValid;
            clone.transform.bounds=source.transform.bounds;
            foreach (var c in source.colliders)
                clone.colliders.Add(new Collider { enabled=c.enabled });
            foreach (var r in source.renderers)
                clone.renderers.Add(new Renderer { submeshes=r.submeshes,
                    sharedMaterials=r.sharedMaterials });
            instances.Add(clone);
            return clone;
        }
        private void Destroy(GameObject item) { destroys++; }
        private void SyncVisibleWheels() { syncCalls++; }

        /* PRODUCTION_ATTACH_WHEEL_MODELS */

        public int CachedLayoutCount => wheelPrefabSlotLayouts.Count;
        public int ColliderScratchCount => wheelPrefabColliders.Count;
        public int RendererScratchCount => wheelPrefabRenderers.Count;
    }
}

internal static class WheelSpawnAllocationTests
{
    static int checks;
    static void Check(bool ok,string why)
    { checks++;if(!ok)throw new Exception(why); }

    static GameObject Source(Material source)
    {
        var wheel=new GameObject();
        wheel.colliders.Add(new Collider());
        wheel.colliders.Add(new Collider());
        wheel.renderers.Add(new Renderer { submeshes=3,sharedMaterials=new [] {source,source,source} });
        wheel.renderers.Add(new Renderer { submeshes=2,sharedMaterials=new [] {source,source} });
        return wheel;
    }
    public static void Main()
    {
        var paletteA=new Material { name="A" };
        var paletteB=new Material { name="B" };
        var source=Source(paletteA);
        var fixture=new Aaris.MapReview.WheelFixture();
        Check(fixture.AttachWheelModels(source,paletteA),"4-wheel fallback rejected");
        Check(fixture.instances.Count==4 && fixture.syncCalls==1,
              "fallback did not build exactly four wheels");
        Check(fixture.CachedLayoutCount==2,"identical submesh slots were not cached");
        Check(fixture.ColliderScratchCount==0 && fixture.RendererScratchCount==0,
              "live references retained after successful wheel integration");
        Material[] three=fixture.instances[0].renderers[0].sharedMaterials;
        Material[] two=fixture.instances[0].renderers[1].sharedMaterials;
        foreach(var wheel in fixture.instances)
        {
            Check(wheel.colliders.TrueForAll(c=>!c.enabled),
                  "imported wheel collider stayed enabled");
            Check(Object.ReferenceEquals(wheel.renderers[0].sharedMaterials,three) &&
                  Object.ReferenceEquals(wheel.renderers[1].sharedMaterials,two),
                  "identical palette layouts are not reused");
            foreach(var renderer in wheel.renderers)
                foreach(var material in renderer.sharedMaterials)
                    Check(Object.ReferenceEquals(material,paletteA),
                          "fallback paint must preserve original palette");
        }
        Check(GameObject.listQueries==8,
              "each wheel must query colliders and renderers once into lists");

        // The explicit reattachment case must change every material slot,
        // without borrowing palette arrays from a previous vehicle/rebind.
        Check(fixture.AttachWheelModels(source,paletteB),"palette switch failed");
        Check(fixture.CachedLayoutCount==2,"rebind cache topology changed");
        Check(!Object.ReferenceEquals(
              fixture.instances[4].renderers[0].sharedMaterials,three),
              "new palette reused stale original material layout");
        foreach(var renderer in fixture.instances[4].renderers)
            foreach(var material in renderer.sharedMaterials)
                Check(Object.ReferenceEquals(material,paletteB),"new palette not applied");
        Check(fixture.ColliderScratchCount==0 && fixture.RendererScratchCount==0,
              "rebound wheel handles retained");

        // Authored URP material wheels keep their own slots and no forced paint.
        var authored=new Aaris.MapReview.WheelFixture();
        Check(authored.AttachWheelModels(source,null,true),"authored wheel path rejected");
        Check(authored.CachedLayoutCount==0,"authored paint path incorrectly cached");
        foreach(var renderer in authored.instances[0].renderers)
            foreach(var mat in renderer.sharedMaterials)
                Check(Object.ReferenceEquals(mat,paletteA),"authored wheel paint overwritten");

        // Invalid wheel mesh must exit without retaining the source components.
        var invalid=Source(paletteA);
        invalid.transform.renderable=false;
        var rejected=new Aaris.MapReview.WheelFixture();
        Check(!rejected.AttachWheelModels(invalid,paletteA),"missing mesh accepted");
        Check(rejected.destroys==1,"invalid hierarchy was not destroyed");
        Check(rejected.ColliderScratchCount==0 && rejected.RendererScratchCount==0,
              "rejected wheel handles leaked into later builds");
        Check(rejected.AttachWheelModels(source,paletteA),"retry after rejected source failed");
        Console.WriteLine("WHEEL_PREFAB_PRODUCTION_BODY_PASS checks="+checks);
        Console.WriteLine("Managed Unity API stub. Native Unity/IL2CPP and handset FPS untested.");
    }
}
