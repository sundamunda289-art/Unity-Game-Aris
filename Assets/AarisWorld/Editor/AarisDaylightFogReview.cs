using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// ONE-TIME REVIEW HARNESS. Renders the exact saved Unity scene with fog
// on/off in memory, then restores it; NEVER saves source scene/assets.
namespace Aaris.MapReview.Editor
{
    public static class AarisDaylightFogReview
    {
        private const string Scene = AarisExplorerBuild.ScenePath;
        private const string Output = "/tmp/aaris-codespaces/daylight-preview";

        [Serializable] private sealed class ShotReport
        {
            public string name;
            public float x, z, cameraY;
            public string baselineImage, clearImage;
            public float baselineMean, clearMean;
            public float baselineDarkFraction, clearDarkFraction;
            public float baselineVariance, clearVariance;
            public float baselinePinkFraction, clearPinkFraction;
        }

        [Serializable] private sealed class Report
        {
            public string status, reason, scene, unityVersion, graphicsDevice;
            public bool nativeFogDisabled;
            public float sunIntensity, ambientIntensity, nativeFogDensity;
            public int terrainTiles;
            public ShotReport[] shots;
        }

        private struct ImageMetrics
        {
            public float mean, dark, variance, pink;
        }

        [MenuItem("Aaris/CI/Review exact native daylight with fog A-B")]
        public static void Capture()
        {
            Directory.CreateDirectory(Output);
            Report report = new Report { status = "failed", reason = "not_started", scene = Scene,
                unityVersion = Application.unityVersion,
                graphicsDevice = SystemInfo.graphicsDeviceType.ToString(),
                shots = new ShotReport[0] };
            GameObject tempCamera = null;
            bool changedFog = false;
            bool previousFog = false;
            try
            {
                var scene = EditorSceneManager.OpenScene(Scene, OpenSceneMode.Single);
                if (!scene.IsValid() || !scene.isLoaded || scene.path != Scene)
                    throw new InvalidOperationException("NATIVE_SCENE_NOT_LOADED");
                var terrains = UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None);
                report.terrainTiles = terrains.Length;
                if (terrains.Length != 4 || terrains.Any(t => t.terrainData == null))
                    throw new InvalidOperationException("NATIVE_TERRAIN_DATA_MISSING");
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                    throw new InvalidOperationException("NO_GRAPHICS_DEVICE");
                if (RenderSettings.sun == null || RenderSettings.skybox == null ||
                    RenderSettings.skybox.shader == null)
                    throw new InvalidOperationException("DAYLIGHT_SKY_OR_SUN_MISSING");
                report.sunIntensity = RenderSettings.sun.intensity;
                report.ambientIntensity = RenderSettings.ambientIntensity;
                if (report.sunIntensity < 1.3f || RenderSettings.sun.shadows == LightShadows.None)
                    throw new InvalidOperationException("SAVED_SUN_OR_SHADOWS_NOT_PRESERVED");
                if (report.ambientIntensity < 1.15f)
                    throw new InvalidOperationException("SAVED_AMBIENT_TOO_DIM");
                previousFog = RenderSettings.fog;
                report.nativeFogDisabled = !previousFog;
                report.nativeFogDensity = RenderSettings.fogDensity;
                changedFog = true;
                tempCamera = new GameObject("__AarisDaylightComparisonCamera");
                tempCamera.hideFlags = HideFlags.HideAndDontSave;
                var camera = tempCamera.AddComponent<Camera>();
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.fieldOfView = 60f;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 4000f;
                var results = new List<ShotReport>();
                foreach (var item in new[]
                {
                    new { name = "forest", x = -420f, z = -210f },
                    new { name = "clocktower", x = 350f, z = 220f }
                })
                {
                    Terrain t = terrains.FirstOrDefault(a =>
                        item.x >= a.transform.position.x &&
                        item.x <= a.transform.position.x + a.terrainData.size.x &&
                        item.z >= a.transform.position.z &&
                        item.z <= a.transform.position.z + a.terrainData.size.z);
                    if (t == null) throw new InvalidOperationException("TARGET_TERRAIN_MISSING");
                    // Terrain.SampleHeight returns world-space elevation (Y already included).
                    float groundY = t.SampleHeight(new Vector3(item.x, 0f, item.z));
                    camera.transform.position = new Vector3(item.x + 28f, groundY + 58f, item.z - 95f);
                    camera.transform.LookAt(new Vector3(item.x, groundY + 9f, item.z + 250f), Vector3.up);
                    var shot = new ShotReport { name = item.name, x = item.x,
                        z = item.z, cameraY = camera.transform.position.y,
                        baselineImage = item.name + "-fog-on.png",
                        clearImage = item.name + "-fog-off.png" };

                    RenderSettings.fog = true;
                    var before = RenderFrame(camera, Path.Combine(Output, shot.baselineImage));
                    RenderSettings.fog = false;
                    var after = RenderFrame(camera, Path.Combine(Output, shot.clearImage));
                    shot.baselineMean = before.mean;
                    shot.clearMean = after.mean;
                    shot.baselineDarkFraction = before.dark;
                    shot.clearDarkFraction = after.dark;
                    shot.baselineVariance = before.variance;
                    shot.clearVariance = after.variance;
                    shot.baselinePinkFraction = before.pink;
                    shot.clearPinkFraction = after.pink;
                    results.Add(shot);
                }
                report.shots = results.ToArray();
                report.status = "passed";
                report.reason = "FOUR_REAL_URP_PNGS_VALID_NONBLANK";
                Debug.Log("[Aaris Fog Review] Four real native URP fog A/B images rendered");
            }
            catch (Exception error)
            {
                report.reason = error is InvalidOperationException ? error.Message : error.GetType().Name;
                Debug.LogError("[Aaris Fog Review] " + report.reason);
                throw;
            }
            finally
            {
                if (changedFog) RenderSettings.fog = previousFog;
                if (tempCamera != null) UnityEngine.Object.DestroyImmediate(tempCamera);
                File.WriteAllText(Path.Combine(Output, "daylight-report.json"),
                    JsonUtility.ToJson(report, true));
            }
        }

