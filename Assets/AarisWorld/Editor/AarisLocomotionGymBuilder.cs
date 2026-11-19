using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Movement-only QA scene. Run from an isolated filtered project via
    /// Tools/CI/build_locomotion_gym.sh, NEVER by altering a live main scene.
    /// </summary>
    public static class AarisLocomotionGymBuilder
    {
        public const string ScenePath =
            "Assets/AarisWorld/Generated/AarisLocomotionGym.unity";
        private const string PrefabPath = "Assets/AarisWorld/Player/AarisPlayer.prefab";
        private const string MaterialPath =
            "Assets/AarisWorld/Generated/AarisGymFloor.mat";
        private const string ApkName = "AarisLocomotionGym.apk";

        public static bool IsIsolatedGymBuild(string outputPath)
        {
            if (!string.Equals(Path.GetFileName(outputPath), ApkName,
                    StringComparison.OrdinalIgnoreCase)) return false;
            EditorBuildSettingsScene[] enabled = EditorBuildSettings.scenes;
            return enabled.Length == 1 && enabled[0].enabled &&
                string.Equals(enabled[0].path, ScenePath,
                    StringComparison.Ordinal);
        }

        [MenuItem("Aaris/Locomotion Gym/Create lightweight test scene")]
        public static void CreateScene()
        {
            Directory.CreateDirectory("Assets/AarisWorld/Generated");
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                shader = Shader.Find("Standard");
            if (shader == null)
                throw new BuildFailedException("GYM_MISSING_LIT_SHADER");

            Material floorMaterial =
                AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (floorMaterial == null)
            {
                floorMaterial = new Material(shader);
                floorMaterial.name = "Aaris Gym Neutral Floor";
                if (floorMaterial.HasProperty("_BaseColor"))
                    floorMaterial.SetColor("_BaseColor", new Color(0.38f, 0.46f, 0.49f));
                AssetDatabase.CreateAsset(floorMaterial, MaterialPath);
            }

            Block("Flat Ground", new Vector3(0f, -0.5f, 0f),
                new Vector3(44f, 1f, 44f), floorMaterial);

            GameObject ramp = Block("Slope - walk and land",
                new Vector3(-9f, 0.65f, 8f),
                new Vector3(4f, 0.4f, 7f), floorMaterial);
            ramp.transform.rotation = Quaternion.Euler(14f, 0f, 0f);

            for (int i = 0; i < 5; i++)
                Block("Step " + (i + 1),
                    new Vector3(9f, 0.12f * (i + 1), 3f + i * 0.62f),
                    new Vector3(3f, 0.24f * (i + 1), 0.61f), floorMaterial);

            // Stance clearance: a low roof, not a full heavy building.
            Block("Crouch tunnel ceiling", new Vector3(-8f, 1.28f, -7f),
                new Vector3(4.5f, 0.22f, 4f), floorMaterial);
            Block("Prone tunnel ceiling", new Vector3(9f, 0.81f, -7f),
                new Vector3(4.5f, 0.20f, 4f), floorMaterial);
            Block("Jump obstacle", new Vector3(0f, 0.32f, 7f),
                new Vector3(3f, 0.64f, 0.65f), floorMaterial);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
                throw new BuildFailedException("GYM_MISSING_SHARED_PLAYER_PREFAB");
            var player = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (player == null)
                throw new BuildFailedException("GYM_PLAYER_INSTANTIATE_FAILED");
            player.name = "AarisPlayer_Gym";
            player.transform.position = new Vector3(0f, 0.14f, -9f);
            if (player.GetComponent<AarisBattleActions>() == null)
                player.AddComponent<AarisBattleActions>();
            if (player.GetComponent<AarisLocomotionGymMarker>() == null)
                player.AddComponent<AarisLocomotionGymMarker>();

            var cameraObject = new GameObject("Gym Third Person Camera");
            cameraObject.tag = "MainCamera";
            var cam = cameraObject.AddComponent<Camera>();
            cam.fieldOfView = 65f;
            cameraObject.AddComponent<AudioListener>();
            var motor = player.GetComponent<AarisThirdPersonPlayer>();
            if (motor == null)
                throw new BuildFailedException("GYM_PLAYER_MOTOR_MISSING");
            var motorData = new SerializedObject(motor);
            motorData.FindProperty("followCamera").objectReferenceValue = cam;
            motorData.ApplyModifiedPropertiesWithoutUndo();

            var sunlight = new GameObject("Gym Directional Light");
            sunlight.transform.rotation = Quaternion.Euler(50f, -35f, 0f);
            var light = sunlight.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.65f, 0.69f, 0.75f);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new BuildFailedException("GYM_SCENE_SAVE_FAILED");
            AssetDatabase.SaveAssets();
            Debug.Log("AARIS_LOCOMOTION_GYM_SCENE_READY:" + ScenePath);
        }

        private static GameObject Block(string name, Vector3 position,
            Vector3 scale, Material material)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.position = position;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = material;
            return go;
        }

        public static void BuildGymAndroid()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                throw new BuildFailedException("GYM_REQUIRES_ANDROID_BUILD_TARGET");

            CreateScene();
            var player = GameObject.Find("AarisPlayer_Gym");
            if (player == null ||
                player.GetComponent<AarisThirdPersonPlayer>() == null ||
                player.GetComponent<AarisBattleActions>() == null ||
                player.GetComponent<AarisLocomotionGymMarker>() == null)
                throw new BuildFailedException("GYM_SHARED_PLAYER_PREFLIGHT_FAILED");

            EditorBuildSettings.scenes =
                new[] { new EditorBuildSettingsScene(ScenePath, true) };
            EditorUserBuildSettings.buildAppBundle = false;
            EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
            EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ETC2;

            // Separate identifier: can coexist with the full game.
            // Only the disposable filtered project receives these settings.
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android,
                "com.aaris.locomotiongym");
            PlayerSettings.productName = "Aaris Locomotion Gym";
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.bundleVersion = "0.1.0";
            PlayerSettings.Android.bundleVersionCode = 1;
            PlayerSettings.Android.minSdkVersion =
                AndroidSdkVersions.AndroidApiLevel26;
            PlayerSettings.Android.targetSdkVersion =
                AndroidSdkVersions.AndroidApiLevel33;
            PlayerSettings.Android.useCustomKeystore = false;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.buildApkPerCpuArchitecture = false;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android,
                ScriptingImplementation.Mono2x);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,
                new[] { GraphicsDeviceType.OpenGLES3 });

            string output = Environment.GetEnvironmentVariable("AARIS_GYM_APK_PATH");
            if (string.IsNullOrEmpty(output))
                output = "Builds/Android/" + ApkName;
            string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(output));
            Directory.CreateDirectory(outputDirectory);

            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = output,
                target = BuildTarget.Android,
                options = BuildOptions.None
            });
            if (report == null || report.summary.result != BuildResult.Succeeded)
                throw new BuildFailedException("GYM_ANDROID_BUILD_FAILED");
            if (report.summary.totalErrors != 0 ||
                !File.Exists(output) || new FileInfo(output).Length < 100000)
                throw new BuildFailedException("GYM_APK_OUTPUT_INVALID");
            Debug.Log("AARIS_LOCOMOTION_GYM_APK_READY:" +
                Path.GetFullPath(output) + ",bytes=" +
                new FileInfo(output).Length);
        }
    }
}
