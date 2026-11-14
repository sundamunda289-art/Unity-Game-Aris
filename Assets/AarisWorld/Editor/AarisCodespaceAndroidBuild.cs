using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
#if UNITY_ANDROID
using System.Text.RegularExpressions;
using UnityEditor.Android;
#endif

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// One-time production Unity Editor Android test APK builder.
    /// Operates in a detached, fully authenticated Git LFS worktree.
    /// Never edits the user's dirty GUI project or creates runtime helpers.
    /// </summary>
    public static class AarisCodespaceAndroidBuild
    {
        private const string MainScene =
            "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";
        private const string ExpectedAppId = "com.aaris.game";
        private const string ExpectedVersion = "0.3.1";
        private const int ExpectedVersionCode = 7;
        private const string Output = "/tmp/aaris-codespaces/android-apk";
        private const string Apk = "/tmp/aaris-codespaces/android-apk/Build/AarisGame.apk";

        [Serializable] private sealed class BuildEvidence
        {
            public string status = "failed";
            public string reason = "NOT_STARTED";
            public string unityVersion;
            public string buildTarget = "Android";
            public string applicationId;
            public string applicationVersion;
            public int applicationVersionCode;
            public int targetSdk;
            public string scriptingBackend;
            public int sceneCount;
            public int terrainTiles;
            public long apkBytes;
            public int errors;
            public int warnings;
        }

        [MenuItem("Aaris/CI/Build Android test APK from saved native map")]
        public static void BuildTestApk()
        {
            Directory.CreateDirectory(Output);
            Directory.CreateDirectory(Path.GetDirectoryName(Apk));
            var evidence = new BuildEvidence { unityVersion = Application.unityVersion };
            try
            {
                if (Application.unityVersion != "6000.3.26f1")
                    throw new InvalidOperationException("EDITOR_VERSION_MISMATCH");
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                    throw new InvalidOperationException("ANDROID_NOT_ACTIVE_BEFORE_METHOD");
                if (BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android,
                                                          BuildTarget.Android) == false)
                    throw new InvalidOperationException("ANDROID_BUILD_MODULE_MISSING");

                // Unity's Embedded JDK preference does not always recognize a
                // manually-installed AndroidPlayer/OpenJDK symbolic link on Linux.
                // JDK 17, NDK r27c and the Android SDK are present and verified,
                // but AndroidExternalToolsSettings must point to their real roots
                // BEFORE BuildPipeline's prerequisite and Gradle steps.
#if UNITY_ANDROID
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string jdkRoot = Environment.GetEnvironmentVariable("AARIS_ANDROID_JDK_ROOT");
                if (String.IsNullOrWhiteSpace(jdkRoot))
                    jdkRoot = "/usr/lib/jvm/java-17-openjdk-amd64";
                string sdkRoot = Environment.GetEnvironmentVariable("AARIS_ANDROID_SDK_ROOT");
                if (String.IsNullOrWhiteSpace(sdkRoot))
                    sdkRoot = Path.Combine(home, ".local/share/aaris-android-sdk");
                string ndkRoot = Environment.GetEnvironmentVariable("AARIS_ANDROID_NDK_ROOT");
                if (String.IsNullOrWhiteSpace(ndkRoot))
                    ndkRoot = Path.Combine(sdkRoot, "ndk/27.2.12479018");

                if (!File.Exists(Path.Combine(jdkRoot, "bin/java")) ||
                    !File.Exists(Path.Combine(jdkRoot, "bin/javac")) ||
                    !File.Exists(Path.Combine(jdkRoot, "release")))
                    throw new InvalidOperationException("ANDROID_JDK17_NOT_INSTALLED_OR_INCOMPLETE");
                if (!File.Exists(Path.Combine(sdkRoot, "platform-tools/adb")) ||
                    !Directory.Exists(Path.Combine(sdkRoot, "platforms/android-33")) ||
                    !Directory.Exists(Path.Combine(sdkRoot, "platforms/android-35")) ||
                    !Directory.Exists(Path.Combine(sdkRoot, "build-tools/36.0.0")))
                    throw new InvalidOperationException("ANDROID_SDK_PLATFORM_OR_TOOLS_MISSING");
                if (!File.Exists(Path.Combine(ndkRoot, "source.properties")))
                    throw new InvalidOperationException("ANDROID_NDK_R27C_MISSING");
                AndroidExternalToolsSettings.jdkRootPath = jdkRoot;
                AndroidExternalToolsSettings.sdkRootPath = sdkRoot;
                AndroidExternalToolsSettings.ndkRootPath = ndkRoot;
                if (AndroidExternalToolsSettings.jdkRootPath != jdkRoot ||
                    AndroidExternalToolsSettings.sdkRootPath != sdkRoot ||
                    AndroidExternalToolsSettings.ndkRootPath != ndkRoot)
                    throw new InvalidOperationException("ANDROID_UNITY_EXTERNAL_TOOLS_PATH_REJECTED");
                Debug.Log("AARIS_ANDROID_TOOLS_EXPLICIT_JDK17_SDK_NDKR27C_VERIFIED");
#else
                throw new InvalidOperationException("ANDROID_EDITOR_SCRIPT_SYMBOL_MISSING");
#endif

                var enabled = EditorBuildSettings.scenes
                    .Where(x => x.enabled).Select(x => x.path).ToArray();
                evidence.sceneCount = enabled.Length;
                if (enabled.Length != 1 || enabled[0] != MainScene ||
                    !File.Exists(MainScene))
                    throw new InvalidOperationException("MAIN_SCENE_BUILD_SETTINGS_INVALID");

                EditorSceneManager.OpenScene(MainScene, OpenSceneMode.Single);
                Terrain[] tiles = UnityEngine.Object.FindObjectsByType<Terrain>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                evidence.terrainTiles = tiles.Length;
                if (tiles.Length != 4 || tiles.Any(t => t.terrainData == null))
                    throw new InvalidOperationException("NATIVE_FOUR_TERRAIN_TILES_MISSING");

                // Catch missing terrain paint/shader references BEFORE the
                // headless Android build. Do not replace the authored terrain
                // material, alter instancing, disable any tile, or modify LFS
                // TerrainData: all four surfaces must remain renderable.
                const string terrainMaterialPath =
                    "Assets/TerrainDemoScene_URP/Terrain/Materials/TerrainLit.mat";
                Material nativeTerrainPaint =
                    AssetDatabase.LoadAssetAtPath<Material>(terrainMaterialPath);
                if (nativeTerrainPaint == null || nativeTerrainPaint.shader == null ||
                    nativeTerrainPaint.shader.name != "Universal Render Pipeline/Terrain/Lit" ||
                    !nativeTerrainPaint.shader.isSupported)
                    throw new InvalidOperationException("ANDROID_TERRAIN_LIT_MATERIAL_INVALID");

                foreach (Terrain tile in tiles)
                {
                    if (!tile.drawHeightmap)
                        throw new InvalidOperationException("ANDROID_TERRAIN_HEIGHTMAP_HIDDEN");
                    TerrainData data = tile.terrainData;
                    if (data.terrainLayers == null || data.terrainLayers.Length == 0 ||
                        data.alphamapTextureCount == 0)
                        throw new InvalidOperationException("ANDROID_TERRAIN_PAINT_LAYERS_MISSING");
                    if (tile.materialTemplate != null &&
                        (tile.materialTemplate.shader == null ||
                         !tile.materialTemplate.shader.isSupported))
                        throw new InvalidOperationException("ANDROID_TERRAIN_TILE_SHADER_UNSUPPORTED");
                }
                Debug.Log("AARIS_TERRAIN_ANDROID_PREFLIGHT_FOUR_PAINTED_TILES_READY");

                // This app has no signing key in the repository. The initial
                // diagnostic installable APK uses Unity's debug signature only.
                // Do not overwrite a production keystore or change product ID.
                EditorUserBuildSettings.buildAppBundle = false;
                EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
                // Unity's imported PlayerSettings may retain stale values from a
                // previous isolated Library import. Explicitly enforce the
                // reviewed native v7 Android identity before BuildPipeline.
                // This is an isolated worktree only, never the owner's GUI project.
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android,
                                                        "com.aaris.game");
                PlayerSettings.bundleVersion = "0.3.1";
                PlayerSettings.Android.bundleVersionCode = 7;
                PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
                PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel33;
                if (PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android) !=
                        "com.aaris.game" ||
                    PlayerSettings.Android.bundleVersionCode != 7 ||
                    PlayerSettings.Android.targetSdkVersion != AndroidSdkVersions.AndroidApiLevel33)
                    throw new InvalidOperationException("ANDROID_V7_SETTINGS_NOT_APPLIED");
                PlayerSettings.Android.useCustomKeystore = false;
                PlayerSettings.Android.targetArchitectures = AarisExplorerBuild.AndroidArchitectures;
                PlayerSettings.Android.buildApkPerCpuArchitecture = false;
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android,
                                                   ScriptingImplementation.IL2CPP);
                evidence.scriptingBackend = "IL2CPP";
                evidence.applicationId = PlayerSettings.GetApplicationIdentifier(
                    NamedBuildTarget.Android);
                if (String.IsNullOrEmpty(evidence.applicationId))
                    throw new InvalidOperationException("ANDROID_APPLICATION_ID_MISSING");
                // Validate BEFORE BuildPipeline and again AFTER all preprocessors.
                // AarisExplorerBuild implements IPreprocessBuildWithReport; its
                // Configure() previously overwrote the v7 manifest with the
                // legacy v6 map explorer ID during BuildPipeline.BuildPlayer.
                if (evidence.applicationId != ExpectedAppId ||
                    PlayerSettings.bundleVersion != ExpectedVersion ||
                    PlayerSettings.Android.bundleVersionCode != ExpectedVersionCode ||
                    PlayerSettings.Android.targetSdkVersion != AndroidSdkVersions.AndroidApiLevel33)
                    throw new InvalidOperationException("ANDROID_GAME_IDENTITY_PREBUILD_MISMATCH");


                var request = new BuildPlayerOptions {
                    scenes = enabled,
                    locationPathName = Apk,
                    target = BuildTarget.Android,
                    options = BuildOptions.None
                };
                BuildReport report = BuildPipeline.BuildPlayer(request);
                if (report == null || report.summary.result != BuildResult.Succeeded)
                {
                    evidence.errors = report != null ? report.summary.totalErrors : 1;
                    evidence.warnings = report != null ? report.summary.totalWarnings : 0;
                    throw new InvalidOperationException("ANDROID_BUILD_PIPELINE_FAILED");
                }
                evidence.errors = report.summary.totalErrors;
                evidence.warnings = report.summary.totalWarnings;
                evidence.applicationId = PlayerSettings.GetApplicationIdentifier(
                    NamedBuildTarget.Android);
                evidence.applicationVersion = PlayerSettings.bundleVersion;
                evidence.applicationVersionCode = PlayerSettings.Android.bundleVersionCode;
                evidence.targetSdk = (int)PlayerSettings.Android.targetSdkVersion;
                if (evidence.applicationId != ExpectedAppId ||
                    evidence.applicationVersion != ExpectedVersion ||
                    evidence.applicationVersionCode != ExpectedVersionCode ||
                    PlayerSettings.Android.targetSdkVersion != AndroidSdkVersions.AndroidApiLevel33)
                    throw new InvalidOperationException("ANDROID_GAME_IDENTITY_POSTBUILD_MISMATCH");
                if (PlayerSettings.Android.targetArchitectures != AarisExplorerBuild.AndroidArchitectures ||
                    PlayerSettings.Android.buildApkPerCpuArchitecture)
                    throw new InvalidOperationException("ANDROID_UNIVERSAL_APK_SETTINGS_CHANGED");
                FileInfo apk = new FileInfo(Apk);
                if (!apk.Exists || apk.Length < 100000)
                    throw new InvalidOperationException("ANDROID_APK_FILE_INVALID");
                evidence.apkBytes = apk.Length;
                evidence.status = "passed";
                evidence.reason = "NATIVE_ANDROID_APK_BUILD_SUCCEEDED";
                Debug.Log("AARIS_ANDROID_APK_NATIVE_BUILD_PASSED");
            }
            catch (Exception e)
            {
                evidence.reason = e is InvalidOperationException ?
                    e.Message : e.GetType().Name;
                Debug.LogError("AARIS_ANDROID_APK_NATIVE_BUILD_FAILED:" + evidence.reason);
                throw;
            }
            finally
            {
                File.WriteAllText(Path.Combine(Output, "build-report.json"),
                                  JsonUtility.ToJson(evidence, true));
            }
        }
    }

