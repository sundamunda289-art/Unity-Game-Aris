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
        private const float BaseForwardGripStiffness = 1.15f;
        private const float BaseSideGripStiffness = 1.35f;
        private readonly WheelCollider[] wheels = new WheelCollider[4];
        private readonly float[] surfaceGripMultipliers = new float[4];
        private readonly Transform[] visualWheels = new Transform[4];
        // Imported vehicle bodies may carry four real wheel meshes with their
        // own authored local axes. Preserve that rest orientation while the
        // WheelCollider supplies steering, suspension and spin.
        private readonly Quaternion[] visualWheelRotationOffsets =
            new Quaternion[4];
        private Rigidbody body;
        private AarisVehicleSession driver;
        private float forward, steering, smoothSteer;
        private float topSpeed, tyreSlip;
        private const float ServiceBrakeTorque = 3200f;
        private const float ParkingBrakeTorque = 3600f;
        private const float AntiRollStrength = 4800f;
        private float nextTelemetryAt;
        // Stop native wheel pose queries only after a parked car truly sleeps.
        private bool sleepingWheelPoseCurrent;
        public string VehicleId { get; private set; }
        public float TopSpeedMetresPerSecond => topSpeed;
        public float ThrottleInput => driver == null ? 0f : forward;
        public float LateralSlipMetresPerSecond => body == null ? 0f :
            Mathf.Abs(Vector3.Dot(body.linearVelocity, transform.right));
        // Peak contact slip from the same WheelHit samples already used by
        // anti-roll. Audio/VFX can react to real tyre friction instead of
        // inferring skid from chassis velocity, with no additional ray/query.
        public float TyreSlip => tyreSlip;
        public bool IsOccupied => driver != null;
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

        private void OnCollisionEnter(Collision collision)
        {
            if (driver != null && collision != null)
                AarisAudioDirector.VehicleCrash(transform.position,
                    collision.relativeVelocity.magnitude);
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
            // Two/three PhysX vehicle substeps stabilize tyre/suspension forces
            // without reducing the project's global fixed timestep. Sleeping
            // parked cars remain asleep, so the mobile cost stays bounded.
            if (wheels[0] != null)
                wheels[0].ConfigureVehicleSubsteps(12f, 3, 2);
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
            // Keep suspension response consistent across the 850-2300 kg
            // vehicle catalog. Unity's WheelCollider spring/damper forces are
            // mass-proportional; fixed values make light cars harsh and heavy
            // cars float/bounce. Scale only once at construction time.
            float massScale = Mathf.Clamp(
                body != null ? body.mass / 1500f : 1f, .55f, 1.55f);
            wheel.suspensionSpring = new JointSpring
            {
                spring = 32000f * massScale,
                damper = 4200f * massScale,
                targetPosition = .5f
            };
            WheelFrictionCurve forwardGrip = wheel.forwardFriction;
            forwardGrip.extremumSlip = .36f;
            forwardGrip.extremumValue = 1.2f;
            forwardGrip.asymptoteSlip = .83f;
            forwardGrip.asymptoteValue = .83f;
            forwardGrip.stiffness = BaseForwardGripStiffness;
            wheel.forwardFriction = forwardGrip;
            WheelFrictionCurve sideGrip = wheel.sidewaysFriction;
            sideGrip.extremumSlip = .28f;
            sideGrip.extremumValue = 1.2f;
            sideGrip.asymptoteSlip = .7f;
            sideGrip.asymptoteValue = .85f;
            sideGrip.stiffness = BaseSideGripStiffness;
            wheel.sidewaysFriction = sideGrip;
            surfaceGripMultipliers[index] = 1f;
            // An initially sleeping, unoccupied car still needs parking brakes.
            wheel.brakeTorque = ParkingBrakeTorque;
            wheels[index] = wheel;
            visualWheelRotationOffsets[index] = Quaternion.identity;
        }

        /// <summary>
        /// Fit each imported vehicle's physical shell and axle track to the
        /// authored car model once on spawn, never in FixedUpdate.
        /// </summary>
        public void FitShellAndWheelbase(Bounds visualBounds)
        {
            Vector3 size = visualBounds.size;
            if (body == null || size.x < .25f || size.y < .25f || size.z < .5f ||
                float.IsNaN(size.x) || float.IsInfinity(size.x) ||
                float.IsNaN(size.y) || float.IsInfinity(size.y) ||
                float.IsNaN(size.z) || float.IsInfinity(size.z)) return;
            var shell = GetComponent<BoxCollider>();
            if (shell == null) return;
            // Wheels, not the chassis shell, must carry vehicle weight.
            float bottom = Mathf.Clamp(visualBounds.min.y + .05f, .20f, .40f);
            float top = Mathf.Clamp(visualBounds.max.y - .05f, .95f, 2.55f);
            if (top <= bottom + .45f) return;
            shell.center = new Vector3(
                Mathf.Clamp(visualBounds.center.x, -.15f, .15f),
                (top + bottom) * .5f,
                Mathf.Clamp(visualBounds.center.z, -.20f, .20f));
            shell.size = new Vector3(
                Mathf.Clamp(size.x * .94f, 1.35f, 2.25f),
                top - bottom,
                Mathf.Clamp(size.z * .94f, 2.35f, 4.30f));
            // Match the wheel geometry across short hatchbacks and longer
            // vans/SUVs without introducing additional wheel GameObjects.
            float halfTrack = Mathf.Clamp(size.x * .445f, .63f, .90f);
            float halfWheelbase = Mathf.Clamp(size.z * .322f, .92f, 1.43f);
            for (int i = 0; i < wheels.Length; i++)
            {
                if (wheels[i] == null) continue;
                wheels[i].transform.localPosition = new Vector3(
                    (i & 1) == 0 ? -halfTrack : halfTrack,
                    -.10f, i < 2 ? halfWheelbase : -halfWheelbase);
            }
            // PhysX distributes chassis mass between wheels from their
            // positions and center of mass. Recalculate after changing axle
            // spacing so hatchbacks, sedans and longer bodies do not inherit
            // the generic sedan's old front/rear load split.
            if (wheels[0] != null) wheels[0].ResetSprungMasses();
        }

        /// <summary>
        /// Instantiate REAL separate FBX wheel geometry, not primitive
        /// cylinders. Synchronize rendered pose to each WheelCollider.
        /// No duplicated physics colliders exist on the visible wheels.
        /// </summary>
        public bool AttachWheelModels(GameObject wheelPrefab, Material palette,
            bool preserveAuthoredUrpMaterials = false)
        {
            if (wheelPrefab == null ||
                (!preserveAuthoredUrpMaterials && palette == null)) return false;
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
                if (!preserveAuthoredUrpMaterials)
                {
                    foreach (var renderer in renderers)
                    {
                        int n = AarisVehicleWorld.VisualSubmeshCount(renderer);
                        var materials = new Material[n];
                        for (int j = 0; j < n; j++) materials[j] = palette;
                        renderer.sharedMaterials = materials;
                    }
                }
                // Wheel size must not shrink or grow merely because a car
                // is parked at a different compass heading.
                if (!AarisVehicleWorld.TryGetVisualBoundsLocal(
                    instance.transform, out Bounds combined))
                { Destroy(instance); return false; }
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
                visualWheelRotationOffsets[i] = Quaternion.identity;
            }
            SyncVisibleWheels();
            return true;
        }

        /// <summary>
        /// Reuse the four separate wheel objects already authored inside a
        /// higher-fidelity FBX body. This avoids duplicate toy wheels and keeps
        /// the imported tyre/rim materials while WheelColliders remain the only
        /// physics authority. The model contract is *_wheel_fl/fr/rl/rr.
        /// </summary>
        public bool AttachEmbeddedWheelModels(Transform bodyRoot)
        {
            if (bodyRoot == null || body == null) return false;
            string[] tokens = { "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr" };
            Transform[] all = bodyRoot.GetComponentsInChildren<Transform>(true);
            Transform[] found = new Transform[4];
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                string label = all[i].name.ToLowerInvariant();
                for (int w = 0; w < tokens.Length; w++)
                    if (found[w] == null && label.Contains(tokens[w]))
                        found[w] = all[i];
            }
            for (int i = 0; i < found.Length; i++)
                if (found[i] == null || wheels[i] == null) return false;

            for (int i = 0; i < found.Length; i++)
            {
                Transform wheelVisual = found[i];
                foreach (var obstacle in
                    wheelVisual.GetComponentsInChildren<Collider>(true))
                    obstacle.enabled = false;

                Renderer[] renderers =
                    wheelVisual.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0) return false;
                Bounds worldBounds = renderers[0].bounds;
                for (int r = 1; r < renderers.Length; r++)
                    worldBounds.Encapsulate(renderers[r].bounds);

                // Source metadata defines local X as the roll axle. Use the
                // world-space Y/Z footprint for physical tyre radius and clamp
                // corrupted imports to plausible passenger-vehicle dimensions.
                float diameter = Mathf.Max(worldBounds.size.y,
                    worldBounds.size.z);
                wheels[i].radius = Mathf.Clamp(diameter * 0.5f, .27f, .46f);
                Vector3 localCenter = transform.InverseTransformPoint(
                    worldBounds.center);
                wheels[i].transform.localPosition = localCenter;

                Quaternion restOffset =
                    Quaternion.Inverse(transform.rotation) * wheelVisual.rotation;
                wheelVisual.SetParent(transform, true);
                visualWheels[i] = wheelVisual;
                visualWheelRotationOffsets[i] = restOffset;
            }
            if (wheels[0] != null) wheels[0].ResetSprungMasses();
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
                visualWheels[i].SetPositionAndRotation(worldPosition,
                    worldRotation * visualWheelRotationOffsets[i]);
            }
        }

        private void LateUpdate()
        {
            if (body == null) return;
            // WheelCollider poses are static while an unoccupied Rigidbody sleeps.
            // Continue every-frame synchronization whenever the car is driven,
            // moving or awakened by a collision; never reduce active wheel FPS.
            bool parkedSleeping = driver == null && body.IsSleeping();
            if (parkedSleeping && sleepingWheelPoseCurrent) return;
            SyncVisibleWheels();
            sleepingWheelPoseCurrent = parkedSleeping;
        }

        private void OnEnable() => sleepingWheelPoseCurrent = false;

        public bool TryTakeDriver(AarisVehicleSession session)
        {
            // Never board a car the player could not safely exit.
            // A slow overturned or airborne car could otherwise trap the
            // walking motor with no legal exit position.
            if (session == null || IsOccupied || body == null ||
                !IsSafeToExit) return false;
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
            // Brakes are initialized in CreateWheel and restored on exit.
            // A sleeping unoccupied car needs no torque/steer writes, tyre
            // contact queries, anti-roll forces or diagnostic calculations.
            // On physics wake, processing resumes in the very next step.
            if (driver == null && body.IsSleeping()) return;
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
            // A sideways skid can have near-zero forward speed even at
            // road speed. Reduce steering by actual planar vehicle speed.
            float planarSpeed = Vector3.ProjectOnPlane(
                body.linearVelocity, transform.up).magnitude;
            float maxAngle = Mathf.Lerp(32f, 10f, Mathf.InverseLerp(0f, 24f, planarSpeed));
            float target = turn * maxAngle;
            smoothSteer = Mathf.MoveTowards(smoothSteer, target, 125f * Time.fixedDeltaTime);
            ApplyAckermannSteering(smoothSteer);

            // Axle load transfer: counter excessive roll on fast bends and
            // uneven slopes without freezing Rigidbody rotation or faking
            // wheel contact. Only grounded wheels receive corrective force.
            // Reset once per physics step; ApplyAntiRoll reuses each WheelHit
            // to accumulate real longitudinal/lateral tyre slip.
            tyreSlip = 0f;
            ApplyAntiRoll(0, 1);
            ApplyAntiRoll(2, 3);

            // Mild aero downforce supports handling but cannot defeat walls.
            if (planarSpeed > 6f)
                body.AddForce(-transform.up * Mathf.Min(18000f, 20f * planarSpeed * planarSpeed),
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
                    " tyre_slip=" + tyreSlip.ToString("F2") +
                    " roll_deg=" + Vector3.Angle(transform.up, Vector3.up).ToString("F1"));
            }
        }

        // Inside and outside tyres follow different radii in a turn.
        // Read the actual fitted wheel positions, so every vehicle body type
        // keeps correct steering geometry without new per-frame allocations.
        private void ApplyAckermannSteering(float centerAngle)
        {
            if (Mathf.Abs(centerAngle) < .01f)
            {
                wheels[0].steerAngle = wheels[1].steerAngle = centerAngle;
                return;
            }
            float track = Mathf.Abs(wheels[1].transform.localPosition.x -
                                    wheels[0].transform.localPosition.x);
            float wheelbase = Mathf.Abs(wheels[0].transform.localPosition.z -
                                        wheels[2].transform.localPosition.z);
            if (track < .01f || wheelbase < .01f)
            {
                wheels[0].steerAngle = wheels[1].steerAngle = centerAngle;
                return;
            }
            float radius = wheelbase / Mathf.Tan(Mathf.Abs(centerAngle) * Mathf.Deg2Rad);
            float inner = Mathf.Atan2(wheelbase, Mathf.Max(.01f, radius - track * .5f)) * Mathf.Rad2Deg;
            float outer = Mathf.Atan2(wheelbase, radius + track * .5f) * Mathf.Rad2Deg;
            wheels[0].steerAngle = centerAngle > 0f ? outer : -inner;
            wheels[1].steerAngle = centerAngle > 0f ? inner : -outer;
        }

        // WheelCollider friction is independent from ordinary PhysicMaterial.
        // Reuse the WheelHit samples already needed for anti-roll, so surface
        // traction changes add no raycasts/overlaps to the 2 GB-phone hotpath.
        // Terrain gets a modest loose-surface reduction, hard road/bridge meshes
        // keep the authored tyre curve, and Water-layer contacts become slick.
        private static float SurfaceGripMultiplier(Collider surface)
        {
            if (surface == null) return 1f;
            if (surface.gameObject.layer == 4) return .42f; // built-in Water layer
            if (surface is TerrainCollider) return .90f;
            return 1f;
        }

        private void ApplySurfaceGrip(int wheelIndex, WheelCollider wheel,
            Collider surface)
        {
            if (wheel == null || wheelIndex < 0 ||
                wheelIndex >= surfaceGripMultipliers.Length) return;
            float multiplier = SurfaceGripMultiplier(surface);
            if (Mathf.Abs(surfaceGripMultipliers[wheelIndex] - multiplier) <= .01f)
                return;
            surfaceGripMultipliers[wheelIndex] = multiplier;

            WheelFrictionCurve forwardGrip = wheel.forwardFriction;
            forwardGrip.stiffness = BaseForwardGripStiffness * multiplier;
            wheel.forwardFriction = forwardGrip;

            WheelFrictionCurve sideGrip = wheel.sidewaysFriction;
            sideGrip.stiffness = BaseSideGripStiffness * multiplier;
            wheel.sidewaysFriction = sideGrip;
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
            {
                ApplySurfaceGrip(leftIndex, left, leftHit.collider);
                leftTravel = Mathf.Clamp01(
                    (-left.transform.InverseTransformPoint(leftHit.point).y -
                        left.radius) / Mathf.Max(.01f, left.suspensionDistance));
                tyreSlip = Mathf.Max(tyreSlip, Mathf.Max(
                    Mathf.Abs(leftHit.sidewaysSlip),
                    Mathf.Abs(leftHit.forwardSlip) * 0.78f));
            }
            if (rightGrounded)
            {
                ApplySurfaceGrip(rightIndex, right, rightHit.collider);
                rightTravel = Mathf.Clamp01(
                    (-right.transform.InverseTransformPoint(rightHit.point).y -
                        right.radius) / Mathf.Max(.01f, right.suspensionDistance));
                tyreSlip = Mathf.Max(tyreSlip, Mathf.Max(
                    Mathf.Abs(rightHit.sidewaysSlip),
                    Mathf.Abs(rightHit.forwardSlip) * 0.78f));
            }
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
