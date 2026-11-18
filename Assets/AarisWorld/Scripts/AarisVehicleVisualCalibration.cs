using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// One Unity world unit is one metre. Keep each offline car credible
    /// beside 8 m two-way roads and the 1.8 m player, regardless of source
    /// FBX importer axes. Applied ONLY at vehicle spawn, never per-frame.
    /// This is the envelope, not a forced stretched bounding box.
    /// </summary>
    public static class AarisVehicleVisualCalibration
    {
        // X = maximum car width including mirrors, Y = maximum length.
        // Differing vehicle classes must not all become 1.88 x 4.28 m.
        public static Vector2 FootprintMetres(string type)
        {
            switch (type)
            {
                case "Sedan": return new Vector2(1.96f, 4.50f);
                case "SUV": return new Vector2(2.10f, 4.80f);
                case "Sports": return new Vector2(1.99f, 4.46f);
                case "GT": return new Vector2(2.08f, 4.48f);
                case "Hatchback": return new Vector2(1.81f, 3.90f);
                case "Van": return new Vector2(2.05f, 5.08f);
                case "Utility": return new Vector2(2.10f, 5.42f);
                case "Buggy": return new Vector2(1.90f, 3.45f);
                default: return new Vector2(1.96f, 4.50f);
            }
        }

        // Several verified CC0 FBX vehicles were authored Z-up / -Y-forward:
        // height appears along Unity Z and 4-5 m length along Unity Y.
        // -90 degrees around X maps source -Y-forward to Unity +Z, and
        // source +Z-up to Unity +Y, preserving the left/right axle order.
        // A normal Y-up prefab such as the sedan or GT must remain untouched.
        public static Quaternion CorrectSourceAxes(Vector3 measuredSize)
        {
            if (measuredSize.y > 2.75f &&
                measuredSize.y > Mathf.Max(measuredSize.x, measuredSize.z) * 1.30f &&
                measuredSize.x > .8f && measuredSize.z > .7f)
                return Quaternion.Euler(-90f, 0f, 0f);
            return Quaternion.identity;
        }

        public static Vector3 FittedScale(string type, Vector3 measuredSize)
        {
            if (measuredSize.x <= .1f || measuredSize.y <= .1f ||
                measuredSize.z <= .1f ||
                float.IsNaN(measuredSize.sqrMagnitude) ||
                float.IsInfinity(measuredSize.sqrMagnitude))
                return Vector3.one;
            Vector2 envelope = FootprintMetres(type);
            if (type == "GT")
            {
                // The CC0 GT shell contains oversized mirror/shoulder width
                // relative to its length. Narrow only its X (track) axis;
                // keep Y and Z on ONE uniform ratio so tyres remain round
                // in their rolling plane and WheelCollider radius matches.
                float width = Mathf.Clamp(envelope.x / measuredSize.x, .1f, 5f);
                float wheelPlane = Mathf.Clamp(envelope.y / measuredSize.z, .1f, 5f);
                return new Vector3(width, wheelPlane, wheelPlane);
            }
            // No cartoon stretching: single metric ratio for entire model,
            // all wheels, springs and cosmetic lamp placements.
            float uniform = Mathf.Clamp(
                Mathf.Min(envelope.x / measuredSize.x,
                    envelope.y / measuredSize.z), .1f, 5f);
            return new Vector3(uniform, uniform, uniform);
        }
    }
}
