#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Small native Play Mode screenshot fixture for REAL bundled weapon FBXs.
    /// This is not a screenshot of the authored 2km battle map. It isolates
    /// visual mesh floor contact from the CPU cost of rendering that entire map.
    /// No asset or scene saves. Output: /tmp/aaris-weapon-photo-fixture.
    /// </summary>
    [InitializeOnLoad]
    public static class AarisWeaponPhotoFixtureQA
    {
        private const string Key = "AarisWeaponPhotoFixtureQA20261121";
        private static readonly string Folder =
            Path.Combine(Path.GetTempPath(), "aaris-weapon-photo-fixture");

        static AarisWeaponPhotoFixtureQA()
        {
            EditorApplication.update += Tick;
        }

        [MenuItem("Aaris/CI/Native actual mesh floor-contact Play Mode photographs")]
        public static void Run()
        {
            Directory.CreateDirectory(Folder);
            SessionState.SetInt(Key, 0);
            SessionState.SetString(Key + "Report",
                "UNITY=" + Application.unityVersion + "\n");
            SessionState.SetFloat(Key + "Deadline",
                (float)EditorApplication.timeSinceStartup + 180f);
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode ||
                    EditorApplication.isCompiling)
                    throw new InvalidOperationException("EDITOR_BUSY");
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                SessionState.SetInt(Key, 1);
                EditorApplication.isPlaying = true;
            }
            catch (Exception ex) { Finish(false, "PREFLIGHT_" + ex.Message); }
        }

        private static Material Paint(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) throw new InvalidOperationException("NO_LIT_SHADER");
            return new Material(shader) { color = color };
        }

        private static GameObject Floor(string name, Vector3 position,
            Vector3 scale, Color paint)
        {
            GameObject obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            obj.name = name;
            obj.transform.position = position;
            obj.transform.localScale = scale;
            obj.GetComponent<MeshRenderer>().sharedMaterial = Paint(paint);
            return obj;
        }

        private static void PlacePickup(string name, Vector3 worldPosition, int seed)
        {
            AarisWeaponSpec spec = AarisWeaponCatalog.Get("AssaultRifle");
            if (spec == null) throw new InvalidOperationException("ASSAULT_RIFLE_UNAVAILABLE");
            var root = new GameObject(name);
            root.transform.position = worldPosition;
            AarisWeaponPickup pickup = root.AddComponent<AarisWeaponPickup>();
            if (!pickup.Initialize(new AarisWeaponState(spec, (uint)seed), seed))
                throw new InvalidOperationException("PICKUP_INITIALIZE_FAILED_" + name);
            int visibleRenderers = 0;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                if (r != null && r.enabled) visibleRenderers++;
            if (visibleRenderers == 0)
                throw new InvalidOperationException("NO_VISIBLE_FBX_" + name);
        }

        private static void SetupFixture()
        {
            if (!AarisWeaponCatalog.TryLoad())
                throw new InvalidOperationException("WEAPON_CATALOG_UNAVAILABLE");
            TerrainData data = new TerrainData();
            data.heightmapResolution = 33;
            data.size = new Vector3(64f, 10f, 64f);
            Terrain terrain = Terrain.CreateTerrainGameObject(data).GetComponent<Terrain>();
            terrain.name = "Native bare terrain fixture";
            terrain.transform.position = new Vector3(-32f, 0f, -32f);
            PlacePickup("QA_Terrain_Gun", new Vector3(-6f, .28f, 0f), 10);

            // Road and indoor top surfaces are y=0.13. The previously
            // PhysX-verified production root support is y=0.17.
            Floor("QA Paved Road", new Vector3(0f, .08f, 0f),
                new Vector3(4f, .10f, 4f), new Color(.36f, .38f, .39f));
            PlacePickup("QA_Road_Gun", new Vector3(0f, .17f, 0f), 11);

            Floor("QA Indoor Floor", new Vector3(6f, .08f, 0f),
                new Vector3(4f, .10f, 4f), new Color(.55f, .46f, .37f));
            Floor("QA Indoor Roof", new Vector3(6f, 2.6f, 0f),
                new Vector3(4f, .10f, 4f), new Color(.44f, .43f, .42f));
            PlacePickup("QA_Indoor_Gun", new Vector3(6f, .17f, 0f), 12);

            GameObject lightObj = new GameObject("QA Sun");
            Light sun = lightObj.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.3f;
            sun.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
            RenderSettings.ambientLight = new Color(.59f, .62f, .68f);
            GameObject camObj = new GameObject("QA Main Camera", typeof(Camera));
            camObj.tag = "MainCamera";
            Camera cam = camObj.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(.15f, .20f, .24f);
            cam.nearClipPlane = .03f;
            cam.farClipPlane = 12f;
            cam.fieldOfView = 52f;
            SessionState.SetInt(Key + "FrameStart", Time.frameCount);
        }

        private static void Capture(string label, Vector3 root)
        {
            Camera cam = Camera.main;
            if (cam == null) throw new InvalidOperationException("NO_CAMERA");
            const int width = 960, height = 540;
            Vector3 focus = root + Vector3.up * .18f;
            cam.transform.position = focus + new Vector3(.45f, 1.15f, -2.6f);
            cam.transform.rotation = Quaternion.LookRotation(
                focus - cam.transform.position, Vector3.up);
            RenderTexture previous = cam.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture rt = RenderTexture.GetTemporary(width, height, 24);
            Texture2D photo = null;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                photo = new Texture2D(width, height, TextureFormat.RGB24, false);
                photo.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                photo.Apply(false);
                byte[] bytes = photo.EncodeToJPG(88);
                if (bytes == null || bytes.Length < 5000)
                    throw new InvalidOperationException("BLANK_OR_INVALID_JPEG_" + label);
                File.WriteAllBytes(Path.Combine(Folder, label + ".jpg"), bytes);
                Append("PHOTO_" + label + "_bytes=" + bytes.Length);
            }
            finally
            {
                cam.targetTexture = previous;
                RenderTexture.active = previousActive;
                if (photo != null) UnityEngine.Object.DestroyImmediate(photo);
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static void Append(string s)
        {
            SessionState.SetString(Key + "Report",
                SessionState.GetString(Key + "Report", "") + s + "\n");
        }

        private static void Finish(bool ok, string message)
        {
            if (!string.IsNullOrEmpty(message)) Append(message);
            Append("RESULT=" + (ok ? "PASS" : "FAIL"));
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, "report.txt"),
                SessionState.GetString(Key + "Report", ""));
            SessionState.SetInt(Key + "Exit", ok ? 0 : 1);
            SessionState.SetInt(Key, 9);
            if (EditorApplication.isPlaying)
                EditorApplication.isPlaying = false;
            else if (Application.isBatchMode)
                EditorApplication.Exit(ok ? 0 : 1);
        }

        private static void Tick()
        {
            int stage = SessionState.GetInt(Key, 0);
            if (stage == 0) return;
            if (stage == 9)
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                SessionState.SetInt(Key, 0);
                if (Application.isBatchMode)
                    EditorApplication.Exit(SessionState.GetInt(Key + "Exit", 1));
                return;
            }
            if (EditorApplication.timeSinceStartup >
                SessionState.GetFloat(Key + "Deadline", 0f))
            { Finish(false, "TIMEOUT"); return; }
            if (!EditorApplication.isPlaying) return;
            try
            {
                if (stage == 1)
                {
                    SetupFixture();
                    SessionState.SetInt(Key, 2);
                    return;
                }
                if (stage != 2 || Time.frameCount <
                    SessionState.GetInt(Key + "FrameStart", 0) + 8) return;
                Capture("Terrain_RealWeaponFBX", new Vector3(-6f, .28f, 0f));
                Capture("Road_RealWeaponFBX", new Vector3(0f, .17f, 0f));
                Capture("Indoor_RealWeaponFBX", new Vector3(6f, .17f, 0f));
                Finish(true, "THREE_REAL_UNITY_PLAYMODE_IMAGES");
            }
            catch (Exception ex)
            { Finish(false, ex.GetType().Name + "_" + ex.Message); }
        }
    }
}
#endif
