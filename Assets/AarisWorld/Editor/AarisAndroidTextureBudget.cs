#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Android build artifacts use at most 1024 px per imported texture edge.
    /// The original image bytes and other platform settings remain authoritative.
    /// Native TerrainData control maps and generated runtime textures are not
    /// TextureImporter assets and must not be rewritten by this policy.
    /// </summary>
    public sealed class AarisAndroidTextureBudget : AssetPostprocessor
    {
        internal const int MaximumTextureSize = 1024;

        // Include the policy in Unity's importer dependency hash. Switching
        // to Android reimports against this version instead of reusing an
        // oversized previously cached Android artifact.
        public override uint GetVersion() => 1;
        public override int GetPostprocessOrder() => 1000;

        private void OnPreprocessTexture()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android ||
                !assetPath.StartsWith("Assets/", StringComparison.Ordinal)) return;
            TextureImporter importer = assetImporter as TextureImporter;
            if (importer == null) return;
            ApplyBudget(importer);
        }

        internal static bool ApplyBudget(TextureImporter importer)
        {
            TextureImporterPlatformSettings settings =
                importer.GetPlatformTextureSettings("Android");
            int originalLimit = settings.overridden
                ? settings.maxTextureSize : importer.maxTextureSize;
            if (originalLimit > 0 && originalLimit <= MaximumTextureSize) return false;

            if (!settings.overridden)
            {
                // Preserve normal-map/compression/alpha policy inherited from
                // the default importer rather than inventing a texture format.
                settings = importer.GetDefaultPlatformTextureSettings();
                settings.name = "Android";
            }
            settings.overridden = true;
            settings.maxTextureSize = MaximumTextureSize;
            importer.SetPlatformTextureSettings(settings);
            // No SaveAndReimport in an import callback: that causes recursion.
            return true;
        }
    }
}
#endif
