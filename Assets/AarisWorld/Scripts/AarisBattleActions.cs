using System.Collections.Generic;
using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// Player intent/state; DOES NOT move the CharacterController.
    /// AarisThirdPersonPlayer remains the sole physics/terrain authority.
    /// A weapon slot is optional: empty hands make Fire invoke a melee punch.
    /// </summary>
    [DisallowMultipleComponent, RequireComponent(typeof(AarisThirdPersonPlayer))]
    [RequireComponent(typeof(CharacterController))]
    [DefaultExecutionOrder(-120)]
    public sealed class AarisBattleActions : MonoBehaviour
    {
        public enum PlayerStance { Standing, Crouching, Prone }

        internal const float PunchVisualDuration = 0.48f;
        // KayKit's authored punch reaches full extension at frame 12 of 28.
        private const float PunchContactDelay = PunchVisualDuration * (12f / 28f);

        [SerializeField, Min(0.56f)] private float crouchHeight = 1.14f;
        [SerializeField, Min(0.56f)] private float proneHeight = 0.62f;
        [SerializeField, Min(0.3f)] private float punchReach = 1.45f;
        [SerializeField, Min(0.1f)] private float punchInterval = 0.56f;
        [SerializeField, Min(0f)] private float punchDamage = 20f;
        [SerializeField] private AarisEquippedGun equippedGun;

        private CharacterController capsule;
        private AarisThirdPersonPlayer motor;
        private Vector3 standingCenter;
        private float standingHeight, standingStepOffset, nextAttack;
        private readonly Collider[] nearHits = new Collider[32];
        // A second reusable buffer handles dense forests/settlements without
        // allocating a Collider[] on every prone movement frame.
        private readonly Collider[] crowdedHits = new Collider[128];
        // Contact-time melee rays reuse one buffer, independently of the
        // overlap candidates which remain live throughout target selection.
        private readonly RaycastHit[] punchRayHits = new RaycastHit[32];
        // Reuse one component list for damage-receiver lookup on rare melee attacks.
        private readonly List<Component> punchComponents = new List<Component>(8);
        private PlayerStance stance;
        private PlayerStance movementStance, transitionFromStance;
        private float stanceStartHeight, stanceTargetHeight, stanceElapsed, stanceDuration;
        private bool stanceTransitioning;
        private readonly RaycastHit[] proneSupportHits = new RaycastHit[32];
        private Vector3 proneGroundNormal = Vector3.up, proneGroundPoint;
        private bool proneSupportValid;
        private float nextProneSupportSample;
        private Vector3 supportSamplePosition, supportSampleForward, supportSamplePoint, supportSampleNormal;
        private Collider continuousSupport;
        private bool fireHeld;
        private bool semiShotQueued;
        private bool shotQueuedDuringDraw;
        // Short trigger edges survive a cooldown / slow Android input frame,
        // but must not manufacture a delayed shot after a long reload.
        private const float SemiTapBufferSeconds = 0.16f;
        private const int MaximumAutomaticShotsPerFrame = 2;
        private float fireBufferUntil;
        private int attackSequence;
        private int automaticShotFrame = -1, automaticShotsThisFrame;
        private float lastPunchTime = -10f;
        private float punchContactTime = -1f;
        private float lastShootTime = -10f;

        public PlayerStance Stance => stance;
        // During a rise, retain the lower gait until the real capsule has
        // clearance and reaches its target. A short capsule cannot sprint.
        public PlayerStance MovementStance => movementStance;
        public bool IsStanceTransitioning => stanceTransitioning;
        public bool FireHeld => fireHeld;
        internal bool RequestsWeaponDraw => fireHeld ||
            (shotQueuedDuringDraw && semiShotQueued && Time.time <= fireBufferUntil);
        public bool IsArmed => equippedGun != null &&
            equippedGun.isActiveAndEnabled && equippedGun.Configured;
        // Flight, vehicle seats and application pauses disable the sole on-foot
        // motor. Never let a stale touch press or queued melee hit bypass it.
        private bool AcceptsCombatInput =>
            isActiveAndEnabled && Time.timeScale > 0f &&
            motor != null && motor.isActiveAndEnabled;
        public float CameraEyeHeight
        {
            get
            {
                if (capsule == null) return 1.45f;
                float crouch = StanceHeight(PlayerStance.Crouching);
                float height = capsule.height;
                float eye = height >= crouch
                    ? Mathf.Lerp(0.94f, 1.45f,
                        Mathf.InverseLerp(crouch, standingHeight, height))
                    : Mathf.Lerp(0.54f, 0.94f,
                        Mathf.InverseLerp(StanceHeight(PlayerStance.Prone), crouch, height));
                return Mathf.Min(eye, MaximumCameraEyeHeight);
            }
        }
        public float MaximumCameraEyeHeight => capsule != null
            ? standingCenter.y - standingHeight * 0.5f + capsule.height - 0.08f
            : 1.45f;
        internal Vector3 ProneGroundNormal => proneSupportValid ? proneGroundNormal : Vector3.up;
        internal bool TryGetProneSupportPlane(out Vector3 point, out Vector3 normal)
        {
            point = proneGroundPoint;
            normal = proneGroundNormal;
            return proneSupportValid;
        }
        internal float ProneGroundHeightOffset(Vector3 worldPosition)
        {
            if (!proneSupportValid || proneGroundNormal.y < 0.5f) return 0f;
            Vector3 delta = worldPosition - transform.position;
            return -(proneGroundNormal.x * delta.x + proneGroundNormal.z * delta.z) /
                proneGroundNormal.y;
        }
        public float WeaponRecoilMultiplier => StanceStability(0.82f, 0.62f);
        public float WeaponSpreadMultiplier => StanceStability(0.78f, 0.55f);

        private float StanceStability(float crouch, float prone)
        {
            if (capsule == null || !capsule.enabled || !capsule.isGrounded ||
                motor == null || !motor.isActiveAndEnabled) return 1f;
            float crouchHeightNow = StanceHeight(PlayerStance.Crouching);
            float multiplier = capsule.height >= crouchHeightNow
                ? Mathf.Lerp(1f, crouch, Mathf.InverseLerp(
                    standingHeight, crouchHeightNow, capsule.height))
                : Mathf.Lerp(crouch, prone, Mathf.InverseLerp(
                    crouchHeightNow, StanceHeight(PlayerStance.Prone), capsule.height));
            Vector3 velocity = capsule.velocity;
            float planarSquared = velocity.x * velocity.x + velocity.z * velocity.z;
            // Braced stationary fire earns the full benefit. Crawling or
            // moving crouched progressively releases it, without new probes.
            return Mathf.Lerp(multiplier, 1f,
                Mathf.InverseLerp(0.01f, 2.25f, planarSquared));
        }
        public int AttackSequence => attackSequence;
        public float LastPunchTime => lastPunchTime;
        public float LastShootTime => lastShootTime;

        // Preserve standing stairs, but limit crouched stepping to 18 cm and
        // prone auto-stepping to 7.5 cm. Higher steps visibly float the chest
        // above the ground and allow crawling over low cover without a vault.
        // This is independent from the temporary stepOffset=0 in the air.
        public float GroundStepOffset => movementStance == PlayerStance.Standing
            ? standingStepOffset : Mathf.Min(standingStepOffset,
                movementStance == PlayerStance.Crouching ? 0.18f : 0.075f);

        private void Awake()
        {
            capsule = GetComponent<CharacterController>();
            motor = GetComponent<AarisThirdPersonPlayer>();
            standingCenter = capsule.center;
            standingHeight = capsule.height;
            standingStepOffset = capsule.stepOffset;
            stance = PlayerStance.Standing;
            movementStance = transitionFromStance = stance;
            stanceStartHeight = stanceTargetHeight = standingHeight;
        }

        public void Equip(AarisEquippedGun gun)
        {
            if (equippedGun != null) equippedGun.BreakAutomaticCadence();
            equippedGun = gun;
            // A weapon transition invalidates the previous unarmed wind-up.
            // Releasing Fire still permits contact; changing equipment does not.
            punchContactTime = -1f;
            lastPunchTime = -10f;
            nextAttack = 0f;
            semiShotQueued = false;
            shotQueuedDuringDraw = false;
            fireBufferUntil = 0f;
        }

        public void SetFireHeld(bool held)
        {
            if (!AcceptsCombatInput)
            {
                CancelPendingCombatInput();
                return;
            }
            // Unity's EventSystem can process both Down and Up between combat
            // Updates on 10-20 FPS phones. Fire immediately on the new press:
            // otherwise a quick tap is discarded before Update can see it.
            bool newPress = held && !fireHeld;
            fireHeld = held;
            // A released semi-auto press retains its brief buffer until it
            // fires or expires; automatic fire stops immediately on release.
            if (!held && (!IsArmed || (equippedGun.Automatic && !shotQueuedDuringDraw)))
            {
                if (equippedGun != null) equippedGun.BreakAutomaticCadence();
                semiShotQueued = false;
                shotQueuedDuringDraw = false;
                fireBufferUntil = 0f;
            }
            // The single player motor owns sprint cancellation. Clearing it
            // here would erase jump-shot momentum before its Update runs when
            // Jump and Fire arrive during the same mobile input frame.
            if (!newPress) return;
            semiShotQueued = true;
            shotQueuedDuringDraw = IsArmed && equippedGun.RequiresVisualDraw;
            fireBufferUntil = Time.time + SemiTapBufferSeconds +
                (shotQueuedDuringDraw ? 0.30f : 0f);
            // Do not bypass the gun's ammo, reload, cooldown or cover checks.
            // An unarmed tap must also begin its punch wind-up immediately,
            // otherwise a short Down/Up pair is lost before the next Update.
            if (Time.time < nextAttack) return;
            if (IsArmed) ShootEquippedWeapon();
            else QueuePunch();
        }

        public bool ToggleCrouch() =>
            TrySetStance(stance == PlayerStance.Crouching
                ? PlayerStance.Standing : PlayerStance.Crouching);

        public bool ToggleProne() =>
            TrySetStance(stance == PlayerStance.Prone
                ? PlayerStance.Standing : PlayerStance.Prone);

        public bool TryStand() => TrySetStance(PlayerStance.Standing);

        // Jump must launch from a fully checked standing capsule in the same
        // input action. Ordinary stance changes retain their smooth timing.
        internal bool TryStandForJump()
        {
            if (!TrySetStance(PlayerStance.Standing)) return false;
            if (capsule.height < standingHeight && !HasRoomAbove(standingHeight)) return false;
            ApplyStanceHeight(standingHeight);
            stanceTransitioning = false;
            movementStance = stance = PlayerStance.Standing;
            capsule.stepOffset = GroundStepOffset;
            return true;
        }

        private float StanceHeight(PlayerStance target)
        {
            float height = target == PlayerStance.Standing ? standingHeight :
                target == PlayerStance.Crouching ? crouchHeight : proneHeight;
            return Mathf.Max(height, capsule.radius * 2f + 0.015f);
        }

        public bool TrySetStance(PlayerStance target)
        {
            if ((int)target < (int)PlayerStance.Standing || (int)target > (int)PlayerStance.Prone ||
                capsule == null || !capsule.enabled || !isActiveAndEnabled) return false;
            if (target == stance) return true;
            // Going fully prone during a jump/fall instantaneously shrinks the
            // capsule and lets the visual head/feet pass through geometry.
            // Crouch remains permitted in the air; prone needs real ground
            // contact from the motor's most recent CharacterController.Move.
            if (target == PlayerStance.Prone && !capsule.isGrounded) return false;
            float height = StanceHeight(target);
            if (height > capsule.height && !HasRoomAbove(height)) return false;
            if (target == PlayerStance.Prone)
            {
                nextProneSupportSample = -1f;
                if (!HasProneClearance(transform.position, transform.forward)) return false;
            }

            transitionFromStance = movementStance;
            stanceStartHeight = capsule.height;
            stanceTargetHeight = height;
            stanceElapsed = 0f;
            stanceDuration = target == PlayerStance.Prone || movementStance == PlayerStance.Prone
                ? 0.38f : target == PlayerStance.Crouching ? 0.20f : 0.24f;
            stanceTransitioning = Mathf.Abs(height - capsule.height) > 0.0001f;
            stance = target;
            if ((int)target > (int)movementStance || !stanceTransitioning)
                movementStance = target;
            // A stance can also be selected by editor automation or other gameplay
            // scripts, not only the HUD. Stop latent auto-run AFTER a successful
            // low stance switch, or zero-stick input would keep crawling forward.
            // A rejected change must leave the player's sprint intent untouched.
            if (target != PlayerStance.Standing && motor != null)
                motor.CancelAutoSprint();
            capsule.stepOffset = GroundStepOffset;
            return true;
        }

        private void ApplyStanceHeight(float height)
        {
            float floorRelative = standingCenter.y - standingHeight * 0.5f;
            Vector3 center = new Vector3(standingCenter.x,
                floorRelative + height * 0.5f, standingCenter.z);
            if (!Mathf.Approximately(capsule.height, height)) capsule.height = height;
            if (capsule.center != center) capsule.center = center;
        }

        private void AdvanceStanceTransition(float deltaTime)
        {
            if (capsule == null || !capsule.enabled) return;
            // Movement/turn queries may test a position the motor never reaches.
            // Publish a presentation plane only for the actual grounded body.
            proneSupportValid = movementStance == PlayerStance.Prone && capsule.isGrounded &&
                TrySampleProneSupport(transform.position, transform.forward,
                    out proneGroundPoint, out proneGroundNormal);
            if (!stanceTransitioning || deltaTime <= 0f) return;
            stanceElapsed = Mathf.Min(stanceElapsed + Mathf.Min(deltaTime, 0.15f), stanceDuration);
            float t = Mathf.SmoothStep(0f, 1f, stanceElapsed / stanceDuration);
            float height = Mathf.Lerp(stanceStartHeight, stanceTargetHeight, t);
            if (height > capsule.height + 0.0001f && !HasRoomAbove(height))
            {
                // A new roof/vehicle can arrive after the initial request.
                // Return to the original safe low stance without an upwards
                // capsule/camera step or a stuck Standing intent.
                stance = movementStance = transitionFromStance;
                stanceStartHeight = capsule.height;
                stanceTargetHeight = StanceHeight(stance);
                stanceElapsed = 0f;
                stanceDuration = 0.18f;
                return;
            }
            ApplyStanceHeight(height);
            if (stanceElapsed >= stanceDuration)
            {
                stanceTransitioning = false;
                movementStance = stance;
                capsule.stepOffset = GroundStepOffset;
            }
        }

        private bool TrySampleProneSupport(Vector3 position, Vector3 forward,
            out Vector3 point, out Vector3 normal)
        {
            // Cache three samples for the whole body's footprint. This also
            // avoids re-querying the continuous ground in each overlap.
            if (Time.unscaledTime < nextProneSupportSample &&
                (position - supportSamplePosition).sqrMagnitude < 0.01f &&
                Vector3.Dot(forward, supportSampleForward) > 0.985f)
            {
                point = supportSamplePoint;
                normal = supportSampleNormal;
                return true;
            }

            nextProneSupportSample = -1f;
            continuousSupport = null;
            point = position;
            normal = Vector3.up;
            float walkable = Mathf.Cos(Mathf.Min(28f, capsule.slopeLimit) * Mathf.Deg2Rad);
            float floorHeight = position.y + (standingCenter.y - standingHeight * 0.5f) * transform.lossyScale.y;
            if (!TryFindProneFloor(position, 0.65f, 1.25f, floorHeight + 0.145f,
                    walkable, out RaycastHit middle))
                return false;

            Vector3 reach = Vector3.ProjectOnPlane(forward, middle.normal).normalized * 0.55f;
            Vector3 frontPoint = middle.point + reach;
            Vector3 rearPoint = middle.point - reach;
            if (!TryFindProneFloor(frontPoint, 0.16f, 0.36f, frontPoint.y + 0.13f,
                    walkable, out RaycastHit front) ||
                !TryFindProneFloor(rearPoint, 0.16f, 0.36f, rearPoint.y + 0.13f,
                    walkable, out RaycastHit rear))
                return false;

            supportSamplePosition = position;
            supportSampleForward = forward;
            point = supportSamplePoint = middle.point;
            normal = supportSampleNormal = middle.normal;
            nextProneSupportSample = Time.unscaledTime + 0.08f;
            // A single box has one support face. A mesh/terrain can also
            // contain a wall or bank, so always inspect those overlaps.
            if (middle.collider is BoxCollider &&
                middle.collider == front.collider && middle.collider == rear.collider)
                continuousSupport = middle.collider;
            return true;
        }

        private bool TryFindProneFloor(Vector3 position, float above, float distance, float maximumY,
            float minNormalY, out RaycastHit floor)
        {
            floor = default;
            int count = Physics.RaycastNonAlloc(position + Vector3.up * above,
                Vector3.down, proneSupportHits, distance,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (count >= proneSupportHits.Length) return false;
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = proneSupportHits[i];
                if (hit.collider == null || hit.point.y > maximumY || hit.collider.transform.IsChildOf(transform) ||
                    hit.distance >= nearest) continue;
                nearest = hit.distance;
                floor = hit;
            }
            return nearest < float.PositiveInfinity && floor.normal.y >= minNormalY;
        }

        private bool HasRoomAbove(float proposedHeight)
        {
            // Query only the *newly occupied headroom*, never the full
            // expanded standing capsule. A full-body overlap intersects the
            // ground TerrainCollider when the player stands on a slope and
            // can incorrectly lock crouch/prone forever.
            float floor = standingCenter.y - standingHeight * 0.5f;
            float currentTop = floor + capsule.height;
            float targetTop = floor + proposedHeight;
            float radius = Mathf.Max(0.05f, capsule.radius - capsule.skinWidth);
            // A tiny smooth-resize step still uses a sphere of full radius.
            // Clamp its lower centre to its upper centre so that sphere can
            // NEVER protrude above the proposed top and invent a low ceiling.
            float to = Mathf.Max(floor + radius, targetTop - radius);
            float from = Mathf.Min(to,
                Mathf.Max(floor + radius, currentTop - radius * 0.35f));
            Vector3 low = transform.TransformPoint(
                new Vector3(standingCenter.x, from, standingCenter.z));
            Vector3 high = transform.TransformPoint(
                new Vector3(standingCenter.x, to, standingCenter.z));
            // Mask the saved player layer, never mask real ceilings/branches.
            // Include other players even if they share the saved player's layer;
            // skip ONLY colliders belonging to this player in the hit loop.
            int mask = Physics.DefaultRaycastLayers;
            int count = Physics.OverlapCapsuleNonAlloc(low, high, radius, nearHits,
                mask, QueryTriggerInteraction.Ignore);
            Collider[] headroomHits = nearHits;
            if (count >= nearHits.Length)
            {
                headroomHits = crowdedHits;
                count = Physics.OverlapCapsuleNonAlloc(low, high, radius,
                    crowdedHits, mask, QueryTriggerInteraction.Ignore);
                // A saturated unordered buffer cannot establish clear headroom.
                if (count >= crowdedHits.Length) return false;
            }
            for (int i = 0; i < count; i++)
            {
                Collider hit = headroomHits[i];
                if (hit != null && !hit.transform.IsChildOf(transform)) return false;
            }
            return true;
        }

        // The upright controller capsule does not cover a prone character's
        // horizontal head/feet footprint while rotating beside cover.
        // Perform this check only for actual prone-facing changes.
        public bool CanFaceProne(Vector3 forward)
        {
            return movementStance != PlayerStance.Prone ||
                HasProneClearance(transform.position, forward);
        }

        // Called after Move so each sample uses the capsule's resolved position.
        // Limit how far the torso can turn in a low stance. When prone, keep
        // the last clear heading along the arc, including on long frames.
        public Quaternion LimitStanceTurn(Quaternion requested, float deltaTime)
        {
            if (movementStance == PlayerStance.Standing) return requested;
            Quaternion current = transform.rotation;
            if (movementStance == PlayerStance.Crouching)
                return capsule.isGrounded
                    ? Quaternion.RotateTowards(current, requested, 240f * deltaTime)
                    : requested;

            float delta = Mathf.DeltaAngle(current.eulerAngles.y, requested.eulerAngles.y);
            float travel = Mathf.Min(Mathf.Abs(delta), 100f * deltaTime);
            if (travel < 0.001f) return current;

            // A 3-degree interval moves each 55 cm footprint endpoint by
            // under 3 cm. At the motor's 150 ms cap this takes at most five
            // existing non-allocating clearance checks in an unobstructed turn.
            int samples = Mathf.CeilToInt(travel / 3f);
            float step = Mathf.Sign(delta) * travel / samples;
            Quaternion clear = current;
            for (int i = 1; i <= samples; i++)
            {
                Quaternion sample = Quaternion.AngleAxis(i * step, Vector3.up) * current;
                if (!CanFaceProne(sample * Vector3.forward)) break;
                clear = sample;
            }
            return clear;
        }

        public bool CanAdvanceProne(Vector3 worldPosition, Vector3 travelDirection)
        {
            if (movementStance != PlayerStance.Prone) return true;
            // The horizontal footprint belongs to the actual facing body,
            // not the travel vector: armed prone strafing can move sideways.
            // Keep travelDirection in the public signature for existing callers.
            return HasProneClearance(worldPosition, transform.forward);
        }

        private bool HasProneClearance(Vector3 position, Vector3 forward)
        {
            // The built-in CharacterController is vertical; its short prone
            // height alone would let the visual body's head pass through
            // house walls. Check an approximate *horizontal* body footprint
            // before lying down and before each prone movement.
            if (!TrySampleProneSupport(position, forward, out Vector3 floor, out Vector3 normal))
                return false;
            Vector3 fwd = Vector3.ProjectOnPlane(forward, normal).normalized;
            if (fwd.sqrMagnitude < 0.9f) fwd = transform.forward;
            Vector3 feet = position - Vector3.up * (Vector3.Dot(position - floor, normal) / normal.y);
            Vector3 center = feet + normal * 0.31f;
            Vector3 head = center + fwd * 0.55f;
            Vector3 legs = center - fwd * 0.55f;
            // Include other players even if they share the saved player's layer;
            // skip ONLY colliders belonging to this player in the hit loop.
            int mask = Physics.DefaultRaycastLayers;
            int count = Physics.OverlapCapsuleNonAlloc(head, legs, 0.245f, nearHits,
                mask, QueryTriggerInteraction.Ignore);
            Collider[] proneHits = nearHits;
            if (count >= nearHits.Length)
            {
                proneHits = crowdedHits;
                count = Physics.OverlapCapsuleNonAlloc(head, legs, 0.245f,
                    crowdedHits, mask, QueryTriggerInteraction.Ignore);
                if (count >= crowdedHits.Length) return false;
            }
            for (int i = 0; i < count; i++)
            {
                Collider obstacle = proneHits[i];
                if (obstacle == null || obstacle == continuousSupport || obstacle.transform.IsChildOf(transform))
                    continue;
                // The character rests ON terrain or paved bridges/steps.
                // The horizontal prone footprint must not interpret a flat
                // MeshCollider floor as a wall when crossing a bridge.
                // Only ignore real support below the chest; an upright wall,
                // overhead slab or steeper face still blocks crawling.
                if (obstacle is TerrainCollider terrain)
                {
                    // Terrain is normally support, but an uphill bank can
                    // intersect the prone head or feet. Never ignore such a
                    // contact unconditionally: both ends must have low,
                    // walkable support before permitting the pose or crawl.
                    // Two collider-local raycasts only when terrain already
                    // overlaps the prone footprint (no per-frame allocations).
                    float minWalkableNormal =
                        Mathf.Cos(capsule.slopeLimit * Mathf.Deg2Rad);
                    for (int end = 0; end < 2; end++)
                    {
                        Vector3 sample = end == 0 ? head : legs;
                        if (!terrain.Raycast(
                                new Ray(sample + Vector3.up * 1.2f, Vector3.down),
                                out RaycastHit sampleHit, 2.5f) ||
                            sampleHit.point.y > sample.y - 0.10f ||
                            sampleHit.normal.y < minWalkableNormal)
                            return false;
                    }
                    continue;
                }
                // An unobstructed centre point on a bridge or mesh ramp does
                // not guarantee that its higher end clears a prone head/foot.
                // Probe only a collider already overlapping the prone body,
                // reusing collider-local raycasts without allocations.
                bool walkableAlongBody = true;
                float minMeshNormal = Mathf.Cos(capsule.slopeLimit * Mathf.Deg2Rad);
                for (int end = 0; end < 2; end++)
                {
                    Vector3 sample = end == 0 ? head : legs;
                    Ray floorProbe = new Ray(sample + Vector3.up * 0.45f, Vector3.down);
                    if (!obstacle.Raycast(floorProbe, out RaycastHit floorHit, 1.1f) ||
                        floorHit.point.y > sample.y - 0.11f ||
                        floorHit.normal.y < minMeshNormal)
                    {
                        walkableAlongBody = false;
                        break;
                    }
                }
                if (walkableAlongBody) continue;
                return false;
            }
            return true;
        }

        private void Update()
        {
            if (!AcceptsCombatInput)
            {
                CancelPendingCombatInput();
                return;
            }
            AdvanceStanceTransition(Time.deltaTime);
            if (punchContactTime >= 0f && Time.time >= punchContactTime)
            {
                punchContactTime = -1f;
                Punch();
            }
            // Preserve a tap during the visual draw, including a quickly
            // released automatic trigger. A released tap still fires once.
            bool bufferedPress = IsArmed && (!equippedGun.Automatic || shotQueuedDuringDraw) &&
                semiShotQueued && Time.time <= fireBufferUntil;
            if ((!fireHeld && !bufferedPress) || Time.time < nextAttack) return;
            if (IsArmed)
            {
                if (!equippedGun.Automatic && !bufferedPress) return;
                ShootEquippedWeapon();
                // At very low FPS a second deadline can fall in this frame.
                // Keep the physics/VFX cost bounded after a long hitch.
                if (equippedGun.Automatic && fireHeld && Time.time >= nextAttack)
                    ShootEquippedWeapon();
                return;
            }

            if (fireHeld) QueuePunch();
        }

        private void QueuePunch()
        {
            lastPunchTime = Time.time;
            nextAttack = lastPunchTime + Mathf.Max(punchInterval, PunchVisualDuration);
            punchContactTime = lastPunchTime + PunchContactDelay;
            attackSequence++;
        }

        private void ShootEquippedWeapon()
        {
            if (equippedGun.RequiresVisualDraw) return;
            if (equippedGun.Automatic)
            {
                if (automaticShotFrame != Time.frameCount)
                {
                    automaticShotFrame = Time.frameCount;
                    automaticShotsThisFrame = 0;
                }
                if (automaticShotsThisFrame >= MaximumAutomaticShotsPerFrame) return;
            }
            // One trigger edge consumes at most one shot. Shared by the HUD
            // press path and the held-fire Update path, so their cooldown and
            // animation sequencing cannot drift apart.
            if (equippedGun.TryFire(motor.AimCamera, gameObject))
            {
                if (equippedGun.Automatic) automaticShotsThisFrame++;
                if (!fireHeld) equippedGun.BreakAutomaticCadence();
                semiShotQueued = false;
                shotQueuedDuringDraw = false;
                fireBufferUntil = 0f;
                attackSequence++;
                lastShootTime = Time.time;
                nextAttack = equippedGun.NextFireTime;
            }
            else nextAttack = Time.time + 0.04f;
        }

        // A nearby bush/rock/house collider is not a damage target. Select a
        // receiver first, then let the raycast below reject real solid cover.
        // Mirrors AarisEquippedGun.ApplyDamage's parent traversal without
        // allocating GetComponents arrays for each melee candidate.
        private IAarisDamageable FindPunchReceiver(Collider hit)
        {
            if (hit == null) return null;
            for (Transform node = hit.transform; node != null; node = node.parent)
            {
                punchComponents.Clear();
                node.GetComponents(punchComponents);
                for (int i = 0; i < punchComponents.Count; i++)
                {
                    if (punchComponents[i] is IAarisDamageable damageable)
                    {
                        punchComponents.Clear();
                        return damageable;
                    }
                }
            }
            punchComponents.Clear();
            return null;
        }

        private void Punch()
        {
            Vector3 origin = transform.position + Vector3.up *
                (stance == PlayerStance.Prone ? 0.4f : stance == PlayerStance.Crouching ? 0.73f : 1.15f);
            Vector3 center = origin + transform.forward * (punchReach * 0.58f);
            // Include other players even if they share the saved player's layer;
            // skip ONLY colliders belonging to this player in the hit loop.
            int mask = Physics.DefaultRaycastLayers;
            int count = Physics.OverlapSphereNonAlloc(center, 0.48f, nearHits, mask,
                QueryTriggerInteraction.Ignore);
            // Full NonAlloc buffers are unordered. A target beyond the first
            // 32 foliage/house colliders must not make a valid punch vanish.
            // Retry with the reusable large buffer, allocating only if even
            // that rare crowd query saturates. Always evaluate complete hits.
            Collider[] punchHits = nearHits;
            if (count >= nearHits.Length)
            {
                punchHits = crowdedHits;
                count = Physics.OverlapSphereNonAlloc(center, 0.48f, crowdedHits,
                    mask, QueryTriggerInteraction.Ignore);
                if (count >= crowdedHits.Length)
                {
                    punchHits = Physics.OverlapSphere(center, 0.48f, mask,
                        QueryTriggerInteraction.Ignore);
                    count = punchHits.Length;
                }
            }
            int best = -1;
            float distance = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                var collider = punchHits[i];
                if (collider == null || collider.transform.IsChildOf(transform)) continue;
                Vector3 nearest = collider.ClosestPoint(origin);
                Vector3 candidateOffset = nearest - origin;
                if (candidateOffset.sqrMagnitude > punchReach * punchReach ||
                    Vector3.Dot(transform.forward, candidateOffset) < 0.1f) continue;
                // Non-damageable vegetation or cover must not absorb a punch.
                // The separate raycast still prevents hitting through cover.
                float candidateDistance = candidateOffset.sqrMagnitude;
                if (candidateDistance >= distance) continue;
                IAarisDamageable receiver = FindPunchReceiver(collider);
                if (receiver != null && HasPunchLineOfSight(origin, nearest, receiver, mask))
                {
                    distance = candidateDistance;
                    best = i;
                }
            }
            if (best < 0) return;
            Collider victim = punchHits[best];
            Vector3 point = victim.ClosestPoint(origin);
            // An impact sound is not a swing sound: trigger it only if the
            // punch actually passed overlap, reach and wall-occlusion tests.
            AarisAudioDirector.Punch(point);
            AarisEquippedGun.ApplyDamage(victim, punchDamage, gameObject);
        }

        private bool HasPunchLineOfSight(Vector3 origin, Vector3 destination,
            IAarisDamageable receiver, int mask)
        {
            Vector3 delta = destination - origin;
            if (delta.sqrMagnitude <= 0.0025f) return true;
            float length = delta.magnitude;
            int count = Physics.RaycastNonAlloc(origin, delta / length,
                punchRayHits, length, mask, QueryTriggerInteraction.Ignore);
            RaycastHit[] hits = punchRayHits;
            if (count >= punchRayHits.Length)
            {
                // A saturated unsorted buffer can omit real cover behind an
                // ignored arm/limb. Only the rare overflow allocates a full set.
                hits = Physics.RaycastAll(origin, delta / length, length,
                    mask, QueryTriggerInteraction.Ignore);
                count = hits.Length;
            }
            for (int i = 0; i < count; i++)
            {
                Collider obstacle = hits[i].collider;
                if (obstacle == null || obstacle.transform.IsChildOf(transform) ||
                    FindPunchReceiver(obstacle) == receiver) continue;
                // Skipping an own/target limb must not skip the wall AFTER it.
                return false;
            }
            return true;
        }

        private void CancelPendingCombatInput()
        {
            // Android can lose focus through a system overlay/gesture without
            // delivering the same pointer-up or pause sequence as normal app
            // backgrounding. Never let held fire, a buffered semi-auto tap or
            // a queued punch contact survive a control-authority interruption.
            fireHeld = false;
            if (equippedGun != null) equippedGun.BreakAutomaticCadence();
            semiShotQueued = false;
            shotQueuedDuringDraw = false;
            fireBufferUntil = 0f;
            punchContactTime = -1f;
        }

        private void OnDisable() => CancelPendingCombatInput();

        private void OnApplicationPause(bool paused)
        {
            if (paused) CancelPendingCombatInput();
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused) CancelPendingCombatInput();
        }
    }
}
