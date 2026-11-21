using System;
using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// Offline health receiver shared by player and future AI bot prefabs.
    /// Existing guns and frag explosions already use IAarisDamageable.
    /// The player match controller consumes the same death event available to bots.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AarisOfflineHealth : MonoBehaviour, IAarisDamageable
    {
        [SerializeField, Min(1f)] private float maximumHealth = 200f;
        [SerializeField, Min(1f)] private float maximumHelmetDurability = 75f;
        [SerializeField, Range(0f, 1f)] private float helmetProtection = 0.35f;
        private float current, helmetDurability;
        private bool dead;
        private GameObject lastAttacker;

        public GameObject KillingAttacker => dead ? lastAttacker : null;
        public float CurrentHealth => current;
        public float MaximumHealth => maximumHealth;
        public bool IsDead => dead;
        public float HelmetDurability => helmetDurability;
        // Actual hitscan headshots use ApplyBallisticDamage. Older
        // IAarisDamageable callers retain generic mitigation for compatibility.
        public bool TryEquipHelmet()
        {
            if (dead || helmetDurability >= maximumHelmetDurability - 0.01f) return false;
            helmetDurability = maximumHelmetDurability;
            HealthChanged?.Invoke(this);
            return true;
        }
        public event Action<AarisOfflineHealth> Died;
        public event Action<AarisOfflineHealth> HealthChanged;
        // Actual damage after helmet absorption, with original source for the
        // player's hit-flash/direction HUD. No global lookups or allocations.
        public event Action<AarisOfflineHealth, float, GameObject> Damaged;

        private void Awake() => current = maximumHealth;

        // IAarisDamageable legacy callers keep existing generic mitigation.
        // Hitscan guns supply true hit location via ApplyBallisticDamage so
        // a body shot no longer consumes protective HEAD gear.
        public void ApplyDamage(float amount, GameObject attacker) =>
            ApplyDamageInternal(amount, attacker, true);

        public void ApplyBallisticDamage(float amount, GameObject attacker, bool headshot) =>
            ApplyDamageInternal(amount, attacker, headshot);

        private void ApplyDamageInternal(float amount, GameObject attacker, bool helmetEligible)
        {
            if (dead || float.IsNaN(amount) || float.IsInfinity(amount) || amount <= 0f) return;
            float previous = current;
            float absorbed = helmetEligible
                ? Mathf.Min(helmetDurability, amount * helmetProtection) : 0f;
            helmetDurability = Mathf.Max(0f, helmetDurability - absorbed);
            current = Mathf.Max(0f, current - (amount - absorbed));
            // Publish fatality before notifying HUD/AI health observers:
            // a zero-HP notification must already report IsDead = true.
            if (current <= 0f) dead = true;
            float actual = previous - current;
            if (actual > 0f) lastAttacker = attacker;
            if (actual > 0f) Damaged?.Invoke(this, actual, attacker);
            HealthChanged?.Invoke(this);
            if (!dead) return;
            Died?.Invoke(this);
            Debug.Log("Aaris offline combat: combatant defeated.");
        }

        // Used only when a near-player bot record reclaims an inactive
        // pooled character. Does not revive a defeated roster entry.
        internal void ResetForStreamingRival(float hitpoints)
        {
            current = Mathf.Clamp(hitpoints, 1f, maximumHealth);
            helmetDurability = 0f;
            dead = false;
            lastAttacker = null;
            HealthChanged?.Invoke(this);
        }

        public void RestoreHealth(float amount)
        {
            if (dead || amount <= 0f || float.IsNaN(amount) || float.IsInfinity(amount)) return;
            current = Mathf.Min(maximumHealth, current + amount);
            HealthChanged?.Invoke(this);
        }
    }
}
