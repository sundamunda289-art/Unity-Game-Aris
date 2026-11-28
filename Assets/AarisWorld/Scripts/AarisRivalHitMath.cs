using UnityEngine;

namespace Aaris.MapReview
{
    internal struct AarisCombatHit
    {
        internal Collider collider;
        internal AarisOfflineRival logicalRival;
        internal float distance;
        internal Vector3 point, normal;

        internal bool BelongsTo(Transform actor) => actor != null &&
            (logicalRival != null ? logicalRival.transform == actor :
             collider != null && collider.transform.IsChildOf(actor));

        internal static AarisCombatHit Physical(RaycastHit hit) => new AarisCombatHit
        {
            collider = hit.collider, distance = hit.distance,
            point = hit.point, normal = hit.normal
        };
    }

    // A data-only standing capsule. All inputs and returned distances are world
    // metres. This does not allocate or register an invisible PhysX receiver.
    internal static class AarisRivalHitMath
    {
        internal static bool RaycastVerticalCapsule(Ray ray, Vector3 feet,
            float height, float radius, float maxDistance, out float distance,
            out Vector3 normal)
        {
            distance = 0f;
            normal = Vector3.zero;
            if (!(radius > 0f) || !(height >= radius * 2f) ||
                float.IsInfinity(radius) || float.IsInfinity(height) ||
                !(maxDistance >= 0f) || float.IsInfinity(maxDistance) ||
                !Finite(feet) || !Finite(ray.origin) || !Finite(ray.direction)) return false;
            float directionLength = ray.direction.magnitude;
            if (!(directionLength > 0.000001f) || float.IsInfinity(directionLength)) return false;
            Vector3 direction = ray.direction / directionLength;
            Vector3 bottom = feet + Vector3.up * radius;
            Vector3 top = feet + Vector3.up * (height - radius);
            Vector3 origin = ray.origin;
            Vector3 closest = new Vector3(feet.x, Mathf.Clamp(origin.y, bottom.y, top.y), feet.z);
            if ((origin - closest).sqrMagnitude <= radius * radius)
            {
                normal = -direction;
                return true;
            }
            float best = float.PositiveInfinity;
            Vector3 bestNormal = Vector3.zero;
            // Infinite vertical cylinder, then restrict the result to its
            // straight segment. Parallel rays are handled by the two caps.
            float ox = origin.x - feet.x, oz = origin.z - feet.z;
            double a = (double)direction.x * direction.x + (double)direction.z * direction.z;
            if (a > 0.0000001f)
            {
                double b = (double)ox * direction.x + (double)oz * direction.z;
                double c = (double)ox * ox + (double)oz * oz - (double)radius * radius;
                double discriminant = b * b - a * c;
                if (discriminant >= 0f)
                {
                    float t = (float)((-b - System.Math.Sqrt(discriminant)) / a);
                    float y = origin.y + direction.y * t;
                    if (t >= 0f && t <= maxDistance && y >= bottom.y && y <= top.y)
                    {
                        best = t;
                        bestNormal = new Vector3(ox + direction.x * t, 0f,
                            oz + direction.z * t).normalized;
                    }
                }
            }
            TestSphere(origin, direction, bottom, radius, maxDistance, ref best, ref bestNormal);
            TestSphere(origin, direction, top, radius, maxDistance, ref best, ref bestNormal);
            if (float.IsPositiveInfinity(best)) return false;
            distance = best;
            normal = bestNormal;
            return true;
        }

        private static void TestSphere(Vector3 origin, Vector3 direction, Vector3 center,
            float radius, float maxDistance, ref float best, ref Vector3 normal)
        {
            Vector3 delta = origin - center;
            double b = (double)delta.x * direction.x + (double)delta.y * direction.y +
                (double)delta.z * direction.z;
            double a = (double)direction.x * direction.x + (double)direction.y * direction.y +
                (double)direction.z * direction.z;
            double c = (double)delta.x * delta.x + (double)delta.y * delta.y +
                (double)delta.z * delta.z - (double)radius * radius;
            double discriminant = b * b - a * c;
            if (discriminant < 0f) return;
            float t = (float)((-b - System.Math.Sqrt(discriminant)) / a);
            if (t < 0f || t > maxDistance || t >= best) return;
            best = t;
            normal = (origin + direction * t - center).normalized;
        }

        private static bool Finite(Vector3 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
