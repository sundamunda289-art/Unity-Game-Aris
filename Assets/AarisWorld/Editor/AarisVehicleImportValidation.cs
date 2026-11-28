using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Aaris.MapReview.Editor
{
    /// <summary>Real licensed Unity importer smoke-test, separate from Python QA.</summary>
    public static class AarisVehicleImportValidation
    {
        [MenuItem("Aaris/CI/Verify Vehicle Exit Collision")]
        public static void VerifyExitCollision()
        {
            const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
            var sessionType = typeof(AarisVehicleSession);
            var findExit = sessionType.GetMethod("TryFindExit", privateInstance);
            var fixture = new GameObject("Vehicle exit collision test");
            fixture.transform.position = Vector3.up * 500f;
            try
            {
                var car = new GameObject("Test car", typeof(AarisVehicleMotor));
                car.transform.SetParent(fixture.transform, false);
                car.transform.localPosition = Vector3.up * .71f;
                var body = car.AddComponent<BoxCollider>();
                body.center = new Vector3(0f, .5f, 0f);
                body.size = new Vector3(1.85f, 1f, 3.85f);

                var player = new GameObject("Test driver", typeof(CharacterController),
                    typeof(AarisVehicleSession));
                player.transform.SetParent(car.transform, false);
                var capsule = player.GetComponent<CharacterController>();
                capsule.height = 1.8f;
                capsule.radius = .3f;
                capsule.enabled = false;
                var session = player.GetComponent<AarisVehicleSession>();
                sessionType.GetField("car", privateInstance).SetValue(session,
                    car.GetComponent<AarisVehicleMotor>());
                sessionType.GetField("capsule", privateInstance).SetValue(session, capsule);

                BoxCollider AddObstacle(string name, Vector3 center, Vector3 size)
                {
                    var obstacle = new GameObject(name);
                    obstacle.transform.SetParent(fixture.transform, false);
                    obstacle.transform.localPosition = center;
                    var collider = obstacle.AddComponent<BoxCollider>();
                    collider.size = size;
                    return collider;
                }

                bool ResolveExit(out Vector3 position)
                {
                    Physics.SyncTransforms();
                    var args = new object[] { Vector3.zero };
                    bool found = (bool)findExit.Invoke(session, args);
                    position = (Vector3)args[0];
                    return found;
                }

                AddObstacle("Road", new Vector3(0f, -.25f, 0f), new Vector3(20f, .5f, 20f));
                if (!ResolveExit(out Vector3 exit) || exit.x < 3f)
                    throw new InvalidOperationException("Expected the unobstructed right-side exit.");

                var crossWall = new Vector3(.2f, 2.2f, 3f);
                AddObstacle("Right wall", new Vector3(2f, 1.1f, 0f), crossWall);
                if (!ResolveExit(out exit) || exit.x > -3f)
                    throw new InvalidOperationException("The right wall must force the left-side exit.");

                AddObstacle("Left wall", new Vector3(-2f, 1.1f, 0f), crossWall);
                var endWall = new Vector3(3f, 2.2f, .2f);
                AddObstacle("Rear wall", new Vector3(0f, 1.1f, -2.5f), endWall);
                AddObstacle("Front wall", new Vector3(0f, 1.1f, 2.5f), endWall);
                if (ResolveExit(out _))
                    throw new InvalidOperationException("A car enclosed by four walls must deny exit.");

                Debug.Log("AARIS_VEHICLE_EXIT_COLLISION_PASS: clear route, alternate side, all sides blocked.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(fixture);
            }
        }

        [MenuItem("Aaris/Validate 2km CC0 Vehicles")]
        public static void Run()
        {
            // Preserves the original 45 saved spawn-marker GameObjects,
            // four native TerrainData assets and the actual source scene.
            AarisExplorerBuild.ValidateSavedScene();
            string[] models =
            {
                "sedan", "suv", "sedan-sports",
                "hatchback-sports", "van", "truck-flat",
                "wheel-default", "wheel-racing", "wheel-truck"
            };
            foreach (string name in models)
            {
                string path = "Assets/Resources/AarisVehicles/Models/" + name + ".fbx";
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                    throw new InvalidOperationException("Unity cannot import vendored CC0 model: " + name);
                if (Resources.Load<GameObject>("AarisVehicles/Models/" + name) == null)
                    throw new InvalidOperationException("Resources API failed: " + name);
                var meshes = prefab.GetComponentsInChildren<MeshFilter>(true);
                var skins = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                int triangles = 0;
                foreach (var filter in meshes)
                    if (filter.sharedMesh != null) triangles += filter.sharedMesh.triangles.Length / 3;
                foreach (var skin in skins)
                    if (skin.sharedMesh != null) triangles += skin.sharedMesh.triangles.Length / 3;
                if (triangles < 10 || triangles > 100000)
                    throw new InvalidOperationException("Bad model mesh triangle count: " + name + "=" + triangles);
                Debug.Log("AARIS_VEHICLE_IMPORT " + name + " triangles=" + triangles);
            }
            string[] productionModels =
            {
                "PremiumRoadGT/AarisRoadGT", "Authored/1990s-suv",
                "Authored/AarisBuggy", "Authored/AarisBuggyWheel", "Authored/AarisRoadBike"
            };
            foreach (string model in productionModels)
            {
                GameObject imported = Resources.Load<GameObject>("AarisVehicles/" + model);
                if (imported == null ||
                    imported.GetComponentInChildren<MeshFilter>(true) == null ||
                    imported.GetComponentInChildren<Renderer>(true) == null)
                    throw new InvalidOperationException("Production vehicle art failed Unity import: " + model);
            }

            Texture2D palette = Resources.Load<Texture2D>("AarisVehicles/Models/colormap");
            if (palette == null || palette.width < 16 || palette.height < 16)
                throw new InvalidOperationException("Shared CC0 car palette texture missing.");
            if (Shader.Find("Universal Render Pipeline/Lit") == null)
                throw new InvalidOperationException("Missing URP Lit shader for car palette.");
            TextAsset points = Resources.Load<TextAsset>("AarisVehicles/vehicle_candidates");
            TextAsset catalog = Resources.Load<TextAsset>("AarisVehicles/vehicle_catalog");
            if (points == null || catalog == null ||
                !points.text.Contains("\"hubCount\": 45") ||
                !points.text.Contains("\"roadsideCount\": 33"))
                throw new InvalidOperationException("Authored 45+33 car locations not imported.");
            string[] scripts =
            {
                "AarisVehicleMotor", "AarisVehicleWorld",
                "AarisVehicleSession", "AarisVehicleHUD"
            };
            foreach (var name in scripts)
            {
                string path = "Assets/AarisWorld/Scripts/" + name + ".cs";
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script == null || script.GetClass() == null)
                    throw new InvalidOperationException("C# script compilation missing: " + path);
            }
            Debug.Log("AARIS_VEHICLE_UNITY_IMPORT_PASS: 6 real car and 3 wheel FBX models, palette, " +
                "4 scripts, 45 author markers + 33 roadside candidates.");
            Debug.LogWarning("VISUAL_QA_PENDING: visual wheel axis/offsets, road driving, " +
                "collision, camera, grip, terrain gradients and Android touch/FPS need Play mode.");
        }
    }
}
