#if UNITY_EDITOR
using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Gives the mobile pipeline its own Forward renderer. URP Auto depth
    /// priming differs between desktop and Android; explicit Disabled keeps
    /// this low preset predictable without changing other quality renderers.
    /// </summary>
    public static class AarisUltraMobileRendererBudget
    {
        private const string Low = "Assets/TerrainDemoScene_URP/Settings/URP/URP-LowQuality Pipeline Asset.asset";
        private const string Shared = "Assets/TerrainDemoScene_URP/Settings/URP/Universal Render Pipeline Asset_Renderer.asset";
        private const string Target = "Assets/AarisWorld/Settings/AarisMobileForwardRenderer.asset";
        private const string Output = "/tmp/aaris-ultra-mobile-renderer";
        [Serializable] private sealed class Report
        {
            public string status, unityVersion, renderer, rendererGuid, pipeline,
                originalSharedRendererSha256, scope;
            public int primingBefore, primingAfter, rendererIndex, rendererFeatures,
                opaqueLayerMask, transparentLayerMask;
            public bool originalSharedRendererUnchanged, reloadedPipelineReferenceValid;
        }

        [MenuItem("Aaris/World/Author dedicated mobile Forward renderer")]
        public static void Apply()
        {
            Require(Application.isBatchMode && !EditorApplication.isPlayingOrWillChangePlaymode,
                "dedicated-edit-batch-required");
            Directory.CreateDirectory(Output);
            Require(!File.Exists(Target) && !File.Exists(Target + ".meta"), "refuse-existing-renderer");
            byte[] beforeLow = File.ReadAllBytes(Low);
            byte[] beforeShared = File.ReadAllBytes(Shared);
            byte[] beforeSharedMeta = File.ReadAllBytes(Shared + ".meta");
            string sharedHash = Hash(Shared);
            string sharedMetaHash = Hash(Shared + ".meta");
            try
            {
                var low = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Low);
                Require(low != null, "missing-low-pipeline");
                var lowState = new SerializedObject(low);
                var list = lowState.FindProperty("m_RendererDataList");
                int index = lowState.FindProperty("m_DefaultRendererIndex").intValue;
                Require(list != null && index >= 0 && index < list.arraySize,
                    "invalid-default-renderer-index");
                var source = list.GetArrayElementAtIndex(index).objectReferenceValue as UniversalRendererData;
                Require(source != null && AssetDatabase.GetAssetPath(source) == Shared,
                    "unexpected-shared-renderer");
                var sourceState = new SerializedObject(source);
                Require(sourceState.FindProperty("m_RenderingMode").intValue == 0 &&
                    source.rendererFeatures.Count == 0, "unreviewed-renderer-features-or-mode");
                int before = sourceState.FindProperty("m_DepthPrimingMode").intValue;
                if (!AssetDatabase.IsValidFolder("Assets/AarisWorld/Settings"))
                    AssetDatabase.CreateFolder("Assets/AarisWorld", "Settings");
                Require(AssetDatabase.CopyAsset(Shared, Target), "renderer-copy-failed");
                var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Target);
                Require(renderer != null, "copied-renderer-missing");
                renderer.name = "Aaris Mobile Forward Renderer";
                var state = new SerializedObject(renderer);
                state.FindProperty("m_DepthPrimingMode").intValue = 0;
                state.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(renderer);
                list.GetArrayElementAtIndex(index).objectReferenceValue = renderer;
                lowState.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(low);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(Target, ImportAssetOptions.ForceUpdate);
                AssetDatabase.ImportAsset(Low, ImportAssetOptions.ForceUpdate);
                renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Target);
                low = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Low);
                var saved = new SerializedObject(renderer);
                var savedLow = new SerializedObject(low);
                Require(saved.FindProperty("m_DepthPrimingMode").intValue == 0 &&
                    savedLow.FindProperty("m_RendererDataList").GetArrayElementAtIndex(index).objectReferenceValue == renderer,
                    "reloaded-pipeline-renderer-mismatch");
                Require(Hash(Shared) == sharedHash && Hash(Shared + ".meta") == sharedMetaHash,
                    "shared-renderer-was-mutated");
                var report = new Report { status = "PASS", unityVersion = Application.unityVersion,
                    renderer = Target, rendererGuid = AssetDatabase.AssetPathToGUID(Target), pipeline = Low,
                    originalSharedRendererSha256 = sharedHash, primingBefore = before, primingAfter = 0,
                    rendererIndex = index, rendererFeatures = renderer.rendererFeatures.Count,
                    opaqueLayerMask = saved.FindProperty("m_OpaqueLayerMask").intValue,
                    transparentLayerMask = saved.FindProperty("m_TransparentLayerMask").intValue,
                    originalSharedRendererUnchanged = true, reloadedPipelineReferenceValid = true,
                    scope = "Low-only renderer has depth priming Disabled. URP17.3 Auto recommendation was already false on Android; desktop duplicate-pass removal is not claimed as equivalent Android savings." };
                File.WriteAllText(Path.Combine(Output, "result.json"), JsonUtility.ToJson(report, true));
                Debug.Log("AARIS_MOBILE_RENDERER_PASS low_only=true depth_priming=Disabled shared_renderer_unchanged=true");
            }
            catch
            {
                File.WriteAllBytes(Low, beforeLow);
                File.WriteAllBytes(Shared, beforeShared);
                File.WriteAllBytes(Shared + ".meta", beforeSharedMeta);
                if (File.Exists(Target)) AssetDatabase.DeleteAsset(Target);
                if (File.Exists(Target + ".meta")) File.Delete(Target + ".meta");
                AssetDatabase.Refresh();
                throw;
            }
        }

        private static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        private static void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("AARIS_MOBILE_RENDERER: " + reason); }
    }
}
#endif