        private static ImageMetrics RenderFrame(Camera camera, string destination)
        {
            const int width = 1280, height = 720;
            RenderTexture previousRT = RenderTexture.active;
            RenderTexture priorTarget = camera.targetTexture;
            RenderTexture target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                texture.Apply(false);
                Color32[] pixels = texture.GetPixels32();
                double sum = 0, sumSq = 0;
                int count = 0, dark = 0, pink = 0;
                int stride = Math.Max(1, pixels.Length / 5000);
                for (int i = 0; i < pixels.Length; i += stride)
                {
                    Color32 c = pixels[i];
                    float lum = c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;
                    sum += lum;
                    sumSq += lum * lum;
                    count++;
                    if (lum < 48f) dark++;
                    if (c.r > 195 && c.g < 95 && c.b > 195) pink++;
                }
                float mean = (float)(sum / count);
                float variance = (float)Math.Sqrt(Math.Max(0, sumSq / count - mean * mean));
                if (variance < 6f || dark > count * 0.93f)
                    throw new InvalidOperationException("FRAME_BLACK_OR_NEAR_UNIFORM");
                byte[] png = texture.EncodeToPNG();
                if (png == null || png.Length < 8192)
                    throw new InvalidOperationException("FRAME_PNG_TOO_SMALL");
                File.WriteAllBytes(destination, png);
                return new ImageMetrics { mean = mean, dark = (float)dark / count,
                    variance = variance, pink = (float)pink / count };
            }
            finally
            {
                camera.targetTexture = priorTarget;
                RenderTexture.active = previousRT;
                UnityEngine.Object.DestroyImmediate(texture);
                RenderTexture.ReleaseTemporary(target);
            }
        }
    }
}
