using System;
using System.Collections.Generic;
using UnityEngine;

namespace Aaris.MapReview
{
    // Balanced Aaris game values. These are NOT reverse-engineered values
    // from any commercial game. The only source of tuning is the local JSON.
    [Serializable]
    public sealed class AarisWeaponSpec
    {
        public string id, displayName, modelResource;
        public string category;
        public bool automatic;
        public int rpm, magazineSize, reserveAmmo, pellets;
        public float damage, rangeMetres, spreadDegrees, recoilPitchDegrees;
        // "weight" is the authored LOOT SELECTION weight, not kilograms.
        // Keep physical presentation inertia separate so changing gun feel never
        // silently changes spawn distribution.
        public float reloadSeconds, adsFov, weight;
        public float handlingMassKg = 3.6f;
        public Vector3 handOffset = new Vector3(0.02f, 0f, 0.02f);
        public Vector3 handRotation = new Vector3(0f, 90f, 0f);
        public float handScale = 1f;
        // Physical weapon length in metres. Imported FBX pivots and units are
        // not reliable; both floor pickup and hand equip use this single value.
        public float presentationLengthMetres = 0.86f;

        public float ShotInterval => 60f / Mathf.Max(1, rpm);

        public bool Valid =>
            !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(modelResource) &&
            rpm >= 20 && rpm <= 1300 && magazineSize > 0 && magazineSize <= 250 &&
            reserveAmmo >= 0 && damage > 0f && damage <= 250f &&
            rangeMetres >= 5f && rangeMetres <= 1000f &&
            spreadDegrees >= 0f && spreadDegrees <= 12f &&
            recoilPitchDegrees >= 0f && recoilPitchDegrees <= 20f &&
            pellets >= 1 && pellets <= 16 && reloadSeconds >= 0.1f &&
            reloadSeconds <= 12f && adsFov >= 8f && adsFov <= 65f &&
            weight > 0f && handlingMassKg >= 2.0f && handlingMassKg <= 8.0f &&
            handScale > 0f && handScale <= 12f &&
            presentationLengthMetres >= 0.48f && presentationLengthMetres <= 1.4f;
    }

    [Serializable]
    internal sealed class AarisWeaponCatalogDocument
    {
        public AarisWeaponSpec[] weapons;
    }

    /// <summary>
    /// Reads a versioned local text asset once at match start. No remote
    /// downloads, no duplicated per-weapon shooting scripts.
    /// </summary>
    public static class AarisWeaponCatalog
    {
        private static AarisWeaponSpec[] specs;
        // One cached reference per authored local weapon model: do not perform
        // asset lookups while the player is firing or exchanging floor loot.
        // These are prefab references, not duplicate live GameObjects.
        private static readonly Dictionary<string, GameObject> modelCache =
            new Dictionary<string, GameObject>(8, StringComparer.Ordinal);
        // Current bundled guns use textureless Prototyping Color Library
        // materials. Tune each SHARED material once per domain without
        // cloning materials or spending per-instance/per-frame memory.
        private static readonly HashSet<Material> tunedFlatMaterials =
            new HashSet<Material>();
        private static readonly int MetallicId = Shader.PropertyToID("_Metallic");
        private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        public static bool Ready => specs != null && specs.Length > 0;

        private static void TuneFlatWeaponSurfaces(GameObject prefab)
        {
            if (prefab == null) return;
            Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Material[] materials = renderers[i].sharedMaterials;
                for (int j = 0; j < materials.Length; j++)
                {
                    Material material = materials[j];
                    if (material == null ||
                        material.name.IndexOf("PCL_Flat", StringComparison.OrdinalIgnoreCase) < 0 ||
                        !tunedFlatMaterials.Add(material))
                        continue;

                    // These are shared Resources materials, so this improves
                    // held + floor guns together without allocating copies.
                    material.enableInstancing = true;
                    string label = material.name;
                    float metallic = 0.10f, smoothness = 0.27f;
                    if (label.IndexOf("Brown", StringComparison.OrdinalIgnoreCase) >= 0)
                    { metallic = 0.02f; smoothness = 0.22f; }
                    else if (label.IndexOf("Blue", StringComparison.OrdinalIgnoreCase) >= 0)
                    { metallic = 0.06f; smoothness = 0.30f; }
                    else if (label.IndexOf("Grey_Light", StringComparison.OrdinalIgnoreCase) >= 0)
                    { metallic = 0.48f; smoothness = 0.44f; }
                    else if (label.IndexOf("Grey_Darker", StringComparison.OrdinalIgnoreCase) >= 0)
                    { metallic = 0.22f; smoothness = 0.31f; }
                    else if (label.IndexOf("Grey_Dark", StringComparison.OrdinalIgnoreCase) >= 0)
                    { metallic = 0.30f; smoothness = 0.36f; }
                    else if (label.IndexOf("Black_Lighter", StringComparison.OrdinalIgnoreCase) >= 0)
                    { metallic = 0.18f; smoothness = 0.34f; }
                    else if (label.IndexOf("White", StringComparison.OrdinalIgnoreCase) >= 0)
                    { metallic = 0.12f; smoothness = 0.32f; }

                    if (material.HasProperty(MetallicId))
                        material.SetFloat(MetallicId, metallic);
                    if (material.HasProperty(SmoothnessId))
                        material.SetFloat(SmoothnessId, smoothness);
                }
            }
        }

