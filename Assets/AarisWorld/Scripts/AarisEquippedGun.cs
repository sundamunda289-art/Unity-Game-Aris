using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// Weapon state follows the actual physical gun between hand and floor.
    /// Ammo and reload deadlines must survive drop/pickup and never duplicate.
    /// </summary>
    public sealed class AarisWeaponState
    {
        public readonly AarisWeaponSpec Spec;
        public readonly int Magazine, Reserve;
        public readonly float ReloadUntil, NextShotAt;
        public readonly bool Reloading;
        public readonly uint SpreadSeed;

        public AarisWeaponState(AarisWeaponSpec spec, uint seed)
            : this(spec, spec != null ? spec.magazineSize : 0,
                spec != null ? spec.reserveAmmo : 0, 0f, 0f, false, seed) { }

        public AarisWeaponState(AarisWeaponSpec spec, int magazine, int reserve,
            float reloadUntil, float nextShotAt, bool reloading, uint seed)
        {
            Spec = spec;
            Magazine = magazine;
            Reserve = reserve;
            ReloadUntil = reloadUntil;
            NextShotAt = nextShotAt;
            Reloading = reloading;
            SpreadSeed = seed;
        }
    }

    /// <summary>
    /// One implementation for all locally configured weapon types.
    /// Emits camera-aligned hit scans, checks line of sight from the stance
    /// muzzle and applies damage only via the game-side IAarisDamageable hook.
    /// Single-player prototype; online sessions need server authority.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AarisEquippedGun : MonoBehaviour
    {
        private readonly RaycastHit[] rayHits = new RaycastHit[48];
        private AarisWeaponSpec spec;
        private int magazine, reserve;
        private float reloadUntil, nextFire;
        private uint spreadSeed;
        private bool reloading;
        public bool Configured => spec != null;
        public bool Automatic => spec != null && spec.automatic;
        public float SecondsBetweenShots => spec != null ? spec.ShotInterval : 1f;
        public int Magazine { get { RefreshReload(); return magazine; } }
        public int Reserve { get { RefreshReload(); return reserve; } }
        public bool Reloading { get { RefreshReload(); return reloading; } }
        public string DisplayName => spec != null ? spec.displayName : "Unarmed";
        public float ScopeFov => spec != null ? spec.adsFov : 60f;
        public string ModelResource => spec != null ? spec.modelResource : null;
        public AarisWeaponSpec Spec => spec;

        public void Configure(AarisWeaponSpec weapon, uint matchSeed) =>
            Configure(new AarisWeaponState(weapon, matchSeed ^ 0x7F4A7C15u));

        public void Configure(AarisWeaponState state)
        {
            if (state == null || state.Spec == null || !state.Spec.Valid)
            {
                Debug.LogError("Aaris: invalid weapon state configured.");
                enabled = false;
                return;
            }
            spec = state.Spec;
            magazine = Mathf.Clamp(state.Magazine, 0, spec.magazineSize);
            reserve = Mathf.Max(0, state.Reserve);
            spreadSeed = state.SpreadSeed;
            nextFire = state.NextShotAt;
            reloadUntil = state.ReloadUntil;
            reloading = state.Reloading;
            RefreshReload();
        }

        public AarisWeaponState CaptureState()
        {
            RefreshReload();
            if (spec == null) return null;
            return new AarisWeaponState(spec, magazine, reserve,
                reloadUntil, nextFire, reloading, spreadSeed);
        }

        private void Update() => RefreshReload();

        // Even an unequipped weapon must complete its reload on schedule.
        // Unity stops calling Update on inactive inventory slot GameObjects.
        public void RefreshReload()
        {
            if (!reloading || spec == null || Time.time < reloadUntil) return;
            int transfer = Mathf.Min(spec.magazineSize - magazine, reserve);
            magazine += transfer;
            reserve -= transfer;
            reloading = false;
        }

        public bool BeginReload()
        {
            RefreshReload();
            if (spec == null || reloading || magazine >= spec.magazineSize || reserve == 0)
                return false;
            reloadUntil = Time.time + spec.reloadSeconds;
            reloading = true;
            return true;
        }

        private bool FirstExternalHit(Ray ray, float range, Transform owner,
            out RaycastHit nearest, out bool saturated)
        {
            nearest = default(RaycastHit);
            int count = Physics.RaycastNonAlloc(ray, rayHits, range,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            saturated = count >= rayHits.Length;
            if (saturated) return false;
            float minDistance = float.PositiveInfinity;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                Collider collider = rayHits[i].collider;
                if (collider == null || collider.transform.IsChildOf(owner)) continue;
                if (rayHits[i].distance >= minDistance) continue;
                minDistance = rayHits[i].distance;
                nearest = rayHits[i];
                found = true;
            }
            return found;
        }

        private static IAarisDamageable DamageReceiver(Collider collider)
        {
            if (collider == null) return null;
            for (Transform node = collider.transform; node != null; node = node.parent)
            {
                foreach (var component in node.GetComponents<MonoBehaviour>())
                    if (component is IAarisDamageable damageable)
                        return damageable;
            }
            return null;
        }

        private static bool SameTarget(Collider a, Collider b)
        {
            if (a == null || b == null) return false;
            if (a == b) return true;
            if (a.attachedRigidbody != null && a.attachedRigidbody == b.attachedRigidbody)
                return true;
            if (a.transform.IsChildOf(b.transform) ||
                b.transform.IsChildOf(a.transform)) return true;
            // Some offline bots and destructible objects have separate
            // sibling hitbox Colliders without one shared Rigidbody. The
            // camera and muzzle can hit different parts of the SAME target.
            // Compare the actual damage recipient, never a generic root
            // transform (which might also own unrelated cover/walls).
            IAarisDamageable recipient = DamageReceiver(a);
            return recipient != null && ReferenceEquals(recipient, DamageReceiver(b));
        }

        public bool TryFire(Camera camera, GameObject owner)
        {
            RefreshReload();
            if (spec == null || camera == null || owner == null || reloading ||
                Time.time < nextFire) return false;
            if (magazine <= 0)
            {
                BeginReload();
                return false;
            }
            // Consume exactly one cartridge per trigger event, including
            // multi-pellet shotguns; reload and cooldown live in weapon state.
            magazine--;
            nextFire = Time.time + spec.ShotInterval;
            AarisBattleActions actions = owner.GetComponent<AarisBattleActions>();
            float muzzleHeight = actions != null
                ? Mathf.Max(0.28f, actions.CameraEyeHeight - 0.15f) : 1.3f;
            Vector3 muzzle = owner.transform.position + Vector3.up * muzzleHeight +
                owner.transform.forward * 0.24f;

            for (int pellet = 0; pellet < spec.pellets; pellet++)
            {
                Vector2 spread = new Vector2(AarisWeaponRandom.Next01(ref spreadSeed) * 2f - 1f,
                    AarisWeaponRandom.Next01(ref spreadSeed) * 2f - 1f);
                spread = Vector2.ClampMagnitude(spread, 1f) * spec.spreadDegrees;
                Vector3 look = Quaternion.AngleAxis(spread.x, camera.transform.up) *
                    Quaternion.AngleAxis(-spread.y, camera.transform.right) * camera.transform.forward;
                Ray aim = new Ray(camera.transform.position, look);
                bool targetFound = FirstExternalHit(aim, spec.rangeMetres,
                    owner.transform, out RaycastHit target, out bool saturated);
                if (saturated) continue; // fail closed on crowded physics
                Vector3 destination = targetFound ? target.point : aim.GetPoint(spec.rangeMetres);
                Vector3 path = destination - muzzle;
                float distance = path.magnitude;
                if (distance > 0.025f)
                {
                    bool muzzleHitFound = FirstExternalHit(
                        new Ray(muzzle, path / distance), distance,
                        owner.transform, out RaycastHit muzzleHit, out saturated);
                    if (saturated) continue;
                    if (muzzleHitFound &&
                        (!targetFound || !SameTarget(target.collider, muzzleHit.collider)))
                        continue;
                }
                if (targetFound) ApplyDamage(target.collider, spec.damage, owner);
            }
            var motor = owner.GetComponent<AarisThirdPersonPlayer>();
            if (motor != null) motor.ApplyWeaponRecoil(spec.recoilPitchDegrees,
                (AarisWeaponRandom.Next01(ref spreadSeed) - 0.5f) *
                spec.recoilPitchDegrees * 0.4f);
            if (magazine == 0) BeginReload();
            return true;
        }

        internal static void ApplyDamage(Collider collider, float amount, GameObject attacker)
        {
            IAarisDamageable damageable = DamageReceiver(collider);
            if (damageable != null) damageable.ApplyDamage(amount, attacker);
        }
    }

    public interface IAarisDamageable
    {
        void ApplyDamage(float amount, GameObject attacker);
    }
}
