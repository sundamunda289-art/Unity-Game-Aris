using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Aaris.MapReview.Editor
{
    /// <summary>Real Unity imported-prefab view; not an Android FPS or Play Mode test.</summary>
    public static class AarisPremiumRoadGtPhoto
    {
        [MenuItem("Aaris/CI/Capture native optimized Premium Road GT photos")]
        public static void Capture()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("Dedicated batch-only photo studio.");
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new InvalidOperationException("Native graphics device required.");

            const string source = "AarisVehicles/PremiumRoadGT/AarisRoadGT";
            GameObject prefab = Resources.Load<GameObject>(source);
            if (prefab == null)
                throw new InvalidOperationException("Verified authored GT prefab not in Resources.");
            string directory = Environment.GetEnvironmentVariable("AARIS_SPECTRAL_PHOTO_DIR");
            if (string.IsNullOrEmpty(directory))
                directory = Path.Combine(Path.GetTempPath(), "aaris-gt-real-photos");
            Directory.CreateDirectory(directory);

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(.76f, .82f, .89f);
            RenderSettings.ambientEquatorColor = new Color(.55f, .59f, .64f);
            RenderSettings.ambientGroundColor = new Color(.25f, .27f, .29f);

            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null) throw new InvalidOperationException("URP Lit unavailable.");
            Material floorMat = new Material(lit);
            floorMat.SetColor("_BaseColor", new Color(.26f, .27f, .28f));
            floorMat.SetFloat("_Smoothness", .27f);

            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.transform.localScale = new Vector3(2.5f, 1f, 2.5f);
            floor.GetComponent<Renderer>().sharedMaterial = floorMat;

            Light sun = new GameObject("Temporary studio key light").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.58f;
            sun.color = new Color(1f, .97f, .91f);
            sun.transform.rotation = Quaternion.Euler(43f, -32f, 0f);
            sun.shadows = LightShadows.Soft;
            Light fill = new GameObject("Temporary sky fill").AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.intensity = .48f;
            fill.color = new Color(.75f, .84f, 1f);
            fill.transform.rotation = Quaternion.Euler(25f, 130f, 0f);
            fill.shadows = LightShadows.None;

            Camera cam = new GameObject("Temporary GT photo camera").AddComponent<Camera>();
            cam.enabled = false;
            cam.fieldOfView = 44f;
            cam.nearClipPlane = .05f;
            cam.farClipPlane = 60f;
            cam.allowHDR = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(.40f, .49f, .54f);
            GameObject model = UnityEngine.Object.Instantiate(prefab);
            model.name = "Temporary imported GT";
            try
            {
                Transform[] wheels = model.GetComponentsInChildren<Transform>(true);
                string[] tokens = { "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr" };
                foreach (var token in tokens)
                {
                    bool found = false;
                    foreach (Transform wheel in wheels)
                        if (wheel.name == token &&
                            wheel.GetComponentInChildren<MeshRenderer>() != null)
                            found = true;
                    if (!found) throw new InvalidOperationException("Real wheel pivot missing: " + token);
                }
                Bounds bounds = new Bounds();
                bool any = false;
                int triangles = 0, urp = 0;
                foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
                {
                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
                        continue;
                    if (renderer is MeshRenderer mesh)
                    {
                        MeshFilter filter = mesh.GetComponent<MeshFilter>();
                        if (filter == null || filter.sharedMesh == null) continue;
                        triangles += filter.sharedMesh.triangles.Length / 3;
                    }
                    if (!any) { bounds = renderer.bounds; any = true; }
                    else bounds.Encapsulate(renderer.bounds);
                    foreach (Material mat in renderer.sharedMaterials)
                        if (mat != null && mat.shader != null &&
                            mat.shader.name == "Universal Render Pipeline/Lit") urp++;
                        else throw new InvalidOperationException("Non-URP material in rendered GT.");
                }
                if (!any || triangles < 50000 || triangles > 80000 || urp < 12)
                    throw new InvalidOperationException("GT art budget/URP check failed: " + triangles);
                model.transform.position -= new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                bounds.center -= new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                Vector3 center = bounds.center + Vector3.up * .11f;
                var cameras = new[]
                {
                    new { label = "front-quarter", offset = new Vector3(3.45f, 1.78f, 5.28f) },
                    new { label = "side-profile", offset = new Vector3(5.60f, 1.46f, .72f) },
                    new { label = "rear-quarter", offset = new Vector3(-3.45f, 1.78f, -5.28f) }
                };
                foreach (var viewpoint in cameras)
                {
                    cam.transform.position = center + viewpoint.offset;
                    cam.transform.LookAt(center, Vector3.up);
                    WritePng(cam, Path.Combine(directory, viewpoint.label + ".png"));
                }
                File.WriteAllText(Path.Combine(directory, "QA.txt"),
                    "Unity " + Application.unityVersion + ", " + SystemInfo.graphicsDeviceName +
                    ", triangles=" + triangles + ", wheels=4, source=CC0 Premium Road GT RS\n" +
                    "Real Editor imported prefab under neutral light. Does NOT prove mobile FPS, driving or in-game material variation.\n");
                Debug.Log("AARIS_ROAD_GT_PHOTO_PASS triangles=" + triangles +
                    " views=3 URP_slots=" + urp + " bounds=" + bounds.size);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(model);
                UnityEngine.Object.DestroyImmediate(cam.gameObject);
                UnityEngine.Object.DestroyImmediate(fill.gameObject);
                UnityEngine.Object.DestroyImmediate(sun.gameObject);
                UnityEngine.Object.DestroyImmediate(floor);
                UnityEngine.Object.DestroyImmediate(floorMat);
            }
        }

        private static void WritePng(Camera cam, string path)
        {
            RenderTexture target = RenderTexture.GetTemporary(1024, 576, 24,
                RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            var output = new Texture2D(1024, 576, TextureFormat.RGB24, false);
            try
            {
                cam.targetTexture = target;
                cam.Render();
                RenderTexture.active = target;
                output.ReadPixels(new Rect(0, 0, 1024, 576), 0, 0);
                output.Apply(false);
                byte[] png = output.EncodeToPNG();
                if (png.Length < 8192)
                    throw new InvalidOperationException("Invalid native GT photograph: " + path);
                File.WriteAllBytes(path, png);
            }
            finally
            {
                cam.targetTexture = null;
                RenderTexture.active = prev;
                UnityEngine.Object.DestroyImmediate(output);
                RenderTexture.ReleaseTemporary(target);
            }
        }
    }
}
