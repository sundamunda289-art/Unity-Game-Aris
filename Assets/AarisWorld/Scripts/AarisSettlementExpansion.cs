using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Profiling;
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
        public bool interactive;
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
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static readonly ProfilerMarker k_TreeClearance =
            new ProfilerMarker("Aaris.Startup.TreeClearance");
#endif
        private const string Scene = "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";
        private const float SeaLevel = -38.07552f;
        private const float MaxGroundDeviation = 1.25f;
        private const float MaxFoundationSlope = 0.55f;
        private const float MaxSlopeDegrees = 10f;
        private const float VegetationMargin = 2.0f;
        private const float ObstacleMargin = 1.25f;

        private readonly Collider[] obstacles = new Collider[96];
        private const int AndroidTreeProbeBudgetPerFrame = 256;
        private const double AndroidTreeProbeBudgetSeconds = 0.001;
        private readonly Dictionary<Terrain, AarisTreeClearanceIndex> treeCache =
            new Dictionary<Terrain, AarisTreeClearanceIndex>();
        // Only clearance records near a planned site (including every possible
        // 26m interactive relocation) can influence settlement placement.
        // The actual native terrain trees and scene visuals remain untouched.
        private const float TreeInterestCellMetres = 32f;
        private const float MaximumTreeClearanceMargin = 3.8f;
        private const float MaximumInteractiveRelocationAxis = 26f;
        private readonly HashSet<long> treeInterestCells = new HashSet<long>();
        private readonly List<GameObject> placed = new List<GameObject>(24);
        // The physics engine cannot query distant authored colliders while
        // the nearby streamer has disabled them. Keep its cached bounds.
        private AarisNearbyColliders nearbyColliderBounds;
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
#if UNITY_ANDROID && !UNITY_EDITOR
            // Keep the expansion catalog and its referenced prefab imports
            // away from the initial terrain/player frame on budget Android.
            StartCoroutine(LoadExpansionCatalogAcrossFrames());
#else
            InitializeExpansion(
                Resources.Load<TextAsset>("AarisExpansion/WorldExpansionPlan"),
                Resources.Load<AarisExpansionCatalog>("AarisExpansion/AarisExpansionCatalog"));
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private IEnumerator LoadExpansionCatalogAcrossFrames()
        {
            yield return null;
            ResourceRequest plan = Resources.LoadAsync<TextAsset>(
                "AarisExpansion/WorldExpansionPlan");
            plan.priority = -1;
            yield return plan;
            if (!isActiveAndEnabled) yield break;
            // Never integrate the two different Resources objects in one
            // scripted frame. Existing tree/site budgets still apply later.
            yield return null;
            // The catalog directly references every settlement prefab.
            // Keep its optional native imports behind the same low-memory
            // admission used by cars, rivals and other visual streams.
            // The tiny world-plan JSON remains on its original async path.
            ResourceRequest catalog = null;
            while (isActiveAndEnabled && catalog == null)
            {
                catalog = AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<AarisExpansionCatalog>(
                    "AarisExpansion/AarisExpansionCatalog");
                if (catalog == null) yield return null;
            }
            if (!isActiveAndEnabled) yield break;
            yield return catalog;
            if (!isActiveAndEnabled) yield break;
            InitializeExpansion(plan.asset as TextAsset,
                catalog.asset as AarisExpansionCatalog);
        }
