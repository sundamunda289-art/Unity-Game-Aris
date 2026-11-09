using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Aaris.MapReview.Editor
{
    // Editor-only hot camera queue. No arbitrary shell execution, network listener,
    // prefab/material modifications, scene saving or Unity account access.
    // Legacy ci-live-control/camera.json and latest.png remain supported.
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
        [Serializable] private sealed class EditorHealth
        {
            public string state;
            public string scene;
            public string graphics;
            public long epochUtc;
        }
        [Serializable] private sealed class Shot
        {
            public string id;
            public float x, y, z, lookX, lookY, lookZ;
            public int width, height;
        }
        [Serializable] private sealed class SnapshotRequest
        {
            public string requestId;
            public Shot[] shots;
        }
        [Serializable] private sealed class FrameResult
        {
            public string id;
            public string state;
            public string file;
            public string reason;
            public float magentaFraction;
        }
        [Serializable] private sealed class SnapshotResult
        {
            public string requestId;
            public string state;
            public string reason;
            public string scene;
            public string graphics;
            public string unityVersion;
            public string utc;
            public FrameResult[] frames;
        }

        private static readonly string Folder = Path.Combine(Directory.GetCurrentDirectory(), "ci-live-control");
        private static readonly string Queue = Path.Combine(Folder, "snapshot-queue");
        private static readonly string Results = Path.Combine(Folder, "snapshot-results");
        private static readonly string Photos = Path.Combine(Folder, "snapshot-photos");
        private static readonly Regex RequestName = new Regex(@"^snap-[0-9a-f]{32}$", RegexOptions.CultureInvariant);
        private static readonly Regex ShotName = new Regex(@"^[A-Za-z][A-Za-z0-9_-]{0,47}$", RegexOptions.CultureInvariant);
        private static Camera reusableCamera;
        private static GameObject cameraObject;
        private static RenderTexture reusableTexture;
        private static double nextPoll, nextHeartbeat;

        static AarisLiveCameraBridge()
        {
            EditorApplication.update += Tick;
            EditorApplication.quitting += Cleanup;
        }

        private static void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now >= nextHeartbeat)
            {
                nextHeartbeat = now + 2.0;
                string state = EditorApplication.isCompiling || EditorApplication.isUpdating
                    ? "importing" : EditorApplication.isPlayingOrWillChangePlaymode
                    ? "playing" : SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null
                    ? "null_graphics" : "ready";
                try
                {
                    Directory.CreateDirectory(Folder);
                    WriteAtomic(Path.Combine(Folder, "snap-health.json"),
                        JsonUtility.ToJson(new EditorHealth
                        {
                            state = state,
                            scene = SceneManager.GetActiveScene().path,
                            graphics = SystemInfo.graphicsDeviceType.ToString(),
                            epochUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        }));
                }
                catch (Exception e) { Debug.LogWarning("[Aaris snapshots] heartbeat failed: " + e.Message); }
            }
            if (now < nextPoll) return;
            nextPoll = now + 0.25;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode) return;
            try
            {
                if (File.Exists(Path.Combine(Folder, "camera.json")))
                    ProcessLegacy();
                if (Directory.Exists(Queue))
                {
                    string next = Directory.GetFiles(Queue, "snap-*.json")
                        .OrderBy(File.GetCreationTimeUtc).FirstOrDefault();
                    if (next != null) ProcessBatch(next);
                }
            }
            catch (Exception e) { Debug.LogError("[Aaris snapshots] " + e); }
        }

        private static void ProcessLegacy()
        {
            string path = Path.Combine(Folder, "camera.json");
            CameraIntent intent = null;
            try
            {
                intent = JsonUtility.FromJson<CameraIntent>(File.ReadAllText(path));
                File.Delete(path);
                if (intent == null || !Inside(intent.x, intent.y, intent.z) ||
                    !ValidNumber(intent.yaw) || !ValidNumber(intent.pitch) ||
                    !ValidNumber(intent.distance) || Math.Abs(intent.yaw) > 360 ||
                    Math.Abs(intent.pitch) > 85 || intent.distance < 2 || intent.distance > 1000)
                    throw new ArgumentException("Invalid legacy camera coordinates");
                EnsureScene();
                Vector3 point = new Vector3(intent.x, intent.y, intent.z);
                Quaternion rotation = Quaternion.Euler(intent.pitch, intent.yaw, 0f);
                SceneView view = SceneView.lastActiveSceneView;
                if (view == null) view = EditorWindow.GetWindow<SceneView>("Scene");
                view.Show();
                view.in2DMode = false;
                view.orthographic = false;
                view.LookAtDirect(point + rotation * Vector3.forward * intent.distance,
                    rotation, intent.distance);
                view.Repaint();
                Camera camera = EnsureCamera();
                camera.transform.SetPositionAndRotation(point, rotation);
                float pink;
                string warning = Capture(camera, 1280, 720,
                    Path.Combine(Folder, "latest.png"), out pink);
                LegacyStatus("Camera moved and real Unity PNG saved" +
                    (warning == "" ? "" : "; " + warning), intent.nonce);
            }
            catch (Exception e)
            {
                LegacyStatus("Camera error: " + e.GetType().Name + ": " + e.Message,
                    intent == null ? 0 : intent.nonce);
                Debug.LogError("[Aaris live camera] " + e);
            }
        }

        private static void ProcessBatch(string source)
        {
            // Move once to a visible .processing marker. A native crash must NOT
            // replay an identical crashing request indefinitely after restart.
            string processing = source + ".processing";
            File.Move(source, processing);
            string requestId = Path.GetFileNameWithoutExtension(source);
            var result = new SnapshotResult
            {
                requestId = requestId, state = "failed", reason = "",
                utc = DateTime.UtcNow.ToString("O"),
                unityVersion = Application.unityVersion,
                graphics = SystemInfo.graphicsDeviceType.ToString(),
                frames = new FrameResult[0]
            };
            try
            {
                var intent = JsonUtility.FromJson<SnapshotRequest>(File.ReadAllText(processing));
                if (intent == null || !RequestName.IsMatch(requestId) ||
                    intent.requestId != requestId || intent.shots == null ||
                    intent.shots.Length == 0 || intent.shots.Length > 20)
                    throw new ArgumentException("Malformed snapshot request");
                if (intent.shots.Any(s => s == null || !ValidShot(s)) ||
                    intent.shots.Select(s => s.id).Distinct().Count() != intent.shots.Length)
                    throw new ArgumentException("Invalid or duplicate snapshot shot");
                EnsureScene();
                result.scene = SceneManager.GetActiveScene().path;
                Camera camera = EnsureCamera();
                string directory = Path.Combine(Photos, requestId);
                Directory.CreateDirectory(directory);
                var frames = new FrameResult[intent.shots.Length];
                for (int i = 0; i < intent.shots.Length; i++)
                {
                    Shot shot = intent.shots[i];
                    var frame = new FrameResult { id = shot.id, state = "failed", reason = "", file = "" };
                    frames[i] = frame;
                    try
                    {
                        camera.transform.position = new Vector3(shot.x, shot.y, shot.z);
                        camera.transform.LookAt(new Vector3(shot.lookX, shot.lookY, shot.lookZ), Vector3.up);
                        string file = Path.Combine(directory, shot.id + ".png");
                        float magenta;
                        string warning = Capture(camera, shot.width, shot.height, file, out magenta);
                        frame.magentaFraction = magenta;
                        frame.file = "snapshot-photos/" + requestId + "/" + shot.id + ".png";
                        frame.state = warning == "" ? "passed" : "suspect";
                        frame.reason = warning;
                    }
                    catch (Exception e)
                    {
                        frame.reason = e.GetType().Name + ": " + e.Message;
                        Debug.LogWarning("[Aaris snapshots] " + shot.id + ": " + e.Message);
                    }
                }
                result.frames = frames;
                result.state = frames.Any(f => f.state == "failed") ? "failed" :
                    frames.Any(f => f.state == "suspect") ? "suspect" : "passed";
            }
            catch (Exception e)
            {
                result.reason = e.GetType().Name + ": " + e.Message;
                Debug.LogError("[Aaris snapshots] " + result.reason);
            }
            finally
            {
                try
                {
                    Directory.CreateDirectory(Results);
                    WriteAtomic(Path.Combine(Results, requestId + ".json"),
                        JsonUtility.ToJson(result, true));
                    File.Delete(processing);
                }
                catch (Exception e) { Debug.LogError("[Aaris snapshots] result write: " + e); }
            }
        }

        private static bool ValidNumber(float x) { return !float.IsNaN(x) && !float.IsInfinity(x); }
        private static bool Inside(float x, float y, float z)
        {
            return ValidNumber(x) && ValidNumber(y) && ValidNumber(z) &&
                x >= -1000 && x <= 1000 && y >= -500 && y <= 4000 && z >= -1000 && z <= 1000;
        }
        private static bool ValidShot(Shot s)
        {
            if (s.id == null || !ShotName.IsMatch(s.id) ||
                !Inside(s.x, s.y, s.z) || !Inside(s.lookX, s.lookY, s.lookZ) ||
                s.width < 320 || s.width > 1920 || s.height < 180 ||
                s.height > 1080 || (long)s.width * s.height > 2073600)
                return false;
            Vector3 direction = new Vector3(s.lookX - s.x, s.lookY - s.y, s.lookZ - s.z);
            return direction.sqrMagnitude > 0.0001f;
        }

        private static void EnsureScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.path != AarisExplorerBuild.ScenePath)
            {
                if (scene.isDirty)
                    throw new InvalidOperationException("Refusing to discard an unsaved scene");
                EditorSceneManager.OpenScene(AarisExplorerBuild.ScenePath, OpenSceneMode.Single);
            }
            Terrain[] tiles = UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None);
            if (tiles.Length != 4 || tiles.Any(t => t.terrainData == null))
                throw new InvalidOperationException("Four native TerrainData tiles missing; verify Git LFS");
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new InvalidOperationException("No graphics device: -nographics cannot take Unity PNGs");
        }

        private static Camera EnsureCamera()
        {
            if (reusableCamera != null) return reusableCamera;
            cameraObject = new GameObject("__AarisReusableSnapshotCamera");
            cameraObject.hideFlags = HideFlags.HideAndDontSave;
            reusableCamera = cameraObject.AddComponent<Camera>();
            reusableCamera.enabled = false;
            reusableCamera.clearFlags = CameraClearFlags.Skybox;
            reusableCamera.fieldOfView = 65f;
            reusableCamera.nearClipPlane = 0.1f;
            reusableCamera.farClipPlane = 5500f;
            reusableCamera.allowHDR = true;
            return reusableCamera;
        }

        private static RenderTexture EnsureTexture(int width, int height)
        {
            if (reusableTexture != null &&
                (reusableTexture.width != width || reusableTexture.height != height))
            {
                reusableTexture.Release();
                UnityEngine.Object.DestroyImmediate(reusableTexture);
                reusableTexture = null;
            }
            if (reusableTexture == null)
            {
                reusableTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                reusableTexture.name = "__AarisReusableSnapshotTarget";
                reusableTexture.hideFlags = HideFlags.HideAndDontSave;
                if (!reusableTexture.Create())
                    throw new InvalidOperationException("Could not create render texture");
            }
            return reusableTexture;
        }

        private static string Capture(Camera camera, int width, int height, string filename, out float magenta)
        {
            magenta = 0f;
            var renderTarget = EnsureTexture(width, height);
            var request = new UniversalRenderPipeline.SingleCameraRequest { destination = renderTarget };
            if (!RenderPipeline.SupportsRenderRequest(camera, request))
                throw new InvalidOperationException("URP SingleCameraRequest not supported; check graphics / URP");
            RenderTexture previous = RenderTexture.active;
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                // URP 17: renders the original shaders/materials, not a replacement shader.
                RenderPipeline.SubmitRenderRequest(camera, request);
                RenderTexture.active = renderTarget;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                texture.Apply(false);
                string warning = InspectPixels(texture, width, height, out magenta);
                byte[] bytes = texture.EncodeToPNG();
                if (bytes == null || bytes.Length < 5000)
                    throw new InvalidOperationException("PNG unexpectedly small");
                Directory.CreateDirectory(Path.GetDirectoryName(filename));
                string temp = filename + ".tmp";
                try
                {
                    File.WriteAllBytes(temp, bytes);
                    if (File.Exists(filename)) File.Delete(filename);
                    File.Move(temp, filename);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                return warning;
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        // Fast sampling protects against false "success" for black/white/flat PNGs.
        // Strong magenta is reported as a suspect material, NEVER shader-hidden.
        private static string InspectPixels(Texture2D texture, int width, int height, out float magenta)
        {
            int count = 0, black = 0, white = 0, pink = 0;
            double sum = 0, sumSquares = 0;
            for (int row = 0; row < 12; row++)
            for (int col = 0; col < 20; col++)
            {
                Color32 pixel = texture.GetPixel(width * (col * 2 + 1) / 40,
                    height * (row * 2 + 1) / 24);
                int bright = (pixel.r + pixel.g + pixel.b) / 3;
                count++;
                sum += bright;
                sumSquares += (double)bright * bright;
                if (pixel.r < 18 && pixel.g < 18 && pixel.b < 18) black++;
                if (pixel.r > 242 && pixel.g > 242 && pixel.b > 242) white++;
                if (pixel.r > 170 && pixel.b > 150 && pixel.g < 110 &&
                    pixel.r > pixel.g * 1.7f && pixel.b > pixel.g * 1.5f) pink++;
            }
            double variance = sumSquares / count - Math.Pow(sum / count, 2);
            if (black >= count * 0.93 || white >= count * 0.93 || variance < 9)
                throw new InvalidOperationException("Suspicious blank/black/white Unity frame");
            magenta = (float)pink / count;
            return magenta >= 0.05f ? "suspect_magenta_materials" : "";
        }

        private static void LegacyStatus(string state, long nonce)
        {
            Directory.CreateDirectory(Folder);
            WriteAtomic(Path.Combine(Folder, "status.json"),
                JsonUtility.ToJson(new CameraStatus
                {
                    state = state, nonce = nonce, updatedUtc = DateTime.UtcNow.ToString("O")
                }, true));
        }
        private static void WriteAtomic(string destination, string content)
        {
            string temp = destination + ".writing";
            File.WriteAllText(temp, content);
            if (File.Exists(destination)) File.Replace(temp, destination, null);
            else File.Move(temp, destination);
        }
        private static void Cleanup()
        {
            if (reusableTexture != null)
            {
                reusableTexture.Release();
                UnityEngine.Object.DestroyImmediate(reusableTexture);
            }
            if (cameraObject != null) UnityEngine.Object.DestroyImmediate(cameraObject);
            reusableTexture = null;
            reusableCamera = null;
            cameraObject = null;
        }
    }
}
