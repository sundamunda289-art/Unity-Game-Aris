using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Aaris.MapReview.Editor
{
    // Editor-only remote scene navigation. Reads a validated local JSON command.
    // No arbitrary script execution, no asset/scene writes, no network listeners.
    [InitializeOnLoad]
    public static class AarisLiveCameraBridge
    {
        [Serializable] private sealed class CameraIntent
        {
            public float x, y, z, yaw, pitch, distance;
            public long nonce;
        }
        [Serializable] private sealed class CameraStatus
        {
            public string state;
            public long nonce;
            public string updatedUtc;
        }
        private static readonly string Folder = Path.Combine(Directory.GetCurrentDirectory(), "ci-live-control");
        private static double nextTime;
        static AarisLiveCameraBridge()
        {
            EditorApplication.update += Poll;
        }
        private static void Report(string state, long nonce)
        {
            Directory.CreateDirectory(Folder);
            var report = new CameraStatus { state = state, nonce = nonce,
                updatedUtc = DateTime.UtcNow.ToString("O") };
            File.WriteAllText(Path.Combine(Folder, "status.json"),
                JsonUtility.ToJson(report, true));
        }
        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < nextTime) return;
            nextTime = EditorApplication.timeSinceStartup + 0.6;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlaying) return;
            string file = Path.Combine(Folder, "camera.json");
            if (!File.Exists(file)) return;
            CameraIntent intent = null;
            try
            {
                intent = JsonUtility.FromJson<CameraIntent>(File.ReadAllText(file));
                File.Delete(file);
                if (intent == null || float.IsNaN(intent.x) || float.IsNaN(intent.y) ||
                    float.IsNaN(intent.z) || intent.x < -1000 || intent.x > 1000 ||
                    intent.z < -1000 || intent.z > 1000 ||
                    intent.y < -500 || intent.y > 4000 ||
                    intent.distance < 2 || intent.distance > 1000 ||
                    intent.pitch < -85 || intent.pitch > 85)
                    throw new ArgumentException("Invalid camera coordinates");
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (scene.path != AarisExplorerBuild.ScenePath)
                    EditorSceneManager.OpenScene(AarisExplorerBuild.ScenePath, OpenSceneMode.Single);

                SceneView view = SceneView.lastActiveSceneView;
                if (view == null) view = EditorWindow.GetWindow<SceneView>("Scene");
                view.Show();
                Quaternion rotation = Quaternion.Euler(intent.pitch, intent.yaw, 0f);
                Vector3 cameraPoint = new Vector3(intent.x, intent.y, intent.z);
                Vector3 lookPoint = cameraPoint + rotation * Vector3.forward * intent.distance;
                view.in2DMode = false;
                view.orthographic = false;
                view.LookAtDirect(lookPoint, rotation, intent.distance);
                view.Repaint();
                TakePhoto(cameraPoint, rotation, Path.Combine(Folder, "latest.png"));
                Report("Camera moved and real Unity PNG saved", intent.nonce);
            }
            catch (Exception error)
            {
                Report("Camera error: " + error.GetType().Name + ": " + error.Message,
                    intent == null ? 0 : intent.nonce);
                Debug.LogError("[Aaris live bridge] " + error);
            }
        }
        internal static void TakePhoto(Vector3 point, Quaternion rotation, string path)
        {
            var go = new GameObject("__AarisLivePreviewCamera");
            go.hideFlags = HideFlags.HideAndDontSave;
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 5000f;
            cam.transform.SetPositionAndRotation(point, rotation);
            var target = RenderTexture.GetTemporary(1280, 720, 24, RenderTextureFormat.ARGB32);
            var before = RenderTexture.active;
            var png = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            try
            {
                cam.targetTexture = target;
                cam.Render();
                RenderTexture.active = target;
                png.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                png.Apply(false);
                // A valid PNG header alone does not prove the GPU rendered
                // the scene. Reject almost-uniform black frames explicitly.
                int visible = 0;
                float darkest = 3f, brightest = 0f;
                for (int gy = 0; gy < 9; gy++)
                {
                    for (int gx = 0; gx < 16; gx++)
                    {
                        Color sample = png.GetPixel((gx * 1280 + 640) / 16,
                                                    (gy * 720 + 360) / 9);
                        float brightness = sample.r + sample.g + sample.b;
                        darkest = Mathf.Min(darkest, brightness);
                        brightest = Mathf.Max(brightest, brightness);
                        if (brightness > 0.08f) visible++;
                    }
                }
                if (visible == 0)
                    throw new InvalidOperationException(
                        "Unity rendered a black frame; check scene, URP, camera or graphics driver");
                if (brightest - darkest < 0.05f)
                    throw new InvalidOperationException(
                        "Unity rendered an almost-uniform frame (black, white or flat). " +
                        "Check Editor scene, camera location, lighting and URP before publishing.");
                var bytes = png.EncodeToPNG();
                if (bytes.Length < 5000) throw new InvalidOperationException("Empty renderer output");
                File.WriteAllBytes(path, bytes);
            }
            finally
            {
                cam.targetTexture = null;
                RenderTexture.active = before;
                UnityEngine.Object.DestroyImmediate(png);
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
