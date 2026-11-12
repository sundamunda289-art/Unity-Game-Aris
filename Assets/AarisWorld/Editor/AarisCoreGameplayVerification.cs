using System;
using System.IO;
using System.Reflection;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace Aaris.MapReview.Editor
{
    [InitializeOnLoad]
    public static class AarisCoreGameplayVerification
    {
        static readonly string Root = Path.Combine(Path.GetTempPath(), "aaris-core-gameplay-verification");
        const string Scene = "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";
        const string Session = "AarisCoreGameplayVerificationActiveV1";
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static int stage;
        static AarisThirdPersonPlayer player;
        static AarisBattleActions battle;
        static Animator rig;
        static AarisOfflineGlooWall wall;
        static int walls;
        static AarisCoreGameplayVerification()
        {
            EditorApplication.update += Tick;
            Application.logMessageReceived += LogRuntimeError;
        }

        // Run with -batchmode -executeMethod Aaris.MapReview.Editor.AarisCoreGameplayVerification.Run.
        // Omit -quit: this verifier exits after leaving Play Mode.
        public static void Run()
        {
            Directory.CreateDirectory(Root);
            SessionState.SetBool(Session, false);
            SessionState.SetBool(Session + "Finish", false);
            SessionState.SetInt(Session + "Errors", 0);
            SessionState.SetFloat(Session + "Deadline", (float)EditorApplication.timeSinceStartup + 240f);
            SessionState.SetBool(Session + "Start", true);
        }

        static void Finish(bool failed)
        {
            if (failed) SessionState.SetInt(Session + "Errors", SessionState.GetInt(Session + "Errors", 0) + 1);
            SessionState.SetBool(Session, false);
            SessionState.SetBool(Session + "Finish", true);
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        }

        static void Note(string message) => File.AppendAllText(Root + "/result.txt", message + "\n");
        static void Check(bool result, string name)
        {
            Note((result ? "PASS " : "FAIL ") + name);
            if (!result) throw new InvalidOperationException(name);
        }

        static void LogRuntimeError(string condition, string stack, LogType type)
        {
            if (!SessionState.GetBool(Session, false) ||
                (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)) return;
            SessionState.SetInt(Session + "Errors", SessionState.GetInt(Session + "Errors", 0) + 1);
            Note("RUNTIME_ERROR " + condition.Split('\n')[0]);
        }

        static float HeadHeight() => rig.GetBoneTransform(HumanBodyBones.Head).position.y - player.transform.position.y;

        static void Tick()
        {
            try
            {
                if (SessionState.GetBool(Session + "Finish", false))
                {
                    if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                    SessionState.SetBool(Session + "Finish", false);
                    int errors = SessionState.GetInt(Session + "Errors", 0);
                    Note(errors == 0 ? "VERIFICATION_PASS" : "VERIFICATION_FAIL errors=" + errors);
                    if (Application.isBatchMode) EditorApplication.Exit(errors == 0 ? 0 : 1);
                    return;
                }
                if (SessionState.GetBool(Session, false) && EditorApplication.timeSinceStartup >
                    SessionState.GetFloat(Session + "Deadline", float.MaxValue))
                {
                    Note("FAIL Play Mode verification timeout");
                    Finish(true);
                    return;
                }
                if (!SessionState.GetBool(Session, false))
                {
                    if (!SessionState.GetBool(Session + "Start", false) || EditorApplication.isPlayingOrWillChangePlaymode ||
                        EditorApplication.isUpdating || EditorApplication.isCompiling) return;
                    SessionState.SetBool(Session + "Start", false);
                    stage = 0;
                    File.WriteAllText(Root + "/result.txt", "UNITY " + Application.unityVersion + "\n");
                    for (int i = 0; i < SceneManager.sceneCount; i++)
                        Check(!SceneManager.GetSceneAt(i).isDirty, "Scene has no pending edits");
                    foreach (string name in new[] { "idle", "walk", "run", "sprint", "crouch", "crouch_walk", "prone", "crawl", "punch", "jump", "jump_start", "jump_land", "shoot" })
                    {
                        string path = "Assets/Resources/AarisCharacters/Primary/Animations/" + name + ".fbx";
                        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                        var assets = AssetDatabase.LoadAllAssetsAtPath(path);
                        var clip = assets.OfType<AnimationClip>().FirstOrDefault(c => c.humanMotion && c.length > 0.04f && !c.name.StartsWith("__preview__"));
                        var avatar = assets.OfType<Avatar>().FirstOrDefault(a => a.isHuman && a.isValid);
                        Check(clip != null && avatar != null, name + " Humanoid import");
                    }
                    int materials = 0;
                    foreach (string guid in AssetDatabase.FindAssets("t:Material",
                        new[] { "Assets/TerrainDemoScene_URP/Prefabs/Trees" }))
                    {
                        string path = AssetDatabase.GUIDToAssetPath(guid);
                        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
                        Check(material != null && material.shader != null && material.shader.isSupported &&
                            !ShaderUtil.GetShaderMessages(material.shader).Any(m => m.severity.ToString() == "Error"),
                            "Tree material shader: " + path);
                        materials++;
                    }
                    Check(materials > 0, "Native tree materials loaded");
                    var soundClips = Resources.LoadAll<AudioClip>("SFX");
                    Check(soundClips.Length >= AarisAudioDirector.RequiredCueKeys.Count + 15 && soundClips.All(clip =>
                        clip.samples > 1000 && clip.channels == 1), "All original and recorded offline AudioClips load from Resources");
                    var glooClip = soundClips.Single(clip => clip.name == "gloo_deploy");
                    var waveform = new float[glooClip.samples];
                    Check(glooClip.GetData(waveform, 0) && waveform.Any(sample => Mathf.Abs(sample) > 0.05f),
                        "Gloo deployment clip contains audio samples");
                    if (SceneManager.GetActiveScene().path != Scene)
                        EditorSceneManager.OpenScene(Scene, OpenSceneMode.Single);
                    Check(SceneManager.GetActiveScene().path == Scene, "Native terrain scene open");
                    int missing = 0;
                    foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                        foreach (var child in root.GetComponentsInChildren<Transform>(true))
                            missing += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject);
                    Check(missing == 0, "Native scene has no missing scripts");
                    var savedPlayer = Object.FindFirstObjectByType<AarisThirdPersonPlayer>();
                    Check(savedPlayer != null, "Saved player exists");
                    Vector3 p = savedPlayer.transform.position;
                    SessionState.SetFloat(Session + "X", p.x);
                    SessionState.SetFloat(Session + "Y", p.y);
                    SessionState.SetFloat(Session + "Z", p.z);
                    SessionState.SetBool(Session, true);
                    EditorApplication.isPlaying = true;
                    return;
                }
                if (!EditorApplication.isPlaying || stage != 0) return;
                player = Object.FindFirstObjectByType<AarisThirdPersonPlayer>();
                if (player == null) return;
                var visual = player.GetComponent<AarisCharacterVisual>();
                if (visual == null || !visual.HasWorkingHumanoid) return;
                stage = 1;
                Check(player.AimCamera != null, "Play Mode player and camera");
                rig = visual.GetComponentInChildren<Animator>(true);
                Check(rig != null && rig.isHuman, "Runtime Humanoid graph active");
                var audio = player.GetComponent<AarisAudioDirector>();
                var listener = player.AimCamera.GetComponent<AudioListener>();
                Check(audio != null && audio.isActiveAndEnabled && listener != null && listener.isActiveAndEnabled &&
                    Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None).Count(item => item.isActiveAndEnabled) == 1,
                    "Gameplay has one active camera AudioListener and an audio director");
                var audioRoot = (GameObject)typeof(AarisAudioDirector).GetField("audioRoot", Private).GetValue(audio);
                var sources = audioRoot.GetComponentsInChildren<AudioSource>();
                Check(sources.Length == 16 && sources.Count(s => s.loop) == 4 &&
                    sources.Count(s => !s.loop && s.spatialBlend == 0f) == 4 &&
                    sources.Count(s => !s.loop && s.spatialBlend == 1f) == 8,
                    "Four local voices, eight spatial world voices and four loops are pooled");
                bool Cue(string name, float blend = 0f) => sources.Any(source =>
                    source.clip != null && source.clip.name == name && source.spatialBlend == blend);
                void TickAudio() => typeof(AarisAudioDirector).GetMethod("LateUpdate", Private).Invoke(audio, null);
                var drop = player.GetComponent<AarisOfflineDrop>();
                Check(drop != null && drop.Phase == AarisOfflineDrop.DropPhase.InPlane, "Audio test starts in the airplane");
                TickAudio();
                Check(Cue("plane_engine_loop"), "Airplane starts its engine audio");
                drop.OnAction();
                TickAudio();
                Check(drop.IsFreefall && Cue("jump_whoosh") && Cue("wind_rush_loop"), "Plane exit starts whoosh and freefall wind");
                typeof(AarisOfflineDrop).GetField("airborneSince", Private).SetValue(drop, Time.time - 1f);
                drop.OnAction();
                TickAudio();
                Check(drop.IsUnderCanopy && Cue("parachute_open") && Cue("canopy_glide_loop"),
                    "Parachute opening starts the local deployment cue and canopy loop");
                player.transform.position = new Vector3(SessionState.GetFloat(Session + "X", 0), SessionState.GetFloat(Session + "Y", 0), SessionState.GetFloat(Session + "Z", 0));
                typeof(AarisOfflineDrop).GetMethod("Land", Private).Invoke(drop, null);
                TickAudio();
                Check(Cue("parachute_land"), "Parachute touchdown emits its cue");
                foreach (var source in sources)
                    if (!source.loop) source.Stop();
                typeof(AarisAudioDirector).GetMethod("PlayJump", Private).Invoke(audio, null);
                typeof(AarisAudioDirector).GetMethod("PlayLanding", Private).Invoke(audio, new object[] { 4f });
                Check(Cue("jump_start") && Cue("land_thud"), "Ground jump and landing use the local feedback pool");
                AarisAudioDirector.GunShot("SMG", "", player.transform.position);
                AarisAudioDirector.BulletImpact(player.transform.position);
                Check(Cue("smg_shot") && Cue("bullet_impact", 1f),
                    "Local gunfire and spatial bullet impact use their separate pools");
                Physics.SyncTransforms();
                battle = player.GetComponent<AarisBattleActions>();
                wall = player.GetComponent<AarisOfflineGlooWall>();
                Check(battle != null && wall != null, "Combat and Gloo on saved player");

                float Pose(float crouch, float prone)
                {
                    var type = typeof(AarisCharacterVisual);
                    type.GetField("crouchBlend", Private).SetValue(visual, crouch);
                    type.GetField("proneBlend", Private).SetValue(visual, prone);
                    type.GetField("airborneBlend", Private).SetValue(visual, 0f);
                    type.GetMethod("LateUpdate", Private).Invoke(visual, null);
                    ((PlayableGraph)type.GetField("graph", Private).GetValue(visual)).Evaluate(0f);
                    return HeadHeight();
                }
                var standing = Pose(0f, 0f);
                Check(battle.ToggleCrouch(), "Crouch accepted");
                var crouched = Pose(1f, 0f);
                Note($"POSE standingHead={standing:F3} crouchHead={crouched:F3}");
                Check(crouched < standing - 0.05f, "Crouch lowers avatar head");
                Check(battle.ToggleProne(), "Prone accepted");
                var proneHead = Pose(0f, 1f);
                Vector3 torso = rig.GetBoneTransform(HumanBodyBones.Head).position - rig.GetBoneTransform(HumanBodyBones.Hips).position;
                Note($"POSE proneHead={proneHead:F3} torsoVertical={Mathf.Abs(torso.normalized.y):F3}");
                Check(Mathf.Abs(torso.normalized.y) < 0.65f, "Prone torso is near horizontal");
                Check(battle.TryStand(), "Stand accepted");

                var dummy = new GameObject("Aaris smoke punch target");
                dummy.transform.position = player.transform.position + player.transform.forward * 0.9f + Vector3.up * 1.15f;
                dummy.AddComponent<SphereCollider>().radius = 0.2f;
                var health = dummy.AddComponent<AarisOfflineHealth>();
                float hp = health.CurrentHealth;
                Physics.SyncTransforms();
                var attackTick = typeof(AarisBattleActions).GetMethod("Update", Private);
                var contact = typeof(AarisBattleActions).GetField("punchContactTime", Private);
                battle.SetFireHeld(true);
                attackTick.Invoke(battle, null);
                battle.SetFireHeld(false);
                Check(health.CurrentHealth == hp && (float)contact.GetValue(battle) > Time.time, "Punch schedules contact");
                contact.SetValue(battle, Time.time);
                attackTick.Invoke(battle, null);
                Check(health.CurrentHealth < hp, "Punch contact damages receiver after release");
                // Weapon changes must invalidate attacks scheduled by the previous state.
                var nextAttack = typeof(AarisBattleActions).GetField("nextAttack", Private);
                nextAttack.SetValue(battle, 0f);
                battle.SetFireHeld(true);
                attackTick.Invoke(battle, null);
                battle.SetFireHeld(false);
                float beforeSwitch = health.CurrentHealth;
                battle.Equip(null);
                if ((float)contact.GetValue(battle) >= 0f) contact.SetValue(battle, Time.time);
                attackTick.Invoke(battle, null);
                Check(health.CurrentHealth == beforeSwitch, "Weapon switch cancels pending punch damage");
                foreach (bool disable in new[] { false, true })
                {
                    nextAttack.SetValue(battle, 0f);
                    battle.SetFireHeld(true);
                    attackTick.Invoke(battle, null);
                    battle.SetFireHeld(false);
                    float beforeCancel = health.CurrentHealth;
                    if (disable) battle.enabled = false;
                    else typeof(AarisBattleActions).GetMethod("OnApplicationPause", Private).Invoke(battle, new object[] { true });
                    if ((float)contact.GetValue(battle) >= 0f) contact.SetValue(battle, Time.time);
                    attackTick.Invoke(battle, null);
                    if (disable) battle.enabled = true;
                    Check(health.CurrentHealth == beforeCancel,
                        disable ? "Disable cancels pending punch damage" : "Pause cancels pending punch damage");
                }
                Object.DestroyImmediate(dummy);

                var valid = typeof(AarisOfflineGlooWall).GetField("placementValid", Private);
                var view = player.AimCamera.transform;
                Quaternion saved = view.rotation;
                walls = wall.Count;
                for (int i = 0; i < 8; i++)
                {
                    view.rotation = Quaternion.Euler(18f, saved.eulerAngles.y + i * 45f, 0f);
                    wall.BeginAim();
                    if ((bool)valid.GetValue(wall)) break;
                    wall.CancelAim();
                }
                Check(wall.Aiming && (bool)valid.GetValue(wall), "Gloo has supported placement on native map");
                var obstacle = new GameObject("Aaris smoke Gloo obstacle");
                obstacle.transform.position = player.transform.position + Vector3.ProjectOnPlane(view.forward, Vector3.up).normalized * 1.4f + Vector3.up;
                obstacle.AddComponent<BoxCollider>().size = Vector3.one * 0.8f;
                Physics.SyncTransforms();
                wall.ReleaseAndDeploy();
                Check(wall.Count == walls, "Blocked Gloo retained inventory");
                Check(!Cue("gloo_deploy"), "Blocked Gloo stays silent");
                Object.DestroyImmediate(obstacle);
                Physics.SyncTransforms();
                wall.BeginAim();
                wall.ReleaseAndDeploy();
                view.rotation = saved;
                Check(wall.Count == walls - 1, "Valid Gloo consumed exactly one wall");
                Check(Cue("gloo_deploy"), "Successful Gloo deployment emits its cue");
                var cover = Object.FindFirstObjectByType<AarisGlooWallHealth>();
                Check(cover != null && cover.GetComponent<BoxCollider>() != null, "Gloo has a solid collider and health");
                Note("PLAY_SMOKE_COMPLETE");
                Finish(false);
            }
            catch (Exception e)
            {
                Note("STOP " + e.GetType().Name + " " + e.Message);
                Finish(true);
            }
        }
    }
}
