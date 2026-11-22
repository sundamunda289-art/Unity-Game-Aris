#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Tests actual authored terrain and authored runtime spawn logic in Play Mode.
    /// Reports headless Editor timings; never claims Android FPS.
    /// Run ONLY in a dedicated batchmode session; no authored scene is saved.
    /// </summary>
    [InitializeOnLoad]
    public static class AarisFullSceneCombatSoak
    {
        private const string Scene = "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";
        private const string Session = "AarisFullSceneCombatSoakV1";
        private static readonly string DirectoryPath =
            Path.Combine(Path.GetTempPath(), "aaris-full-scene-combat-soak");
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const int BaselineSamples = 200;
        // Keep the usual CI smoke fast. Set AARIS_FULLSCENE_SOAK_SECONDS=25
        // to sample sustained real-scene combat without editing scene assets.
        // Read once per Editor domain load, not per PlayMode frame.
        private static readonly float LoadedSecondsMinimum = ReadLoadedSecondsMinimum();
        private static readonly int LoadedSamplesMinimum =
            Mathf.Max(200, Mathf.CeilToInt(LoadedSecondsMinimum * 48f));

        private static float ReadLoadedSecondsMinimum()
        {
            string value = Environment.GetEnvironmentVariable("AARIS_FULLSCENE_SOAK_SECONDS");
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float seconds) || float.IsNaN(seconds) || float.IsInfinity(seconds))
                return 1.30f;
            return Mathf.Clamp(seconds, 1.30f, 60f);
        }
        private static AarisOfflineBRDirector director;
        private static AarisOfflineDrop drop;
        private static AarisOfflineHealth health;
        private static AarisThirdPersonPlayer player;
        private static ProfilerRecorder mainThread, gcFrame;
        private static Array rivals;
        private static readonly List<float> baselineMain = new List<float>(250);
        private static readonly List<float> loadedMain = new List<float>(3500);
        private static readonly List<long> baselineGc = new List<long>(250);
        private static readonly List<long> loadedGc = new List<long>(3500);
        private static readonly List<float> baselineDelta = new List<float>(250);
        private static readonly List<float> loadedDelta = new List<float>(3500);
        private static int stage, lastFrame = -1, warmup, loadedFrames, damageEvents;
        private static float sampleStarted, previousTimeScale, previousFixedDelta;
        private static bool hadRuntimeError;
        private static int peakSpawned, peakActive;
        private static int peakCloseRigs, peakVisibleBodies, peakNearbyAlive, visualSampleFrames;
        private static Component rigRebuildTarget;
        private static int rigsBeforeTravel, originalRigRenderers;
        private static string errorFirst = "";
        static AarisFullSceneCombatSoak()
        {
            EditorApplication.update += Tick;
            Application.logMessageReceived += RuntimeLog;
        }

        [MenuItem("Aaris/CI/Soak real scene with 100 spawned rivals")]
        public static void Run()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("CI_BATCHMODE_ONLY");
            if (EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorApplication.isCompiling)
                throw new InvalidOperationException("EDITOR_BUSY");
            System.IO.Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(Path.Combine(DirectoryPath, "result.txt"),
                "UNITY " + Application.unityVersion +
                "\nSCENE " + Scene +
                "\nTARGET_LOADED_SECONDS=" + LoadedSecondsMinimum.ToString("F2", CultureInfo.InvariantCulture) +
                "\nTARGET_LOADED_MIN_FRAMES=" + LoadedSamplesMinimum +
                "\nLIMIT=Linux headless Editor only; not Android device FPS\n");
            SessionState.SetBool(Session, false);
            SessionState.SetBool(Session + "Start", true);
            SessionState.SetBool(Session + "Finish", false);
            SessionState.SetInt(Session + "Exit", 1);
            SessionState.SetFloat(Session + "Deadline",
                (float)EditorApplication.timeSinceStartup + 360f);
            stage = 0;
            rigRebuildTarget = null;
            rigsBeforeTravel = originalRigRenderers = 0;
            peakCloseRigs = peakVisibleBodies = peakNearbyAlive = visualSampleFrames = 0;
            hadRuntimeError = false;
            errorFirst = "";
        }

        private static object Read(object instance, string field)
        {
            var member = instance.GetType().GetField(field, Private);
            if (member == null)
                throw new InvalidOperationException("MISSING_FIELD_" + field);
            return member.GetValue(instance);
        }
        private static void Note(string line) =>
            File.AppendAllText(Path.Combine(DirectoryPath, "result.txt"), line + "\n");

        private static void RuntimeLog(string message, string stack, LogType type)
        {
            if (!SessionState.GetBool(Session, false)) return;
            if (type != LogType.Exception && type != LogType.Error &&
                type != LogType.Assert) return;
            // Unity's background Search indexer is unrelated to gameplay.
            if (stack != null && stack.Contains("UnityEditor.Search.")) return;
            if (!hadRuntimeError)
            {
                hadRuntimeError = true;
                errorFirst = message.Split('\n')[0];
                Note("RUNTIME_ERROR " + errorFirst);
            }
        }

        private static void Tick()
        {
            if (SessionState.GetBool(Session + "Finish", false))
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                SessionState.SetBool(Session, false);
                SessionState.SetBool(Session + "Finish", false);
                int code = SessionState.GetInt(Session + "Exit", 1);
                if (Application.isBatchMode) EditorApplication.Exit(code);
                return;
            }
            if (!SessionState.GetBool(Session, false))
            {
                if (!SessionState.GetBool(Session + "Start", false) ||
                    EditorApplication.isPlayingOrWillChangePlaymode ||
                    EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                try
                {
                    SessionState.SetBool(Session + "Start", false);
                    for (int i = 0; i < SceneManager.sceneCount; i++)
                        if (SceneManager.GetSceneAt(i).isDirty)
                            throw new InvalidOperationException("UNSAVED_SCENE");
                    EditorSceneManager.OpenScene(Scene, OpenSceneMode.Single);
                    SessionState.SetBool(Session, true);
                    EditorApplication.isPlaying = true;
                }
                catch (Exception ex) { Fail(ex.GetType().Name + " " + ex.Message); }
                return;
            }
            if (EditorApplication.timeSinceStartup >
                SessionState.GetFloat(Session + "Deadline", float.MaxValue))
            { Fail("TIMEOUT stage=" + stage + " spawned=" + peakSpawned); return; }
            if (!EditorApplication.isPlaying) return;
            int frame = Time.frameCount;
            if (frame == lastFrame || frame == 0) return;
            lastFrame = frame;
            try
            {
                if (stage == 0) { Setup(); return; }
                if (stage == 1) { PrewarmAndBaseline(); return; }
                if (stage == 2) { SpawnAll(); return; }
                if (stage == 3) { MoveToCombatAndMeasure(); return; }
                if (stage == 4) { VerifyRigRetirement(); return; }
                if (stage == 5) { VerifyRigRebuild(); return; }
            }
            catch (Exception ex)
            {
                Fail(ex.GetType().Name + " " + ex.Message +
                    (ex.InnerException == null ? "" :
                        " => " + ex.InnerException.Message));
            }
        }

        private static void Setup()
        {
            if (Time.frameCount < 6) return;
            player = Object.FindFirstObjectByType<AarisThirdPersonPlayer>();
            director = Object.FindFirstObjectByType<AarisOfflineBRDirector>();
            if (player == null || director == null || player.AimCamera == null) return;
            // Wait for MonoBehaviour.Start to schedule the real async visual prewarm.
            drop = player.GetComponent<AarisOfflineDrop>();
            health = player.GetComponent<AarisOfflineHealth>();
            if (drop == null || health == null)
                throw new InvalidOperationException("SAVED_PLAYER_MISSING_DROP_OR_HEALTH");
            director.enabled = false;
            if (!drop.HasLanded)
            {
                // OnAction hands off from the aircraft; private Land uses the
                // same player renderer/capsule restoration path as real landing.
                drop.OnAction();
                var land = typeof(AarisOfflineDrop).GetMethod("Land", Private);
                if (land == null) throw new InvalidOperationException("LAND_METHOD_MISSING");
                land.Invoke(drop, null);
            }
            if (!drop.HasLanded) throw new InvalidOperationException("LAND_FAILED");
            previousTimeScale = Time.timeScale;
            previousFixedDelta = Time.fixedDeltaTime;
            baselineMain.Clear(); baselineGc.Clear(); baselineDelta.Clear();
            loadedMain.Clear(); loadedGc.Clear(); loadedDelta.Clear();
            mainThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 16);
            gcFrame = ProfilerRecorder.StartNew(ProfilerCategory.Memory,
                "GC Allocated In Frame", 16);
            health.Damaged += TrackDamage;
            stage = 1;
            Note("PASS scene loaded, original player landed, director temporarily paused");
        }

        private static void PrewarmAndBaseline()
        {
            // Keep naturally scheduled async Resources prewarm; a disabled
            // MonoBehaviour still runs an already-started Coroutine in Unity.
            if (!(bool)Read(director, "rivalVisualPrewarmComplete")) return;
            if (warmup++ < 40) return;
            Capture(baselineMain, baselineGc, baselineDelta);
            if (baselineMain.Count < BaselineSamples) return;
            Note("PASS 0-bot real-scene baseline captured; prewarm complete");
            director.enabled = true;
            Time.timeScale = 8f;
            Time.fixedDeltaTime = previousFixedDelta * 8f;
            stage = 2;
        }

        private static void SpawnAll()
        {
            if ((Time.frameCount % 20) != 0) return;
            int spawned = (int)Read(director, "spawnedRivals");
            int active = (int)Read(director, "activeRivals");
            if (spawned > peakSpawned) peakSpawned = spawned;
            if (active > peakActive) peakActive = active;
            if (!(bool)Read(director, "spawnComplete")) return;
            rivals = (Array)Read(director, "rivals");
            Note("SPAWNED_REAL_SCENE=" + spawned + "/100" +
                " ACTIVE=" + active + " ATTEMPTED_PASSES=" + Read(director, "spawnPasses"));
            Time.timeScale = 1f;
            Time.fixedDeltaTime = previousFixedDelta;
            // Real model import happens naturally on the next visual update.
            // Move the player to the first valid spawn and 5 other genuine
            // rivals into nearby sight range. This is transient Play Mode only.
            Component first = null;
            for (int i = 0; i < rivals.Length; i++)
            {
                first = rivals.GetValue(i) as Component;
                if (first != null && IsAlive(first)) break;
            }
            if (first == null) throw new InvalidOperationException("NO_ALIVE_RIVAL");
            Vector3 center = first.transform.position;
            player.transform.position = center + new Vector3(0f, 0.15f, -17f);
            int near = 0;
            for (int i = 0; i < rivals.Length && near < 6; i++)
            {
                var bot = rivals.GetValue(i) as Component;
                if (bot == null || !IsAlive(bot)) continue;
                if (near > 0)
                {
                    float angle = near * Mathf.PI * 2f / 6f;
                    bot.transform.position = center +
                        new Vector3(Mathf.Sin(angle) * 11f, .15f,
                            Mathf.Cos(angle) * 11f);
                }
                near++;
            }
            Physics.SyncTransforms();
            Note("NEARBY_ACTUAL_RIVALS=" + near);
            sampleStarted = Time.time;
            warmup = 0;
            stage = 3;
        }

        private static void MoveToCombatAndMeasure()
        {
            if (health.IsDead)
                throw new InvalidOperationException("PLAYER_DIED_BEFORE_PROFILE");
            if (health.CurrentHealth < 130f) health.RestoreHealth(150f);
            if (warmup++ <= 80) return;
            Capture(loadedMain, loadedGc, loadedDelta);
            loadedFrames++;
            // The 25-second endurance run legitimately ends with zero close
            // humanoids after rival-vs-rival kills and LOD retirement. Sample
            // genuine loaded rigs throughout combat, not only at its last frame.
            if ((loadedFrames % 20) == 0) TrackLoadedVisualPeaks();
            // Native visual imports and 0.55 sec staggered checks must get
            // game-time to run, not just many ultra-fast Editor frames.
            if (Time.time - sampleStarted < LoadedSecondsMinimum ||
                loadedFrames < LoadedSamplesMinimum) return;
            int humanoids = 0, far = 0, visible = 0;
            for (int i = 0; i < rivals.Length; i++)
            {
                var bot = rivals.GetValue(i) as Component;
                if (bot == null || !IsAlive(bot)) continue;
                if (HasVisibleBody(bot)) visible++;
                var close = (GameObject)Read(bot, "visualRoot");
                var distant = (GameObject)Read(bot, "farVisualRoot");
                if (close != null && close.activeInHierarchy) humanoids++;
                if (distant != null && distant.activeInHierarchy) far++;
            }
            Note("RUNTIME_ALIVE=" + Read(director, "activeRivals") +
                " VISIBLE=" + visible + " CLOSE_RIGS=" + humanoids +
                " FAR_LODS=" + far + " PLAYER_DAMAGE_EVENTS=" + damageEvents);
            Note("BASELINE_MAIN_THREAD=" + Stats(baselineMain));
            Note("100_BOT_MAIN_THREAD=" + Stats(loadedMain));
            Note("BASELINE_GC=" + GcStats(baselineGc));
            Note("100_BOT_GC=" + GcStats(loadedGc));
            Note("BASELINE_FRAME=" + Stats(baselineDelta));
            Note("100_BOT_FRAME=" + Stats(loadedDelta));
            Note("LOADED_SECONDS=" + (Time.time - sampleStarted).ToString("F3"));
            Note("LOADED_PEAK_NEARBY_ALIVE=" + peakNearbyAlive +
                " PEAK_CLOSE_RIGS=" + peakCloseRigs +
                " PEAK_VISIBLE=" + peakVisibleBodies +
                " VISUAL_SAMPLES=" + visualSampleFrames);
            VerifyScopedCombatHudOnRealActors();
            // A high-quality actor that is visible but never fights is not
            // an end-to-end combat smoke pass. At least one genuine player
            // HP-loss event must come through the authoritative receiver.
            bool pass = peakSpawned == 100 && (humanoids > 0 || peakCloseRigs > 0) &&
                (visible > 0 || peakVisibleBodies > 0) &&
                damageEvents > 0 && !hadRuntimeError;
            if (!pass)
            {
                Note("RESULT=FAIL expected_100_spawns_visible_rigs_and_incoming_damage");
                Complete(1);
                return;
            }
            // The ordinary short smoke separately checks distant rig retirement
            // and bit-for-bit humanoid reconstruction. During a long endurance
            // run those rigs can naturally retire before sampling ends, so do
            // not require a physically impossible long-lived retention fixture.
            if (LoadedSecondsMinimum > 5f)
            {
                Note("PASS sustained 100-bot combat, native scopes, HP, damage and earlier close rigs");
                Note("RIG_REBUILD_QA=run_default_short_soak_separately");
                Note("RESULT=PASS");
                Complete(0);
                return;
            }
            PrepareRigRetentionCheck();
        }

        private static void TrackLoadedVisualPeaks()
        {
            int near = 0, close = 0, visible = 0;
            for (int i = 0; i < rivals.Length; i++)
            {
                var bot = rivals.GetValue(i) as Component;
                if (bot == null || !IsAlive(bot)) continue;
                if ((bot.transform.position - player.transform.position).sqrMagnitude <
                    155f * 155f) near++;
                if (HasVisibleBody(bot)) visible++;
                var rig = (GameObject)Read(bot, "visualRoot");
                if (rig != null && rig.activeInHierarchy) close++;
            }
            peakNearbyAlive = Mathf.Max(peakNearbyAlive, near);
            peakCloseRigs = Mathf.Max(peakCloseRigs, close);
            peakVisibleBodies = Mathf.Max(peakVisibleBodies, visible);
            visualSampleFrames++;
        }

        private static void PrepareRigRetentionCheck()
        {
            rigsBeforeTravel = 0;
            rigRebuildTarget = null;
            for (int i = 0; i < rivals.Length; i++)
            {
                var bot = rivals.GetValue(i) as Component;
                if (bot == null || !IsAlive(bot)) continue;
                var rig = (GameObject)Read(bot, "visualRoot");
                if (rig == null) continue;
                rigsBeforeTravel++;
                if (rigRebuildTarget != null ||
                    (bot.transform.position - player.transform.position).sqrMagnitude <
                    210f * 210f) continue;
                rigRebuildTarget = bot;
                originalRigRenderers = rig.GetComponentsInChildren<Renderer>(true).Length;
            }
            if (rigRebuildTarget == null || rigsBeforeTravel < 2 ||
                originalRigRenderers == 0)
                throw new InvalidOperationException("RIG_RETIRE_FIXTURE_MISSING");
            director.enabled = false;
            health.RestoreHealth(200f);
            sampleStarted = Time.time;
            stage = 4;
            Note("RIG_INSTANCES_BEFORE_TRAVEL=" + rigsBeforeTravel);
        }

        private static void VerifyRigRetirement()
        {
            if (Time.time - sampleStarted < 7.5f) return;
            if (rigRebuildTarget == null || !IsAlive(rigRebuildTarget))
                throw new InvalidOperationException("RIG_RETIRE_TEST_ACTOR_LOST");
            if ((GameObject)Read(rigRebuildTarget, "visualRoot") != null ||
                (bool)Read(rigRebuildTarget, "visualBuilt") ||
                (bool)Read(rigRebuildTarget, "hasAnimations"))
                throw new InvalidOperationException("DISTANT_HUMANOID_STILL_RETAINED");
            int remaining = 0;
            for (int i = 0; i < rivals.Length; i++)
            {
                var bot = rivals.GetValue(i) as Component;
                if (bot != null && IsAlive(bot) &&
                    (GameObject)Read(bot, "visualRoot") != null) remaining++;
            }
            if (remaining >= rigsBeforeTravel)
                throw new InvalidOperationException("NO_RIG_RETIREMENT_OBSERVED");
            Note("PASS distant rig and PlayableGraph retired: " +
                rigsBeforeTravel + " -> " + remaining);
            var capsule = player.GetComponent<CharacterController>();
            capsule.enabled = false;
            player.transform.position = rigRebuildTarget.transform.position +
                new Vector3(0f, 0.3f, -16f);
            capsule.enabled = true;
            Physics.SyncTransforms();
            sampleStarted = Time.time;
            stage = 5;
        }

        private static void VerifyRigRebuild()
        {
            GameObject rebuilt = (GameObject)Read(rigRebuildTarget, "visualRoot");
            if (rebuilt != null && rebuilt.activeInHierarchy &&
                (bool)Read(rigRebuildTarget, "visualBuilt"))
            {
                int renderers = rebuilt.GetComponentsInChildren<Renderer>(true).Length;
                if (renderers != originalRigRenderers)
                    throw new InvalidOperationException("RIG_VISUAL_QUALITY_CHANGED");
                Note("PASS original humanoid rig restored with " +
                    renderers + " renderers");
                Note("RESULT=" + (hadRuntimeError ? "FAIL runtime_error" : "PASS"));
                Complete(hadRuntimeError ? 1 : 0);
                return;
            }
            if (Time.time - sampleStarted > 4f)
                throw new InvalidOperationException("RIG_NOT_REBUILT_ON_RETURN");
        }

        // Runs against the live saved player, its real inventory, HUD, camera
        // and one of the actual 100 rival health/capsule instances. Relocate
        // temporarily above empty world space so map foliage cannot make a
        // scope smoke test depend on random spawn geometry. This is Play Mode
        // only: neither terrain nor authored scene is modified or saved.
        private static void VerifyScopedCombatHudOnRealActors()
        {
            var inventory = player.GetComponent<AarisWeaponInventory>();
            var hud = player.GetComponent<AarisCombatFeedbackHUD>();
            Camera camera = player.AimCamera;
            if (inventory == null || hud == null || camera == null)
                throw new InvalidOperationException("LIVE_SCOPE_PLAYER_HUD_MISSING");
            Component victim = null;
            for (int i = 0; i < rivals.Length; i++)
            {
                var candidate = rivals.GetValue(i) as Component;
                if (candidate != null && IsAlive(candidate) && HasVisibleBody(candidate))
                { victim = candidate; break; }
            }
            if (victim == null)
                throw new InvalidOperationException("NO_VISIBLE_LIVE_RIVAL_FOR_SCOPE");
            var victimHp = victim.GetComponent<AarisOfflineHealth>();
            var victimCapsule = victim.GetComponent<CharacterController>();
            var playerCapsule = player.GetComponent<CharacterController>();
            if (victimHp == null || victimCapsule == null || playerCapsule == null)
                throw new InvalidOperationException("LIVE_COMBAT_CAPSULE_MISSING");
            Vector3 calibration = new Vector3(2600f, 1500f, 2600f);
            playerCapsule.enabled = false;
            player.transform.SetPositionAndRotation(calibration, Quaternion.identity);
            playerCapsule.enabled = true;
            victimCapsule.enabled = false;
            victim.transform.SetPositionAndRotation(
                calibration + new Vector3(0f, 0f, 16f), Quaternion.identity);
            victimCapsule.enabled = true;
            camera.transform.position = calibration + Vector3.up * 1.45f;
            camera.transform.LookAt(victim.transform.position + Vector3.up * 1.12f);
            Physics.SyncTransforms();
            health.RestoreHealth(200f);
            victimHp.RestoreHealth(200f);
            var healthLabel = (Text)Read(hud, "healthText");
            var fill = (Image)Read(hud, "healthFill");
            var scopeLabel = (Text)Read(hud, "scopeText");
            var optic = (Image)Read(hud, "opticalRing");
            var reticle = (Image[])Read(hud, "crosshair");
            if (healthLabel == null || fill == null || scopeLabel == null ||
                optic == null || reticle.Length != 4)
                throw new InvalidOperationException("LIVE_COMBAT_HUD_WIDGET_MISSING");
            if (healthLabel.text != "HP 200 / 200" ||
                Mathf.Abs(fill.rectTransform.sizeDelta.x - 220f) > .1f)
                throw new InvalidOperationException("HP200_LABEL_OR_BAR_NOT_FULL");
            Note("PASS native player HP 200 text and full-width bar");

            // Includes every authored optical category: no scope, normal,
            // 2x and 4x. Real gun model must equip, actual reticle turn red,
            // ammo decrease, and enemy HP decrease via a physical bullet.
            string[] guns = { "Shotgun", "SMG", "AssaultRifle", "Sniper" };
            string[] expected = {
                "NO SCOPE ADS", "NORMAL ADS", "2X ADS", "4X ADS"
            };
            int verified = 0;
            for (int i = 0; i < guns.Length; i++)
            {
                var spec = AarisWeaponCatalog.Get(guns[i]);
                if (spec == null || spec.scopePower != (i == 0 ? 0 : i == 1 ? 1 : i == 2 ? 2 : 4))
                    throw new InvalidOperationException("AUTHORED_SCOPE_MISSING_" + guns[i]);
                AarisWeaponState dropped;
                if (!inventory.TryPickup(new AarisWeaponState(spec, (uint)(100 + i)), out dropped) ||
                    inventory.Current == null || !inventory.HasGun)
                    throw new InvalidOperationException("REAL_GUN_EQUIP_FAILED_" + guns[i]);
                if (inventory.Aiming) inventory.ToggleAim();
                inventory.ToggleAim();
                camera.transform.position = calibration + Vector3.up * 1.45f;
                camera.transform.LookAt(victim.transform.position + Vector3.up * 1.12f);
                Physics.SyncTransforms();
                ForceHudAimRefresh(hud);
                if (scopeLabel.text != expected[i] || !inventory.Aiming ||
                    optic.gameObject.activeSelf != (spec.scopePower >= 2))
                    throw new InvalidOperationException("HUD_SCOPE_LABEL_OR_OPTIC_FAILED_" + guns[i] +
                        " label=" + scopeLabel.text);
                bool locked = (bool)Read(hud, "lastLock");
                if (!locked || reticle[0].color.r < .85f ||
                    reticle[0].color.g > .4f)
                    throw new InvalidOperationException("RED_LOCK_NOT_CONFIRMED_" + guns[i]);
                victimHp.RestoreHealth(200f);
                float before = victimHp.CurrentHealth;
                int ammo = inventory.Current.Magazine;
                int popupIndex = (int)Read(hud, "numberIndex");
                bool fired = inventory.Current.TryFire(camera, player.gameObject);
                if (!fired || inventory.Current.Magazine != ammo - 1 ||
                    victimHp.CurrentHealth >= before ||
                    (int)Read(hud, "numberIndex") <= popupIndex)
                    throw new InvalidOperationException("REAL_SCOPE_SHOT_NOT_DAMAGING_" + guns[i] +
                        " fired=" + fired + " ammo=" + inventory.Current.Magazine +
                        " hp=" + victimHp.CurrentHealth);
                Note("PASS " + guns[i] + " scope=" + spec.scopePower +
                    " Red-Lock, " + expected[i] + ", physical bullet, HUD damage popup");
                verified++;
            }

            // Stance-specific camera/muzzle parity. The real on-foot
            // CharacterController needs a physical support before prone.
            // The fixture floor is temporary and never saved to the scene.
            var stanceFloor = new GameObject("Temporary stance QA support");
            stanceFloor.transform.position = calibration + Vector3.down * .5f;
            stanceFloor.AddComponent<BoxCollider>().size =
                new Vector3(60f, 1f, 60f);
            Physics.SyncTransforms();
            playerCapsule.Move(Vector3.down * .12f);
            victimCapsule.Move(Vector3.down * .12f);
            var actions = player.GetComponent<AarisBattleActions>();
            if (actions == null || !playerCapsule.isGrounded)
                throw new InvalidOperationException("STANCE_QA_PLAYER_NOT_GROUNDED");
            var arSpec = AarisWeaponCatalog.Get("AssaultRifle");
            var poses = new[] {
                AarisBattleActions.PlayerStance.Crouching,
                AarisBattleActions.PlayerStance.Prone,
                AarisBattleActions.PlayerStance.Standing
            };
            foreach (var pose in poses)
            {
                if (!actions.TrySetStance(pose) || actions.Stance != pose)
                    throw new InvalidOperationException("STANCE_TRANSITION_REJECTED_" + pose);
                // After resizing the capsule the next Move resolves the
                // grounded flag before a crouch-to-prone transition.
                playerCapsule.Move(Vector3.down * .08f);
                AarisWeaponState outgoing;
                if (!inventory.TryPickup(new AarisWeaponState(arSpec,
                    (uint)(550 + (int)pose)), out outgoing))
                    throw new InvalidOperationException("STANCE_RIFLE_EQUIP_FAILED_" + pose);
                inventory.ToggleAim();
                victimHp.RestoreHealth(200f);
                camera.transform.position = player.transform.position +
                    Vector3.up * actions.CameraEyeHeight;
                camera.transform.LookAt(victim.transform.position + Vector3.up * 1.12f);
                Physics.SyncTransforms();
                ForceHudAimRefresh(hud);
                if (!(bool)Read(hud, "lastLock") ||
                    scopeLabel.text != "2X ADS")
                    throw new InvalidOperationException("STANCE_RED_LOCK_FAILED_" + pose);
                int beforeAmmo = inventory.Current.Magazine;
                float priorHealth = victimHp.CurrentHealth;
                if (!inventory.Current.TryFire(camera, player.gameObject) ||
                    inventory.Current.Magazine != beforeAmmo - 1 ||
                    victimHp.CurrentHealth >= priorHealth)
                    throw new InvalidOperationException("STANCE_REAL_SHOT_FAILED_" + pose);
                Note("PASS " + pose + " 2X Red-Lock plus real physical shot");
            }
            if (actions.Stance != AarisBattleActions.PlayerStance.Standing)
                throw new InvalidOperationException("STANCE_NOT_RESTORED");
            Object.Destroy(stanceFloor);

            // Reticle must never offer a red lock through solid cover; the
            // real camera and gun must not damage the now-occluded rival.
            var wall = new GameObject("Temporary real-scope LOS blocker");
            wall.transform.position = calibration + new Vector3(0f, 1.20f, 8f);
            wall.AddComponent<BoxCollider>().size = new Vector3(2.5f, 2.2f, .20f);
            Physics.SyncTransforms();
            var ar = AarisWeaponCatalog.Get("AssaultRifle");
            AarisWeaponState ignored;
            if (!inventory.TryPickup(new AarisWeaponState(ar, 878u), out ignored))
                throw new InvalidOperationException("BLOCKED_SCOPE_EQUIP_FAILED");
            inventory.ToggleAim();
            camera.transform.position = calibration + Vector3.up * 1.45f;
            camera.transform.LookAt(victim.transform.position + Vector3.up * 1.12f);
            ForceHudAimRefresh(hud);
            if ((bool)Read(hud, "lastLock") ||
                reticle[0].color.g < .75f)
                throw new InvalidOperationException("RED_LOCK_THROUGH_SOLID_WALL");
            victimHp.RestoreHealth(200f);
            float beforeWallShot = victimHp.CurrentHealth;
            int beforeWallAmmo = inventory.Current.Magazine;
            bool wallShot = inventory.Current.TryFire(camera, player.gameObject);
            if (!wallShot || inventory.Current.Magazine != beforeWallAmmo - 1 ||
                Mathf.Abs(victimHp.CurrentHealth - beforeWallShot) > .001f)
                throw new InvalidOperationException("REAL_WALL_SHOT_PASSED_COVER");
            Note("PASS real AssaultRifle no red-lock and no HP damage through wall");
            Object.Destroy(wall);
            // A punch has no hit coordinate: validate actual 10 HP body damage
            // and the reusable floating damage-number feedback in Play Mode.
            victimHp.RestoreHealth(200f);
            int meleePopupBefore = (int)Read(hud, "numberIndex");
            var meleeRoute = typeof(AarisEquippedGun).GetMethod("ApplyDamage",
                BindingFlags.Static | BindingFlags.NonPublic, null,
                new[] { typeof(Collider), typeof(float), typeof(GameObject) }, null);
            if (meleeRoute == null)
                throw new InvalidOperationException("MELEE_DAMAGE_ROUTE_MISSING");
            meleeRoute.Invoke(null, new object[] { victimCapsule, 10f, player.gameObject });
            if (Mathf.Abs(victimHp.CurrentHealth - 190f) > .001f ||
                (int)Read(hud, "numberIndex") != meleePopupBefore + 1)
                throw new InvalidOperationException("MELEE_DAMAGE_FLOATING_NUMBER_MISSING");
            Note("PASS unpositioned melee hits body HP and shows pooled damage popup");

            // Real incoming damage must report a camera-relative hit bearing,
            // not just flash a red overlay. The calibrated rival is directly
            // in front of the same authored camera used by the weapon.
            health.ApplyDamage(25f, victim.gameObject);
            var directionLabel = (Text)Read(hud, "hitDirection");
            if (healthLabel.text != "HP 175 / 200" ||
                Mathf.Abs(fill.rectTransform.sizeDelta.x - 192.5f) > .2f ||
                !((Image)Read(hud, "hitFlash")).gameObject.activeSelf ||
                directionLabel == null || !directionLabel.gameObject.activeSelf ||
                directionLabel.text != "HIT FRONT")
                throw new InvalidOperationException("HP175_FLASH_OR_HIT_BEARING_FAILED");
            Note("PASS native HP 175 / 200, 87.5% bar, hit flash and HIT FRONT");
            // A self-inflicted hit must never keep the previous enemy bearing.
            health.ApplyDamage(1f, player.gameObject);
            if (directionLabel.gameObject.activeSelf)
                throw new InvalidOperationException("SELF_DAMAGE_STALE_ENEMY_DIRECTION");
            Note("PASS self damage clears the old enemy hit direction");
            Note("SCOPE_TIERS_VERIFIED=" + verified + "/4");
        }

        private static void ForceHudAimRefresh(AarisCombatFeedbackHUD hud)
        {
            var field = hud.GetType().GetField("nextAimCheck", Private);
            var update = hud.GetType().GetMethod("LateUpdate", Private);
            if (field == null || update == null)
                throw new InvalidOperationException("COMBAT_HUD_AIM_REFRESH_MISSING");
            field.SetValue(hud, -1f);
            update.Invoke(hud, null);
        }

        private static bool IsAlive(Component bot)
        {
            var property = bot.GetType().GetProperty("IsAlive",
                BindingFlags.Instance | BindingFlags.Public);
            return property != null && (bool)property.GetValue(bot);
        }

        private static bool HasVisibleBody(Component bot)
        {
            var property = bot.GetType().GetProperty("HasVisibleBody",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return property != null && (bool)property.GetValue(bot);
        }

        private static void TrackDamage(AarisOfflineHealth receiver,
            float amount, GameObject source)
        {
            if (amount > 0f) damageEvents++;
        }

        private static void Capture(List<float> cpu, List<long> gc,
            List<float> frames)
        {
            if (mainThread.Valid && mainThread.LastValue > 0)
                cpu.Add(mainThread.LastValue / 1000000f);
            if (gcFrame.Valid && gcFrame.LastValue >= 0)
                gc.Add(gcFrame.LastValue);
            float elapsed = Time.unscaledDeltaTime * 1000f;
            if (elapsed > 0f && elapsed < 3000f) frames.Add(elapsed);
        }

        private static string Stats(List<float> values)
        {
            if (values.Count == 0) return "UNAVAILABLE";
            float[] sorted = values.ToArray();
            Array.Sort(sorted);
            double total = 0;
            foreach (float v in sorted) total += v;
            int index = Mathf.Clamp(Mathf.CeilToInt(sorted.Length * .95f) - 1,
                0, sorted.Length - 1);
            return "frames=" + sorted.Length + " mean=" +
                (total / sorted.Length).ToString("F3") +
                "ms p95=" + sorted[index].ToString("F3") + "ms";
        }

        private static string GcStats(List<long> values)
        {
            if (values.Count == 0) return "UNAVAILABLE";
            long total = 0, peak = 0;
            foreach (long value in values)
            {
                total += value;
                peak = Math.Max(peak, value);
            }
            return "frames=" + values.Count + " mean=" +
                (total / (double)values.Count).ToString("F1") +
                "B/frame max=" + peak + "B/frame";
        }

        private static void Fail(string failure)
        {
            Note("RESULT=FAIL " + failure);
            Complete(1);
        }

        private static void Complete(int code)
        {
            if (mainThread.Valid) mainThread.Dispose();
            if (gcFrame.Valid) gcFrame.Dispose();
            if (health != null) health.Damaged -= TrackDamage;
            Time.timeScale = previousTimeScale > 0 ? previousTimeScale : 1f;
            if (previousFixedDelta > 0) Time.fixedDeltaTime = previousFixedDelta;
            SessionState.SetInt(Session + "Exit", code);
            SessionState.SetBool(Session + "Finish", true);
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        }
    }
}
#endif
