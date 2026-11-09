using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Aaris.MapReview.Editor
{
    // One-time, Editor-only native TerrainData asset edit. Never runs in an APK.
    // Replaces a bounded subset of existing forest trees with DeepForestPine;
    // never spawns extra instances, moves existing trees, touches a road, or
    // changes mesh triangle/LOD budgets.
    public static class AarisDeepForestNativeBake
    {
        private const string ScenePath =
            "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";
        private const string TreePath =
            "Assets/TerrainDemoScene_URP/Prefabs/Trees/DeepForest/DeepForestPine.prefab";
        private const string Output = "/tmp/aaris-codespaces/deep-forest-bake-report.json";
        private static readonly Vector2[] ForestSites = {
            new Vector2(418f,-350f), new Vector2(65f,870f),
            new Vector2(48f,-828f)
        };
        private const float Radius = 265f;
        private const int MaxConversions = 48;

        [Serializable] private sealed class TileReport
        {
            public string asset, terrainName;
            public int beforeTrees, afterTrees, oldPrototypes, newPrototypes, converted;
        }
        [Serializable] private sealed class Report
        {
            public string status, reason, unityVersion;
            public int terrainTiles, converted, oldTrees, newTrees;
            public TileReport[] tiles;
        }
        private sealed class TilePlan
        {
            public TerrainData data;
            public TreePrototype[] prototypes;
            public TreeInstance[] instances;
            public TileReport report;
        }
        private static bool IsOriginalForestSpecies(string path)
        {
            return path.EndsWith("/Conifer/Conifer.prefab", StringComparison.Ordinal) ||
                   path.EndsWith("/Pines/Pine_B/Pine_B.prefab", StringComparison.Ordinal) ||
                   path.EndsWith("/Pines/Pine_D/Pine_D.prefab", StringComparison.Ordinal);
        }
        private static uint PositionHash(Vector3 p)
        {
            unchecked
            {
                uint a=(uint)Mathf.RoundToInt(p.x*10f);
                uint b=(uint)Mathf.RoundToInt(p.z*10f);
                uint hash=(a*0x9e3779b9u) ^ (b*0x85ebca6bu);
                hash^=hash>>16;
                hash*=0x7feb352du;
                hash^=hash>>15;
                return hash;
            }
        }
        private static bool InsideForest(Vector3 world)
        {
            Vector2 xz=new Vector2(world.x,world.z);
            foreach(var site in ForestSites)
                if((xz-site).sqrMagnitude<=Radius*Radius) return true;
            return false;
        }

        [MenuItem("Aaris/CI/Bake bounded native deep forest variant")]
        public static void Apply()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Output));
            var result=new Report{status="failed",reason="NOT_STARTED",
                                  unityVersion=Application.unityVersion};
            var tileReports=new List<TileReport>();
            try
            {
                if(Application.unityVersion!="6000.3.26f1")
                    throw new InvalidOperationException("UNEXPECTED_UNITY_EDITOR_VERSION");
                var variant=AssetDatabase.LoadAssetAtPath<GameObject>(TreePath);
                if(variant==null || variant.GetComponent<LODGroup>()==null ||
                   variant.GetComponent<LODGroup>().lodCount!=4 ||
                   variant.GetComponent<BoxCollider>()==null)
                    throw new InvalidOperationException("NATIVE_VARIANT_PREFAB_INCOMPLETE");
                var scene=EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);
                var terrains=UnityEngine.Object.FindObjectsByType<Terrain>(
                    FindObjectsInactive.Include,FindObjectsSortMode.None)
                    .Where(t=>t!=null && t.gameObject.scene==scene && t.terrainData!=null)
                    .OrderBy(t=>t.name,StringComparer.Ordinal).ToArray();
                if(terrains.Length!=4)throw new InvalidOperationException("FOUR_TERRAIN_TILES_REQUIRED");
                result.terrainTiles=terrains.Length;
                var plans=new List<TilePlan>();
                var uniqueData=new HashSet<TerrainData>();
                int selected=0;
                foreach(var terrain in terrains)
                {
                    TerrainData data=terrain.terrainData;
                    if(!uniqueData.Add(data))
                        throw new InvalidOperationException("TERRAIN_DATA_SHARED_UNEXPECTEDLY");
                    string path=AssetDatabase.GetAssetPath(data);
                    if(!path.StartsWith("Assets/TerrainDemoScene_URP/Terrain/Data/",StringComparison.Ordinal) ||
                       !path.EndsWith(".asset",StringComparison.Ordinal))
                        throw new InvalidOperationException("UNEXPECTED_TERRAIN_ASSET_PATH");
                    var old=data.treePrototypes;
                    var oldInstances=data.treeInstances;
                    if(old==null||old.Length==0||oldInstances==null)
                        throw new InvalidOperationException("TERRAIN_TREE_SOURCE_MISSING");
                    var targetIndices=new HashSet<int>();
                    for(int p=0;p<old.Length;p++)
                    {
                        var prefab=old[p].prefab;
                        if(!prefab)continue;
                        var prefabPath=AssetDatabase.GetAssetPath(prefab);
                        if(prefabPath==TreePath)
                            throw new InvalidOperationException("DEEP_FOREST_ALREADY_INSTALLED");
                        if(IsOriginalForestSpecies(prefabPath))targetIndices.Add(p);
                    }
                    var cloned=(TreeInstance[])oldInstances.Clone();
                    int changed=0;
                    for(int i=0;i<cloned.Length && selected<MaxConversions;i++)
                    {
                        var instance=cloned[i];
                        if(!targetIndices.Contains(instance.prototypeIndex))continue;
                        Vector3 world=terrain.transform.position+
                                      Vector3.Scale(instance.position,data.size);
                        if(!InsideForest(world) || PositionHash(world)%5u!=0u)continue;
                        // Only the saved prototype index changes. Existing positions,
                        // scale, color, lightmap and wind instance data are retained.
                        instance.prototypeIndex=old.Length;
                        cloned[i]=instance;
                        changed++;selected++;
                    }
                    var next=new TreePrototype[old.Length+(changed>0?1:0)];
                    Array.Copy(old,next,old.Length);
                    if(changed>0)
                    {
                        // New silhouette and darker bark, but same original
                        // native four LOD mesh references and tree count.
                        next[old.Length]=new TreePrototype{prefab=variant,bendFactor=old[targetIndices.Min()].bendFactor};
                    }
                    var tile=new TileReport{
                        asset=path,terrainName=terrain.name,
                        beforeTrees=oldInstances.Length,afterTrees=cloned.Length,
                        oldPrototypes=old.Length,newPrototypes=next.Length,
                        converted=changed
                    };
                    tileReports.Add(tile);
                    result.oldTrees+=oldInstances.Length;
                    result.newTrees+=cloned.Length;
                    plans.Add(new TilePlan{data=data,prototypes=next,
                                           instances=cloned,report=tile});
                }
                if(selected<1 || selected>MaxConversions || result.oldTrees!=result.newTrees)
                    throw new InvalidOperationException("INVALID_FOREST_CONVERSION_BUDGET");
                // Preflight all four sources before committing any TerrainData.
                foreach(var plan in plans.Where(p=>p.report.converted>0))
                {
                    plan.data.treePrototypes=plan.prototypes;
                    plan.data.SetTreeInstances(plan.instances,false);
                    if(plan.data.treeInstanceCount!=plan.report.beforeTrees)
                        throw new InvalidOperationException("TREE_INSTANCE_COUNT_CHANGED");
                    EditorUtility.SetDirty(plan.data);
                }
                AssetDatabase.SaveAssets();
                result.tiles=tileReports.ToArray();
                result.converted=selected;
                result.status="passed";
                result.reason="NATIVE_TERRAINDATA_TREE_PROTOTYPE_SAVED_NO_NEW_INSTANCES";
                Debug.Log("AARIS_NATIVE_DEEP_FOREST_BAKE_PASSED");
            }
            catch(Exception error)
            {
                result.reason=error is InvalidOperationException?error.Message:error.GetType().Name;
                Debug.LogError("AARIS_NATIVE_DEEP_FOREST_BAKE_FAILED:"+result.reason);
                throw;
            }
            finally
            {
                result.tiles=tileReports.ToArray();
                File.WriteAllText(Output,JsonUtility.ToJson(result,true));
            }
        }
    }
}
