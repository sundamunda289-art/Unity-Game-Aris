using UnityEngine;
using UnityEngine.Rendering;

namespace Aaris.MapReview
{
    internal static class AarisSupplyGeometry
    {
        private static Mesh medical, helmet, crate;

        private static Mesh Shared(ref Mesh mesh, string resource)
        {
            if (mesh == null)
            {
                mesh = Resources.Load<Mesh>(resource);
                if (mesh != null && mesh.isReadable) mesh.UploadMeshData(true);
            }
            return mesh;
        }

        internal static Mesh Crate => Shared(ref crate, "AarisLootAmmoCrate");

        internal static GameObject Create(bool isHelmet, Material[] materials)
        {
            var root = new GameObject(isHelmet ? "Aaris Streamed Helmet" : "Aaris Streamed Medkit");
            var filter = root.AddComponent<MeshFilter>();
            filter.sharedMesh = isHelmet
                ? Shared(ref helmet, "AarisLootHelmet")
                : Shared(ref medical, "AarisLootMedical");
            var renderer = root.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return root;
        }
    }
}
