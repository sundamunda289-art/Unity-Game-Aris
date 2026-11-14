using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace Aaris.MapReview.Editor {
[InitializeOnLoad] public static class AarisPoseFrames {
const string Dir="/tmp/aaris-codespaces/motion-frames";
const string Req=Dir+"/capture.request";
static readonly string[] Names={"01_Standing","02_Crouch","03_Prone","04_Walk","05_Sprint","06_Jump"};
static AarisPoseFrames(){EditorApplication.update+=Tick;}
static double Now=>EditorApplication.timeSinceStartup;
static void Log(string s){Directory.CreateDirectory(Dir);File.AppendAllText(Dir+"/report.txt",s+"\n");Debug.Log("AARIS_POSE_FRAME "+s);}
static void Phase(string s){File.WriteAllText(Dir+"/phase.txt",s+"\n");}
// Native batchmode capture must open the actual Unity scene before entering Play.
public static void CaptureBatch(){
if(!Application.isBatchMode)throw new InvalidOperationException("BATCHMODE_REQUIRED");
try{
var scene=EditorSceneManager.OpenScene(AarisExplorerBuild.ScenePath,OpenSceneMode.Single);
if(!scene.IsValid()||!scene.isLoaded)throw new InvalidOperationException("SCENE_LOAD_FAILED");
SessionState.SetBool("pose.batch",true);
Start();
}catch(Exception ex){Debug.LogError("AARIS_POSE_BATCH_START_FAILED "+ex.GetType().Name);EditorApplication.Exit(2);}
}
static void Start(){
Directory.CreateDirectory(Dir);
foreach(var f in Directory.GetFiles(Dir,"*.jpg"))File.Delete(f);
File.WriteAllText(Dir+"/report.txt","REAL_UNITY_CAMERA_TEST\n");
SessionState.SetString("pose.state","start");SessionState.SetFloat("pose.time",(float)Now);
SessionState.SetInt("pose.step",-1);SessionState.SetInt("pose.count",0);SessionState.SetInt("pose.warp",0);
EditorApplication.isPlaying=true;
}
static void Stage(int n,AarisThirdPersonPlayer p,AarisBattleActions b){
SessionState.SetInt("pose.step",n);SessionState.SetInt("pose.count",0);
SessionState.SetFloat("pose.time",(float)Now);
Phase(Names[n]);
bool ok=true;
if(n==0){p.CancelAutoSprint();ok=b.TryStand();}
if(n==1)ok=b.TrySetStance(AarisBattleActions.PlayerStance.Crouching);
if(n==2)ok=b.TrySetStance(AarisBattleActions.PlayerStance.Prone);
if(n==3){p.CancelAutoSprint();ok=b.TryStand();}
if(n==4){ok=b.TryStand();p.ToggleAutoSprint();}
if(n==5){p.CancelAutoSprint();ok=b.TryStand();p.RequestJump();}
Log("STAGE "+Names[n]+" accepted="+ok);
}
static void Finish(string reason){
Log(reason);SessionState.SetString("pose.state","");Phase("07_COMPLETE");
if(EditorApplication.isPlaying)EditorApplication.isPlaying=false;
if(SessionState.GetBool("pose.batch",false)){
SessionState.EraseBool("pose.batch");
int code=reason=="SUCCESS_18_REAL_FRAMES"?0:2;
EditorApplication.delayCall+=()=>EditorApplication.Exit(code);
}
}
static void Tick(){
if(File.Exists(Req)&&!EditorApplication.isPlayingOrWillChangePlaymode&&
SessionState.GetString("pose.state","")==""){File.Delete(Req);Start();return;}
string status=SessionState.GetString("pose.state","");
if(status!="start"&&status!="run"||!EditorApplication.isPlaying)return;
if(status=="start"){
SessionState.SetString("pose.state","run");SessionState.SetFloat("pose.time",(float)Now);
Phase("00_Drop");Log("ENTER_PLAYMODE");return;
}
var p=UnityEngine.Object.FindFirstObjectByType<AarisThirdPersonPlayer>();
var d=UnityEngine.Object.FindFirstObjectByType<AarisOfflineDrop>();
if(p==null||d==null){
if(Now-SessionState.GetFloat("pose.time",(float)Now)>15)
Finish("NO_PLAYER_OR_DROP");
return;
}
var b=p.GetComponent<AarisBattleActions>();
int n=SessionState.GetInt("pose.step",-1);
double age=Now-SessionState.GetFloat("pose.time",(float)Now);
if(n<0){
if(d.Phase==AarisOfflineDrop.DropPhase.InPlane&&age>1.6){
d.OnAction();SessionState.SetFloat("pose.air",(float)Now);}
if(d.Phase==AarisOfflineDrop.DropPhase.Freefall&&
Now-SessionState.GetFloat("pose.air",(float)Now)>.9)d.OnAction();
if(d.Phase==AarisOfflineDrop.DropPhase.Gliding&&age>3.5&&
SessionState.GetInt("pose.warp",0)==0){
SessionState.SetInt("pose.warp",1);
foreach(var t in Terrain.activeTerrains){
Vector3 pos=d.transform.position,o=t.transform.position,s=t.terrainData.size;
if(pos.x<o.x||pos.x>o.x+s.x||pos.z<o.z||pos.z>o.z+s.z)continue;
var cap=d.GetComponent<CharacterController>();
if(cap!=null){
cap.enabled=false;pos.y=t.SampleHeight(pos)+o.y+3f;d.transform.position=pos;cap.enabled=true;
Log("EDITOR_ONLY_FAST_FORWARD_TO_TOUCHDOWN");
}break;}}
if(d.HasLanded&&b!=null){Stage(0,p,b);return;}
if(age>85)Finish("TIMEOUT_"+d.Phase);
return;
}
if(b==null){Finish("NO_BATTLE_COMPONENT");return;}
int count=SessionState.GetInt("pose.count",0);
if(count<3&&age>.25+count*.30){
try{
Camera cam=Camera.main;if(cam==null)throw new InvalidOperationException("CAMERA_MISSING");
var rt=RenderTexture.GetTemporary(960,540,24);
var old=cam.targetTexture;var active=RenderTexture.active;
Vector3 savedCameraPosition=cam.transform.position;
Quaternion savedCameraRotation=cam.transform.rotation;
var image=new Texture2D(960,540,TextureFormat.RGB24,false);
try{
// The real game camera usually faces the aircraft or canopy and makes
// stance screenshots unusable. For PHOTO readback only, move its view to
// face the player at human scale, then restore the untouched game camera.
float eye=b.Stance==AarisBattleActions.PlayerStance.Prone ? 0.42f :
 b.Stance==AarisBattleActions.PlayerStance.Crouching ? 0.85f : 1.18f;
Vector3 target=p.transform.position+Vector3.up*eye;
Vector3 photoPosition=target+p.transform.forward*3.0f+Vector3.up*0.42f;
cam.transform.position=photoPosition;
cam.transform.rotation=Quaternion.LookRotation(target-photoPosition,Vector3.up);
cam.targetTexture=rt;cam.Render();RenderTexture.active=rt;
image.ReadPixels(new Rect(0,0,960,540),0,0,false);image.Apply(false);
var bytes=image.EncodeToJPG(82);
if(bytes.Length<4000)throw new InvalidOperationException("EMPTY_FRAME");
File.WriteAllBytes(Dir+"/"+Names[n]+"_f"+count+".jpg",bytes);
}finally{
cam.transform.SetPositionAndRotation(savedCameraPosition,savedCameraRotation);
cam.targetTexture=old;RenderTexture.active=active;
UnityEngine.Object.DestroyImmediate(image);RenderTexture.ReleaseTemporary(rt);}
SessionState.SetInt("pose.count",count+1);
Log("FRAME "+Names[n]+" "+count+" stance="+b.Stance+
" air="+p.IsInAir+" pos="+p.transform.position.ToString("F2"));
}catch(Exception ex){Finish("ERROR_"+ex.GetType().Name+"_"+ex.Message);return;}
}
if(age>1.3&&SessionState.GetInt("pose.count",0)>=3){
if(n+1>=Names.Length){p.CancelAutoSprint();Finish("SUCCESS_18_REAL_FRAMES");}
else Stage(n+1,p,b);}
}
}
}
