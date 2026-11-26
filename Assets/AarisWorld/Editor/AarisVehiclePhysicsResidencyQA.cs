#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
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
    /// Real native Rigidbody/WheelCollider support in an isolated physics scene.
    /// Does not load/save authored assets or enter the Editor Play lifecycle.
    /// </summary>
    public static class AarisVehiclePhysicsResidencyQA
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly MethodInfo FixedCollider = typeof(AarisNearbyColliders)
            .GetMethod("FixedUpdate", Private);
        private static readonly MethodInfo FixedVehicle = typeof(AarisVehicleMotor)
            .GetMethod("FixedUpdate", Private);
        private static readonly MethodInfo Refresh = typeof(AarisNearbyColliders)
            .GetMethod("RefreshNow", Private, null, new[] { typeof(bool) }, null);
        private static void Set(object target, string field, object value) =>
            target.GetType().GetField(field, Private).SetValue(target, value);
        private static T Get<T>(object target, string field) =>
            (T)target.GetType().GetField(field, Private).GetValue(target);
        private static int Property(object target, string name) =>
            (int)target.GetType().GetProperty(name, Private).GetValue(target);
        private static int Residents => (int)typeof(AarisVehicleMotor).GetProperty(
            "TrackedPhysicsResidentCount", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        private static void Invoke(object target, string name) =>
            target.GetType().GetMethod(name, Private).Invoke(target, null);

        [MenuItem("Aaris/CI/Verify streamed vehicle support before physics")]
        public static void Run()
        {
            Scene scene = default(Scene);
            var report = new StringBuilder("UNITY=" + Application.unityVersion + "\n");
            int failures = 0, checks = 0;
            Action<bool, string> check = (passed, label) =>
            {
                checks++;
                report.AppendLine((passed ? "PASS " : "FAIL ") + label);
                if (!passed) failures++;
            };
            int initialResidents = Residents;
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                    throw new InvalidOperationException("EDITOR_BUSY");
                // SceneManager.CreateScene(LocalPhysicsMode) is Play-only.
                // Editor preview scenes own a separate native physics world;
                // verify that fact before simulating instead of ever advancing
                // the user's default/authored physics scene.
                scene = EditorSceneManager.NewPreviewScene();
                PhysicsScene physics = scene.GetPhysicsScene();
                bool isolated = physics.IsValid() && !physics.Equals(Physics.defaultPhysicsScene);
                check(isolated, "isolated-native-physics-scene");
                if (!isolated) throw new InvalidOperationException("PREVIEW_PHYSICS_NOT_ISOLATED");

                GameObject player = FixtureObject("Stationary observer", scene);
                player.transform.position = new Vector3(-300f, 20f, 0f);
                BoxCollider bridgeA = Floor("Elevated bridge A", 100f, true, scene);
                BoxCollider bridgeB = Floor("Elevated bridge B", 124f, true, scene);
                BoxCollider remoteFloor = Floor("Remote pool return floor", 250f, true, scene);
                BoxCollider authoredOff = Floor("Intentionally disabled floor", 100f, false, scene);
                Physics.SyncTransforms();
                check(physics.Raycast(new Vector3(100f, 25f, 0f), Vector3.down,
                        out RaycastHit floorHit, 10f) && floorHit.collider == bridgeA,
                    "elevated-floor-belongs-to-preview-physics-world");
                Collider[] obstacles = { bridgeA, bridgeB, remoteFloor, authoredOff };
                var indexObject = FixtureObject("Temporary collider index", scene);
                var index = indexObject.AddComponent<AarisNearbyColliders>();
                InitializeIndex(index, player.transform, obstacles);
                Refresh.Invoke(index, new object[] { false });
                int initialPins = Property(index, "VehicleSupportPinCount");

                var carObject = FixtureObject("Real native parked vehicle", scene);
                carObject.transform.position = new Vector3(100f, 20.72f, 0f);
                var car = carObject.AddComponent<AarisVehicleMotor>();
                car.Configure("QA", 20f, 1250f);
                Rigidbody body = carObject.GetComponent<Rigidbody>();
                WheelCollider[] wheels = carObject.GetComponentsInChildren<WheelCollider>();
                check(Residents == initialResidents + 1, "configured-rig-registers-once");
                Invoke(car, "OnEnable");
                check(Residents == initialResidents + 1, "repeated-enable-does-not-duplicate-registry");

                Refresh.Invoke(index, new object[] { false });
                Drain(index, "DeactivateDistantAcrossFrames");
                check(bridgeA.enabled, "startup-retains-far-car-bridge");
                check(!remoteFloor.enabled && !authoredOff.enabled,
                    "unrelated-and-authored-off-floors-stay-disabled");
                check(Property(index, "VehicleSupportPinCount") >= 1 &&
                    Property(index, "VehiclePinnedColliderCount") >= 1,
                    "small-vehicle-support-pin-counted");

                Physics.SyncTransforms();
                for (int i = 0; i < 200; i++)
                {
                    FixedCollider.Invoke(index, null);
                    FixedVehicle.Invoke(car, null);
                    physics.Simulate(.02f);
                }
                report.AppendLine("PARKED_POSITION=" + body.position.ToString("F3"));
                check(body.position.y > 20f && body.position.y < 22f,
                    "native-parked-car-stays-on-elevated-bridge");
                body.Sleep();
                int refreshes = Property(index, "VehicleSupportRefreshCount");
                for (int i = 0; i < 100; i++) FixedCollider.Invoke(index, null);
                check(Property(index, "VehicleSupportRefreshCount") == refreshes,
                    "sleeping-car-does-not-rescan-collider-grid");

                // A stale Android release queue must recheck vehicle ownership,
                // even when the player no longer owns this collider's wanted stamp.
                QueueStaleRelease(index, 0);
                Invoke(index, "FlushPendingColliderReleases");
                check(bridgeA.enabled, "forced-stale-release-keeps-support");
                QueueStaleRelease(index, 0);
                Drain(index, "ReleaseDistantAcrossFrames");
                check(bridgeA.enabled, "deferred-stale-release-keeps-support");

                // Model component suspension does not suspend native physics.
                car.enabled = false;
                Invoke(car, "OnDisable");
                Refresh.Invoke(index, new object[] { false });
                check(bridgeA.enabled && Residents == initialResidents + 1,
                    "component-only-disable-retains-live-rigidbody-support");
                car.enabled = true;
                Invoke(car, "OnEnable");

                // External pushes/rolling must acquire the next floor while the
                // observer remains fixed. No distance-based kinematic freeze.
                foreach (WheelCollider wheel in wheels) wheel.brakeTorque = 0f;
                body.WakeUp();
                float initialX = body.position.x;
                for (int i = 0; i < 100; i++)
                {
                    body.linearVelocity = new Vector3(8f, body.linearVelocity.y, 0f);
                    FixedCollider.Invoke(index, null);
                    physics.Simulate(.02f);
                }
                report.AppendLine("ROLLING_POSITION=" + body.position.ToString("F3"));
                check(body.position.x > initialX + 10f && body.position.y > 20f && bridgeB.enabled,
                    "rolling-car-acquires-next-floor-with-stationary-player");
                check(!body.isKinematic && Array.TrueForAll(wheels, w => w.enabled),
                    "moving-car-keeps-real-rigidbody-and-wheel-authority");

                body.position = new Vector3(250f, 30f, 0f);
                body.linearVelocity = new Vector3(0f, -3f, 0f);
                body.angularVelocity = Vector3.zero;
                body.rotation = Quaternion.identity;
                body.WakeUp();
                for (int i = 0; i < 150; i++)
                {
                    FixedCollider.Invoke(index, null);
                    FixedVehicle.Invoke(car, null);
                    physics.Simulate(.02f);
                }
                report.AppendLine("LANDED_POSITION=" + body.position.ToString("F3"));
                check(body.position.y > 20f && body.position.y < 22f && remoteFloor.enabled,
                    "airborne-car-acquires-floor-before-native-landing");

                // Deliberately keep the motor component disabled across the
                // pool cycle. Unity delivers neither a second OnDisable nor
                // OnEnable to it, while the native Rigidbody still resumes.
                car.enabled = false;
                Invoke(car, "OnDisable");
                carObject.SetActive(false);
                FixedCollider.Invoke(index, null);
                check(Residents == initialResidents + 1 && !remoteFloor.enabled &&
                    Property(index, "VehicleSupportPinCount") == initialPins,
                    "inactive-pool-retains-record-but-removes-physics-pins");
                carObject.transform.SetPositionAndRotation(new Vector3(100f, 20.72f, 0f),
                    Quaternion.identity);
                body.position = carObject.transform.position;
                body.rotation = Quaternion.identity;
                carObject.SetActive(true);
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                // Same ordering as runtime: collider FixedUpdate precedes the
                // very first resumed vehicle FixedUpdate and PhysX simulation.
                FixedCollider.Invoke(index, null);
                check(bridgeA.enabled && !remoteFloor.enabled && !car.enabled,
                    "disabled-motor-pool-resume-acquires-floor-without-OnEnable");
                physics.Simulate(.02f);
                check(body.position.y > 20f, "reacquired-car-does-not-fall-through-floor");
                Object.DestroyImmediate(carObject);
                FixedCollider.Invoke(index, null);
                check(Residents == initialResidents,
                    "destroyed-rig-is-removed-from-registry");
            }
            catch (Exception ex)
            {
                failures++;
                report.AppendLine("FAIL QA_EXCEPTION " + ex);
            }
            finally
            {
                if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
            }
            report.AppendLine("CHECKS=" + checks);
            report.AppendLine("RESULT=" + (failures == 0 ? "PASS" : "FAIL"));
            string folder = Path.Combine(Path.GetTempPath(), "aaris-vehicle-physics-residency-qa");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "report.txt"), report.ToString());
            Debug.Log(report.ToString());
            if (Application.isBatchMode) EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        private static GameObject FixtureObject(string name, Scene scene)
        {
            var obj = new GameObject(name);
            SceneManager.MoveGameObjectToScene(obj, scene);
            return obj;
        }

        private static BoxCollider Floor(string name, float x, bool active, Scene scene)
        {
            var obj = FixtureObject(name, scene);
            obj.transform.position = new Vector3(x, 19f, 0f);
            var collider = obj.AddComponent<BoxCollider>();
            collider.size = new Vector3(24f, 2f, 14f);
            collider.enabled = active;
            return collider;
        }

        private static void InitializeIndex(AarisNearbyColliders index, Transform target,
            Collider[] obstacles)
        {
            int count = obstacles.Length;
            Set(index, "target", target);
            Set(index, "obstacles", obstacles);
            Set(index, "worldBounds", new Bounds[count]);
            Set(index, "localBoxBounds", new Bounds[count]);
            Set(index, "worldToBox", new Matrix4x4[count]);
            Set(index, "streamed", new bool[count]);
            Set(index, "disabledByStreaming", new bool[count]);
            Set(index, "visited", new int[count]);
            Set(index, "wanted", new int[count]);
            Set(index, "probeVisited", new int[count]);
            Set(index, "colliderReleaseQueued", new bool[count]);
            Set(index, "active", new List<int>(count));
            Set(index, "nextActive", new List<int>(count));
            MethodInfo add = typeof(AarisNearbyColliders).GetMethod("IndexCollider", Private);
            for (int i = 0; i < count; i++) add.Invoke(index, new object[] { i });
            Set(index, "indexReady", true);
        }

        private static void QueueStaleRelease(AarisNearbyColliders index, int collider)
        {
            Get<int[]>(index, "wanted")[collider] = -1;
            Get<bool[]>(index, "colliderReleaseQueued")[collider] = true;
            Get<List<int>>(index, "pendingColliderRelease").Add(collider);
        }

        private static void Drain(AarisNearbyColliders index, string name)
        {
            var operation = (IEnumerator)typeof(AarisNearbyColliders).GetMethod(name, Private)
                .Invoke(index, null);
            while (operation.MoveNext()) { }
        }
    }
}
#endif
