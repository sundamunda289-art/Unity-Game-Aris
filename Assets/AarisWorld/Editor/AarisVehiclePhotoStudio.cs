using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Genuine Unity import / URP art photographs, NOT gameplay or FPS evidence.
    /// Runs only in batch mode and never saves changes to the game scene.
    /// </summary>
    public static class AarisVehiclePhotoStudio
    {
        private static readonly string[] Names =
        {
            "sedan", "suv", "sports", "premium-gt",
            "hatchback", "pickup", "van"
        };
        private static readonly string[] Paths =
        {
            "Assets/Resources/AarisVehicles/Authored/CivilianSedan.prefab",
            "Assets/Resources/AarisVehicles/Authored/1990s-suv.fbx",
            "Assets/Resources/AarisVehicles/Authored/1990s-sports-car.fbx",
            "Assets/Resources/AarisVehicles/Authored/QuaterniusSports.obj",
            "Assets/Resources/AarisVehicles/Authored/1990s-hatchback.fbx",
            "Assets/Resources/AarisVehicles/Authored/1990s-pickup-truck.fbx",
            "Assets/Resources/AarisVehicles/Authored/1980s-van.fbx"
        };

        [MenuItem("Aaris/CI/Capture genuine imported vehicle previews")]
        public static void Capture()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException(
                    "Vehicle studio requires isolated Unity batch mode; never replace the live editor scene.");
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new InvalidOperationException(
                    "Real OpenGL/Vulkan renderer required; do not use -nographics.");
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                throw new InvalidOperationException("URP Lit unavailable; cannot judge PBR vehicle art.");

            string directory = Environment.GetEnvironmentVariable("AARIS_VEHICLE_AUDIT_DIR");
            if (string.IsNullOrEmpty(directory))
                directory = Path.Combine(Path.GetTempPath(), "aaris-vehicle-art-review");
            Directory.CreateDirectory(directory);

            // Unsaved isolated stage; the native 2km terrain and game assets stay untouched.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(.79f, .84f, .91f);
            RenderSettings.ambientEquatorColor = new Color(.56f, .60f, .65f);
            RenderSettings.ambientGroundColor = new Color(.24f, .25f, .27f);

            var road = GameObject.CreatePrimitive(PrimitiveType.Plane);
            road.name = "Temporary neutral vehicle floor";
            road.transform.localScale = new Vector3(2.2f, 1f, 2.2f);
            road.transform.position = new Vector3(0f, -.035f, 0f);
            var roadMaterial = new Material(shader) { name = "Temporary neutral asphalt" };
            roadMaterial.SetColor("_BaseColor", new Color(.24f, .27f, .29f));
            roadMaterial.SetFloat("_Metallic", 0f);
            roadMaterial.SetFloat("_Smoothness", .24f);
            road.GetComponent<Renderer>().sharedMaterial = roadMaterial;

            Light key = new GameObject("Temporary soft daylight").AddComponent<Light>();
            key.type = LightType.Directional;
            key.transform.rotation = Quaternion.Euler(42f, -36f, 0f);
            key.color = new Color(1f, .97f, .92f);
            key.intensity = 1.55f;
            key.shadows = LightShadows.Soft;
            Light fill = new GameObject("Temporary sky fill").AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.transform.rotation = Quaternion.Euler(27f, 130f, 0f);
            fill.color = new Color(.74f, .83f, 1f);
            fill.intensity = .46f;
            fill.shadows = LightShadows.None;

            Camera camera = new GameObject("Temporary photo camera").AddComponent<Camera>();
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.40f, .49f, .55f);
            camera.fieldOfView = 48f;
            camera.nearClipPlane = .05f;
            camera.farClipPlane = 100f;
            camera.allowHDR = false;

            var saved = new List<string>();
            try
            {
                for (int i = 0; i < Paths.Length; i++)
                {
                    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Paths[i]);
                    if (prefab == null)
                        throw new InvalidOperationException("Imported vehicle prefab missing: " + Paths[i]);
                    GameObject instance = UnityEngine.Object.Instantiate(prefab);
                    instance.name = "Temporary " + Names[i];
                    instance.SetActive(true);
                    try
                    {
                        if (!VisibleBounds(instance, out Bounds bounds))
                            throw new InvalidOperationException("No visible mesh geometry in " + Paths[i]);
                        if (bounds.size.x > bounds.size.z * 1.10f)
                        {
                            instance.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
                            if (!VisibleBounds(instance, out bounds))
                                throw new InvalidOperationException("Vehicle orientation bounds missing: " + Paths[i]);
                        }
                        float scale = Mathf.Min(1.95f / Mathf.Max(.01f, bounds.size.x),
                            4.45f / Mathf.Max(.01f, bounds.size.z));
                        instance.transform.localScale *= Mathf.Clamp(scale, .01f, 30f);
                        if (!VisibleBounds(instance, out bounds))
                            throw new InvalidOperationException("Vehicle scale bounds missing: " + Paths[i]);
                        instance.transform.position -=
                            new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                        if (!VisibleBounds(instance, out bounds))
                            throw new InvalidOperationException("Vehicle recenter failed: " + Paths[i]);
                        Vector3 target = bounds.center + new Vector3(0f, .1f, 0f);
                        camera.transform.position = target + new Vector3(4.15f, 2.45f, 6.9f);
                        camera.transform.LookAt(target, Vector3.up);
                        string output = Path.Combine(directory, Names[i] + ".png");
                        WritePng(camera, output);
                        saved.Add(Names[i] + ".png");
                        Debug.Log("AARIS_VEHICLE_STUDIO_CAPTURE " + Names[i] +
                            " bounds=" + bounds.size.ToString("F2") + " path=" + output);
                    }
                    finally { UnityEngine.Object.DestroyImmediate(instance); }
                }
                File.WriteAllText(Path.Combine(directory, "README.txt"),
                    "Unity " + Application.unityVersion + " / " +
                    SystemInfo.graphicsDeviceName + Environment.NewLine +
                    "Actual imported vehicle asset photographs under neutral studio lighting." +
                    Environment.NewLine +
                    "These are not in-game spawn, handling, real match lighting, or Android FPS proof." +
                    Environment.NewLine + string.Join(Environment.NewLine, saved));
                Debug.Log("AARIS_VEHICLE_STUDIO_PASS " + saved.Count + " genuine Unity photos");
            }
            finally
            {
                camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(roadMaterial);
                UnityEngine.Object.DestroyImmediate(camera.gameObject);
                UnityEngine.Object.DestroyImmediate(fill.gameObject);
                UnityEngine.Object.DestroyImmediate(key.gameObject);
                UnityEngine.Object.DestroyImmediate(road);
            }
        }

        private static bool VisibleBounds(GameObject instance, out Bounds combined)
        {
            combined = new Bounds();
            bool found = false;
            foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || !renderer.enabled || renderer.forceRenderingOff ||
                    !renderer.gameObject.activeInHierarchy) continue;
                if (renderer is SkinnedMeshRenderer skin)
                {
                    if (skin.sharedMesh == null || skin.sharedMesh.vertexCount == 0) continue;
                }
                else if (renderer is MeshRenderer mesh)
                {
                    MeshFilter filter = mesh.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null ||
                        filter.sharedMesh.vertexCount == 0) continue;
                }
                else continue;
                if (!found) { combined = renderer.bounds; found = true; }
                else combined.Encapsulate(renderer.bounds);
            }
            return found;
        }

        private static void WritePng(Camera camera, string path)
        {
            const int width = 1024, height = 576;
            RenderTexture target = RenderTexture.GetTemporary(width, height, 24,
                RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            Texture2D photo = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                photo.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                photo.Apply(false);
                byte[] png = photo.EncodeToPNG();
                if (png.Length < 8192)
                    throw new InvalidOperationException("Suspiciously small car photo: " + path);
                File.WriteAllBytes(path, png);
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(photo);
                RenderTexture.ReleaseTemporary(target);
            }
        }
    }
}
