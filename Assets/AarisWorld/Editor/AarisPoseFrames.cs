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
static readonly string[] MotionNames={"01_Standing","02_Walking","03_Running","04_Sprinting","05_Crouching","06_CrouchWalking","07_Prone","08_Crawling","09_GettingUp","10_Jumping"};
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
public static void CapturePortrait(){
SessionState.SetBool("pose.portrait",true);
CaptureBatch();
}
// Opt-in real Unity Play Mode photo QA. Four directions for the standing,
// crouch and prone poses, without changing their gameplay controller or
// replacing the established seven-frame front-only capture workflow.
public static void CaptureMultiAnglePortrait(){
SessionState.SetBool("pose.multiangle",true);
CapturePortrait();
}
// Editor-only native motor matrix. Unlike the legacy 04_Walk label, these
// captures inject nonzero movement through the actual player Update/motor.
// Front / side / back frames for ten locomotion and stance states (30 images).
public static void CaptureLocomotionMatrixPortrait(){
SessionState.SetBool("pose.motionMatrix",true);
CapturePortrait();
}
// Editor-only time-sampled stance transition portraits. Keep the existing
// 16-angle settled-pose QA unchanged; this mode inspects the interpolation
// at four steps during crouch, prone and return-to-standing.
public static void CaptureTransitionPortrait(){
SessionState.SetFloat("pose.transitionYawDegrees",90f);
SessionState.SetBool("pose.transitions",true);
CapturePortrait();
}
// Keep the exact same time samples but use additional *fixed* camera angles.
// Four pose frames viewed from four angles at different times can hide a
// transitional left/right knee flare; each of these runs retains one angle.
public static void CaptureTransitionFrontPortrait(){
SessionState.SetFloat("pose.transitionYawDegrees",0f);
SessionState.SetBool("pose.transitions",true);
CapturePortrait();
}
public static void CaptureTransitionBackPortrait(){
SessionState.SetFloat("pose.transitionYawDegrees",180f);
SessionState.SetBool("pose.transitions",true);
CapturePortrait();
}
// Fail with an actionable Editor import error before waiting 15 seconds for
// runtime actors that cannot exist. This is QA-only, not gameplay self-repair.
static void VerifyNativeSceneBindings(Scene scene)
{
    GameObject player = null;
    foreach (var root in scene.GetRootGameObjects())
        if (root.name == "AarisPlayer") { player = root; break; }

    var monoScript = AssetDatabase.LoadAssetAtPath<MonoScript>(
        "Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs");
    var scriptType = monoScript != null ? monoScript.GetClass() : null;
    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
        "Assets/AarisWorld/Player/AarisPlayer.prefab");
    int missing = player != null
        ? GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(player) : -1;
    bool bound = player != null &&
        player.GetComponent<AarisThirdPersonPlayer>() != null;
    bool prefabBound = prefab != null &&
        prefab.GetComponent<AarisThirdPersonPlayer>() != null;

    string fbxFile = Path.Combine(Application.dataPath,
        "Resources/AarisCharacters/Primary/Player.fbx");
    // FileInfo.Length can describe the link itself when the hydrated LFS
    // binary is accessed through a symlink. Read the target stream instead,
    // and reject raw Git LFS pointer text or an unreadable/corrupt FBX.
    bool hydrated = false;
    long fbxBytes = 0;
    string fbxProbe = "unreadable";
    try
    {
        using (var stream = File.OpenRead(fbxFile))
        {
            fbxBytes = stream.Length;
            byte[] header = new byte[18];
            int read = stream.Read(header, 0, header.Length);
            hydrated = fbxBytes > 1000 && read == header.Length &&
                System.Text.Encoding.ASCII.GetString(header) ==
                "Kaydara FBX Binary";
            fbxProbe = "stream-opened";
        }
    }
    catch (IOException) { fbxProbe = "io-error"; }
    catch (UnauthorizedAccessException) { fbxProbe = "access-denied"; }
    Debug.Log("AARIS_POSE_QA_PREFLIGHT scene=" + scene.path +
        " playerRoot=" + (player != null) +
        " rootActive=" + (player != null && player.activeInHierarchy) +
        " playerScriptClass=" + (scriptType != null ? scriptType.FullName : "null") +
        " playerBound=" + bound + " prefabBound=" + prefabBound +
        " missingRootScripts=" + missing +
        " hydratedFBX=" + hydrated +
        " fbxBytes=" + fbxBytes + " fbxProbe=" + fbxProbe +
        " fbxPath=" + fbxFile);
    if (!hydrated)
        throw new InvalidOperationException("QA_PLAYER_FBX_NOT_MATERIALIZED");
    if (scriptType != typeof(AarisThirdPersonPlayer) || !bound || !prefabBound)
        throw new InvalidOperationException("QA_PLAYER_SCRIPT_BINDING_MISSING");
}
public static void CaptureBatch(){
if(!Application.isBatchMode)throw new InvalidOperationException("BATCHMODE_REQUIRED");
try{
var scene=EditorSceneManager.OpenScene(AarisExplorerBuild.ScenePath,OpenSceneMode.Single);
if(!scene.IsValid()||!scene.isLoaded)throw new InvalidOperationException("SCENE_LOAD_FAILED");
VerifyNativeSceneBindings(scene);
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
SessionState.SetFloat("pose.air",-1f);
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
if(SessionState.GetBool("pose.motionMatrix",false)){
StageMotion(n,p,b);return;
}
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
// Editor-only root-cause probe for an actual rejected prone->stand request.
// Duplicate the motor's *new headroom* overlap shape without modifying
// collision, moving the actor or overriding the safety rejection.
static void LogRejectedGetUpHeadroom(AarisThirdPersonPlayer p,
 AarisBattleActions b){
 var capsule=p.GetComponent<CharacterController>();
 if(capsule==null||b==null)return;
 var flags=System.Reflection.BindingFlags.Instance|
  System.Reflection.BindingFlags.NonPublic;
 var type=typeof(AarisBattleActions);
 var heightField=type.GetField("standingHeight",flags);
 var centerField=type.GetField("standingCenter",flags);
 if(heightField==null||centerField==null){
  Log("GETUP_HEADROOM_QA_REFLECTION_UNAVAILABLE");return;
 }
 float standingHeight=(float)heightField.GetValue(b);
 Vector3 standingCenter=(Vector3)centerField.GetValue(b);
 float floor=standingCenter.y-standingHeight*.5f;
 float radius=Mathf.Max(.05f,capsule.radius-capsule.skinWidth);
 float currentTop=floor+capsule.height;
 float from=Mathf.Max(floor+radius,currentTop-radius*.35f);
 float to=Mathf.Max(from,floor+standingHeight-radius);
 Vector3 low=p.transform.TransformPoint(new Vector3(
  standingCenter.x,from,standingCenter.z));
 Vector3 high=p.transform.TransformPoint(new Vector3(
  standingCenter.x,to,standingCenter.z));
 Collider[] hits=Physics.OverlapCapsule(low,high,radius,
  Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore);
 int blockers=0;
 foreach(var hit in hits){
  if(hit==null||hit.transform.IsChildOf(p.transform))continue;
  if(blockers<12)Log("GETUP_HEADROOM_BLOCKER type="+
   hit.GetType().Name+" name="+hit.name+
   " pos="+hit.transform.position.ToString("F2"));
  blockers++;
 }
 Log("GETUP_HEADROOM_QA blockers="+blockers+
  " grounded="+capsule.isGrounded+
  " capsuleHeight="+capsule.height.ToString("F3")+
  " root="+p.transform.position.ToString("F3")+
  " low="+low.ToString("F3")+
  " high="+high.ToString("F3"));
}
static void StageMotion(int n,AarisThirdPersonPlayer p,AarisBattleActions b){
FinishMotionContactSummary();
motionContactStage=-1;
if(n<0||n>=MotionNames.Length){Finish("MOTION_STAGE_OUT_OF_RANGE");return;}
SessionState.SetInt("pose.step",n);SessionState.SetInt("pose.count",0);
SessionState.SetFloat("pose.time",(float)Now);
Phase(MotionNames[n]);
bool ok=true;
if(n==0){
 if(!SurveyTeleport(p)){Finish("SURVEY_LOCATION_UNAVAILABLE");return;}
 p.CancelAutoSprint();p.SetEditorLocomotionQAInput(Vector2.zero);ok=b.TryStand();
}else if(n==1){
 p.CancelAutoSprint();p.SetEditorLocomotionQAInput(Vector2.up*.38f);ok=b.TryStand();
}else if(n==2){
 p.CancelAutoSprint();p.SetEditorLocomotionQAInput(Vector2.up);ok=b.TryStand();
}else if(n==3){
 p.SetEditorLocomotionQAInput(Vector2.up);ok=b.TryStand();
 if(ok&&!p.IsAutoSprinting)p.ToggleAutoSprint();
 ok=ok&&p.IsAutoSprinting;
}else if(n==4){
 p.CancelAutoSprint();p.SetEditorLocomotionQAInput(Vector2.zero);
 ok=b.TrySetStance(AarisBattleActions.PlayerStance.Crouching);
}else if(n==5){
 // Walking/sprinting through the full terrain scene may reach a real cabin
 // before the prone->stand stage. Reset the QA route ONCE on entering the
 // low-stance locomotion block, while preserving the actual motor, collision
 // checks, real terrain and the ensuing crouch-walk/prone/crawl/get-up path.
 // This is Editor-only: no gameplay teleport, no fake stance acceptance.
 p.CancelAutoSprint();
 if(!SurveyTeleport(p)){Finish("SURVEY_LOCATION_UNAVAILABLE");return;}
 Log("EDITOR_ONLY_LOW_STANCE_OPEN_GROUND_ROUTE_RESET");
 p.SetEditorLocomotionQAInput(Vector2.up);
 ok=b.TrySetStance(AarisBattleActions.PlayerStance.Crouching);
}else if(n==6){
 p.SetEditorLocomotionQAInput(Vector2.zero);
 ok=b.TrySetStance(AarisBattleActions.PlayerStance.Prone);
}else if(n==7){
 p.SetEditorLocomotionQAInput(Vector2.up);
 ok=b.TrySetStance(AarisBattleActions.PlayerStance.Prone);
}else if(n==8){
 p.SetEditorLocomotionQAInput(Vector2.zero);ok=b.TryStand();
  if(!ok)LogRejectedGetUpHeadroom(p,b);
}else if(n==9){
 p.SetEditorLocomotionQAInput(Vector2.zero);ok=b.TryStand();
 if(ok)p.RequestJump();
}
Log("MOTION_STAGE "+MotionNames[n]+" accepted="+ok);
if(!ok)Finish("MOTION_STAGE_REJECTED_"+MotionNames[n]);
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
if(SessionState.GetBool("pose.portrait",false)&&
(b.Stance==AarisBattleActions.PlayerStance.Crouching||
 b.Stance==AarisBattleActions.PlayerStance.Prone)){
var head=animator.GetBoneTransform(HumanBodyBones.Head);
if(head==null)throw new InvalidOperationException("ACTOR_HEAD_BONE_MISSING");
float capTop=p.transform.TransformPoint(capsule.center+
 Vector3.up*(capsule.height*.5f)).y;
float excess=head.position.y-capTop;
Log("PORTRAIT_HEADROOM stance="+b.Stance+
 " excessMetres="+excess.ToString("F3"));
// The capsule changes immediately on input, while visuals intentionally
// interpolate. Only enforce the helmet ceiling on settled portrait poses.
if(!SessionState.GetBool("pose.transitions",false)&&
 (!SessionState.GetBool("pose.motionMatrix",false)||
  SessionState.GetInt("pose.count",0)>=2)&&excess>.055f)
throw new InvalidOperationException("ACTOR_HEAD_PENETRATES_CEILING_"+b.Stance);
}
// Make the visual screenshot suite fail for genuinely sideways crouch knees
// or a prone silhouette that frog-splays. The check exists only in the Unity
// Editor QA harness; there is no runtime physics, rigging or frame cost.
if(SessionState.GetBool("pose.multiangle",false)&&
 (b.Stance==AarisBattleActions.PlayerStance.Crouching||
  b.Stance==AarisBattleActions.PlayerStance.Prone)){
var leftUpper=animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
var rightUpper=animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
var leftKnee=animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
var rightKnee=animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
var leftFoot=animator.GetBoneTransform(HumanBodyBones.LeftFoot);
var rightFoot=animator.GetBoneTransform(HumanBodyBones.RightFoot);
if(leftUpper==null||rightUpper==null||leftKnee==null||rightKnee==null||
 leftFoot==null||rightFoot==null)
throw new InvalidOperationException("LOW_STANCE_LEG_BONE_MISSING");
float spread=Vector3.ProjectOnPlane(leftFoot.position-rightFoot.position,
 Vector3.up).magnitude;
if(spread>0.62f)throw new InvalidOperationException("LOW_STANCE_FOOT_SPLAY");
Log("LOW_STANCE_SPREAD stance="+b.Stance+" metres="+spread.ToString("F3"));
if(b.Stance==AarisBattleActions.PlayerStance.Crouching){
Vector3 hipRight=Vector3.ProjectOnPlane(
 rightUpper.position-leftUpper.position,Vector3.up);
if(hipRight.sqrMagnitude<0.02f)
throw new InvalidOperationException("CROUCH_HIP_AXIS_INVALID");
hipRight.Normalize();
float leftLateral=Vector3.Dot(Vector3.ProjectOnPlane(
 leftKnee.position-leftUpper.position,Vector3.up),hipRight);
float rightLateral=Vector3.Dot(Vector3.ProjectOnPlane(
 rightKnee.position-rightUpper.position,Vector3.up),hipRight);
float kneeHeightDelta=Mathf.Abs(leftKnee.position.y-rightKnee.position.y);
Log("CROUCH_KNEE_ALIGNMENT leftLateral="+leftLateral.ToString("F3")+
" rightLateral="+rightLateral.ToString("F3")+
" kneeHeightDelta="+kneeHeightDelta.ToString("F3"));
if(Mathf.Abs(leftLateral)>0.25f||Mathf.Abs(rightLateral)>0.25f||
 kneeHeightDelta>0.12f)
throw new InvalidOperationException("CROUCH_KNEE_SIDEWAYS_OR_MISALIGNED");
}
// Only the settled prone orbit sample is an appropriate foot-contact gate:
// the first frame deliberately captures the transition from crouching.
if(b.Stance==AarisBattleActions.PlayerStance.Prone&&terrainFound&&
 SessionState.GetInt("pose.count",0)>=3){
// The final 4-angle orbit frame is index 3, not index 4.
 // Measure the true settled knee axis using the same thigh plane as
 // crouch; this marker is mandatory for the native Python auditor.
 Vector3 hipSide=Vector3.ProjectOnPlane(
  rightUpper.position-leftUpper.position,Vector3.up);
 if(hipSide.sqrMagnitude<0.02f)
  throw new InvalidOperationException("PRONE_HIP_AXIS_INVALID");
 hipSide.Normalize();
 float leftLateral=Vector3.Dot(Vector3.ProjectOnPlane(
  leftKnee.position-leftUpper.position,Vector3.up),hipSide);
 float rightLateral=Vector3.Dot(Vector3.ProjectOnPlane(
  rightKnee.position-rightUpper.position,Vector3.up),hipSide);
 float kneeHeightDelta=Mathf.Abs(leftKnee.position.y-rightKnee.position.y);
 Log("PRONE_KNEE_ALIGNMENT leftLateral="+leftLateral.ToString("F3")+
  " rightLateral="+rightLateral.ToString("F3")+
  " kneeHeightDelta="+kneeHeightDelta.ToString("F3"));
 if(Mathf.Abs(leftLateral)>0.25f||Mathf.Abs(rightLateral)>0.25f||
  kneeHeightDelta>0.12f)
  throw new InvalidOperationException("PRONE_KNEE_SIDEWAYS_OR_MISALIGNED");
float leftAnkle=leftFoot.position.y-terrainY;
float rightAnkle=rightFoot.position.y-terrainY;
float leftKneeHeight=leftKnee.position.y-terrainY;
float rightKneeHeight=rightKnee.position.y-terrainY;
Log("SETTLED_PRONE_CONTACT leftAnkle="+leftAnkle.ToString("F3")+
" rightAnkle="+rightAnkle.ToString("F3")+
" leftKnee="+leftKneeHeight.ToString("F3")+
" rightKnee="+rightKneeHeight.ToString("F3"));
if(leftAnkle<0.04f||leftAnkle>0.18f||
 rightAnkle<0.04f||rightAnkle>0.18f)
throw new InvalidOperationException("SETTLED_PRONE_BOOT_CONTACT_INVALID");
if(leftKneeHeight>0.33f||rightKneeHeight>0.33f)
throw new InvalidOperationException("SETTLED_PRONE_KNEE_TOO_HIGH");
}
}
HumanBodyBones[] bones={HumanBodyBones.Hips,HumanBodyBones.Chest,
HumanBodyBones.Head,HumanBodyBones.LeftFoot,HumanBodyBones.RightFoot,
HumanBodyBones.LeftHand,HumanBodyBones.RightHand,
HumanBodyBones.LeftUpperLeg,HumanBodyBones.RightUpperLeg,
HumanBodyBones.LeftLowerLeg,HumanBodyBones.RightLowerLeg};
Log("STANCE_BASIS stance="+b.Stance+
" rootForward="+p.transform.forward.ToString("F3")+
" rootRight="+p.transform.right.ToString("F3")+
" actorYaw="+p.transform.eulerAngles.y.ToString("F2"));
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
// Only the Editor QA observes shoe movement at full Play-Mode cadence.
// Three orbit photos at different instants cannot prove foot sliding; this
// monitor samples the true animated ankles between photographs instead.
static int motionContactStage=-1,motionContactSamples;
static float motionLastSampleTime,motionLastLGap,motionLastRGap;
static float motionContactMaxSpeed,motionContactSumSpeed;
static Vector3 motionLastLeftFoot,motionLastRightFoot;
static void FinishMotionContactSummary(){
 if(motionContactStage<0)return;
 float mean=motionContactSamples>0
  ?motionContactSumSpeed/motionContactSamples:0f;
 Log("MOTION_CONTACT_QA state="+MotionNames[motionContactStage]+
  " samples="+motionContactSamples+
  " meanMetresPerSecond="+mean.ToString("F3")+
  " maxMetresPerSecond="+motionContactMaxSpeed.ToString("F3"));
 motionContactStage=-1;
}
static float MotionAnkleTerrainGap(Transform foot){
 if(foot==null)return float.NaN;
 Vector3 p=foot.position;
 foreach(var tile in Terrain.activeTerrains){
  if(tile==null||tile.terrainData==null)continue;
  Vector3 origin=tile.transform.position,size=tile.terrainData.size;
  if(p.x>=origin.x&&p.z>=origin.z&&
     p.x<=origin.x+size.x&&p.z<=origin.z+size.z)
   return p.y-(origin.y+tile.SampleHeight(p));
 }
 return float.NaN;
}
static void ObserveMotionContacts(AarisThirdPersonPlayer p,int stage){
 if(stage<0||stage>=MotionNames.Length)return;
 var anim=p.GetComponentInChildren<Animator>(true);
 if(anim==null||!anim.isHuman)return;
 var left=anim.GetBoneTransform(HumanBodyBones.LeftFoot);
 var right=anim.GetBoneTransform(HumanBodyBones.RightFoot);
 if(left==null||right==null)return;
 float time=Time.unscaledTime;
 float lGap=MotionAnkleTerrainGap(left),rGap=MotionAnkleTerrainGap(right);
 if(motionContactStage!=stage){
  FinishMotionContactSummary();
  motionContactStage=stage;
  motionContactSamples=0;motionContactMaxSpeed=0f;
  motionContactSumSpeed=0f;
  motionLastSampleTime=time;
  motionLastLeftFoot=left.position;motionLastRightFoot=right.position;
  motionLastLGap=lGap;motionLastRGap=rGap;
  return;
 }
 float dt=time-motionLastSampleTime;
 if(dt<0.018f)return; // No duplicate Editor ticks on the same frame.
 if(dt<0.18f&&p.GetComponent<CharacterController>().isGrounded&&
    (stage==1||stage==2||stage==3||stage==5||stage==7)){
  // A foot hovering or actively swinging is not a planted contact.
  // Contact must hold both at previous and current sampled instants.
  if(lGap>=-0.06f&&lGap<=0.15f&&
     motionLastLGap>=-0.06f&&motionLastLGap<=0.15f){
   Vector3 delta=Vector3.ProjectOnPlane(
    left.position-motionLastLeftFoot,Vector3.up);
   float speed=delta.magnitude/dt;
   if(speed<12f){motionContactSamples++;motionContactSumSpeed+=speed;
    motionContactMaxSpeed=Mathf.Max(motionContactMaxSpeed,speed);}
  }
  if(rGap>=-0.06f&&rGap<=0.15f&&
     motionLastRGap>=-0.06f&&motionLastRGap<=0.15f){
   Vector3 delta=Vector3.ProjectOnPlane(
    right.position-motionLastRightFoot,Vector3.up);
   float speed=delta.magnitude/dt;
   if(speed<12f){motionContactSamples++;motionContactSumSpeed+=speed;
    motionContactMaxSpeed=Mathf.Max(motionContactMaxSpeed,speed);}
  }
 }
 motionLastSampleTime=time;
 motionLastLeftFoot=left.position;motionLastRightFoot=right.position;
 motionLastLGap=lGap;motionLastRGap=rGap;
}
// The native photo matrix logs measurable bones at every sample, not only
// the first and last photograph. This is Editor-only: no Android cost.
// Ground samples are per shoe, rather than assuming both shoes stand at
// the capsule center (which is wrong on ramps and sloped terrain).
static void LogMotionRigQuality(AarisThirdPersonPlayer p,
 AarisBattleActions b,int frame){
 var animator=p.GetComponentInChildren<Animator>(true);
 if(animator==null||!animator.isHuman)return;
 var lh=animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
 var rh=animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
 var lk=animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
 var rk=animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
 var lf=animator.GetBoneTransform(HumanBodyBones.LeftFoot);
 var rf=animator.GetBoneTransform(HumanBodyBones.RightFoot);
 var hips=animator.GetBoneTransform(HumanBodyBones.Hips);
 var head=animator.GetBoneTransform(HumanBodyBones.Head);
 if(lh==null||rh==null||lk==null||rk==null||
    lf==null||rf==null||hips==null||head==null)return;
 Vector3 hipSide=Vector3.ProjectOnPlane(
  rh.position-lh.position,Vector3.up);
 if(hipSide.sqrMagnitude<0.01f)return;
 hipSide.Normalize();
 // Measure real lateral separation separately from fore/aft stride.
 // Foot-to-foot distance alone is not proof of sideways skating.
 Vector3 feetXZ=Vector3.ProjectOnPlane(rf.position-lf.position,Vector3.up);
 float footLateralSpan=Mathf.Abs(Vector3.Dot(feetXZ,hipSide));
 float footForwardStagger=Vector3.Dot(feetXZ,
  Vector3.Cross(hipSide,Vector3.up));
 float leftKneeSide=Vector3.Dot(Vector3.ProjectOnPlane(
  lk.position-lh.position,Vector3.up),hipSide);
 float rightKneeSide=Vector3.Dot(Vector3.ProjectOnPlane(
  rk.position-rh.position,Vector3.up),hipSide);
 float footSpread=feetXZ.magnitude;
 Vector3 spineForward=Vector3.ProjectOnPlane(
  head.position-hips.position,Vector3.up);
 float spineFacing=spineForward.sqrMagnitude>0.01f
  ?Vector3.Dot(spineForward.normalized,p.transform.forward):float.NaN;
 float leftGap=float.NaN,rightGap=float.NaN;
 foreach(var tile in Terrain.activeTerrains){
  if(tile==null||tile.terrainData==null)continue;
  Vector3 origin=tile.transform.position,sz=tile.terrainData.size;
  Vector3 lp=lf.position,rp=rf.position;
  if(float.IsNaN(leftGap)&&lp.x>=origin.x&&lp.z>=origin.z&&
     lp.x<=origin.x+sz.x&&lp.z<=origin.z+sz.z)
   leftGap=lp.y-(origin.y+tile.SampleHeight(lp));
  if(float.IsNaN(rightGap)&&rp.x>=origin.x&&rp.z>=origin.z&&
     rp.x<=origin.x+sz.x&&rp.z<=origin.z+sz.z)
   rightGap=rp.y-(origin.y+tile.SampleHeight(rp));
 }
 float leftSpeed=float.NaN,rightSpeed=float.NaN;
 float seconds=Time.unscaledTime;
 if(frame>0){
  float dt=seconds-SessionState.GetFloat("pose.motionMetricTime",seconds);
  if(dt>0.025f){
   Vector3 oldL=new Vector3(
    SessionState.GetFloat("pose.motionLX",lf.position.x),0f,
    SessionState.GetFloat("pose.motionLZ",lf.position.z));
   Vector3 oldR=new Vector3(
    SessionState.GetFloat("pose.motionRX",rf.position.x),0f,
    SessionState.GetFloat("pose.motionRZ",rf.position.z));
   leftSpeed=Vector3.Distance(
    oldL,Vector3.ProjectOnPlane(lf.position,Vector3.up))/dt;
   rightSpeed=Vector3.Distance(
    oldR,Vector3.ProjectOnPlane(rf.position,Vector3.up))/dt;
  }
 }
 SessionState.SetFloat("pose.motionMetricTime",seconds);
 SessionState.SetFloat("pose.motionLX",lf.position.x);
 SessionState.SetFloat("pose.motionLZ",lf.position.z);
 SessionState.SetFloat("pose.motionRX",rf.position.x);
 SessionState.SetFloat("pose.motionRZ",rf.position.z);
 Log("MOTION_RIG state="+MotionNames[SessionState.GetInt("pose.step",0)]+
  " frame="+frame+
  " leftGap="+leftGap.ToString("F3")+
  " rightGap="+rightGap.ToString("F3")+
  " spread="+footSpread.ToString("F3")+
  " lateralFootSpan="+footLateralSpan.ToString("F3")+
  " foreAftFootStagger="+footForwardStagger.ToString("F3")+
  " leftKneeSide="+leftKneeSide.ToString("F3")+
  " rightKneeSide="+rightKneeSide.ToString("F3")+
  " torsoFacingDot="+spineFacing.ToString("F3")+
  " leftFootSpeed="+leftSpeed.ToString("F3")+
  " rightFootSpeed="+rightSpeed.ToString("F3"));
}
// Native, read-only transition diagnostic: only a pose that is actually
// blending is required to release both previous low-stance world locks.
// The reflection stays in Editor QA, never the Android gameplay hot path.
static void LogTransitionContact(AarisThirdPersonPlayer p,
    AarisBattleActions b,int sampleIndex){
var actor=p.GetComponent<AarisCharacterVisual>();
if(actor==null)return;
var type=typeof(AarisCharacterVisual);
var flags=System.Reflection.BindingFlags.Instance|
    System.Reflection.BindingFlags.NonPublic;
float crouch=(float)type.GetField("crouchBlend",flags).GetValue(actor);
float prone=(float)type.GetField("proneBlend",flags).GetValue(actor);
bool left=(bool)type.GetField("leftLowPlantLocked",flags).GetValue(actor);
bool right=(bool)type.GetField("rightLowPlantLocked",flags).GetValue(actor);
bool transitioning=(crouch>0.03f&&crouch<0.96f)||
                   (prone>0.03f&&prone<0.96f);
Log("TRANSITION_CONTACT stance="+b.Stance+
" frame="+sampleIndex+" crouch="+crouch.ToString("F3")+
" prone="+prone.ToString("F3")+
" leftLocked="+left+" rightLocked="+right+
" blending="+transitioning);
if(transitioning&&(left||right))
throw new InvalidOperationException("TRANSITION_FOOT_ANCHOR_STALE");
var animator=p.GetComponentInChildren<Animator>(true);
if(animator==null||!animator.isHuman)return;
var lh=animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
var rh=animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
var lk=animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
var rk=animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
var lf=animator.GetBoneTransform(HumanBodyBones.LeftFoot);
var rf=animator.GetBoneTransform(HumanBodyBones.RightFoot);
if(lh==null||rh==null||lk==null||rk==null||lf==null||rf==null)return;
// The photo floor is deliberately cosmetic. Check native *terrain* under
// both actual ankle bones so an early pose cannot pass while buried below
// the real gameplay surface. This only runs in Editor transition QA.
float worstGroundGap=float.PositiveInfinity;
int terrainFootSamples=0;
foreach(var shoe in new[]{lf,rf}){
 foreach(var tile in Terrain.activeTerrains){
  if(tile==null||tile.terrainData==null)continue;
  Vector3 origin=tile.transform.position;
  Vector3 size=tile.terrainData.size;
  Vector3 pos=shoe.position;
  if(pos.x<origin.x||pos.x>origin.x+size.x||
     pos.z<origin.z||pos.z>origin.z+size.z)continue;
  float terrainY=origin.y+tile.SampleHeight(pos);
  worstGroundGap=Mathf.Min(worstGroundGap,pos.y-terrainY);
  terrainFootSamples++;
  break;
 }
}
if(terrainFootSamples>0){
Log("TRANSITION_TERRAIN_FEET stance="+b.Stance+
" frame="+sampleIndex+" worstGap="+worstGroundGap.ToString("F3"));
if(transitioning&&worstGroundGap< -0.15f)
 throw new InvalidOperationException("TRANSITION_FOOT_BURIED");
// All samples in this opt-in stage are stationary; the minimum of BOTH
// actual ankle clearances must approach terrain before get-up completes.
// A 70cm full-body float used to pass because only BURIED feet were gated.
// Skip the first handoff sample while the authored prone clip still dominates.
if(b.Stance==AarisBattleActions.PlayerStance.Standing&&
 sampleIndex>=1&&worstGroundGap>0.24f)
 throw new InvalidOperationException("RETURN_TO_STAND_FOOT_FLOAT");
}
Vector3 hipRight=Vector3.ProjectOnPlane(rh.position-lh.position,Vector3.up);
if(hipRight.sqrMagnitude<0.02f)return;
hipRight.Normalize();
Vector3 feetXZ=Vector3.ProjectOnPlane(rf.position-lf.position,Vector3.up);
float footLateralSpan=Mathf.Abs(Vector3.Dot(feetXZ,hipRight));
float footForwardStagger=Vector3.Dot(feetXZ,
 Vector3.Cross(hipRight,Vector3.up));
float lSide=Vector3.Dot(Vector3.ProjectOnPlane(
 lk.position-lh.position,Vector3.up),hipRight);
float rSide=Vector3.Dot(Vector3.ProjectOnPlane(
 rk.position-rh.position,Vector3.up),hipRight);
float spread=feetXZ.magnitude;
Log("TRANSITION_LEGS stance="+b.Stance+" frame="+sampleIndex+
" spread="+spread.ToString("F3")+
" lateralFootSpan="+footLateralSpan.ToString("F3")+
" foreAftFootStagger="+footForwardStagger.ToString("F3")+
" leftKneeSide="+lSide.ToString("F3")+
" rightKneeSide="+rSide.ToString("F3"));
}
static void Finish(string reason){
FinishMotionContactSummary();
Log(reason);SessionState.SetString("pose.state","");Phase("07_COMPLETE");
if(EditorApplication.isPlaying)EditorApplication.isPlaying=false;
if(SessionState.GetBool("pose.batch",false)){
SessionState.EraseBool("pose.batch");
SessionState.EraseBool("pose.diag");
int code=(reason=="SUCCESS_18_REAL_FRAMES"||
 reason=="SUCCESS_PRONE_DIAGNOSTIC"||
 reason=="SUCCESS_PRONE_GEOMETRY_ONLY"||
 reason=="SUCCESS_STANCE_JUMP_MOTOR"||
 reason=="SUCCESS_7_ACTOR_PORTRAITS"||
 reason=="SUCCESS_16_ACTOR_MULTIANGLE_PORTRAITS"||
 reason=="SUCCESS_16_STANCE_TRANSITION_FRAMES"||
 reason=="SUCCESS_30_MOTION_MATRIX_FRAMES")?0:2;
SessionState.EraseBool("pose.geometryOnly");
SessionState.EraseBool("pose.stanceJump");
SessionState.EraseBool("pose.portrait");
SessionState.EraseBool("pose.multiangle");
SessionState.EraseBool("pose.transitions");
SessionState.EraseBool("pose.motionMatrix");
SessionState.SetFloat("pose.transitionYawDegrees",90f);
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
// The flight system legitimately disables the foot motor while the actor
// rides the aircraft. Resolve both QA components from the SAME live player
// object before falling back to a scene-wide motor search. GetComponent
// still works for a disabled Behaviour; the runtime controller is unchanged.
var d=UnityEngine.Object.FindFirstObjectByType<AarisOfflineDrop>();
var p=d!=null?d.GetComponent<AarisThirdPersonPlayer>():null;
if(p==null)p=UnityEngine.Object.FindFirstObjectByType<AarisThirdPersonPlayer>();
if(p==null||d==null){
if(Now-SessionState.GetFloat("pose.time",(float)Now)>15){
// Distinguish a missing imported script, inactive saved component and
// normal aircraft-disabled motor before changing ANY production logic.
var roots = SceneManager.GetActiveScene().GetRootGameObjects();
GameObject savedPlayer = null;
foreach (var root in roots)
    if (root.name == "AarisPlayer") { savedPlayer = root; break; }
var motors = UnityEngine.Object.FindObjectsByType<AarisThirdPersonPlayer>(
    FindObjectsInactive.Include, FindObjectsSortMode.None);
var drops = UnityEngine.Object.FindObjectsByType<AarisOfflineDrop>(
    FindObjectsInactive.Include, FindObjectsSortMode.None);
Log("QA_BOOTSTRAP_DETAIL roots="+roots.Length+
    " rootFound="+(savedPlayer!=null)+
    " rootActive="+(savedPlayer!=null&&savedPlayer.activeInHierarchy)+
    " rootMotor="+(savedPlayer!=null&&
        savedPlayer.GetComponent<AarisThirdPersonPlayer>()!=null)+
    " rootMissingScripts="+(savedPlayer!=null?
        GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(savedPlayer):-1)+
    " motorsIncludeInactive="+motors.Length+
    " dropsIncludeInactive="+drops.Length+
    " mainCamera="+(Camera.main!=null));
Log("QA_BOOTSTRAP_LOOKUP player="+(p!=null)+" drop="+(d!=null)+
" scene="+SceneManager.GetActiveScene().path);
Finish("NO_PLAYER_OR_DROP");
}
return;
}
var b=p.GetComponent<AarisBattleActions>();
int n=SessionState.GetInt("pose.step",-1);
double age=Now-SessionState.GetFloat("pose.time",(float)Now);
if(n<0){
if(d.Phase==AarisOfflineDrop.DropPhase.InPlane&&age>1.6){
d.OnAction();SessionState.SetFloat("pose.air",(float)Now);}
if(d.Phase==AarisOfflineDrop.DropPhase.Freefall){
float airStarted=SessionState.GetFloat("pose.air",-1f);
if(airStarted<0f){SessionState.SetFloat("pose.air",(float)Now);}
else if(Now-airStarted>.9)d.OnAction();
}
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
if(SessionState.GetBool("pose.motionMatrix",false))
 ObserveMotionContacts(p,n);
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
bool portrait=SessionState.GetBool("pose.portrait",false);
bool motionMatrix=SessionState.GetBool("pose.motionMatrix",false);
bool multiangle=portrait&&SessionState.GetBool("pose.multiangle",false);
bool transitions=portrait&&SessionState.GetBool("pose.transitions",false);
bool orbitStance=motionMatrix||(multiangle&&n<=2);
bool transitionFrames=transitions&&(n==1||n==2||n==3);
int needed=portrait?(motionMatrix?3:n==5?2:transitionFrames?4:orbitStance?4:1):
 fastProbe?(n==2?1:0):3;
// Each orbit frame gets its own measured Play Mode pose/readback. The
// existing seven front-view frames retain their original timings.
double readyAge=portrait?(motionMatrix?(n==9?.18+count*.17:.42+count*.20):
 n==5?(count==0?.30:1.15):
 transitionFrames?.10+count*.18:
 orbitStance?.52+count*.22:.52):.25+count*.30;
if(count<needed&&age>readyAge){
try{
Camera cam=Camera.main;if(cam==null)throw new InvalidOperationException("CAMERA_MISSING");
int imageWidth=portrait?640:SessionState.GetBool("pose.diag",false)?640:960;
int imageHeight=portrait?720:SessionState.GetBool("pose.diag",false)?360:540;
var rt=RenderTexture.GetTemporary(imageWidth,imageHeight,24);
var old=cam.targetTexture;var active=RenderTexture.active;
Vector3 savedCameraPosition=cam.transform.position;
Quaternion savedCameraRotation=cam.transform.rotation;
int savedMask=cam.cullingMask;
CameraClearFlags savedClear=cam.clearFlags;
Color savedBackground=cam.backgroundColor;
float savedFov=cam.fieldOfView;
GameObject portraitGround=null;
Material groundMaterial=null;
var image=new Texture2D(imageWidth,imageHeight,TextureFormat.RGB24,false);
try{
// The real game camera usually faces the aircraft or canopy and makes
// stance screenshots unusable. For PHOTO readback only, move its view to
// face the player at human scale, then restore the untouched game camera.
float eye=b.Stance==AarisBattleActions.PlayerStance.Prone ? 0.42f :
 b.Stance==AarisBattleActions.PlayerStance.Crouching ? 0.85f : 1.18f;
Vector3 target=p.transform.position+Vector3.up*eye;
Vector3 viewDirection=orbitStance
 ?Quaternion.AngleAxis(90f*count,Vector3.up)*p.transform.forward
 :transitionFrames
 ?Quaternion.AngleAxis(
    SessionState.GetFloat("pose.transitionYawDegrees",90f),Vector3.up)*
    p.transform.forward
 :p.transform.forward;
Vector3 photoPosition=target+viewDirection*3.0f+Vector3.up*0.42f;
cam.transform.position=photoPosition;
cam.transform.rotation=Quaternion.LookRotation(target-photoPosition,Vector3.up);
if(portrait){cam.cullingMask=1 << p.gameObject.layer;
cam.clearFlags=CameraClearFlags.SolidColor;
cam.backgroundColor=new Color(.11f,.15f,.19f,1f);
cam.fieldOfView=50f;
// The QA-only neutral floor makes foot/hand contact visible, without the
// expensive terrain render. It has no collider and is destroyed after readback.
portraitGround=GameObject.CreatePrimitive(PrimitiveType.Plane);
portraitGround.layer=p.gameObject.layer;
portraitGround.transform.position=p.transform.position+Vector3.down*0.04f;
portraitGround.transform.localScale=Vector3.one*0.34f;
Collider floorCollider=portraitGround.GetComponent<Collider>();
if(floorCollider!=null)floorCollider.enabled=false;
Shader floorShader=Shader.Find("Universal Render Pipeline/Unlit");
if(floorShader==null)floorShader=Shader.Find("Unlit/Color");
if(floorShader!=null){
groundMaterial=new Material(floorShader);
groundMaterial.color=new Color(0.21f,0.24f,0.25f,1f);
portraitGround.GetComponent<Renderer>().sharedMaterial=groundMaterial;
}
}
cam.targetTexture=rt;cam.Render();RenderTexture.active=rt;
image.ReadPixels(new Rect(0,0,imageWidth,imageHeight),0,0,false);image.Apply(false);
var bytes=image.EncodeToJPG(82);
if(bytes.Length<4000)throw new InvalidOperationException("EMPTY_FRAME");
File.WriteAllBytes(Dir+"/"+(motionMatrix?MotionNames[n]:Names[n])+"_f"+count+".jpg",bytes);
}finally{
cam.transform.SetPositionAndRotation(savedCameraPosition,savedCameraRotation);
cam.cullingMask=savedMask;cam.clearFlags=savedClear;
cam.backgroundColor=savedBackground;cam.fieldOfView=savedFov;
if(portraitGround!=null)UnityEngine.Object.DestroyImmediate(portraitGround);
if(groundMaterial!=null)UnityEngine.Object.DestroyImmediate(groundMaterial);
cam.targetTexture=old;RenderTexture.active=active;
UnityEngine.Object.DestroyImmediate(image);RenderTexture.ReleaseTemporary(rt);}
SessionState.SetInt("pose.count",count+1);
Log("FRAME "+(motionMatrix?MotionNames[n]:Names[n])+" "+count+" stance="+b.Stance+
" viewDegrees="+(orbitStance?90*count:transitionFrames?
SessionState.GetFloat("pose.transitionYawDegrees",90f):0)+
" air="+p.IsInAir+" pos="+p.transform.position.ToString("F2"));
if(transitionFrames)LogTransitionContact(p,b,count);
// A 30-frame capture contains sequential camera samples, not the same
// time instant. Include their real game-clock timestamps for bone audits.
if(motionMatrix)
 Log("MOTION_TIMESTAMP stage="+MotionNames[n]+" frame="+count+
 " unscaledSeconds="+Time.unscaledTime.ToString("F3"));
// Measure both the first pose and its final settled multi-angle frame.
// Editor-only QA: zero work or allocations in Android players.
if(motionMatrix || count==0 || (orbitStance && count==needed-1))
 LogPhysicalHeight(p,b);
if(motionMatrix)LogMotionRigQuality(p,b,count);
}catch(Exception ex){Finish("ERROR_"+ex.GetType().Name+"_"+ex.Message);return;}
}
if(age>(portrait?(motionMatrix?1.06:n==5?1.35:transitionFrames?.84:
 orbitStance?1.40:.70):fastProbe?.38:1.3)&&
 SessionState.GetInt("pose.count",0)>=needed){
if(SessionState.GetBool("pose.diag",false)&&n>=2){
p.CancelAutoSprint();Finish("SUCCESS_PRONE_DIAGNOSTIC");
}else if(n+1>=(motionMatrix?MotionNames.Length:Names.Length)){p.CancelAutoSprint();
if(motionMatrix)p.ClearEditorLocomotionQAInput();
Finish(portrait?(motionMatrix?"SUCCESS_30_MOTION_MATRIX_FRAMES":
transitions?"SUCCESS_16_STANCE_TRANSITION_FRAMES":
multiangle?"SUCCESS_16_ACTOR_MULTIANGLE_PORTRAITS":
"SUCCESS_7_ACTOR_PORTRAITS"):"SUCCESS_18_REAL_FRAMES");}
else Stage(n+1,p,b);}
}
}
}
