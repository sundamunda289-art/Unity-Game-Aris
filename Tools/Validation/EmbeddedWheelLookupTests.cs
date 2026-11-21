// Mono stubs compile the exact production embedded-wheel method and axle guard.
// No native Unity renderer, mesh importer, PhysX, or Android FPS simulated.
using System;
using System.Collections.Generic;
using UnityEngine;
namespace UnityEngine
{
    public sealed class Rigidbody {}
    public sealed class Collider { public bool enabled = true; }
    public struct Quaternion
    {
        public static Quaternion Inverse(Quaternion q) => q;
        public static Quaternion operator *(Quaternion a,Quaternion b) => a;
        public static Vector3 operator *(Quaternion q,Vector3 v) => v;
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}
        public float sqrMagnitude => x*x+y*y+z*z;
        public static Vector3 operator +(Vector3 a,Vector3 b) =>
            new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);
        public static Vector3 operator -(Vector3 a,Vector3 b) =>
            new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);
    }
    public struct Bounds
    {
        public Vector3 size,center;
        public Bounds(Vector3 center,Vector3 size)
        {this.center=center;this.size=size;}
    }
    public static class Mathf
    {
        public static float Max(float a,float b)=>Math.Max(a,b);
        public static float Abs(float a)=>Math.Abs(a);
        public static float Clamp(float a,float low,float high)=>
            Math.Max(low,Math.Min(high,a));
    }
    public sealed class Transform
    {
        public string name;
        public Transform parent;
        public Vector3 position,localPosition;
        public Quaternion rotation;
        public Bounds bounds;
        public bool renderable=true,validBounds=true;
        public int reparentCalls;
        public readonly List<Transform> children=new List<Transform>();
        public readonly List<Collider> colliders=new List<Collider>();
        public static int transformQueries,colliderQueries;
        public Vector3 TransformPoint(Vector3 value)=>value;
        public void SetParent(Transform target,bool worldPositionStays)
        {
            reparentCalls++;
            if(parent!=null)parent.children.Remove(this);
            parent=target;
            target.children.Add(this);
        }
        public void GetComponentsInChildren<T>(bool includeInactive,List<T> target)
        {
            target.Clear();
            if(typeof(T)==typeof(Transform))
            {
                transformQueries++;
                target.Add((T)(object)this);
                foreach(var child in children)target.Add((T)(object)child);
            }
            else if(typeof(T)==typeof(Collider))
            {
                colliderQueries++;
                foreach(var collider in colliders)target.Add((T)(object)collider);
            }
            else throw new Exception("Unexpected query type");
        }
    }
    public sealed class WheelCollider
    {
        public float radius;
        public readonly Transform transform=new Transform();
        public int resetCount;
        public void ResetSprungMasses(){resetCount++;}
    }
}
namespace Aaris.MapReview
{
    internal static class AarisVehicleWorld
    {
        public static bool HasRenderableWheelGeometry(Transform wheel)=>wheel.renderable;
        public static bool TryGetVisualBoundsLocal(Transform chassis,Transform wheel,out Bounds bounds)
        { bounds=wheel.bounds;return wheel.validBounds; }
    }
    public sealed class EmbeddedFixture
    {
        private readonly Rigidbody body=new Rigidbody();
        private readonly Transform transform=new Transform();
        private readonly WheelCollider[] wheels={
            new WheelCollider(),new WheelCollider(),
            new WheelCollider(),new WheelCollider() };
        private static readonly string[] EmbeddedWheelTokens =
            { "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr" };
        private readonly List<Transform> embeddedWheelTransforms =
            new List<Transform>(64);
        private readonly List<Collider> embeddedWheelColliders =
            new List<Collider>(8);
        private readonly Transform[] embeddedWheelFound=new Transform[4];
        private readonly Bounds[] embeddedWheelBounds=new Bounds[4];
        private readonly Transform[] visualWheels=new Transform[4];
        private readonly Quaternion[] visualWheelRotationOffsets=new Quaternion[4];
        private readonly Vector3[] visualWheelCenterOffsets=new Vector3[4];
        private float appliedSteeringAngle;
        private int syncCalls;
        private void SyncVisibleWheels(){syncCalls++;}

        /* PRODUCTION_EMBEDDED_WHEELS */
        /* PRODUCTION_AXLE_CHECK */

