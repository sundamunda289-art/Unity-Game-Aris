using System;
using System.Collections;
using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// Ninety-four distant combatant records plus the director's six original
    /// full-physics rivals form a 100-opponent offline roster. Only three of the
    /// distant records may borrow a near-player visual/capsule at once.
    /// No per-bot Update, pathfinding instance or per-shot allocation at range.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class AarisMassBotPopulation : MonoBehaviour
    {
        private const int DistantRoster = 94;
        private const int NearProxyBudget = 3;
        private const float MaterializeRangeSqr = 115f * 115f;
        private const float DematerializeRangeSqr = 170f * 170f;
        private sealed class BotRecord
        {
            internal Vector3 position, direction;
            internal float hp, previousTick, nextDecision, nextShot, lastHit, retryMaterializeAt;
            internal int proxySlot = -1, medkits = 1, ammo = 30;
            internal bool alive;
        }

        private readonly BotRecord[] bots = new BotRecord[DistantRoster];
        private readonly AarisOfflineRival[] proxies = new AarisOfflineRival[NearProxyBudget];
        private readonly int[] proxyIds = { -1, -1, -1 };
        private readonly System.Random random = new System.Random(42061);
        private AarisOfflineBRDirector director;
        private Transform player;
        private AarisOfflineHealth playerHealth;
        private int seeded, aliveCount, simulationCursor, validSeeds;
        private float nextStreaming;
        private bool ready;
        internal int AliveCount => aliveCount;
        internal bool HadValidBots => validSeeds > 0;
        internal bool IsReady => ready;

        internal void Initialize(AarisOfflineBRDirector match,
            Transform localPlayer, AarisOfflineHealth localHealth)
        {
            director = match;
            player = localPlayer;
            playerHealth = localHealth;
            StartCoroutine(SeedRoster());
        }

        private IEnumerator SeedRoster()
        {
            // Establish at most three logical spawn records per frame. Terrain
            // sampling does not create GameObjects, navmesh or colliders.
            while (seeded < DistantRoster)
            {
                for (int j = 0; j < 3 && seeded < DistantRoster; j++, seeded++)
                {
                    var record = new BotRecord();
                    if (TrySeedPosition(seeded, out record.position))
                    {
                        record.hp = 200f;
                        record.alive = true;
                        record.previousTick = Time.time;
                        float bearing = (float)(random.NextDouble() * Math.PI * 2d);
                        record.direction = new Vector3(Mathf.Cos(bearing), 0f,
                            Mathf.Sin(bearing));
                        record.nextDecision = Time.time + 2f;
                        record.nextShot = Time.time + 1f;
                        aliveCount++;
                        validSeeds++;
                    }
                    bots[seeded] = record;
                }
                yield return null;
            }
            ready = true;
        }

        private bool TrySeedPosition(int index, out Vector3 position)
        {
            position = Vector3.zero;
            for (int attempt = 0; attempt < 18; attempt++)
            {
                // A shuffled 10x10 lattice distributes opponents across
                // the 2km authored terrain instead of concentrating at spawn.
                int cell = (index * 37 + attempt * 29) % 100;
                float x = -875f + (cell % 10) * 195f +
                    (float)(random.NextDouble() - .5d) * 105f;
                float z = -875f + (cell / 10) * 195f +
                    (float)(random.NextDouble() - .5d) * 105f;
                if (!director.TryGetBotGround(x, z, out float y)) continue;
                Vector3 candidate = new Vector3(x, y + .08f, z);
                if ((candidate - player.position).sqrMagnitude < 55f * 55f)
                    continue;
                position = candidate;
                return true;
            }
            return false;
        }

        private float Random01() => (float)random.NextDouble();

        private void Update()
        {
            if (!ready || director == null || !director.CanEngage || player == null)
                return;
            // Exactly three lightweight states, never the whole roster, get
            // a think step per rendered frame. Near avatars run their native
            // collision and combat in their own staggered existing controller.
            for (int i = 0; i < 3; i++)
            {
                int id = simulationCursor++ % bots.Length;
                BotRecord bot = bots[id];
                if (bot != null && bot.alive && bot.proxySlot < 0)
                    SimulateDistant(id, bot);
            }
            if (Time.time >= nextStreaming)
            {
                nextStreaming = Time.time + .4f;
                StreamOneProxy();
            }
        }

        private void SimulateDistant(int id, BotRecord bot)
        {
            float dt = Mathf.Clamp(Time.time - bot.previousTick, 0f, 1.5f);
            bot.previousTick = Time.time;
            if (dt <= .001f) return;
            if (director.TryZoneRetreatDirection(bot.position, out Vector3 retreat))
                bot.direction = retreat;
            else if (Time.time >= bot.nextDecision)
            {
                bot.nextDecision = Time.time + 3f + Random01() * 3f;
                float angle = (Random01() - .5f) * 105f;
                bot.direction = Quaternion.Euler(0f, angle, 0f) * bot.direction;
                if (bot.direction.sqrMagnitude < .1f) bot.direction = Vector3.forward;
                bot.direction.Normalize();
            }
            Vector3 next = bot.position + bot.direction * (2.2f * dt);
            if (director.TryGetBotGround(next.x, next.z, out float y))
            {
                next.y = y + .08f;
                bot.position = next;
            }
            else bot.direction = Quaternion.Euler(0f, 127f, 0f) * bot.direction;

            // Keep the closing zone authoritative for distant logical bots,
            // without 94 CharacterController/Physics.Update calls.
            if (director.BotOutsideZone(bot.position))
            {
                bot.hp -= 4.5f * dt;
                if (bot.hp <= 0f) { EliminateVirtual(id); return; }
            }

            if (Time.time < bot.nextShot) return;
            bot.nextShot = Time.time + 1.0f + Random01() * .7f;
            int nearest = -1;
            float nearestSqr = 58f * 58f;
            for (int j = 0; j < bots.Length; j++)
            {
                if (j == id) continue;
                BotRecord other = bots[j];
                // Off-screen firefights must never damage a materialized bot
                // through an unloaded wall or bypass its physical sight gate.
                if (other == null || !other.alive || other.proxySlot >= 0) continue;
                Vector3 diff = other.position - bot.position;
                float d = diff.sqrMagnitude;
                if (d >= nearestSqr) continue;
                nearestSqr = d;
                nearest = j;
            }
            if (nearest >= 0 && bot.ammo > 0)
            {
                bot.ammo--;
                BotRecord other = bots[nearest];
                float hit = 5f + Random01() * 9f;
                other.lastHit = Time.time;
                other.hp -= hit;
                if (other.hp <= 0f) EliminateVirtual(nearest);
            }
            else
            {
                // Abstract off-screen scavenging: no fake on-screen ground
                // pickup or Object.Instantiate is triggered at long range.
                if (bot.ammo < 6) bot.ammo = 30;
                if (bot.hp < 85f && bot.medkits > 0 &&
                    Time.time - bot.lastHit > 7f)
                {
                    bot.medkits--;
                    bot.hp = Mathf.Min(200f, bot.hp + 65f);
                }
            }
        }

        private void EliminateVirtual(int index)
        {
            BotRecord bot = bots[index];
            if (bot == null || !bot.alive) return;
            bot.hp = 0f;
            bot.alive = false;
            aliveCount = Mathf.Max(0, aliveCount - 1);
            director.VirtualBotDefeated();
        }

        internal void OnProxyDefeated(AarisOfflineRival rival, int index)
        {
            if (index < 0 || index >= bots.Length) return;
            BotRecord bot = bots[index];
            if (bot == null) return;
            int slot = bot.proxySlot;
            // Retire the native proxy mapping first. Death events can
            // synchronously finish the match and halt Update callbacks.
            if (bot.alive) EliminateVirtual(index);
            if (slot >= 0 && slot < proxyIds.Length && proxies[slot] == rival)
                proxyIds[slot] = -1;
            bot.proxySlot = -1;
        }

        internal AarisOfflineRival PhysicalProxy(int index)
        {
            return index >= 0 && index < proxies.Length && proxyIds[index] >= 0 &&
                proxies[index] != null && proxies[index].IsAlive &&
                proxies[index].gameObject.activeInHierarchy ? proxies[index] : null;
        }

        internal int ProxySlots => proxies.Length;

        private void StreamOneProxy()
        {
            for (int slot = 0; slot < proxies.Length; slot++)
            {
                int id = proxyIds[slot];
                if (id < 0) continue;
                var proxy = proxies[slot];
                if (proxy == null || !proxy.IsAlive)
                {
                    proxyIds[slot] = -1;
                    continue;
                }
                float distanceSqr =
                    (proxy.transform.position - player.position).sqrMagnitude;
                if (distanceSqr < DematerializeRangeSqr) continue;
                BotRecord record = bots[id];
                record.position = proxy.transform.position;
                record.hp = proxy.CurrentHP;
                record.ammo = proxy.CurrentAmmo;
                record.medkits = proxy.RemainingMedkits;
                record.previousTick = Time.time;
                record.proxySlot = -1;
                proxy.SuspendForStreaming();
                proxyIds[slot] = -1;
                return; // At most one retirement/activation each interval.
            }

            int free = -1;
            for (int i = 0; i < proxyIds.Length; i++)
                if (proxyIds[i] < 0) { free = i; break; }
            if (free < 0) return;
            int closest = -1;
            float best = MaterializeRangeSqr;
            for (int i = 0; i < bots.Length; i++)
            {
                BotRecord candidate = bots[i];
                if (candidate == null || !candidate.alive || candidate.proxySlot >= 0 ||
                    Time.time < candidate.retryMaterializeAt)
                    continue;
                float distance = (candidate.position - player.position).sqrMagnitude;
                if (distance >= best) continue;
                best = distance;
                closest = i;
            }
            if (closest < 0) return;
            BotRecord bot = bots[closest];
            // Serialized proxy activation is physical world creation. Check
            // inactive buildings and active capsule collision once here, not
            // every virtual AI step. An obstructed record remains logical.
            if (!director.CanMaterializeBot(bot.position))
            {
                bot.retryMaterializeAt = Time.time + 2.5f;
                return;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            if (proxies[free] == null &&
                !AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())
                return;
#endif
            if (proxies[free] == null)
            {
                var actor = new GameObject("Aaris streamed BR rival " + free);
                // Build the Humanoid and its bounds at the REAL ground point,
                // not the world origin. Avoid a transient physics actor at 0.
                actor.transform.position = bot.position;
                var capsule = actor.AddComponent<CharacterController>();
                capsule.height = 1.8f;
                capsule.radius = .31f;
                capsule.center = new Vector3(0f, .9f, 0f);
                capsule.skinWidth = .05f;
                capsule.stepOffset = .28f;
                capsule.slopeLimit = 43f;
                var health = actor.AddComponent<AarisOfflineHealth>();
                var rival = actor.AddComponent<AarisOfflineRival>();
                rival.Initialize(director, health, player, playerHealth,
                    6 + free);
                proxies[free] = rival;
            }
            bot.proxySlot = free;
            proxyIds[free] = closest;
            proxies[free].ResumeStreamed(this, closest, bot.position, bot.hp,
                bot.ammo, bot.medkits);
        }

        private void OnDestroy()
        {
            for (int i = 0; i < proxies.Length; i++)
                if (proxies[i] != null) Destroy(proxies[i].gameObject);
        }
    }
}
