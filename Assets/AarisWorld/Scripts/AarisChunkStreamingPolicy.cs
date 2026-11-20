using System;

namespace Aaris.MapReview
{
    /// <summary>Allocation-free XZ policy for authored additive scene cells.</summary>
    internal static class AarisChunkStreamingPolicy
    {
        // Squared distance outside an AABB (zero inside). Bounds represent the
        // entire chunk, not its centre, so large tiles do not vanish at edges.
        internal static float OutsideDistanceSquared(float x, float z,
            float centerX, float centerZ, float halfX, float halfZ)
        {
            float dx = Math.Max(0f, Math.Abs(x - centerX) - halfX);
            float dz = Math.Max(0f, Math.Abs(z - centerZ) - halfZ);
            return dx * dx + dz * dz;
        }

        // Reject teleports/resumes rather than extrapolating hundreds of metres.
        // This predicts *loading* only; unloading always uses actual position.
        internal static void Predict(float x, float z, float oldX, float oldZ,
            float elapsed, float leadSeconds, float speedCap,
            float teleportSpeed, out float predictedX, out float predictedZ)
        {
            predictedX = x;
            predictedZ = z;
            if (elapsed < 0.05f || elapsed > 1.5f || leadSeconds <= 0f)
                return;
            float vx = (x - oldX) / elapsed;
            float vz = (z - oldZ) / elapsed;
            float speedSquared = vx * vx + vz * vz;
            if (float.IsNaN(speedSquared) || float.IsInfinity(speedSquared) ||
                speedSquared > teleportSpeed * teleportSpeed)
                return;
            float speed = (float)Math.Sqrt(speedSquared);
            if (speed <= 1f) return;
            float ratio = Math.Min(1f, speedCap / speed);
            predictedX += vx * ratio * leadSeconds;
            predictedZ += vz * ratio * leadSeconds;
        }
    }
}
