using System;
using System.Collections.Generic;
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
        // The FBX chassis is visual-only. WheelCollider meshes remain under
        // the true Rigidbody, so tyre contact/steering never inherit body sway.
        private Transform visualBodyPivot;
        private Vector3 previousPhysicsVelocity;
        private bool hasPreviousPhysicsVelocity;
        private float visualPitchTarget, visualRollTarget, visualJounceTarget;
        // Wear is only applied to verified body-paint submeshes, never to
        // tyres, lights or transparent glass renderers.
        private struct BodyPaintSlot
        {
            public Renderer renderer;
            public int index;
            public Color fresh;
            public bool atlas;
        }
        private readonly List<BodyPaintSlot> bodyPaintSlots =
            new List<BodyPaintSlot>(6);
        private MaterialPropertyBlock bodyPaintBlock;
        private float lastBodyWear = -1f;
        // Two bounded, world-space skid traces are created ONLY when a player
        // first drives this car. Parked streamed vehicles allocate zero trails.
        // All contact points reuse ApplyAntiRoll WheelHit (no extra raycasts).
        private readonly TrailRenderer[] rearTyreTrails = new TrailRenderer[2];
        private readonly Vector3[] rearContactPoints = new Vector3[2];
        private readonly Vector3[] rearContactNormals = new Vector3[2];
        private readonly float[] rearContactSlip = new float[2];
        private readonly bool[] rearContactsGrounded = new bool[2];
        private Material sharedSkidMaterial;
        // Two compact lamp clusters per end, plus two white reversing lenses.
        // Every car shares the World-owned URP materials; no runtime clones.
        private readonly Renderer[] frontLamps = new Renderer[2];
        private readonly Renderer[] rearLamps = new Renderer[2];
        private readonly Renderer[] reversingLamps = new Renderer[2];
        private Material headlampGlass, headlampOn, taillampGlass, brakeLamp;
        private Light roadHeadlight;
        private int lastLampState = -1;
        // Only the occupied car may use a real spotlight. Keep its daytime
        // beam out of the mobile URP additional-light pass; all emissive
        // lenses remain visible using their existing shared materials.
        private float nextRoadBeamSampleAt;
        private bool roadBeamNeeded = true;
        // Imported vehicle bodies may carry four real wheel meshes with their
        // own authored local axes. Preserve that rest orientation while the
        // WheelCollider supplies steering, suspension and spin.
        private readonly Quaternion[] visualWheelRotationOffsets =
            new Quaternion[4];
        // Some authentic 3D tyres have meshes offset from their Transform
        // origins (Road GT: ~9 cm at the front and ~12 cm at the rear).
        // Store this offset in wheel-pivot orientation, in WORLD metres,
        // once at attachment; WheelCollider wheel centres stay authoritative.
        private readonly Vector3[] visualWheelCenterOffsets = new Vector3[4];
        private Rigidbody body;
        private AarisVehicleSession driver;
        private float forward, steering, smoothSteer;
        private float topSpeed, tyreSlip;
        // Derived once from authored mass, never from per-frame model queries.
        // Heavier SUVs/utility cars need more wheel torque yet still accelerate
        // more slowly than light sports cars under the same WheelCollider model.
        private float driveTorqueScale = 1f;
        // One-time vehicle-specific handling; no extra physics authority.
        private float frontDriveFraction; // Legacy RWD default.
        private float tyreCompoundMultiplier = 1f;
        private float steeringResponseMultiplier = 1f;
        private float steeringMassFactor = 1f;
        private float appliedDriveTorque = float.NaN, appliedBrakeTorque = float.NaN;
        private float appliedSteeringAngle = float.NaN;
        // Cached signed brake direction avoids redundant PhysX writes when
        // the car slows/reverses while the driver holds the same arrow.
        // +1 = forward/front bias; -1 = reversing/rear bias; 0 = balanced.
        private int appliedServiceBrakeDirection;
        private const float ServiceBrakeTorque = 3200f;
        private const float ParkingBrakeTorque = 3600f;
        private const float AntiRollStrength = 4800f;
        private float antiRollMassScale = 1f;
        private int groundedWheelCount;
        // Contact count alone cannot tell whether the road supports both
        // axles: two front tyres at a crest are not two supported axles.
        // Bit 0 = front, bit 1 = rear. Reuse the SAME four WheelHit queries.
        private int groundedAxleMask;
        private float nextTelemetryAt;
        // A short impact pulse communicates a meaningful collision without
        // creating sparks/particles on phones or changing the drive authority.
        private float impactFlashUntil;
        private float nextDamageAt, damage01, nextExhaustUpdateAt;
        private Material sharedExhaustMaterial;
        private Vector3 exhaustAnchor;
        private ParticleSystem exhaustParticles;
        // Stop native wheel pose queries only after a parked car truly sleeps.
        private bool sleepingWheelPoseCurrent;
        // Cache the broadest visible body renderer once at spawn. The native
        // isVisible flag considers all cameras (and shadow passes), avoiding
        // per-frame renderer traversal, allocations or custom frustum math.
        private Renderer representativeBodyRenderer;
        private int offscreenVisualFramePhase;
        public string VehicleId { get; private set; }
        public float TopSpeedMetresPerSecond => topSpeed;
        // Camera look-ahead reads authoritative Rigidbody velocity without
        // extra physics queries or per-frame allocations.
        public Vector3 DrivingVelocityWorld =>
            body != null ? body.linearVelocity : Vector3.zero;
        public float ThrottleInput => driver == null ? 0f : forward;
        // True only when the active driver is asking the wheels to slow down;
        // parking/coasting drag must not illuminate the brake indicator.
        public bool IsServiceBraking => driver != null && appliedBrakeTorque >= 500f;
        public bool IsRecentImpact => driver != null && Time.time < impactFlashUntil;
        public float Damage01 => damage01;
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

        // A rolled car may rest motionless on its roof with zero grounded
        // wheels. Allow a deliberate low-speed EXIT attempt in that case;
        // VehicleSession still requires a supported, empty landing capsule
        // and a clear walking route before restoring the player motor.
        // Never use this relaxed condition for boarding a moving/rolled car.
        public bool CanEvacuateStoppedVehicle =>
            body != null && TotalSpeedMetresPerSecond < .65f &&
            body.angularVelocity.magnitude < .25f;

        private void OnCollisionEnter(Collision collision)
        {
            if (driver == null || collision == null) return;
            // A fast car sliding parallel to a wall can have large total
            // relative velocity but near-zero velocity INTO that wall.
            // Measure impact along each actual collision-contact normal, not
            // tangential speed. Unity GetContact avoids allocating contacts[].
            // This runs on collision entry only, never in FixedUpdate.
            Vector3 relativeVelocity = collision.relativeVelocity;
            float impactSpeed = 0f;
            for (int i = 0; i < collision.contactCount; i++)
            {
                Vector3 normal = collision.GetContact(i).normal;
                float normalSpeed = Mathf.Abs(Vector3.Dot(relativeVelocity, normal));
                impactSpeed = Mathf.Max(impactSpeed, normalSpeed);
            }
            if (impactSpeed < 4f) return;
            AarisAudioDirector.VehicleCrash(transform.position, impactSpeed);
            // Contact impulses are not reliable across Rigidbody mass/road
            // physics configurations; relative speed is already available.
            // Only substantial impacts trigger a half-second visual cue.
            if (impactSpeed >= 7f)
            {
                impactFlashUntil = Time.time + .55f;
                if (Time.time >= nextDamageAt)
                {
                    // One collision can invoke several contacts: limit wear.
                    damage01 = Mathf.Clamp(damage01 +
                        Mathf.Max(0f, impactSpeed - 7f) * .011f, 0f, .85f);
                    nextDamageAt = Time.time + .75f;
                    RefreshBodyWear();
                }
                RefreshExteriorLights();
            }
        }

        public void Configure(string id, float maxSpeedMetres, float kilograms)
        {
            VehicleId = id;
            topSpeed = Mathf.Clamp(maxSpeedMetres, 16f, 28f);
            body = GetComponent<Rigidbody>();
            body.mass = Mathf.Clamp(kilograms, 850f, 2300f);
            driveTorqueScale = Mathf.Clamp(Mathf.Sqrt(body.mass / 1250f),
                .88f, 1.28f);
            // Mass-aware sway resistance: the same fixed anti-roll force
            // leaves heavy vans leaning excessively and over-stiffens light
            // sports cars. One bounded one-time calculation per spawn.
            antiRollMassScale = Mathf.Clamp(
                Mathf.Sqrt(body.mass / 1250f), .85f, 1.24f);
            steeringMassFactor = Mathf.Lerp(1f, .88f,
                Mathf.InverseLerp(1150f, 2100f, body.mass));
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
            appliedDriveTorque = appliedBrakeTorque = appliedSteeringAngle = float.NaN;
            appliedServiceBrakeDirection = 0;
            // Two/three PhysX vehicle substeps stabilize tyre/suspension forces
            // without reducing the project's global fixed timestep. Sleeping
            // parked cars remain asleep, so the mobile cost stays bounded.
            if (wheels[0] != null)
                wheels[0].ConfigureVehicleSubsteps(12f, 3, 2);
        }

        private static float SafeHandlingScale(float value, float minimum, float maximum)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f)
                return 1f;
            return Mathf.Clamp(value, minimum, maximum);
        }

        /// <summary>
        /// Original mobile-oriented drivetrain/suspension profiles. Split the
        /// same total torque between real WheelCollider axles, never teleport
        /// velocity or create new mesh/physics materials in FixedUpdate.
        /// </summary>
        public void ConfigureHandling(string drivetrain, float tyreGrip,
            float suspension, float steeringResponse,
            float suspensionTravelScale = 1f)
        {
            if (body == null || wheels[0] == null) return;
            frontDriveFraction = string.Equals(drivetrain, "FWD",
                StringComparison.OrdinalIgnoreCase) ? 1f :
                string.Equals(drivetrain, "AWD",
                StringComparison.OrdinalIgnoreCase) ? .46f : 0f;
            tyreCompoundMultiplier = SafeHandlingScale(tyreGrip, .88f, 1.12f);
            steeringResponseMultiplier = SafeHandlingScale(
                steeringResponse, .78f, 1.16f);
            float springScale = SafeHandlingScale(suspension, .86f, 1.18f);
            float travelScale = SafeHandlingScale(
                suspensionTravelScale, .82f, 1.25f);
            for (int i = 0; i < wheels.Length; i++)
            {
                WheelCollider wheel = wheels[i];
                if (wheel == null) continue;
                // Short-stroke sports cars have firmer control; off-road
                // axles can articulate farther without a second physics rig.
                // Keep the existing mass-aware baseline and damp in proportion
                // to natural frequency rather than adding constant forces.
                wheel.suspensionDistance = Mathf.Clamp(
                    wheel.suspensionDistance * travelScale, .18f, .31f);
                JointSpring spring = wheel.suspensionSpring;
                spring.spring *= springScale / Mathf.Sqrt(travelScale);
                spring.damper *= Mathf.Sqrt(springScale);
                spring.damper /= Mathf.Sqrt(travelScale);
                wheel.suspensionSpring = spring;
                WheelFrictionCurve forwardGrip = wheel.forwardFriction;
                forwardGrip.stiffness = BaseForwardGripStiffness *
                    tyreCompoundMultiplier;
                wheel.forwardFriction = forwardGrip;
                WheelFrictionCurve sideGrip = wheel.sidewaysFriction;
                sideGrip.stiffness = BaseSideGripStiffness *
                    tyreCompoundMultiplier;
                wheel.sidewaysFriction = sideGrip;
            }
            appliedDriveTorque = float.NaN;
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
            visualWheelCenterOffsets[index] = Vector3.zero;
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
            // On imported cars the real tyre-bottom alignment can put the
            // chassis shell below its root pivot. Keep the solid shell over
            // its suspension contact patch rather than floating above it.
            float bottom = Mathf.Clamp(visualBounds.min.y + .10f, -.35f, .40f);
            float top = Mathf.Clamp(visualBounds.max.y - .05f, .65f, 2.55f);
            if (top <= bottom + .45f) return;
            shell.center = new Vector3(
                Mathf.Clamp(visualBounds.center.x, -.15f, .15f),
                (top + bottom) * .5f,
                Mathf.Clamp(visualBounds.center.z, -.20f, .20f));
            shell.size = new Vector3(
                Mathf.Clamp(size.x * .94f, 1.35f, 2.25f),
                top - bottom,
                Mathf.Clamp(size.z * .94f, 2.35f, 5.20f));
            // Match the wheel geometry across short hatchbacks and longer
            // vans/SUVs without introducing additional wheel GameObjects.
            float halfTrack = Mathf.Clamp(size.x * .445f, .63f, .90f);
            float halfWheelbase = Mathf.Clamp(size.z * .322f, .92f, 1.72f);
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
            appliedSteeringAngle = float.NaN;
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
                if (renderers.Length == 0 ||
                    !AarisVehicleWorld.HasRenderableWheelGeometry(instance.transform))
                {
                    // A Renderer component alone does not make this tyre visible.
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
                // Imported prefab pivots need not coincide with the rendered
                // tyre centre. The measurement is in wheel-local orientation
                // AFTER sizing, so steering/spin rotate it without drifting.
                Vector3 visualCentreWorld =
                    instance.transform.TransformPoint(combined.center);
                visualWheelCenterOffsets[i] =
                    Quaternion.Inverse(instance.transform.rotation) *
                    (visualCentreWorld - instance.transform.position);
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
            // Validate all four tyres before mutating any physics or art.
            // Direct chassis-space mesh bounds cancel spawn yaw and slope;
            // a world AABB can inflate the diameter of a diagonal tyre.
            var bounds = new Bounds[4];
            for (int i = 0; i < found.Length; i++)
            {
                if (found[i] == null || wheels[i] == null ||
                    !AarisVehicleWorld.HasRenderableWheelGeometry(found[i]) ||
                    !AarisVehicleWorld.TryGetVisualBoundsLocal(
                        transform, found[i], out bounds[i])) return false;
                Vector3 size = bounds[i].size;
                Vector3 center = bounds[i].center;
                if (size.x <= .001f || size.y <= .001f || size.z <= .001f ||
                    float.IsNaN(size.sqrMagnitude) || float.IsInfinity(size.sqrMagnitude) ||
                    float.IsNaN(center.sqrMagnitude) || float.IsInfinity(center.sqrMagnitude))
                    return false;
            }

            // Reject a wrongly-imported or misnamed four-wheel source BEFORE
            // modifying any WheelCollider, Transform, renderer, or Rigidbody.
            // Otherwise a single inverted axle can make the entire car
            // visually drive backwards despite a correctly-fitted shell.
            if (!AreAuthoredWheelAxlesPlausible(bounds)) return false;

            for (int i = 0; i < found.Length; i++)
            {
                Transform wheelVisual = found[i];
                foreach (var obstacle in
                    wheelVisual.GetComponentsInChildren<Collider>(true))
                    obstacle.enabled = false;

                // The roll axle is chassis X. Measure the Y/Z diameter
                // and center in that same frame used by WheelCollider.
                float diameter = Mathf.Max(bounds[i].size.y, bounds[i].size.z);
                wheels[i].radius = Mathf.Clamp(diameter * 0.5f, .27f, .46f);
                wheels[i].transform.localPosition = bounds[i].center;

                Quaternion restOffset =
                    Quaternion.Inverse(transform.rotation) * wheelVisual.rotation;
                // Compute BEFORE detaching the wheel from its authored body.
                // Mesh.bounds centre may be 9-12 cm inward from the FBX/OBJ
                // Transform pivot. If we place that pivot on the WheelCollider
                // pose, the visible tyre shifts inward a second time.
                Vector3 visualCentreWorld =
                    transform.TransformPoint(bounds[i].center);
                Vector3 centreOffset =
                    Quaternion.Inverse(wheelVisual.rotation) *
                    (visualCentreWorld - wheelVisual.position);
                wheelVisual.SetParent(transform, true);
                visualWheels[i] = wheelVisual;
                visualWheelRotationOffsets[i] = restOffset;
                visualWheelCenterOffsets[i] = centreOffset;
            }
            if (wheels[0] != null) wheels[0].ResetSprungMasses();
            appliedSteeringAngle = float.NaN;
            SyncVisibleWheels();
            return true;
        }

        /// <summary>
        /// Validate actual wheel-mesh centres in chassis space, before we
        /// detach imported wheels. FL/FR must form the forward axle; RL/RR
        /// must form the rear axle with positive track and parallel heights.
        /// Broad tolerances preserve genuinely different SUV/van/buggy shapes.
        /// This is a one-time source-art check, not a driving-frame query.
        /// </summary>
        private static bool AreAuthoredWheelAxlesPlausible(Bounds[] tyres)
        {
            if (tyres == null || tyres.Length != 4) return false;
            Vector3 fl = tyres[0].center;
            Vector3 fr = tyres[1].center;
            Vector3 rl = tyres[2].center;
            Vector3 rr = tyres[3].center;
            float frontTrack = fr.x - fl.x;
            float rearTrack = rr.x - rl.x;
            float frontZ = (fl.z + fr.z) * .5f;
            float rearZ = (rl.z + rr.z) * .5f;
            float wheelbase = frontZ - rearZ;
            // The source names must agree with the steering/front axle.
            if (frontTrack < 1.02f || frontTrack > 2.40f ||
                rearTrack < 1.02f || rearTrack > 2.40f ||
                wheelbase < 1.55f || wheelbase > 4.65f) return false;
            // Reject an importer that distorts one tyre of a matched axle:
            // broad wheel-diameter limits and axle spacing alone can still
            // admit a cartoonishly oversized left/right tyre. Verify once
            // before detaching meshes or changing the WheelColliders.
            float frontLeftDiameter = Mathf.Max(tyres[0].size.y, tyres[0].size.z);
            float frontRightDiameter = Mathf.Max(tyres[1].size.y, tyres[1].size.z);
            float rearLeftDiameter = Mathf.Max(tyres[2].size.y, tyres[2].size.z);
            float rearRightDiameter = Mathf.Max(tyres[3].size.y, tyres[3].size.z);
            float frontTolerance = Mathf.Max(.07f,
                Mathf.Max(frontLeftDiameter, frontRightDiameter) * .12f);
            float rearTolerance = Mathf.Max(.07f,
                Mathf.Max(rearLeftDiameter, rearRightDiameter) * .12f);
            if (Mathf.Abs(frontLeftDiameter - frontRightDiameter) > frontTolerance ||
                Mathf.Abs(rearLeftDiameter - rearRightDiameter) > rearTolerance)
                return false;
            // A flat imported car cannot have a lopsided wheel pair or
            // grossly different wheelbase on its left and right sides.
            if (Mathf.Abs(fl.z - fr.z) > .30f ||
                Mathf.Abs(rl.z - rr.z) > .30f ||
                Mathf.Abs(fl.y - fr.y) > .22f ||
                Mathf.Abs(rl.y - rr.y) > .22f ||
                Mathf.Abs((fl.z - rl.z) - (fr.z - rr.z)) > .42f ||
                Mathf.Abs((fl.x + fr.x) * .5f) > .60f ||
                Mathf.Abs((rl.x + rr.x) * .5f) > .60f)
                return false;
            return true;
        }

        // Flush chamfered lamp polygons, not bright toy cubes that stick out
        // from the carefully textured FBX bodies. The mesh is scene-shared
        // and each lens has no Collider, shadows, or unique mesh/material.
        private static Renderer CreateLampLens(Transform chassis, string label,
            Vector3 position, Vector3 dimensions, Material material,
            Mesh sharedLensMesh, bool rearFacing)
        {
            var obj = new GameObject(label, typeof(MeshFilter),
                typeof(MeshRenderer));
            obj.transform.SetParent(chassis, false);
            obj.transform.localPosition = position;
            obj.transform.localRotation = rearFacing
                ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
            obj.transform.localScale = dimensions;
            obj.GetComponent<MeshFilter>().sharedMesh = sharedLensMesh;
            var renderer = obj.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode =
                UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return renderer;
        }

        public void ConfigureExteriorLights(Bounds fittedBounds,
            Material frontGlass, Material frontActive,
            Material backGlass, Material backBrake, Material reverseWhite,
            Mesh sharedLensMesh, Material darkBezel)
        {
            if (frontGlass == null || frontActive == null ||
                backGlass == null || backBrake == null || reverseWhite == null ||
                sharedLensMesh == null || darkBezel == null ||
                fittedBounds.size.x < .5f || fittedBounds.size.z < 1f ||
                frontLamps[0] != null) return;
            headlampGlass = frontGlass;
            headlampOn = frontActive;
            taillampGlass = backGlass;
            brakeLamp = backBrake;

            // Distinct nose and rear elevations; the prior identical Y value
            // made van brake lamps sit at sedan-headlight height. Widths now
            // follow the fitted car, rather than forcing 0.24m toy rectangles.
            float width = fittedBounds.size.x;
            float height = fittedBounds.size.y;
            float halfTrack = Mathf.Clamp(width * .385f, .48f, .84f);
            float frontY = Mathf.Clamp(
                fittedBounds.center.y + height * .15f, .55f, 1.06f);
            float backY = Mathf.Clamp(
                fittedBounds.center.y + height * .22f, .62f, 1.24f);
            float frontZ = fittedBounds.max.z + .017f;
            float backZ = fittedBounds.min.z - .017f;
            float lampWidth = Mathf.Clamp(width * .174f, .26f, .39f);
            float lampHeight = Mathf.Clamp(height * .097f, .10f, .17f);
            var lampSize = new Vector3(lampWidth, lampHeight, 1f);
            var bezelSize = new Vector3(lampWidth * 1.20f,
                lampHeight * 1.32f, 1f);
            Transform lampChassis =
                visualBodyPivot != null ? visualBodyPivot : transform;
            for (int side = 0; side < 2; side++)
            {
                float x = side == 0 ? -halfTrack : halfTrack;
                // Dark narrow bezels give the front/back glass real visual
                // contrast without covering the detailed body textures.
                CreateLampLens(lampChassis, "Headlight Bezel " + side,
                    new Vector3(x, frontY, frontZ - .007f),
                    bezelSize, darkBezel, sharedLensMesh, false);
                CreateLampLens(lampChassis, "Taillight Bezel " + side,
                    new Vector3(x, backY, backZ + .007f),
                    bezelSize, darkBezel, sharedLensMesh, true);
                frontLamps[side] = CreateLampLens(lampChassis,
                    "Beveled Headlamp " + side,
                    new Vector3(x, frontY, frontZ), lampSize,
                    headlampGlass, sharedLensMesh, false);
                rearLamps[side] = CreateLampLens(lampChassis,
                    "Beveled Brake Lamp " + side,
                    new Vector3(x, backY, backZ), lampSize,
                    taillampGlass, sharedLensMesh, true);
                reversingLamps[side] = CreateLampLens(lampChassis,
                    "Recessed Reverse Lamp " + side,
                    new Vector3(x * .68f, backY - lampHeight * .96f, backZ),
                    new Vector3(lampWidth * .38f, lampHeight * .56f, 1f),
                    reverseWhite, sharedLensMesh, true);
                reversingLamps[side].enabled = false;
            }

            // The 10-car mobile stream needs *zero* idle Light components.
            // A single player-car spotlight is created only on first entry.
            // Parked cars still show the original shared URP lens materials.
            lastLampState = -1;
            RefreshExteriorLights();
        }

        private void EnsureRoadHeadlight()
        {
            // No per-frame allocations or repeated lamp creation. The visual
            // body pivot already exists when the player enters a fitted car.
            if (roadHeadlight != null || frontLamps[0] == null ||
                frontLamps[1] == null) return;
            Transform mount = frontLamps[0].transform.parent;
            if (mount == null) return;
            var beamObject = new GameObject("Player Car Headlight Beam");
            beamObject.transform.SetParent(mount, false);
            Vector3 middle = (frontLamps[0].transform.localPosition +
                frontLamps[1].transform.localPosition) * .5f;
            beamObject.transform.localPosition = middle + Vector3.forward * .10f;
            roadHeadlight = beamObject.AddComponent<Light>();
            roadHeadlight.type = LightType.Spot;
            roadHeadlight.color = new Color(1f, .94f, .80f);
            roadHeadlight.range = 17f;
            roadHeadlight.spotAngle = 64f;
            roadHeadlight.intensity = 1.2f;
            roadHeadlight.shadows = LightShadows.None;
            roadHeadlight.enabled = false;
        }

        // Sample the scene sun at most once every two seconds, never every
        // physics or rendering frame. With no configured directional sun,
        // retain the original beam for night/custom-lighting scenes.
        private bool NeedsRoadBeam()
        {
            if (Time.unscaledTime < nextRoadBeamSampleAt)
                return roadBeamNeeded;
            nextRoadBeamSampleAt = Time.unscaledTime + 2f;
            Light sun = RenderSettings.sun;
            if (sun == null || !sun.isActiveAndEnabled ||
                sun.type != LightType.Directional)
            {
                roadBeamNeeded = true;
                return true;
            }
            float elevation = Vector3.Dot(-sun.transform.forward, Vector3.up);
            roadBeamNeeded = elevation < .18f || sun.intensity < .35f;
            return roadBeamNeeded;
        }

        private void RefreshExteriorLights()
        {
            if (frontLamps[0] == null) return;
            bool driving = driver != null && driver.isActiveAndEnabled &&
                isActiveAndEnabled;
            bool braking = driving &&
                (appliedBrakeTorque >= 500f || Time.time < impactFlashUntil);
            // Reverse lamps turn on when the driver requests backward travel
            // and the car has slowed enough to engage reverse, not when a
            // moving-forward car is merely braking with the down arrow.
            bool reversing = driving && forward < -.08f &&
                SpeedMetresPerSecond < .55f;
            bool roadBeam = driving && NeedsRoadBeam();
            int lampState = (driving ? 1 : 0) |
                (braking ? 2 : 0) | (reversing ? 4 : 0) |
                (roadBeam ? 8 : 0);
            if (lastLampState == lampState) return;
            lastLampState = lampState;
            for (int side = 0; side < 2; side++)
            {
                frontLamps[side].sharedMaterial =
                    driving ? headlampOn : headlampGlass;
                rearLamps[side].sharedMaterial =
                    braking ? brakeLamp : taillampGlass;
                reversingLamps[side].enabled = reversing;
            }
            if (roadHeadlight != null) roadHeadlight.enabled = roadBeam;
        }

        // The world has already tinted these precise PBR slots with a
        // stable paint variant. Darken only those paint overrides when wear
        // grows: no mesh cloning, texture loss, property-block allocations
        // per crash or shared-material mutation across parked cars.
        /// <summary>
        /// Install a cosmetic body-only pivot AFTER real wheel meshes are
        /// attached/detached. Never tilt colliders, wheel meshes or the car's
        /// Rigidbody; they remain the sole physical authority.
        /// </summary>
        public void ConfigureBodyDynamics(Transform bodyModel)
        {
            if (bodyModel == null || visualBodyPivot != null ||
                bodyModel == transform || !bodyModel.IsChildOf(transform))
                return;
            var pivot = new GameObject("Visual Body Suspension Pivot");
            pivot.transform.SetParent(transform, false);
            // Identity chassis-space pivot: old FBX pose and local bounds stay
            // exactly identical until the first REAL physics acceleration.
            pivot.transform.localPosition = Vector3.zero;
            pivot.transform.localRotation = Quaternion.identity;
            pivot.transform.localScale = Vector3.one;
            bodyModel.SetParent(pivot.transform, true);
            visualBodyPivot = pivot.transform;
            // The chassis may consist of many panels. Use the largest active
            // renderer so an invisible badge/trim piece never throttles a car
            // whose body is actually on screen. This is spawn-only work.
            float largestBodyBounds = 0f;
            foreach (var candidate in bodyModel.GetComponentsInChildren<Renderer>())
            {
                if (candidate == null || !candidate.enabled ||
                    candidate.forceRenderingOff) continue;
                float sizeSquared = candidate.bounds.size.sqrMagnitude;
                if (sizeSquared <= largestBodyBounds) continue;
                largestBodyBounds = sizeSquared;
                representativeBodyRenderer = candidate;
            }
            // Spread the cosmetic-only work across four frames for the
            // maximum ten nearby cars instead of updating all in lockstep.
            offscreenVisualFramePhase = GetInstanceID() & 3;
            previousPhysicsVelocity = body != null ? body.linearVelocity :
                Vector3.zero;
            hasPreviousPhysicsVelocity = body != null;
            visualPitchTarget = visualRollTarget = visualJounceTarget = 0f;
        }

        private void SampleBodyDynamics()
        {
            if (body == null || visualBodyPivot == null) return;
            Vector3 velocity = body.linearVelocity;
            if (!hasPreviousPhysicsVelocity)
            {
                previousPhysicsVelocity = velocity;
                hasPreviousPhysicsVelocity = true;
                return;
            }
            Vector3 acceleration = (velocity - previousPhysicsVelocity) /
                Mathf.Max(.005f, Time.fixedDeltaTime);
            previousPhysicsVelocity = velocity;
            bool driving = driver != null && driver.isActiveAndEnabled &&
                isActiveAndEnabled;
            // Reuse all four WheelHit samples already gathered by the two
            // axle anti-roll calls. No duplicate isGrounded lookups per tick.
            int grounded = groundedWheelCount;
            // Airborne, overturned, parked and disabled cars must not lean
            // sideways because of gravity, collisions or stale input.
            if (!driving || grounded < 2 || groundedAxleMask != 3 ||
                Vector3.Dot(transform.up, Vector3.up) < .45f)
            {
                visualPitchTarget = visualRollTarget = visualJounceTarget = 0f;
                return;
            }
            Vector3 local = transform.InverseTransformDirection(acceleration);
            if (float.IsNaN(local.x) || float.IsInfinity(local.x) ||
                float.IsNaN(local.y) || float.IsInfinity(local.y) ||
                float.IsNaN(local.z) || float.IsInfinity(local.z))
            {
                visualPitchTarget = visualRollTarget = visualJounceTarget = 0f;
                return;
            }
            // Rigidbody remains physically stable. A 1.8 degree cap makes
            // braking squat and corners readable without cartoon body lean
            // or visible tyre/body separation in sports cars and long vans.
            visualPitchTarget = Mathf.Clamp(-local.z * .18f, -1.8f, 1.8f);
            visualRollTarget = Mathf.Clamp(local.x * .18f, -1.8f, 1.8f);
            visualJounceTarget = Mathf.Clamp(-local.y * .0025f, -.022f, .022f);
        }

        private void UpdateVisualBodyDynamics()
        {
            if (visualBodyPivot == null) return;
            float dt = Mathf.Clamp(Time.deltaTime, 0f, .12f);
            float blend = 1f - Mathf.Exp(-8f * dt);
            Quaternion goal = Quaternion.Euler(
                visualPitchTarget, 0f, visualRollTarget);
            visualBodyPivot.localRotation = Quaternion.Slerp(
                visualBodyPivot.localRotation, goal, blend);
            // Vertical jounce is almost imperceptible, just enough to link
            // the cabin to spring motion without moving physics contact.
            Vector3 pos = visualBodyPivot.localPosition;
            pos.y = Mathf.Lerp(pos.y, visualJounceTarget, blend);
            visualBodyPivot.localPosition = pos;
        }

        public void ConfigureBodyWear(Transform bodyModel, Color carPaint)
        {
            if (bodyModel == null) return;
            bodyPaintSlots.Clear();
            foreach (var renderer in
                bodyModel.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                var materials = renderer.sharedMaterials;
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    Material material = materials[slot];
                    if (material == null ||
                        !material.HasProperty("_BaseColor")) continue;
                    string label = material.name;
                    bool separate = label.IndexOf("AarisPaint",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                    bool atlas = label.IndexOf("body_pbr",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                    bool sedan = label.IndexOf("AarisCivilianSurface",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!separate && !atlas && !sedan) continue;
                    // Restore damage from the exact same premium swatch.
                    // A driven GT must not snap back to imported dark red.
                    Color fresh = AarisVehicleWorld.SelectPaintTint(
                        material, carPaint, atlas, sedan);
                    bodyPaintSlots.Add(new BodyPaintSlot
                    {
                        renderer = renderer, index = slot,
                        fresh = fresh, atlas = atlas
                    });
                }
            }
            if (bodyPaintSlots.Count == 0) return;
            if (bodyPaintBlock == null)
                bodyPaintBlock = new MaterialPropertyBlock();
            lastBodyWear = -1f;
            RefreshBodyWear();
        }

        private void RefreshBodyWear()
        {
            if (bodyPaintSlots.Count == 0 ||
                Mathf.Abs(lastBodyWear - damage01) < .015f) return;
            lastBodyWear = damage01;
            float fade = Mathf.Clamp01(damage01 / .85f);
            foreach (var entry in bodyPaintSlots)
            {
                if (entry.renderer == null) continue;
                // A textured body atlas also contains trim/decal pixels:
                // reduce its tint more gently than a dedicated paint slot.
                float darkness = Mathf.Lerp(1f,
                    entry.atlas ? .73f : .52f, fade);
                Color fresh = entry.fresh;
                Color worn = new Color(fresh.r * darkness,
                    fresh.g * darkness, fresh.b * darkness, fresh.a);
                bodyPaintBlock.Clear();
                entry.renderer.GetPropertyBlock(bodyPaintBlock, entry.index);
                bodyPaintBlock.SetColor("_BaseColor", worn);
                entry.renderer.SetPropertyBlock(bodyPaintBlock, entry.index);
            }
        }

        public void ConfigureRearTyreTraces(Material material)
        {
            // The shared material is owned and disposed by VehicleWorld.
            // Do not instantiate graphics for any parked/never-driven car.
            sharedSkidMaterial = material;
        }

        private void EnsureRearTyreTraces()
        {
            if (sharedSkidMaterial == null || rearTyreTrails[0] != null)
                return;
            for (int i = 0; i < rearTyreTrails.Length; i++)
            {
                var mark = new GameObject("Physics Rear Tyre Skid " + i);
                mark.transform.SetParent(transform, false);
                var trail = mark.AddComponent<TrailRenderer>();
                trail.sharedMaterial = sharedSkidMaterial;
                trail.time = 2.2f;
                trail.minVertexDistance = .20f;
                trail.startWidth = .14f;
                trail.endWidth = .085f;
                trail.numCornerVertices = 0;
                trail.numCapVertices = 0;
                trail.alignment = LineAlignment.TransformZ;
                trail.textureMode = LineTextureMode.Stretch;
                trail.shadowCastingMode =
                    UnityEngine.Rendering.ShadowCastingMode.Off;
                trail.receiveShadows = false;
                trail.emitting = false;
                trail.Clear();
                rearTyreTrails[i] = trail;
            }
        }

        private void StopRearTyreTraces(bool clear)
        {
            for (int i = 0; i < rearTyreTrails.Length; i++)
            {
                var trail = rearTyreTrails[i];
                if (trail == null) continue;
                trail.emitting = false;
                if (clear) trail.Clear();
            }
        }

        private void UpdateRearTyreTraces()
        {
            if (rearTyreTrails[0] == null || body == null) return;
            // Never draw for an unoccupied car, a disabled/suspended session,
            // an airborne tyre, standing water, or slow stationary wheelspin.
            bool driving = driver != null && driver.isActiveAndEnabled &&
                isActiveAndEnabled;
            bool moving = driving && body.linearVelocity.sqrMagnitude > 16f;
            for (int i = 0; i < rearTyreTrails.Length; i++)
            {
                var trail = rearTyreTrails[i];
                if (trail == null) continue;
                bool makingMark = moving && rearContactsGrounded[i] &&
                    rearContactSlip[i] > .35f;
                if (!makingMark)
                {
                    trail.emitting = false;
                    continue;
                }
                // WheelHit.point and WheelHit.normal came from the existing
                // physics step. Ground offset avoids Z-fighting with roads.
                // TransformZ aligns the ribbon width along the actual road.
                Vector3 normal = rearContactNormals[i];
                trail.transform.SetPositionAndRotation(
                    rearContactPoints[i] + normal * .014f,
                    Quaternion.FromToRotation(Vector3.forward, normal));
                if (!trail.emitting)
                {
                    // Break any old strip when a wheel recontacts the road;
                    // no drawn line across a gap, landing or respawn jump.
                    trail.Clear();
                    trail.emitting = true;
                }
            }
        }

        private void CacheRearTyreContact(int wheelIndex, WheelHit hit,
            float slip)
        {
            if (wheelIndex < 2 || wheelIndex >= wheels.Length ||
                driver == null || rearTyreTrails[0] == null)
                return;
            int i = wheelIndex - 2;
            // Water is not a skid-mark surface. The world-up normal guard
            // rejects wall contacts and unrealistically steep slopes.
            if (hit.collider == null || hit.collider.gameObject.layer == 4 ||
                Vector3.Dot(hit.normal, Vector3.up) < .45f)
                return;
            rearContactsGrounded[i] = true;
            rearContactPoints[i] = hit.point;
            rearContactNormals[i] = hit.normal;
            rearContactSlip[i] = slip;
        }

        // Exhaust is strictly optional: Shader.Find may be stripped in a
        // device build. Never replace it with a pink/default particle material.
        public void ConfigureExhaust(Material particleMaterial,
            Bounds chassisBounds)
        {
            sharedExhaustMaterial = particleMaterial;
            exhaustAnchor = new Vector3(
                Mathf.Clamp(-chassisBounds.size.x * .24f, -.48f, -.26f),
                Mathf.Clamp(chassisBounds.min.y + .16f, .18f, .38f),
                -chassisBounds.size.z * .5f - .10f);
        }

        public void RestoreStreamedDamage(float savedDamage)
        {
            damage01 = float.IsNaN(savedDamage) || float.IsInfinity(savedDamage)
                ? 0f : Mathf.Clamp(savedDamage, 0f, .85f);
        }

        private void EnsureExhaustEmitter()
        {
            // Parked cars never instantiate a ParticleSystem.
            if (sharedExhaustMaterial == null || exhaustParticles != null)
                return;
            var nozzle = new GameObject("Occupied Car Exhaust");
            nozzle.transform.SetParent(
                visualBodyPivot != null ? visualBodyPivot : transform, false);
            nozzle.transform.localPosition = exhaustAnchor;
            nozzle.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            exhaustParticles = nozzle.AddComponent<ParticleSystem>();
            var main = exhaustParticles.main;
            main.playOnAwake = false;
            main.loop = true;
            main.duration = 1.2f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 24;
            main.startLifetime = new ParticleSystem.MinMaxCurve(.55f, 1.0f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(.25f, .65f);
            main.startSize = new ParticleSystem.MinMaxCurve(.095f, .20f);
            main.startColor = new Color(.56f, .58f, .60f, .22f);
            var shape = exhaustParticles.shape;
            shape.enabled = false;
            var emission = exhaustParticles.emission;
            emission.rateOverTime = 0f;
            var renderer = nozzle.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = sharedExhaustMaterial;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode =
                UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            exhaustParticles.Stop(true,
                ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        private void StopExhaust(bool clear)
        {
            if (exhaustParticles == null) return;
            exhaustParticles.Stop(true, clear
                ? ParticleSystemStopBehavior.StopEmittingAndClear
                : ParticleSystemStopBehavior.StopEmitting);
        }

        private void UpdateExhaust()
        {
            if (exhaustParticles == null ||
                Time.unscaledTime < nextExhaustUpdateAt) return;
            nextExhaustUpdateAt = Time.unscaledTime + .16f;
            if (driver == null || !driver.isActiveAndEnabled ||
                !isActiveAndEnabled)
            {
                if (exhaustParticles.isPlaying) StopExhaust(false);
                return;
            }
            // No raycasts, dynamic materials or instantiations in this hotpath.
            float load = Mathf.Abs(forward);
            float emissionRate = 1.4f + load * 3.5f + damage01 * 10f;
            if (body != null && body.linearVelocity.sqrMagnitude > 625f)
                emissionRate *= .6f;
            var emission = exhaustParticles.emission;
            emission.rateOverTime = emissionRate;
            var main = exhaustParticles.main;
            main.startColor = Color.Lerp(
                new Color(.56f, .58f, .60f, .20f),
                new Color(.33f, .34f, .35f, .36f),
                Mathf.Clamp01(damage01 / .85f));
            if (!exhaustParticles.isPlaying) exhaustParticles.Play();
        }

        private void SyncVisibleWheels()
        {
            for (int i = 0; i < wheels.Length; i++)
            {
                if (wheels[i] == null || visualWheels[i] == null) continue;
                wheels[i].GetWorldPose(out Vector3 worldPosition,
                    out Quaternion worldRotation);
                Quaternion visualRotation =
                    worldRotation * visualWheelRotationOffsets[i];
                // Preserve the visible mesh CENTRE at the real WheelCollider
                // pose, including during steering, suspension and tyre spin.
                // Cached float vectors: no per-frame mesh bounds or allocations.
                Vector3 visualCentreOffsetWorld =
                    visualRotation * visualWheelCenterOffsets[i];
                visualWheels[i].SetPositionAndRotation(
                    worldPosition - visualCentreOffsetWorld, visualRotation);
            }
        }

        private void LateUpdate()
        {
            if (body == null) return;
            // WheelCollider poses are static while an unoccupied Rigidbody sleeps.
            // Continue every-frame synchronization whenever the car is driven,
            // moving or awakened by a collision; never reduce active wheel FPS.
            bool parkedSleeping = driver == null && body.IsSleeping();
            // Parked cars can fall asleep before the visual-only cabin has
            // eased out of its last turn. Settle without repeated wheel poses.
            bool bodyNeedsToSettle = visualBodyPivot != null &&
                (Quaternion.Angle(visualBodyPivot.localRotation,
                    Quaternion.identity) > .10f ||
                 Mathf.Abs(visualBodyPivot.localPosition.y) > .0015f);
            if (parkedSleeping && sleepingWheelPoseCurrent && !bodyNeedsToSettle)
                return;
            // A driverless, *offscreen* rolling car needs full-rate physics,
            // but its visual wheel poses/body animation need not cost four
            // native WheelCollider.GetWorldPose calls every render frame.
            // Always update occupied, visible, waking/settling cars at full
            // cadence. On entering any camera's view the next LateUpdate
            // immediately restores full-rate visuals with no stale pose cache.
            if (!parkedSleeping && driver == null && !bodyNeedsToSettle &&
                representativeBodyRenderer != null &&
                !representativeBodyRenderer.isVisible &&
                (Time.frameCount & 3) != offscreenVisualFramePhase)
                return;
            if (!parkedSleeping || !sleepingWheelPoseCurrent)
                SyncVisibleWheels();
            UpdateVisualBodyDynamics();
            UpdateRearTyreTraces();
            UpdateExhaust();
            sleepingWheelPoseCurrent = parkedSleeping;
        }

        private void OnEnable()
        {
            sleepingWheelPoseCurrent = false;
            appliedDriveTorque = appliedBrakeTorque = appliedSteeringAngle = float.NaN;
            appliedServiceBrakeDirection = 0;
            lastLampState = -1;
            nextRoadBeamSampleAt = 0f;
            StopRearTyreTraces(true);
            StopExhaust(true);
            RefreshExteriorLights();
        }

        public bool TryTakeDriver(AarisVehicleSession session)
        {
            // Never board a car the player could not safely exit.
            // A slow overturned or airborne car could otherwise trap the
            // walking motor with no legal exit position.
            // A temporarily disabled vehicle cannot supply drive physics.
            // Never accept a new driver while either movement authority
            // is inactive, even when its old WheelCollider contacts remain.
            if (session == null || !session.isActiveAndEnabled ||
                !isActiveAndEnabled || IsOccupied || body == null ||
                !IsSafeToExit) return false;
            driver = session;
            // A streamed parked car may have been asleep for minutes.
            // PhysX is not guaranteed to wake just because we replaced the
            // brake torque. Guarantee the first player throttle reacts.
            if (body.IsSleeping()) body.WakeUp();
            forward = steering = smoothSteer = 0f;
            impactFlashUntil = 0f;
            // Release parked-car brake cache on boarding, otherwise the
            // first frame falsely displays brake lamps before physics ticks.
            ApplyWheelForces(0f, 155f);
            previousPhysicsVelocity = body.linearVelocity;
            hasPreviousPhysicsVelocity = true;
            EnsureRearTyreTraces();
            EnsureExhaustEmitter();
            StopRearTyreTraces(true);
            nextExhaustUpdateAt = 0f;
            // This is the only creation point for the visual spotlight.
            EnsureRoadHeadlight();
            RefreshExteriorLights();
            return true;
        }

        public void SetInput(float forwardAxis, float steerAxis)
        {
            // The player may still be seated during a temporary disable.
            // OnDisable brakes and zeros the motor. Reject subsequent UI
            // callbacks until re-enabled to prevent stale throttle resuming.
            if (driver == null || !isActiveAndEnabled ||
                !driver.isActiveAndEnabled || Time.timeScale <= 0f)
            {
                forward = steering = 0f;
                return;
            }
            // Ignore corrupted/non-finite input rather than poisoning the
            // WheelCollider torque or steering angle with NaN.
            if (float.IsNaN(forwardAxis) || float.IsInfinity(forwardAxis)) forwardAxis = 0f;
            if (float.IsNaN(steerAxis) || float.IsInfinity(steerAxis)) steerAxis = 0f;
            forward = Mathf.Clamp(forwardAxis, -1f, 1f);
            steering = Mathf.Clamp(steerAxis, -1f, 1f);
            // A legitimately idle occupied Rigidbody can go back to sleep.
            // Resume the native wheel simulation only on an actual new
            // throttle/steer intent; neutral input never wakes parked cars.
            if (body != null &&
                (Mathf.Abs(forward) > .08f || Mathf.Abs(steering) > .08f) &&
                body.IsSleeping())
                body.WakeUp();
        }

        public void ReleaseDriver(AarisVehicleSession session)
        {
            if (session != driver) return;
            driver = null;
            forward = steering = 0f;
            impactFlashUntil = 0f;
            ApplyWheelForces(0f, ParkingBrakeTorque);
            StopRearTyreTraces(false);
            StopExhaust(false);
            visualPitchTarget = visualRollTarget = visualJounceTarget = 0f;
            RefreshExteriorLights();
        }

        private void ApplyWheelForces(float drive, float brake)
        {
            bool driveChanged = drive != appliedDriveTorque;
            // Braking shifts weight toward the direction of travel: front
            // tyres during forward motion, rear tyres while reversing.
            // Sideways skids and nearly stationary cars get even braking.
            // Total Rigidbody speed alone would incorrectly front-bias a
            // lateral skid; use the signed longitudinal speed instead.
            int brakeDirection = 0;
            if (driver != null && body != null &&
                brake >= 500f && brake < ParkingBrakeTorque)
            {
                float longitudinalSpeed = SpeedMetresPerSecond;
                if (longitudinalSpeed > 2f) brakeDirection = 1;
                else if (longitudinalSpeed < -2f) brakeDirection = -1;
            }
            // Direction is part of the cache: a held brake with unchanged
            // torque still updates all four WheelColliders on a direction flip.
            bool brakeChanged = brake != appliedBrakeTorque ||
                brakeDirection != appliedServiceBrakeDirection;
            if (!driveChanged && !brakeChanged) return;
            for (int i = 0; i < wheels.Length; i++)
            {
                if (wheels[i] == null) continue;
                if (driveChanged)
                {
                    float axleFraction = i < 2 ? frontDriveFraction :
                        1f - frontDriveFraction;
                    wheels[i].motorTorque = drive * .5f * axleFraction;
                }
                if (brakeChanged)
                {
                    float bias = brakeDirection == 0 ? 1f :
                        (i < 2) == (brakeDirection > 0) ? 1.12f : .88f;
                    wheels[i].brakeTorque = brake * bias;
                }
            }
            appliedDriveTorque = drive;
            appliedBrakeTorque = brake;
            appliedServiceBrakeDirection = brakeDirection;
        }

        // Smooth, monotone torque bands keep zero slope at every transition.
        // Real WheelCollider impulses and the speed cap remain authoritative.
        // The old step function dropped 21-32% torque at exact thresholds,
        // which could make the driver feel a lurch during acceleration.
        private static float ContinuousDriveTorque(float speedAbs)
        {
            if (speedAbs < 7f)
                return Mathf.SmoothStep(1500f, 1180f,
                    Mathf.Clamp01(speedAbs / 7f));
            if (speedAbs < 14f)
                return Mathf.SmoothStep(1180f, 850f,
                    Mathf.Clamp01((speedAbs - 7f) / 7f));
            if (speedAbs < 21f)
                return Mathf.SmoothStep(850f, 580f,
                    Mathf.Clamp01((speedAbs - 14f) / 7f));
            return 580f;
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
            // Reuse last physics step's real WheelHit slip as a zero-query
            // traction-control signal. This avoids cartoon wheelspin on loose
            // terrain/water without adding raycasts or changing WheelCollider
            // authority. The current step refreshes tyreSlip below.
            float previousTyreSlip = tyreSlip;

            // Keep the existing four real-physics torque targets, but no
            // abrupt threshold step that kicks the chassis at 7/14/21 m/s.
            float baseTorque = ContinuousDriveTorque(speedAbs);
            float torque = baseTorque * driveTorqueScale;
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
            // Damage costs only some torque; preserve drivable exits.
            drive *= Mathf.Lerp(1f, .72f, damage01);
            if (Mathf.Abs(drive) > 0.01f && previousTyreSlip > 0.35f)
            {
                float slip01 = Mathf.InverseLerp(0.35f, 1.10f, previousTyreSlip);
                drive *= Mathf.Lerp(1f, 0.52f, slip01);
            }
            // Downhill speed control: apply brake instead of directly
            // overwriting velocity, which would ignore collision impulses.
            if (speed > topSpeed + .6f || speed < -8f)
                brake = Mathf.Max(brake, 1100f);
            ApplyWheelForces(drive, brake);
            RefreshExteriorLights();
            // Gradually reduce steering angle at speed so 100km/h inputs
            // cannot instantaneously rotate a multi-ton vehicle by 70°/s.
            // A sideways skid can have near-zero forward speed even at
            // road speed. Reduce steering by actual planar vehicle speed.
            float planarSpeed = Vector3.ProjectOnPlane(
                body.linearVelocity, transform.up).magnitude;
            float maxAngle = Mathf.Lerp(32f, 10f,
                Mathf.InverseLerp(0f, 24f, planarSpeed)) * steeringMassFactor *
                steeringResponseMultiplier;
            // High-speed tyre-slip feedback reduces counter-steer spikes
            // when drifting; at parking speeds steering stays immediate.
            // This reuses last step's contact sample: no extra wheel query.
            float speedBlend = Mathf.InverseLerp(10f, 25f, planarSpeed);
            float slipBlend = Mathf.InverseLerp(.42f, 1.40f,
                previousTyreSlip) * speedBlend;
            float target = turn * maxAngle * Mathf.Lerp(1f, .82f, slipBlend);
            float steeringRateScale = Mathf.Lerp(1f, .63f, speedBlend);
            smoothSteer = Mathf.MoveTowards(smoothSteer, target,
                125f * steeringResponseMultiplier * Time.fixedDeltaTime *
                steeringRateScale);
            ApplyAckermannSteering(smoothSteer);

            // Axle load transfer: counter excessive roll on fast bends and
            // uneven slopes without freezing Rigidbody rotation or faking
            // wheel contact. Only grounded wheels receive corrective force.
            // Reset once per physics step; ApplyAntiRoll reuses each WheelHit
            // to accumulate real longitudinal/lateral tyre slip.
            tyreSlip = 0f;
            groundedWheelCount = 0;
            groundedAxleMask = 0;
            rearContactsGrounded[0] = rearContactsGrounded[1] = false;
            ApplyAntiRoll(0, 1);
            ApplyAntiRoll(2, 3);
            SampleBodyDynamics();

            // Two grounded tyres on one axle do NOT support the other end
            // of a vehicle at a crest, bridge lip, steep ramp or drop-off.
            // Require front AND rear support to avoid pulling an unsupported
            // end down with an artificial aero/suspension force. Only the
            // existing four WheelHit samples are used; no extra queries.
            if (planarSpeed > 6f && groundedWheelCount >= 2 &&
                groundedAxleMask == 3)
                // Realistic road-car aero: the old force reached >1g on
                // light hatchbacks at motorway speed. Bound added contact
                // load to 0.36g across every catalog mass without altering
                // Rigidbody velocity or introducing another raycast.
                body.AddForce(-transform.up * (body.mass *
                    Mathf.Min(3.5f, .007f * planarSpeed * planarSpeed)),
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
            // Unchanged wheel angles already hold in PhysX. Invalidate this
            // cache when the model fit changes the actual axle positions.
            if (centerAngle == appliedSteeringAngle) return;
            appliedSteeringAngle = centerAngle;
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
            forwardGrip.stiffness = BaseForwardGripStiffness *
                tyreCompoundMultiplier * multiplier;
            wheel.forwardFriction = forwardGrip;

            WheelFrictionCurve sideGrip = wheel.sidewaysFriction;
            sideGrip.stiffness = BaseSideGripStiffness *
                tyreCompoundMultiplier * multiplier;
            wheel.sidewaysFriction = sideGrip;
        }

        private void ApplyAntiRoll(int leftIndex, int rightIndex)
        {
            var left = wheels[leftIndex];
            var right = wheels[rightIndex];
            if (left == null || right == null || body == null) return;

            bool leftGrounded = left.GetGroundHit(out WheelHit leftHit);
            bool rightGrounded = right.GetGroundHit(out WheelHit rightHit);
            if (leftGrounded || rightGrounded)
                groundedAxleMask |= leftIndex == 0 ? 1 : 2;
            float leftTravel = 1f, rightTravel = 1f;
            if (leftGrounded)
            {
                groundedWheelCount++;
                ApplySurfaceGrip(leftIndex, left, leftHit.collider);
                leftTravel = Mathf.Clamp01(
                    (-left.transform.InverseTransformPoint(leftHit.point).y -
                        left.radius) / Mathf.Max(.01f, left.suspensionDistance));
                float leftSlip = Mathf.Max(Mathf.Abs(leftHit.sidewaysSlip),
                    Mathf.Abs(leftHit.forwardSlip) * .78f);
                tyreSlip = Mathf.Max(tyreSlip, leftSlip);
                CacheRearTyreContact(leftIndex, leftHit, leftSlip);
            }
            if (rightGrounded)
            {
                groundedWheelCount++;
                ApplySurfaceGrip(rightIndex, right, rightHit.collider);
                rightTravel = Mathf.Clamp01(
                    (-right.transform.InverseTransformPoint(rightHit.point).y -
                        right.radius) / Mathf.Max(.01f, right.suspensionDistance));
                float rightSlip = Mathf.Max(Mathf.Abs(rightHit.sidewaysSlip),
                    Mathf.Abs(rightHit.forwardSlip) * .78f);
                tyreSlip = Mathf.Max(tyreSlip, rightSlip);
                CacheRearTyreContact(rightIndex, rightHit, rightSlip);
            }
            // A lifted wheel has no suspension contact. The legacy
            // leftTravel/rightTravel default of 1 would push upward on the
            // ONLY grounded wheel at a curb and roll the car further over.
            // Still sample slip/surface grip above for all grounded wheels.
            if (!leftGrounded || !rightGrounded) return;
            float effectiveAntiRoll = AntiRollStrength * antiRollMassScale;
            float force = Mathf.Clamp((leftTravel - rightTravel) * effectiveAntiRoll,
                -effectiveAntiRoll, effectiveAntiRoll);
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
            if (driver != null) driver.OnVehicleSuspended(this);
            ApplyWheelForces(0f, ParkingBrakeTorque);
            lastLampState = -1;
            StopRearTyreTraces(true);
            StopExhaust(true);
            visualPitchTarget = visualRollTarget = visualJounceTarget = 0f;
            if (visualBodyPivot != null)
            {
                visualBodyPivot.localRotation = Quaternion.identity;
                visualBodyPivot.localPosition = Vector3.zero;
            }
            impactFlashUntil = 0f;
            RefreshExteriorLights();
        }
    }
}
