#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Independent exact-boundary and native PhysX parity checks for logical
    /// rival receivers. Dedicated batch Editor only; no assets are saved.
    /// </summary>
    [InitializeOnLoad]
    public static class AarisRivalHitMathQA
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic;
        private const string Output = "/tmp/aaris-rival-hit-math-qa";
        private const string Session = "AarisRivalHitMathQA.PlayMode";
        private delegate bool CapsuleRay(Ray ray, Vector3 feet, float height,
            float radius, float maxDistance, out float distance, out Vector3 normal);
        private static CapsuleRay cast;
        private static Type rivalType, hitType;
        private static readonly List<string> results = new List<string>(48);
        private static int failures, checks;
        private static IEnumerator admissionChecks;
        private static object priorDirector;
        private static int lastAdmissionFrame = -1;

        static AarisRivalHitMathQA()
        {
            EditorApplication.update += Tick;
            Application.logMessageReceived += RuntimeLog;
        }

        [MenuItem("Aaris/CI/Verify analytic rival hits against PhysX")]
        public static void Run()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("CI_BATCHMODE_ONLY");
            foreach (string argument in Environment.GetCommandLineArgs())
                if (string.Equals(argument, "-quit", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("PLAYMODE_QA_MUST_LAUNCH_WITHOUT_QUIT");
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("EDITOR_BUSY");
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("UNSAVED_SCENE");
            Directory.CreateDirectory(Output);
            // This entry point is dedicated batch-only. Isolate the default
            // Physics world from any restored map so its colliders cannot
            // contaminate known-distance cover and ray cases. Actual Play Mode
            // is required: a fresh Edit Mode CharacterController can expose
            // bounds yet have no native ray-query actor in Unity 6000.3.
            Scene temporary = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SceneManager.SetActiveScene(temporary);
            SessionState.SetBool(Session + ".Active", true);
            SessionState.SetBool(Session + ".Executed", false);
            SessionState.SetBool(Session + ".Finishing", false);
            SessionState.SetInt(Session + ".Errors", 0);
            SessionState.SetInt(Session + ".Exit", 1);
            SessionState.SetString(Session + ".FirstError", "");
            SessionState.SetFloat(Session + ".Deadline", (float)EditorApplication.timeSinceStartup + 180f);
            File.WriteAllText(Path.Combine(Output, "result.txt"), "STARTING_EMPTY_SCENE_PLAYMODE unity=" + Application.unityVersion + "\n");
            EditorApplication.isPlaying = true;
        }

        private static void RuntimeLog(string condition, string stack, LogType type)
        {
            if (!SessionState.GetBool(Session + ".Active", false) ||
                (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)) return;
            int count = SessionState.GetInt(Session + ".Errors", 0);
            SessionState.SetInt(Session + ".Errors", count + 1);
            if (count == 0) SessionState.SetString(Session + ".FirstError", condition + "\n" + stack);
        }

        private static void Tick()
        {
            if (!SessionState.GetBool(Session + ".Active", false)) return;
            if (SessionState.GetBool(Session + ".Finishing", false))
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    if (EditorApplication.timeSinceStartup <= SessionState.GetFloat(Session + ".Deadline", 0f) + 30f)
                        return;
                    File.AppendAllText(Path.Combine(Output, "result.txt"), "FAIL PLAYMODE_EXIT_TIMEOUT\nRESULT=FAIL\n");
                    SessionState.SetBool(Session + ".Active", false);
                    EditorApplication.Exit(1);
                    return;
                }
                int errors = SessionState.GetInt(Session + ".Errors", 0);
                int exitCode = SessionState.GetInt(Session + ".Exit", 1);
                if (errors != 0) exitCode = 1;
                File.AppendAllText(Path.Combine(Output, "result.txt"),
                    "PLAYMODE_RUNTIME_ERRORS=" + errors + "\n" +
                    (errors == 0 ? "" : "FIRST_RUNTIME_ERROR=" + SessionState.GetString(Session + ".FirstError", "") + "\n") +
                    "RESULT=" + (exitCode == 0 ? "PASS" : "FAIL") + "\n");
                SessionState.SetBool(Session + ".Active", false);
                SessionState.SetBool(Session + ".Finishing", false);
                EditorApplication.Exit(exitCode);
                return;
            }
            if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Session + ".Deadline", 0f))
            {
                if (admissionChecks != null)
                {
                    Check("native_receiver_admission_deadline", false);
                    FinishChecks();
                    return;
                }
                File.AppendAllText(Path.Combine(Output, "result.txt"), "FAIL PLAYMODE_ENTRY_TIMEOUT\n");
                SessionState.SetInt(Session + ".Exit", 1);
                SessionState.SetBool(Session + ".Finishing", true);
                EditorApplication.isPlaying = false;
                return;
            }
            if (!EditorApplication.isPlaying || !Application.isPlaying || Time.frameCount < 2) return;
            if (SessionState.GetBool(Session + ".Executed", false))
            {
                if (admissionChecks != null) AdvanceAdmissionChecks();
                return;
            }
            SessionState.SetBool(Session + ".Executed", true);
            ExecuteChecks();
        }

        private static void ExecuteChecks()
        {
            results.Clear(); failures = checks = 0;
            priorDirector = typeof(AarisOfflineBRDirector).GetField("activeDirector", Members).GetValue(null);
            try
            {
                Assembly runtime = typeof(AarisOfflineBRDirector).Assembly;
                Type math = runtime.GetType("Aaris.MapReview.AarisRivalHitMath", true);
                rivalType = runtime.GetType("Aaris.MapReview.AarisOfflineRival", true);
                hitType = runtime.GetType("Aaris.MapReview.AarisCombatHit", true);
                cast = (CapsuleRay)Delegate.CreateDelegate(typeof(CapsuleRay), math.GetMethod("RaycastVerticalCapsule", Members));
                BoundaryChecks();
                NativeParity();
                IntegrationChecks();
                SectorBoundaryChecks(runtime);
                admissionChecks = ReceiverAdmissionChecks();
                lastAdmissionFrame = -1;
            }
            catch (Exception ex)
            {
                failures++;
                results.Add("FAIL HARNESS_EXCEPTION " + ex);
                FinishChecks();
                return;
            }
            AdvanceAdmissionChecks();
        }

        private static void AdvanceAdmissionChecks()
        {
            // Editor updates can run repeatedly between player frames. Only
            // actual Play frames advance the asynchronous receiver fixture.
            if (lastAdmissionFrame == Time.frameCount) return;
            lastAdmissionFrame = Time.frameCount;
            try
            {
                if (admissionChecks != null && admissionChecks.MoveNext()) return;
            }
            catch (Exception ex)
            {
                failures++;
                results.Add("FAIL NATIVE_ADMISSION_EXCEPTION " + ex);
            }
            FinishChecks();
        }

        private static void FinishChecks()
        {
            try { (admissionChecks as IDisposable)?.Dispose(); }
            catch (Exception ex) { failures++; results.Add("FAIL ADMISSION_TEARDOWN " + ex); }
            admissionChecks = null;
            // Test objects are destroyed by each case. Keep the sole
            // unsaved empty scene until the dedicated Editor exits.
            typeof(AarisOfflineBRDirector).GetField("activeDirector", Members).SetValue(null, priorDirector);
            results.Insert(0, "UNITY=" + Application.unityVersion + " MODE=PLAY CHECKS=" + checks + " FAILURES=" + failures);
            results.Add("CHECK_RESULT=" + (failures == 0 ? "PASS" : "FAIL"));
            results.Add("SCOPE=analytic geometry, native capsule parity, nearest physical cover, logical head/body damage, off-camera blast eligibility/LOS, exact 150m active-ring boundaries, native CCT admission across ordinary fixed/player frames and actual rival Update/Move; not Android performance or full weapon-input certification");
            File.WriteAllLines(Path.Combine(Output, "result.txt"), results);
            SessionState.SetInt(Session + ".Exit", failures == 0 ? 0 : 1);
            SessionState.SetBool(Session + ".Finishing", true);
            EditorApplication.isPlaying = false;
        }

        private static void Check(string name, bool passed)
        { checks++; if (!passed) failures++; results.Add((passed ? "PASS " : "FAIL ") + name); }

        private static bool Hit(Vector3 origin, Vector3 direction, float range, out float distance)
        { return cast(new Ray(origin, direction), Vector3.zero, 1.8f, .31f, range, out distance, out _); }

        private static void SectorBoundaryChecks(Assembly runtime)
        {
            Type policy = runtime.GetType("Aaris.MapReview.AarisWorldSectorPolicy", true);
            MethodInfo intersects = policy.GetMethod("IntersectsActiveRing", Members);
            Vector2[] positions = { Vector2.zero, new Vector2(149.999f, 149.999f),
                new Vector2(150f, 150f), new Vector2(-.001f, -.001f),
                new Vector2(-150f, -150f), new Vector2(-150.001f, 600f) };
            foreach (Vector2 position in positions)
            {
                int selected = 0;
                int px = Mathf.FloorToInt(position.x / 150f);
                int pz = Mathf.FloorToInt(position.y / 150f);
                bool exact = true;
                for (int x = px - 3; x <= px + 3; x++)
                    for (int z = pz - 3; z <= pz + 3; z++)
                    {
                        bool actual = (bool)intersects.Invoke(null, new object[] {
                            (x + .5f) * 150f, (z + .5f) * 150f, 75f, 75f,
                            position.x, position.y, 0f });
                        bool expected = Mathf.Abs(x - px) <= 1 && Mathf.Abs(z - pz) <= 1;
                        if (actual) selected++;
                        exact &= actual == expected;
                    }
                Check("sector_exact_nine_cells_at_" + position.ToString("R"), exact && selected == 9);
            }
            bool Test(float centerX, float centerZ, float halfX, float halfZ, float padding = 0f)
                => (bool)intersects.Invoke(null, new object[] {
                    centerX, centerZ, halfX, halfZ, 0f, 0f, padding });
            Check("sector_edge_contact_has_no_area", !Test(375f, 75f, 75f, 75f));
            Check("sector_positive_edge_geometry_spill", Test(375f, 75f, 75.001f, 75f));
            Check("sector_corner_contact_has_no_area", !Test(375f, 375f, 75f, 75f));
            Check("sector_corner_one_axis_spill_insufficient", !Test(375f, 375f, 75.001f, 75f));
            Check("sector_corner_both_axes_spill", Test(375f, 375f, 75.001f, 75.001f));
            Check("sector_prefetch_padding_includes_edge", Test(375f, 75f, 75f, 75f, 1f));
        }

        private static void BoundaryChecks()
        {
            bool yes = Hit(new Vector3(0, .9f, -10), Vector3.forward, 20, out float d);
            Check("body_near_distance", yes && Mathf.Abs(d - 9.69f) < .0001f);
            yes = Hit(new Vector3(0, .9f, -480), Vector3.forward, 500, out d);
            Check("body_480m_precision", yes && Mathf.Abs(d - 479.69f) < .0001f);
            Check("max_range_reject", !Hit(new Vector3(0, .9f, -10), Vector3.forward, 9.68f, out _));
            Check("max_range_boundary_accept", Hit(new Vector3(0, .9f, -10), Vector3.forward, 9.69f, out _));
            Check("away_from_target", !Hit(new Vector3(0, .9f, -10), Vector3.back, 20, out _));
            Check("horizontal_above_head", !Hit(new Vector3(0, 1.81f, -10), Vector3.forward, 20, out _));
            yes = Hit(new Vector3(0, 5, 0), Vector3.down, 10, out d);
            Check("vertical_top_cap", yes && Mathf.Abs(d - 3.2f) < .0001f);
            yes = Hit(new Vector3(0, -5, 0), Vector3.up, 10, out d);
            Check("vertical_bottom_cap", yes && Mathf.Abs(d - 5f) < .0001f);
            yes = Hit(new Vector3(.31f, .9f, -10), Vector3.forward, 20, out d);
            Check("exact_cylinder_tangent", yes && Mathf.Abs(d - 10f) < .001f);
            Check("outside_tangent_miss", !Hit(new Vector3(.3101f, .9f, -10), Vector3.forward, 20, out _));
            yes = Hit(new Vector3(0, .9f, 0), Vector3.forward, 0, out d);
            Check("inside_origin_zero_distance", yes && d == 0f);
            yes = cast(new Ray(new Vector3(0, .9f, -10), new Vector3(0, 0, 3)), Vector3.zero,
                1.8f, .31f, 20, out d, out Vector3 normal);
            Check("nonunit_direction_world_metres", yes && Mathf.Abs(d - 9.69f) < .0001f && Vector3.Dot(normal, Vector3.back) > .9999f);
            Check("zero_direction_reject", !Hit(new Vector3(0, .9f, -10), Vector3.zero, 20, out _));
            Check("nan_origin_reject", !Hit(new Vector3(float.NaN, 0, 0), Vector3.forward, 20, out _));
            Check("infinite_range_reject", !Hit(new Vector3(0, .9f, -10), Vector3.forward, float.PositiveInfinity, out _));
            Check("negative_range_reject", !Hit(new Vector3(0, .9f, -10), Vector3.forward, -1, out _));
            Check("infinite_height_reject", !cast(new Ray(new Vector3(0, 9, -10), Vector3.forward), Vector3.zero,
                float.PositiveInfinity, .31f, 20, out _, out _));
            Check("infinite_radius_reject", !cast(new Ray(new Vector3(0, .9f, -10), Vector3.forward), Vector3.zero,
                float.PositiveInfinity, float.PositiveInfinity, 20, out _, out _));
            Check("invalid_height_reject", !cast(new Ray(new Vector3(0, .9f, -10), Vector3.forward), Vector3.zero,
                .3f, .31f, 20, out _, out _));
        }

        private static void NativeParity()
        {
            var root = new GameObject("QA native capsule parity");
            var collider = root.AddComponent<CapsuleCollider>();
            collider.direction = 1; collider.center = Vector3.up * .9f; collider.height = 1.8f; collider.radius = .31f;
            var random = new System.Random(680610);
            int disagree = 0, comparedHits = 0;
            float largestDistanceError = 0f;
            try
            {
                Vector3[] scales = { Vector3.one, new Vector3(1.5f, 2f, .8f), new Vector3(3f, .5f, 2f) };
                for (int shape = 0; shape < scales.Length; shape++)
                {
                    root.transform.position = new Vector3(25f, 3f, -40f);
                    root.transform.localScale = scales[shape];
                    float radius = .31f * Mathf.Max(scales[shape].x, scales[shape].z);
                    float height = Mathf.Max(1.8f * scales[shape].y, radius * 2f);
                    Vector3 center = root.transform.TransformPoint(Vector3.up * .9f);
                    Vector3 feet = center - Vector3.up * (height * .5f);
                    Physics.SyncTransforms();
                    for (int i = 0; i < 160; i++)
                    {
                        float angle = (float)(random.NextDouble() * Math.PI * 2);
                        float far = i % 2 == 0 ? 8f : 480f;
                        Vector3 offset = new Vector3(Mathf.Sin(angle) * far,
                            (float)(random.NextDouble() - .5) * 6f, Mathf.Cos(angle) * far);
                        Vector3 origin = center + offset;
                        Vector3 destination = center + new Vector3(
                            (float)(random.NextDouble() - .5) * radius * 2.8f,
                            (float)(random.NextDouble() - .5) * height * 1.5f,
                            (float)(random.NextDouble() - .5) * radius * 2.8f);
                        Ray ray = new Ray(origin, (destination - origin).normalized);
                        bool expected = collider.Raycast(ray, out RaycastHit hit, 600f);
                        bool actual = cast(ray, feet, height, radius, 600f, out float distance, out _);
                        if (expected != actual) { disagree++; continue; }
                        if (!expected) continue;
                        comparedHits++;
                        float error = Mathf.Abs(hit.distance - distance);
                        largestDistanceError = Mathf.Max(largestDistanceError, error);
                        if (error > .003f) disagree++;
                    }
                }
            }
            finally { Object.DestroyImmediate(root); }
            Check("480_seeded_near_far_scaled_rays_match_native_capsule", disagree == 0 && comparedHits > 60);
            results.Add("PARITY_HITS=" + comparedHits + " DISAGREEMENTS=" + disagree +
                " MAX_DISTANCE_ERROR_METRES=" + largestDistanceError.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        private static object Read(object value, string name) => value.GetType().GetField(name, Members).GetValue(value);
        private static void Set(object value, string name, object content) => value.GetType().GetField(name, Members).SetValue(value, content);

        private static bool First(Ray ray, float range, Transform shooter, out object hit, bool visibleOnly = true)
        {
            object[] args = { ray, range, shooter, Activator.CreateInstance(hitType), visibleOnly };
            bool found = (bool)typeof(AarisOfflineBRDirector).GetMethod("TryFirstCombatHit", Members).Invoke(null, args);
            hit = args[3]; return found;
        }

        private static bool LogicalReceiver(Component rival) =>
            (bool)rivalType.GetProperty("HasLogicalHitReceiver", Members).GetValue(rival);

        private static bool ReadyReceiver(Component rival) => (bool)Read(rival, "nativeReceiverReady");

        private static Ray ReceiverBodyRay(Component rival)
        {
            // Follow the actual post-Move pose with a known 10m body ray.
            // This changes the query, never the actor or physics state.
            Vector3 center = rival.transform.TransformPoint(new Vector3(0f, .9f, 0f));
            return new Ray(center - Vector3.forward * 10f, Vector3.forward);
        }

        private static bool SameReceiverHit(Ray ray, Transform shooter, Component rival, CharacterController receiver)
        {
            if (!First(ray, 30f, shooter, out object hit)) return false;
            return (Component)Read(hit, "logicalRival") == rival || (Collider)Read(hit, "collider") == receiver;
        }

        private static void ReceiverDiagnostic(string stage, Component rival, CharacterController receiver,
            Transform shooter, Ray ray)
        {
            bool found = First(ray, 30f, shooter, out object hit);
            Collider physical = (Collider)Read(hit, "collider");
            bool direct = receiver.Raycast(ray, out RaycastHit directHit, 30f);
            bool physics = Physics.Raycast(ray, out RaycastHit physicsHit, 30f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            results.Add("NATIVE_RECEIVER_DIAGNOSTIC stage=" + stage + " playing=" + Application.isPlaying +
                " frame=" + Time.frameCount + " fixedTime=" + Time.fixedTime.ToString("R") +
                " enabled=" + receiver.enabled + " trigger=" + receiver.isTrigger +
                " detectCollisions=" + receiver.detectCollisions + " ready=" + ReadyReceiver(rival) +
                " active=" + rival.gameObject.activeInHierarchy + " layer=" + rival.gameObject.layer +
                " position=" + rival.transform.position.ToString("R") +
                " ray_origin=" + ray.origin.ToString("R") + " ray_direction=" + ray.direction.ToString("R") +
                " bounds_center=" + receiver.bounds.center.ToString("R") +
                " bounds_size=" + receiver.bounds.size.ToString("R") +
                " logicalEligible=" + LogicalReceiver(rival) + " unifiedFound=" + found +
                " unifiedNative=" + (physical == receiver) +
                " unifiedLogical=" + ((Component)Read(hit, "logicalRival") == rival) +
                " directFound=" + direct + " directNative=" + (directHit.collider == receiver) +
                " directDistance=" + directHit.distance.ToString("R") +
                " physicsFound=" + physics + " physicsNative=" + (physicsHit.collider == receiver) +
                " physicsDistance=" + physicsHit.distance.ToString("R"));
            File.WriteAllLines(Path.Combine(Output, "result.txt"), results);
        }

        private static IEnumerator ReceiverAdmissionChecks()
        {
            var root = new GameObject("QA real native receiver admission");
            try
            {
                var directorObject = new GameObject("QA admission director");
                directorObject.transform.SetParent(root.transform);
                var director = directorObject.AddComponent<AarisOfflineBRDirector>(); director.enabled = false;
                typeof(AarisOfflineBRDirector).GetField("activeDirector", Members).SetValue(null, director);
                var player = new GameObject("QA healthy admission player"); player.transform.SetParent(root.transform);
                var playerHealth = player.AddComponent<AarisOfflineHealth>();
                Type matchType = rivalType.Assembly.GetType("Aaris.MapReview.AarisOfflineMatchEnd", true);
                Component match = player.AddComponent(matchType); ((Behaviour)match).enabled = false;
                Set(director, "player", player.transform); Set(director, "playerHealth", playerHealth);
                Set(director, "matchEnd", match); Set(director, "started", true); Set(director, "radius", 1080f);

                var floor = new GameObject("QA admission floor"); floor.transform.SetParent(root.transform);
                floor.transform.position = new Vector3(0f, -.5f, 10f);
                floor.AddComponent<BoxCollider>().size = new Vector3(50f, 1f, 50f);
                var target = new GameObject("QA admission rival"); target.transform.SetParent(root.transform);
                target.transform.position = new Vector3(0f, 0f, 10f);
                var health = target.AddComponent<AarisOfflineHealth>();
                Component rival = target.AddComponent(rivalType); ((Behaviour)rival).enabled = false;
                Set(rival, "owner", director); Set(rival, "health", health);
                Set(rival, "playerTarget", player.transform); Set(rival, "playerTargetHealth", playerHealth);
                Set(rival, "spatialSlot", 0); Set(rival, "virtualBody", false);
                Set(rival, "lastCameraRelevant", true); Set(rival, "wasWithinDetailedDistance", true);
                // Isolate physical admission from assets, looting, enemy
                // selection and camera changes. Movement itself remains the
                // real enabled rival Update -> TickRival -> capsule.Move path.
                foreach (string field in new[] { "nextCameraProbe", "nextVisualCheck", "staggeredVisualDelay",
                    "nextLootAt", "nextThink", "nextSeparationAt", "nextAttack" })
                    Set(rival, field, float.MaxValue);
                Set(rival, "verticalSpeed", -1.5f);
                Set(rival, "lastSimulationTime", Time.time);
                var visible = new GameObject("QA admission silhouette"); visible.transform.SetParent(target.transform, false);
                Set(rival, "farVisualRoot", visible);
                ((Array)Read(director, "rivals")).SetValue(rival, 0);
                Ray bodyRay = ReceiverBodyRay(rival);
                MethodInfo ensure = rivalType.GetMethod("EnsurePhysicalReceiver", Members);
                MethodInfo enable = rivalType.GetMethod("SetPhysicalReceiverEnabled", Members);
                Check("native_receiver_production_lifecycle_methods_exist", ensure != null && enable != null);
                if (ensure == null || enable == null) yield break;
                var receiver = (CharacterController)ensure.Invoke(rival, null);
                Physics.SyncTransforms();
                Check("fresh_enabled_pending_native_receiver_remains_hittable",
                    receiver.enabled && !ReadyReceiver(rival) && LogicalReceiver(rival) &&
                    SameReceiverHit(bodyRay, player.transform, rival, receiver));
                Array candidates = Array.CreateInstance(rivalType, 100);
                MethodInfo copy = typeof(AarisOfflineBRDirector).GetMethod("CopyLogicalBlastCandidates", Members);
                int count = (int)copy.Invoke(null, new object[] { new Vector3(0f, .9f, 7f), 3f, candidates });
                Check("fresh_enabled_pending_native_receiver_blast_eligible_once", count == 1 &&
                    (Component)candidates.GetValue(0) == rival);
                ReceiverDiagnostic("fresh_enabled_same_tick", rival, receiver, player.transform, bodyRay);

                int idleFrame = Time.frameCount; float idleFixedTime = Time.fixedTime;
                double deadline = EditorApplication.timeSinceStartup + 8d;
                while ((Time.frameCount < idleFrame + 2 || Time.fixedTime <= idleFixedTime) &&
                       EditorApplication.timeSinceStartup < deadline) yield return null;
                Check("native_receiver_fixture_observed_normal_fixed_step", Time.frameCount >= idleFrame + 2 &&
                    Time.fixedTime > idleFixedTime);
                Check("unmoved_receiver_keeps_verified_pending_fallback", !ReadyReceiver(rival) &&
                    LogicalReceiver(rival) && SameReceiverHit(bodyRay, player.transform, rival, receiver));
                ReceiverDiagnostic("after_natural_fixed_step_before_rival_update", rival, receiver, player.transform, bodyRay);

                int moveFrame = Time.frameCount;
                float beforeSimulation = (float)Read(rival, "lastSimulationTime");
                Vector3 beforeMove = target.transform.position;
                ((Behaviour)rival).enabled = true;
                deadline = EditorApplication.timeSinceStartup + 8d;
                while ((Time.frameCount < moveFrame + 2 || !ReadyReceiver(rival) ||
                        (target.transform.position - beforeMove).sqrMagnitude <= 0.00000001f) &&
                       EditorApplication.timeSinceStartup < deadline) yield return null;
                ((Behaviour)rival).enabled = false;
                bodyRay = ReceiverBodyRay(rival);
                Check("ordinary_rival_update_performed_native_movement", Time.frameCount > moveFrame &&
                    (float)Read(rival, "lastSimulationTime") > beforeSimulation &&
                    (target.transform.position - beforeMove).sqrMagnitude > 0.00000001f);
                bool logicalEligible = LogicalReceiver(rival);
                bool unifiedFound = First(bodyRay, 30f, player.transform, out object hit);
                Collider unifiedCollider = (Collider)Read(hit, "collider");
                // Preserve the strict original assertion after actual native
                // admission. The QA never sets readiness true, calls Move,
                // invokes ConfirmNativeReceiverAfterMove, or simulates physics.
                Check("enabled_native_receiver_has_no_duplicate_logical_hit",
                    !logicalEligible && unifiedFound && unifiedCollider == receiver);
                Check("ready_native_receiver_is_directly_queryable", ReadyReceiver(rival) &&
                    receiver.Raycast(bodyRay, out RaycastHit direct, 30f) && direct.collider == receiver &&
                    Physics.Raycast(bodyRay, out RaycastHit raw, 30f, Physics.DefaultRaycastLayers,
                        QueryTriggerInteraction.Ignore) && raw.collider == receiver);
                count = (int)copy.Invoke(null, new object[] { target.transform.position + Vector3.up * .9f, 3f, candidates });
                Check("ready_native_receiver_excluded_from_logical_blast", count == 0);
                ReceiverDiagnostic("after_real_rival_update_move", rival, receiver, player.transform, bodyRay);

                enable.Invoke(rival, new object[] { false }); Physics.SyncTransforms();
                Check("disabled_native_receiver_uses_logical_fallback", !receiver.enabled && !ReadyReceiver(rival) &&
                    LogicalReceiver(rival) && SameReceiverHit(bodyRay, player.transform, rival, receiver));
                enable.Invoke(rival, new object[] { true }); Physics.SyncTransforms();
                Check("reenabled_native_receiver_requires_new_admission", receiver.enabled && !ReadyReceiver(rival) &&
                    LogicalReceiver(rival) && SameReceiverHit(bodyRay, player.transform, rival, receiver));
                ReceiverDiagnostic("reenabled_pending", rival, receiver, player.transform, bodyRay);
                moveFrame = Time.frameCount; beforeSimulation = (float)Read(rival, "lastSimulationTime");
                beforeMove = target.transform.position;
                ((Behaviour)rival).enabled = true;
                deadline = EditorApplication.timeSinceStartup + 8d;
                while ((Time.frameCount < moveFrame + 2 || !ReadyReceiver(rival) ||
                        (target.transform.position - beforeMove).sqrMagnitude <= 0.00000001f) &&
                       EditorApplication.timeSinceStartup < deadline) yield return null;
                ((Behaviour)rival).enabled = false;
                bodyRay = ReceiverBodyRay(rival);
                unifiedFound = First(bodyRay, 30f, player.transform, out hit);
                Check("reenabled_receiver_reverified_after_real_update", ReadyReceiver(rival) &&
                    !LogicalReceiver(rival) && (float)Read(rival, "lastSimulationTime") > beforeSimulation &&
                    (target.transform.position - beforeMove).sqrMagnitude > 0.00000001f &&
                    unifiedFound && (Collider)Read(hit, "collider") == receiver && Read(hit, "logicalRival") == null);
                ReceiverDiagnostic("reenabled_after_real_rival_update_move", rival, receiver, player.transform, bodyRay);
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static void IntegrationChecks()
        {
            var root = new GameObject("QA logical receiver integration");
            var directorObject = new GameObject("QA director"); directorObject.transform.SetParent(root.transform);
            var director = directorObject.AddComponent<AarisOfflineBRDirector>(); director.enabled = false;
            typeof(AarisOfflineBRDirector).GetField("activeDirector", Members).SetValue(null, director);
            var targetObject = new GameObject("QA logical rival"); targetObject.transform.SetParent(root.transform);
            targetObject.transform.position = new Vector3(0, 0, 10);
            var hp = targetObject.AddComponent<AarisOfflineHealth>(); hp.RestoreHealth(200f);
            Component rival = targetObject.AddComponent(rivalType); ((Behaviour)rival).enabled = false;
            Set(rival, "health", hp); Set(rival, "virtualBody", true);
            var visible = new GameObject("QA far silhouette marker"); visible.transform.SetParent(targetObject.transform, false);
            Set(rival, "farVisualRoot", visible);
            Array roster = (Array)Read(director, "rivals"); roster.SetValue(rival, 0);
            var shooter = new GameObject("QA shooter"); shooter.transform.SetParent(root.transform);
            var wallObject = new GameObject("QA cover"); wallObject.transform.SetParent(root.transform);
            var wall = wallObject.AddComponent<BoxCollider>(); wall.size = new Vector3(4, 4, 1);
            Ray bodyRay = new Ray(new Vector3(0, .9f, 0), Vector3.forward);
            try
            {
                wallObject.transform.position = new Vector3(0, .9f, 5);
                Physics.SyncTransforms();
                bool found = First(bodyRay, 20, shooter.transform, out object hit);
                Check("closer_wall_wins_before_logical_target", found && (Collider)Read(hit, "collider") == wall && Read(hit, "logicalRival") == null);
                targetObject.transform.position = Vector3.zero;
                wallObject.transform.position = new Vector3(0, .9f, .5f);
                Physics.SyncTransforms();
                if (wall.Raycast(bodyRay, out RaycastHit atOrigin, 20f) && atOrigin.distance == 0f)
                    Check("native_zero_distance_cover_wins_analytic_tie", First(bodyRay, 20, shooter.transform, out hit) &&
                        (Collider)Read(hit, "collider") == wall && Read(hit, "logicalRival") == null);
                else results.Add("SKIP native_zero_distance_cover_tie: PhysX treated the exact face origin as inside or returned positive distance; strict source postcompare still required");
                targetObject.transform.position = new Vector3(0, 0, 10);
                wallObject.transform.position = new Vector3(0, .9f, 15);
                Physics.SyncTransforms();
                found = First(bodyRay, 20, shooter.transform, out hit);
                Check("logical_target_wins_before_farther_wall", found && (Component)Read(hit, "logicalRival") == rival);
                wall.enabled = false;
                // Near physical admission is bounded. The frame before a
                // native receiver is available must not make the actor immune.
                Set(rival, "virtualBody", false);
                Check("pending_near_receiver_remains_hittable", First(bodyRay, 20, shooter.transform, out hit) &&
                    (Component)Read(hit, "logicalRival") == rival);
                Array pendingCandidates = Array.CreateInstance(rivalType, 100);
                int pendingCount = (int)typeof(AarisOfflineBRDirector).GetMethod("CopyLogicalBlastCandidates", Members)
                    .Invoke(null, new object[] { new Vector3(0, .9f, 7), 3f, pendingCandidates });
                Check("pending_near_receiver_blast_eligible", pendingCount == 1 &&
                    (Component)pendingCandidates.GetValue(0) == rival);
                // Native enable/disable admission is verified separately
                // across real PlayerLoop frames in ReceiverAdmissionChecks.
                Set(rival, "virtualBody", true);
                visible.SetActive(false);
                Physics.SyncTransforms();
                Check("offcamera_player_aim_rejected", !First(bodyRay, 20, shooter.transform, out _));
                Check("offcamera_ai_hits_preserved", First(bodyRay, 20, shooter.transform, out hit, false) && (Component)Read(hit, "logicalRival") == rival);
                visible.SetActive(true);
                Check("logical_shooter_cannot_self_hit", !First(bodyRay, 20, rival.transform, out _));
                targetObject.layer = 2;
                Check("ignore_raycast_layer_matches_native", !First(bodyRay, 20, shooter.transform, out _));
                targetObject.layer = 0;
                var ownerBlocker = new GameObject("QA own child collider"); ownerBlocker.transform.SetParent(shooter.transform);
                ownerBlocker.transform.position = new Vector3(0, .9f, 2);
                ownerBlocker.AddComponent<BoxCollider>(); Physics.SyncTransforms();
                Check("self_child_collider_does_not_block", First(bodyRay, 20, shooter.transform, out hit) && (Component)Read(hit, "logicalRival") == rival);
                Object.DestroyImmediate(ownerBlocker);

                MethodInfo damage = typeof(AarisEquippedGun).GetMethod("ApplyCombatDamage", Members);
                First(bodyRay, 20, shooter.transform, out hit);
                float before = hp.CurrentHealth;
                bool applied = (bool)damage.Invoke(null, new object[] { hit, 10f, null, 2f });
                Check("logical_body_damage_exact", applied && Mathf.Abs(before - hp.CurrentHealth - 10f) < .001f);
                hp.RestoreHealth(200f);
                Ray headRay = new Ray(new Vector3(0, 1.65f, 0), Vector3.forward);
                First(headRay, 20, shooter.transform, out hit);
                before = hp.CurrentHealth;
                applied = (bool)damage.Invoke(null, new object[] { hit, 10f, null, 2f });
                Check("logical_headshot_multiplier_exact", applied && Mathf.Abs(before - hp.CurrentHealth - 20f) < .001f);
                hp.RestoreHealth(200f); hp.TryEquipHelmet(); before = hp.CurrentHealth;
                applied = (bool)damage.Invoke(null, new object[] { hit, 10f, null, 2f });
                Check("logical_headshot_helmet_protection", applied && Mathf.Abs(before - hp.CurrentHealth - 13f) < .001f);

                // An unobstructed camera ray does not authorize a different,
                // blocked muzzle path. Exercise the same first-hit arbiter.
                wall.enabled = true; wall.size = new Vector3(.5f, 3f, .5f);
                wallObject.transform.position = new Vector3(1f, .9f, 1f);
                Vector3 muzzle = new Vector3(1f, .9f, 0);
                Physics.SyncTransforms();
                bool cameraClear = First(bodyRay, 20, shooter.transform, out hit) && (Component)Read(hit, "logicalRival") == rival;
                Vector3 muzzleDelta = new Vector3(0, .9f, 10) - muzzle;
                bool muzzleBlocked = First(new Ray(muzzle, muzzleDelta.normalized), muzzleDelta.magnitude + .1f,
                    shooter.transform, out hit) && (Collider)Read(hit, "collider") == wall;
                Check("camera_clear_muzzle_blocked", cameraClear && muzzleBlocked);

                wall.enabled = false; visible.SetActive(false); Physics.SyncTransforms();
                Array candidates = Array.CreateInstance(rivalType, 100);
                MethodInfo copy = typeof(AarisOfflineBRDirector).GetMethod("CopyLogicalBlastCandidates", Members);
                Vector3 explosion = new Vector3(0, .9f, 7);
                int count = (int)copy.Invoke(null, new object[] { explosion, 3f, candidates });
                Check("offcamera_logical_blast_candidate", count == 1 && (Component)candidates.GetValue(0) == rival);
                count = (int)copy.Invoke(null, new object[] { explosion, 2.6f, candidates });
                Check("blast_radius_uses_capsule_surface", count == 0);
                var grenadeObject = new GameObject("QA blast LOS"); grenadeObject.transform.SetParent(root.transform);
                var grenade = grenadeObject.AddComponent<AarisOfflineFragGrenade>(); grenade.enabled = false;
                MethodInfo los = typeof(AarisOfflineFragGrenade).GetMethod("HasBlastLineOfSight", Members);
                Vector3 destination = new Vector3(0, .9f, 9.69f);
                Check("logical_blast_open_los", (bool)los.Invoke(grenade, new object[] { explosion, destination, rival.transform }));
                wall.enabled = true; wall.size = new Vector3(4, 3, .5f); wallObject.transform.position = new Vector3(0, .9f, 8);
                Physics.SyncTransforms();
                Check("logical_blast_wall_blocks_los", !(bool)los.Invoke(grenade, new object[] { explosion, destination, rival.transform }));

                // Use the actual production scale adapter, not repeated math.
                targetObject.transform.localScale = new Vector3(3, .5f, 2);
                object[] capsule = { Vector3.zero, 0f, 0f };
                rivalType.GetMethod("GetLogicalCapsule", Members).Invoke(rival, capsule);
                Vector3 feet = (Vector3)capsule[0]; float h = (float)capsule[1], r = (float)capsule[2];
                Check("scaled_receiver_preserves_transformed_center", Mathf.Abs(r - .93f) < .001f &&
                    Mathf.Abs(h - 1.86f) < .001f && Mathf.Abs(feet.y + h * .5f - .45f) < .001f);
            }
            finally { Object.DestroyImmediate(root); }
        }
    }
}
#endif
