using System;
using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// Offline health receiver shared by player and future AI bot prefabs.
    /// Existing guns and frag explosions already use IAarisDamageable.
    /// This receiver reports death; a match/bot state machine decides respawn or defeat.
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

        private void Awake() => current = maximumHealth;

        public void ApplyDamage(float amount, GameObject attacker)
        {
            if (dead || float.IsNaN(amount) || float.IsInfinity(amount) || amount <= 0f) return;
            float absorbed = Mathf.Min(helmetDurability, amount * helmetProtection);
            helmetDurability = Mathf.Max(0f, helmetDurability - absorbed);
            current = Mathf.Max(0f, current - (amount - absorbed));
            HealthChanged?.Invoke(this);
            if (current > 0f) return;
            dead = true;
            Died?.Invoke(this);
            Debug.Log("Aaris offline combat: combatant defeated, awaiting match state transition.");
        }

        public void RestoreHealth(float amount)
        {
            if (dead || amount <= 0f || float.IsNaN(amount) || float.IsInfinity(amount)) return;
            current = Mathf.Min(maximumHealth, current + amount);
            HealthChanged?.Invoke(this);
        }
    }
}
