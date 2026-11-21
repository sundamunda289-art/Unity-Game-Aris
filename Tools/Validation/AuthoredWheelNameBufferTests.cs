// Compiles the exact production name lookup with controlled Unity-style stubs.
// Does not exercise Unity native import, PhysX or physical Android FPS.
using System;
using System.Collections.Generic;
using UnityEngine;
namespace UnityEngine
{
    public sealed class Transform
    {
        public string name;
        public readonly List<Transform> descendants=new List<Transform>();
        public bool throwOnQuery;
        public static int queries;
        public static readonly HashSet<object> queryLists=new HashSet<object>();
        public void GetComponentsInChildren<T>(bool includeInactive,List<T> results)
        {
            if(typeof(T)!=typeof(Transform))throw new Exception("incorrect Unity query");
            queries++;
            queryLists.Add(results);
            results.Clear();
            if(throwOnQuery)throw new InvalidOperationException("failed native child query");
            results.Add((T)(object)this);
            foreach(var t in descendants)results.Add((T)(object)t);
        }
    }
}
namespace Aaris.MapReview
{
    internal static class AarisVehicleWorld
    {
        /* PRODUCTION_SCRATCH_FIELD */
        /* PRODUCTION_WHEEL_NAME_LOOKUP */
        internal static Transform Find(Transform root,string token) =>
            FindNamedWheel(root,token);
        internal static int Retained => namedWheelQueryScratch==null
            ? 0:namedWheelQueryScratch.Count;
    }
}
internal static class NativeWheelNameTests
{
    static int checks;
    static void Check(bool value,string reason)
    { checks++; if(!value)throw new Exception(reason); }
    public static void Main()
    {
        var root=new Transform{name="car_body"};
        var first=new Transform{name="CAR_WHEEL_FL"};
        var duplicate=new Transform{name="extra_wheel_fl"};
        var fr=new Transform{name="Wheel_Fr"};
        var rl=new Transform{name="wheel_RL"};
        var rr=new Transform{name="wheel_rr"};
        root.descendants.Add(new Transform{name="mesh_shell"});
        root.descendants.Add(first);root.descendants.Add(duplicate);
        root.descendants.Add(fr);root.descendants.Add(rl);root.descendants.Add(rr);
        for(int i=0;i<40;i++)
            root.descendants.Add(new Transform{name="panel_"+i});
        for(int i=0;i<200;i++)
        {
            Check(Object.ReferenceEquals(Aaris.MapReview.AarisVehicleWorld.Find(
                root,"wheel_fl"),first),"first authored wheel changed");
            Check(Object.ReferenceEquals(Aaris.MapReview.AarisVehicleWorld.Find(
                root,"WHEEL_FR"),fr),"mixed case wheel search failed");
            Check(Object.ReferenceEquals(Aaris.MapReview.AarisVehicleWorld.Find(
                root,"wheel_rl"),rl),"rear left search failed");
            Check(Object.ReferenceEquals(Aaris.MapReview.AarisVehicleWorld.Find(
                root,"wheel_rr"),rr),"rear right search failed");
            Check(Aaris.MapReview.AarisVehicleWorld.Find(root,"missing")==null,
                "unknown wheel erroneously matched");
            Check(Aaris.MapReview.AarisVehicleWorld.Retained==0,
                "imported child references retained");
        }
        Check(Transform.queryLists.Count==1,
            "repeated wheel lookup allocated different output list");
        Check(Transform.queries==1000,"unexpected number of component lookups");
        root.throwOnQuery=true;
        bool caught=false;
        try{Aaris.MapReview.AarisVehicleWorld.Find(root,"wheel_fl");}
        catch(InvalidOperationException){caught=true;}
        Check(caught && Aaris.MapReview.AarisVehicleWorld.Retained==0,
            "failed Unity query retained imported hierarchy");
        root.throwOnQuery=false;
        Check(Object.ReferenceEquals(Aaris.MapReview.AarisVehicleWorld.Find(
            root,"wheel_fl"),first),"retry after exception broken");
        Check(Aaris.MapReview.AarisVehicleWorld.Find(null,"wheel_fl")==null,
            "null chassis should not initiate query");
        Console.WriteLine("AUTHORED_WHEEL_NAME_PRODUCTION_BODY_PASS checks="+checks);
        Console.WriteLine("Mono Unity API stubs, not native Unity/Adreno benchmark");
    }
}
