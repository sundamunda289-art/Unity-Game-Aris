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
// Faster three-pose height probe; keeps production 18-frame capture unchanged.
// Isolate bone / skinning data from expensive OpenGL readback on CI.
public static void CaptureGeometryDiagnostic(){
SessionState.SetBool("pose.geometryOnly",true);
CaptureDiagnostic();
}
public static void CaptureDiagnostic(){
SessionState.SetBool("pose.diag",true);
CaptureBatch();
}
// No camera/ReadPixels: exercise transitions and the real motor in Play Mode.
public static void CaptureStanceJumpDiagnostic(){
SessionState.SetBool("pose.stanceJump",true);
CaptureBatch();
}
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
// Editor QA only: same open terrain coordinate on every run, not
// a random plane drop that may land beside a wall or steep bank.
static bool SurveyTeleport(AarisThirdPersonPlayer p){
var target=new Vector3(-765.31f,0f,80.87f);
foreach(var t in Terrain.activeTerrains){
if(t==null||t.terrainData==null)continue;
Vector3 o=t.transform.position,sz=t.terrainData.size;
if(target.x<o.x||target.x>o.x+sz.x||
 target.z<o.z||target.z>o.z+sz.z)continue;
var cap=p.GetComponent<CharacterController>();
if(cap==null)return false;
target.y=t.SampleHeight(target)+o.y+0.05f;
cap.enabled=false;
p.transform.position=target;
cap.enabled=true;
Log("EDITOR_ONLY_FIXED_SURVEY_LOCATION");
return true;
}
return false;
}
static void Stage(int n,AarisThirdPersonPlayer p,AarisBattleActions b){
SessionState.SetInt("pose.step",n);SessionState.SetInt("pose.count",0);
SessionState.SetFloat("pose.time",(float)Now);
Phase(Names[n]);
bool ok=true;
if(n==0){
if(!SurveyTeleport(p)){
Finish("SURVEY_LOCATION_UNAVAILABLE");return;}
p.CancelAutoSprint();ok=b.TryStand();
}
if(n==1)ok=b.TrySetStance(AarisBattleActions.PlayerStance.Crouching);
if(n==2)ok=b.TrySetStance(AarisBattleActions.PlayerStance.Prone);
if(n==3){p.CancelAutoSprint();ok=b.TryStand();}
if(n==4){ok=b.TryStand();p.ToggleAutoSprint();}
if(n==5){
p.CancelAutoSprint();ok=b.TryStand();
if(ok){
SessionState.SetFloat("pose.jumpRequestAt",Time.unscaledTime);
SessionState.SetFloat("pose.jumpY",p.transform.position.y);
SessionState.SetFloat("pose.jumpMax",0f);
p.RequestJump();
}}
Log("STAGE "+Names[n]+" accepted="+ok);
if(!ok) Finish("STANCE_REJECTED_"+Names[n]);
}
// Read-only diagnostic: compare the real capsule foot, local terrain and
// animated mesh bounds; the preview camera alone cannot prove floating.
static void LogPhysicalHeight(AarisThirdPersonPlayer p,AarisBattleActions b){
var capsule=p.GetComponent<CharacterController>();
Vector3 foot=p.transform.TransformPoint(capsule.center+Vector3.down*(capsule.height*0.5f));
float terrainY=float.NaN;bool terrainFound=false;
foreach(var t in Terrain.activeTerrains){
if(t==null||t.terrainData==null)continue;
Vector3 o=t.transform.position;
Vector3 sz=t.terrainData.size;
if(p.transform.position.x<o.x||p.transform.position.x>o.x+sz.x||
p.transform.position.z<o.z||p.transform.position.z>o.z+sz.z)continue;
terrainY=t.SampleHeight(p.transform.position)+o.y;
terrainFound=true;break;
}
float meshMin=float.PositiveInfinity,meshMax=float.NegativeInfinity;
int count=0;
foreach(var skin in p.GetComponentsInChildren<SkinnedMeshRenderer>(true)){
if(!skin.enabled||!skin.gameObject.activeInHierarchy)continue;
Bounds bounds=skin.bounds;
if(bounds.size.y<0.01f||bounds.size.y>10f)continue;
meshMin=Mathf.Min(meshMin,bounds.min.y);
meshMax=Mathf.Max(meshMax,bounds.max.y);
count++;
}
// Inspect each renderer separately: dynamic canopy/weapon meshes must not
// be misread as deforming humanoid geometry in the aggregated bounds.
foreach(var skin in p.GetComponentsInChildren<SkinnedMeshRenderer>(true)){
if(skin==null)continue;
var bb=skin.bounds;
Log("SKIN stance="+b.Stance+" name="+skin.gameObject.name+
" active="+(skin.enabled&&skin.gameObject.activeInHierarchy)+
" minY="+bb.min.y.ToString("F3")+
" maxY="+bb.max.y.ToString("F3")+
" height="+bb.size.y.ToString("F3")+
" boneCount="+skin.bones.Length);
}
var animator=p.GetComponentInChildren<Animator>(true);
if(animator!=null&&animator.isHuman){
HumanBodyBones[] bones={HumanBodyBones.Hips,HumanBodyBones.Chest,
HumanBodyBones.Head,HumanBodyBones.LeftFoot,HumanBodyBones.RightFoot};
foreach(var bone in bones){
var t=animator.GetBoneTransform(bone);
if(t==null)continue;
Log("BONE stance="+b.Stance+" id="+bone+
" pos="+t.position.ToString("F3")+
" heightAboveTerrain="+(t.position.y-terrainY).ToString("F3"));
}
}
Log("MEASURE stance="+b.Stance+" footY="+foot.y.ToString("F3")+
" terrainFound="+terrainFound+" terrainY="+terrainY.ToString("F3")+
" footGap="+(foot.y-terrainY).ToString("F3")+
" meshMin="+meshMin.ToString("F3")+" meshMax="+meshMax.ToString("F3")+
" meshGap="+(meshMin-terrainY).ToString("F3")+
" skins="+count+" capsuleHeight="+capsule.height.ToString("F3"));
}
static void Finish(string reason){
Log(reason);SessionState.SetString("pose.state","");Phase("07_COMPLETE");
if(EditorApplication.isPlaying)EditorApplication.isPlaying=false;
if(SessionState.GetBool("pose.batch",false)){
SessionState.EraseBool("pose.batch");
SessionState.EraseBool("pose.diag");
int code=(reason=="SUCCESS_18_REAL_FRAMES"||
 reason=="SUCCESS_PRONE_DIAGNOSTIC"||
 reason=="SUCCESS_PRONE_GEOMETRY_ONLY"||
 reason=="SUCCESS_STANCE_JUMP_MOTOR")?0:2;
SessionState.EraseBool("pose.geometryOnly");
SessionState.EraseBool("pose.stanceJump");
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
// Deterministic stand > crouch > prone > stand > jump > land sequence.
// Treat a rejected stance, missing takeoff or missing landing event as FAIL.
if(SessionState.GetBool("pose.stanceJump",false)){
if((n==0||n==1||n==2||n==3)&&age>0.65){
if(n==2&&b.Stance!=AarisBattleActions.PlayerStance.Prone){
Finish("PRONE_NOT_ACTIVE");return;}
if(n==3&&b.Stance!=AarisBattleActions.PlayerStance.Standing){
Finish("STAND_NOT_ACTIVE");return;}
LogPhysicalHeight(p,b);
Stage(n==3?5:n+1,p,b);
return;
}
if(n==5){
float requested=SessionState.GetFloat("pose.jumpRequestAt",-10f);
float before=SessionState.GetFloat("pose.jumpY",p.transform.position.y);
bool launched=p.LastJumpStartedTime>=requested-0.001f;
float maxHeight=SessionState.GetFloat("pose.jumpMax",0f);
maxHeight=Mathf.Max(maxHeight,p.transform.position.y-before);
SessionState.SetFloat("pose.jumpMax",maxHeight);
if(launched && p.LastLandingTime>p.LastJumpStartedTime &&
   Time.unscaledTime-p.LastJumpStartedTime>0.40f){
Log("JUMP_MOTOR jumpApexMetres="+maxHeight.ToString("F3")+
" landingImpact="+p.LastLandingImpactSpeed.ToString("F3")+
" landed="+(!p.IsInAir));
if(!p.IsInAir&&maxHeight>=0.45f){
Finish("SUCCESS_STANCE_JUMP_MOTOR");return;}}
if(age>8f){
Log("JUMP_MOTOR_TIMEOUT launched="+launched+
" maxHeight="+maxHeight.ToString("F3")+
" landingAfterTakeoff="+(p.LastLandingTime>p.LastJumpStartedTime));
Finish("JUMP_FAILED");}
return;
}
return;
}
if(n==2 && SessionState.GetBool("pose.geometryOnly",false) && age>0.8){
if(b.Stance != AarisBattleActions.PlayerStance.Prone){
Finish("PRONE_NOT_ACTIVE");return;
}
LogPhysicalHeight(p,b);
Finish("SUCCESS_PRONE_GEOMETRY_ONLY");
return;
}
bool fastProbe=SessionState.GetBool("pose.diag",false) ||
SessionState.GetBool("pose.geometryOnly",false);
int needed=fastProbe?(n==2?1:0):3;
if(count<needed&&age>.25+count*.30){
try{
Camera cam=Camera.main;if(cam==null)throw new InvalidOperationException("CAMERA_MISSING");
int imageWidth=SessionState.GetBool("pose.diag",false)?640:960;
int imageHeight=SessionState.GetBool("pose.diag",false)?360:540;
var rt=RenderTexture.GetTemporary(imageWidth,imageHeight,24);
var old=cam.targetTexture;var active=RenderTexture.active;
Vector3 savedCameraPosition=cam.transform.position;
Quaternion savedCameraRotation=cam.transform.rotation;
var image=new Texture2D(imageWidth,imageHeight,TextureFormat.RGB24,false);
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
image.ReadPixels(new Rect(0,0,imageWidth,imageHeight),0,0,false);image.Apply(false);
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
if(count==0)LogPhysicalHeight(p,b);
}catch(Exception ex){Finish("ERROR_"+ex.GetType().Name+"_"+ex.Message);return;}
}
if(age>(fastProbe?0.38:1.3)&&
 SessionState.GetInt("pose.count",0)>=needed){
if(SessionState.GetBool("pose.diag",false)&&n>=2){
p.CancelAutoSprint();Finish("SUCCESS_PRONE_DIAGNOSTIC");
}else if(n+1>=Names.Length){p.CancelAutoSprint();Finish("SUCCESS_18_REAL_FRAMES");}
else Stage(n+1,p,b);}
}
}
}
