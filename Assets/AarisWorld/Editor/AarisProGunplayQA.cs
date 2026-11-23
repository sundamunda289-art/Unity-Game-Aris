#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Aaris.MapReview.Editor
{
    // Actual Play Mode/PhysX regressions. Runs only in a dedicated batch Editor;
    // the temporary scene and fixtures are never saved over the game scene.
    [InitializeOnLoad]
    public static class AarisProGunplayQA
    {
        private const string Session = "AarisProGunplayQA";
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly string Output = Path.Combine(Path.GetTempPath(), "aaris-progunplay-qa");
        private static StringBuilder report;
        private static bool runtimeError;
        static AarisProGunplayQA() { EditorApplication.update += Tick; }
        public static void Run()
        {
            if (!Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("DEDICATED_BATCH_EDITOR_REQUIRED");
            Directory.CreateDirectory(Output);
            SessionState.SetBool(Session + "Start", true);
            SessionState.SetBool(Session, false);
            SessionState.SetBool(Session + "Tested", false);
            SessionState.SetBool(Session + "Finish", false);
            SessionState.SetInt(Session + "Exit", 1);
        }
        // Defer entry until import/startup has settled. Entering directly from
        // executeMethod can lose a delayCall to Editor Search initialization.
        private static void Tick()
        {
            if (SessionState.GetBool(Session + "Finish", false))
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                SessionState.SetBool(Session + "Finish", false);
                SessionState.SetBool(Session, false);
                EditorApplication.Exit(SessionState.GetInt(Session + "Exit", 1));
                return;
            }
            if (SessionState.GetBool(Session + "Start", false))
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                    EditorApplication.isPlayingOrWillChangePlaymode) return;
                SessionState.SetBool(Session + "Start", false);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                SessionState.SetBool(Session, true);
                EditorApplication.isPlaying = true;
                return;
            }
            if (!SessionState.GetBool(Session, false) || !EditorApplication.isPlaying ||
                Time.frameCount < 3 || SessionState.GetBool(Session + "Tested", false)) return;
            SessionState.SetBool(Session + "Tested", true);
            Test();
        }
        private static object Read(object target, string name) =>
            target.GetType().GetField(name, Private).GetValue(target);
        private static void Set(object target, string name, object value) =>
            target.GetType().GetField(name, Private).SetValue(target, value);
        private static object Call(object target, string name, params object[] args) =>
            target.GetType().GetMethod(name, Private).Invoke(target, args);
        private static void Check(bool pass, string label)
        {
            if (!pass) throw new InvalidOperationException(label);
            report.AppendLine("PASS " + label);
        }
        private static AarisOfflineHealth Health(GameObject actor)
        {
            var result = actor.AddComponent<AarisOfflineHealth>();
            Set(result, "current", 200f);
            return result;
        }
        private static void Log(string text, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                runtimeError = true;
        }
        private static void Test()
        {
            report = new StringBuilder("Unity " + Application.unityVersion + "\nPLAY_MODE=true\n");
            runtimeError = false;
            Application.logMessageReceived += Log;
            var root = new GameObject("Temporary pro gunplay QA");
            int exit = 1;
            try
            {
                Time.timeScale = 1f;
                var actor = new GameObject("Shooter"); actor.transform.SetParent(root.transform);
                var own = actor.AddComponent<BoxCollider>();
                own.size = new Vector3(.62f, 1.8f, .62f); own.center = Vector3.up * .9f;
                var floor = new GameObject("Real floor"); floor.transform.SetParent(root.transform);
                floor.transform.position = new Vector3(0f, -.1f, 0f);
                floor.AddComponent<BoxCollider>().size = new Vector3(30f, .2f, 30f);
                var findGround = typeof(AarisEquippedGun).GetMethod("TryFindCasingGround", Static);
                foreach (float height in new[] {1.8f, 1.2f, .5f})
                {
                    own.size = new Vector3(.62f, height, .62f); own.center = Vector3.up * height * .5f;
                    Physics.SyncTransforms();
                    object[] args = {new Vector3(.08f, height * .75f, .1f), actor.transform, default(RaycastHit)};
                    Check((bool)findGround.Invoke(null, args) &&
                        ((RaycastHit)args[2]).collider == floor.GetComponent<Collider>(),
                        "Casing excludes own body, stance height=" + height);
                }
                // Saturate the small buffer with shooter-owned attachments.
                for (int i = 0; i < 25; i++)
                {
                    var part = new GameObject("Own attachment"); part.transform.SetParent(actor.transform);
                    part.transform.localPosition = new Vector3(.08f, .04f + i * .025f, .1f);
                    part.AddComponent<BoxCollider>().size = new Vector3(.05f, .008f, .05f);
                }
                Physics.SyncTransforms();
                object[] dense = {new Vector3(.08f, 1.5f, .1f), actor.transform, default(RaycastHit)};
                Check((bool)findGround.Invoke(null, dense) &&
                    ((RaycastHit)dense[2]).collider == floor.GetComponent<Collider>(),
                    "Dense self hit buffer still resolves real floor");
                // A second living enemy may stand directly above the real
                // static floor. Its CharacterController top is not a shell
                // landing support; neither is a physics vehicle bonnet.
                var otherRival = new GameObject("Live rival casing obstruction");
                otherRival.transform.SetParent(root.transform);
                otherRival.transform.position = new Vector3(.08f, 0f, .1f);
                var rivalCapsule = otherRival.AddComponent<CharacterController>();
                rivalCapsule.radius = .23f;
                rivalCapsule.height = .9f;
                rivalCapsule.center = Vector3.up * .45f;
                Physics.SyncTransforms();
                object[] behindRival = {new Vector3(.08f, 1.5f, .1f),
                    actor.transform, default(RaycastHit)};
                Check((bool)findGround.Invoke(null, behindRival) &&
                    ((RaycastHit)behindRival[2]).collider == floor.GetComponent<Collider>(),
                    "Shell falls past nearby live rival capsule to stationary floor");
                rivalCapsule.enabled = false;

                var movingVehicle = new GameObject("Kinematic vehicle casing obstruction");
                movingVehicle.transform.SetParent(root.transform);
                movingVehicle.transform.position = new Vector3(.08f, .4f, .1f);
                var vehicleCollider = movingVehicle.AddComponent<BoxCollider>();
                vehicleCollider.size = new Vector3(.46f, .8f, .46f);
                var vehiclePhysics = movingVehicle.AddComponent<Rigidbody>();
                vehiclePhysics.isKinematic = true;
                Physics.SyncTransforms();
                object[] behindVehicle = {new Vector3(.08f, 1.5f, .1f),
                    actor.transform, default(RaycastHit)};
                Check((bool)findGround.Invoke(null, behindVehicle) &&
                    ((RaycastHit)behindVehicle[2]).collider == floor.GetComponent<Collider>(),
                    "Shell falls past kinematic vehicle to stationary floor");
                vehicleCollider.enabled = false;
                // A fixed bridge/porch IS safe support and must remain valid.
                var bridge = new GameObject("Static raised casing support");
                bridge.transform.SetParent(root.transform);
                bridge.transform.position = new Vector3(.08f, .36f, .1f);
                var bridgeFloor = bridge.AddComponent<BoxCollider>();
                bridgeFloor.size = new Vector3(.5f, .72f, .5f);
                Physics.SyncTransforms();
                object[] elevated = {new Vector3(.08f, 1.5f, .1f),
                    actor.transform, default(RaycastHit)};
                Check((bool)findGround.Invoke(null, elevated) &&
                    ((RaycastHit)elevated[2]).collider == bridgeFloor,
                    "Shell correctly rests on static raised bridge support");
                bridgeFloor.enabled = false;
                Object.Destroy(bridge);
                Object.Destroy(otherRival);
                Object.Destroy(movingVehicle);

                own.size = new Vector3(.62f, 1.8f, .62f); own.center = Vector3.up * .9f;
                string[] ids = {"SMG", "AssaultRifle", "BattleRifle", "Sniper", "Shotgun", "DMR", "Pistol"};
                var damage = typeof(AarisEquippedGun).GetMethod("ApplyDamage", Static, null,
                    new[] {typeof(Collider), typeof(float), typeof(GameObject), typeof(Vector3?), typeof(float)}, null);
                foreach (string id in ids)
                {
                    var spec = AarisWeaponCatalog.Get(id);
                    Check(spec != null && spec.Valid && AarisWeaponCatalog.ModelFor(spec) != null,
                        id + " native model and authored profile loaded");
                    for (int head = 0; head < 2; head++)
                    {
                        var victim = new GameObject("Profile target"); victim.transform.SetParent(root.transform);
                        victim.transform.position = new Vector3(8f, 0f, 8f);
                        var body = victim.AddComponent<BoxCollider>();
                        body.size = new Vector3(.6f, 1.8f, .6f); body.center = Vector3.up * .9f;
                        var hp = Health(victim); Physics.SyncTransforms();
                        Vector3 hit = victim.transform.position + Vector3.up * (head == 1 ? 1.7f : .7f);
                        damage.Invoke(null, new object[] {body, spec.damage, actor, (Vector3?)hit, spec.headshotMultiplier});
                        float expected = Mathf.Max(0f, 200f - spec.damage * (head == 1 ? spec.headshotMultiplier : 1f));
                        Check(Mathf.Abs(hp.CurrentHealth - expected) < .02f, id + (head == 1 ? " head" : " body") + " actual HP loss");
                        Object.Destroy(victim);
                    }
                }
                var target = new GameObject("Real pistol target"); target.transform.SetParent(root.transform);
                target.transform.position = new Vector3(0f, 0f, 2f);
                var hitbox = target.AddComponent<BoxCollider>(); hitbox.center = Vector3.up * 1.1f;
                hitbox.size = new Vector3(.9f, 1.8f, .4f); var targetHp = Health(target);
                var view = new GameObject("QA Camera"); view.transform.SetParent(root.transform);
                var camera = view.AddComponent<Camera>(); camera.transform.position = Vector3.up * 1.45f;
                camera.transform.LookAt(target.transform.position + Vector3.up * 1.1f);
                var gunObject = new GameObject("Pistol firing fixture"); gunObject.transform.SetParent(actor.transform);
                gunObject.transform.localPosition = Vector3.up * 1.28f;
                var gun = gunObject.AddComponent<AarisEquippedGun>();
                gun.Configure(AarisWeaponCatalog.Get("Pistol"), 727u);
                // No per-equip primitive/Collider allocations: player and
                // bot metal shells must share one capped, UV-ready mesh.
                var playerShell = (Renderer)Read(gun, "casingRenderer");
                var casingMesh = playerShell.GetComponent<MeshFilter>().sharedMesh;
                Check(casingMesh != null && casingMesh.vertexCount == 50 &&
                    gun.GetComponentsInChildren<Collider>(true).Length == 0,
                    "Player casing pool reuses one sealed mesh without Colliders");
                var prebuiltShells = (Array)Read(gun, "shellTrail");
                foreach (object shell in prebuiltShells)
                {
                    var pose = (Transform)shell.GetType().GetField("pose",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shell);
                    Check(pose.GetComponent<MeshFilter>().sharedMesh == casingMesh,
                        "Every automatic casing shares the same closed mesh");
                }
                Physics.SyncTransforms();
                Check(gun.TryFire(camera, actor) && gun.Magazine == 14 && targetHp.CurrentHealth < 200f,
                    "Pistol consumes one round and physically damages target");
                var tracer = (LineRenderer)Read(gun, "tracerRenderer");
                Check(tracer.enabled && Vector3.Distance(tracer.GetPosition(0), tracer.GetPosition(1)) > .01f,
                    "Close tracer has visible length on accepting frame");
                Call(gun, "TickTracer");
                Check(tracer.enabled && Vector3.Distance(tracer.GetPosition(0), tracer.GetPosition(1)) > .001f,
                    "Close tracer remains nonzero through first Update");
                var flash = (Renderer)Read(gun, "muzzleFlashRenderer");
                Check(Vector3.Distance(tracer.GetPosition(0), flash.transform.position) < .001f,
                    "Tracer starts on rendered barrel path");
                Check(flash.enabled && flash.sharedMaterial.renderQueue >= 3000,
                    "Accepted shot enables transparent muzzle flame");
                var light = (Light)Read(gun, "muzzleLight");
                Check(light.enabled && light.shadows == LightShadows.None, "Player barrel burst casts no shadow map");
                Call(gun, "EjectCasingNow");
                Check(Mathf.Abs((float)Read(gun, "casingGroundY") - .012f) < .02f,
                    "Player casing bounce plane is actual floor");

                // Physical renderer validation: three rapid automatic rounds
                // must coexist, rather than replacing the first 64m tracer.
                var autoVisual = new GameObject("SMG pooled tracer fixture");
                autoVisual.transform.SetParent(actor.transform, false);
                var automatic = autoVisual.AddComponent<AarisEquippedGun>();
                automatic.Configure(AarisWeaponCatalog.Get("SMG"), 728u);
                for (int i = 0; i < 3; i++)
                    Call(automatic, "TriggerTracer", new Vector3(0f, 1.3f, .3f),
                        new Vector3(i, 1.3f, 30f));
                var autoTracers = (Array)Read(automatic, "tracerTrail");
                var firstAuto = (LineRenderer)Read(automatic, "tracerRenderer");
                int visibleAuto = firstAuto != null && firstAuto.enabled ? 1 : 0;
                for (int i = 0; i < autoTracers.Length; i++)
                {
                    object trailState = autoTracers.GetValue(i);
                    var trailRenderer = (LineRenderer)trailState.GetType()
                        .GetField("renderer", BindingFlags.Instance |
                            BindingFlags.NonPublic).GetValue(trailState);
                    if (trailRenderer != null && trailRenderer.enabled &&
                        Vector3.Distance(trailRenderer.GetPosition(0),
                            trailRenderer.GetPosition(1)) > .01f)
                        visibleAuto++;
                }
                Check(visibleAuto == 3,
                    "Three rapid automatic bullets retain three independent thin tracers");

                // Recreate a slow-frame ordering hazard: several accepted
                // rounds arrive before the casing component's next Update.
                // None may overwrite an earlier scheduled metal casing.
                automatic.transform.localPosition = Vector3.up * 1.28f;
                for (int i = 0; i < 3; i++)
                    Call(automatic, "ScheduleCasingEjection");
                Check((int)Read(automatic, "pendingCasingEjections") == 3,
                    "Three pending automatic shells retain their own deadlines");
                float[] ejectionDeadlines = (float[])Read(automatic, "casingDeadlines");
                for (int i = 0; i < 3; i++)
                    ejectionDeadlines[i] = Time.time - .001f;
                Call(automatic, "TickCasingEjections");
                var metalSlots = (Array)Read(automatic, "shellTrail");
                int liveCasings = ((Renderer)Read(automatic, "casingRenderer")).enabled
                    ? 1 : 0;
                for (int i = 0; i < metalSlots.Length; i++)
                {
                    object shell = metalSlots.GetValue(i);
                    var shellRenderer = (Renderer)shell.GetType()
                        .GetField("renderer", BindingFlags.Instance |
                            BindingFlags.NonPublic).GetValue(shell);
                    if (shellRenderer != null && shellRenderer.enabled)
                        liveCasings++;
                }
                Check((int)Read(automatic, "pendingCasingEjections") == 0 &&
                    (int)Read(automatic, "shellCursor") == 3 && liveCasings == 3,
                    "All three queued shells eject into independent physical visuals");
                Call(automatic, "ScheduleCasingEjection");
                automatic.enabled = false;
                Check((int)Read(automatic, "pendingCasingEjections") == 0,
                    "Gun disable cancels delayed shell ejection without orphaned shots");

                var bots = root.AddComponent<AarisRivalGunfireVfx>(); Call(bots, "Bind", actor.transform);
                for (int i = 0; i < 6; i++) Call(bots, "Emit", actor.transform, new Vector3(0f, 1.2f, 4f), false);
                int activeLights = 0;
                foreach (Light burst in bots.GetComponentsInChildren<Light>())
                    if (burst != light && burst.enabled) activeLights++;
                Check(activeLights == 2, "Six bot shots keep at most two active barrel lights");
                var botShellRenderers = bots.GetComponentsInChildren<MeshFilter>(true);
                int matchedBotCasings = 0;
                int botEffectColliders = 0;
                foreach (MeshFilter filter in botShellRenderers)
                    if (filter.gameObject.name ==
                        "Aaris Bot Reused Independent Ejected Casing")
                    {
                        Check(filter.sharedMesh == casingMesh,
                            "Nearby bot shell shares the player's capped mesh");
                        matchedBotCasings++;
                        // The pooled FX share the fixture root with the player,
                        // floor and target. Count only FX-owned colliders.
                        botEffectColliders += filter.GetComponentsInChildren<Collider>(true).Length;
                    }
                Check(matchedBotCasings == 6 &&
                    botEffectColliders == 0,
                    "Six nearby bot ejections create no transient native Colliders");
                var earlyCasings = (Array)Read(bots, "casings");
                Vector3 firstVelocity = (Vector3)Read(earlyCasings.GetValue(0), "velocity");
                Vector3 secondVelocity = (Vector3)Read(earlyCasings.GetValue(1), "velocity");
                Check((firstVelocity - secondVelocity).sqrMagnitude > .001f,
                    "Nearby bot cartridge ejections have distinct ballistic velocities");
                var slots = (Array)Read(bots, "slots"); var firstSlot = slots.GetValue(0);
                var botFlash = (Renderer)firstSlot.GetType().GetField("flash", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(firstSlot);
                Check(botFlash.sharedMaterial == flash.sharedMaterial, "Bots share transparent flame material with player");

                // Actual bot weapon category must affect its visuals, not
                // damage or the shared pool count. Bolt-action snipers delay
                // cartridge ejection while their flash/tracer render now.
                Call(bots, "EmitForGun", actor.transform,
                    new Vector3(0f, 1.2f, 4f), AarisWeaponCatalog.Get("Sniper"));
                var botShellSlots = (Array)Read(bots, "casings");
                object delayedSniperShell = botShellSlots.GetValue(6);
                var sniperRenderer = (Renderer)Read(delayedSniperShell, "renderer");
                Check((bool)Read(delayedSniperShell, "queued") &&
                    !sniperRenderer.enabled &&
                    (float)Read(delayedSniperShell, "ejectAt") > Time.time,
                    "Sniper muzzle fires before delayed bolt-action shell");
                var sniperFlash = (Transform)Read(slots.GetValue(0), "flashPose");
                Check(sniperFlash.localScale.x > .18f &&
                    sniperRenderer.GetComponentsInChildren<Collider>(true).Length == 0,
                    "Sniper visual differs from AR without a new collider");
                // A real sniper can move/rotate during bolt cycling. Its brass
                // must leave the NEW receiver port, not the stale shot point.
                Vector3 originalShooterPoint = actor.transform.position;
                Quaternion originalShooterRotation = actor.transform.rotation;
                actor.transform.position += new Vector3(.92f, 0f, .18f);
                actor.transform.rotation = Quaternion.Euler(0f, 37f, 0f) *
                    originalShooterRotation;
                Physics.SyncTransforms();
                Set(delayedSniperShell, "ejectAt", Time.time - .01f);
                Call(bots, "Update");
                Check(sniperRenderer.enabled &&
                    !(bool)Read(delayedSniperShell, "queued"),
                    "Delayed sniper brass ejects from prebuilt pool");
                var delayedPose = (Transform)Read(delayedSniperShell, "pose");
                float sniperPort = Mathf.Clamp(
                    AarisWeaponCatalog.Get("Sniper").presentationLengthMetres *
                    .25f, .075f, .34f);
                Vector3 currentPort = actor.transform.position + Vector3.up * 1.32f +
                    actor.transform.forward * sniperPort + actor.transform.right * .18f;
                // The pooled slot advances its visual ballistics in the
                // same Update after ejection. Include that exact movement so
                // slower software-rendered Editor frames cannot give false QA.
                float ejectionDt = Mathf.Min(Time.deltaTime, .12f);
                Vector3 ejectionVelocity = (Vector3)Read(delayedSniperShell, "velocity");
                Vector3 simulatedPose = currentPort + ejectionVelocity * ejectionDt;
                Check(Vector3.Distance(delayedPose.position, simulatedPose) < .035f &&
                    Mathf.Abs((float)Read(delayedSniperShell, "groundY") - .012f) < .03f,
                    "Delayed bolt brass originates at moving shooter with actual floor");
                actor.transform.rotation = originalShooterRotation;
                actor.transform.position = originalShooterPoint;
                Physics.SyncTransforms();

                // Actual nearby crate transfer; preserve rounds and consumed meds.
                var crate = root.AddComponent<AarisDeathCrateWorld>();
                typeof(AarisDeathCrateWorld).GetField("instance", Static).SetValue(null, crate);
                Call(crate, "Bind", actor.transform);
                var cratePreview = (GameObject)Call(crate, "CreateVisual");
                int cubeParts = 0;
                Mesh sharedCrateMesh = null;
                foreach (MeshFilter part in cratePreview.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (part.gameObject.name == "Vertical loot marker") continue;
                    cubeParts++;
                    if (sharedCrateMesh == null) sharedCrateMesh = part.sharedMesh;
                    Check(part.sharedMesh == sharedCrateMesh,
                        "Crate part shares a single prebuilt cube mesh");
                }
                Check(cubeParts == 3 && sharedCrateMesh != null &&
                    cratePreview.GetComponentsInChildren<Collider>(true).Length == 0,
                    "Death crate visual has three meshes and zero transient colliders");
                Object.Destroy(cratePreview);
                // The closer medkit-only crate cannot be collected by this
                // fixture (no medical HUD); the farther pistol must still be
                // lootable with the same single PICK action.
                Call(crate, "Spawn", new Vector3(1.15f, 0f, 0f),
                    null, 0, 0, 1, 882u);
                Call(crate, "Spawn", new Vector3(1.8f, 0f, 0f), AarisWeaponCatalog.Get("Pistol"), 7, 19, 0, 883u);
                // Provide the real motor's required saved-camera contract.
                view.tag = "MainCamera";
                var motor = actor.AddComponent<AarisThirdPersonPlayer>();
                var inventory = actor.AddComponent<AarisWeaponInventory>(); Set(inventory, "motor", motor);
                var selfBlocker = new GameObject("Own pickup-path attachment");
                selfBlocker.transform.SetParent(actor.transform, false);
                selfBlocker.transform.localPosition = new Vector3(.85f, .7f, 0f);
                selfBlocker.AddComponent<BoxCollider>().size = Vector3.one * .25f;
                var capsule = actor.GetComponent<CharacterController>();
                capsule.enabled = false; actor.transform.position = Vector3.zero; capsule.enabled = true;
                foreach (float height in new[] {1.8f, 1.2f, .5f})
                {
                    capsule.height = height; capsule.center = Vector3.up * height * .5f;
                    Physics.SyncTransforms();
                    Check((bool)Call(crate, "Reachable", actor.transform.position, new Vector3(1.8f, 0f, 0f)),
                        "Crate ignores owned colliders at stance height=" + height);
                }
                // A low solid wall permits standing reach over its top but
                // must stop a crouched or crawling pickup LOS. This also
                // tests the real CharacterController height changing back
                // and forth without caching an obsolete eye height.
                var lowCover = new GameObject("Low prone-blocking crate wall");
                lowCover.transform.SetParent(root.transform);
                lowCover.transform.position = new Vector3(.9f, .29f, 0f);
                var lowCoverCollider = lowCover.AddComponent<BoxCollider>();
                lowCoverCollider.size = new Vector3(.12f, .58f, 1.2f);
                float[] stanceHeights = {1.8f, 1.2f, .5f, 1.8f, .5f};
                for (int stanceCase = 0; stanceCase < stanceHeights.Length; stanceCase++)
                {
                    float height = stanceHeights[stanceCase];
                    capsule.height = height;
                    capsule.center = Vector3.up * height * .5f;
                    Physics.SyncTransforms();
                    bool reachable = (bool)Call(crate, "Reachable",
                        actor.transform.position, new Vector3(1.8f, 0f, 0f));
                    Check(reachable == (height > 1.6f),
                        "Low cover obeys standing/crouch/prone LOS height=" +
                        height + " case=" + stanceCase);
                }
                lowCoverCollider.enabled = false;
                Object.Destroy(lowCover);
                Physics.SyncTransforms();

                // The nearer uncollectable medkit must not monopolize PICK
                // while the farther gun is actually reachable.
                Call(crate, "Update");
                var crateHint = typeof(AarisDeathCrateWorld).GetMethod(
                    "TryGetNearbyHint", Static);
                var hasPickupNear = typeof(AarisDeathCrateWorld).GetMethod(
                    "HasPickupNear", Static);
                object[] hintArgs = {actor.transform, null, 0f, false};
                Check((bool)crateHint.Invoke(null, hintArgs) &&
                    (string)hintArgs[1] == AarisWeaponCatalog.Get("Pistol").displayName &&
                    (bool)hintArgs[3] &&
                    (bool)hasPickupNear.Invoke(null, new object[] {actor.transform}),
                    "Actionable crate hint bypasses nearer unavailable medkit");
                var loot = typeof(AarisDeathCrateWorld).GetMethod("TryLootNearest", Static);
                Check((bool)loot.Invoke(null, new object[] {actor.transform, inventory}) &&
                    inventory.Current.Spec.id == "Pistol" && inventory.Current.Magazine == 7 && inventory.Current.Reserve == 19,
                    "Death crate transfers exact pistol magazine and reserve without phantom medkit");
                var leftoverCrates = (IList)Read(crate, "drops");
                Check(leftoverCrates.Count == 1 &&
                    Read(leftoverCrates[0], "gun") == null &&
                    (int)Read(leftoverCrates[0], "medkits") == 1,
                    "PICK skips unavailable medkit, takes farther gun and preserves remaining crate");
                Call(crate, "Update");
                hintArgs = new object[] {actor.transform, null, 0f, false};
                Check((bool)crateHint.Invoke(null, hintArgs) &&
                    !(bool)hintArgs[3] &&
                    !(bool)hasPickupNear.Invoke(null, new object[] {actor.transform}),
                    "Uncollectable medkit-only crate never promises PICK");
                Call(crate, "Spawn", new Vector3(1.8f, 0f, 0f), AarisWeaponCatalog.Get("Pistol"), 5, 11, 1, 884u);
                var wall = new GameObject("Crate wall"); wall.transform.SetParent(root.transform);
                wall.transform.position = new Vector3(.9f, 1f, 0f);
                wall.AddComponent<BoxCollider>().size = new Vector3(.1f, 2f, 2f);
                Physics.SyncTransforms();
                Check(!(bool)loot.Invoke(null, new object[] {actor.transform, inventory}), "Crate cannot be looted through solid wall");
                Object.Destroy(wall);
                // Validate true HP-confirmed headshot -> HUD tint, followed
                // by body pellets which must preserve the critical marker,
                // then expire back to a normal gold body hit.
                var hitUiRoot = new GameObject("Temporary hit marker test");
                hitUiRoot.transform.SetParent(root.transform);
                var feedback = hitUiRoot.AddComponent<AarisWeaponHUD>();
                Set(feedback, "inventory", inventory);
                var markerRoot = new GameObject("Hit marker", typeof(RectTransform));
                markerRoot.transform.SetParent(hitUiRoot.transform, false);
                Set(feedback, "hitMarker", markerRoot.GetComponent<RectTransform>());
                var strokes = (Image[])Read(feedback, "hitMarkerStrokes");
                for (int i = 0; i < strokes.Length; i++)
                {
                    var child = new GameObject("Prebuilt stroke",
                        typeof(RectTransform), typeof(Image));
                    child.transform.SetParent(markerRoot.transform, false);
                    strokes[i] = child.GetComponent<Image>();
                    strokes[i].raycastTarget = false;
                }
                markerRoot.SetActive(false);
                typeof(AarisWeaponHUD).GetField("instance", Static).SetValue(null,
                    feedback);
                float beforeCritical = targetHp.CurrentHealth;
                damage.Invoke(null, new object[] {hitbox, 9f, actor,
                    (Vector3?)(target.transform.position + Vector3.up * 1.75f), 2f});
                Check(targetHp.CurrentHealth < beforeCritical - 15f &&
                    markerRoot.activeSelf && strokes[0].color.g < .45f,
                    "Real headshot HP loss produces distinct critical HUD marker");
                damage.Invoke(null, new object[] {hitbox, 4f, actor,
                    (Vector3?)(target.transform.position + Vector3.up * .8f), 2f});
                Check(strokes[0].color.g < .45f,
                    "Following shotgun body pellet cannot erase critical cue");
                Set(feedback, "criticalHitMarkerUntil", Time.unscaledTime - 1f);
                damage.Invoke(null, new object[] {hitbox, 4f, actor,
                    (Vector3?)(target.transform.position + Vector3.up * .8f), 2f});
                Check(strokes[0].color.g > .8f &&
                    !strokes[0].raycastTarget,
                    "Expired critical marker restores nonblocking body tint");

                // Exercise the production health event -> rival OnDied ->
                // director -> crate chain, including duplicate fatal hits.
                var director = root.AddComponent<AarisOfflineBRDirector>();
                Set(director, "deathCrates", crate);
                var deadActor = new GameObject("Real rival fatality fixture");
                deadActor.transform.SetParent(root.transform);
                deadActor.transform.position = new Vector3(200f, 0f, 0f);
                var deadCapsule = deadActor.AddComponent<CharacterController>();
                var deadHealth = Health(deadActor);
                Type rivalType = typeof(AarisOfflineBRDirector).Assembly.GetType("Aaris.MapReview.AarisOfflineRival", true);
                var rival = deadActor.AddComponent(rivalType);
                Call(rival, "Initialize", director, deadHealth, actor.transform, Health(actor), 0);
                Set(rival, "carriedGun", AarisWeaponCatalog.Get("DMR"));
                Set(rival, "carriedMagazine", 3); Set(rival, "carriedReserve", 17); Set(rival, "medkits", 0);
                deadActor.transform.position = new Vector3(1.8f, 0f, 0f); deadCapsule.enabled = true;
                Physics.SyncTransforms();
                var records = (IList)Read(crate, "drops"); int beforeDeaths = records.Count;
                deadHealth.ApplyDamage(250f, actor);
                Check(deadHealth.IsDead && !deadCapsule.enabled && records.Count == beforeDeaths + 1,
                    "Actual rival fatality spawns exactly one grounded crate");
                var deathRecord = records[records.Count - 1];
                var deathGun = (AarisWeaponState)Read(deathRecord, "gun");
                Check(deathGun.Spec.id == "DMR" && deathGun.Magazine == 3 &&
                    (int)Read(deathRecord, "looseAmmo") == 17 && (int)Read(deathRecord, "medkits") == 0 &&
                    Mathf.Abs(((Vector3)Read(deathRecord, "position")).y) < .02f,
                    "Fatality crate preserves real gun, spent ammo and zero medkits on real floor");
                deadHealth.ApplyDamage(250f, actor);
                Check(records.Count == beforeDeaths + 1, "Repeated fatal hit cannot duplicate death loot");
                // A nearby standing rival's capsule is not static floor. The
                // death ray starts just above their head, so a first-hit-only
                // Raycast would incorrectly float the new crate at head height.
                var standingRival = new GameObject("Nearby living rival cover");
                standingRival.transform.SetParent(root.transform, false);
                standingRival.transform.position = new Vector3(2.6f, 0f, 0f);
                var standingCapsule = standingRival.AddComponent<CharacterController>();
                standingCapsule.height = 1.8f;
                standingCapsule.center = Vector3.up * .9f;
                Physics.SyncTransforms();
                Call(crate, "Spawn", standingRival.transform.position,
                    AarisWeaponCatalog.Get("SMG"), 5, 13, 0, 885u);
                Check(records.Count == beforeDeaths + 2 &&
                    Mathf.Abs(((Vector3)Read(records[records.Count - 1],
                        "position")).y) < .02f &&
                    (int)Read(records[records.Count - 1], "looseAmmo") == 13,
                    "Crate ignores living rival cap and anchors on real floor");
                Call(crate, "Spawn", standingRival.transform.position,
                    null, 0, 0, 0, 886u);
                Check(records.Count == beforeDeaths + 2,
                    "Empty death crate skips physics and creates no record");
                Check(!runtimeError, "No runtime errors during native gunplay regressions");
                exit = 0;
            }
            catch (Exception ex) { report.AppendLine("FAIL " + ex); }
            finally
            {
                Application.logMessageReceived -= Log;
                report.AppendLine("RESULT=" + (exit == 0 ? "PASS" : "FAIL"));
                File.WriteAllText(Path.Combine(Output, "report.txt"), report.ToString());
                Object.Destroy(root);
                SessionState.SetInt(Session + "Exit", exit);
                SessionState.SetBool(Session + "Finish", true);
                EditorApplication.isPlaying = false;
            }
        }
    }
}
#endif
