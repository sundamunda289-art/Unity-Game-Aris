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
        private PlayerStance stance;
        private bool fireHeld;
        private bool semiShotQueued;
        private int attackSequence;
        private float lastPunchTime = -10f;
        private float punchContactTime = -1f;
        private float lastShootTime = -10f;

        public PlayerStance Stance => stance;
        public bool FireHeld => fireHeld;
        public bool IsArmed => equippedGun != null &&
            equippedGun.isActiveAndEnabled && equippedGun.Configured;
        public float CameraEyeHeight => stance == PlayerStance.Prone ? 0.54f
            : stance == PlayerStance.Crouching ? 0.94f : 1.45f;
        public int AttackSequence => attackSequence;
        public float LastPunchTime => lastPunchTime;
        public float LastShootTime => lastShootTime;

        // Keep 30 cm standing stairs, but do not let prone movement
        // automatically climb a ledge as high as a crouched torso.
        // This is independent from the temporary stepOffset=0 in the air.
        public float GroundStepOffset => stance == PlayerStance.Standing
            ? standingStepOffset : Mathf.Min(standingStepOffset,
                stance == PlayerStance.Crouching ? 0.20f : 0.16f);

        private void Awake()
        {
            capsule = GetComponent<CharacterController>();
            motor = GetComponent<AarisThirdPersonPlayer>();
            standingCenter = capsule.center;
            standingHeight = capsule.height;
            standingStepOffset = capsule.stepOffset;
            stance = PlayerStance.Standing;
        }

        public void Equip(AarisEquippedGun gun)
        {
            equippedGun = gun;
            nextAttack = 0f;
            semiShotQueued = false;
        }

        public void SetFireHeld(bool held)
        {
            // Semi-auto must fire on a NEW press only, never repeatedly
            // while held. Full-auto retains the original held cadence.
            if (held && !fireHeld) semiShotQueued = true;
            fireHeld = held;
            if (!held) semiShotQueued = false;
            if (held && motor != null) motor.CancelAutoSprint();
        }

        public bool ToggleCrouch() =>
            TrySetStance(stance == PlayerStance.Crouching
                ? PlayerStance.Standing : PlayerStance.Crouching);

        public bool ToggleProne() =>
            TrySetStance(stance == PlayerStance.Prone
                ? PlayerStance.Standing : PlayerStance.Prone);

        public bool TryStand() => TrySetStance(PlayerStance.Standing);

        public bool TrySetStance(PlayerStance target)
        {
            if (target == stance) return true;
            float height = target == PlayerStance.Standing ? standingHeight :
                target == PlayerStance.Crouching ? crouchHeight : proneHeight;
            // CharacterController cannot be shorter than its diameter.
            height = Mathf.Max(height, capsule.radius * 2f + 0.015f);
            if (height > capsule.height && !HasRoomAbove(height)) return false;
            if (target == PlayerStance.Prone &&
                !HasProneClearance(transform.position, transform.forward))
                return false;

            float floorRelative = standingCenter.y - standingHeight * 0.5f;
            capsule.height = height;
            capsule.center = new Vector3(standingCenter.x, floorRelative + height * 0.5f, standingCenter.z);
            stance = target;
            capsule.stepOffset = GroundStepOffset;
            return true;
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
            float from = Mathf.Max(floor + radius, currentTop - radius * 0.35f);
            float to = Mathf.Max(from, targetTop - radius);
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
            if (count == nearHits.Length) return false; // Buffer saturation: stay low.
            for (int i = 0; i < count; i++)
            {
                Collider hit = nearHits[i];
                if (hit != null && !hit.transform.IsChildOf(transform)) return false;
            }
            return true;
        }

        public bool CanAdvanceProne(Vector3 worldPosition, Vector3 travelDirection)
        {
            if (stance != PlayerStance.Prone) return true;
            Vector3 direction = travelDirection.sqrMagnitude > 0.0025f
                ? travelDirection.normalized : transform.forward;
            return HasProneClearance(worldPosition, direction);
        }

        private bool HasProneClearance(Vector3 position, Vector3 forward)
        {
            // The built-in CharacterController is vertical; its short prone
            // height alone would let the visual body's head pass through
            // house walls. Check an approximate *horizontal* body footprint
            // before lying down and before each prone movement.
            Vector3 fwd = new Vector3(forward.x, 0f, forward.z).normalized;
            if (fwd.sqrMagnitude < 0.9f) fwd = transform.forward;
            Vector3 center = position + Vector3.up * 0.31f;
            Vector3 head = center + fwd * 0.55f;
            Vector3 legs = center - fwd * 0.55f;
            // Include other players even if they share the saved player's layer;
            // skip ONLY colliders belonging to this player in the hit loop.
            int mask = Physics.DefaultRaycastLayers;
            int count = Physics.OverlapCapsuleNonAlloc(head, legs, 0.245f, nearHits,
                mask, QueryTriggerInteraction.Ignore);
            if (count == nearHits.Length) return false;
            for (int i = 0; i < count; i++)
            {
                Collider obstacle = nearHits[i];
                if (obstacle == null || obstacle.transform.IsChildOf(transform)) continue;
                // The character rests ON terrain or paved bridges/steps.
                // The horizontal prone footprint must not interpret a flat
                // MeshCollider floor as a wall when crossing a bridge.
                // Only ignore real support below the chest; an upright wall,
                // overhead slab or steeper face still blocks crawling.
                if (obstacle is TerrainCollider) continue;
                Ray floorProbe = new Ray(center + Vector3.up * 0.45f, Vector3.down);
                if (obstacle.Raycast(floorProbe, out RaycastHit floorHit, 1.1f) &&
                    floorHit.point.y <= center.y - 0.11f &&
                    floorHit.normal.y >= Mathf.Cos(capsule.slopeLimit * Mathf.Deg2Rad))
                    continue;
                return false;
            }
            return true;
        }

        private void Update()
        {
            if (punchContactTime >= 0f && Time.time >= punchContactTime)
            {
                punchContactTime = -1f;
                Punch();
            }
            if (!fireHeld || Time.time < nextAttack) return;
            if (IsArmed)
            {
                if (!equippedGun.Automatic && !semiShotQueued) return;
                if (!equippedGun.Automatic) semiShotQueued = false;
                if (equippedGun.TryFire(motor.AimCamera, gameObject))
                {
                    attackSequence++;
                    lastShootTime = Time.time;
                    nextAttack = Time.time + equippedGun.SecondsBetweenShots;
                }
                else nextAttack = Time.time + 0.04f;
                return;
            }

            lastPunchTime = Time.time;
            nextAttack = lastPunchTime + Mathf.Max(punchInterval, PunchVisualDuration);
            punchContactTime = lastPunchTime + PunchContactDelay;
            attackSequence++;
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
            Collider[] targets = nearHits;
            if (count >= nearHits.Length)
            {
                targets = Physics.OverlapSphere(center, 0.48f, mask,
                    QueryTriggerInteraction.Ignore);
                count = targets.Length;
            }
            int best = -1;
            float distance = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                var collider = targets[i];
                if (collider == null || collider.transform.IsChildOf(transform)) continue;
                Vector3 nearest = collider.ClosestPoint(origin);
                Vector3 candidateOffset = nearest - origin;
                if (candidateOffset.sqrMagnitude > punchReach * punchReach ||
                    Vector3.Dot(transform.forward, candidateOffset) < 0.1f) continue;
                if (candidateOffset.sqrMagnitude < distance)
                {
                    distance = candidateOffset.sqrMagnitude;
                    best = i;
                }
            }
            if (best < 0) return;
            Collider victim = targets[best];
            Vector3 point = victim.ClosestPoint(origin);
            Vector3 delta = point - origin;
            // A fist cannot hit a player through a wall or a solid rock.
            if (delta.sqrMagnitude > 0.0025f &&
                Physics.Raycast(origin, delta.normalized, out RaycastHit obstruction,
                    delta.magnitude, mask, QueryTriggerInteraction.Ignore) &&
                obstruction.collider != victim &&
                !obstruction.collider.transform.IsChildOf(victim.transform))
                return;
            AarisEquippedGun.ApplyDamage(victim, punchDamage, gameObject);
        }

        private void OnDisable()
        {
            fireHeld = false;
            semiShotQueued = false;
            punchContactTime = -1f;
        }

        private void OnApplicationPause(bool paused)
        {
            if (!paused) return;
            fireHeld = false;
            punchContactTime = -1f;
        }
    }
}
