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
        // Reuse one component list for damage-receiver lookup on rare melee attacks.
        private readonly List<Component> punchComponents = new List<Component>(8);
        private PlayerStance stance;
        private bool fireHeld;
        private bool semiShotQueued;
        // Short trigger edges survive a cooldown / slow Android input frame,
        // but must not manufacture a delayed shot after a long reload.
        private const float SemiTapBufferSeconds = 0.16f;
        private float fireBufferUntil;
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
            // A weapon transition invalidates the previous unarmed wind-up.
            // Releasing Fire still permits contact; changing equipment does not.
            punchContactTime = -1f;
            lastPunchTime = -10f;
            nextAttack = 0f;
            semiShotQueued = false;
            fireBufferUntil = 0f;
        }

        public void SetFireHeld(bool held)
        {
            // Unity's EventSystem can process both Down and Up between combat
            // Updates on 10-20 FPS phones. Fire immediately on the new press:
            // otherwise a quick tap is discarded before Update can see it.
            bool newPress = held && !fireHeld;
            fireHeld = held;
            // A released semi-auto press retains its brief buffer until it
            // fires or expires; automatic fire stops immediately on release.
            if (!held && (!IsArmed || equippedGun.Automatic))
            {
                semiShotQueued = false;
                fireBufferUntil = 0f;
            }
            // The single player motor owns sprint cancellation. Clearing it
            // here would erase jump-shot momentum before its Update runs when
            // Jump and Fire arrive during the same mobile input frame.
            if (!newPress) return;
            semiShotQueued = true;
            fireBufferUntil = Time.time + SemiTapBufferSeconds;
            // Do not bypass the gun's ammo, reload, cooldown or cover checks.
            // An unarmed tap must also begin its punch wind-up immediately,
            // otherwise a short Down/Up pair is lost before the next Update.
            if (!isActiveAndEnabled || Time.timeScale <= 0f ||
                Time.time < nextAttack) return;
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
            Collider[] headroomHits = nearHits;
            if (count >= nearHits.Length)
            {
                // A filled NonAlloc result is incomplete, not proof of a wall.
                // In dense settlements resolve ALL contacts, then still reject
                // every real ceiling while ignoring only our own colliders.
                headroomHits = crowdedHits;
                count = Physics.OverlapCapsuleNonAlloc(low, high, radius,
                    crowdedHits, mask, QueryTriggerInteraction.Ignore);
                if (count >= crowdedHits.Length)
                {
                    headroomHits = Physics.OverlapCapsule(low, high, radius, mask,
                        QueryTriggerInteraction.Ignore);
                    count = headroomHits.Length;
                }
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
            return stance != PlayerStance.Prone ||
                HasProneClearance(transform.position, forward);
        }

        public bool CanAdvanceProne(Vector3 worldPosition, Vector3 travelDirection)
        {
            if (stance != PlayerStance.Prone) return true;
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
            Collider[] proneHits = nearHits;
            if (count >= nearHits.Length)
            {
                // Crowded terrain can fill the reusable buffer with floors and
                // the player's own colliders. Query every hit on overflow so
                // real walls still block crawling without falsely locking pose.
                proneHits = crowdedHits;
                count = Physics.OverlapCapsuleNonAlloc(head, legs, 0.245f,
                    crowdedHits, mask, QueryTriggerInteraction.Ignore);
                if (count >= crowdedHits.Length)
                {
                    proneHits = Physics.OverlapCapsule(head, legs, 0.245f, mask,
                        QueryTriggerInteraction.Ignore);
                    count = proneHits.Length;
                }
            }
            for (int i = 0; i < count; i++)
            {
                Collider obstacle = proneHits[i];
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
            // A quick semi-auto press can start and end between Updates.
            // Only carry a release across a short cooldown, never indefinitely.
            bool bufferedSemi = IsArmed && !equippedGun.Automatic &&
                semiShotQueued && Time.time <= fireBufferUntil;
            if ((!fireHeld && !bufferedSemi) || Time.time < nextAttack) return;
            if (IsArmed)
            {
                if (!equippedGun.Automatic && !bufferedSemi) return;
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
            // One trigger edge consumes at most one shot. Shared by the HUD
            // press path and the held-fire Update path, so their cooldown and
            // animation sequencing cannot drift apart.
            if (equippedGun.TryFire(motor.AimCamera, gameObject))
            {
                semiShotQueued = false;
                fireBufferUntil = 0f;
                attackSequence++;
                lastShootTime = Time.time;
                nextAttack = Time.time + equippedGun.SecondsBetweenShots;
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
                if (candidateDistance < distance && FindPunchReceiver(collider) != null)
                {
                    distance = candidateDistance;
                    best = i;
                }
            }
            if (best < 0) return;
            Collider victim = punchHits[best];
            Vector3 point = victim.ClosestPoint(origin);
            Vector3 delta = point - origin;
            // A fist cannot hit a player through a wall or a solid rock.
            if (delta.sqrMagnitude > 0.0025f &&
                Physics.Raycast(origin, delta.normalized, out RaycastHit obstruction,
                    delta.magnitude, mask, QueryTriggerInteraction.Ignore) &&
                obstruction.collider != victim &&
                !obstruction.collider.transform.IsChildOf(victim.transform) &&
                // A skinned enemy may have sibling limb colliders. A same-
                // receiver collider is not a wall between fist and target.
                FindPunchReceiver(obstruction.collider) != FindPunchReceiver(victim))
                return;
            // An impact sound is not a swing sound: trigger it only if the
            // punch actually passed overlap, reach and wall-occlusion tests.
            AarisAudioDirector.Punch(point);
            AarisEquippedGun.ApplyDamage(victim, punchDamage, gameObject);
        }

        private void OnDisable()
        {
            fireHeld = false;
            semiShotQueued = false;
            fireBufferUntil = 0f;
            punchContactTime = -1f;
        }

        private void OnApplicationPause(bool paused)
        {
            if (!paused) return;
            fireHeld = false;
            semiShotQueued = false;
            fireBufferUntil = 0f;
            punchContactTime = -1f;
        }
    }
}
