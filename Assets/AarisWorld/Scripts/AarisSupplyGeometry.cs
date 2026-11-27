using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Aaris.MapReview
{
    internal static class AarisSupplyGeometry
    {
        private static Mesh medical, helmet, crate;

        private static MeshRenderer runway;
        private static bool runwayResolved;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRunway()
        {
            runway = null;
            runwayResolved = false;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
        }

        private static void OnActiveSceneChanged(Scene previous, Scene current)
        {
            runway = null;
            runwayResolved = false;
        }

        // The saved airfield runway is a flat, axis-aligned mesh over the
        // TerrainCollider. Use its visible top only for nearby terrain hits.
        // Caching the renderer avoids adding a collider or another raycast.
        internal static void AlignToRunway(ref Vector3 point, ref Vector3 normal)
        {
            if (!runwayResolved)
            {
                GameObject surface = GameObject.Find("Airfield_240m_Runway");
                if (surface != null) surface.TryGetComponent(out runway);
                runwayResolved = true;
            }
            if (runway == null || !runway.enabled || !runway.gameObject.activeInHierarchy) return;
            Bounds deck = runway.bounds;
            Vector3 min = deck.min;
            Vector3 max = deck.max;
            float gap = max.y - point.y;
            if (point.x < min.x || point.x > max.x || point.z < min.z || point.z > max.z ||
                gap <= 0f || gap > .15f) return;
            point.y = max.y;
            normal = Vector3.up;
        }

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
