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

        public float CurrentHealth => current;
        public float MaximumHealth => maximumHealth;
        public bool IsDead => dead;
        public float HelmetDurability => helmetDurability;
        // IAarisDamageable has no hit-bone data; armor currently absorbs
        // capped generic damage until headshot-specific hits are available.
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

        public void ApplyDamage(float amount, GameObject attacker)
        {
            if (dead || float.IsNaN(amount) || float.IsInfinity(amount) || amount <= 0f) return;
            float previous = current;
            float absorbed = Mathf.Min(helmetDurability, amount * helmetProtection);
            helmetDurability = Mathf.Max(0f, helmetDurability - absorbed);
            current = Mathf.Max(0f, current - (amount - absorbed));
            // Publish fatality before notifying HUD/AI health observers:
            // a zero-HP notification must already report IsDead = true.
            if (current <= 0f) dead = true;
            float actual = previous - current;
            if (actual > 0f) Damaged?.Invoke(this, actual, attacker);
            HealthChanged?.Invoke(this);
            if (!dead) return;
            Died?.Invoke(this);
            Debug.Log("Aaris offline combat: combatant defeated.");
        }

        public void RestoreHealth(float amount)
        {
            if (dead || amount <= 0f || float.IsNaN(amount) || float.IsInfinity(amount)) return;
            current = Mathf.Min(maximumHealth, current + amount);
            HealthChanged?.Invoke(this);
        }
    }
}