        public static bool TryLoad()
        {
            if (Ready) return true;
            TextAsset asset = Resources.Load<TextAsset>("AarisWeapons/weapon_catalog");
            if (asset == null)
            {
                Debug.LogError("Aaris: missing embedded AarisWeapons/weapon_catalog.json.");
                return false;
            }
            try
            {
                var doc = JsonUtility.FromJson<AarisWeaponCatalogDocument>(asset.text);
                if (doc == null || doc.weapons == null || doc.weapons.Length == 0)
                    throw new FormatException("Empty weapon catalog.");
                // A single failed Android FBX import must not disable all
                // working guns and the entire offline loot world. Keep strict
                // catalog validation, but select only locally usable models.
                var available = new List<AarisWeaponSpec>(doc.weapons.Length);
                for (int i = 0; i < doc.weapons.Length; i++)
                {
                    if (doc.weapons[i] == null || !doc.weapons[i].Valid)
                        throw new FormatException("Invalid weapon entry at " + i);
                    for (int j = 0; j < i; j++)
                        if (doc.weapons[i].id == doc.weapons[j].id)
                            throw new FormatException("Duplicate weapon identifier.");
                    string modelPath = doc.weapons[i].modelResource;
                    if (!modelCache.TryGetValue(modelPath, out GameObject prefab) || prefab == null)
                    {
                        prefab = Resources.Load<GameObject>(modelPath);
                        if (prefab == null)
                        {
                            Debug.LogWarning("Aaris: unavailable local weapon model; skipping " +
                                doc.weapons[i].id);
                            continue;
                        }
                        TuneFlatWeaponSurfaces(prefab);
                        modelCache[modelPath] = prefab;
                    }
                    // A nominally imported FBX with no renderer is also
                    // unusable as a floor pickup or held weapon on Android.
                    if (prefab.GetComponentInChildren<Renderer>(true) == null)
                    {
                        Debug.LogWarning("Aaris: weapon model has no renderer; skipping " +
                            doc.weapons[i].id);
                        continue;
                    }
                    available.Add(doc.weapons[i]);
                }
                if (available.Count == 0)
                    throw new FormatException("No usable local weapon models.");
                specs = available.ToArray();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("Aaris: invalid local weapon catalog: " + e.Message);
                return false;
            }
        }

        public static GameObject ModelFor(AarisWeaponSpec spec)
        {
            if (spec == null || !TryLoad()) return null;
            if (modelCache.TryGetValue(spec.modelResource, out GameObject prefab) &&
                prefab != null) return prefab;
            // Only a domain reload/re-import should reach this path after boot.
            prefab = Resources.Load<GameObject>(spec.modelResource);
            if (prefab != null)
            {
                TuneFlatWeaponSurfaces(prefab);
                modelCache[spec.modelResource] = prefab;
            }
            return prefab;
        }

        public static AarisWeaponSpec Get(string id)
        {
            if (!TryLoad()) return null;
            foreach (var spec in specs)
                if (string.Equals(spec.id, id, StringComparison.Ordinal)) return spec;
            return null;
        }

        public static AarisWeaponSpec Select(ref uint rng, bool indoor)
        {
            if (!TryLoad()) return null;
            float total = 0f;
            foreach (var spec in specs)
                total += indoor ? spec.weight : spec.weight * (spec.category == "Sniper" ? 0.2f : 1f);
            float roll = AarisWeaponRandom.Next01(ref rng) * total;
            foreach (var spec in specs)
            {
                roll -= indoor ? spec.weight : spec.weight *
                    (spec.category == "Sniper" ? 0.2f : 1f);
                if (roll <= 0f) return spec;
            }
            return specs[specs.Length - 1];
        }
    }

    /// <summary>Self-contained xorshift32, isolated from UnityEngine.Random.</summary>
    public static class AarisWeaponRandom
    {
        public static uint Next(ref uint state)
        {
            if (state == 0) state = 0x9E3779B9u;
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }
        public static float Next01(ref uint state) => (Next(ref state) & 0xFFFFFFu) / 16777216f;
    }
}
