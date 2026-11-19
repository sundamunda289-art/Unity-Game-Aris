using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Aaris.MapReview.Editor
{
    public sealed class AarisExplorerBuild : IPreprocessBuildWithReport
    {
        public const string ScenePath = "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";
        internal const string AndroidVersionName = "0.3.3";
        internal const int AndroidVersionCode = 20261116;
        internal const AndroidArchitecture AndroidArchitectures = AndroidArchitecture.ARMv7 | AndroidArchitecture.ARM64;
        private static bool buildSceneValidated;

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            // Movement QA uses an independent one-scene Android test APK.
            // Never run native-map validation or rewrite the gym's build settings.
            if (AarisLocomotionGymBuilder.IsIsolatedGymBuild(
                    report.summary.outputPath)) return;
            if (buildSceneValidated) return;
            Configure();
            ValidateSavedScene();
        }

        internal static BuildReport BuildValidatedPlayer(BuildPlayerOptions options)
        {
            Configure();
            ValidateSavedScene();
            buildSceneValidated = true;
            try
            {
                return BuildPipeline.BuildPlayer(options);
            }
            finally
            {
                buildSceneValidated = false;
            }
        }

        [MenuItem("Aaris/Build Android Aaris Game APK")]
        public static void BuildAndroid()
        {
            Configure();
            EditorUserBuildSettings.buildAppBundle = false;
            EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ETC2;
            Build("Builds/Android/AarisGame.apk", BuildTarget.Android);
        }

        [MenuItem("Aaris/Build Web Map Explorer")]
        public static void BuildWebGL() { Configure(); Build("Builds/WebGL", BuildTarget.WebGL); }

        public static void Configure()
        {
            PlayerSettings.companyName = "Aaris";
            PlayerSettings.productName = "Aaris Game";
            PlayerSettings.bundleVersion = AndroidVersionName;
            PlayerSettings.Android.bundleVersionCode = AndroidVersionCode;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android, "com.aaris.game");
            // A single sideloadable APK supports both Android ARM userlands.
            PlayerSettings.Android.targetArchitectures = AndroidArchitectures;
            PlayerSettings.Android.buildApkPerCpuArchitecture = false;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel33;
            // A reproducible GLES3 renderer/ETC2 pair avoids silently selecting a different
            // mobile graphics driver on each phone. HDR and render scale remain unchanged.
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
            EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ETC2;
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            // The review UI uses the built-in Input API; no Input Actions asset is required.
            UnityEngine.Object[] settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (settings.Length > 0)
            {
                var serialized = new SerializedObject(settings[0]);
                var input = serialized.FindProperty("activeInputHandler");
                if (input != null) { input.intValue = 0; serialized.ApplyModifiedPropertiesWithoutUndo(); }
            }
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        public static void ValidateSavedScene()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();
            var player = SceneComponents<AarisThirdPersonPlayer>(roots).FirstOrDefault();
            if (player == null || !player.enabled || player.GetComponent<CharacterController>() == null)
                throw new BuildFailedException("The saved scene must contain the grounded third-person player.");
            if (!SceneComponents<AarisMobileRuntime>(roots).Any() || !SceneComponents<AarisNearbyColliders>(roots).Any())
                throw new BuildFailedException("The saved scene must include mobile memory diagnostics and bounded building physics.");
            var binding = new SerializedObject(player).FindProperty("followCamera");
            if (binding == null || !(binding.objectReferenceValue is Camera camera))
                throw new BuildFailedException("The saved player must reference the shoulder camera.");
            var listener = camera.GetComponent<AudioListener>();
            if (listener == null || !listener.enabled)
                throw new BuildFailedException("The saved gameplay camera must have an enabled AudioListener.");
            foreach (string cue in AarisAudioDirector.RequiredCueKeys)
                if (Resources.Load<AudioClip>("SFX/" + cue) == null)
                    throw new BuildFailedException("Missing imported gameplay audio: Resources/SFX/" + cue);
            if (Mathf.Abs(player.GetComponent<CharacterController>().height - 1.78f) > 0.01f)
                throw new BuildFailedException("The review player's reference height must stay 1.78 m.");
            if (LayerMask.NameToLayer("Player") != 8 || player.GetComponentsInChildren<Transform>(true).Any(t => t.gameObject.layer != 8))
                throw new BuildFailedException("The saved player must use the dedicated Player layer (8) for camera/support queries.");
            var rendererData = AssetDatabase.LoadMainAssetAtPath("Assets/TerrainDemoScene_URP/Settings/URP/Universal Render Pipeline Asset_Renderer.asset");
            if (rendererData == null) throw new BuildFailedException("The saved URP renderer is missing; check Git LFS.");
            var opaqueLayers = new SerializedObject(rendererData).FindProperty("m_OpaqueLayerMask");
            if (opaqueLayers == null || (opaqueLayers.intValue & (1 << 8)) == 0)
                throw new BuildFailedException("The URP opaque renderer excludes the saved Player layer.");
            string nativeReport = "Docs/GROUND_REVIEW_REPORT.json";
            if (!File.Exists(nativeReport) || !JsonUtility.FromJson<NativeReport>(File.ReadAllText(nativeReport)).native_assets_materialized)
                throw new BuildFailedException("Materialize the native forest and building LOD assets before building.");
            if (RenderSettings.skybox == null || RenderSettings.skybox.shader.name != "Skybox/Procedural")
                throw new BuildFailedException("The saved daylight sky material is missing or uses the wrong shader.");
            string worldReport = "Docs/WORLD_REVIEW_REPORT.json";
            if (!File.Exists(worldReport) || !JsonUtility.FromJson<NativeReport>(File.ReadAllText(worldReport)).native_assets_materialized)
                throw new BuildFailedException("Saved world proportion assets are missing. Check out the complete current project and its Git LFS assets.");
            if (RenderSettings.sun == null || RenderSettings.sun.intensity < 1.3f || !camera.CompareTag("MainCamera") || camera.clearFlags != CameraClearFlags.Skybox)
                throw new BuildFailedException("The saved daylight sun and skybox camera must be present.");
            foreach (string asset in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/AarisWorld/Models" }))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(asset));
                if (prefab == null || prefab.GetComponent<LODGroup>() == null || prefab.GetComponent<LODGroup>().GetLODs().Length != 3)
                    throw new BuildFailedException("A saved building prefab is missing its three integrated LODs.");
            }
            foreach (string texture in new[] { "Asphalt", "Concrete", "Sand", "RoadMark" })
                if (!(AssetImporter.GetAtPath("Assets/AarisWorld/Surfaces/" + texture + ".png") is TextureImporter))
                    throw new BuildFailedException("A surface PNG has the wrong importer: " + texture);
            foreach (LODGroup lod in SceneComponents<LODGroup>(roots))
                if (!lod.name.StartsWith("AarisPlayer", StringComparison.Ordinal) && lod.GetLODs().Length < 3)
                    throw new BuildFailedException("Building is missing its saved near/middle/far LODs: " + lod.name);
            var terrains = SceneComponents<Terrain>(roots).ToArray();
            if (terrains.Length != 4 || terrains.Any(t => t.terrainData == null))
                throw new BuildFailedException("Expected the four saved 1km TerrainData tiles; check Git LFS downloads.");
            var spawns = SceneComponents<Transform>(roots)
                .Count(t => t.parent != null && t.parent.name == "VehicleSpawns"
                    && t.name.StartsWith("VehicleSpawnPoint_", StringComparison.Ordinal));
            if (spawns != 45) throw new BuildFailedException("Expected all 45 empty vehicle spawn markers.");
            foreach (var root in roots)
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                    if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject) != 0)
                        throw new BuildFailedException("Missing script on " + transform.name);
            Debug.Log("Aaris ground review verified: human player, daylight, saved forest/LODs, four terrains, 45 markers.");
        }

        private static System.Collections.Generic.IEnumerable<T> SceneComponents<T>(GameObject[] roots)
            where T : Component
        {
            return roots.SelectMany(root => root.GetComponentsInChildren<T>())
                .Where(component => component.gameObject.activeInHierarchy);
        }

        [Serializable]
        private sealed class NativeReport { public bool native_assets_materialized; }

        private static void Build(string path, BuildTarget target)
        {
            Directory.CreateDirectory(target == BuildTarget.Android ? Path.GetDirectoryName(path) : path);
            BuildReport report = BuildValidatedPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath }, locationPathName = path, target = target, options = BuildOptions.None
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new BuildFailedException("Map explorer build failed: " + report.summary.result);
            Debug.Log("Aaris map explorer built: " + report.summary.outputPath);
        }
    }
}
