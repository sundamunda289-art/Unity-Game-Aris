#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Compiles six wind modes through native desktop and Android shader backends.
    /// No material, shader, scene, build-target or graphics setting is modified.
    /// A bytecode success is shader validation, not an Android player build/device test.
    /// </summary>
    public static class AarisSpeedTreeVariantQA
    {
        private const string ShaderPath =
            "Assets/TerrainDemoScene_URP/ShaderGraphs/AarisSpeedTree8Mobile.shader";
        private static readonly string[] WindKeywords = {
            "_WINDQUALITY_NONE", "", "_WINDQUALITY_FAST", "_WINDQUALITY_BETTER",
            "_WINDQUALITY_BEST", "_WINDQUALITY_PALM"
        };
        private static readonly string[] PassNames = {
            "ForwardLit", "ShadowCaster", "DepthOnly", "DepthNormals"
        };

        [MenuItem("Aaris/CI/Verify SpeedTree native wind variants")]
        public static void Run() => Verify(true, true);
        public static void RunDesktop() => Verify(true, false);
        public static void RunAndroid() => Verify(false, true);

        private static void Verify(bool desktop, bool android)
        {
            var report = new StringBuilder("UNITY=" + Application.unityVersion + "\n");
            int compiled = 0, failed = 0, unavailable = 0, warnings = 0;
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                    throw new InvalidOperationException("EDITOR_BUSY");
                Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
                if (shader == null) throw new InvalidOperationException("SHADER_MISSING " + ShaderPath);
                ShaderData data = ShaderUtil.GetShaderData(shader);
                if (data.SubshaderCount < 1) throw new InvalidOperationException("SUBSHADER_MISSING");
                ShaderData.Subshader subshader = data.GetSubshader(0);
                var passes = new Dictionary<string, ShaderData.Pass>(StringComparer.Ordinal);
                for (int i = 0; i < subshader.PassCount; i++)
                {
                    ShaderData.Pass pass = subshader.GetPass(i);
                    passes[pass.Name] = pass;
                }

                if (desktop) CompilePlatform(ShaderCompilerPlatform.OpenGLCore,
                    BuildTarget.StandaloneLinux64, passes, report,
                    ref compiled, ref failed, ref unavailable, ref warnings);
                if (android)
                {
                    CompilePlatform(ShaderCompilerPlatform.GLES3x, BuildTarget.Android,
                        passes, report, ref compiled, ref failed, ref unavailable, ref warnings);
                    CompilePlatform(ShaderCompilerPlatform.Vulkan, BuildTarget.Android,
                        passes, report, ref compiled, ref failed, ref unavailable, ref warnings);
                }
            }
            catch (Exception exception)
            {
                failed++;
                report.AppendLine("FAIL " + exception);
            }

            report.AppendLine("COMPILED=" + compiled + " FAILED=" + failed +
                " UNAVAILABLE=" + unavailable + " WARNINGS=" + warnings);
            // Unavailable platforms are never represented as a successful run.
            bool passed = failed == 0 && unavailable == 0 && compiled > 0;
            report.AppendLine(passed ? "AARIS_SPEEDTREE_VARIANTS_PASS" : "AARIS_SPEEDTREE_VARIANTS_FAIL");
            string mode = desktop && android ? "all" : (desktop ? "desktop" : "android");
            string directory = Path.Combine(Path.GetTempPath(), "aaris-speedtree-variant-qa");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, mode + ".txt"), report.ToString());
            Debug.Log(report.ToString());
            if (Application.isBatchMode) EditorApplication.Exit(passed ? 0 : 1);
        }

        private static void CompilePlatform(ShaderCompilerPlatform platform, BuildTarget target,
            Dictionary<string, ShaderData.Pass> passes, StringBuilder report,
            ref int compiled, ref int failed, ref int unavailable, ref int warnings)
        {
            bool playerModule = BuildPipeline.IsBuildTargetSupported(
                BuildPipeline.GetBuildTargetGroup(target), target);
            report.AppendLine("PLATFORM=" + platform + " TARGET=" + target +
                " PLAYER_BUILD_SUPPORTED=" + playerModule);
            // Unity can expose cross-platform shader compilers even when the
            // matching player module/SDK is absent. Attempt native bytecode and
            // report both facts; do not skip a broken shader as a missing SDK.
            foreach (string passName in PassNames)
            {
                if (!passes.TryGetValue(passName, out ShaderData.Pass pass))
                {
                    failed++;
                    report.AppendLine("FAIL missing-pass " + passName);
                    continue;
                }
                foreach (string wind in WindKeywords)
                {
                    string label = platform + "/" + passName + "/" +
                        (wind.Length == 0 ? "Fastest" : wind);
                    try
                    {
                        // GLCore/GLES/Vulkan combine vertex and fragment stages
                        // in the Vertex compile request (Unity ShaderData API).
                        ShaderData.VariantCompileInfo variant = pass.CompileVariant(
                            ShaderType.Vertex, Keywords(passName, wind), platform, target);
                        bool hasErrors = false;
                        if (variant.Messages != null)
                        {
                            foreach (ShaderMessage message in variant.Messages)
                            {
                                bool error = message.severity == ShaderCompilerMessageSeverity.Error;
                                if (error) hasErrors = true;
                                else warnings++;
                                report.AppendLine((error ? "ERROR " : "WARNING ") + label +
                                    " " + message.file + ":" + message.line + " " + message.message);
                            }
                        }
                        bool hasCode = variant.ShaderData != null && variant.ShaderData.Length > 0;
                        if (variant.Success && hasCode && !hasErrors)
                        {
                            compiled++;
                            report.AppendLine("PASS " + label + " bytes=" + variant.ShaderData.Length);
                        }
                        else
                        {
                            failed++;
                            report.AppendLine("FAIL " + label + " success=" + variant.Success +
                                " bytecode=" + hasCode);
                        }
                    }
                    catch (NotSupportedException exception)
                    {
                        unavailable++;
                        report.AppendLine("UNAVAILABLE " + label + " " + exception.Message);
                    }
                    catch (Exception exception)
                    {
                        failed++;
                        report.AppendLine("FAIL " + label + " " + exception);
                    }
                }
            }
        }

        private static string[] Keywords(string passName, string wind)
        {
            var keywords = new List<string> { "INSTANCING_ON", "LOD_FADE_PERCENTAGE" };
            if (wind.Length != 0) keywords.Add(wind);
            if (passName == "ForwardLit" || passName == "DepthNormals")
            {
                keywords.Add("EFFECT_BUMP");
                keywords.Add("EFFECT_SUBSURFACE");
            }
            if (passName == "ForwardLit")
            {
                keywords.Add("EFFECT_EXTRA_TEX");
                keywords.Add("EFFECT_HUE_VARIATION");
                keywords.Add("_MAIN_LIGHT_SHADOWS_CASCADE");
                keywords.Add("_SHADOWS_SOFT");
            }
            return keywords.ToArray();
        }
    }
}
#endif
