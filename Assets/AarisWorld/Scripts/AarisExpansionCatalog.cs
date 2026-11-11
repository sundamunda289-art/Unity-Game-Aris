using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// Keeps original LOD/collider prefabs as direct serialized asset references.
    /// This Resources asset bundles the local prefabs even though their source
    /// paths are in Assets/AarisWorld/Models (not under Resources).
    /// </summary>
    public sealed class AarisExpansionCatalog : ScriptableObject
    {
        public GameObject cottage;
        public GameObject bungalow;
        public GameObject detached;
        public GameObject watchtower;
        // Baked, offline CC0 house prefabs with real open entrance/window apertures.
        // Explicit serialized References (no Resources.Load per house).
        public GameObject openCottage;
        public GameObject openBungalow;
        public GameObject openDetached;

        public GameObject ResolveOpen(string id)
        {
            switch (id)
            {
                case "cottage": return openCottage;
                case "bungalow": return openBungalow;
                case "detached": return openDetached;
                default: return null;
            }
        }

        public GameObject Resolve(string id)
        {
            switch (id)
            {
                case "cottage": return cottage;
                case "bungalow": return bungalow;
                case "detached": return detached;
                case "watchtower": return watchtower;
                default: return null;
            }
        }
    }

}