        public int CurrentTransformCount=>embeddedWheelTransforms.Count;
        public int CurrentColliderCount=>embeddedWheelColliders.Count;
        public bool ClearedReferences()
        {
            foreach(var found in embeddedWheelFound)if(found!=null)return false;
            return true;
        }
        public Transform[] Attached=>visualWheels;
        public WheelCollider[] Wheels=>wheels;
        public int SyncCalls=>syncCalls;
        public bool SteeringReset=>float.IsNaN(appliedSteeringAngle);
    }
}
internal static class EmbeddedWheelProductionTests
{
    static int checks;
    static void Check(bool ok,string why)
    {checks++;if(!ok)throw new Exception(why);}
    static readonly string[] Names={
        "Car_BODY__WHEEL_FL", "road_wheel_Fr", "Race_WHEEL_RL",
        "rally_WHEEL_rr" };
    static Transform Make(bool valid)
    {
        var root=new Transform{name="CAR_MAIN"};
        for(int i=0;i<45;i++)
        {
            var spare=new Transform{name="Panel_"+i,parent=root};
            root.children.Add(spare);
        }
        for(int i=0;i<4;i++)
        {
            float x=(i%2==0?-.78f:.78f), z=i<2?1.34f:-1.34f;
            if(!valid && i==1)x=-.1f; // reversed axle physically invalid
            var b=new Bounds(new Vector3(x,.32f,z),new Vector3(.42f,.74f,.74f));
            var wheel=new Transform{name=Names[i],parent=root,
                position=b.center,bounds=b};
            wheel.colliders.Add(new Collider());
            wheel.colliders.Add(new Collider());
            root.children.Add(wheel);
        }
        return root;
    }
    public static void Main()
    {
        Transform.transformQueries=Transform.colliderQueries=0;
        var motor=new Aaris.MapReview.EmbeddedFixture();
        var root=Make(true);
        var original=new List<Transform>();
        foreach(var part in root.children)
            if(part.name.IndexOf("wheel_",StringComparison.OrdinalIgnoreCase)>=0)
                original.Add(part);
        Check(motor.AttachEmbeddedWheelModels(root),"valid authored chassis rejected");
        Check(motor.SyncCalls==1 && motor.SteeringReset,"wheel pose not refreshed");
        Check(motor.CurrentTransformCount==0 && motor.CurrentColliderCount==0 &&
              motor.ClearedReferences(),"source hierarchy retained after valid fit");
        Check(Transform.transformQueries==1 && Transform.colliderQueries==4,
              "extra native queries performed");
        for(int i=0;i<4;i++)
        {
            Check(Object.ReferenceEquals(motor.Attached[i],original[i]),
                  "first-match authored wheel identity changed");
            Check(motor.Attached[i].reparentCalls==1,
                  "authored wheel not attached to chassis exactly once");
            Check(Math.Abs(motor.Wheels[i].radius-.37f)<.00001,
                  "authored diameter changed");
            foreach(var c in motor.Attached[i].colliders)
                Check(!c.enabled,"visual-only wheel collider remains enabled");
        }
        Check(motor.Wheels[0].resetCount==1,"sprung mass not refitted");

        var invalid=Make(false);
        var reject=new Aaris.MapReview.EmbeddedFixture();
        Check(!reject.AttachEmbeddedWheelModels(invalid),
              "inverted tyre axle wrongly accepted");
        Check(reject.SyncCalls==0,"invalid source changed car wheel pose");
        Check(reject.CurrentTransformCount==0 && reject.CurrentColliderCount==0 &&
              reject.ClearedReferences(),"rejected hierarchy retained");
        foreach(var part in invalid.children)
            foreach(var c in part.colliders)
                Check(c.enabled,"failed axle test mutated collider state");
        Check(reject.AttachEmbeddedWheelModels(Make(true)),
              "retry after invalid source stranded cached axle lookup");
        Check(reject.SyncCalls==1 && reject.ClearedReferences(),
              "retry omitted pose update or retained source references");
        Console.WriteLine("EMBEDDED_WHEEL_PRODUCTION_BODY_PASS checks="+checks);
        Console.WriteLine("Controlled Mono test; no native Unity/Android FPS claim.");
    }
}
