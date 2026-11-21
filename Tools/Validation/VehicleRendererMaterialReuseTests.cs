// Inject actual car wheel visibility and authored URP validation methods.
// Managed Mono Unity API stubs. Not native Unity or Android performance proof.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityEngine
{
    public sealed class Mesh { public int vertexCount=300; }
    public sealed class MeshFilter { public Mesh sharedMesh=new Mesh(); }
    public sealed class Shader { public string name; }
    public sealed class Material
    {
        public Shader shader;
        public bool allProperties=true;
        public bool HasProperty(string key) => allProperties &&
           (key=="_BaseColor" || key=="_Metallic" || key=="_Smoothness");
    }
    public class GameObject
    {
        public bool activeInHierarchy=true;
        public bool throwQuery;
        public readonly List<Renderer> children = new List<Renderer>();
        public static readonly HashSet<object> usedResultLists = new HashSet<object>();
        public static int queryCount;
        public void GetComponentsInChildren<T>(bool includeInactive,List<T> output)
        {
            if(typeof(T)!=typeof(Renderer)) throw new Exception("bad component query");
            queryCount++;
            usedResultLists.Add(output);
            output.Clear();
            if(throwQuery)throw new InvalidOperationException("native renderer query failed");
            foreach(var item in children)output.Add((T)(object)item);
        }
    }
    public sealed class Transform
    {
        public GameObject gameObject;
        public void GetComponentsInChildren<T>(bool includeInactive,List<T> output)
        { gameObject.GetComponentsInChildren(includeInactive,output); }
    }
    public class Renderer
    {
        public bool enabled=true,forceRenderingOff,throwMaterials;
        public GameObject gameObject;
        public readonly List<Material> originalShared=new List<Material>();
        public static readonly HashSet<object> materialsLists = new HashSet<object>();
        public static int materialReads;
        public void GetSharedMaterials(List<Material> output)
        {
            materialReads++;
            materialsLists.Add(output);
            output.Clear();
            if(throwMaterials)throw new InvalidOperationException("shared materials failed");
            output.AddRange(originalShared);
        }
        public T GetComponent<T>() where T:class=>attachedFilter as T;
        public MeshFilter attachedFilter;
    }
    public sealed class MeshRenderer:Renderer {}
    public sealed class SkinnedMeshRenderer:Renderer
    { public Mesh sharedMesh; }
}
namespace Aaris.MapReview
{
    internal static class AarisVehicleWorld
    {
        /* ACTUAL_SCRATCH_FIELDS */
        /* ACTUAL_RENDERABLE_BODY */
        /* ACTUAL_VISIBLE_WHEEL */
        /* ACTUAL_URP_COMPATIBILITY */

