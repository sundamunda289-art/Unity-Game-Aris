using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// One-time offline CC0 Spectral GT authoring. Never runs in Android.
    /// Takes five verified low-poly-profile OBJ sources and builds a Unity
    /// prefab with independent, physics-compatible tyre pivots and URP paint.
    /// </summary>
    public static class AarisSpectralGtAuthor
    {
        private const string Root = "Assets/Resources/AarisVehicles/SpectralPrototype";
        private static readonly string[] Groups =
            { "body", "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr" };
        // Source glTF nose +X converted to +Z, left wheel at negative X.
        private static readonly Vector3[] WheelCentres =
        {
            new Vector3(-1.015f, .53f, 1.37f),
            new Vector3( 1.015f, .53f, 1.37f),
            new Vector3(-1.015f, .53f,-1.48f),
            new Vector3( 1.015f, .53f,-1.48f)
        };

        [MenuItem("Aaris/CI/Build isolated CC0 Spectral GT prefab")]
        public static void Build()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("Author prototype only in isolated Editor batch mode.");
            string prefabPath = Root + "/SpectralGT.prefab";
            if (File.Exists(prefabPath))
                throw new InvalidOperationException("Existing Spectral GT prefab: refuse silent overwrite.");

            foreach (string group in Groups)
                if (!File.Exists(Root + "/SpectralGT_" + group + ".obj"))
                    throw new FileNotFoundException("Verified source OBJ missing: " + group);
            if (!File.Exists(Root + "/SpectralGT.mtl"))
                throw new FileNotFoundException("Authoring palette missing.");

            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null)
                throw new InvalidOperationException("URP Lit required for true automotive surfaces.");
            var palette = ParseMtl(Root + "/SpectralGT.mtl");
            if (!palette.ContainsKey("AarisPaint") || !palette.ContainsKey("AarisGlass") ||
                !palette.ContainsKey("AarisRubber") || !palette.ContainsKey("AarisAlloy"))
                throw new InvalidOperationException("Source automotive material IDs missing.");
            string matsFolder = Root + "/PBRMaterials";
            if (!AssetDatabase.IsValidFolder(matsFolder))
                AssetDatabase.CreateFolder(Root, "PBRMaterials");
            var materials = new Dictionary<string, Material>();
            foreach (var entry in palette)
            {
                string name = entry.Key;
                string path = matsFolder + "/" + name + ".mat";
                if (AssetDatabase.LoadAssetAtPath<Material>(path) != null)
                    throw new InvalidOperationException("Existing authored material: " + path);
                var material = new Material(lit)
                {
                    name = name,
                    enableInstancing = true
                };
                float metallic = name == "AarisPaint" ? .39f : .12f;
                float smoothness = name == "AarisPaint" ? .67f : .35f;
                if (name.IndexOf("Glass", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Display", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    metallic = .04f;
                    smoothness = .83f;
                }
                if (name.IndexOf("Alloy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Aluminium", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Chrome", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    metallic = .85f;
                    smoothness = .59f;
                }
                if (name.IndexOf("Rubber", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Sidewall", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    metallic = 0f;
                    smoothness = .17f;
                }
                if (name.IndexOf("Carbon", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    metallic = .26f;
                    smoothness = .37f;
                }
                material.SetColor("_BaseColor", entry.Value);
                material.SetFloat("_Metallic", metallic);
                material.SetFloat("_Smoothness", smoothness);
                if (name == "AarisPaint" && material.HasProperty("_ClearCoatMask"))
                {
                    material.SetFloat("_ClearCoatMask", .28f);
                    material.SetFloat("_ClearCoatSmoothness", .72f);
                    material.EnableKeyword("_CLEARCOAT");
                }
                if (name.Contains("Headlight") || name.Contains("Taillight") ||
                    name.Contains("IceBlue"))
                {
                    material.SetColor("_EmissionColor", Color.Lerp(
                        Color.black, entry.Value, .42f));
                    material.EnableKeyword("_EMISSION");
                }
                AssetDatabase.CreateAsset(material, path);
                materials.Add(name, material);
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("SpectralGT");
            try
            {
                long totalTriangles = 0;
                for (int i = 0; i < Groups.Length; i++)
                {
                    string group = Groups[i];
                    string source = Root + "/SpectralGT_" + group + ".obj";
                    var imported = AssetDatabase.LoadAssetAtPath<GameObject>(source);
                    if (imported == null)
                        throw new InvalidOperationException("Unity failed native OBJ import: " + source);
                    var pivot = new GameObject(i == 0 ? "Spectral_GT_Body" : group);
                    pivot.transform.SetParent(root.transform, false);
                    if (i > 0) pivot.transform.localPosition = WheelCentres[i - 1];
                    var model = UnityEngine.Object.Instantiate(imported, pivot.transform, false);
                    model.name = group + "_geometry";
                    foreach (Collider collider in model.GetComponentsInChildren<Collider>(true))
                        UnityEngine.Object.DestroyImmediate(collider);
                    int renderers = 0;
                    foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
                    {
                        if (!r.enabled) continue;
                        var sources = r.sharedMaterials;
                        if (sources == null || sources.Length == 0)
                            throw new InvalidOperationException("Source material slots missing on " + group);
                        var slots = new Material[sources.Length];
                        for (int s = 0; s < sources.Length; s++)
                        {
                            string sourceName = sources[s] != null ? sources[s].name : string.Empty;
                            if (!materials.TryGetValue(sourceName, out slots[s]))
                                throw new InvalidOperationException(
                                    "Missing explicit URP mapping for " + group + " material " + sourceName);
                        }
                        r.sharedMaterials = slots;
                        r.shadowCastingMode = ShadowCastingMode.On;
                        r.receiveShadows = true;
                        renderers++;
                    }
                    if (renderers == 0)
                        throw new InvalidOperationException("No visible mesh renderer on " + group);
                    foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>(true))
                        if (filter.sharedMesh != null)
                            totalTriangles += filter.sharedMesh.triangles.Length / 3;
                }
                if (totalTriangles < 50000 || totalTriangles > 80000)
                    throw new InvalidOperationException("Unexpected mobile GT triangle count: " + totalTriangles);
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                AssetDatabase.SaveAssets();
                if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null ||
                    Resources.Load<GameObject>("AarisVehicles/SpectralPrototype/SpectralGT") == null)
                    throw new InvalidOperationException("Unity prefab/Resources import failed.");
                Debug.Log("AARIS_SPECTRAL_GT_IMPORT_PASS triangles=" + totalTriangles +
                    " wheels=4 URP_materials=" + materials.Count + " prefab=" + prefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static Dictionary<string, Color> ParseMtl(string path)
        {
            var results = new Dictionary<string, Color>(StringComparer.Ordinal);
            string active = null;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith("newmtl ", StringComparison.Ordinal))
                {
                    active = line.Substring(7).Trim();
                }
                else if (active != null && line.StartsWith("Kd ", StringComparison.Ordinal))
                {
                    string[] parts = line.Split(new[] {' '},
                        StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 4)
                        throw new InvalidOperationException("Malformed MTL diffuse factor.");
                    var color = new Color(
                        float.Parse(parts[1], CultureInfo.InvariantCulture),
                        float.Parse(parts[2], CultureInfo.InvariantCulture),
                        float.Parse(parts[3], CultureInfo.InvariantCulture), 1f);
                    if (results.ContainsKey(active))
                        throw new InvalidOperationException("Duplicate MTL ID " + active);
                    results[active] = color;
                }
            }
            if (results.Count < 12 || results.Count > 25)
                throw new InvalidOperationException("Unexpected GT palette size.");
            return results;
        }
    }
}
