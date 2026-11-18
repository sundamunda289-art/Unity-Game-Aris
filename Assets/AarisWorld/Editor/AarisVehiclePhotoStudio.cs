using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Aaris.MapReview.Editor
{
    // Editor assembly cannot reference runtime-internal AarisVehicleType.
    // JsonUtility reads only fields needed from the SAME shipped catalogue.
    [Serializable]
    internal sealed class AarisVehicleStudioCatalog
    {
        public AarisVehicleStudioRecord[] vehicles;
    }

    [Serializable]
    internal sealed class AarisVehicleStudioRecord
    {
        public string id, drivetrain;
        public float referenceWidthMetres, referenceLengthMetres;
        public float maxSpeedMetres, massKg, tyreGripScale;
        public float suspensionScale, steeringScale, suspensionTravelScale;
    }

    /// <summary>
    /// Genuine Unity import / URP art photographs, NOT gameplay or FPS evidence.
    /// Runs only in batch mode and never saves changes to the game scene.
    /// </summary>
    public static class AarisVehiclePhotoStudio
    {
        private static readonly string[] Names =
        {
            "sedan", "suv", "sports", "premium-gt",
            "hatchback", "pickup", "van", "buggy"
        };
        private static readonly string[] VehicleIds =
        {
            "Sedan", "SUV", "Sports", "GT",
            "Hatchback", "Utility", "Van", "Buggy"
        };
        private static readonly string[] Paths =
        {
            "Assets/Resources/AarisVehicles/Authored/CivilianSedan.prefab",
            "Assets/Resources/AarisVehicles/Authored/1990s-suv.fbx",
            "Assets/Resources/AarisVehicles/Authored/1990s-sports-car.fbx",
            "Assets/Resources/AarisVehicles/PremiumRoadGT/AarisRoadGT.prefab",
            "Assets/Resources/AarisVehicles/Authored/1990s-hatchback.fbx",
            "Assets/Resources/AarisVehicles/Authored/1990s-pickup-truck.fbx",
            "Assets/Resources/AarisVehicles/Authored/1980s-van.fbx",
            "Assets/Resources/AarisVehicles/Authored/AarisBuggy.obj"
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
            TextAsset catalogJson = Resources.Load<TextAsset>(
                "AarisVehicles/vehicle_catalog");
            if (catalogJson == null)
                throw new InvalidOperationException("Real runtime car catalogue not available.");
            var catalog = JsonUtility.FromJson<AarisVehicleStudioCatalog>(catalogJson.text);
            if (catalog == null || catalog.vehicles == null)
                throw new InvalidOperationException("Invalid runtime car catalogue.");

            // Unsaved isolated stage; the native 2km terrain and game assets stay untouched.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(.79f, .84f, .91f);
            RenderSettings.ambientEquatorColor = new Color(.56f, .60f, .65f);
            RenderSettings.ambientGroundColor = new Color(.24f, .25f, .27f);

            var road = GameObject.CreatePrimitive(PrimitiveType.Plane);
            road.name = "Temporary neutral vehicle floor";
            road.transform.localScale = new Vector3(.7f, 1f, 1.8f);
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
                    GameObject wheelRig = null;
                    Material studioWheelMat = null;
                    try
                    {
                        if (!VisibleBounds(instance, out Bounds bounds))
                            throw new InvalidOperationException("No visible mesh geometry in " + Paths[i]);
                        // Use REAL live catalogue metre caps, not duplicated
                        // or silently outdated photo-studio dimensions.
                        AarisVehicleStudioRecord type = Array.Find(catalog.vehicles,
                            v => v != null && v.id == VehicleIds[i]);
                        if (type == null)
                            throw new InvalidOperationException(
                                "Runtime vehicle category absent: " + VehicleIds[i]);
                        if (bounds.size.y > 2.75f &&
                            bounds.size.y > Mathf.Max(bounds.size.x, bounds.size.z) * 1.30f &&
                            bounds.size.x > .8f && bounds.size.z > .7f)
                        {
                            instance.transform.rotation = Quaternion.Euler(-90f, 0f, 0f) *
                                instance.transform.rotation;
                            if (!VisibleBounds(instance, out bounds))
                                throw new InvalidOperationException("FBX up-axis import failed.");
                        }
                        if (bounds.size.x > bounds.size.z * 1.10f)
                        {
                            instance.transform.rotation = Quaternion.Euler(0f, 90f, 0f) *
                                instance.transform.rotation;
                            if (!VisibleBounds(instance, out bounds))
                                throw new InvalidOperationException("Vehicle orientation bounds missing: " + Paths[i]);
                        }
                        float width = Mathf.Clamp(type.referenceWidthMetres > 0f
                            ? type.referenceWidthMetres : 1.88f, 1.65f, 2.15f);
                        float length = Mathf.Clamp(type.referenceLengthMetres > 0f
                            ? type.referenceLengthMetres : 4.28f, 3.35f, 5.50f);
                        float uniform = Mathf.Min(width / bounds.size.x,
                            length / bounds.size.z);
                        if (type.id == "GT")
                        {
                            // The GT correction preserves equal rolling-plane
                            // Y/Z ratios so tyres do not turn into ellipses.
                            float transverse = Mathf.Clamp(width / bounds.size.x,
                                .01f, 50f);
                            float wheelPlane = Mathf.Clamp(length / bounds.size.z,
                                .01f, 50f);
                            instance.transform.localScale = Vector3.Scale(
                                instance.transform.localScale,
                                new Vector3(transverse, wheelPlane, wheelPlane));
                        }
                        else
                            instance.transform.localScale *=
                                Mathf.Clamp(uniform, .01f, 50f);
                        if (!VisibleBounds(instance, out bounds))
                            throw new InvalidOperationException("Vehicle scale bounds missing: " + Paths[i]);
                        instance.transform.position -=
                            new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                        if (!VisibleBounds(instance, out bounds))
                            throw new InvalidOperationException("Vehicle recenter failed: " + Paths[i]);
                        if (VehicleIds[i] == "Sedan")
                        {
                            // Sedan art contains a body only. Recreate the
                            // 4 actual game wheel visuals so the comparison
                            // cannot show an incorrectly wheel-less car.
                            wheelRig = new GameObject("Studio Sedan Wheels");
                            wheelRig.transform.position = new Vector3(0f, .49f, 0f);
                            var motor = wheelRig.AddComponent<AarisVehicleMotor>();
                            motor.Configure("Sedan", 24f, 1250f);
                            motor.FitShellAndWheelbase(
                                new Bounds(new Vector3(0f, .48f, 0f), bounds.size));
                            studioWheelMat = new Material(shader);
                            var atlas = Resources.Load<Texture2D>(
                                "AarisVehicles/Models/colormap");
                            if (atlas == null) throw new InvalidOperationException(
                                "Missing actual Kenney wheel UV palette.");
                            studioWheelMat.SetTexture("_BaseMap", atlas);
                            studioWheelMat.SetColor("_BaseColor", Color.white);
                            studioWheelMat.SetFloat("_Metallic", 0f);
                            studioWheelMat.SetFloat("_Smoothness", .18f);
                            if (!motor.AttachWheelModels(Resources.Load<GameObject>(
                                    "AarisVehicles/Models/wheel-default"), studioWheelMat))
                                throw new InvalidOperationException("Sedan wheel art unavailable.");
                        }
                        Vector3 target = bounds.center + new Vector3(0f, .1f, 0f);
                        camera.transform.position = target + new Vector3(4.15f, 2.45f, 6.9f);
                        camera.transform.LookAt(target, Vector3.up);
                        string output = Path.Combine(directory, Names[i] + ".png");
                        WritePng(camera, output);
                        saved.Add(Names[i] + ".png");
                        Debug.Log("AARIS_VEHICLE_STUDIO_CAPTURE " + Names[i] +
                            " bounds=" + bounds.size.ToString("F2") + " path=" + output);
                    }
                    finally
                    {
                        if (wheelRig != null) UnityEngine.Object.DestroyImmediate(wheelRig);
                        if (studioWheelMat != null)
                            UnityEngine.Object.DestroyImmediate(studioWheelMat);
                        UnityEngine.Object.DestroyImmediate(instance);
                    }
                }
                File.WriteAllText(Path.Combine(directory, "README.txt"),
                    "Unity " + Application.unityVersion + " / " +
                    SystemInfo.graphicsDeviceName + Environment.NewLine +
                    "Actual catalog-sized vehicle photographs on a 7 m street under neutral studio lighting." +
                    Environment.NewLine +
                    "These are not in-game spawn, handling, real match lighting, or Android FPS proof." +
                    Environment.NewLine + string.Join(Environment.NewLine, saved));
                if (saved.Count != 8)
                    throw new InvalidOperationException("Expected all 8 current vehicle models.");
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
