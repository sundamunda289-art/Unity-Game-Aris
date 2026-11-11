using System;
using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Aaris.MapReview.Editor
{
    // Offline, editor-only mesh baker. Gameplay uses saved Resources prefabs,
    // never an on-device mesh constructor or external downloader.
    [InitializeOnLoad]
    public static class AarisKenneyOpenHouseBaker
    {
        private const string Source = "Assets/AarisWorld/ThirdParty/KenneyBuildingKit/FBX/";
        private const string Generated = "Assets/AarisWorld/Generated/KenneyOpenHomes";
        private const string Prefabs = "Assets/Resources/AarisBuildings";
        private const string Request = "/tmp/aaris-codespaces/bake-kenney-houses.request";
        private const string Report = "/tmp/aaris-codespaces/bake-kenney-houses.report";

        static AarisKenneyOpenHouseBaker()
        {
            EditorApplication.delayCall += TryRequested;
        }

        private static void TryRequested()
        {
            if (!File.Exists(Request)) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            File.Delete(Request);
            Run();
        }

        [MenuItem("Aaris/Assets/Bake Offline Kenney Open Houses")]
        public static void Run()
        {
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                    throw new InvalidOperationException("EDITOR_PLAY_MODE_NOT_SAFE_FOR_BAKING");
                RequireFolder("Assets/AarisWorld/Generated", "KenneyOpenHomes");
                RequireFolder("Assets/Resources", "AarisBuildings");
                Material shared = CreateMaterial();
                string[] names = { "OpenCottage", "OpenBungalow", "OpenDetached" };
                float[] widths = { 9.968f, 14.448f, 12.383f };
                float[] depths = { 14.349f, 14.349f, 16.430f };
                float[] heights = { 7.517f, 6.673f, 10.244f };
                var result = new List<string> { "RESULT=PASS", "SOURCE=KENNEY_BUILDING_KIT_CC0" };
                for (int i = 0; i < names.Length; i++)
                    result.Add(Bake(names[i], widths[i], depths[i], heights[i], shared));
                AssetDatabase.SaveAssets();
                File.WriteAllLines(Report, result);
                Debug.Log("AARIS_KENNEY_OPEN_HOUSES_BAKED_THREE");
            }
            catch (Exception error)
            {
                File.WriteAllText(Report, "RESULT=FAILED\nTYPE=" +
                    error.GetType().Name + "\nREASON=" + error.Message.Replace('\n',' '));
                Debug.LogError("AARIS_KENNEY_OPEN_HOUSE_BAKE_FAILED");
            }
        }

        private static void RequireFolder(string parent, string name)
        {
            string path = parent + "/" + name;
            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(parent, name);
        }

        private static Material CreateMaterial()
        {
            string path = Generated + "/KenneyAtlasURP.mat";
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Texture2D atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(Source + "Textures/colormap.png");
            if (shader == null || atlas == null)
                throw new InvalidOperationException("URP_SHADER_OR_SHARED_ATLAS_MISSING");
            Material mat = new Material(shader) { name = "Kenney CC0 Optimized Atlas", enableInstancing = true };
            mat.SetTexture("_BaseMap", atlas);
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Smoothness", 0.18f);
            mat.SetFloat("_Metallic", 0f);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        private static Bounds GetBounds(GameObject obj)
        {
            var all = obj.GetComponentsInChildren<Renderer>(true);
            if (all.Length == 0) throw new InvalidOperationException("FBX_RENDERERS_MISSING");
            Bounds bounds = all[0].bounds;
            for (int i = 1; i < all.Length; i++) bounds.Encapsulate(all[i].bounds);
            return bounds;
        }

        private static void AddPart(Transform parent, string model, Vector3 center,
            Vector3 targetSize, Quaternion rotation)
        {
            GameObject original = AssetDatabase.LoadAssetAtPath<GameObject>(Source + model + ".fbx");
            if (original == null) throw new InvalidOperationException("SOURCE_FBX_MISSING_" + model);
            GameObject part = UnityEngine.Object.Instantiate(original, parent);
            part.name = model;
            part.transform.localPosition = Vector3.zero;
            part.transform.localRotation = rotation;
            part.transform.localScale = Vector3.one;
            Bounds before = GetBounds(part);
            Vector3 size = before.size;
            // Renderer.bounds is in world axes but Transform.localScale is in
            // object axes: yaw 90/270 swaps local X and Z. Without this mapping
            // narrow side walls stretch tens of metres through the doorway.
            bool quarterTurn = Mathf.Abs(Mathf.Sin(rotation.eulerAngles.y *
                Mathf.Deg2Rad)) > .9f;
            float worldX = targetSize.x / Mathf.Max(0.001f, size.x);
            float worldZ = targetSize.z / Mathf.Max(0.001f, size.z);
            Vector3 ratios = quarterTurn
                ? new Vector3(worldZ,
                    targetSize.y / Mathf.Max(0.001f, size.y), worldX)
                : new Vector3(worldX,
                    targetSize.y / Mathf.Max(0.001f, size.y), worldZ);
            part.transform.localScale = ratios;
            Bounds after = GetBounds(part);
            part.transform.position += center - after.center;
        }

        // Build real negative-space openings by omitting mesh geometry entirely.
        // Do not assume decorative FBX doorway meshes have a traversable aperture.
        private static void Panel(Transform parent, Vector3 center, Vector3 size,
            Quaternion rotation, bool opening, bool doorway, bool alongZ)
        {
            if (!opening) { AddPart(parent,"wall",center,size,rotation); return; }
            float span = alongZ ? size.z : size.x;
            float gap = span * (doorway ? .68f : .62f);
            float margin = (span-gap)*.5f;
            float bottom = doorway ? 0f : Mathf.Max(.82f,size.y*.28f);
            float gapHeight = doorway ? Mathf.Min(2.30f,size.y*.74f) :
                Mathf.Min(1.38f,size.y*.43f);
            float top = size.y-bottom-gapHeight;
            if (top < .23f) throw new InvalidOperationException("OPENING_TOP_TOO_SMALL");
            // Wall posts flank the true opening; the space between them has NO triangles.
            for (int side=-1;side<=1;side+=2)
            {
                Vector3 c=center;
                float delta=side*(gap+margin)*.5f;
                if(alongZ)c.z+=delta;else c.x+=delta;
                Vector3 dimensions=alongZ?new Vector3(size.x,size.y,margin):
                    new Vector3(margin,size.y,size.z);
                AddPart(parent,"wall",c,dimensions,rotation);
            }
            if (bottom > .001f) Band(center,0f,bottom);
            Band(center,bottom+gapHeight,top);

            void Band(Vector3 fullCenter,float start,float height)
            {
                Vector3 c=fullCenter;
                c.y=fullCenter.y-size.y*.5f+start+height*.5f;
                Vector3 dimensions=alongZ?new Vector3(size.x,height,gap):
                    new Vector3(gap,height,size.z);
                AddPart(parent,"wall",c,dimensions,rotation);
            }
        }

        private static void AddFront(Transform parent, float width, float depth,
            float baseY, float layerHeight, bool ground, bool back)
        {
            float strip=width/5f;
            for(int i=-2;i<=2;i++)
            {
                bool doorway=i==0 && ground && !back;
                bool window=i==-1 || i==1 || (back && i==0);
                Vector3 center=new Vector3(i*strip,baseY+layerHeight*.5f,
                    back?depth*.5f:-depth*.5f);
                Panel(parent,center,new Vector3(strip+.008f,layerHeight+.008f,.22f),
                    back?Quaternion.Euler(0,180,0):Quaternion.identity,
                    doorway||window,doorway,false);
            }
        }

        private static void AddSides(Transform parent, float width, float depth,
            float baseY, float layerHeight, int level)
        {
            float strip=depth/5f;
            for(int side=-1;side<=1;side+=2)
                for(int i=-2;i<=2;i++)
                {
                    Vector3 center=new Vector3(side*width*.5f,
                        baseY+layerHeight*.5f,i*strip);
                    Panel(parent,center,new Vector3(.22f,layerHeight+.008f,strip+.008f),
                        Quaternion.Euler(0,side>0?90f:-90f,0),
                        i==-1||i==1,false,true);
                }
        }

        private static string Bake(string name, float width, float depth, float height, Material material)
        {
            string meshPath = Generated + "/" + name + ".asset";
            string prefabPath = Prefabs + "/" + name + ".prefab";
            Mesh previousMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            GameObject staging = new GameObject("TemporaryKenneyBake");
            try
            {
                int levels = height >= 9f ? 3 : 2;
                float floorHeight = (height - .30f) / levels;
                for (int level = 0; level < levels; level++)
                {
                    float y = level * floorHeight;
                    AddFront(staging.transform,width,depth,y,floorHeight,level == 0,false);
                    AddFront(staging.transform,width,depth,y,floorHeight,false,true);
                    AddSides(staging.transform,width,depth,y,floorHeight,level);
                }
                AddPart(staging.transform, "floor", new Vector3(0,-.10f,0),
                    new Vector3(width,.22f,depth),Quaternion.identity);
                AddPart(staging.transform, "roof-flat-square",
                    new Vector3(0,height-.13f,0),
                    new Vector3(width+.30f,.26f,depth+.30f),Quaternion.identity);
                var combine = new List<CombineInstance>();
                foreach (MeshFilter filter in staging.GetComponentsInChildren<MeshFilter>(true))
                {
                    Mesh source = filter.sharedMesh;
                    if (source == null) continue;
                    for (int sub = 0; sub < source.subMeshCount; sub++)
                        combine.Add(new CombineInstance { mesh = source,
                            subMeshIndex = sub, transform = filter.transform.localToWorldMatrix });
                }
                if (combine.Count < 15) throw new InvalidOperationException("INSUFFICIENT_GEOMETRY");
                Mesh mesh = new Mesh { name = name + "_SingleBatchMesh",
                    indexFormat = IndexFormat.UInt32 };
                mesh.CombineMeshes(combine.ToArray(), true, true, false);
                mesh.RecalculateBounds();
                if (mesh.vertexCount < 200 || mesh.triangles.Length / 3 > 30000)
                    throw new InvalidOperationException("BAD_TRIANGLE_BUDGET");
                if (previousMesh == null) AssetDatabase.CreateAsset(mesh, meshPath);
                else { EditorUtility.CopySerialized(mesh, previousMesh);
                    UnityEngine.Object.DestroyImmediate(mesh); mesh=previousMesh; }
                GameObject root = new GameObject(name);
                try
                {
                    GameObject render = new GameObject("OneMaterialCombinedGeometry");
                    render.transform.SetParent(root.transform,false);
                    render.AddComponent<MeshFilter>().sharedMesh = mesh;
                    render.AddComponent<MeshRenderer>().sharedMaterial = material;
                    MeshCollider collision = render.AddComponent<MeshCollider>();
                    collision.sharedMesh = mesh;
                    collision.convex = false;
                    render.isStatic = true;
                    root.isStatic = true;
                    GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root,prefabPath);
                    if (prefab == null) throw new InvalidOperationException("PREFAB_SAVE_FAILED_" + name);
                    return name + " vertices=" + mesh.vertexCount +
                        " triangles=" + (mesh.triangles.Length / 3) +
                        " width=" + width + " depth=" + depth + " height=" + height;
                }
                finally { UnityEngine.Object.DestroyImmediate(root); }
            }
            finally { UnityEngine.Object.DestroyImmediate(staging); }
        }
    }
}