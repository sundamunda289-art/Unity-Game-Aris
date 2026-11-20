using UnityEngine;

namespace ALSUnity
{
    /// <summary>
    /// Unity equivalents of the Unreal FMath / ALSMathLibrary helpers the locomotion logic relies on.
    /// Angles are yaw degrees, distances are metres.
    /// </summary>
    public static class ALSMath
    {
        // FMath::FInterpTo
        public static float InterpTo(float current, float target, float deltaTime, float speed)
        {
            if (speed <= 0f)
            {
                return target;
            }
            float dist = target - current;
            if (dist * dist < 1e-8f)
            {
                return target;
            }
            return current + dist * Mathf.Clamp01(deltaTime * speed);
        }

        // FMath::VInterpTo
        public static Vector3 InterpTo(Vector3 current, Vector3 target, float deltaTime, float speed)
        {
            if (speed <= 0f)
            {
                return target;
            }
            Vector3 dist = target - current;
            if (dist.sqrMagnitude < 1e-8f)
            {
                return target;
            }
            return current + dist * Mathf.Clamp01(deltaTime * speed);
        }

        // FMath::RInterpTo for a single axis
        public static float AngleInterpTo(float current, float target, float deltaTime, float speed)
        {
            if (speed <= 0f)
            {
                return target;
            }
            float delta = Mathf.DeltaAngle(current, target);
            if (Mathf.Abs(delta) < 1e-4f)
            {
                return target;
            }
            return current + delta * Mathf.Clamp01(deltaTime * speed);
        }

        // FMath::RInterpConstantTo for a single axis (speed in degrees per second)
        public static float AngleInterpConstantTo(float current, float target, float deltaTime, float speed)
        {
            if (deltaTime == 0f)
            {
                return current;
            }
            if (speed <= 0f)
            {
                return target;
            }
            float delta = Mathf.DeltaAngle(current, target);
            float step = deltaTime * speed;
            return current + Mathf.Clamp(delta, -step, step);
        }

        public static Quaternion InterpTo(Quaternion current, Quaternion target, float deltaTime, float speed)
        {
            if (speed <= 0f)
            {
                return target;
            }
            return Quaternion.Slerp(current, target, Mathf.Clamp01(deltaTime * speed));
        }

        // FMath::GetMappedRangeValueClamped
        public static float MapRangeClamped(float inA, float inB, float outA, float outB, float value)
        {
            return Mathf.Lerp(outA, outB, Mathf.InverseLerp(inA, inB, value));
        }

        /// <summary>Yaw (degrees) of a direction on the ground plane.</summary>
        public static float YawOf(Vector3 direction)
        {
            return Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        }

        public static Vector3 DirectionOfYaw(float yaw)
        {
            float rad = yaw * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
        }

        public static Vector3 Flatten(Vector3 v)
        {
            v.y = 0f;
            return v;
        }

        // UALSMathLibrary::AngleInRange
        public static bool AngleInRange(float angle, float minAngle, float maxAngle, float buffer, bool increaseBuffer)
        {
            if (increaseBuffer)
            {
                return angle >= minAngle - buffer && angle <= maxAngle + buffer;
            }
            return angle >= minAngle + buffer && angle <= maxAngle - buffer;
        }

        // UALSMathLibrary::CalculateQuadrant
        public static ALSMovementDirection CalculateQuadrant(ALSMovementDirection current, float frThreshold,
            float flThreshold, float brThreshold, float blThreshold, float buffer, float angle)
        {
            if (AngleInRange(angle, flThreshold, frThreshold, buffer,
                    current != ALSMovementDirection.Forward && current != ALSMovementDirection.Backward))
            {
                return ALSMovementDirection.Forward;
            }

            if (AngleInRange(angle, frThreshold, brThreshold, buffer,
                    current != ALSMovementDirection.Right && current != ALSMovementDirection.Left))
            {
                return ALSMovementDirection.Right;
            }

            if (AngleInRange(angle, blThreshold, flThreshold, buffer,
                    current != ALSMovementDirection.Right && current != ALSMovementDirection.Left))
            {
                return ALSMovementDirection.Left;
            }

            return ALSMovementDirection.Backward;
        }

        /// <summary>Piecewise-linear curve from (time, value) pairs.</summary>
        public static AnimationCurve LinearCurve(params float[] timeValuePairs)
        {
            var curve = new AnimationCurve();
            for (int i = 0; i + 1 < timeValuePairs.Length; i += 2)
            {
                curve.AddKey(new Keyframe(timeValuePairs[i], timeValuePairs[i + 1]));
            }
            for (int i = 0; i < curve.length; i++)
            {
                Keyframe key = curve[i];
                key.inTangent = i > 0 ? Slope(curve[i - 1], key) : 0f;
                key.outTangent = i < curve.length - 1 ? Slope(key, curve[i + 1]) : 0f;
                curve.MoveKey(i, key);
            }
            return curve;
        }

        /// <summary>Smooth (auto tangent) curve from (time, value) pairs.</summary>
        public static AnimationCurve SmoothCurve(params float[] timeValuePairs)
        {
            var curve = new AnimationCurve();
            for (int i = 0; i + 1 < timeValuePairs.Length; i += 2)
            {
                curve.AddKey(new Keyframe(timeValuePairs[i], timeValuePairs[i + 1]));
            }
            for (int i = 0; i < curve.length; i++)
            {
                curve.SmoothTangents(i, 0f);
            }
            return curve;
        }

        /// <summary>Curve with flat tangents on every key (ease in / ease out between keys).</summary>
        public static AnimationCurve EaseCurve(params float[] timeValuePairs)
        {
            var curve = new AnimationCurve();
            for (int i = 0; i + 1 < timeValuePairs.Length; i += 2)
            {
                curve.AddKey(new Keyframe(timeValuePairs[i], timeValuePairs[i + 1], 0f, 0f));
            }
            return curve;
        }

        private static float Slope(Keyframe a, Keyframe b)
        {
            float dt = b.time - a.time;
            return dt > 1e-6f ? (b.value - a.value) / dt : 0f;
        }
    }
}
