#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
public static class AarisGpt6OpenEntrySceneBaker
{
 const string SceneFile="Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";
 const string Dir="/tmp/aaris-doorless-house-swap";
 const string Marker="__AarisNativeOpenEntryHome";
 static readonly Regex NameRx=new Regex(@"^.+_(a_frame_cabin|bungalow_house|pitched_roof_cottage|detached_two_storey_house|garage_apartment|two_bedroom_garage_house|row_three_townhouses)_\d{2}$");
 static readonly Dictionary<string,string> Styles=new Dictionary<string,string>{
 {"a_frame_cabin","RidgeCabin"},{"bungalow_house","PlasterBungalow"},
 {"pitched_roof_cottage","RidgeCottage"},{"detached_two_storey_house","ConcreteHome"},
 {"garage_apartment","CoastalHome"},{"two_bedroom_garage_house","MasonryHome"},
 {"row_three_townhouses","TerraceHome"}};
 static IEnumerable<GameObject> All(GameObject g){yield return g;foreach(Transform t in g.transform)foreach(var x in All(t.gameObject))yield return x;}
 static string Sha(){using(var s=SHA256.Create())using(var f=File.OpenRead(SceneFile))return BitConverter.ToString(s.ComputeHash(f)).Replace("-","");}
 static string Style(string name){var m=NameRx.Match(name);return m.Success?m.Groups[1].Value:null;}
 [MenuItem("Aaris/Houses/Preview native open houses")]
 public static void Preview(){Run(false);}
 [MenuItem("Aaris/Houses/Apply native open houses")]
 public static void Apply(){Run(true);}
 static void Run(bool apply)
 {
  Directory.CreateDirectory(Dir);
  string report=Path.Combine(Dir,apply?"apply.report":"preview.report");
  var log=new List<string>();
  try{
   if(EditorApplication.isPlayingOrWillChangePlaymode)throw new Exception("PLAY_MODE_BLOCKED");
   string hash=Sha();
   if(apply && (!File.Exists(Path.Combine(Dir,"preview.report"))||
     !File.ReadAllLines(Path.Combine(Dir,"preview.report")).Contains("SCENE_SHA="+hash)||
     !File.ReadAllLines(Path.Combine(Dir,"preview.report")).Contains("RESULT=PASS")))
    throw new Exception("FRESH_PREVIEW_REQUIRED");
   Scene scene=EditorSceneManager.OpenScene(SceneFile,OpenSceneMode.Single);
   var all=scene.GetRootGameObjects().SelectMany(All).ToArray();
   var homes=all.Where(x=>Style(x.name)!=null).OrderBy(x=>x.name).ToArray();
   if(homes.Length<40||homes.Length>55)throw new Exception("UNEXPECTED_HOUSE_COUNT "+homes.Length);
   var models=new Dictionary<string,GameObject>();
   foreach(var model in Styles.Values){
    string path="Assets/AarisWorld/Models/RuinedHouses/"+model+"/"+model+".prefab";
    var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(path);
    if(prefab==null||prefab.GetComponent<MeshRenderer>()==null||
       prefab.GetComponent<LODGroup>()==null||prefab.GetComponents<Collider>().Length<4)
      throw new Exception("INVALID_PREFAB "+path);
    models.Add(model,prefab);
   }
   log.Add("SCENE_SHA="+hash);log.Add("COUNT="+homes.Length);log.Add("MODELS="+models.Count);
   foreach(var g in homes){
    var mr=g.GetComponent<MeshRenderer>();
    if(g.transform.Find(Marker)!=null||mr==null||g.GetComponent<LODGroup>()==null||
       g.GetComponent<MeshFilter>()==null||mr.bounds.size.x<1||mr.bounds.size.z<1)
      throw new Exception("OLD_HOUSE_INVALID_OR_ALREADY_DONE "+g.name);
    log.Add("HOUSE="+g.name+" MODEL="+Styles[Style(g.name)]);
   }
   if(apply){
    string backup=Dir+"/native-scene-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+
                "-"+hash.Substring(0,12)+".unity";
    File.Copy(SceneFile,backup,false);log.Add("BACKUP="+backup);
    int colliders=0,lods=0;
    foreach(var g in homes){
     var old=g.GetComponent<MeshRenderer>().bounds;
     var replacement=(GameObject)PrefabUtility.InstantiatePrefab(models[Styles[Style(g.name)]],scene);
     if(replacement==null)throw new Exception("PREFAB_CREATE_FAILED "+g.name);
     replacement.name=Marker;
     replacement.transform.SetParent(g.transform,false);
     replacement.transform.localPosition=Vector3.zero;
     replacement.transform.localRotation=Quaternion.identity;
     replacement.transform.localScale=Vector3.one;
     var b=replacement.GetComponent<MeshRenderer>().bounds;
     if(b.size.x<.1f||b.size.z<.1f)throw new Exception("INVALID_NEW_BOUNDS");
     float k=Mathf.Clamp(Mathf.Min(old.size.x/b.size.x,old.size.z/b.size.z),.65f,1.15f);
     replacement.transform.localScale=Vector3.one*k;
     b=replacement.GetComponent<MeshRenderer>().bounds;
     replacement.transform.position+=new Vector3(old.center.x-b.center.x,old.min.y-b.min.y,old.center.z-b.center.z);
     foreach(Component c in g.GetComponents<Component>()){
      if(c is Collider){colliders++;UnityEngine.Object.DestroyImmediate(c);}
      else if(c is LODGroup||c is MeshRenderer||c is MeshFilter)UnityEngine.Object.DestroyImmediate(c);
     }
     for(int i=1;i<=2;i++){
      string n="ReviewLOD_"+g.name+"_"+i;
      var lod=all.FirstOrDefault(x=>x!=null&&x.name==n);
      if(lod!=null){UnityEngine.Object.DestroyImmediate(lod);lods++;}
     }
    }
    EditorSceneManager.MarkSceneDirty(scene);
    if(!EditorSceneManager.SaveScene(scene))throw new Exception("SCENE_SAVE_FAILED");
    log.Add("REMOVED_COLLIDERS="+colliders);
    log.Add("REMOVED_LODS="+lods);
    log.Add("NEW_SCENE_SHA="+Sha());
   }
   log.Insert(0,"RESULT=PASS");File.WriteAllLines(report,log);
   Debug.Log("AARIS_GPT6_OPENENTRY_"+(apply?"APPLY":"PREVIEW")+"_PASS count="+homes.Length);
  }catch(Exception ex){
   log.Insert(0,"RESULT=FAIL");log.Add("ERROR="+ex.ToString());
   File.WriteAllLines(report,log);
   Debug.LogError("AARIS_GPT6_OPENENTRY_FAIL "+ex);
   if(Application.isBatchMode)EditorApplication.Exit(1);
  }
 }
}
#endif