#endif

        private void InitializeExpansion(TextAsset planAsset, AarisExpansionCatalog catalog)
        {
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
            // The streamer's Awake runs at order -250 before this Start.
            // Inspect its already cached bounds without re-enabling physics.
            nearbyColliderBounds = FindFirstObjectByType<AarisNearbyColliders>();

            var player = FindFirstObjectByType<AarisThirdPersonPlayer>();
            Transform interactionTarget = player != null ? player.transform : null;
            // Sites require multiple terrain/tree/physics probes and some expensive
            // prefab imports. Doing all 21 in one Start() causes a startup spike
            // on budget Android CPUs. Preserve placement/order, but spread the
            // work over frames without touching authored terrain or scene assets.
            StartCoroutine(PlaceSitesAcrossFrames(plan, catalog, terrains, interactionTarget));
        }

        private IEnumerator PlaceSitesAcrossFrames(
            AarisExpansionPlan plan, AarisExpansionCatalog catalog,
            Terrain[] terrains, Transform interactionTarget)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            int examinedThisFrame = 0;
            // The mobile cold-start is already creating the player, URP state,
            // terrain renderer and physics streamer. Avoid adding landmark
            // prefab instantiation to the same opening frame. No houses are
            // removed: Android places one attempted site per subsequent frame.
            bool mobile = Application.platform == RuntimePlatform.Android;
            int attemptsPerFrame = mobile ? 1 : 2;
            try
            {
                // Even if the match exits during the opening-frame delay, the
                // iterator's finally releases its temporary tree cache.
                if (mobile) yield return null;
                // TerrainData.treeInstances copies an entire native tree array to
                // managed memory at once. On Android, read bounded individual
                // records instead; Unity 6 GetTreeInstance avoids that copy.
                // Preserve the selective retention filter: only trees that can
                // affect a house or any safe relocation enter the temporary index.
                BuildTreeInterestCells(plan);
                foreach (Terrain t in terrains)
                {
                    if (t == null || t.terrainData == null) continue;
                    TerrainData data = t.terrainData;
                    Vector3 origin = t.transform.position;
                    Vector3 size = data.size;
                    var samples = new AarisTreeClearanceIndex();
                    treeCache.Add(t, samples);
                    int treeCount = data.treeInstanceCount;
                    int processed = 0;
                    double sliceDeadline = mobile ? Time.realtimeSinceStartupAsDouble +
                        AndroidTreeProbeBudgetSeconds : 0d;
                    for (int i = 0; i < treeCount; i++)
                    {
                        TreeInstance tree = data.GetTreeInstance(i);
                        float x = origin.x + tree.position.x * size.x;
                        float z = origin.z + tree.position.z * size.z;
                        // Retain only relevant cells, but budget EVERY native
                        // tree probe, including discarded clearance records.
                        if (!float.IsNaN(x) && !float.IsInfinity(x) &&
                            !float.IsNaN(z) && !float.IsInfinity(z) &&
                            treeInterestCells.Contains(
                                TreeInterestKey(TreeInterestCell(x), TreeInterestCell(z))))
                        {
                            float margin = Mathf.Min(3.8f,
                                Mathf.Max(0.5f, tree.widthScale * 1.5f));
                            samples.Add(x, z, margin);
                        }
                        // A fixed item count alone is not a CPU time budget on
                        // throttled phones. Check every 16 records and yield at
                        // either limit; always process some work before yielding.
                        if (mobile && (++processed >= AndroidTreeProbeBudgetPerFrame ||
                            ((processed & 15) == 0 &&
                             Time.realtimeSinceStartupAsDouble >= sliceDeadline)))
                        {
                            processed = 0;
                            yield return null;
                            sliceDeadline = Time.realtimeSinceStartupAsDouble +
                                AndroidTreeProbeBudgetSeconds;
                        }
                    }
                    if (mobile) yield return null;
                }
                foreach (var site in plan.sites)
                {
                    // Budget attempted sites, not just successful instantiations:
                    // unsuccessful tree/physics relocation probes also cost CPU.
                    // Android yields the opening frame; desktop keeps its two-site budget.
                    if (examinedThisFrame >= attemptsPerFrame)
                    {
                        examinedThisFrame = 0;
                        yield return null;
                    }
                    examinedThisFrame++;
                    if (site == null || string.IsNullOrWhiteSpace(site.name) ||
                        !names.Add(site.name) || !CoordinatesValid(site))
                    {
                        skippedResources++;
                        continue;
                    }
                    // Preserve the four proven interactive modular sites and the
                    // original watchtowers. Only the noninteractive house variants
                    // are replaced with prefabs baked into core project assets.
                    bool replaceClosedHouse = !site.interactive &&
                        (site.prefabKey == "cottage" || site.prefabKey == "bungalow" ||
                         site.prefabKey == "detached");
                    GameObject prefab = replaceClosedHouse
                        ? catalog.ResolveOpen(site.prefabKey)
                        : catalog.Resolve(site.prefabKey);
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
                    // Vegetation can obstruct a planned interactive entrance just as
                    // a building can. Both cases must use the SAME bounded, fully
                    // revalidated relocation search before an interactive home is
                    // skipped. Closed houses remain pinned to their authored sites.
                    bool blockedByTrees = TreesBlock(site, terrains);
                    bool blockedByCollider = !blockedByTrees && CollidersBlock(site, y);
                    if (blockedByTrees || blockedByCollider)
                    {
                        if (!site.interactive ||
                            !TryRelocateInteractiveSite(site, terrains, out y))
                        {
                            if (blockedByTrees) skippedTrees++;
                            else
                            {
                                skippedObstacle++;
                                Debug.LogWarning("AARIS_EXPANSION_OBSTACLE_SITE " + site.name +
                                    " x=" + site.x.ToString("F1") + " z=" + site.z.ToString("F1"));
                            }
                            continue;
                        }
                    }

#if UNITY_ANDROID && !UNITY_EDITOR
                    // Site validation never consumes an optional build slot.
                    // Only a fully valid house/watchtower admission competes
                    // with cars, ground loot, motorcycles and medical visuals.
                    // This yields rather than skipping any authored landmark.
                    while (!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())
                        yield return null;
#endif
                    Vector3 position = new Vector3(site.x, y - 0.025f, site.z);
                    Quaternion rotation = Quaternion.Euler(0f, site.yaw, 0f);
                    // Original vendor houses have ONE fused mesh/collider; an
                    // animated door pasted on top would stay physically blocked.
                    // Only marked, tested new sites use true modular wall holes.
                    GameObject instance = site.interactive
                        ? AarisModularHouse.Create(position, rotation, site.width,
                            site.depth, site.height, transform, interactionTarget, prefab,
                            includePanels: true)
                        : Instantiate(prefab, position, rotation, transform);
                    if (instance == null)
                    {
                        skippedResources++;
                        continue;
                    }
                    instance.name = site.name;
                    // Register the complete authored house immediately. A later
                    // optional cosmetic pass must never lose it on cancellation.
                    placed.Add(instance);
                    // Reuse ONE shared URP Lit material and ONE combined mesh per
                    // house for roof fascia, foundation plinth and corner trim.
                    // No extra colliders, so door and window apertures stay open.
                    if (site.prefabKey == "cottage" ||
                        site.prefabKey == "bungalow" ||
                        site.prefabKey == "detached")
                    {
#if UNITY_ANDROID && !UNITY_EDITOR
                        // Instantiating a house and uploading its additional
                        // combined facade mesh in ONE frame stacks native CPU/GPU
                        // integration costs. Preserve every mesh and texture but
                        // defer the optional trim until a DIFFERENT admitted frame.
                        yield return null;
                        while (isActiveAndEnabled && instance != null &&
                            !AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())
                            yield return null;
                        if (!isActiveAndEnabled || instance == null) yield break;
#endif
                        AarisHouseFacadeTrim.Install(instance,
                            site.width, site.depth, site.height);
                    }
                }
            }
            finally
            {
                // Release compact clearance caches when placement finishes or
                // the coroutine is cancelled; do not retain them all match
                // or the coroutine is cancelled during a scene transition.
                treeCache.Clear();
                treeInterestCells.Clear();
            }
            Debug.Log("AARIS_EXPANSION_RESULT placed=" + placed.Count +
                " planned=" + plan.siteCount + " terrain_skips=" + skippedTerrain +
                " tree_skips=" + skippedTrees + " obstacle_skips=" + skippedObstacle +
                " asset_skips=" + skippedResources);
        }

        // A blocked interactive house should not simply disappear.
        // Search deterministic nearby foundations while preserving all terrain,
        // slope, vegetation, and physical-collider requirements. This runs only
        // once at scene load, never in the mobile streaming hot path.
        private bool TryRelocateInteractiveSite(AarisExpansionSite site,
            Terrain[] terrains, out float elevation)
        {
            float originalX = site.x, originalZ = site.z;
            elevation = 0f;
            Vector2[] offsets = {
                new Vector2(0f, 18f), new Vector2(18f, 0f),
                new Vector2(0f, -18f), new Vector2(-18f, 0f),
                new Vector2(18f, 18f), new Vector2(-18f, 18f),
                new Vector2(18f, -18f), new Vector2(-18f, -18f),
                new Vector2(0f, 26f), new Vector2(26f, 0f),
                new Vector2(0f, -26f), new Vector2(-26f, 0f)
            };
            foreach (Vector2 offset in offsets)
            {
                site.x = originalX + offset.x;
                site.z = originalZ + offset.y;
                if (!CoordinatesValid(site) ||
                    !TryGround(site, terrains, out _, out float candidateY) ||
                    TreesBlock(site, terrains) || CollidersBlock(site, candidateY))
                    continue;
                elevation = candidateY;
                Debug.Log("AARIS_INTERACTIVE_HOUSE_SAFE_RELOCATION " + site.name +
                    " from (" + originalX.ToString("F1") + "," +
                    originalZ.ToString("F1") + ") to (" +
                    site.x.ToString("F1") + "," + site.z.ToString("F1") + ")");
                return true;
            }
            site.x = originalX;
            site.z = originalZ;
            return false;
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
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using var profilerScope = k_TreeClearance.Auto();
#endif
            // A footprint on the seam between native tiles must check trees
            // from BOTH tiles, not only the tile containing its centre.
            FootprintHalfExtents(site, VegetationMargin, out float hx, out float hz);
            foreach (Terrain terrain in all)
            {
                if (terrain == null || terrain.terrainData == null ||
                    !treeCache.TryGetValue(terrain, out AarisTreeClearanceIndex trees))
                    continue;
                Vector3 basePoint = terrain.transform.position;
                Vector3 tileSize = terrain.terrainData.size;
                if (site.x + hx + 3.8f < basePoint.x ||
                    site.x - hx - 3.8f > basePoint.x + tileSize.x ||
                    site.z + hz + 3.8f < basePoint.z ||
                    site.z - hz - 3.8f > basePoint.z + tileSize.z)
                    continue;
                // The complete tile snapshot includes trees from both sides
                // of seams. Query only intersecting cells; every candidate is
                // still tested against the exact original clearance predicate.
                if (trees.Blocks(site.x, site.z, hx, hz)) return true;
            }
            return false;
        }

        private static int TreeInterestCell(float coordinate) =>
            Mathf.FloorToInt(coordinate / TreeInterestCellMetres);

        private static long TreeInterestKey(int x, int z) =>
            ((long)x << 32) | (uint)z;

        private void BuildTreeInterestCells(AarisExpansionPlan plan)
        {
            treeInterestCells.Clear();
            foreach (AarisExpansionSite site in plan.sites)
            {
                if (site == null || !CoordinatesValid(site)) continue;
                FootprintHalfExtents(site, VegetationMargin,
                    out float halfX, out float halfZ);
                // Existing relocation tries +/-18m and +/-26m along each
                // axis. Expand the initial envelope by the maximum per-axis
                // displacement and the maximum width-based tree margin.
                float relocation = site.interactive
                    ? MaximumInteractiveRelocationAxis : 0f;
                float xRange = halfX + MaximumTreeClearanceMargin + relocation;
                float zRange = halfZ + MaximumTreeClearanceMargin + relocation;
                int minX = TreeInterestCell(site.x - xRange);
                int maxX = TreeInterestCell(site.x + xRange);
                int minZ = TreeInterestCell(site.z - zRange);
                int maxZ = TreeInterestCell(site.z + zRange);
                for (int x = minX; x <= maxX; x++)
                    for (int z = minZ; z <= maxZ; z++)
                        treeInterestCells.Add(TreeInterestKey(x, z));
            }
        }

        private bool CollidersBlock(AarisExpansionSite site, float y)
        {
            Quaternion rotation = Quaternion.Euler(0f, site.yaw, 0f);
            Vector3 half = new Vector3(site.width * 0.5f + ObstacleMargin,
                site.height * 0.5f + 0.3f,
                site.depth * 0.5f + ObstacleMargin);
            Vector3 center = new Vector3(site.x, y + half.y + 0.05f, site.z);
            // Physics.OverlapBox omits disabled distant walls and houses.
            // Conservative streamed bounds prevent invisible intersections;
            // existing safe relocation may search alternative foundations.
            if (nearbyColliderBounds != null &&
                nearbyColliderBounds.HasPotentialInactiveObstacle(center, half, rotation))
                return true;
            int n = Physics.OverlapBoxNonAlloc(center, half, obstacles,
                rotation, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            Collider[] candidates = obstacles;
            if (n >= obstacles.Length)
            {
                // NonAlloc saturation is not proof that a planned site is blocked:
                // the first 96 unordered hits can all be terrain/low foundations
                // that the filter below intentionally ignores. Expansion placement
                // runs only during setup, so resolve the rare overflow completely.
                candidates = Physics.OverlapBox(center, half, rotation,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                n = candidates.Length;
            }
            for (int i = 0; i < n; i++)
            {
                Collider collider = candidates[i];
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