#if UNITY_ANDROID
    // Android's compile SDK and manifest target SDK are independent. The
    // isolated build can compile with the installed platform without changing the app target.
    public sealed class AarisBundledCompileSdk : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => int.MaxValue;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            if (Environment.GetEnvironmentVariable("AARIS_ANDROID_COMPILE_SDK") != "34") return;
            string launcher = Path.Combine(Path.GetDirectoryName(path), "launcher", "build.gradle");
            string library = Path.Combine(path, "build.gradle");
            foreach (string gradle in new[] { launcher, library })
            {
                string text = File.ReadAllText(gradle);
                const string compile = @"(?m)^([ \t]*compileSdk[ \t]+)(?:33|34)([ \t]*\r?)$";
                const string target = @"(?m)^[ \t]*targetSdk[ \t]+33[ \t]*\r?$";
                if (Regex.Matches(text, compile).Count != 1 || !Regex.IsMatch(text, target))
                    throw new BuildFailedException("AARIS_ANDROID_GENERATED_SDK_SETTINGS_UNEXPECTED");
                string updated = Regex.Replace(text, compile, match =>
                    match.Groups[1].Value + "34" + match.Groups[2].Value);
                if (updated != text) File.WriteAllText(gradle, updated);
            }
            Debug.Log("AARIS_ANDROID_COMPILE_SDK_34_TARGET_SDK_33");
        }
    }
#endif
}
