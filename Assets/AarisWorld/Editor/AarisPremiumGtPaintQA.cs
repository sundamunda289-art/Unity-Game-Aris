using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Visual proof of the ACTUAL runtime deterministic paint application:
    /// invokes production PaintVerifiedBodySlots with a temporary in-memory
    /// car record. Never edits shared materials, prefabs, scenes or saves.
    /// </summary>
    public static class AarisPremiumGtPaintQA
    {
        private const BindingFlags StaticPrivate =
            BindingFlags.Static | BindingFlags.NonPublic;
        private const BindingFlags InstanceAll =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static Bounds Visible(GameObject instance)
        {
            Bounds combined = new Bounds();
            bool found = false;
            foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
            {
                var f = r.GetComponent<MeshFilter>();
                if (!r.enabled || r.forceRenderingOff ||
                    !r.gameObject.activeInHierarchy || f == null ||
                    f.sharedMesh == null || f.sharedMesh.vertexCount == 0) continue;
                if (!found) { combined = r.bounds; found = true; }
                else combined.Encapsulate(r.bounds);
            }
            if (!found) throw new InvalidOperationException("GT rendered body missing");
            return combined;
        }

        private static object NewRecord(Type type, string vehicleId, string pointId)
        {
            Assembly game = typeof(AarisVehicleWorld).Assembly;
            Type pointType = game.GetType("Aaris.MapReview.AarisVehiclePoint", true);
            Type carType = game.GetType("Aaris.MapReview.AarisVehicleType", true);
            object record = Activator.CreateInstance(type, true);
            object point = Activator.CreateInstance(pointType, true);
            object car = Activator.CreateInstance(carType, true);
            pointType.GetField("id", InstanceAll).SetValue(point, pointId);
            carType.GetField("id", InstanceAll).SetValue(car, vehicleId);
            type.GetField("point", InstanceAll).SetValue(record, point);
            type.GetField("type", InstanceAll).SetValue(record, car);
            return record;
        }

        private static string PointIdForVariant(MethodInfo stableHash, int variant)
        {
            for (int n = 0; n < 1024; n++)
            {
                string id = "studio-vehicle-paint-" + n.ToString("D4");
                int actual = (int)stableHash.Invoke(null, new object[] { id, "GT" });
                if (actual == variant) return id;
            }
            throw new InvalidOperationException("Could not find stable car paint hash");
        }

        private static Color ActualPaintBlock(GameObject car)
        {
            foreach (var r in car.GetComponentsInChildren<Renderer>(true))
            {
                Material[] shared = r.sharedMaterials;
                for (int i = 0; i < shared.Length; i++)
                {
                    Material mat = shared[i];
                    if (mat == null ||
                        mat.name.IndexOf("AarisPaint", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    MaterialPropertyBlock block = new MaterialPropertyBlock();
                    r.GetPropertyBlock(block, i);
                    if (block.isEmpty)
                        throw new InvalidOperationException("Paint renderer missing runtime property block");
                    Color tint = block.GetColor("_BaseColor");
                    if (tint.a < .98f || float.IsNaN(tint.r) ||
                        float.IsNaN(tint.g) || float.IsNaN(tint.b) ||
                        float.IsInfinity(tint.r) || float.IsInfinity(tint.g) ||
                        float.IsInfinity(tint.b))
                        throw new InvalidOperationException("Invalid runtime paint swatch");
                    return tint;
                }
            }
            throw new InvalidOperationException("GT has no dedicated paint material");
        }

        [MenuItem("Aaris/CI/Capture eight actual runtime GT paint variants")]
        public static void Capture()
        {
            if (!Application.isBatchMode ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new InvalidOperationException("Native graphics batch mode required");
            var prefab = Resources.Load<GameObject>("AarisVehicles/PremiumRoadGT/AarisRoadGT");
            if (prefab == null) throw new InvalidOperationException("GT prefab missing");
            TextAsset catalogJson = Resources.Load<TextAsset>("AarisVehicles/vehicle_catalog");
            if (catalogJson == null) throw new InvalidOperationException("Car catalogue missing");
            AarisVehicleStudioCatalog catalog =
                JsonUtility.FromJson<AarisVehicleStudioCatalog>(catalogJson.text);
            AarisVehicleStudioRecord gt = Array.Find(catalog.vehicles,
                x => x != null && x.id == "GT");
            if (gt == null) throw new InvalidOperationException("GT catalogue missing");
            Type recordType = typeof(AarisVehicleWorld).Assembly.GetType(
                "Aaris.MapReview.AarisVehicleRecord", true);
            MethodInfo paint = typeof(AarisVehicleWorld).GetMethod(
                "PaintVerifiedBodySlots", StaticPrivate);
            MethodInfo stableHash = typeof(AarisVehicleWorld).GetMethod(
                "StablePaintVariant", StaticPrivate);
            if (paint == null || stableHash == null)
                throw new InvalidOperationException("Real paint methods missing");
            string directory = Environment.GetEnvironmentVariable("AARIS_GT_PAINT_QA_DIR");
            if (string.IsNullOrEmpty(directory))
                directory = Path.Combine(Path.GetTempPath(), "aaris-gt-paint-qa");
            Directory.CreateDirectory(directory);

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(.77f, .83f, .88f);
            RenderSettings.ambientEquatorColor = new Color(.54f, .58f, .63f);
            RenderSettings.ambientGroundColor = new Color(.25f, .27f, .29f);
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null) throw new InvalidOperationException("URP shader missing");
            Material roadMat = new Material(lit);
            roadMat.SetColor("_BaseColor", new Color(.25f, .27f, .30f));
            roadMat.SetFloat("_Smoothness", .23f);
            GameObject road = GameObject.CreatePrimitive(PrimitiveType.Plane);
            road.transform.localScale = new Vector3(.7f, 1f, 1.8f);
            road.transform.position = new Vector3(0f, -.035f, 0f);
            road.GetComponent<Renderer>().sharedMaterial = roadMat;
            var sun = new GameObject("Paint QA daylight").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.6f;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(39f, -34f, 0f);
            var camera = new GameObject("Paint QA camera").AddComponent<Camera>();
            camera.enabled = false;
            camera.fieldOfView = 48f;
            camera.nearClipPlane = .05f;
            camera.farClipPlane = 75f;
            camera.allowHDR = false;
            camera.backgroundColor = new Color(.42f, .50f, .57f);
            camera.clearFlags = CameraClearFlags.SolidColor;
            List<Color> swatches = new List<Color>();
            try
            {
                for (int i = 0; i < 8; i++)
                {
                    GameObject car = UnityEngine.Object.Instantiate(prefab);
                    try
                    {
                        Bounds original = Visible(car);
                        float width = Mathf.Clamp(gt.referenceWidthMetres,
                            1.65f, 2.15f);
                        float length = Mathf.Clamp(gt.referenceLengthMetres,
                            3.35f, 5.50f);
                        float sx = width / original.size.x;
                        float rolling = length / original.size.z;
                        car.transform.localScale = Vector3.Scale(
                            car.transform.localScale, new Vector3(sx, rolling, rolling));
                        Bounds fitted = Visible(car);
                        car.transform.position -= new Vector3(
                            fitted.center.x, fitted.min.y, fitted.center.z);
                        string point = PointIdForVariant(stableHash, i);
                        object record = NewRecord(recordType, "GT", point);
                        paint.Invoke(null, new[] { (object)car, record });
                        Color tint = ActualPaintBlock(car);
                        foreach (Color prev in swatches)
                            if (Vector3.Distance(new Vector3(tint.r,tint.g,tint.b),
                                    new Vector3(prev.r,prev.g,prev.b)) < .035f)
                                throw new InvalidOperationException("Duplicated real GT paint");
                        swatches.Add(tint);
                        Bounds b = Visible(car);
                        if (Mathf.Abs(b.size.x - width) > .03f ||
                            Mathf.Abs(b.size.z - length) > .03f)
                            throw new InvalidOperationException("Paint variant changed car size");
                        Vector3 aim = b.center + Vector3.up * .10f;
                        camera.transform.position = aim + new Vector3(4.15f, 2.45f, 6.9f);
                        camera.transform.LookAt(aim, Vector3.up);
                        string path = Path.Combine(directory, "gt-paint-" + i + ".png");
                        WritePng(camera, path);
                        Debug.Log("AARIS_REAL_GT_PAINT " + i +
                            " rgb=" + tint.ToString("F3") +
                            " metres=" + b.size.ToString("F3"));
                    }
                    finally { UnityEngine.Object.DestroyImmediate(car); }
                }
                Debug.Log("AARIS_GT_EIGHT_REAL_PAINT_QA_PASS count=" + swatches.Count);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(camera.gameObject);
                UnityEngine.Object.DestroyImmediate(sun.gameObject);
                UnityEngine.Object.DestroyImmediate(road);
                UnityEngine.Object.DestroyImmediate(roadMat);
            }
        }

        private static void WritePng(Camera camera, string path)
        {
            const int width = 1024, height = 576;
            RenderTexture target = RenderTexture.GetTemporary(
                width, height, 24, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            var output = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                output.ReadPixels(new Rect(0,0,width,height),0,0,false);
                output.Apply(false);
                byte[] encoded = output.EncodeToPNG();
                if (encoded.Length < 8192)
                    throw new InvalidOperationException("GT paint frame is empty");
                File.WriteAllBytes(path, encoded);
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(output);
                RenderTexture.ReleaseTemporary(target);
            }
        }
    }
}
