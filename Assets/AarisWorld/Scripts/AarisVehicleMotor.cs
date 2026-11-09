using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// One physics authority per vehicle, driven by four input intents.
    /// Real WheelColliders supply traction, suspension, braking and steering;
    /// never write Rigidbody.linearVelocity to fake acceleration.
    /// </summary>
    [DisallowMultipleComponent, RequireComponent(typeof(Rigidbody))]
    [DefaultExecutionOrder(-40)]
    public sealed class AarisVehicleMotor : MonoBehaviour
    {
        private const float WheelRadius = 0.39f;
        private readonly WheelCollider[] wheels = new WheelCollider[4];
        private readonly Transform[] visualWheels = new Transform[4];
        private Rigidbody body;
        private AarisVehicleSession driver;
        private float forward, steering, smoothSteer;
        private float topSpeed;
        private const float ServiceBrakeTorque = 3200f;
        private const float ParkingBrakeTorque = 3600f;
        private const float AntiRollStrength = 4800f;
        private float nextTelemetryAt;
        public string VehicleId { get; private set; }
        public bool IsOccupied => driver != null;
        // Never attach the player/camera to a rolling, spinning, flipped or
        // airborne vehicle. This is a separate, stricter entry precondition
        // from the exit safety test below.
        public bool IsSafeToEnter
        {
            get
            {
                if (body == null || body.linearVelocity.magnitude >= 1.5f ||
                    body.angularVelocity.magnitude >= .8f ||
                    Vector3.Dot(transform.up, Vector3.up) < .72f)
                    return false;
                int grounded = 0;
                foreach (var wheel in wheels)
                    if (wheel != null && wheel.isGrounded) grounded++;
                return grounded >= 2;
            }
        }
        public bool CanPark => !IsOccupied && body != null &&
            body.linearVelocity.sqrMagnitude < .20f &&
            body.angularVelocity.sqrMagnitude < .16f;
        public float SpeedMetresPerSecond => body != null
            ? Vector3.Dot(body.linearVelocity, transform.forward) : 0f;
        public float TotalSpeedMetresPerSecond => body != null
            ? body.linearVelocity.magnitude : 0f;

        /// <summary>
        /// Reject EXIT while sliding sideways, rotating, upside-down or
        /// airborne. Checking only longitudinal speed permitted exiting
        /// a sideways-sliding car at road speed.
        /// </summary>
        public bool IsSafeToExit
        {
            get
            {
                if (body == null || TotalSpeedMetresPerSecond >= 1.1f ||
                    body.angularVelocity.magnitude >= .5f ||
                    Vector3.Dot(transform.up, Vector3.up) < .72f)
                    return false;
                int grounded = 0;
                foreach (var wheel in wheels)
                    if (wheel != null && wheel.isGrounded) grounded++;
                return grounded >= 2;
            }
        }

        public void Configure(string id, float maxSpeedMetres, float kilograms)
        {
            VehicleId = id;
            topSpeed = Mathf.Clamp(maxSpeedMetres, 16f, 28f);
            body = GetComponent<Rigidbody>();
            body.mass = Mathf.Clamp(kilograms, 850f, 2300f);
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.linearDamping = .12f;
            body.angularDamping = .65f;
            body.maxAngularVelocity = 3f;
            // A low but plausible COM improves stability; it does not
            // guarantee that a flipped car will right itself.
            body.centerOfMass = new Vector3(0f, -.27f, 0f);
            var shell = GetComponent<BoxCollider>();
            if (shell == null) shell = gameObject.AddComponent<BoxCollider>();
            shell.center = new Vector3(0f, .52f, 0f);
            shell.size = new Vector3(1.85f, 1.03f, 3.85f);
            CreateWheel(0, -.84f, 1.38f);
            CreateWheel(1, .84f, 1.38f);
            CreateWheel(2, -.84f, -1.38f);
            CreateWheel(3, .84f, -1.38f);
        }

        private void CreateWheel(int index, float x, float z)
        {
            var anchor = new GameObject("PhysicsWheel_" + index);
            anchor.transform.SetParent(transform, false);
            anchor.transform.localPosition = new Vector3(x, -.10f, z);
            var wheel = anchor.AddComponent<WheelCollider>();
            wheel.radius = WheelRadius;
            wheel.mass = 24f;
            wheel.suspensionDistance = .24f;
            wheel.wheelDampingRate = .32f;
            wheel.suspensionSpring = new JointSpring
            {
                spring = 32000f, damper = 4200f, targetPosition = .5f
            };
            WheelFrictionCurve forwardGrip = wheel.forwardFriction;
            forwardGrip.extremumSlip = .36f;
            forwardGrip.extremumValue = 1.2f;
            forwardGrip.asymptoteSlip = .83f;
            forwardGrip.asymptoteValue = .83f;
            forwardGrip.stiffness = 1.15f;
            wheel.forwardFriction = forwardGrip;
            WheelFrictionCurve sideGrip = wheel.sidewaysFriction;
            sideGrip.extremumSlip = .28f;
            sideGrip.extremumValue = 1.2f;
            sideGrip.asymptoteSlip = .7f;
            sideGrip.asymptoteValue = .85f;
            sideGrip.stiffness = 1.35f;
            wheel.sidewaysFriction = sideGrip;
            wheels[index] = wheel;
        }

        /// <summary>
        /// Instantiate REAL separate FBX wheel geometry, not primitive
        /// cylinders. Synchronize rendered pose to each WheelCollider.
        /// No duplicated physics colliders exist on the visible wheels.
        /// </summary>
        public bool AttachWheelModels(GameObject wheelPrefab, Material palette)
        {
            if (wheelPrefab == null || palette == null) return false;
            for (int i = 0; i < wheels.Length; i++)
            {
                var instance = Instantiate(wheelPrefab, transform, false);
                instance.name = "Kenney Wheel Visual " + i;
                foreach (var obstacle in instance.GetComponentsInChildren<Collider>(true))
                    obstacle.enabled = false;
                var renderers = instance.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0)
                {
                    Destroy(instance);
                    return false;
                }
                Bounds combined = renderers[0].bounds;
                foreach (var renderer in renderers)
                {
                    combined.Encapsulate(renderer.bounds);
                    int n = Mathf.Max(1, renderer.sharedMaterials.Length);
                    var materials = new Material[n];
                    for (int j = 0; j < n; j++) materials[j] = palette;
                    renderer.sharedMaterials = materials;
                }
                float maxDimension = Mathf.Max(combined.size.x,
                    Mathf.Max(combined.size.y, combined.size.z));
                if (maxDimension < .001f)
                {
                    Destroy(instance);
                    return false;
                }
                instance.transform.localScale *=
                    Mathf.Clamp(2f * WheelRadius / maxDimension, .01f, 30f);
                visualWheels[i] = instance.transform;
            }
            SyncVisibleWheels();
            return true;
        }

        private void SyncVisibleWheels()
        {
            for (int i = 0; i < wheels.Length; i++)
            {
                if (wheels[i] == null || visualWheels[i] == null) continue;
                wheels[i].GetWorldPose(out Vector3 worldPosition,
                    out Quaternion worldRotation);
                visualWheels[i].SetPositionAndRotation(worldPosition, worldRotation);
            }
        }

        private void LateUpdate() => SyncVisibleWheels();

        public bool TryTakeDriver(AarisVehicleSession session)
        {
            if (session == null || IsOccupied || !IsSafeToEnter) return false;
            driver = session;
            forward = steering = smoothSteer = 0f;
            return true;
        }

        public void SetInput(float forwardAxis, float steerAxis)
        {
            if (driver == null) return;
            // Ignore corrupted/non-finite input rather than poisoning the
            // WheelCollider torque or steering angle with NaN.
            if (float.IsNaN(forwardAxis) || float.IsInfinity(forwardAxis)) forwardAxis = 0f;
            if (float.IsNaN(steerAxis) || float.IsInfinity(steerAxis)) steerAxis = 0f;
            forward = Mathf.Clamp(forwardAxis, -1f, 1f);
            steering = Mathf.Clamp(steerAxis, -1f, 1f);
        }

        public void ReleaseDriver(AarisVehicleSession session)
        {
            if (session != driver) return;
            driver = null;
            forward = steering = 0f;
            for (int i = 0; i < wheels.Length; i++)
                if (wheels[i] != null)
                {
                    wheels[i].motorTorque = 0f;
                    wheels[i].brakeTorque = ParkingBrakeTorque;
                }
        }

        private void FixedUpdate()
        {
            if (body == null || wheels[0] == null) return;
            float speed = SpeedMetresPerSecond;
            float speedAbs = Mathf.Abs(speed);
            float throttle = driver != null ? forward : 0f;
            float turn = driver != null ? steering : 0f;

            // Four progressive torque bands are speed-derived, not arbitrary
            // teleporting 9/16/22/28 m/s velocities after an input timer.
            float torque = speedAbs < 7f ? 1500f : speedAbs < 14f ? 1180f :
                speedAbs < 21f ? 850f : 580f;
            float drive = 0f, brake = 0f;
            if (throttle > .08f)
            {
                if (speed < -.55f) brake = ServiceBrakeTorque * throttle;
                else if (speed < topSpeed) drive = torque * throttle;
            }
            else if (throttle < -.08f)
            {
                if (speed > .55f) brake = ServiceBrakeTorque * -throttle;
                else if (speed > -7f) drive = -torque * .48f * -throttle;
            }
            else
            {
                // Realistic rolling drag; touching neither arrow coasts
                // rather than stopping instantly in mid-air.
                brake = driver == null ? ParkingBrakeTorque : 155f;
            }
            // Downhill speed control: apply brake instead of directly
            // overwriting velocity, which would ignore collision impulses.
            if (speed > topSpeed + .6f || speed < -8f)
                brake = Mathf.Max(brake, 1100f);
            for (int i = 0; i < wheels.Length; i++)
            {
                wheels[i].motorTorque = i >= 2 ? drive * .5f : 0f;
                wheels[i].brakeTorque = brake;
            }
            // Gradually reduce steering angle at speed so 100km/h inputs
            // cannot instantaneously rotate a multi-ton vehicle by 70°/s.
            float maxAngle = Mathf.Lerp(32f, 10f, Mathf.InverseLerp(0f, 24f, speedAbs));
            float target = turn * maxAngle;
            smoothSteer = Mathf.MoveTowards(smoothSteer, target, 125f * Time.fixedDeltaTime);
            wheels[0].steerAngle = wheels[1].steerAngle = smoothSteer;

            // Axle load transfer: counter excessive roll on fast bends and
            // uneven slopes without freezing Rigidbody rotation or faking
            // wheel contact. Only grounded wheels receive corrective force.
            ApplyAntiRoll(0, 1);
            ApplyAntiRoll(2, 3);

            // Mild aero downforce supports handling but cannot defeat walls.
            if (speedAbs > 6f)
                body.AddForce(-transform.up * Mathf.Min(18000f, 20f * speedAbs * speedAbs),
                    ForceMode.Force);

            // Diagnostic telemetry is development-build only and bounded to
            // one line every three seconds while occupied. This lets actual
            // Unity/Android logs prove whether wheels contact terrain, how
            // fast the car drives, and whether steering/brakes work.
            if (Debug.isDebugBuild && driver != null && Time.time >= nextTelemetryAt)
            {
                nextTelemetryAt = Time.time + 3f;
                int grounded = 0;
                for (int i = 0; i < wheels.Length; i++)
                    if (wheels[i] != null && wheels[i].isGrounded) grounded++;
                Debug.Log("AARIS_DRIVE_TELEMETRY id=" + VehicleId +
                    " speed_mps=" + speed.ToString("F2") +
                    " total_mps=" + TotalSpeedMetresPerSecond.ToString("F2") +
                    " wheels_grounded=" + grounded +
                    " steer_deg=" + smoothSteer.ToString("F1") +
                    " throttle=" + throttle.ToString("F2") +
                    " brake_Nm=" + brake.ToString("F0") +
                    " roll_deg=" + Vector3.Angle(transform.up, Vector3.up).ToString("F1"));
            }
        }

        private void ApplyAntiRoll(int leftIndex, int rightIndex)
        {
            var left = wheels[leftIndex];
            var right = wheels[rightIndex];
            if (left == null || right == null || body == null) return;

            bool leftGrounded = left.GetGroundHit(out WheelHit leftHit);
            bool rightGrounded = right.GetGroundHit(out WheelHit rightHit);
            float leftTravel = 1f, rightTravel = 1f;
            if (leftGrounded)
                leftTravel = Mathf.Clamp01(
                    (-left.transform.InverseTransformPoint(leftHit.point).y -
                        left.radius) / Mathf.Max(.01f, left.suspensionDistance));
            if (rightGrounded)
                rightTravel = Mathf.Clamp01(
                    (-right.transform.InverseTransformPoint(rightHit.point).y -
                        right.radius) / Mathf.Max(.01f, right.suspensionDistance));
            float force = Mathf.Clamp((leftTravel - rightTravel) * AntiRollStrength,
                -AntiRollStrength, AntiRollStrength);
            if (leftGrounded)
                body.AddForceAtPosition(left.transform.up * -force,
                    left.transform.position, ForceMode.Force);
            if (rightGrounded)
                body.AddForceAtPosition(right.transform.up * force,
                    right.transform.position, ForceMode.Force);
        }

        private void OnDisable()
        {
            forward = steering = 0f;
            foreach (var wheel in wheels)
            {
                if (wheel == null) continue;
                wheel.motorTorque = 0f;
                wheel.brakeTorque = ParkingBrakeTorque;
            }
        }
    }
}
