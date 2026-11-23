#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Real PhysX combat contract in an isolated temporary scene.
    /// 100 simplified logical rivals, not a device FPS benchmark.
    /// </summary>
    public static class AarisBotCombatIntegrationQA
    {
        private static readonly Vector3 Origin = new Vector3(10000f, 10000f, 10000f);
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly Type BotType = typeof(AarisOfflineBRDirector).Assembly.GetType(
            "Aaris.MapReview.AarisOfflineRival", true);

        private static FieldInfo Field(Type type, string name, BindingFlags flags)
        {
            var field = type.GetField(name, flags);
            if (field == null) throw new InvalidOperationException("Missing field " + name);
            return field;
        }
        private static void Set(object target, string name, object value)
        {
            Field(target.GetType(), name, Instance).SetValue(target, value);
        }
        private static object Get(object target, string name)
        {
            return Field(target.GetType(), name, Instance).GetValue(target);
        }
        private static object Call(object target, string name, params object[] args)
        {
            var method = target.GetType().GetMethod(name, Instance);
            if (method == null) throw new InvalidOperationException("Missing method " + name);
            return method.Invoke(target, args);
        }
        private static void Check(bool pass, string label, StringBuilder result)
        {
            result.AppendLine((pass ? "PASS " : "FAIL ") + label);
            if (!pass) throw new InvalidOperationException(label);
        }
        private static AarisOfflineHealth Health(GameObject actor)
        {
            var hp = actor.AddComponent<AarisOfflineHealth>();
            Field(typeof(AarisOfflineHealth), "current", Instance).SetValue(hp, 200f);
            return hp;
        }
        // In Edit Mode, CharacterController bodies are not reliably registered
        // with PhysX until Play Mode. This query-only collider is an Editor
        // surrogate; the actual game uses its CharacterController.
        private static void QueryBody(GameObject actor)
        {
            var box = actor.AddComponent<BoxCollider>();
            box.center = Vector3.up * .9f;
            box.size = new Vector3(.62f, 1.8f, .62f);
        }
        private static CharacterController Capsule(GameObject actor)
        {
            var c = actor.AddComponent<CharacterController>();
            c.height = 1.8f;
            c.radius = .31f;
            c.center = Vector3.up * .9f;
            return c;
        }

        [MenuItem("Aaris/CI/Verify 100-bot combat selection and real damage")]
        public static void Run()
        {
            var report = new StringBuilder("Unity " + Application.unityVersion + "\n");
            Scene original = SceneManager.GetActiveScene();
            Scene scratch = default(Scene);
            int failures = 0;
            var active = Field(typeof(AarisOfflineBRDirector), "activeDirector", Static);
            object oldDirector = active.GetValue(null);
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                    throw new InvalidOperationException("EDITOR_BUSY");
                // Batchmode can start with an unsaved empty untitled scene.
                // Unity disallows additive scene creation beside that scene.
                bool emptyBatchScene = Application.isBatchMode &&
                    string.IsNullOrEmpty(original.path) && original.rootCount == 0;
                if (!emptyBatchScene)
                {
                    scratch = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                        NewSceneMode.Additive);
                    SceneManager.SetActiveScene(scratch);
                }
                VerifyDamage(report);
                VerifyDestructibleHitConfirmation(report);
                VerifyBots(report, active);
                VerifyVirtualCombat(report);
                VerifyBotLootCover(report);
            }
            catch (Exception ex)
            {
                failures++;
                report.AppendLine("FAIL " + ex.GetType().Name + ": " + ex.Message);
                if (ex.InnerException != null)
                    report.AppendLine("CAUSE " + ex.InnerException.GetType().Name +
                        ": " + ex.InnerException.Message);
            }
            finally
            {
                active.SetValue(null, oldDirector);
                if (original.IsValid() && original.isLoaded)
                    SceneManager.SetActiveScene(original);
                if (scratch.IsValid() && scratch.isLoaded)
                    EditorSceneManager.CloseScene(scratch, true);
            }
            report.AppendLine("RESULT=" + (failures == 0 ? "PASS" : "FAIL"));
            string directory = Path.Combine(Path.GetTempPath(), "aaris-bot-combat-qa");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "report.txt"), report.ToString());
            Debug.Log(report.ToString());
            if (Application.isBatchMode) EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        // Offscreen opponents use the real health receiver and own ammo
        // logic while both physical colliders stay disabled. This does not
        // claim an Android FPS result or construct a fake gun/rig.
        private static void VerifyVirtualCombat(StringBuilder report)
        {
            var root = new GameObject("Temporary remote 100-bot math combat QA");
            try
            {
                var player = new GameObject("Virtual QA player");
                player.transform.SetParent(root.transform, false);
                player.transform.position = Origin;
                var playerHP = Health(player);
                var directorGo = new GameObject("Remote QA director");
                directorGo.transform.SetParent(root.transform, false);
                var director = directorGo.AddComponent<AarisOfflineBRDirector>();
                director.enabled = false;
                Set(director, "player", player.transform);
                Set(director, "playerHealth", playerHP);
                Set(director, "center", new Vector2(Origin.x, Origin.z));
                Set(director, "radius", 100000f);
                var alpha = new GameObject("Remote Alpha");
                alpha.transform.SetParent(root.transform, false);
                alpha.transform.position = Origin + Vector3.right * 500f;
                var bravo = new GameObject("Remote Bravo");
                bravo.transform.SetParent(root.transform, false);
                bravo.transform.position = alpha.transform.position + Vector3.forward * 1.6f;
                var alphaBody = Capsule(alpha);
                var bravoBody = Capsule(bravo);
                var alphaHP = Health(alpha);
                var bravoHP = Health(bravo);
                var alphaBot = alpha.AddComponent(BotType);
                var bravoBot = bravo.AddComponent(BotType);
                Set(alphaBot, "owner", director);
                Set(alphaBot, "health", alphaHP);
                Set(alphaBot, "capsule", alphaBody);
                Set(alphaBot, "playerTarget", player.transform);
                Set(alphaBot, "playerTargetHealth", playerHP);
                Set(alphaBot, "target", bravo.transform);
                Set(alphaBot, "targetHealth", bravoHP);
                Set(alphaBot, "nextThink", 100000f);
                Set(alphaBot, "nextAttack", 0f);
                Set(alphaBot, "virtualBody", true);
                Set(bravoBot, "virtualBody", true);
                alphaBody.enabled = false;
                bravoBody.enabled = false;
                int remoteDeaths = 0;
                GameObject lastAttacker = null;
                bravoHP.Died += _ => remoteDeaths++;
                bravoHP.Damaged += (_, loss, attacker) =>
                {
                    if (loss > 0f) lastAttacker = attacker;
                };
                for (int i = 0; i < 110 && !bravoHP.IsDead; i++)
                    Call(alphaBot, "TickVirtualCombat", 20f + i * 1.5f, 0.35f);
                Check(bravoHP.IsDead && remoteDeaths == 1,
                    "Remote virtual combat eliminates actual rival exactly once", report);
                Check(lastAttacker == alpha,
                    "Remote damage retains actual rival attacker identity", report);
                Check(!alphaBody.enabled && !bravoBody.enabled,
                    "Both remote combatants fought without physical capsules", report);
                Check(playerHP.CurrentHealth == 200f,
                    "Virtual rival combat never damages distant player", report);
                // No terrain exists under the remote synthetic fixture. A
                // wandering logical rival must not teleport across voids.
                Set(alphaBot, "target", null);
                Set(alphaBot, "targetHealth", null);
                Vector3 beforeVoid = alpha.transform.position;
                Call(alphaBot, "TickVirtualCombat", 500f, 0.35f);
                Check((alpha.transform.position - beforeVoid).sqrMagnitude < 0.0001f,
                    "Remote rival refuses movement across missing terrain", report);
            }
            finally { Object.DestroyImmediate(root); }
        }

        // Actual PhysX cover validation of the shared bot/player pickup ray.
        private static void VerifyBotLootCover(StringBuilder report)
        {
            var root = new GameObject("Temporary bot ground-loot LOS audit");
            try
            {
                var actor = new GameObject("Looter with own collision");
                actor.transform.SetParent(root.transform, false);
                actor.transform.position = Origin + new Vector3(300f, 0f, 300f);
                QueryBody(actor);
                var world = root.AddComponent<AarisWeaponLootWorld>();
                Vector3 pickup = actor.transform.position + Vector3.forward * 1.8f;
                Physics.SyncTransforms();
                Check((bool)Call(world, "HasClearPickupPath", actor.transform, pickup),
                    "Bot can reach unobstructed nearby ground weapon", report);

                var wall = new GameObject("Physical wall between bot and gun");
                wall.transform.SetParent(root.transform, false);
                wall.transform.position = actor.transform.position +
                    new Vector3(0f, 0.85f, 0.9f);
                wall.AddComponent<BoxCollider>().size = new Vector3(1f, 2f, .15f);
                Physics.SyncTransforms();
                Check(!(bool)Call(world, "HasClearPickupPath", actor.transform, pickup),
                    "Bot cannot loot through solid wall", report);
                Object.DestroyImmediate(wall);
                Physics.SyncTransforms();
                Check((bool)Call(world, "HasClearPickupPath", actor.transform, pickup),
                    "Bot can reacquire ground weapon after cover clears", report);
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static void VerifyDamage(StringBuilder report)
        {
            var root = new GameObject("Temporary two-way damage audit");
            try
            {
                var player = new GameObject("Player");
                var bot = new GameObject("Bot");
                player.transform.SetParent(root.transform, false);
                bot.transform.SetParent(root.transform, false);
                player.transform.position = Origin + new Vector3(0f, 0f, 3f);
                bot.transform.position = Origin;
                var pBody = Capsule(player);
                var bBody = Capsule(bot);
                var pHp = Health(player);
                var bHp = Health(bot);
                int playerHits = 0, botHits = 0;
                pHp.Damaged += (receiver, loss, source) => { if (loss > 0f && source == bot) playerHits++; };
                bHp.Damaged += (receiver, loss, source) => { if (loss > 0f && source == player) botHits++; };
                var route = typeof(AarisEquippedGun).GetMethod("ApplyDamage", Static, null,
                    new[] { typeof(Collider), typeof(float), typeof(GameObject),
                        typeof(Vector3?) }, null);
                Check(route != null, "Shared authoritative collider damage route", report);
                Check(pHp.CurrentHealth == 200f && bHp.CurrentHealth == 200f,
                    "Both actors start with 200 HP", report);
                Physics.SyncTransforms();
                route.Invoke(null, new object[] { bBody, 20f, player,
                    (Vector3?)(Origin + Vector3.up * .6f) });
                route.Invoke(null, new object[] { pBody, 20f, bot,
                    (Vector3?)(player.transform.position + Vector3.up * .6f) });
                Check(Mathf.Abs(pHp.CurrentHealth - 180f) < .01f &&
                    Mathf.Abs(bHp.CurrentHealth - 180f) < .01f &&
                    playerHits == 1 && botHits == 1,
                    "Player-to-bot and bot-to-player real hit events", report);
                route.Invoke(null, new object[] { bBody, 20f, player,
                    (Vector3?)(Origin + Vector3.up * 1.65f) });
                Check(Mathf.Abs(bHp.CurrentHealth - 145f) < .01f && botHits == 2,
                    "Headshot applies 1.75x and notifies victim", report);
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static void VerifyDestructibleHitConfirmation(StringBuilder report)
        {
            var root = new GameObject("Destructible hit feedback audit");
            try
            {
                var shooter = new GameObject("Shooter");
                shooter.transform.SetParent(root.transform, false);
                var wall = new GameObject("Gloo Wall");
                wall.transform.SetParent(root.transform, false);
                var wallCollider = wall.AddComponent<BoxCollider>();
                var wallHealth = wall.AddComponent<AarisGlooWallHealth>();
                var hit = typeof(AarisEquippedGun).GetMethod("ApplyDamage", Static,
                    null, new[] { typeof(Collider), typeof(float), typeof(GameObject) }, null);
                Check(hit != null && hit.ReturnType == typeof(bool),
                    "Authoritative hit result", report);
                Check(!(bool)hit.Invoke(null, new object[] { wallCollider, 10f, shooter }),
                    "Uninitialized Gloo Wall cannot confirm damage", report);
                wallHealth.Initialize(60f, 50f);
                Check((bool)hit.Invoke(null, new object[] { wallCollider, 20f, shooter }) &&
                    Mathf.Abs(wallHealth.CurrentHealth - 40f) < .01f,
                    "Gloo Wall real HP loss confirms hit", report);
                Set(wallHealth, "health", 0f);
                Check(!(bool)hit.Invoke(null, new object[] { wallCollider, 20f, shooter }),
                    "Depleted Gloo Wall rejects duplicate hit marker", report);
                var panel = new GameObject("Window");
                panel.transform.SetParent(root.transform, false);
                var panelCollider = panel.AddComponent<BoxCollider>();
                var panelHealth = panel.AddComponent<AarisBreakablePanel>();
                Check(!(bool)hit.Invoke(null, new object[] { panelCollider, 10f, shooter }),
                    "Unconfigured panel rejects phantom damage", report);
                panelHealth.Configure(panel.transform, panelCollider, null,
                    false, 45f, root.transform);
                Check((bool)hit.Invoke(null, new object[] { panelCollider, 20f, shooter }) &&
                    Mathf.Abs(panelHealth.CurrentHealth - 25f) < .01f,
                    "Configured breakable panel reports real HP loss", report);
                Set(panelHealth, "destroyed", true);
                Check(!(bool)hit.Invoke(null, new object[] { panelCollider, 10f, shooter }),
                    "Broken panel rejects duplicate hit marker", report);
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static void VerifyBots(StringBuilder report, FieldInfo active)
        {
            var root = new GameObject("Temporary 100-bot encounter audit");
            try
            {
                var player = new GameObject("Player target");
                player.transform.SetParent(root.transform, false);
                player.transform.position = Origin;
                var pBody = Capsule(player);
                QueryBody(player);
                var pHp = Health(player);
                var end = player.AddComponent<AarisOfflineMatchEnd>();
                var directorObj = new GameObject("Director");
                directorObj.transform.SetParent(root.transform, false);
                var director = directorObj.AddComponent<AarisOfflineBRDirector>();
                Set(director, "player", player.transform);
                Set(director, "playerHealth", pHp);
                Set(director, "matchEnd", end);
                Set(director, "started", true);
                var roster = (Array)Get(director, "rivals");
                Check(roster.Length == 100, "Exactly 100 rival roster slots", report);
                var bots = new Component[100];
                var health = new AarisOfflineHealth[100];
                Vector3[] nearby = { new Vector3(6f, 0f, 0f),
                    new Vector3(0f, 0f, 7f), new Vector3(-8f, 0f, 0f) };
                for (int i = 0; i < 100; i++)
                {
                    var go = new GameObject("QA rival " + i);
                    go.transform.SetParent(root.transform, false);
                    Vector3 offset;
                    if (i < nearby.Length) offset = nearby[i];
                    else
                    {
                        float angle = (i - nearby.Length) * (Mathf.PI * 2f / 97f);
                        float radius = 500f + (i % 9) * 24f;
                        offset = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * radius;
                    }
                    go.transform.position = Origin + offset;
                    go.transform.rotation = Quaternion.LookRotation(-offset.normalized);
                    Capsule(go);
                    QueryBody(go);
                    health[i] = Health(go);
                    bots[i] = go.AddComponent(BotType);
                    Set(bots[i], "health", health[i]);
                    Set(bots[i], "playerTarget", player.transform);
                    Set(bots[i], "playerTargetHealth", pHp);
                    Set(bots[i], "playerTargetCapsule", pBody);
                    var visual = new GameObject("Visible body signal");
                    visual.transform.SetParent(go.transform, false);
                    Set(bots[i], "visualRoot", visual);
                    roster.SetValue(bots[i], i);
                }
                active.SetValue(null, director);
                Physics.SyncTransforms();
                // The production selection advances only once per actual frame.
                // In edit-mode test explicitly emulate nine successive frames.
                for (int frame = 0; frame < 9; frame++)
                {
                    Set(director, "attackerScanFrame", -1);
                    Call(director, "RefreshPlayerAttackers");
                }
                for (int i = 0; i < 3; i++)
                {
                    object[] ray = { player.transform, default(RaycastHit) };
                    bool los = (bool)Call(bots[i], "TryGetSightHit", ray);
                    bool selected = (bool)Call(bots[i], "HasPlayerSightForSelection");
                    report.AppendLine("DEBUG bot=" + i + " visible=" +
                        ((GameObject)Get(bots[i], "visualRoot")).activeInHierarchy +
                        " los=" + los + " selected=" + selected +
                        " hit=" + (((RaycastHit)ray[1]).collider != null ?
                            ((RaycastHit)ray[1]).collider.name : "null"));
                }
                report.AppendLine("DEBUG cursor=" + Get(director, "attackerScanCursor") +
                    " nearest=" + Get(director, "attackerScanFirst") +
                    " second=" + Get(director, "attackerScanSecond"));
                var first = Get(director, "attackerOne");
                var second = Get(director, "attackerTwo");
                Check(first != null && second != null && first != second,
                    "Two distinct nearby player attackers selected", report);
                int permissions = 0;
                for (int i = 0; i < 100; i++)
                    if ((bool)Call(director, "MayAttackPlayer", bots[i])) permissions++;
                Check(permissions == 2, "100 rivals allow exactly two player attackers", report);
                Check(!(bool)Call(director, "MayAttackPlayer", bots[50]),
                    "Far rival cannot swarm player", report);
                var firstVisible = (GameObject)Get(first, "visualRoot");
                firstVisible.SetActive(false);
                Check(!(bool)Call(director, "MayAttackPlayer", first),
                    "Invisible rival loses attack permission immediately", report);
                firstVisible.SetActive(true);

                var muzzle = BotType.GetMethod("MuzzlePathClear", Instance);
                Check(muzzle != null, "Bot close-muzzle guard present", report);
                Check((bool)muzzle.Invoke(bots[1], new object[] { player.transform }),
                    "Bot muzzle clear in open terrain", report);
                // More than the 20-slot small ray buffer can be filled entirely
                // by harmless colliders owned by this shooter. Full does not
                // mean physically blocked: retry with the retained larger buffer.
                var denseSelf = new GameObject("Dense shooter-owned muzzle fragments");
                denseSelf.transform.SetParent(bots[1].transform, false);
                for (int fragmentIndex = 0; fragmentIndex < 25; fragmentIndex++)
                {
                    var fragment = new GameObject("Shooter-owned attachment");
                    fragment.transform.SetParent(denseSelf.transform, false);
                    fragment.transform.localPosition = new Vector3(
                        0f, 1.28f, 0.04f + fragmentIndex * .017f);
                    fragment.AddComponent<BoxCollider>().size =
                        new Vector3(.05f, .05f, .006f);
                }
                Physics.SyncTransforms();
                Check((bool)muzzle.Invoke(bots[1], new object[] { player.transform }),
                    "Dense own colliders do not fake a blocked bot muzzle", report);
                var denseCover = new GameObject("Real blocker among owned muzzle hits");
                denseCover.transform.SetParent(root.transform, false);
                denseCover.transform.position = bots[1].transform.position +
                    bots[1].transform.forward * .37f + Vector3.up * 1.28f;
                denseCover.AddComponent<BoxCollider>().size =
                    new Vector3(.1f, .1f, .02f);
                Physics.SyncTransforms();
                Check(!(bool)muzzle.Invoke(bots[1], new object[] { player.transform }),
                    "Dense own hits cannot hide a real short-muzzle wall", report);
                Object.DestroyImmediate(denseCover);
                Physics.SyncTransforms();
                Check((bool)muzzle.Invoke(bots[1], new object[] { player.transform }),
                    "Dense muzzle clears again after solid cover removal", report);
                Object.DestroyImmediate(denseSelf);
                Physics.SyncTransforms();
                var cover = new GameObject("Bot muzzle obstruction");
                cover.transform.SetParent(root.transform, false);
                cover.transform.position = Origin + new Vector3(0f, 1.28f, 6.77f);
                cover.AddComponent<BoxCollider>().size = new Vector3(.8f, 1f, .08f);
                Physics.SyncTransforms();
                Check(!(bool)muzzle.Invoke(bots[1], new object[] { player.transform }),
                    "Bot shot blocked by close solid muzzle cover", report);
                Check(!(bool)Call(bots[1], "HasSightTo", player.transform),
                    "Close wall now blocks bot tactical LOS before attack", report);
                Check(!(bool)Call(bots[1], "HasPlayerSightForSelection"),
                    "Covered rival cannot claim one of two attack slots", report);
                // Re-run the actual staged selector while the nearest rival
                // remains behind a 48cm muzzle-origin blind spot. The other
                // two visible rivals must replace the blocked contender.
                Set(director, "nextAttackerSelection", -1f);
                Set(director, "attackerScanCursor", -1);
                for (int frame = 0; frame < 9; frame++)
                {
                    Set(director, "attackerScanFrame", -1);
                    Call(director, "RefreshPlayerAttackers");
                }
                Check(!(bool)Call(director, "MayAttackPlayer", bots[1]) &&
                    (bool)Call(director, "MayAttackPlayer", bots[0]) &&
                    (bool)Call(director, "MayAttackPlayer", bots[2]),
                    "100-bot scheduler replaces thin-wall blocked attacker", report);
                Object.DestroyImmediate(cover);
                Physics.SyncTransforms();
                Check((bool)Call(bots[1], "HasPlayerSightForSelection"),
                    "Bot may reacquire attacker slot after wall removal", report);

                var cameraObj = new GameObject("QA scope camera");
                cameraObj.transform.SetParent(player.transform, false);
                var camera = cameraObj.AddComponent<Camera>();
                camera.enabled = false;
                camera.transform.position = Origin + Vector3.up * 1.35f;
                camera.transform.LookAt(bots[1].transform.position + Vector3.up * 1.12f);
                var acquire = typeof(AarisOfflineBRDirector).GetMethod(
                    "TryAcquireAimTarget", Static);
                Check(acquire != null, "HUD and gun shared Red-Lock query present", report);
                object[] args = { camera, player.transform, 50f, 2.3f, Vector3.zero };
                Check((bool)acquire.Invoke(null, args),
                    "Red-Lock finds physical visible rival in range", report);
                var sightWall = new GameObject("QA LOS wall");
                sightWall.transform.SetParent(root.transform, false);
                sightWall.transform.position = Origin + new Vector3(0f, 1.25f, 3f);
                sightWall.AddComponent<BoxCollider>().size = new Vector3(1f, 2f, .15f);
                Physics.SyncTransforms();
                Check(!(bool)acquire.Invoke(null, args),
                    "Red-Lock turns off behind solid wall", report);
                Object.DestroyImmediate(sightWall);
                Physics.SyncTransforms();
                // Regression: at contact range the actual rival capsule may
                // enter the first 24 cm of the player's muzzle corridor.
                // This is an enemy impact, not cover; Red-Lock must not
                // silently turn white while the gun can still damage it.
                Vector3 savedNearPosition = bots[1].transform.position;
                bots[1].transform.position = Origin + Vector3.forward * .65f;
                camera.transform.LookAt(bots[1].transform.position + Vector3.up * 1.12f);
                Physics.SyncTransforms();
                Check((bool)acquire.Invoke(null, args),
                    "Point-blank visible rival retains Red-Lock", report);
                bots[1].transform.position = savedNearPosition;
                camera.transform.LookAt(bots[1].transform.position + Vector3.up * 1.12f);
                Physics.SyncTransforms();

                var gunObj = new GameObject("QA equipped gun");
                gunObj.transform.SetParent(player.transform, false);
                var gun = gunObj.AddComponent<AarisEquippedGun>();
                var spec = new AarisWeaponSpec { id = "qa", category = "AR",
                    magazineSize = 30, reserveAmmo = 60, rpm = 600, damage = 20f,
                    rangeMetres = 50f, pellets = 1, spreadDegrees = 0f,
                    recoilPitchDegrees = 0f, reloadSeconds = 1f, scopePower = 2 };
                Set(gun, "spec", spec);
                Set(gun, "magazine", 30);
                Check(gun.TryFire(camera, player) && gun.Magazine == 29 &&
                    health[1].CurrentHealth < 200f,
                    "Real 2x Red-Lock shot consumes ammo and damages bot", report);
                // Real 100-roster bot-versus-bot combat regression. The
                // closest opponent is hidden by a wall, while another
                // opponent a little farther away is genuinely visible.
                // Use existing rivals and their actual PhysX colliders.
                var hunter = bots[30];
                var blockedRival = bots[31];
                var visibleRival = bots[32];
                Vector3 arena = Origin + new Vector3(100f, 0f, 100f);
                hunter.transform.SetPositionAndRotation(arena, Quaternion.identity);
                blockedRival.transform.position = arena + Vector3.forward * 9f;
                visibleRival.transform.position = arena + Vector3.right * 14f;
                // Register every authored slot before the QA manually moves
                // actors; otherwise the partial index could mask regressions.
                Call(director, "BootstrapRivalSpatialIndex");
                // QA teleports directly; production movement keeps these
                // three spatial entries synchronized after every move.
                for (int slot = 30; slot <= 32; slot++)
                    Call(director, "TrackRivalPosition", slot, bots[slot],
                        bots[slot].transform.position);
                Physics.SyncTransforms();
                object[] nearestArgs = { hunter, null, null };
                Check((bool)Call(director, "FindRivalOpponent", nearestArgs) &&
                    (Transform)nearestArgs[1] == blockedRival.transform,
                    "Nearest rival is initially selected by distance", report);
                var rivalCover = new GameObject("QA bot-versus-bot occlusion wall");
                rivalCover.transform.SetParent(root.transform, false);
                rivalCover.transform.position = arena +
                    new Vector3(0f, 1.20f, 4.5f);
                rivalCover.AddComponent<BoxCollider>().size =
                    new Vector3(2.0f, 2.3f, .25f);
                Physics.SyncTransforms();
                Check(!(bool)Call(hunter, "HasSightToRival", blockedRival.transform),
                    "Nearest bot is physically obscured by real cover", report);
                Check((bool)Call(hunter, "HasSightToRival", visibleRival.transform),
                    "Alternate bot has unobstructed physical LOS", report);
                object[] fallback = { hunter, blockedRival.transform, null, null };
                Check((bool)Call(director, "TryFindVisibleRivalAlternative", fallback) &&
                    (Transform)fallback[2] == visibleRival.transform &&
                    (AarisOfflineHealth)fallback[3] == health[32],
                    "Cover-aware rival hunting chooses the visible second target", report);
                // Verify the actual rival Update decision, not only a direct
                // helper call: the hunter must acquire the second opponent
                // and flag real physical sight in the same think tick.
                Set(hunter, "owner", director);
                Set(hunter, "capsule", hunter.GetComponent<CharacterController>());
                Set(hunter, "nextVisualCheck", float.MaxValue);
                Set(hunter, "staggeredVisualDelay", float.MaxValue);
                Set(hunter, "nextLootAt", float.MaxValue);
                Set(hunter, "nextThink", -1f);
                // At 140m an offscreen fighter is intentionally math-only.
                // Face the real gameplay camera at this hunter to exercise
                // its physical LOS and cover-selection path explicitly.
                camera.transform.LookAt(hunter.transform.position + Vector3.up * .95f);
                camera.enabled = true;
                Set(director, "cachedRivalCamera", camera);
                Set(hunter, "nextCameraProbe", -1f);
                Set(director, "center", new Vector2(arena.x, arena.z));
                Set(director, "radius", 1080f);
                Call(hunter, "Update");
                Check((Transform)Get(hunter, "target") == visibleRival.transform &&
                    (bool)Get(hunter, "canSee"),
                    "Live bot think chooses and sees wall-clear rival", report);
                Object.DestroyImmediate(rivalCover);
                Physics.SyncTransforms();
                Check((bool)Call(hunter, "HasSightToRival", blockedRival.transform),
                    "Nearest rival is visible again after cover removal", report);
                // A previously selected shooter must lose permission as soon
                // as its authoritative HP reaches zero. A new full roster
                // scan may refill the slot, but no intermediate scan slice
                // may ever authorize a third simultaneous player attacker.
                var eliminatedAttacker = Get(director, "attackerOne") as Component;
                int eliminatedIndex = Array.IndexOf(bots, eliminatedAttacker);
                Check(eliminatedIndex >= 0,
                    "Selected attacker still belongs to 100-rival roster", report);
                health[eliminatedIndex].ApplyDamage(250f, player);
                Check(health[eliminatedIndex].IsDead &&
                    !(bool)Call(director, "MayAttackPlayer", eliminatedAttacker),
                    "Eliminated bot immediately loses player attack permission", report);
                Set(director, "nextAttackerSelection", -1f);
                Set(director, "attackerScanCursor", -1);
                int maximumConcurrent = 0;
                int finalConcurrent = 0;
                for (int frame = 0; frame < 9; frame++)
                {
                    Set(director, "attackerScanFrame", -1);
                    Call(director, "RefreshPlayerAttackers");
                    int concurrent = 0;
                    for (int i = 0; i < bots.Length; i++)
                        if ((bool)Call(director, "MayAttackPlayer", bots[i]))
                            concurrent++;
                    maximumConcurrent = Mathf.Max(maximumConcurrent, concurrent);
                    finalConcurrent = concurrent;
                }
                Check(maximumConcurrent <= 2 && finalConcurrent == 2 &&
                    !(bool)Call(director, "MayAttackPlayer", eliminatedAttacker),
                    "Death replacement remains capped at two through every scan slice", report);
                // Six nearby candidates: nearest hidden by a solid house, four
                // other blocked rivals, and one actually clear side opponent.
                // This was invisible to the old three-alternative fallback.
                Vector3 crowd = Origin + new Vector3(160f, 0f, 160f);
                var crowdHunter = bots[40];
                crowdHunter.transform.SetPositionAndRotation(crowd, Quaternion.identity);
                for (int i = 0; i < 5; i++)
                    bots[41 + i].transform.position = crowd +
                        new Vector3((i - 2) * .5f, 0f, 9f + i);
                bots[46].transform.position = crowd + new Vector3(8f, 0f, 20f);
                for (int slot = 40; slot <= 46; slot++)
                    Call(director, "TrackRivalPosition", slot, bots[slot],
                        bots[slot].transform.position);
                var crowdWall = new GameObject("QA dense rival cover");
                crowdWall.transform.SetParent(root.transform, false);
                crowdWall.transform.position = crowd + new Vector3(0f, 1.2f, 4.5f);
                crowdWall.AddComponent<BoxCollider>().size = new Vector3(3f, 2.3f, .25f);
                Physics.SyncTransforms();
                Check((bool)Call(crowdHunter, "HasSightToRival", bots[46].transform),
                    "Distant flank rival has physically clear LOS", report);
                object[] crowdedAlternative = { crowdHunter, bots[41].transform, null, null };
                Check((bool)Call(director, "TryFindVisibleRivalAlternative", crowdedAlternative) &&
                    (Transform)crowdedAlternative[2] == bots[46].transform,
                    "Six-slot fallback finds open rival past four blocked targets", report);
                Object.DestroyImmediate(crowdWall);
                Physics.SyncTransforms();
                // The hash insertion order is opposite to roster order,
                // so deterministic tie-breaking must be explicit.
                bots[47].transform.position = crowd + new Vector3(12f, 0f, 0f);
                bots[48].transform.position = crowd + new Vector3(-12f, 0f, 0f);
                for (int slot = 47; slot <= 48; slot++)
                    Call(director, "TrackRivalPosition", slot, bots[slot],
                        bots[slot].transform.position);
                Physics.SyncTransforms();
                // Restore the crowd fixture before testing ties: its other
                // opponents would otherwise legitimately rank closer.
                for (int slot = 41; slot <= 46; slot++)
                {
                    bots[slot].transform.position = Origin +
                        new Vector3(900f + slot * 3f, 0f, 900f);
                    Call(director, "TrackRivalPosition", slot, bots[slot],
                        bots[slot].transform.position);
                }
                Physics.SyncTransforms();
                object[] equalDistance = { crowdHunter, null, null, null };
                Check((bool)Call(director, "TryFindVisibleRivalAlternative", equalDistance) &&
                    (Transform)equalDistance[2] == bots[47].transform,
                    "Spatial fallback preserves roster-index tie ordering", report);
                // Three centered scope targets behind thin solid cover may
                // eclipse a less-centered but physically hittable fourth.
                Quaternion oldCameraRotation = camera.transform.rotation;
                for (int i = 0; i < 3; i++)
                    bots[60 + i].transform.position =
                        Origin + new Vector3((i - 1) * .05f, 0f, 15f + i);
                bots[63].transform.position = Origin + new Vector3(1.3f, 0f, 25f);
                camera.transform.position = Origin + Vector3.up * 1.35f;
                camera.transform.LookAt(Origin + Vector3.up * 1.12f +
                    Vector3.forward * 20f);
                var thinWall = new GameObject("QA narrow center-cover wall");
                thinWall.transform.SetParent(root.transform, false);
                thinWall.transform.position = Origin + new Vector3(0f, 1.25f, 6f);
                thinWall.AddComponent<BoxCollider>().size = new Vector3(.35f, 2.2f, .1f);
                Physics.SyncTransforms();
                object[] crowdedAim = { camera, player.transform, 40f, 3.7f, Vector3.zero };
                bool focused = (bool)acquire.Invoke(null, crowdedAim);
                Vector3 expectedAim = (bots[63].transform.position + Vector3.up * 1.12f -
                    camera.transform.position).normalized;
                Check(focused && Vector3.Dot((Vector3)crowdedAim[4], expectedAim) > .9999f,
                    "Red-Lock finds fourth open body after three centered hidden rivals", report);
                Object.DestroyImmediate(thinWall);
                camera.transform.rotation = oldCameraRotation;
                Physics.SyncTransforms();
            }
            finally { Object.DestroyImmediate(root); }
        }
    }
}
#endif
