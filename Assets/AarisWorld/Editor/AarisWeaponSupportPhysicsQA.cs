#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Native PhysX regression for terrain grass, a raised road collider and an
    /// indoor floor. Runs only in a temporary unsaved Editor scene:
    /// -batchmode -nographics -executeMethod
    /// Aaris.MapReview.Editor.AarisWeaponSupportPhysicsQA.Run
    /// Writes /tmp/aaris-weapon-floor-native/report.txt; never mutates game assets.
    /// </summary>
    public static class AarisWeaponSupportPhysicsQA
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private static void Require(bool ok, string reason)
        {
            if (!ok) throw new InvalidOperationException(reason);
        }

        private static object Candidate(string kind, float y)
        {
            var type = typeof(AarisWeaponLootWorld).Assembly.GetType(
                "Aaris.MapReview.AarisLootCandidate", true);
            object candidate = Activator.CreateInstance(type, true);
            type.GetField("kind").SetValue(candidate, kind);
            type.GetField("x").SetValue(candidate, 0f);
            type.GetField("y").SetValue(candidate, y);
            type.GetField("z").SetValue(candidate, 0f);
            return candidate;
        }

        private static Vector3 Resolve(AarisWeaponLootWorld world, Terrain terrain, object point)
        {
            MethodInfo method = typeof(AarisWeaponLootWorld).GetMethod("TryResolve", Private);
            Require(method != null, "TRY_RESOLVE_NOT_FOUND");
            object[] args = { point, new Terrain[] { terrain }, Vector3.zero, 0f, 0f, false };
            bool found = (bool)method.Invoke(world, args);
            Require(found, "NO_USABLE_SUPPORT");
            return (Vector3)args[2];
        }

        public static void Run()
        {
            var folder = Path.Combine(Path.GetTempPath(), "aaris-weapon-floor-native");
            int exit = 1;
            string report = "UNITY=" + Application.unityVersion + "\n";
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var data = new TerrainData
                {
                    heightmapResolution = 33,
                    size = new Vector3(64f, 10f, 64f)
                };
                GameObject ground = Terrain.CreateTerrainGameObject(data);
                ground.name = "Native flat terrain";
                ground.transform.position = new Vector3(-32f, 0f, -32f);
                Terrain terrain = ground.GetComponent<Terrain>();
                var lootWorld = new GameObject("Native QA Loot")
                    .AddComponent<AarisWeaponLootWorld>();
                Physics.SyncTransforms();

                Vector3 grass = Resolve(lootWorld, terrain, Candidate("open", 0f));
                report += "GRASS_ROOT_Y=" + grass.y.ToString("F3") + "\n";
                Require(Mathf.Abs(grass.y - 0.28f) < 0.025f,
                    "GRASS_PRESENTATION_LIFT_CHANGED");

                // The hard road's top is +0.13m above the underlying terrain.
                // Terrain and road are BOTH hit by the unsorted downward ray.
                var road = new GameObject("Native paved road");
                road.transform.position = new Vector3(0f, 0.08f, 0f);
                road.AddComponent<BoxCollider>().size = new Vector3(3f, 0.10f, 3f);
                Physics.SyncTransforms();
                Vector3 paved = Resolve(lootWorld, terrain, Candidate("open", 0f));
                report += "ROAD_ROOT_Y=" + paved.y.ToString("F3") + "\n";
                Require(Mathf.Abs(paved.y - 0.17f) < 0.035f,
                    "ROAD_COLLIDER_LOST_TO_UNDERLYING_TERRAIN");

                // Indoors, the authored floor height is primary. A ceiling
                // must be genuinely present; the same 4cm root contact applies.
                var roof = new GameObject("Native interior ceiling");
                roof.transform.position = new Vector3(0f, 2.60f, 0f);
                roof.AddComponent<BoxCollider>().size = new Vector3(3f, 0.10f, 3f);
                Physics.SyncTransforms();
                Vector3 indoors = Resolve(lootWorld, terrain, Candidate("indoor", .13f));
                report += "INDOOR_ROOT_Y=" + indoors.y.ToString("F3") + "\n";
                Require(Mathf.Abs(indoors.y - .17f) < 0.035f,
                    "INDOOR_SUPPORT_CLEARANCE_CHANGED");
                report += "RESULT=PASS\n";
                exit = 0;
            }
            catch (Exception ex)
            {
                report += "RESULT=FAIL\n" + ex.GetType().Name + ": " + ex.Message + "\n";
            }
            finally
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "report.txt"), report);
                Debug.Log("AARIS_NATIVE_FLOOR_QA=" + (exit == 0 ? "PASS" : "FAIL"));
                EditorApplication.Exit(exit);
            }
        }
    }
}
#endif
