// Compile the actual production HasEmbeddedWheelSet and lamp body bounds methods.
// Managed Mono Unity API stubs only; this is NOT a native Unity/Android FPS test.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x,y,z;
        public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}
    }
    public struct Bounds
    {
        public Vector3 center,size;
        public Bounds(Vector3 c,Vector3 s){center=c;size=s;}
    }
    public sealed class MeshFilter {}
    public sealed class Transform
    {
        public string name;
        public bool throwOnQuery,validBounds=true;
        public Bounds fittedBounds;
        public MeshFilter mesh;
        public readonly List<Transform> children=new List<Transform>();
        public static int queries;
        public static readonly HashSet<object> outputLists=new HashSet<object>();
        public T GetComponent<T>() where T:class=>mesh as T;
        public void GetComponentsInChildren<T>(bool includeInactive,List<T> result)
        {
            if(typeof(T)!=typeof(Transform))throw new Exception("wrong Unity query");
            queries++;
            outputLists.Add(result);
            result.Clear();
            if(throwOnQuery)throw new InvalidOperationException("native hierarchy lookup failed");
            result.Add((T)(object)this);
            foreach(var child in children)result.Add((T)(object)child);
        }
    }
    public sealed class GameObject
    {
        public readonly Transform transform=new Transform();
        public void GetComponentsInChildren<T>(bool includeInactive,List<T> result)
        {
            transform.GetComponentsInChildren(includeInactive,result);
        }
    }
}
namespace Aaris.MapReview
{
    internal static class AarisVehicleWorld
    {
        /* ORIGINAL_SHARED_SCRATCH_FIELD */
        /* ACTUAL_BODY_BOUNDS */
        /* ACTUAL_EMBEDDED_WHEEL_CHECK */

        private static bool TryGetVisualBoundsLocal(Transform chassis,
            Transform child,out Bounds candidate)
        {
            candidate=child.fittedBounds;
            return child.validBounds;
        }
        internal static bool Body(Transform chassis,Transform visual,
            Bounds entire,out Bounds body) =>
            TryGetAuthoredLampBodyBounds(chassis,visual,entire,out body);
        internal static bool Wheels(GameObject source) =>
            HasEmbeddedWheelSet(source);
        internal static int ScratchCount => namedWheelQueryScratch==null
            ? 0 : namedWheelQueryScratch.Count;
    }
}
internal static class AuthoredVehicleValidationTests
{
    static int checks;
    static void Check(bool pass,string error)
    {checks++;if(!pass)throw new Exception(error);}
    static Transform Body(string name,Vector3 size,bool withMesh=true,
        bool valid=true)
    {
        return new Transform {name=name,fittedBounds=new Bounds(
            new Vector3(0,0,0),size),
            validBounds=valid,mesh=withMesh?new MeshFilter():null};
    }
    static GameObject MakeCar()
    {
        var car=new GameObject();
        car.transform.name="CAR_ROOT";
        for(int i=0;i<65;i++)car.transform.children.Add(
            new Transform{name="helper_"+i});
        car.transform.children.Add(Body("giant_collision_BODY",
            new Vector3(100,5,100)));
        car.transform.children.Add(Body("ignored_body_without_mesh",
            new Vector3(1.9f,1.3f,3.7f),false));
        car.transform.children.Add(Body("CAR_PAINT_BODY",
            new Vector3(1.9f,1.3f,3.8f)));
        car.transform.children.Add(Body("second_body",
            new Vector3(1.8f,1.2f,3.5f)));
        foreach(var wheel in new [] {"Car_WHEEL_FL","CAR_Wheel_fr",
            "car_wheel_Rl","Car_wheel_rR"})
            car.transform.children.Add(new Transform {name=wheel});
        return car;
    }
    static void Main()
    {
        var car=MakeCar();
        var chassis=new Transform{name="CAR_PARENT"};
        Bounds entire=new Bounds(new Vector3(0,0,0),
            new Vector3(2f,1.8f,4f));
        Bounds sample;
        for(int i=0;i<250;i++)
        {
            Check(Aaris.MapReview.AarisVehicleWorld.Wheels(car),
                "complete mixed-case authored tyres rejected");
            Check(Aaris.MapReview.AarisVehicleWorld.Body(
                chassis,car.transform,entire,out sample),
                "correct named body not selected");
            Check(Math.Abs(sample.size.x-1.9f)<.001f &&
                  Math.Abs(sample.size.z-3.8f)<.001f,
                "wrong body or invalid helper mesh chosen");
            Check(Aaris.MapReview.AarisVehicleWorld.ScratchCount==0,
                "retired car transforms retained");
        }
        Check(Transform.outputLists.Count==1,
            "validation queries did not share existing scratch list");
        var missing=new GameObject();
        missing.transform.name="NoTyres";
        missing.transform.children.Add(new Transform {name="wheel_fl"});
        Check(!Aaris.MapReview.AarisVehicleWorld.Wheels(missing),
            "three missing tyres incorrectly accepted");
        Bounds unused;
        Check(!Aaris.MapReview.AarisVehicleWorld.Body(
            chassis,missing.transform,entire,out unused),
            "invalid lamp body accepted");
        Check(Aaris.MapReview.AarisVehicleWorld.ScratchCount==0,
            "missing mesh result retained references");
        car.transform.throwOnQuery=true;
        bool caught=false;
        try{Aaris.MapReview.AarisVehicleWorld.Wheels(car);}
        catch(InvalidOperationException){caught=true;}
        Check(caught && Aaris.MapReview.AarisVehicleWorld.ScratchCount==0,
            "wheel-check exception retained source");
        caught=false;
        try{Aaris.MapReview.AarisVehicleWorld.Body(
            chassis,car.transform,entire,out unused);}
        catch(InvalidOperationException){caught=true;}
        Check(caught && Aaris.MapReview.AarisVehicleWorld.ScratchCount==0,
            "lamp-body exception retained source");
        car.transform.throwOnQuery=false;
        Check(Aaris.MapReview.AarisVehicleWorld.Wheels(car),
            "wheel query unable to recover");
        Check(Aaris.MapReview.AarisVehicleWorld.Body(
            chassis,car.transform,entire,out unused),
            "body query unable to recover");
        Check(Aaris.MapReview.AarisVehicleWorld.ScratchCount==0,
            "recovery left stale hierarchy references");
        Check(!Aaris.MapReview.AarisVehicleWorld.Wheels(null),
            "null source incorrectly accepted");
        Check(!Aaris.MapReview.AarisVehicleWorld.Body(
            null,car.transform,entire,out unused),
            "null chassis incorrectly accepted");
        Console.WriteLine("AUTHORED_VEHICLE_VALIDATION_PRODUCTION_BODY_PASS checks="+checks);
        Console.WriteLine("Mono Unity API stubs; native Unity/Android FPS not tested.");
    }
}
