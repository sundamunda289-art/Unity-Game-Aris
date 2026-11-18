using System;
using UnityEditor;
using UnityEngine;
namespace Aaris.MapReview.Editor
{
 public static class AarisPremiumRoadGtPhysicalQA
 {
  [MenuItem("Aaris/CI/Verify premium GT wheel/physics assembly")]
  public static void Verify()
  {
   var prefab=Resources.Load<GameObject>("AarisVehicles/PremiumRoadGT/AarisRoadGT");
   if(prefab==null)throw new Exception("Premium GT prefab missing");
   var root=new GameObject("Temporary GT physics QA");
   try
   {
    var visual=UnityEngine.Object.Instantiate(prefab,root.transform,false);
    Bounds bounds=new Bounds();bool found=false;
    foreach(var r in visual.GetComponentsInChildren<Renderer>(true))
      if(r.enabled){if(!found){bounds=r.bounds;found=true;}else bounds.Encapsulate(r.bounds);}
    if(!found||bounds.size.x<1||bounds.size.z<3.5f)throw new Exception("Bad GT visual bounds");
    float scale=Mathf.Min(1.88f/bounds.size.x,4.28f/bounds.size.z);
    visual.transform.localScale*=Mathf.Clamp(scale,.01f,50f);
    Physics.SyncTransforms();
    found=false;
    foreach(var r in visual.GetComponentsInChildren<Renderer>(true))
      if(r.enabled){if(!found){bounds=r.bounds;found=true;}else bounds.Encapsulate(r.bounds);}
    visual.transform.localPosition+=new Vector3(0,.48f,0)-bounds.center;
    var motor=root.AddComponent<AarisVehicleMotor>();
    motor.Configure("GT",27.5f,1160f);
    motor.ConfigureHandling("RWD",1.08f,1.12f,1.1f,.86f);
    motor.FitShellAndWheelbase(new Bounds(new Vector3(0,.48f,0),bounds.size));
    if(!motor.AttachEmbeddedWheelModels(visual.transform))throw new Exception("Embedded wheels rejected");
    motor.ConfigureBodyDynamics(visual.transform);
    var wheels=root.GetComponentsInChildren<WheelCollider>();
    var mass=root.GetComponent<Rigidbody>().mass;
    if(wheels.Length!=4||Mathf.Abs(mass-1160f)>1f)throw new Exception("Wrong physical rig");
    var names=new[]{"wheel_fl","wheel_fr","wheel_rl","wheel_rr"};
    foreach(string name in names)
    {
      bool ok=false;
      foreach(var t in root.GetComponentsInChildren<Transform>(true))
        if(t.name==name&&t.parent==root.transform&&
           t.GetComponentInChildren<Renderer>(true)!=null)ok=true;
      if(!ok)throw new Exception("Detached visual not found: "+name);
    }
    foreach(var w in wheels)
      if(w.radius<.26f||w.radius>.46f||
         Mathf.Abs(w.transform.localPosition.x)<.60f||
         Mathf.Abs(w.transform.localPosition.z)<.8f)
           throw new Exception("Bad GT WheelCollider placement");
    Debug.Log("AARIS_PREMIUM_ROAD_GT_MOTOR_PASS wheels=4 mass="+mass);
   }
   finally{UnityEngine.Object.DestroyImmediate(root);}
  }
 }
}