        internal static bool Visible(Transform wheel)=>HasRenderableWheelGeometry(wheel);
        internal static bool Urp(GameObject model)=>HasCompatibleUrpMaterials(model);
        internal static bool ScratchClean =>
            (validationRenderers==null || validationRenderers.Count==0) &&
            (validationMaterials==null || validationMaterials.Count==0);
    }
}
internal static class ImportedCarRendererTests
{
    static int checks;
    static void Check(bool ok,string message)
    {checks++;if(!ok)throw new Exception(message);}
    static Material UrpMaterial()=>
        new Material{shader=new Shader{name="Universal Render Pipeline/Lit"}};
    static MeshRenderer VisibleMesh(GameObject body)
    {
        var r=new MeshRenderer {gameObject=body,attachedFilter=new MeshFilter()};
        r.originalShared.Add(UrpMaterial());
        r.originalShared.Add(UrpMaterial());
        body.children.Add(r);
        return r;
    }
    public static void Main()
    {
        var body=new GameObject();
        var wheel=new Transform{gameObject=body};
        var active=VisibleMesh(body);
        for(int i=0;i<150;i++)
        {
            Check(Aaris.MapReview.AarisVehicleWorld.Visible(wheel),
                "valid wheel mesh rejected");
            Check(Aaris.MapReview.AarisVehicleWorld.Urp(body),
                "valid multi-slot URP paint rejected");
            Check(Aaris.MapReview.AarisVehicleWorld.ScratchClean,
                "car model handles retained after validation");
        }
        Check(GameObject.usedResultLists.Count==1,
            "wheel and body validators allocated distinct query lists");
        Check(Renderer.materialsLists.Count==1,
            "URP validator allocated fresh material list per call");
        active.enabled=false;
        Check(!Aaris.MapReview.AarisVehicleWorld.Visible(wheel),
            "disabled mesh incorrectly accepted");
        active.enabled=true;active.forceRenderingOff=true;
        Check(!Aaris.MapReview.AarisVehicleWorld.Visible(wheel),
            "forced-off mesh incorrectly accepted");
        active.forceRenderingOff=false;active.attachedFilter.sharedMesh.vertexCount=0;
        Check(!Aaris.MapReview.AarisVehicleWorld.Visible(wheel),
            "mesh with no vertices incorrectly accepted");
        active.attachedFilter.sharedMesh.vertexCount=300;
        var skin=new SkinnedMeshRenderer{gameObject=body,
            sharedMesh=new Mesh{vertexCount=50}};
        skin.originalShared.Add(UrpMaterial());
        body.children.Add(skin);
        active.enabled=false;
        Check(Aaris.MapReview.AarisVehicleWorld.Visible(wheel),
            "valid skinned mesh rejected");
        active.enabled=true;
        Check(Aaris.MapReview.AarisVehicleWorld.Urp(body),
            "valid mixed mesh/skin slots rejected");
        skin.originalShared[0].shader.name="Legacy";
        Check(!Aaris.MapReview.AarisVehicleWorld.Urp(body),
            "non URP shader incorrectly accepted");
        skin.originalShared[0].shader.name="Universal Render Pipeline/Lit";
        skin.originalShared[0].allProperties=false;
        Check(!Aaris.MapReview.AarisVehicleWorld.Urp(body),
            "missing shader properties accepted");
        skin.originalShared[0].allProperties=true;
        skin.originalShared.Clear();
        Check(!Aaris.MapReview.AarisVehicleWorld.Urp(body),
            "missing material slots incorrectly accepted");
        skin.originalShared.Add(UrpMaterial());
        skin.throwMaterials=true;
        bool caught=false;
        try{Aaris.MapReview.AarisVehicleWorld.Urp(body);}
        catch(InvalidOperationException){caught=true;}
        Check(caught && Aaris.MapReview.AarisVehicleWorld.ScratchClean,
            "failed material query retained imported renderer references");
        skin.throwMaterials=false;
        body.throwQuery=true;
        caught=false;
        try{Aaris.MapReview.AarisVehicleWorld.Visible(wheel);}
        catch(InvalidOperationException){caught=true;}
        Check(caught && Aaris.MapReview.AarisVehicleWorld.ScratchClean,
            "failed wheel renderer query retained references");
        body.throwQuery=false;
        Check(Aaris.MapReview.AarisVehicleWorld.Visible(wheel),
            "wheel visibility did not recover after query failure");
        Check(Aaris.MapReview.AarisVehicleWorld.Urp(body),
            "URP material validator did not recover");
        Check(!Aaris.MapReview.AarisVehicleWorld.Urp(null),
            "null source accepted");
        Check(!Aaris.MapReview.AarisVehicleWorld.Visible(null),
            "null wheel accepted");
        body.activeInHierarchy=false;
        Check(!Aaris.MapReview.AarisVehicleWorld.Visible(wheel),
            "inactive parent incorrectly rendered");
        Check(Aaris.MapReview.AarisVehicleWorld.ScratchClean,
            "final list cleanup missing");
        Console.WriteLine("VEHICLE_RENDERER_MATERIAL_PRODUCTION_PASS checks="+checks);
        Console.WriteLine("Controlled Mono Unity stubs, no native Unity/Android FPS claim.");
    }
}
