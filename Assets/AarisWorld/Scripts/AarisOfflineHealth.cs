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
        private float current;
        private bool dead;

        public float CurrentHealth => current;
        public float MaximumHealth => maximumHealth;
        public bool IsDead => dead;
        public event Action<AarisOfflineHealth> Died;
        public event Action<AarisOfflineHealth> HealthChanged;

        private void Awake() => current = maximumHealth;

        public void ApplyDamage(float amount, GameObject attacker)
        {
            if (dead || float.IsNaN(amount) || float.IsInfinity(amount) || amount <= 0f) return;
            float remaining = Mathf.Max(0f, current - amount);
            if (remaining == current) return;
            current = remaining;
            bool lethal = remaining <= 0f;
            // Commit death before callbacks. A HealthChanged listener may
            // synchronously attempt a heal or another damage event.
            // Neither must resurrect an actor nor fire Died twice.
            if (lethal) dead = true;
            HealthChanged?.Invoke(this);
            if (!lethal) return;
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
