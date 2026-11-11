using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Aaris.MapReview;

namespace Aaris.MapReview.Editor
{
    [InitializeOnLoad]
    public static class AarisKenneyCatalogInstall
    {
        private const string Flag = "/tmp/aaris-codespaces/kenney-catalog-install.request";
        private const string Report = "/tmp/aaris-codespaces/kenney-catalog-install.report";

        static AarisKenneyCatalogInstall() { EditorApplication.delayCall += OnReload; }

        private static void OnReload()
        {
            if (!File.Exists(Flag) || EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorApplication.isCompiling) return;
            File.Delete(Flag);
            try
            {
                var catalog=AssetDatabase.LoadAssetAtPath<AarisExpansionCatalog>(
                    "Assets/Resources/AarisExpansion/AarisExpansionCatalog.asset");
                if (catalog == null) throw new InvalidOperationException("CATALOG_NOT_FOUND");
                var cottage=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/AarisBuildings/OpenCottage.prefab");
                var bungalow=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/AarisBuildings/OpenBungalow.prefab");
                var detached=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/AarisBuildings/OpenDetached.prefab");
                if (cottage == null || bungalow == null || detached == null)
                    throw new InvalidOperationException("NEW_PREFABS_NOT_FOUND");
                if (catalog.watchtower == null || catalog.cottage == null ||
                    catalog.bungalow == null || catalog.detached == null)
                    throw new InvalidOperationException("LEGACY_CATALOG_REFERENCES_MISSING");
                catalog.openCottage=cottage;
                catalog.openBungalow=bungalow;
                catalog.openDetached=detached;
                EditorUtility.SetDirty(catalog);
                AssetDatabase.SaveAssets();
                var path=AssetDatabase.GetAssetPath(catalog.openCottage);
                if (path != "Assets/Resources/AarisBuildings/OpenCottage.prefab")
                    throw new InvalidOperationException("SERIALIZED_REFS_NOT_LINKED");
                File.WriteAllText(Report,"RESULT=PASS\nOPEN_HOUSES=3\nLEGACY_PREFABS_PRESERVED=true");
                Debug.Log("AARIS_KENNEY_CATALOG_LINKED_THREE");
            }
            catch(Exception error)
            {
                File.WriteAllText(Report,"RESULT=FAIL\nERROR="+error.GetType().Name);
                Debug.LogError("AARIS_KENNEY_CATALOG_INSTALL_FAILED");
            }
        }
    }
}
