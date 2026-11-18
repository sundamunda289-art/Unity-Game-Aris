using System;
using UnityEditor;
using UnityEngine;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Native Unity Editor import gate. This is not a substitute for
    /// visual screenshots and physical Android gameplay. Run after loading
    /// the project with a valid Unity license.
    /// </summary>
    public static class AarisWeaponImportValidation
    {
        [Serializable] private sealed class LiveCatalog { public AarisWeaponSpec[] weapons; }

        // Measure imported mesh geometry in a temporary, identity-scaled
        // scene root. Renderer.bounds can be stale after import or rotation,
        // so this repeats the real pickup/equip corner measurement in Unity.
        private static void ValidateVisibleScale(AarisWeaponSpec spec, GameObject prefab)
        {
            var auditRoot = new GameObject("Aaris_Weapon_Scale_Audit_Temporary");
            try
            {
                var instance = UnityEngine.Object.Instantiate(
                    prefab, auditRoot.transform, false);
                instance.transform.localPosition = Vector3.zero;
                // Runtime import correction for today's five PBR models
                // is 180 degrees around Y. That changes facing, not AABB
                // dimensions, so an identity rotation measures the same size
                // without calling an internal runtime API from Editor assembly.
                instance.transform.localRotation = Quaternion.identity;
                Bounds combined = default(Bounds);
                bool found = false;
                foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null || !renderer.enabled ||
                        renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy)
                        continue;
                    Bounds localBounds;
                    if (renderer is SkinnedMeshRenderer skinned)
                    {
                        if (skinned.sharedMesh == null ||
                            skinned.sharedMesh.vertexCount == 0) continue;
                        localBounds = skinned.localBounds;
                    }
                    else if (renderer is MeshRenderer)
                    {
                        var filter = renderer.GetComponent<MeshFilter>();
                        if (filter == null || filter.sharedMesh == null ||
                            filter.sharedMesh.vertexCount == 0) continue;
                        localBounds = filter.sharedMesh.bounds;
                    }
                    else continue;

                    Vector3 center = localBounds.center;
                    Vector3 extents = localBounds.extents;
                    for (int corner = 0; corner < 8; corner++)
                    {
                        Vector3 local = center + new Vector3(
                            (corner & 1) == 0 ? -extents.x : extents.x,
                            (corner & 2) == 0 ? -extents.y : extents.y,
                            (corner & 4) == 0 ? -extents.z : extents.z);
                        Vector3 position = auditRoot.transform.InverseTransformPoint(
                            renderer.transform.TransformPoint(local));
                        if (!found)
                        {
                            combined = new Bounds(position, Vector3.zero);
                            found = true;
                        }
                        else combined.Encapsulate(position);
                    }
                }

                if (!found) throw new InvalidOperationException(
                    "No visible geometry for weapon scale: " + spec.id);
                float longest = Mathf.Max(
                    combined.size.x, combined.size.y, combined.size.z);
                if (longest < 0.001f || float.IsNaN(longest) ||
                    float.IsInfinity(longest))
                    throw new InvalidOperationException(
                        "Invalid imported weapon size: " + spec.id);
                // These are the actual normalization rules used by ground
                // pickups and the held gun. Detect a tiny imported model
                // hitting the 10x cap rather than silently shipping a toy gun.
                float scale = Mathf.Clamp(
                    spec.presentationLengthMetres / longest, 0.0000001f, 10f);
                float groundLength = longest * scale;
                float heldLength = groundLength * spec.handScale;
                if (Mathf.Abs(groundLength - spec.presentationLengthMetres) > 0.01f ||
                    Mathf.Abs(heldLength - spec.presentationLengthMetres *
                        spec.handScale) > 0.01f)
                    throw new InvalidOperationException(
                        "Imported geometry cannot reach target visible gun length: " +
                        spec.id + " source=" + longest + "m, floor=" +
                        groundLength + "m, hand=" + heldLength + "m");
                Debug.Log("AARIS_WEAPON_SCALE_PASS: " + spec.id +
                    " imported=" + longest.ToString("F3") + "m, floor=" +
                    groundLength.ToString("F3") + "m, hand=" +
                    heldLength.ToString("F3") + "m");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(auditRoot);
            }
        }
        [MenuItem("Aaris/Validate Offline Weapons and Loot")]
        public static void Run()
        {
            AarisExplorerBuild.ValidateSavedScene();
            var json = Resources.Load<TextAsset>("AarisWeapons/weapon_catalog");
            if (json == null) throw new InvalidOperationException("Missing active catalog.");
            var parsed = JsonUtility.FromJson<LiveCatalog>(json.text);
            if (parsed == null || parsed.weapons == null || parsed.weapons.Length != 5)
                throw new InvalidOperationException("Active catalog requires five models.");
            var specs = parsed.weapons;
            int triangles = 0;
            foreach (var spec in specs)
            {
                if (spec == null || !spec.Valid)
                    throw new InvalidOperationException("Invalid live catalog entry.");
                string name = spec.id;
                string prefix = "Assets/Resources/" + spec.modelResource;
                string path = AssetDatabase.LoadAssetAtPath<GameObject>(prefix + ".fbx") != null ? prefix + ".fbx" : prefix + ".obj";
                var model = Resources.Load<GameObject>(spec.modelResource);
                if (model == null)
                    throw new InvalidOperationException("Missing Unity imported model: " + path);
                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer == null)
                    throw new InvalidOperationException("Unity failed FBX ModelImporter: " + path);
                var renderers = model.GetComponentsInChildren<Renderer>(true);
                if (!Array.Exists(renderers, r => r != null && r.enabled && r.gameObject.activeSelf))
                    throw new InvalidOperationException("No enabled floor mesh renderer: " + path);
                var meshFilters = model.GetComponentsInChildren<MeshFilter>(true);
                var skinRenderers = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                if (meshFilters.Length == 0 && skinRenderers.Length == 0)
                    throw new InvalidOperationException("Weapon has no renderable mesh: " + path);
                int thisModel = 0;
                foreach (var filter in meshFilters)
                {
                    if (filter.sharedMesh == null) continue;
                    thisModel += filter.sharedMesh.triangles.Length / 3;
                }
                // The licensed source FBXs are rigged and some use ONLY
                // skinned meshes; checking MeshFilter alone wrongly fails.
                foreach (var skinned in skinRenderers)
                {
                    if (skinned.sharedMesh == null) continue;
                    thisModel += skinned.sharedMesh.triangles.Length / 3;
                }
                if (thisModel < 8 || thisModel > 180000)
                    throw new InvalidOperationException("Invalid phone mesh triangle count " +
                        thisModel + ": " + path);
                triangles += thisModel;
                ValidateVisibleScale(spec, model);
                string resourcePath = spec.modelResource;
                if (Resources.Load<GameObject>(resourcePath) == null)
                    throw new InvalidOperationException("Resources.Load failed: " + resourcePath);
                Debug.Log("AARIS_WEAPON_IMPORT: " + name + ", " + thisModel + " triangles, Active gameplay model GUID stable.");
            }
            string[] scripts =
            {
                "AarisWeaponCatalog", "AarisWeaponPickup", "AarisWeaponInventory",
                "AarisWeaponLootWorld", "AarisWeaponHUD", "AarisBattleActions",
                "AarisEquippedGun", "AarisThirdPersonPlayer"
            };
            foreach (string name in scripts)
            {
                string path = "Assets/AarisWorld/Scripts/" + name + ".cs";
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script == null || script.GetClass() == null)
                    throw new InvalidOperationException("Unity script import failed: " + path);
            }
            if (!AarisWeaponCatalog.TryLoad())
                throw new InvalidOperationException("Offline catalog could not import in Unity.");
            var loot = Resources.Load<TextAsset>("AarisWeapons/loot_candidates");
            if (loot == null || !loot.text.Contains("\"schema\": 1"))
                throw new InvalidOperationException("Missing baked 2km loot candidate data.");
            Debug.Log("AARIS_WEAPON_IMPORT_PASS: 5 active gameplay FBX/OBJ assets; " +
                triangles + " total triangles; offline catalog + authored loot + player scripts.");
            Debug.Log("PLAYMODE_QA_REQUIRED: test hand rig, scope FOV, prone muzzle, ammo, " +
                "indoor accessible floor, native tree cover, fire audio/materials and Android FPS.");
        }
    }
}
