#if UNITY_EDITOR
// PR #75: read-only native Unity physics preflight; never edits or saves a scene.
// Run from a fully imported, verified matching scene, not from a historic copy.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Aaris.WorldAutomation
{
    public static class AarisResidentialRoutePhysicsAudit
    {
        private const string ScenePath =
            "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";
        private const float Width = 2.2f;     // Conservative EXAMPLE, not an actual car prefab.
        private const float Length = 5.0f;
        private const float Height = 1.8f;
        private const float EdgeMargin = 0.5f;
        private const float GroundClearance = 0.15f;
        private const float Step = 0.8f;
        private const float MinRoadLink = 5.0f;

        [Serializable] private sealed class Layout
        {
            public Spawn[] vehicle_spawns;
        }

        [Serializable] private sealed class Spawn
        {
            public string name;
            public float[] position_m;
            public float[] exit_world_xz_m;
        }

        [Serializable] private sealed class Manifest
        {
            public Row[] rows;
        }

        [Serializable] private sealed class Row
        {
            public string spawn_name;
            public float[] new_spawn_position_m;
        }

        [Serializable] private sealed class Blocker
        {
            public string route;
            public string collider;
            public float along_route_m;
        }

        [Serializable] private sealed class Report
        {
            public string status;
            public string scene;
            public string unity_version;
            public int authored_markers;
            public int checked_garage_routes;
            public int checked_samples;
            public int nonterrain_collider_hits;
            public float largest_terrain_offset_m;
            public Blocker[] first_blockers;
            public string limitations;
        }

        private static float DistanceXZ(Vector3 a, Vector3 b)
        {
            return Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
        }

        private static string FullName(Transform tr)
        {
            var parts = new List<string>();
            for (Transform cur = tr; cur != null; cur = cur.parent)
                parts.Add(cur.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static Terrain FindTerrain(float x, float z)
        {
            foreach (var terrain in Terrain.activeTerrains)
            {
                if (terrain == null || terrain.terrainData == null)
                    continue;
                var origin = terrain.transform.position;
                var size = terrain.terrainData.size;
                if (x >= origin.x - 0.0001f && x <= origin.x + size.x + 0.0001f &&
                    z >= origin.z - 0.0001f && z <= origin.z + size.z + 0.0001f)
                    return terrain;
            }
            return null;
        }

        private static float TerrainY(Terrain t, float x, float z)
        {
            // TerrainData interpolation is explicitly local to its heightmap.
            // Add the terrain's world origin exactly once to avoid ambiguity
            // around the Terrain.SampleHeight coordinate/return contract.
            var origin = t.transform.position;
            var size = t.terrainData.size;
            float u = Mathf.Clamp01((x - origin.x) / size.x);
            float v = Mathf.Clamp01((z - origin.z) / size.z);
            return origin.y + t.terrainData.GetInterpolatedHeight(u, v);
        }

        [MenuItem("AarisEngine/QA/Read-Only Garage Physics Probe")]
        public static void Run()
        {
            if (SceneManager.GetActiveScene().path != ScenePath)
                throw new InvalidOperationException(
                    "Open the exact approved TerrainDemoScene first; no scene auto-open or save.");
            if (Terrain.activeTerrains == null || Terrain.activeTerrains.Length != 4)
                throw new InvalidOperationException("Four native terrain tiles must be imported");
            var root = Directory.GetParent(Application.dataPath).FullName;
            var layoutPath = Path.Combine(root, "Docs/MAP_LAYOUT.json");
            var manifestPath = Path.Combine(root,
                "Tools/World/reviewed-residential-vehicle-spawns-20261105.json");
            if (!File.Exists(layoutPath) || !File.Exists(manifestPath))
                throw new InvalidOperationException("Approved source layout or 17-route manifest missing");

            var layout = JsonUtility.FromJson<Layout>(File.ReadAllText(layoutPath));
            var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(manifestPath));
            if (layout == null || layout.vehicle_spawns == null ||
                layout.vehicle_spawns.Length != 45 || manifest == null ||
                manifest.rows == null || manifest.rows.Length != 17)
                throw new InvalidOperationException("Unreviewed vehicle marker/manifest counts");

            var markers = UnityEngine.Object.FindObjectsByType<GameObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(g => g != null && g.scene.IsValid() &&
                       g.scene.path == ScenePath &&
                       System.Text.RegularExpressions.Regex.IsMatch(
                           g.name, @"^VehicleSpawnPoint_\d{2}$"))
                .GroupBy(g => g.name).ToDictionary(g => g.Key, g => g.ToArray());
            if (markers.Count != 45 || markers.Values.Any(a => a.Length != 1))
                throw new InvalidOperationException("Scene must contain 45 unique vehicle markers");

            var byName = layout.vehicle_spawns.ToDictionary(s => s.name, s => s);
            var seen = new HashSet<string>();
            foreach (var row in manifest.rows)
            {
                if (row == null || !seen.Add(row.spawn_name) ||
                    !byName.ContainsKey(row.spawn_name) ||
                    row.new_spawn_position_m == null ||
                    row.new_spawn_position_m.Length != 3)
                    throw new InvalidOperationException("Invalid garage review manifest");
                var position = markers[row.spawn_name][0].transform.position;
                var expected = row.new_spawn_position_m;
                if (Vector3.Distance(position,
                        new Vector3(expected[0],expected[1],expected[2])) > 0.025f)
                    throw new InvalidOperationException(
                        "Native scene marker differs from saved PR #75: " + row.spawn_name);
            }

            Physics.SyncTransforms();  // no simulation, no scene mutation
            var hits = new List<Blocker>();
            int samples = 0;
            float biggestYError = 0f;
            var results = new Collider[256];
            foreach (var row in manifest.rows)
            {
                var spot = byName[row.spawn_name];
                if (spot.exit_world_xz_m == null || spot.exit_world_xz_m.Length != 2)
                    throw new InvalidOperationException("Missing road exit for " + row.spawn_name);
                var marker = markers[row.spawn_name][0];
                var start = marker.transform.position;
                var finish = new Vector3(spot.exit_world_xz_m[0], start.y,
                                         spot.exit_world_xz_m[1]);
                float distance = DistanceXZ(start,finish);
                if (distance < MinRoadLink)
                    throw new InvalidOperationException("Garage road exit too near: " + row.spawn_name);
                Vector3 direction = (finish-start).normalized;
                if (Mathf.Abs(direction.x) > 0.0001f &&
                    Mathf.Abs(direction.z) > 0.0001f)
                    throw new InvalidOperationException("Only straight axis-aligned routes are reviewed");
                var yaw = Quaternion.LookRotation(direction,Vector3.up);
                var half = new Vector3(Width/2f+EdgeMargin,Height/2f,
                                       Length/2f+EdgeMargin);
                int count = Mathf.CeilToInt(distance/Step);
                for (int i = 0; i <= count; ++i)
                {
                    var at = Vector3.Lerp(start,finish,i/(float)count);
                    var terrain = FindTerrain(at.x,at.z);
                    if (terrain == null)
                        throw new InvalidOperationException("Missing actual TerrainData at garage route");
                    float ground = TerrainY(terrain,at.x,at.z);
                    biggestYError = Mathf.Max(biggestYError,Mathf.Abs(ground-start.y));
                    var center = new Vector3(at.x,ground+GroundClearance+Height/2f,at.z);
                    Array.Clear(results,0,results.Length);
                    int found = Physics.OverlapBoxNonAlloc(center,half,results,yaw,
                                                           ~0,QueryTriggerInteraction.Ignore);
                    if (found >= results.Length)
                        throw new InvalidOperationException("Collider buffer truncated: fail closed");
                    ++samples;
                    for(int n=0;n<found;n++)
                    {
                        var collider = results[n];
                        if (collider == null || collider is TerrainCollider ||
                            collider.transform.IsChildOf(marker.transform))
                            continue;
                        if (hits.Count < 200)
                            hits.Add(new Blocker {
                                route=row.spawn_name,
                                collider=FullName(collider.transform),
                                along_route_m=distance*i/count
                            });
                    }
                }
            }

            var report = new Report {
                status=hits.Count == 0 && biggestYError <= 0.4f
                    ? "editor_static_route_box_probe_clear"
                    : "editor_static_route_box_probe_requires_review",
                scene=ScenePath,
                unity_version=Application.unityVersion,
                authored_markers=markers.Count,
                checked_garage_routes=manifest.rows.Length,
                checked_samples=samples,
                nonterrain_collider_hits=hits.Count,
                largest_terrain_offset_m=biggestYError,
                first_blockers=hits.Take(40).ToArray(),
                limitations="Example 2.2x5x1.8m vehicle plus 0.5m lateral/length safety edge. " +
                    "Static query of enabled scene Colliders only, without car prefab, wheel " +
                    "suspension, steering sweep, dynamic Rigidbody, terrain tree-collider guarantees, " +
                    "runtime navigation, or Android frame-time tests."
            };
            Debug.Log("AARIS_EDITOR_GARAGE_ROUTE_PHYSICS_AUDIT=" +
                      JsonUtility.ToJson(report));
            if (report.status != "editor_static_route_box_probe_clear")
                throw new InvalidOperationException(
                    "Real Unity collider/ground contact needs review; see audit JSON in console");
        }
    }
}
#endif
