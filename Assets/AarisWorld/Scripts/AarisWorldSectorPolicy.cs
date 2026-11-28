using UnityEngine;

namespace Aaris.MapReview
{
    internal static class AarisWorldSectorPolicy
    {
        internal const float CellMetres = 150f;
        internal const int ActiveRing = 1;
        internal static int Cell(float coordinate) => Mathf.FloorToInt(coordinate / CellMetres);

        internal static bool IntersectsActiveRing(float centerX, float centerZ,
            float halfX, float halfZ, float playerX, float playerZ, float padding = 0f)
        {
            float left = (Cell(playerX) - ActiveRing) * CellMetres - padding;
            float bottom = (Cell(playerZ) - ActiveRing) * CellMetres - padding;
            float right = left + CellMetres * (ActiveRing * 2 + 1) + padding * 2f;
            float top = bottom + CellMetres * (ActiveRing * 2 + 1) + padding * 2f;
            // Half-open grid: sharing an edge has no area of detail inside
            // the ring. Inclusive tests silently turn a 3x3 ring into 5x5.
            return centerX + halfX > left && centerX - halfX < right &&
                centerZ + halfZ > bottom && centerZ - halfZ < top;
        }
    }
}
