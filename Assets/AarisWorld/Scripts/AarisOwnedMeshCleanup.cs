using System.Collections.Generic;
using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// Owns only the per-house procedural meshes allocated by AarisModularHouse.
    /// Unity does not automatically destroy runtime-created Mesh objects when
    /// their MeshFilter or GameObject is destroyed. Never track imported or
    /// shared primitive meshes, collider meshes, or authored materials.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AarisOwnedMeshCleanup : MonoBehaviour
    {
        private readonly List<Mesh> ownedMeshes = new List<Mesh>(4);

        internal void Track(Mesh mesh)
        {
            if (mesh != null) ownedMeshes.Add(mesh);
        }

        private void OnDestroy()
        {
            for (int i = 0; i < ownedMeshes.Count; i++)
            {
                Mesh mesh = ownedMeshes[i];
                if (mesh != null) Destroy(mesh);
            }
            ownedMeshes.Clear();
        }
    }
}
