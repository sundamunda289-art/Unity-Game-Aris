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
        // Authoritative combat feedback; listeners receive only actual HP loss.
        public event Action<AarisOfflineHealth, float, GameObject> Damaged;

        private void Awake() => current = maximumHealth;

        // Generic explosions, zone damage and body hits do not consume helmets.
        public void ApplyDamage(float amount, GameObject attacker)
        {
            ApplyHitDamage(amount, attacker, false);
        }

        public void ApplyHitDamage(float amount, GameObject attacker, bool headshot)
        {
            if (dead || float.IsNaN(amount) || float.IsInfinity(amount) || amount <= 0f) return;
            float previous = current;
            float absorbed = headshot
                ? Mathf.Min(helmetDurability, amount * helmetProtection) : 0f;
            helmetDurability = Mathf.Max(0f, helmetDurability - absorbed);
            current = Mathf.Max(0f, current - (amount - absorbed));
            // Publish fatality before notifying HUD/AI health observers:
            // a zero-HP notification must already report IsDead = true.
            if (current <= 0f) dead = true;
            HealthChanged?.Invoke(this);
            if (previous > current) Damaged?.Invoke(this, previous - current, attacker);
            if (!dead) return;
            Died?.Invoke(this);
            // Elimination logs should not capture stack traces for up to 100 bots
            // in a production Android match. Keep development diagnostics.
            if (Debug.isDebugBuild)
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
