using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Aaris.MapReview
{
    [Serializable]
    internal sealed class AarisExpansionPlan
    {
        public int schemaVersion;
        public int mapSizeMetres;
        public string targetGameScene;
        public int siteCount;
        public AarisExpansionSite[] sites;
    }

    [Serializable]
    internal sealed class AarisExpansionSite
    {
        public string name;
        public string district;
        public string prefabKey;
        public float x, z, groundReferenceY, yaw;
        public float width, depth, height;
    }

    /// <summary>
    /// Offline, repeatable map expansion. Existing authored 2 km x 2 km terrain,
    /// buildings, streets, vegetation, 45 car bays, LFS assets, and modified
    /// working files are not altered. New objects exist in the running game.
    /// Skips unsafe sites rather than sculpting or overwriting source TerrainData.
    /// </summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(-80)]
    public sealed class AarisSettlementExpansion : MonoBehaviour
    {
        private const string Scene = "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";
        private const float SeaLevel = -38.07552f;
        private const float MaxGroundDeviation = 1.25f;
        private const float MaxFoundationSlope = 0.55f;
        private const float MaxSlopeDegrees = 10f;
        private const float VegetationMargin = 2.0f;
        private const float ObstacleMargin = 1.25f;

        private readonly Collider[] obstacles = new Collider[96];
        private readonly Dictionary<Terrain, TreeInstance[]> treeCache =
            new Dictionary<Terrain, TreeInstance[]>();
        private readonly List<GameObject> placed = new List<GameObject>(24);
        private int skippedTerrain, skippedTrees, skippedObstacle, skippedResources;
        private static bool spawnPending;

        public int Placed => placed.Count;
        public int SkippedTerrain => skippedTerrain;
        public int SkippedTrees => skippedTrees;
        public int SkippedObstacle => skippedObstacle;
        public int SkippedResources => skippedResources;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (SceneManager.GetActiveScene().path != Scene ||
                FindFirstObjectByType<AarisSettlementExpansion>() != null ||
                spawnPending)
                return;
            spawnPending = true;
            new GameObject("__AarisAdditionalSettlements").AddComponent<AarisSettlementExpansion>();
        }

        private void Start()
        {
            spawnPending = false;
            var planAsset = Resources.Load<TextAsset>("AarisExpansion/WorldExpansionPlan");
            var catalog = Resources.Load<AarisExpansionCatalog>("AarisExpansion/AarisExpansionCatalog");
            if (planAsset == null || catalog == null)
            {
                Debug.LogError("AARIS_EXPANSION_LOCAL_CATALOG_MISSING");
                enabled = false;
                return;
            }

            AarisExpansionPlan plan;
            try { plan = JsonUtility.FromJson<AarisExpansionPlan>(planAsset.text); }
            catch (Exception)
            {
                Debug.LogError("AARIS_EXPANSION_INVALID_PLAN_JSON");
                enabled = false;
                return;
            }
            if (plan == null || plan.schemaVersion != 1 || plan.mapSizeMetres != 2000 ||
                plan.targetGameScene != Scene || plan.sites == null ||
                plan.sites.Length != plan.siteCount || plan.sites.Length > 32)
            {
                Debug.LogError("AARIS_EXPANSION_SCHEMA_OR_WORLD_MISMATCH");
                enabled = false;
                return;
            }

            var terrains = Terrain.activeTerrains;
            if (terrains == null || terrains.Length != 4)
            {
                Debug.LogWarning("AARIS_EXPANSION_FOUR_TERRAIN_TILES_REQUIRED");
                enabled = false;
                return;
            }
            foreach (Terrain t in terrains)
            {
                if (t != null && t.terrainData != null)
                    treeCache[t] = t.terrainData.treeInstances;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var site in plan.sites)
            {
                if (site == null || string.IsNullOrWhiteSpace(site.name) ||
                    !names.Add(site.name) || !CoordinatesValid(site))
                {
                    skippedResources++;
                    continue;
                }
                GameObject prefab = catalog.Resolve(site.prefabKey);
                if (prefab == null)
                {
                    skippedResources++;
                    continue;
                }
                if (!TryGround(site, terrains, out Terrain support, out float y))
                {
                    skippedTerrain++;
                    continue;
                }
                if (TreesBlock(site, terrains))
                {
                    skippedTrees++;
                    continue;
                }
                if (CollidersBlock(site, y))
                {
                    skippedObstacle++;
                    continue;
                }

                Vector3 position = new Vector3(site.x, y - 0.025f, site.z);
                GameObject instance = Instantiate(prefab, position,
                    Quaternion.Euler(0f, site.yaw, 0f), transform);
                instance.name = site.name;
                placed.Add(instance);
            }
            treeCache.Clear();
            Debug.Log("AARIS_EXPANSION_RESULT placed=" + placed.Count +
                " planned=" + plan.siteCount + " terrain_skips=" + skippedTerrain +
                " tree_skips=" + skippedTrees + " obstacle_skips=" + skippedObstacle +
                " asset_skips=" + skippedResources);
        }

        private static bool CoordinatesValid(AarisExpansionSite site)
        {
            return !(float.IsNaN(site.x) || float.IsNaN(site.z) ||
                float.IsInfinity(site.x) || float.IsInfinity(site.z) ||
                float.IsNaN(site.yaw) || float.IsInfinity(site.yaw) ||
                float.IsNaN(site.groundReferenceY) || float.IsInfinity(site.groundReferenceY) ||
                Mathf.Abs(site.x) > 965f || Mathf.Abs(site.z) > 965f ||
                float.IsNaN(site.width) || float.IsNaN(site.depth) || float.IsNaN(site.height) ||
                float.IsInfinity(site.width) || float.IsInfinity(site.depth) ||
                float.IsInfinity(site.height) ||
                site.width < 2f || site.depth < 2f || site.height < 2f ||
                site.width > 50f || site.depth > 50f || site.height > 50f);
        }

        // Conservative projected extents of the rotated footprint, shared by
        // foundation samples and native-tree clearance. This handles 90-degree
        // watchtowers and arbitrary future yaw values on multi-tile seams.
        private static void FootprintHalfExtents(AarisExpansionSite site, float margin,
            out float halfX, out float halfZ)
        {
            float yaw = site.yaw * Mathf.Deg2Rad;
            float cos = Mathf.Abs(Mathf.Cos(yaw));
            float sin = Mathf.Abs(Mathf.Sin(yaw));
            halfX = (site.width * cos + site.depth * sin) * 0.5f + margin;
            halfZ = (site.width * sin + site.depth * cos) * 0.5f + margin;
        }

        private static bool TryTerrainAt(float x, float z, Terrain[] all,
            out Terrain terrain, out float height)
        {
            foreach (Terrain t in all)
            {
                if (t == null || t.terrainData == null) continue;
                Vector3 origin = t.transform.position, size = t.terrainData.size;
                if (x < origin.x || x > origin.x + size.x ||
                    z < origin.z || z > origin.z + size.z) continue;
                terrain = t;
                // Terrain.SampleHeight uses world XZ coordinates but its origin
                // treatment can be misread. Explicitly sample TerrainData in
                // normalized tile coordinates and add its transform Y once.
                height = origin.y + t.terrainData.GetInterpolatedHeight(
                    (x - origin.x) / size.x, (z - origin.z) / size.z);
                return true;
            }
            terrain = null;
            height = 0f;
            return false;
        }

        private static bool TryGround(AarisExpansionSite site, Terrain[] all,
            out Terrain support, out float elevation)
        {
            support = null;
            elevation = 0f;
            if (!TryTerrainAt(site.x, site.z, all, out support, out elevation) ||
                elevation <= SeaLevel + 2f ||
                Mathf.Abs(elevation - site.groundReferenceY) > MaxGroundDeviation)
                return false;

            Vector3 tpos = support.transform.position;
            Vector3 size = support.terrainData.size;
            float nx = (site.x - tpos.x) / size.x;
            float nz = (site.z - tpos.z) / size.z;
            if (support.terrainData.GetSteepness(nx, nz) > MaxSlopeDegrees)
                return false;

            FootprintHalfExtents(site, ObstacleMargin, out float halfX, out float halfZ);
            for (int xi = -1; xi <= 1; xi++)
                for (int zi = -1; zi <= 1; zi++)
                {
                    if (!TryTerrainAt(site.x + xi * halfX,
                        site.z + zi * halfZ, all, out _, out float testY) ||
                        Mathf.Abs(testY - elevation) > MaxFoundationSlope)
                        return false;
                }
            return true;
        }

        private bool TreesBlock(AarisExpansionSite site, Terrain[] all)
        {
            // A footprint on the seam between native tiles must check trees
            // from BOTH tiles, not only the tile containing its centre.
            FootprintHalfExtents(site, VegetationMargin, out float hx, out float hz);
            foreach (Terrain terrain in all)
            {
                if (terrain == null || terrain.terrainData == null ||
                    !treeCache.TryGetValue(terrain, out TreeInstance[] trees))
                    continue;
                Vector3 basePoint = terrain.transform.position;
                Vector3 tileSize = terrain.terrainData.size;
                if (site.x + hx + 3.8f < basePoint.x ||
                    site.x - hx - 3.8f > basePoint.x + tileSize.x ||
                    site.z + hz + 3.8f < basePoint.z ||
                    site.z - hz - 3.8f > basePoint.z + tileSize.z)
                    continue;
                for (int i = 0; i < trees.Length; i++)
                {
                    TreeInstance tree = trees[i];
                    float x = basePoint.x + tree.position.x * tileSize.x;
                    float z = basePoint.z + tree.position.z * tileSize.z;
                    float margin = Mathf.Min(3.8f, Mathf.Max(0.5f, tree.widthScale * 1.5f));
                    if (Mathf.Abs(x - site.x) < hx + margin &&
                        Mathf.Abs(z - site.z) < hz + margin)
                        return true;
                }
            }
            return false;
        }

        private bool CollidersBlock(AarisExpansionSite site, float y)
        {
            Quaternion rotation = Quaternion.Euler(0f, site.yaw, 0f);
            Vector3 half = new Vector3(site.width * 0.5f + ObstacleMargin,
                site.height * 0.5f + 0.3f,
                site.depth * 0.5f + ObstacleMargin);
            Vector3 center = new Vector3(site.x, y + half.y + 0.05f, site.z);
            int n = Physics.OverlapBoxNonAlloc(center, half, obstacles,
                rotation, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (n >= obstacles.Length) return true;
            for (int i = 0; i < n; i++)
            {
                Collider collider = obstacles[i];
                if (collider == null || collider is TerrainCollider) continue;
                // A low foundation/floor surface does not obstruct an upright house;
                // walls, vehicles, rocks, existing structures and NPCs do.
                if (collider.bounds.max.y < y + 0.1f) continue;
                return true;
            }
            return false;
        }
    }
}
