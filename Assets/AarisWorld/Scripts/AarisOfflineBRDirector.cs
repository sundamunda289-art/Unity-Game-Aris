using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Unity.Profiling;
#endif
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Aaris.MapReview
{
    /// <summary>
    /// Offline-only match authority: a bounded set of local rivals, a nested
    /// closing zone, actual elimination counting and a final survivor result.
    /// No network, NavMesh bake, asset download or scene authoring required.
    /// </summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(280)]
    public sealed class AarisOfflineBRDirector : MonoBehaviour
    {
        private const int RivalCount = 100;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // CPU attribution for the real Android Development Build. In a release
        // player these markers and scopes are stripped by the preprocessor.
        private static readonly ProfilerMarker k_DormantScan =
            new ProfilerMarker("Aaris.AI.DormantScheduler");
        private static readonly ProfilerMarker k_SpatialHunt =
            new ProfilerMarker("Aaris.AI.SpatialOpponentLookup");
        private static readonly ProfilerMarker k_PlayerThreat =
            new ProfilerMarker("Aaris.AI.PlayerAttackerSelection");
        private static readonly ProfilerMarker k_AimSelection =
            new ProfilerMarker("Aaris.AI.AimTargetCandidates");
#endif
        private const float OceanCutoff = -37.35f;
        private static readonly float[] ZoneRadii = { 1080f, 660f, 360f, 175f, 65f };
        private static readonly float[] CloseStarts = { 45f, 165f, 285f, 400f };
        private static readonly float[] CloseEnds = { 140f, 245f, 360f, 465f };
        private readonly Vector2[] centers = new Vector2[5];
        private readonly AarisOfflineRival[] rivals = new AarisOfflineRival[RivalCount];
        private static AarisOfflineBRDirector activeDirector;
        private AarisDeathCrateWorld deathCrates;
        private AarisRivalGunfireVfx rivalGunfire;
        // Opt-in 1Hz phone diagnostics only. No extra work in normal play.
        internal struct RivalPerfSample
        {
            internal int spawned, alive, dormant, virtualBodies;
            internal int detailedRigs, farMeshes, physicalReceivers, attackers;
        }

        internal static bool TrySampleRivalPerf(out RivalPerfSample sample)
        {
            sample = default;
            AarisOfflineBRDirector director = activeDirector;
            if (director == null || !director.started) return false;
            sample.spawned = director.spawnedRivals;
            for (int i = 0; i < RivalCount; i++)
            {
                AarisOfflineRival rival = director.rivals[i];
                if (rival == null || !rival.IsAlive) continue;
                sample.alive++;
                rival.AccumulatePerfSample(ref sample);
            }
            if (director.attackerOne != null && director.attackerOne.IsAlive)
                sample.attackers++;
            if (director.attackerTwo != null &&
                director.attackerTwo != director.attackerOne &&
                director.attackerTwo.IsAlive)
                sample.attackers++;
            return true;
        }

        private static RaycastHit[] assistHits = new RaycastHit[32];
        private static RaycastHit[] muzzleAssistHits = new RaycastHit[48];
        // Preallocated main-thread Red-Lock shortlist: never scan PhysX for all 100 actors.
        private const int AimBudget = 3;
        // Cache follows all authored physical and virtual rival moves.
        // 10m slack covers camera/aim-height offset and far-tick movement.
        private const float AimSpatialSlack = 10f;
        private static readonly AarisOfflineRival[] aimBots = new AarisOfflineRival[AimBudget];
        private static readonly Vector3[] aimDirs = new Vector3[AimBudget];
        private static readonly float[] aimRanges = new float[AimBudget];
        private static readonly float[] aimDots = new float[AimBudget];
        private AarisOfflineRival attackerOne, attackerTwo;
        private AarisOfflineRival scanAttackerOne, scanAttackerTwo;
        private const int AttackerSelectionPerFrame = 12;
        private int attackerScanCursor = -1, attackerScanFrame = -1;
        private float attackerScanFirst, attackerScanSecond;
        private float nextAttackerSelection;
        // Successfully populated slots stay claimed even if their rival
        // loses later: retry waves must never resurrect defeated opponents.
        private readonly bool[] populatedSlots = new bool[RivalCount];
        // One central queue replaces per-frame native Update dispatch for
        // far/unseen agents, without changing their AI combat deadlines.
        private readonly bool[] dormantRivalSlots = new bool[RivalCount];
        private int dormantRivalCount;
        // Keep the 100-slot scheduler asleep between the earliest actual
        // combat, camera or proximity deadline. No extra timers or allocations.
        private float nextDormantSweepAt = float.PositiveInfinity;
        private Vector3 lastDirectorPlayerPosition;
        private bool hasDirectorPlayerPosition;

        // A vehicle warp/teleport may cross a virtual actor's whole LOD band.
        // Wake only candidates near the new player location, not all 100 bots.
        private void WakeDormantRivalsNearPlayer(Vector3 position, float now)
        {
            bool woken = false;
            for (int i = 0; i < RivalCount; i++)
            {
                if (!dormantRivalSlots[i] || !rivalSpatialRegistered[i] ||
                    (rivalSpatialPositions[i] - position).sqrMagnitude >=
                        530f * 530f) continue;
                AarisOfflineRival bot = rivals[i];
                if (bot == null || !bot.IsAlive) continue;
                bot.ForceDormantProximityProbe();
                woken = true;
            }
            if (woken) nextDormantSweepAt = Mathf.Min(nextDormantSweepAt, now);
        }

        internal void RegisterDormantRival(int slot, AarisOfflineRival bot, bool dormant)
        {
            if (slot < 0 || slot >= RivalCount || rivals[slot] != bot) return;
            if (dormantRivalSlots[slot] == dormant) return;
            dormantRivalSlots[slot] = dormant;
            dormantRivalCount += dormant ? 1 : -1;
            if (dormant)
                nextDormantSweepAt = Mathf.Min(nextDormantSweepAt,
                    bot.NextDormantWakeTime);
            else if (dormantRivalCount == 0)
                nextDormantSweepAt = float.PositiveInfinity;
        }

        public void TickDormantRivals(float now)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using var aiProfilerScope = k_DormantScan.Auto();
#endif
            if (dormantRivalCount == 0 || now < nextDormantSweepAt) return;
            // All existing AI deadlines retain priority. Recompute only after
            // some rival has become due; dormant registration can lower this
            // bound immediately, including after a player/camera transition.
            nextDormantSweepAt = float.PositiveInfinity;
            for (int i = 0; i < RivalCount; i++)
            {
                if (!dormantRivalSlots[i]) continue;
                AarisOfflineRival bot = rivals[i];
                // A sleeping slot's deadline is managed state. Avoid Unity's
                // native object-liveness checks (including Health) until due.
                // OnDied/OnDestroy unregister normally; a missed teardown is
                // still cleaned at the existing wake deadline.
                if (ReferenceEquals(bot, null))
                {
                    dormantRivalSlots[i] = false;
                    dormantRivalCount--;
                    continue;
                }
                if (now >= bot.NextDormantWakeTime)
                {
                    if (bot == null || !bot.IsAlive)
                    {
                        dormantRivalSlots[i] = false;
                        dormantRivalCount--;
                        continue;
                    }
                    bot.TickDormantFromDirector();
                }
                if (dormantRivalSlots[i])
                    nextDormantSweepAt = Mathf.Min(nextDormantSweepAt,
                        bot.NextDormantWakeTime);
            }
        }
        private const int MaximumSpawnPasses = 4;
        private int spawnPasses = 1;
        private readonly Collider[] spawnHits = new Collider[24];
        // Only used when dense authored cover saturates the normal overlap
        // query; an exhaustive allocating fallback remains for >96 hits.
        private readonly Collider[] crowdedSpawnHits = new Collider[96];
        private readonly System.Random random = new System.Random();
        private Transform player;
        private AarisThirdPersonPlayer playerMotor;
        private Camera cachedRivalCamera;
        private float nextRivalCameraLookup;
        private AarisOfflineHealth playerHealth;
        private AarisOfflineDrop drop;
        // Flight component may be attached after this director starts. Limit
        // missing-component native lookups during the cold-start render path.
        private float nextDropComponentRefresh;
        private AarisOfflineMatchEnd matchEnd;
        private AarisNearbyColliders nearbyColliders;
        private AarisWeaponLootWorld rivalLootWorld;
        private float nextLootWorldLookup;
        private Terrain[] terrains;
        // Map tiles are static within a match. Cache their native metadata.
        private struct TerrainProbeMeta
        {
            internal Terrain tile;
            internal TerrainData data;
            internal Vector3 origin, size;
        }
        private TerrainProbeMeta[] terrainProbeMeta;
        private int attemptedSlots, activeRivals, spawnedRivals;
        private float startedAt, nextSpawn, nextZoneTick, nextDamageTick, nextRingDraw;
        private float radius;
        private Vector2 center;
        private bool started, spawnComplete;
        private Canvas statusCanvas;
        private Text status;
        private Text recentKillFeed;
        private float killFeedClearAt;
        private LineRenderer boundary;
        private readonly Vector3[] boundaryPoints = new Vector3[65];
        private LineRenderer drawnBoundary;
        private Vector2 drawnBoundaryCenter;
        private float drawnBoundaryRadius;
        private Material boundaryMaterial;
        // One bounded resource lookup per match, not per opponent.
        private bool rivalAssetsResolved;
        private GameObject rivalPrefab;
        private Avatar rivalAvatar;
        private AnimationClip rivalIdle, rivalRun;
        private readonly Dictionary<Material, Material> rivalMaterialCache =
            new Dictionary<Material, Material>();
        // Begin visual loading away from the first terrain frames. Only one
        // ResourceRequest is active; no models, materials, LODs or tree counts change.
        private static readonly string[] RivalVisualResourcePaths =
        {
            "AarisCharacters/Primary/RealisticSoldier/RealisticSoldier",
            "AarisCharacters/Primary/Player",
            "AarisCharacters/Kenney/characterMedium"
        };
        private GameObject prewarmedRivalPrefab;
        private string prewarmedRivalPath;
        private bool rivalVisualPrewarmComplete;
        // Android imports only the candidate clip subassets that can be read
        // asynchronously. If a bundled FBX exposes clips only via LoadAll,
        // ResolveRivalAssets retains the exact original fallback behavior.
        private AnimationClip prewarmedRivalIdle, prewarmedRivalRun;

        private IEnumerator PrewarmRivalVisuals()
        {
            // Defer CPU/asset integration until the initial map is visible.
            yield return new WaitForSecondsRealtime(2f);
            for (int i = 0; i < RivalVisualResourcePaths.Length; i++)
            {
                ResourceRequest request;
                while ((request = AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<GameObject>(
                           RivalVisualResourcePaths[i])) == null)
                    yield return null;
                yield return request;
                GameObject candidate = request.asset as GameObject;
#if UNITY_ANDROID && !UNITY_EDITOR
                // Async completion is NOT the same as a free native frame.
                // Renderer hierarchy inspection can still coincide with a
                // car, settlement or pickup build. Do not throw away a
                // successfully loaded rival while that slot is occupied.
                if (candidate != null)
                    while (!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())
                        yield return null;
#endif
                if (HasRenderableRivalPrefab(candidate))
                {
                    prewarmedRivalPrefab = candidate;
                    prewarmedRivalPath = RivalVisualResourcePaths[i];
                    break;
                }
                // Failed fallback attempts also yield a frame.
                yield return null;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            // Keep animation imports off the first rival's spawn frame.
            // The two requests are sequential and low-priority; neither
            // instantiates an Animator nor replaces the original clip fallback.
            const string idlePath = "AarisCharacters/Primary/Animations/idle";
            const string runPath = "AarisCharacters/Primary/Animations/run";
            ResourceRequest idleRequest;
            while ((idleRequest = AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<AnimationClip>(idlePath)) == null)
                yield return null;
            yield return idleRequest;
            var idle = idleRequest.asset as AnimationClip;
            if (IsUsableRivalClip(idle)) prewarmedRivalIdle = idle;
            yield return null;
            ResourceRequest runRequest;
            while ((runRequest = AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<AnimationClip>(runPath)) == null)
                yield return null;
            yield return runRequest;
            var run = runRequest.asset as AnimationClip;
            if (IsUsableRivalClip(run)) prewarmedRivalRun = run;
            yield return null;
#endif
            rivalVisualPrewarmComplete = true;
        }

        private static bool IsUsableRivalClip(AnimationClip clip)
        {
            return clip != null && !clip.legacy && clip.humanMotion && clip.length > .02f;
        }

        internal void ResolveRivalAssets(out GameObject prefab, out Avatar avatar,
            out AnimationClip idle, out AnimationClip run)
        {
            if (!rivalAssetsResolved)
            {
                rivalAssetsResolved = true;
                const string primary = "AarisCharacters/Primary/RealisticSoldier/RealisticSoldier";
                string path = prewarmedRivalPrefab != null
                    ? prewarmedRivalPath : primary;
                rivalPrefab = prewarmedRivalPrefab != null
                    ? prewarmedRivalPrefab : Resources.Load<GameObject>(path);
                if (!HasRenderableRivalPrefab(rivalPrefab))
                {
                    path = "AarisCharacters/Primary/Player";
                    rivalPrefab = Resources.Load<GameObject>(path);
                }
                if (!HasRenderableRivalPrefab(rivalPrefab))
                {
                    path = "AarisCharacters/Kenney/characterMedium";
                    rivalPrefab = Resources.Load<GameObject>(path);
                }
                if (!HasRenderableRivalPrefab(rivalPrefab))
                    rivalPrefab = null;
                if (rivalPrefab != null)
                {
                    // The original prefab often already references its valid
                    // humanoid Avatar. Reuse it rather than synchronously
                    // enumerating imported Avatar subassets on first spawn.
                    Animator prefabAnimator =
                        rivalPrefab.GetComponentInChildren<Animator>(true);
                    Avatar attachedAvatar = prefabAnimator != null
                        ? prefabAnimator.avatar : null;
                    if (attachedAvatar != null && attachedAvatar.isValid &&
                        attachedAvatar.isHuman)
                        rivalAvatar = attachedAvatar;
                    if (rivalAvatar == null)
                    {
                        // Older FBX imports can have a detached Avatar asset:
                        // retain the same verified fallback as before.
                        foreach (Avatar available in Resources.LoadAll<Avatar>(path))
                            if (available != null && available.isValid && available.isHuman)
                            { rivalAvatar = available; break; }
                    }
                }
                rivalIdle = IsUsableRivalClip(prewarmedRivalIdle)
                    ? prewarmedRivalIdle
                    : LoadRivalClip("AarisCharacters/Primary/Animations/idle");
                rivalRun = IsUsableRivalClip(prewarmedRivalRun)
                    ? prewarmedRivalRun
                    : LoadRivalClip("AarisCharacters/Primary/Animations/run");
                if (rivalIdle == null) rivalIdle = LoadRivalClip("AarisCharacters/Kenney/idle");
                if (rivalRun == null) rivalRun = LoadRivalClip("AarisCharacters/Kenney/run");
                if (rivalPrefab == null)
                    Debug.LogWarning("Aaris BR: no bundled humanoid model was found.");
            }
            prefab = rivalPrefab;
            avatar = rivalAvatar;
            idle = rivalIdle;
            run = rivalRun;
        }

        // Prefab assets are not necessarily activeInHierarchy before spawning.
        // Check authored activeSelf ancestry and mesh content only on setup.
        internal static bool HasRenderableRivalPrefab(GameObject source)
        {
            if (source == null || !source.activeSelf) return false;
            foreach (Renderer renderer in source.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || !renderer.enabled ||
                    renderer.forceRenderingOff) continue;
                bool active = true;
                Transform node = renderer.transform;
                while (node != null)
                {
                    if (!node.gameObject.activeSelf) { active = false; break; }
                    if (node == source.transform) break;
                    node = node.parent;
                }
                if (!active) continue;
                if (renderer is SkinnedMeshRenderer skinned &&
                    skinned.sharedMesh != null &&
                    skinned.sharedMesh.vertexCount > 0) return true;
                if (renderer is MeshRenderer meshRenderer)
                {
                    MeshFilter filter = meshRenderer.GetComponent<MeshFilter>();
                    if (filter != null && filter.sharedMesh != null &&
                        filter.sharedMesh.vertexCount > 0) return true;
                }
            }
            return false;
        }

        private static AnimationClip LoadRivalClip(string path)
        {
            foreach (AnimationClip clip in Resources.LoadAll<AnimationClip>(path))
                if (IsUsableRivalClip(clip))
                    return clip;
            return null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            var actor = FindFirstObjectByType<AarisThirdPersonPlayer>();
            if (actor == null || FindFirstObjectByType<AarisOfflineBRDirector>() != null) return;
            var root = new GameObject("Aaris Offline Battle Royale Director");
            var director = root.AddComponent<AarisOfflineBRDirector>();
            director.player = actor.transform;
        }

        private void Awake() { activeDirector = this; }

        private void Start()
        {
            if (player == null) { enabled = false; return; }
            playerMotor = player.GetComponent<AarisThirdPersonPlayer>();
            playerHealth = player.GetComponent<AarisOfflineHealth>();
            if (playerHealth == null)
                playerHealth = player.gameObject.AddComponent<AarisOfflineHealth>();
            matchEnd = player.GetComponent<AarisOfflineMatchEnd>();
            if (matchEnd == null)
                matchEnd = player.gameObject.AddComponent<AarisOfflineMatchEnd>();
            drop = player.GetComponent<AarisOfflineDrop>();
            terrains = Terrain.activeTerrains;
            CacheTerrainProbeMetadata();
            nearbyColliders = FindFirstObjectByType<AarisNearbyColliders>();
            deathCrates = GetComponent<AarisDeathCrateWorld>();
            if (deathCrates == null) deathCrates = gameObject.AddComponent<AarisDeathCrateWorld>();
            deathCrates.Bind(player);
            rivalGunfire = GetComponent<AarisRivalGunfireVfx>();
            if (rivalGunfire == null) rivalGunfire = gameObject.AddComponent<AarisRivalGunfireVfx>();
            rivalGunfire.Bind(player);
            StartCoroutine(PrewarmRivalVisuals());
        }

        private void Update()
        {
            if (Time.timeScale <= 0f || player == null || playerHealth == null || playerHealth.IsDead ||
                matchEnd == null || matchEnd.IsFinished) return;
            if (!started)
            {
                // Runtime boot order is unspecified. Do not start an unseen
                // ground battle while the real player is still parachuting.
                if (drop == null && Time.unscaledTime >= nextDropComponentRefresh)
                {
                    nextDropComponentRefresh = Time.unscaledTime + 1f;
                    drop = player.GetComponent<AarisOfflineDrop>();
                }
                if (drop != null && !drop.HasLanded) return;
                // Don't start the match clock while the first character
                // resource is integrating into Unity on constrained phones.
                if (!rivalVisualPrewarmComplete) return;
                Begin();
            }

            float elapsed = Time.time - startedAt;
            float now = Time.time;
            Vector3 playerPosition = player.position;
            if (hasDirectorPlayerPosition &&
                (playerPosition - lastDirectorPlayerPosition).sqrMagnitude >
                    25f * 25f)
                WakeDormantRivalsNearPlayer(playerPosition, now);
            lastDirectorPlayerPosition = playerPosition;
            hasDirectorPlayerPosition = true;
            // Combat cadence is independent from presentation wake-ups.
            TickDormantRivals(now);
            // Drive staged player-attacker selection once per rendered frame.
            // Do not rely on individual bots' staggered 0.35s think ticks.
            RefreshPlayerAttackers();
            if (!spawnComplete && Time.time >= nextSpawn &&
                CanAttemptRivalSpawn())
            {
                // Exactly one authored encounter attempt per half-second.
                // Avoid a burst of costly Humanoid imports on 2GB phones.
                int slot = attemptedSlots++;
                if (!populatedSlots[slot]) TrySpawnSlot(slot);
                nextSpawn = Time.time + 0.17f;
                if (attemptedSlots >= RivalCount)
                {
                    if (spawnedRivals < RivalCount &&
                        spawnPasses < MaximumSpawnPasses)
                    {
                        // Retry each still-empty slot from the player's CURRENT
                        // position, with a cooldown between waves. A dense
                        // landing settlement must not produce a one-rival BR.
                        // When every site fails, never declare fake victory.
                        attemptedSlots = 0;
                        spawnPasses++;
                        nextSpawn = Time.time + 2.5f;
                    }
                    else
                    {
                        spawnComplete = true;
                        // A blocked/missing spawn map must not loop forever or
                        // report a fake solo victory. Offer a clean restart.
                        if (spawnedRivals == 0)
                            matchEnd.FailMatchSetup();
                        else if (activeRivals == 0)
                            matchEnd.WinMatch();
                    }
                }
            }

            if (Time.time >= nextZoneTick)
            {
                nextZoneTick = Time.time + 0.5f;
                EvaluateZone(elapsed);
                RefreshStatus(elapsed);
                if (recentKillFeed != null && recentKillFeed.gameObject.activeSelf &&
                    Time.time >= killFeedClearAt)
                    recentKillFeed.gameObject.SetActive(false);
            }
            if (Time.time >= nextDamageTick)
            {
                nextDamageTick = Time.time + 2f;
                ApplyZoneDamage();
            }
            if (boundary != null && Time.time >= nextRingDraw)
            {
                nextRingDraw = Time.time + 2f;
                DrawBoundary();
            }
        }

        // A rival's first Humanoid Instantiate, collider and PlayableGraph
        // setup are optional work after the player has landed. Do not start
        // them in the same frame as vehicle/loot/settlement native integration.
        // Budget rejection must NOT consume a spawn slot or retry pass: all
        // original rivals and their authored spawn checks remain eligible.
        private bool CanAttemptRivalSpawn()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Retry-only waves also visit already-populated slots. Those
            // require no native build and must not monopolize the shared gate.
            if (attemptedSlots < RivalCount && !populatedSlots[attemptedSlots] &&
                !AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())
            {
                nextSpawn = Time.time + 0.12f;
                return false;
            }
#endif
            return true;
        }

        private void Begin()
        {
            started = true;
            startedAt = Time.time;
            nextSpawn = Time.time + 0.6f;
            terrains = Terrain.activeTerrains;
            CacheTerrainProbeMetadata();
            Vector2 landing = new Vector2(player.position.x, player.position.z);
            centers[0] = landing;
            for (int i = 1; i < centers.Length; i++)
            {
                double angle = random.NextDouble() * Math.PI * 2.0;
                float shift = (ZoneRadii[i - 1] - ZoneRadii[i]) * 0.54f;
                Vector2 direction = new Vector2((float)Math.Cos(angle),
                    (float)Math.Sin(angle));
                // A final circle in the sea or on a steep hillside is
                // unwinnable. Backtrack the proposed shift toward the known
                // playable previous center, without ever breaking nested
                // safe-zone geometry. At most eight terrain probes per phase.
                centers[i] = centers[i - 1];
                for (int probe = 0; probe < 8; probe++)
                {
                    float fraction = 1f - probe * 0.125f;
                    Vector2 proposed = centers[i - 1] + direction * (shift * fraction);
                    proposed.x = Mathf.Clamp(proposed.x, -930f, 930f);
                    proposed.y = Mathf.Clamp(proposed.y, -930f, 930f);
                    if (!TryGetGround(proposed.x, proposed.y, out float ground))
                        continue;
                    centers[i] = proposed;
                    break;
                }
            }
            center = centers[0];
            radius = ZoneRadii[0];
            CreateStatus();
            CreateBoundary();
            RefreshStatus(0f);
        }

        private void TrySpawnSlot(int index)
        {
            if (index < 0 || index >= RivalCount || populatedSlots[index]) return;
            // Spread actors over the full 2 km terrain; no cluster by spawn.
            for (int attempt = 0; attempt < 9; attempt++)
            {
                float x = -910f + (float)random.NextDouble() * 1820f;
                float z = -910f + (float)random.NextDouble() * 1820f;
                float dx = x - player.position.x, dz = z - player.position.z;
                if (dx * dx + dz * dz < 45f * 45f) continue;
                if (!TryGetGround(x, z, out float ground)) continue;
                Vector3 point = new Vector3(x, ground + 0.08f, z);
                if (!SpawnClear(point)) continue;

                var actor = new GameObject("Aaris Offline Rival " + (index + 1));
                actor.transform.SetPositionAndRotation(point,
                    Quaternion.Euler(0f, (float)random.NextDouble() * 360f, 0f));
                // Far-away logical opponents need no native capsule at spawn.
                // Initialize materializes it only for actual nearby combat.
                var health = actor.AddComponent<AarisOfflineHealth>();
                var rival = actor.AddComponent<AarisOfflineRival>();
                rival.Initialize(this, health, player, playerHealth, index);
                rivals[index] = rival;
                TrackRivalPosition(index, rival, point);
                populatedSlots[index] = true;
                activeRivals++;
                spawnedRivals++;
                return;
            }
        }

        private void CacheTerrainProbeMetadata()
        {
            if (terrains == null)
            {
                terrainProbeMeta = null;
                return;
            }
            terrainProbeMeta = new TerrainProbeMeta[terrains.Length];
            for (int i = 0; i < terrains.Length; i++)
            {
                Terrain tile = terrains[i];
                if (tile == null) continue;
                TerrainData data = tile.terrainData;
                if (data == null) continue;
                terrainProbeMeta[i] = new TerrainProbeMeta
                {
                    tile = tile, data = data,
                    origin = tile.transform.position, size = data.size
                };
            }
        }

        private bool TryGetGround(float x, float z, out float height, float margin = 2f)
        {
            height = 0f;
            if (terrainProbeMeta == null || terrainProbeMeta.Length == 0)
                return false;
            for (int i = 0; i < terrainProbeMeta.Length; i++)
            {
                TerrainProbeMeta meta = terrainProbeMeta[i];
                Terrain tile = meta.tile;
                TerrainData data = meta.data;
                if (tile == null || data == null) continue;
                Vector3 origin = meta.origin, size = meta.size;
                if (x < origin.x + margin || x > origin.x + size.x - margin ||
                    z < origin.z + margin || z > origin.z + size.z - margin) continue;
                float u = (x - origin.x) / size.x;
                float v = (z - origin.z) / size.z;
                // Slope, ocean and native height sampling stay authoritative.
                if (data.GetInterpolatedNormal(u, v).y < 0.78f) continue;
                height = origin.y + tile.SampleHeight(new Vector3(x, 0f, z));
                if (height > OceanCutoff) return true;
            }
            return false;
        }

        private bool SpawnClear(Vector3 point)
        {
            Vector3 low = point + Vector3.up * 0.36f;
            Vector3 high = point + Vector3.up * 1.50f;
            if (nearbyColliders != null &&
                nearbyColliders.HasPotentialInactiveObstacle(
                    point + Vector3.up * 0.90f,
                    new Vector3(0.38f, 0.90f, 0.38f), Quaternion.identity))
                return false;
            int count = Physics.OverlapCapsuleNonAlloc(low, high, 0.34f,
                spawnHits, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            Collider[] hits = spawnHits;
            if (count >= spawnHits.Length)
            {
                // NonAlloc returns an unordered, potentially incomplete set
                // at capacity. A larger reusable query avoids allocating in
                // ordinary crowded settlements but never ignores blockers.
                hits = crowdedSpawnHits;
                count = Physics.OverlapCapsuleNonAlloc(low, high, 0.34f,
                    crowdedSpawnHits, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);
                if (count >= crowdedSpawnHits.Length)
                {
                    hits = Physics.OverlapCapsule(low, high, 0.34f,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    count = hits.Length;
                }
            }
            for (int i = 0; i < count; i++)
                if (hits[i] != null && !(hits[i] is TerrainCollider)) return false;
            return true;
        }

        // Far-away house/bridge colliders are deliberately unloaded for
        // Android. CharacterController.Move cannot hit disabled geometry,
        // so check the prebuilt no-allocation obstacle grid before each
        // rival displacement; NEVER activate thousands of distant colliders.
        // Scoped targets are physically present only when the real camera
        // can see them. Callers probe in staggered intervals, without GC.
        internal bool IsDistantRivalOnScreen(Vector3 position)
        {
            // Refresh even when an old camera stays enabled during scene
            // handover. One director-wide lookup, never 100 per-bot lookups.
            if (Time.unscaledTime >= nextRivalCameraLookup)
            {
                nextRivalCameraLookup = Time.unscaledTime + 0.5f;
                if (playerMotor != null)
                    cachedRivalCamera = playerMotor.AimCamera;
                else
                {
                    Camera liveMain = Camera.main;
                    if (liveMain != null && liveMain.isActiveAndEnabled)
                        cachedRivalCamera = liveMain;
                    else if (cachedRivalCamera == null || !cachedRivalCamera.isActiveAndEnabled)
                        cachedRivalCamera = player != null
                            ? player.GetComponentInChildren<Camera>(true) : null;
                }
            }
            // A missing or disabled gameplay camera is NOT evidence that a remote
            // fighter is visible. Avoid admitting distant meshes, animation and
            // PhysX receivers during Android startup/camera handover. Close
            // encounters below 55m still use the independent near-distance gate.
            if (cachedRivalCamera == null || !cachedRivalCamera.isActiveAndEnabled) return false;
            Vector3 v = cachedRivalCamera.WorldToViewportPoint(position + Vector3.up * 0.95f);
            // The viewport transform reports points beyond the camera far clip
            // as inside its 2D rectangle. Those rivals cannot actually render,
            // so never allocate their distant mesh or a PhysX hit receiver.
            // Keep a 2m depth margin for head/weapon extents at the clip edge.
            return v.z > 0f && v.z <= cachedRivalCamera.farClipPlane + 2f &&
                v.x >= -0.07f && v.x <= 1.07f &&
                v.y >= -0.1f && v.y <= 1.1f;
        }

        internal bool TryVirtualGround(float x, float z, out float height)
        {
            // The spawn margin must not become a four-metre gap between tiles.
            return TryGetGround(x, z, out height, margin: 0f);
        }

        internal bool WouldCrossInactiveCover(Vector3 from, Vector3 displacement)
        {
            if (nearbyColliders == null || displacement.sqrMagnitude < 0.000001f)
                return false;
            // Vector3.normalized and Vector3.magnitude each compute a
            // square root. Reuse one magnitude while retaining the exact
            // Normalize epsilon and 0.72m look-ahead policy.
            float distance = displacement.magnitude;
            Vector3 heading = distance > 0.00001f
                ? displacement / distance : Vector3.zero;
            Vector3 lookAhead = heading * Mathf.Max(distance, 0.72f);
            Vector3 predicted = from + lookAhead + Vector3.up * 0.9f;
            return nearbyColliders.HasPotentialInactiveObstacle(predicted,
                new Vector3(0.36f, 0.90f, 0.36f), Quaternion.identity);
        }

        private void EvaluateZone(float elapsed)
        {
            center = centers[centers.Length - 1];
            radius = ZoneRadii[ZoneRadii.Length - 1];
            for (int stage = 0; stage < CloseStarts.Length; stage++)
            {
                if (elapsed < CloseStarts[stage])
                {
                    center = centers[stage];
                    radius = ZoneRadii[stage];
                    return;
                }
                if (elapsed <= CloseEnds[stage])
                {
                    float blend = Mathf.Clamp01((elapsed - CloseStarts[stage]) /
                        (CloseEnds[stage] - CloseStarts[stage]));
                    center = Vector2.Lerp(centers[stage], centers[stage + 1], blend);
                    radius = Mathf.Lerp(ZoneRadii[stage], ZoneRadii[stage + 1], blend);
                    return;
                }
            }
        }

        // During the closing phases rivals should actually migrate into
        // the play area instead of running after an already unsafe player
        // indefinitely. This is a direction only: local collision avoidance
        // and terrain-constrained movement still decide each step.
        internal bool TryZoneRetreatDirection(Vector3 position, out Vector3 direction)
        {
            Vector2 inward = center - new Vector2(position.x, position.z);
            float safe = Mathf.Max(6f, radius - 12f);
            if (inward.sqrMagnitude <= safe * safe)
            {
                direction = Vector3.zero;
                return false;
            }
            direction = new Vector3(inward.x, 0f, inward.y).normalized;
            return true;
        }

        private bool Outside(Vector3 position)
        {
            float dx = position.x - center.x, dz = position.z - center.y;
            float safe = Mathf.Max(1f, radius - 2f);
            return dx * dx + dz * dz > safe * safe;
        }

        private void ApplyZoneDamage()
        {
            if (!spawnComplete || playerHealth == null || playerHealth.IsDead) return;
            if (Outside(player.position))
                playerHealth.ApplyDamage(7f, null);
            for (int i = 0; i < rivals.Length; i++)
                if (rivals[i] != null && rivals[i].IsAlive &&
                    Outside(rivals[i].transform.position))
                    rivals[i].TakeZoneDamage(9f);
        }

        // This notification happens only after actual health reaches zero;
        // an abstract remote kill updates the same real Alive tally and HUD.
        internal void ReportRivalElimination(GameObject killer, GameObject victim)
        {
            if (statusCanvas == null || status == null || victim == null) return;
            if (recentKillFeed == null)
            {
                var node = new GameObject("Recent elimination",
                    typeof(RectTransform), typeof(Text));
                node.transform.SetParent(statusCanvas.transform, false);
                RectTransform rect = node.GetComponent<RectTransform>();
                rect.anchorMin = rect.anchorMax = new Vector2(.97f, .84f);
                rect.pivot = new Vector2(1f, .5f);
                rect.sizeDelta = new Vector2(520f, 42f);
                recentKillFeed = node.GetComponent<Text>();
                recentKillFeed.font = status.font;
                recentKillFeed.fontSize = 17;
                recentKillFeed.alignment = TextAnchor.MiddleRight;
                recentKillFeed.color = Color.white;
                recentKillFeed.raycastTarget = false;
            }
            recentKillFeed.text = (killer != null ? killer.name : "ZONE") +
                "  >  " + victim.name;
            killFeedClearAt = Time.time + 5.5f;
            recentKillFeed.gameObject.SetActive(true);
        }

        internal void ShowRivalGunfire(Transform shooter, Vector3 impact,
            AarisWeaponSpec gun)
        {
            if (rivalGunfire != null && gun != null)
                rivalGunfire.EmitForGun(shooter, impact, gun);
        }

        internal void DropRivalLoot(Vector3 position, AarisWeaponSpec gun,
            int magazine, int reserve, int medkits, uint seed)
        {
            if (deathCrates != null)
                deathCrates.Spawn(position, gun, magazine, reserve, medkits, seed);
        }

        internal void RivalDefeated(AarisOfflineRival rival)
        {
            if (rival != null)
                UntrackRivalPosition(rival.SpatialSlot, rival);
            if (activeRivals > 0) activeRivals--;
            if (spawnComplete && spawnedRivals > 0 && activeRivals == 0 &&
                playerHealth != null && !playerHealth.IsDead &&
                matchEnd != null && !matchEnd.IsFinished)
                matchEnd.WinMatch();
        }

        internal bool CanEngage => Time.timeScale > 0f && started && playerHealth != null &&
            !playerHealth.IsDead && matchEnd != null && !matchEnd.IsFinished;

        // Skip the 100-slot attack scan when no live, visible rival is within
        // the 42m player-interest band. The existing spatial grid is kept
        // updated by physical AND time-sliced virtual movement.
        private bool HasPotentialPlayerAttacker()
        {
            if (player == null) return false;
            BootstrapRivalSpatialIndex();
            Vector3 pos = player.position;
            int cx = RivalCell(pos.x), cz = RivalCell(pos.z);
            const float rangeSqr = 42f * 42f;
            for (int x = cx - 1; x <= cx + 1; x++)
                for (int z = cz - 1; z <= cz + 1; z++)
                {
                    long key = RivalCellKey(x, z);
                    for (int i = rivalBucketHeads[RivalBucket(x, z)]; i >= 0;
                         i = rivalSpatialNext[i])
                    {
                        if (rivalSpatialKeys[i] != key) continue;
                        AarisOfflineRival rival = rivals[i];
                        if (rival != null && rival.IsAlive && rival.HasVisibleBody &&
                            (rival.transform.position - pos).sqrMagnitude < rangeSqr)
                            return true;
                    }
                }
            return false;
        }

        private void RefreshPlayerAttackers()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using var aiProfilerScope = k_PlayerThreat.Auto();
#endif
            if (attackerScanCursor < 0)
            {
                if (Time.time < nextAttackerSelection) return;
                if (!HasPotentialPlayerAttacker())
                {
                    // No valid local player threat. Invalidate old slots;
                    // remote rival-vs-rival combat is completely independent.
                    attackerOne = attackerTwo = null;
                    nextAttackerSelection = Time.time + .3f;
                    return;
                }
                // Build the new two-attacker set incrementally. Keep the last
                // complete selection active until this sweep finishes.
                attackerScanCursor = 0;
                scanAttackerOne = scanAttackerTwo = null;
                attackerScanFirst = attackerScanSecond = 42f * 42f;
            }
            // Multiple bots can ask for permission in the same Update frame.
            // Run only one bounded LOS slice, regardless of caller count.
            if (attackerScanFrame == Time.frameCount) return;
            attackerScanFrame = Time.frameCount;
            float first = attackerScanFirst, second = attackerScanSecond;
            Vector3 playerPosition = player.position;
            int end = Mathf.Min(attackerScanCursor + AttackerSelectionPerFrame,
                rivals.Length);
            for (int i = attackerScanCursor; i < end; i++)
            {
                AarisOfflineRival bot = rivals[i];
                if (bot == null || !bot.IsAlive) continue;
                // A rival's tracked world position changes on every movement,
                // including same-cell movement. Keep a generous 48m broadphase
                // (versus 42m physical sight), covering normal frame-level
                // displacement without reading native Transform positions
                // for the other ~100 distant rivals each selection sweep.
                // Live position, camera/visual gate, exact LOS, roster-order ties
                // and the existing 12-slot/frame deadline remain authoritative.
                if (rivalSpatialRegistered[i] &&
                    (rivalSpatialPositions[i] - playerPosition).sqrMagnitude >=
                        48f * 48f) continue;
                if (!bot.HasVisibleBody) continue;
                float distance = (bot.transform.position - playerPosition).sqrMagnitude;
                // Reject distant targets before the expensive physical LOS ray.
                // Only visible, unobstructed rivals may reserve either slot.
                if (distance >= second || !bot.HasPlayerSightForSelection())
                    continue;
                if (distance < first)
                {
                    scanAttackerTwo = scanAttackerOne; second = first;
                    scanAttackerOne = bot; first = distance;
                }
                else if (distance < second)
                {
                    scanAttackerTwo = bot; second = distance;
                }
            }
            attackerScanFirst = first;
            attackerScanSecond = second;
            attackerScanCursor = end;
            if (attackerScanCursor != rivals.Length) return;
            // Commit both slots atomically; no partially-scanned rival can
            // become a third player attacker while the scan is underway.
            attackerOne = scanAttackerOne != null && scanAttackerOne.IsAlive
                ? scanAttackerOne : null;
            attackerTwo = scanAttackerTwo != null && scanAttackerTwo.IsAlive
                ? scanAttackerTwo : null;
            attackerScanCursor = -1;
            nextAttackerSelection = Time.time + .3f;
        }

        internal bool MayAttackPlayer(AarisOfflineRival bot)
        {
            // The staged selection is intentionally reused for up to a few
            // frames; it must not grant a now-dead or streamed-out rival
            // permission to deal invisible damage in that interval.
            if (!CanEngage || bot == null || !bot.IsAlive || !bot.HasVisibleBody)
                return false;
            RefreshPlayerAttackers();
            return bot == attackerOne || bot == attackerTwo;
        }

        internal bool TryLootForRival(Transform rival, out AarisWeaponState weapon)
        {
            weapon = null;
            if (rivalLootWorld == null && Time.time >= nextLootWorldLookup)
            {
                nextLootWorldLookup = Time.time + 5f;
                rivalLootWorld = FindFirstObjectByType<AarisWeaponLootWorld>();
            }
            return rivalLootWorld != null &&
                rivalLootWorld.TryTakeForRival(rival, out weapon);
        }

        // Distant opponents are strategic hunt targets, not shooting targets.
        // Only the existing 42 m LOS gate and 38 m firing gate permit damage.
        private const float RivalHuntRadius = 110f;
        // 110m spatial cells: any rival within hunt range is in the current
        // cell or one of eight neighbours. Preallocated arrays + hash buckets
        // avoid scanning 100 native Transforms for every distant hunt.
        private const int RivalSpatialBuckets = 256;
        private readonly int[] rivalBucketHeads = new int[RivalSpatialBuckets];
        private readonly int[] rivalSpatialNext = new int[RivalCount];
        private readonly int[] rivalSpatialPrevious = new int[RivalCount];
        private readonly int[] rivalSpatialBuckets = new int[RivalCount];
        private readonly long[] rivalSpatialKeys = new long[RivalCount];
        // Last authoritative rival locations, updated on every movement tick,
        // including moves that stay inside a 110m cell. Remote candidate
        // selection reads these structs instead of crossing into native
        // Transform.position for every spatial-index collision/neighbor.
        private readonly Vector3[] rivalSpatialPositions = new Vector3[RivalCount];
        // Cell bounds are stable through thousands of physical/virtual moves.
        // Cache them so intra-cell movements avoid two FloorToInt conversions,
        // a packed key and a hash lookup. Four floats per rival, no tick GC.
        private readonly Vector2[] rivalCellMin = new Vector2[RivalCount];
        private readonly Vector2[] rivalCellMax = new Vector2[RivalCount];
        private const float RivalCellBoundaryGuard = 0.01f;
        private readonly bool[] rivalSpatialRegistered = new bool[RivalCount];
        private bool rivalBucketsInitialized;
        private int indexedRivals;

        private static int RivalCell(float coordinate) =>
            Mathf.FloorToInt(coordinate / RivalHuntRadius);

        private static long RivalCellKey(int x, int z) =>
            ((long)x << 32) | (uint)z;

        private static int RivalBucket(int x, int z) =>
            unchecked((x * 73856093) ^ (z * 19349663)) &
            (RivalSpatialBuckets - 1);

        private void PrepareRivalBuckets()
        {
            if (rivalBucketsInitialized) return;
            for (int i = 0; i < rivalBucketHeads.Length; i++)
                rivalBucketHeads[i] = -1;
            rivalBucketsInitialized = true;
        }

        private void RemoveRivalSpatialSlot(int slot)
        {
            if (slot < 0 || slot >= RivalCount || !rivalSpatialRegistered[slot])
                return;
            int previous = rivalSpatialPrevious[slot];
            int next = rivalSpatialNext[slot];
            if (previous < 0) rivalBucketHeads[rivalSpatialBuckets[slot]] = next;
            else rivalSpatialNext[previous] = next;
            if (next >= 0) rivalSpatialPrevious[next] = previous;
            rivalSpatialRegistered[slot] = false;
            indexedRivals--;
        }

        internal void UntrackRivalPosition(int slot, AarisOfflineRival bot)
        {
            if (slot >= 0 && slot < RivalCount && rivals[slot] == bot)
                RemoveRivalSpatialSlot(slot);
        }

        internal void TrackRivalPosition(int slot, AarisOfflineRival bot,
            Vector3 position)
        {
            if (slot < 0 || slot >= RivalCount || rivals[slot] != bot) return;
            rivalSpatialPositions[slot] = position;
            // Preserve exact FloorToInt semantics at cell seams (including
            // negative coordinates): the 1cm seam guard takes the original
            // slow path. Teleports to another cell always reindex normally.
            if (rivalSpatialRegistered[slot])
            {
                Vector2 low = rivalCellMin[slot], high = rivalCellMax[slot];
                if (position.x >= low.x + RivalCellBoundaryGuard &&
                    position.x < high.x - RivalCellBoundaryGuard &&
                    position.z >= low.y + RivalCellBoundaryGuard &&
                    position.z < high.y - RivalCellBoundaryGuard)
                    return;
            }
            PrepareRivalBuckets();
            int x = RivalCell(position.x), z = RivalCell(position.z);
            long key = RivalCellKey(x, z);
            if (rivalSpatialRegistered[slot] && rivalSpatialKeys[slot] == key)
                return;
            RemoveRivalSpatialSlot(slot);
            int bucket = RivalBucket(x, z);
            int head = rivalBucketHeads[bucket];
            rivalSpatialNext[slot] = head;
            rivalSpatialPrevious[slot] = -1;
            if (head >= 0) rivalSpatialPrevious[head] = slot;
            rivalBucketHeads[bucket] = slot;
            rivalSpatialKeys[slot] = key;
            rivalSpatialBuckets[slot] = bucket;
            rivalCellMin[slot] = new Vector2(x * RivalHuntRadius,
                z * RivalHuntRadius);
            rivalCellMax[slot] = new Vector2((x + 1f) * RivalHuntRadius,
                (z + 1f) * RivalHuntRadius);
            rivalSpatialRegistered[slot] = true;
            indexedRivals++;
        }

        private void BootstrapRivalSpatialIndex()
        {
            PrepareRivalBuckets();
            // Supports Editor harnesses that populate rivals by reflection.
            if (indexedRivals != 0) return;
            for (int i = 0; i < rivals.Length; i++)
            {
                AarisOfflineRival bot = rivals[i];
                if (bot != null && bot.IsAlive)
                    TrackRivalPosition(i, bot, bot.transform.position);
            }
        }

        private const int AlternativeBudget = 6;
        private readonly AarisOfflineRival[] alternativeBots = new AarisOfflineRival[AlternativeBudget];
        private readonly float[] alternativeDistSq = new float[AlternativeBudget];
        private readonly int[] alternativeSlots = new int[AlternativeBudget];

        internal bool FindRivalOpponent(AarisOfflineRival self,
            out Transform victim, out AarisOfflineHealth victimHealth)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using var aiProfilerScope = k_SpatialHunt.Auto();
#endif
            victim = null; victimHealth = null;
            if (self == null) return false;
            BootstrapRivalSpatialIndex();
            Vector3 origin = self.transform.position;
            // The requester can have been teleported by an external script.
            TrackRivalPosition(self.SpatialSlot, self, origin);
            int cx = RivalCell(origin.x), cz = RivalCell(origin.z);
            float nearest = RivalHuntRadius * RivalHuntRadius;
            int nearestIndex = RivalCount;
            for (int x = cx - 1; x <= cx + 1; x++)
                for (int z = cz - 1; z <= cz + 1; z++)
                {
                    long key = RivalCellKey(x, z);
                    int bucket = RivalBucket(x, z);
                    for (int i = rivalBucketHeads[bucket]; i >= 0;
                         i = rivalSpatialNext[i])
                    {
                        if (rivalSpatialKeys[i] != key) continue;
                        float d = (rivalSpatialPositions[i] - origin).sqrMagnitude;
                        // Reject noncompetitive slots using managed positions before
                        // native Unity object liveness/Transform access. Exact
                        // 110m cutoff and roster-order tie handling are unchanged.
                        if (d >= RivalHuntRadius * RivalHuntRadius ||
                            d > nearest || (d == nearest && i >= nearestIndex))
                            continue;
                        AarisOfflineRival bot = rivals[i];
                        if (bot == null || bot == self || !bot.IsAlive) continue;
                        nearest = d;
                        nearestIndex = i;
                    }
                }
            // Resolve the native Transform only for the final winner, not
            // every progressively nearer candidate in the spatial buckets.
            if (nearestIndex == RivalCount) return false;
            AarisOfflineRival winner = rivals[nearestIndex];
            victim = winner.transform;
            victimHealth = winner.Health;
            return victim != null;
        }

        // The closest rival may be behind cover while another is visible.
        // Only a bot whose first target already failed a combat-range LOS
        // check asks for an alternative. Scan positions once without GC;
        // then try at most THREE nearest alternate LOS paths. This avoids
        // 100 expensive raycasts per thinking bot on 2 GB Android devices.
        internal bool TryFindVisibleRivalAlternative(AarisOfflineRival hunter,
            Transform blocked, out Transform victim, out AarisOfflineHealth victimHealth)
        {
            victim = null; victimHealth = null;
            if (hunter == null) return false;
            // The 42m LOS band is entirely inside the hunter's 110m hash
            // cell and its eight neighbours. Never scan the entire 100-bot
            // roster on each blocked-shot think tick.
            BootstrapRivalSpatialIndex();
            const float maxSqr = 42f * 42f;
            int used = 0;
            Vector3 origin = hunter.transform.position;
            TrackRivalPosition(hunter.SpatialSlot, hunter, origin);
            int cx = RivalCell(origin.x), cz = RivalCell(origin.z);
            for (int x = cx - 1; x <= cx + 1; x++)
                for (int z = cz - 1; z <= cz + 1; z++)
                {
                    long key = RivalCellKey(x, z);
                    int bucket = RivalBucket(x, z);
                    for (int i = rivalBucketHeads[bucket]; i >= 0;
                         i = rivalSpatialNext[i])
                    {
                        // Hash collisions must never import remote cells.
                        if (rivalSpatialKeys[i] != key) continue;
                        AarisOfflineRival bot = rivals[i];
                        if (bot == null || bot == hunter || !bot.IsAlive)
                            continue;
                        // Remote dormant fighters cannot have a physical LOS
                        // target until their live rig/receiver is admitted.
                        // Reject obviously distant hidden entries using the
                        // 48m spatial snapshot (42m actual LOS plus 6m slack),
                        // but never trust stale snapshots for visible/active
                        // or unregistered actors, including teleported rivals.
                        if (rivalSpatialRegistered[i] && bot.IsDormantAimInvisible &&
                            (rivalSpatialPositions[i] - origin).sqrMagnitude >=
                                48f * 48f)
                            continue;
                        // Only surviving candidates need a native Transform
                        // reference to check the obstructed primary target.
                        if (bot.transform == blocked) continue;
                        float sqr = (bot.transform.position - origin).sqrMagnitude;
                        if (sqr >= maxSqr) continue;
                        // Hash chains are not slot-ordered. Preserve the
                        // previous distance then roster-index tie ordering.
                        int slot = 0;
                        while (slot < used &&
                            (sqr > alternativeDistSq[slot] ||
                             (sqr == alternativeDistSq[slot] &&
                              i >= alternativeSlots[slot]))) slot++;
                        if (slot >= AlternativeBudget) continue;
                        for (int j = Mathf.Min(used, AlternativeBudget - 1);
                             j > slot; j--)
                        {
                            alternativeBots[j] = alternativeBots[j - 1];
                            alternativeDistSq[j] = alternativeDistSq[j - 1];
                            alternativeSlots[j] = alternativeSlots[j - 1];
                        }
                        alternativeBots[slot] = bot;
                        alternativeDistSq[slot] = sqr;
                        alternativeSlots[slot] = i;
                        if (used < AlternativeBudget) used++;
                    }
                }
            // Six closest candidates at most; only the survivors get real LOS.
            // Preserve the original cover-aware fallback firing behavior.
            for (int i = 0; i < used; i++)
            {
                AarisOfflineRival bot = alternativeBots[i];
                if (bot == null || !bot.IsAlive ||
                    !hunter.HasSightToRival(bot.transform)) continue;
                victim = bot.transform; victimHealth = bot.Health;
                return true;
            }
            return false;
        }

        // A camera-visible enemy is not red-locked through a barrel-side wall.
        // Match TryFire's stance eye-height and near-muzzle obstruction probe.
        private static bool MuzzlePathOpenTo(Transform shooter, Transform victim,
            Vector3 cameraImpact)
        {
            AarisBattleActions actions = shooter.GetComponent<AarisBattleActions>();
            float height = actions != null
                ? Mathf.Max(0.28f, actions.CameraEyeHeight - 0.15f) : 1.3f;
            Vector3 anchor = shooter.position + Vector3.up * height;
            if (FirstMuzzleExternalHit(new Ray(anchor, shooter.forward), 0.24f,
                shooter, out RaycastHit shortBlocker))
            {
                // At contact range the first muzzle hit can be the visible
                // target itself. Reject real cover, not the enemy the shot
                // will physically hit; the camera LOS was checked separately.
                return shortBlocker.collider.transform.IsChildOf(victim);
            }
            Vector3 muzzle = anchor + shooter.forward * 0.24f;
            Vector3 to = cameraImpact - muzzle;
            float distance = to.magnitude;
            if (distance <= 0.025f) return false;
            Ray ray = new Ray(muzzle, to / distance);
            return FirstMuzzleExternalHit(ray, distance + 0.05f, shooter,
                out RaycastHit first) && first.collider.transform.IsChildOf(victim) &&
                !StreamedCoverBlocks(ray, first.distance);
        }

        private static bool StreamedCoverBlocks(Ray ray, float distance)
        {
            AarisNearbyColliders cover = activeDirector.nearbyColliders;
            return cover != null && cover.TryGetInactiveCoverHit(ray, distance, out _);
        }

        private static bool FirstMuzzleExternalHit(Ray ray, float range,
            Transform shooter, out RaycastHit closest)
        {
            if (!Physics.Raycast(ray, out closest, range,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return false;
            if (!closest.collider.transform.IsChildOf(shooter)) return true;
            closest = default;
            int count = AarisPhysicsHitBuffer.Raycast(ray, range,
                Physics.DefaultRaycastLayers, ref muzzleAssistHits, 256,
                out RaycastHit[] hits);
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                Collider collider = hits[i].collider;
                if (collider == null || collider.transform.IsChildOf(shooter) ||
                    hits[i].distance >= nearest) continue;
                nearest = hits[i].distance;
                closest = hits[i];
            }
            return nearest < float.PositiveInfinity;
        }

        internal static bool TryAcquireAimTarget(Camera camera, Transform shooter,
            float range, float coneDegrees, out Vector3 aimedDirection) =>
            TryAcquireAimLock(camera, shooter, range, coneDegrees,
                out aimedDirection, out _);

        internal static bool TryAcquireAimLock(Camera camera, Transform shooter,
            float range, float coneDegrees, out Vector3 aimedDirection,
            out AarisOfflineRival lockedRival)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using var aiProfilerScope = k_AimSelection.Auto();
#endif
            aimedDirection = Vector3.zero;
            lockedRival = null;
            if (camera == null || shooter == null || activeDirector == null) return false;
            Vector3 origin = camera.transform.position;
            Vector3 cameraForward = camera.transform.forward;
            var centerRay = new Ray(origin, cameraForward);
            // A physical hit under the crosshair takes priority over snapping
            // to a rival's torso. This also handles intentionally aimed headshots.
            if (FirstMuzzleExternalHit(centerRay, range, shooter, out RaycastHit direct))
            {
                // Optional authored child hitboxes belong to the same rival.
                AarisOfflineRival directRival =
                    direct.collider.GetComponentInParent<AarisOfflineRival>();
                if (directRival != null && directRival.IsAlive && directRival.HasVisibleBody &&
                    !StreamedCoverBlocks(centerRay, direct.distance) &&
                    MuzzlePathOpenTo(shooter, directRival.transform, direct.point))
                {
                    lockedRival = directRival;
                    aimedDirection = cameraForward;
                    return true;
                }
            }
            float maximumRangeSqr = range * range;
            float cachedCullRange = range + AimSpatialSlack;
            float cachedCullRangeSqr = cachedCullRange * cachedCullRange;
            float minimumDot = Mathf.Cos(coneDegrees * Mathf.Deg2Rad);
            // Find the three most centered visible bodies in ONE scan, not
            // three full 100-rival scans on every 90ms HUD update/shot.
            AarisOfflineRival first = null, second = null, third = null;
            Vector3 d1 = Vector3.zero, d2 = Vector3.zero, d3 = Vector3.zero;
            float r1 = 0f, r2 = 0f, r3 = 0f;
            float a1 = minimumDot, a2 = minimumDot, a3 = minimumDot;
            for (int i = 0; i < activeDirector.rivals.Length; i++)
            {
                AarisOfflineRival bot = activeDirector.rivals[i];
                if (bot == null || !bot.IsAlive) continue;
                // Reject only centrally-dormant remote entries before native pose.
                // Active visible actors use live geometry even after teleports.
                if (activeDirector.rivalSpatialRegistered[i] &&
                    bot.IsDormantAimInvisible &&
                    (activeDirector.rivalSpatialPositions[i] - origin).sqrMagnitude >
                    cachedCullRangeSqr) continue;
                // A dormant hidden fighter has no physical aim receiver;
                // skip its native position access even inside a 4x range.
                // Active or camera-visible targets keep exact LOS/geometry.
                if (bot.IsDormantAimInvisible) continue;
                Vector3 delta = bot.transform.position + Vector3.up * 1.12f - origin;
                float sqrDistance = delta.sqrMagnitude;
                if (sqrDistance < .0625f || sqrDistance > maximumRangeSqr) continue;
                float projected = Vector3.Dot(cameraForward, delta);
                float threshold = a3;
                // Reject by squared cone before sqrt, preserving candidate order.
                if (projected <= 0f ||
                    projected * projected <= threshold * threshold * sqrDistance)
                    continue;
                // Query renderer visibility only after cheap range and cone rejection.
                if (!bot.HasVisibleBody) continue;
                float distance = Mathf.Sqrt(sqrDistance);
                float alignment = projected / distance;
                if (alignment <= threshold) continue;
                Vector3 direction = delta / distance;
                if (alignment > a1)
                {
                    third = second; d3 = d2; r3 = r2; a3 = a2;
                    second = first; d2 = d1; r2 = r1; a2 = a1;
                    first = bot; d1 = direction; r1 = distance; a1 = alignment;
                }
                else if (alignment > a2)
                {
                    third = second; d3 = d2; r3 = r2; a3 = a2;
                    second = bot; d2 = direction; r2 = distance; a2 = alignment;
                }
                else
                {
                    third = bot; d3 = direction; r3 = distance; a3 = alignment;
                }
            }
            // Best-first physical camera AND muzzle tests. An occluded first
            // or second candidate must not block the next visible target.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                AarisOfflineRival candidate = attempt == 0 ? first :
                    attempt == 1 ? second : third;
                if (candidate == null) break;
                Vector3 direction = attempt == 0 ? d1 : attempt == 1 ? d2 : d3;
                float distance = attempt == 0 ? r1 : attempt == 1 ? r2 : r3;
                Ray candidateRay = new Ray(origin, direction);
                int count = AarisPhysicsHitBuffer.Raycast(candidateRay,
                    distance + .5f, Physics.DefaultRaycastLayers, ref assistHits, 256,
                    out RaycastHit[] hits);
                float nearest = float.PositiveInfinity;
                Collider closest = null;
                Vector3 closestPoint = Vector3.zero;
                for (int i = 0; i < count; i++)
                {
                    Collider collider = hits[i].collider;
                    if (collider == null || collider.transform.IsChildOf(shooter)) continue;
                    if (hits[i].distance < nearest)
                    { nearest = hits[i].distance; closest = collider; closestPoint = hits[i].point; }
                }
                if (closest != null && closest.transform.IsChildOf(candidate.transform) &&
                    !StreamedCoverBlocks(candidateRay, nearest) &&
                    MuzzlePathOpenTo(shooter, candidate.transform, closestPoint))
                {
                    aimedDirection = direction;
                    lockedRival = candidate;
                    return true;
                }
            }
            // Keep the original top-three fast path; only investigate the
            // next three when every centered body is physically obstructed.
            return TryAcquireAimFallback(origin, cameraForward, shooter,
                maximumRangeSqr, minimumDot, first, second, third, out aimedDirection,
                out lockedRival);
        }

        internal static bool CanKeepAimLock(Camera camera, Transform shooter,
            AarisOfflineRival rival, float range, float coneDegrees)
        {
            if (camera == null || activeDirector == null || rival == null ||
                !rival.IsAlive || !rival.HasVisibleBody) return false;
            Vector3 origin = camera.transform.position;
            Vector3 forward = camera.transform.forward;
            var centerRay = new Ray(origin, forward);
            if (AimRayReachesRival(centerRay, range, shooter, rival.transform)) return true;
            Vector3 to = rival.transform.position + Vector3.up * 1.12f - origin;
            float lengthSquared = to.sqrMagnitude;
            float projected = Vector3.Dot(forward, to);
            float minimum = Mathf.Cos(coneDegrees * Mathf.Deg2Rad);
            if (lengthSquared > range * range || projected <= 0f ||
                projected * projected <= minimum * minimum * lengthSquared) return false;
            var candidateRay = new Ray(origin, to);
            return AimRayReachesRival(candidateRay, Mathf.Min(range, to.magnitude + .5f),
                shooter, rival.transform);
        }

        private static bool AimRayReachesRival(Ray ray, float distance, Transform shooter,
            Transform rival)
        {
            return FirstMuzzleExternalHit(ray, distance, shooter, out RaycastHit hit) &&
                hit.collider.transform.IsChildOf(rival) &&
                !StreamedCoverBlocks(ray, hit.distance) &&
                MuzzlePathOpenTo(shooter, rival, hit.point);
        }

        private void RefreshStatus(float elapsed)
        {
            if (status == null) return;
            float wait = 0f;
            for (int i = 0; i < CloseStarts.Length; i++)
                if (elapsed < CloseEnds[i])
                {
                    wait = Mathf.Max(0f, CloseStarts[i] - elapsed);
                    break;
                }
            string label = Outside(player.position) ? "OUTSIDE ZONE" :
                wait > 0f ? "CLOSE IN " + Mathf.CeilToInt(wait) + "s" : "ZONE MOVING";
            // Until a valid rival actually exists, ALIVE 1 incorrectly
            // tells the player the BR has already been won.
            string participants = spawnComplete
                ? "ALIVE " + (activeRivals + 1)
                : "RIVALS " + spawnedRivals + "/" + RivalCount;
            status.text = participants +
                "    ZONE " + Mathf.CeilToInt(radius) + "m    " + label;
        }

        private static bool TryAcquireAimFallback(Vector3 origin, Vector3 forward,
            Transform shooter, float maxRangeSqr, float minimumDot,
            AarisOfflineRival first, AarisOfflineRival second,
            AarisOfflineRival third, out Vector3 aimedDirection, out AarisOfflineRival lockedRival)
        {
            aimedDirection = Vector3.zero;
            lockedRival = null;
            float cachedCullRange = Mathf.Sqrt(maxRangeSqr) + AimSpatialSlack;
            float cachedCullRangeSqr = cachedCullRange * cachedCullRange;
            int used = 0;
            for (int i = 0; i < activeDirector.rivals.Length; i++)
            {
                AarisOfflineRival bot = activeDirector.rivals[i];
                if (bot == null || bot == first || bot == second || bot == third ||
                    !bot.IsAlive) continue;
                if (activeDirector.rivalSpatialRegistered[i] &&
                    bot.IsDormantAimInvisible &&
                    (activeDirector.rivalSpatialPositions[i] - origin).sqrMagnitude >
                    cachedCullRangeSqr) continue;
                // A dormant hidden fighter has no physical aim receiver;
                // skip its native position access even inside a 4x range.
                // Active or camera-visible targets keep exact LOS/geometry.
                if (bot.IsDormantAimInvisible) continue;
                Vector3 delta = bot.transform.position + Vector3.up * 1.12f - origin;
                float sqr = delta.sqrMagnitude;
                if (sqr < .0625f || sqr > maxRangeSqr) continue;
                float projected = Vector3.Dot(forward, delta);
                float cutoff = used < AimBudget ? minimumDot : aimDots[AimBudget - 1];
                if (projected <= 0f || projected * projected <= cutoff * cutoff * sqr)
                    continue;
                if (!bot.HasVisibleBody) continue;
                float distance = Mathf.Sqrt(sqr);
                float score = projected / distance;
                if (score <= cutoff) continue;
                int slot = 0;
                while (slot < used && score <= aimDots[slot]) slot++;
                if (slot >= AimBudget) continue;
                for (int j = Mathf.Min(used, AimBudget - 1); j > slot; j--)
                {
                    aimBots[j] = aimBots[j - 1];
                    aimDirs[j] = aimDirs[j - 1];
                    aimRanges[j] = aimRanges[j - 1];
                    aimDots[j] = aimDots[j - 1];
                }
                aimBots[slot] = bot;
                aimDirs[slot] = delta / distance;
                aimRanges[slot] = distance;
                aimDots[slot] = score;
                if (used < AimBudget) used++;
            }
            // Only after the original three fail: up to three more true
            // camera AND muzzle LOS checks; never fire through cover.
            for (int attempt = 0; attempt < used; attempt++)
            {
                AarisOfflineRival bot = aimBots[attempt];
                if (bot == null || !bot.IsAlive || !bot.HasVisibleBody) continue;
                Vector3 direction = aimDirs[attempt];
                Ray candidateRay = new Ray(origin, direction);
                int count = AarisPhysicsHitBuffer.Raycast(candidateRay,
                    aimRanges[attempt] + .5f, Physics.DefaultRaycastLayers,
                    ref assistHits, 256, out RaycastHit[] hits);
                float nearest = float.PositiveInfinity;
                Collider closest = null;
                Vector3 point = Vector3.zero;
                for (int i = 0; i < count; i++)
                {
                    Collider hit = hits[i].collider;
                    if (hit == null || hit.transform.IsChildOf(shooter) ||
                        hits[i].distance >= nearest) continue;
                    nearest = hits[i].distance;
                    closest = hit;
                    point = hits[i].point;
                }
                if (closest != null && closest.transform.IsChildOf(bot.transform) &&
                    !StreamedCoverBlocks(candidateRay, nearest) &&
                    MuzzlePathOpenTo(shooter, bot.transform, point))
                {
                    aimedDirection = direction;
                    lockedRival = bot;
                    return true;
                }
            }
            return false;
        }

        private void CreateStatus()
        {
            var canvasRoot = new GameObject("Aaris BR Status", typeof(RectTransform),
                typeof(Canvas), typeof(CanvasScaler));
            canvasRoot.transform.SetParent(transform, false);
            statusCanvas = canvasRoot.GetComponent<Canvas>();
            statusCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            statusCanvas.sortingOrder = 165; // Below the five real action buttons.
            var scaler = canvasRoot.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(960f, 540f);
            var labelRoot = new GameObject("Alive and zone", typeof(RectTransform), typeof(Text));
            labelRoot.transform.SetParent(canvasRoot.transform, false);
            var rect = labelRoot.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, .98f);
            rect.sizeDelta = new Vector2(670f, 44f);
            status = labelRoot.GetComponent<Text>();
            status.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            status.fontSize = 18;
            status.alignment = TextAnchor.MiddleCenter;
            status.color = Color.white;
            status.raycastTarget = false;
        }

        private void CreateBoundary()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) return;
            boundaryMaterial = new Material(shader) { name = "Aaris Offline Zone Edge" };
            if (boundaryMaterial.HasProperty("_BaseColor"))
                boundaryMaterial.SetColor("_BaseColor", new Color(.25f, .9f, 1f, .85f));
            var objectRoot = new GameObject("Aaris Zone Boundary");
            objectRoot.transform.SetParent(transform, false);
            boundary = objectRoot.AddComponent<LineRenderer>();
            boundary.sharedMaterial = boundaryMaterial;
            boundary.positionCount = 65;
            boundary.useWorldSpace = true;
            boundary.widthMultiplier = 1.1f;
            boundary.startColor = boundary.endColor = new Color(.27f, .9f, 1f, .80f);
            boundary.shadowCastingMode = ShadowCastingMode.Off;
            boundary.receiveShadows = false;
            boundary.enabled = false;
        }

        private void DrawBoundary()
        {
            if (boundary == null) return;
            // Until the ring enters the map, it adds no draw calls at all.
            if (radius > 925f)
            {
                if (boundary.enabled) boundary.enabled = false;
                return;
            }
            // Terrain is authored/static. During the long stationary zone
            // phases, the same ring needs neither 65 terrain resamples nor
            // another native point upload. Compare exact values: even small
            // legitimate zone motion must still be drawn at the existing rate.
            if (drawnBoundary == boundary &&
                drawnBoundaryCenter.x == center.x &&
                drawnBoundaryCenter.y == center.y &&
                drawnBoundaryRadius == radius)
            {
                if (!boundary.enabled) boundary.enabled = true;
                return;
            }
            for (int i = 0; i < 65; i++)
            {
                float angle = i * Mathf.PI * 2f / 64f;
                float x = center.x + Mathf.Cos(angle) * radius;
                float z = center.y + Mathf.Sin(angle) * radius;
                float ground = TryGetGround(x, z, out float sample) ? sample : OceanCutoff;
                boundaryPoints[i] = new Vector3(x, ground + 1.2f, z);
            }
            // One native batch upload preserves all 65 original points.
            boundary.SetPositions(boundaryPoints);
            drawnBoundary = boundary;
            drawnBoundaryCenter = center;
            drawnBoundaryRadius = radius;
            if (!boundary.enabled) boundary.enabled = true;
        }

        // Source rig uses some legacy shaders. Never modify imported materials:
        // share one optional URP conversion per authored material across rivals.
        internal Material CompatibleRivalMaterial(Material source)
        {
            if (source == null) return null;
            if (source.shader != null && source.shader.name.StartsWith(
                "Universal Render Pipeline/", StringComparison.Ordinal)) return source;
            if (rivalMaterialCache.TryGetValue(source, out Material cached))
                return cached;
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null) return source;
            var result = new Material(lit) { name = "Aaris rival URP " + source.name };
            Color color = source.HasProperty("_BaseColor") ? source.GetColor("_BaseColor") :
                source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white;
            result.SetColor("_BaseColor", color);
            string baseProp = source.HasProperty("_BaseMap") ? "_BaseMap" :
                source.HasProperty("_MainTex") ? "_MainTex" : null;
            if (baseProp != null)
            {
                result.SetTexture("_BaseMap", source.GetTexture(baseProp));
                result.SetTextureScale("_BaseMap", source.GetTextureScale(baseProp));
                result.SetTextureOffset("_BaseMap", source.GetTextureOffset(baseProp));
            }
            if (source.HasProperty("_Metallic"))
                result.SetFloat("_Metallic", source.GetFloat("_Metallic"));
            if (source.HasProperty("_Smoothness"))
                result.SetFloat("_Smoothness", source.GetFloat("_Smoothness"));
            else if (source.HasProperty("_Glossiness"))
                result.SetFloat("_Smoothness", source.GetFloat("_Glossiness"));
            if (source.HasProperty("_BumpMap") && source.GetTexture("_BumpMap") != null)
            {
                result.SetTexture("_BumpMap", source.GetTexture("_BumpMap"));
                if (source.HasProperty("_BumpScale"))
                    result.SetFloat("_BumpScale", source.GetFloat("_BumpScale"));
                result.EnableKeyword("_NORMALMAP");
            }
            if (source.HasProperty("_OcclusionMap") &&
                source.GetTexture("_OcclusionMap") != null)
            {
                result.SetTexture("_OcclusionMap", source.GetTexture("_OcclusionMap"));
                result.EnableKeyword("_OCCLUSIONMAP");
            }
            rivalMaterialCache.Add(source, result);
            return result;
        }

        private void OnDestroy()
        {
            foreach (Material converted in rivalMaterialCache.Values)
                if (converted != null) Destroy(converted);
            rivalMaterialCache.Clear();
            if (boundaryMaterial != null) Destroy(boundaryMaterial);
            // Child HUD/ring and rivals are scene-local, never DontDestroyOnLoad.
            // The scene manager handles their cleanup on restart.
        }
    }

    /// <summary>
    /// Cheap local rival: capsule collision, terrain-aware pursuit, bounded
    /// perception, genuine cover-occluded hits and a reused local Humanoid.
    /// The player's controller is never duplicated or modified.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class AarisOfflineRival : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static readonly ProfilerMarker k_RemoteCombat =
            new ProfilerMarker("Aaris.AI.RemoteCombatTick");
#endif
        private AarisOfflineBRDirector owner;
        private AarisOfflineHealth health, targetHealth;
        private Transform target;
        private CharacterController capsule;
#if UNITY_ANDROID && !UNITY_EDITOR
        // Avoid bulk native PhysX allocations when the camera turns.
        private static int lastReceiverBuildFrame = -1;
#endif
        // Receivers admitted for one scoped glimpse must not accumulate
        // for the entire two-kilometre match after their native body is idle.
        private const float ReceiverRetireDistance = 190f;
        private const float ReceiverRetireAfterSeconds = 6f;
        private float receiverUnusedSince = -1f;
        private static int lastReceiverRetireFrame = -1;
        private RaycastHit[] sightHits;
        // Kept per rival: retain correct closest-visible-target selection
        // without RaycastAll allocations when >20 cover hits overlap.
        private RaycastHit[] crowdedSightHits;
        private float nextThink, nextAttack, verticalSpeed, turnOffset, visualBlend;
        private Transform playerTarget;
        private AarisOfflineHealth playerTargetHealth;
        // Cache stance-aware collision bodies; do not GetComponent in LOS ray hot paths.
        private CharacterController playerTargetCapsule, targetCapsule;
        private Transform cachedTargetCapsuleOwner;
        // The same hidden rival may receive multiple abstract shots. Cache
        // its managed component after the first native TryGetComponent, but
        // invalidate on target, health or destroyed-component changes.
        private Transform cachedVirtualDamageTarget;
        private AarisOfflineHealth cachedVirtualDamageHealth;
        private AarisOfflineRival cachedVirtualDamageRival;
        private GameObject visualRoot;
        // Near attackers must remain physical/targetable behind the camera.
        // Renderer-only culling removes their skinning/shadow submissions
        // without disabling their gameplay transform or physical capsule.
        private Renderer[] detailedRenderers;
        private bool[] authoredRendererForcedOff;
        private bool detailedRenderingSuppressed;
        private bool virtualBody;
        private bool lastCameraRelevant;
        private float nextCameraProbe;
        // Beyond 485m the bot has no render/receiver. Check proximity at
        // 10Hz rather than fetching two Transforms on every rendered frame.
        // Simulation ticks still take priority; no AI/kill-feed tick is lost.
        private bool dormantOutOfVisualRange;
        // Off-camera virtual rivals need no per-render-frame transform reads.
        private bool dormantOffscreenVirtual;
        private bool centrallyDormant;
        internal float NextDormantWakeTime => dormantOutOfVisualRange
            ? Mathf.Min(nextFarSimulation, nextDormantProximityProbe)
            : Mathf.Min(nextFarSimulation,
                Mathf.Min(nextDormantProximityProbe, nextCameraProbe));

        internal void TickDormantFromDirector()
        {
            // Disabled native Update, unchanged AI simulation code.
            if (centrallyDormant) Update();
        }

        internal void ForceDormantProximityProbe()
        {
            // Teleports wake presentation only; never advance combat clocks.
            nextDormantProximityProbe = 0f;
        }

        private void SetCentralDormancy(bool dormant)
        {
            if (centrallyDormant == dormant) return;
            centrallyDormant = dormant;
            if (owner != null)
                owner.RegisterDormantRival(spatialSlot, this, dormant);
            enabled = !dormant;
        }

        private float nextDormantProximityProbe;
        private const float DormantProximityProbeInterval = 0.10f;
        // A separate low-triangle visual keeps 4x targets visible beyond 155 m
        // without loading distant Humanoid rigs or changing close-up quality.
        private GameObject farVisualRoot;
        private static Mesh sharedFarVisualMesh;
        private static Material sharedFarVisualMaterial;
        private const float FarVisualDistance = 485f;
        private bool visualBuilt;
        private float nextVisualCheck, staggeredVisualDelay;
        // A hidden close Humanoid still retains its skinned meshes and
        // PlayableGraph. Retire it after sustained distance and rebuild the
        // SAME authored rig on return; the shared far LOD stays visible.
        private const float RigRetireDistance = 190f;
        private const float RigRetireAfterSeconds = 5f;
        private float rigFarSince = -1f;
        private static int lastRigRetireFrame = -1;
        private int medkits = 1;
        private float nextLootAt;
        private AarisWeaponSpec carriedGun;
        private int carriedMagazine, carriedReserve;
        private uint remoteLootSeed;
        private bool remoteLoadoutGiven;
        private Collider[] coverCandidates;
        // Nearby settlements can fill the small unordered NonAlloc result.
        // Retry only saturated damage-triggered searches in a reusable buffer.
        private Collider[] crowdedCoverCandidates;
        private Vector3 coverDestination;
        private float coverUntil, nextCoverQuery, nextHitReaction;
        // Briefly remember the real rival who landed damage. This is bot-vs-bot
        // awareness only; player attack reservations still require the director.
        private AarisOfflineRival recentRivalAttacker;
        private float recentRivalAttackerUntil;
        internal AarisOfflineHealth Health => health;
        // Outside the combat range, save mobile CPU by updating far capsules
        // at 10 Hz. Keep near opponents at full gameplay frame rate.
        private const float FarSimulationDistance = 90f;
        private const float FarSimulationInterval = 0.35f;
        // Sparse far ticks can span two frames on a 15 FPS phone; do not
        // truncate ordinary 133 ms travel, but cap extreme app-resume hitches.
        private float nextFarSimulation, lastSimulationTime;
#if UNITY_ANDROID && !UNITY_EDITOR
        // Six 10Hz phases distribute distant capsules across rendered frames.
        private float farSimulationPhase;
#endif
        private float avoidanceHandedness;
        private int spatialSlot = -1;
        internal int SpatialSlot => spatialSlot;
        private bool canSee;
        private PlayableGraph graph;
        private AnimationMixerPlayable animations;
        private bool hasAnimations;
        public bool IsAlive => health != null && !health.IsDead;
        // The physical collider may spawn before its streamed visual. Do not
        // permit invisible opponents to claim player attack slots or Red-Lock.
        internal bool HasVisibleBody =>
            (visualRoot != null && visualRoot.activeInHierarchy) ||
            (farVisualRoot != null && farVisualRoot.activeInHierarchy);
        // Only centrally dormant, off-camera bots can trust their cached
        // coordinates for early aim rejection. Live/visible actors may be
        // teleported by gameplay or QA and must use their actual Transform.
        internal bool IsDormantAimInvisible => centrallyDormant && !lastCameraRelevant;

        // Called only by the opt-in performance overlay, never per AI frame.
        internal void AccumulatePerfSample(ref AarisOfflineBRDirector.RivalPerfSample sample)
        {
            if (centrallyDormant) sample.dormant++;
            if (virtualBody) sample.virtualBodies++;
            if (visualRoot != null && visualRoot.activeInHierarchy)
                sample.detailedRigs++;
            if (farVisualRoot != null && farVisualRoot.activeInHierarchy)
                sample.farMeshes++;
            if (capsule != null && capsule.enabled)
                sample.physicalReceivers++;
        }

        internal void Initialize(AarisOfflineBRDirector director, AarisOfflineHealth self,
            Transform victim, AarisOfflineHealth victimHealth, int encounterSlot)
        {
            owner = director;
            spatialSlot = encounterSlot;
            health = self;
            target = null;
            targetHealth = null;
            cachedVirtualDamageTarget = null;
            cachedVirtualDamageHealth = null;
            cachedVirtualDamageRival = null;
            playerTarget = victim;
            playerTargetHealth = victimHealth;
            playerTargetCapsule = victim != null ? victim.GetComponent<CharacterController>() : null;
            capsule = GetComponent<CharacterController>();
            health.Died += OnDied;
            health.Damaged += OnDamaged;
            turnOffset = UnityEngine.Random.Range(-12f, 12f);
            avoidanceHandedness = UnityEngine.Random.value < 0.5f ? 1f : -1f;
            lastSimulationTime = Time.time;
            nextFarSimulation = Time.time; // First distant update remains immediate.
#if UNITY_ANDROID && !UNITY_EDITOR
            // Six authored rival slots at distinct phases of the same 100ms cycle.
            // No additional physics ticks, bots, timers or per-frame allocations.
            farSimulationPhase = encounterSlot * (FarSimulationInterval / 100f);
#endif
            staggeredVisualDelay = Time.time + encounterSlot * 0.015f;
            nextCameraProbe = Time.time + encounterSlot * 0.0012f;
            nextLootAt = Time.time + encounterSlot * 0.035f + 1f;
            remoteLootSeed = 0x9E3779B9u ^ ((uint)encounterSlot + 1u);
            // Register distant rivals as logical fighters from the first frame.
            // Do not register 100 expensive PhysX character capsules briefly
            // only to turn them off on the next scheduled simulation tick.
            float spawnDistanceSqr = (transform.position - victim.position).sqrMagnitude;
            // Offscreen 90-185m rivals spawn math-only; nearby visible
            // rivals keep a real collision receiver for hitscan and cover.
            bool spawnCameraRelevant = spawnDistanceSqr >=
                FarSimulationDistance * FarSimulationDistance &&
                spawnDistanceSqr < 185f * 185f &&
                owner.IsDistantRivalOnScreen(transform.position);
            lastCameraRelevant = spawnCameraRelevant;
            if (spawnDistanceSqr >= 185f * 185f ||
                (spawnDistanceSqr >= FarSimulationDistance * FarSimulationDistance &&
                 !spawnCameraRelevant))
            {
                virtualBody = true;
                if (capsule != null) capsule.enabled = false;
            }
            else
            {
                EnsurePhysicalReceiver();
                EnsurePhysicalCombatBuffers();
            }
            // A 55-100m unseen spawn must not instantiate a skinned Humanoid
            // just because it was nearby when the slot was created. The
            // staggered visibility admission in Update builds it on entry.
            if (spawnDistanceSqr < 55f * 55f ||
                (spawnDistanceSqr < 100f * 100f &&
                 owner.IsDistantRivalOnScreen(transform.position)))
                BuildVisual();
            // A close rival can physically fight behind the player, but its
            // newly instantiated renderer must not emit hidden shadow draws.
            if (visualRoot != null)
                SetDetailedRenderVisibility(owner.IsDistantRivalOnScreen(transform.position));
        }

        // Retain a native collision body only once an actor is physically
        // nearby or camera-visible within the scoped rifle hit range.
        private CharacterController EnsurePhysicalReceiver()
        {
            if (capsule != null) return capsule;
            capsule = gameObject.AddComponent<CharacterController>();
            capsule.height = 1.8f;
            capsule.radius = 0.31f;
            capsule.center = new Vector3(0f, 0.9f, 0f);
            capsule.stepOffset = 0.28f;
            capsule.skinWidth = 0.05f;
            capsule.slopeLimit = 43f;
            return capsule;
        }

        // Destroy only a disabled, distant, off-camera hit receiver after
        // a quiet dwell. Never retire local movement or camera-visible hits.
        // One teardown per frame bounds native destruction spikes.
        private void RetireUnusedPhysicalReceiver(float now, float playerDistanceSqr,
            bool needsPhysicalReceiver)
        {
            if (capsule == null || needsPhysicalReceiver || !virtualBody ||
                playerDistanceSqr < ReceiverRetireDistance * ReceiverRetireDistance)
            {
                receiverUnusedSince = -1f;
                return;
            }
            if (capsule.enabled) { receiverUnusedSince = -1f; return; }
            if (receiverUnusedSince < 0f) receiverUnusedSince = now;
            if (now - receiverUnusedSince < ReceiverRetireAfterSeconds ||
                lastReceiverRetireFrame == Time.frameCount) return;
            lastReceiverRetireFrame = Time.frameCount;
            CharacterController retired = capsule;
            capsule = null;
            receiverUnusedSince = -1f;
#if UNITY_ANDROID && !UNITY_EDITOR
            AarisMobileStreamingFrameGate.ReserveNativeTeardownFrame();
#endif
            Destroy(retired);
        }

        private void EnsurePhysicalCombatBuffers()
        {
            if (sightHits != null) return;
            sightHits = new RaycastHit[20];
            crowdedSightHits = new RaycastHit[128];
            coverCandidates = new Collider[24];
            crowdedCoverCandidates = new Collider[128];
        }

        // Cache once at rig creation: never traverse the Humanoid hierarchy
        // on a camera tick. Preserve authored forceRenderingOff flags.
        private void CacheDetailedRenderers()
        {
            if (visualRoot == null) return;
            detailedRenderers = visualRoot.GetComponentsInChildren<Renderer>(true);
            authoredRendererForcedOff = new bool[detailedRenderers.Length];
            for (int i = 0; i < detailedRenderers.Length; i++)
                authoredRendererForcedOff[i] = detailedRenderers[i] != null &&
                    detailedRenderers[i].forceRenderingOff;
            detailedRenderingSuppressed = false;
        }

        // Called at most on the existing camera/visual transition ticks.
        // No new allocations, shader changes, material swaps or colliders.
        private void SetDetailedRenderVisibility(bool cameraRelevant)
        {
            if (visualRoot == null || !visualRoot.activeSelf ||
                detailedRenderers == null) return;
            bool suppress = !cameraRelevant;
            if (detailedRenderingSuppressed == suppress) return;
            detailedRenderingSuppressed = suppress;
            for (int i = 0; i < detailedRenderers.Length; i++)
            {
                Renderer renderer = detailedRenderers[i];
                if (renderer != null)
                    renderer.forceRenderingOff = suppress ||
                        authoredRendererForcedOff[i];
            }
            if (hasAnimations && graph.IsValid())
            {
                if (suppress) graph.Stop();
                else graph.Play();
            }
        }

        private void BuildVisual()
        {
            // The bundled CC0 humanoid is shared by all rivals. Avoid six
            // full hero-quality tactical rigs on entry-level 2GB devices.
            if (owner == null || visualBuilt) return;
            visualBuilt = true;
            owner.ResolveRivalAssets(out GameObject prefab, out Avatar cachedAvatar,
                out AnimationClip idle, out AnimationClip run);
            if (prefab == null)
            {
                BuildFallbackSilhouette();
                return;
            }
            var visual = Instantiate(prefab, transform, false);
            if (!AarisOfflineBRDirector.HasRenderableRivalPrefab(visual))
            {
                Destroy(visual);
                BuildFallbackSilhouette();
                return;
            }
            visual.name = "Rival humanoid visual";
            visualRoot = visual;
            foreach (var c in visual.GetComponentsInChildren<Collider>(true))
            { c.enabled = false; Destroy(c); }
            foreach (var body in visual.GetComponentsInChildren<Rigidbody>(true))
            { body.isKinematic = true; body.detectCollisions = false; }
            // The conversion cache is on the match director; one rig is
            // imported once and six opponents reuse the same URP materials.
            foreach (var renderer in visual.GetComponentsInChildren<Renderer>(true))
            {
                Material[] materials = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material compatible = owner.CompatibleRivalMaterial(materials[i]);
                    if (compatible == materials[i]) continue;
                    materials[i] = compatible;
                    changed = true;
                }
                if (changed) renderer.sharedMaterials = materials;
            }
            CacheDetailedRenderers();
            var animator = visual.GetComponentInChildren<Animator>(true);
            if (animator == null) animator = visual.AddComponent<Animator>();
            if (animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
                animator.avatar = cachedAvatar;
            // Native skinning must not continue while the rig is off-screen.
            // Keep the original high-quality close mesh and its materials.
            foreach (SkinnedMeshRenderer skin in
                visual.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                skin.updateWhenOffscreen = false;
            bool boneFitted = TryFitHumanoidModel(visual.transform, animator);
            if (!boneFitted)
            {
                Bounds bounds = new Bounds();
                bool found = false;
                foreach (var render in visual.GetComponentsInChildren<Renderer>(true))
                {
                    if (!render.enabled || !render.gameObject.activeInHierarchy) continue;
                    if (!found) { bounds = render.bounds; found = true; }
                    else bounds.Encapsulate(render.bounds);
                }
                if (found && bounds.size.y > .01f && bounds.size.y < 1000f)
                {
                    float scale = Mathf.Clamp(1.74f / bounds.size.y, .02f, 25f);
                    visual.transform.localScale *= scale;
                    float low = float.PositiveInfinity;
                    foreach (var render in visual.GetComponentsInChildren<Renderer>(true))
                        if (render.enabled && render.gameObject.activeInHierarchy)
                            low = Mathf.Min(low, render.bounds.min.y);
                    if (!float.IsInfinity(low))
                        visual.transform.position += Vector3.up * (transform.position.y - low);
                }

            }
            // A failed Avatar/clip import must not disable native culling:
            // the prefab may still have its authored Animator controller.
            // Apply culling before the graph-only early exit as well.
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullCompletely;
            if (animator.avatar == null || !animator.avatar.isValid ||
                !animator.avatar.isHuman || idle == null || run == null) return;
            graph = PlayableGraph.Create("Aaris Offline Rival");
            graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            animations = AnimationMixerPlayable.Create(graph, 2);
            graph.Connect(AnimationClipPlayable.Create(graph, idle), 0, animations, 0);
            graph.Connect(AnimationClipPlayable.Create(graph, run), 0, animations, 1);
            visualBlend = 0f;
            animations.SetInputWeight(0, 1f);
            animations.SetInputWeight(1, 0f);
            AnimationPlayableOutput.Create(graph, "Local rival animation", animator)
                .SetSourcePlayable(animations);
            graph.Play();
            hasAnimations = true;
        }

        // Called only on the existing 0.55s staggered visual tick.
        // Never retire a visible rig, or one lacking its far LOD in the
        // 155-485m visual band. At most one native hierarchy per frame.
        private void RetireHiddenRig(float now, float playerDistanceSqr)
        {
            if (visualRoot == null || visualRoot.activeSelf ||
                playerDistanceSqr < RigRetireDistance * RigRetireDistance)
            {
                rigFarSince = -1f;
                return;
            }
            if (rigFarSince < 0f) rigFarSince = now;
            if (now - rigFarSince < RigRetireAfterSeconds ||
                lastRigRetireFrame == Time.frameCount) return;
            if (playerDistanceSqr < FarVisualDistance * FarVisualDistance &&
                owner != null && owner.IsDistantRivalOnScreen(transform.position) &&
                (farVisualRoot == null || !farVisualRoot.activeInHierarchy))
                return;
            lastRigRetireFrame = Time.frameCount;
            if (graph.IsValid()) graph.Destroy();
            hasAnimations = false;
            visualBlend = 0f;
            GameObject retired = visualRoot;
            visualRoot = null;
            visualBuilt = false;
            detailedRenderers = null;
            authoredRendererForcedOff = null;
            detailedRenderingSuppressed = false;
            rigFarSince = -1f;
#if UNITY_ANDROID && !UNITY_EDITOR
            AarisMobileStreamingFrameGate.ReserveNativeTeardownFrame();
#endif
            Destroy(retired);
        }

        // Built only once per domain. Seven small cuboids yield one shared
        // humanoid silhouette mesh: no skinning, Animator, extra physics,
        // texture import, material clones or per-frame managed allocations.
        private static void AppendFarBox(List<Vector3> vertices, List<int> indices,
            Vector3 center, Vector3 half)
        {
            int start = vertices.Count;
            vertices.Add(center + new Vector3(-half.x, -half.y, -half.z));
            vertices.Add(center + new Vector3( half.x, -half.y, -half.z));
            vertices.Add(center + new Vector3( half.x,  half.y, -half.z));
            vertices.Add(center + new Vector3(-half.x,  half.y, -half.z));
            vertices.Add(center + new Vector3(-half.x, -half.y,  half.z));
            vertices.Add(center + new Vector3( half.x, -half.y,  half.z));
            vertices.Add(center + new Vector3( half.x,  half.y,  half.z));
            vertices.Add(center + new Vector3(-half.x,  half.y,  half.z));
            // All six faces wound outward. No collider is added to the LOD.
            int[] cube = {
                4,5,6, 4,6,7, 1,0,3, 1,3,2,
                0,4,7, 0,7,3, 5,1,2, 5,2,6,
                3,7,6, 3,6,2, 0,1,5, 0,5,4
            };
            for (int i = 0; i < cube.Length; i++)
                indices.Add(start + cube[i]);
        }

        private static Mesh DistantRivalMesh()
        {
            if (sharedFarVisualMesh != null) return sharedFarVisualMesh;
            var vertices = new List<Vector3>(56);
            var indices = new List<int>(252);
            AppendFarBox(vertices, indices, new Vector3(0f, 1.13f, 0f),
                new Vector3(.27f, .38f, .16f)); // chest
            AppendFarBox(vertices, indices, new Vector3(0f, 1.65f, 0f),
                new Vector3(.155f, .17f, .15f)); // head
            AppendFarBox(vertices, indices, new Vector3(0f, 1.81f, 0f),
                new Vector3(.20f, .075f, .18f)); // helmet
            AppendFarBox(vertices, indices, new Vector3(-.375f, 1.13f, 0f),
                new Vector3(.105f, .34f, .13f)); // left arm
            AppendFarBox(vertices, indices, new Vector3(.375f, 1.13f, 0f),
                new Vector3(.105f, .34f, .13f)); // right arm
            AppendFarBox(vertices, indices, new Vector3(-.145f, .40f, 0f),
                new Vector3(.12f, .40f, .13f)); // left leg
            AppendFarBox(vertices, indices, new Vector3(.145f, .40f, 0f),
                new Vector3(.12f, .40f, .13f)); // right leg
            var mesh = new Mesh { name = "Aaris shared 4x distant rival LOD" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            sharedFarVisualMesh = mesh;
            return mesh;
        }

        private static Material DistantRivalMaterial()
        {
            if (sharedFarVisualMaterial != null) return sharedFarVisualMaterial;
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) return null;
            var material = new Material(shader)
            {
                name = "Aaris shared distant rival olive",
                enableInstancing = true
            };
            Color color = new Color(.35f, .41f, .33f, 1f);
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
            sharedFarVisualMaterial = material;
            return material;
        }

        private void BuildDistantVisual()
        {
            if (farVisualRoot != null) return;
            Material material = DistantRivalMaterial();
            if (material == null) return;
            var distant = new GameObject("Shared distant rival LOD",
                typeof(MeshFilter), typeof(MeshRenderer));
            distant.layer = gameObject.layer;
            distant.transform.SetParent(transform, false);
            distant.GetComponent<MeshFilter>().sharedMesh = DistantRivalMesh();
            var renderer = distant.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            farVisualRoot = distant;
        }

        private void BuildFallbackSilhouette()
        {
            // This is a visible last resort, not a replacement for the
            // bundled realistic models. It adds no secondary physics collider.
            var fallback = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            fallback.name = "Rival fallback silhouette";
            visualRoot = fallback;
            fallback.transform.SetParent(transform, false);
            fallback.transform.localPosition = Vector3.up * .90f;
            fallback.transform.localScale = new Vector3(.62f, .9f, .62f);
            Collider collider = fallback.GetComponent<Collider>();
            if (collider != null) { collider.enabled = false; Destroy(collider); }
            CacheDetailedRenderers();
        }

        // Rigs may contain enormous import/bind-pose renderer bounds.
        // Fit on the actual Humanoid skeleton before falling back to AABBs.
        private static bool TryFitHumanoidModel(Transform model, Animator animator)
        {
            if (animator == null || animator.avatar == null ||
                !animator.avatar.isValid || !animator.avatar.isHuman) return false;
            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            Transform left = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            Transform right = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (head == null || left == null || right == null) return false;
            float low = Mathf.Min(left.position.y, right.position.y);
            float span = head.position.y - low;
            if (float.IsNaN(span) || float.IsInfinity(span) ||
                span < .80f || span > 2.50f) return false;
            model.localScale *= Mathf.Clamp(1.64f / span, .50f, 2f);
            Vector3 feet = (left.position + right.position) * .5f;
            feet.y = Mathf.Min(left.position.y, right.position.y);
            model.position += model.parent.position + Vector3.up * .075f - feet;
            return true;
        }

        private void Update()
        {
            float now = Time.time;
            // A bot previously confirmed beyond the maximum visual distance
            // has no visible body or hit receiver. Wake for its scheduled AI
            // tick OR an earlier 100ms proximity check (including teleports).
            // Close and potentially visible bots never enter this fast path.
            if (dormantOutOfVisualRange && now < nextFarSimulation &&
                now < nextDormantProximityProbe) return;
            // Proximity, camera and combat deadlines wake independently.
            if (dormantOffscreenVirtual && now < nextFarSimulation &&
                now < nextDormantProximityProbe && now < nextCameraProbe) return;
            if (owner == null || !owner.CanEngage || !IsAlive ||
                playerTarget == null || playerTargetHealth == null || playerTargetHealth.IsDead)
                return;
            // Avoid 100 square roots each rendered frame on mobile. All
            // thresholds below are distances squared; the 90m policy is unchanged.
            float playerDistanceSqr =
                (playerTarget.position - transform.position).sqrMagnitude;
            dormantOutOfVisualRange =
                playerDistanceSqr >= FarVisualDistance * FarVisualDistance;
            if (dormantOutOfVisualRange)
            {
                // 485-650m: check vehicle approach between fight ticks.
                // Beyond 650m the 350ms math-combat tick already probes
                // distance; do not separately poll 10x/sec at 1km.
                // 90-485m retains the existing fast camera/proximity probes.
                nextDormantProximityProbe = now +
                    (playerDistanceSqr >= 650f * 650f
                        ? FarSimulationInterval : 0.16f);
            }
            bool wasCameraRelevant = lastCameraRelevant;
            bool cameraRelevant = wasCameraRelevant;
            if (playerDistanceSqr < FarVisualDistance * FarVisualDistance)
            {
                if (now >= nextCameraProbe)
                {
                    nextCameraProbe = now + 0.12f;
                    cameraRelevant = owner.IsDistantRivalOnScreen(transform.position);
                    // Entering OR leaving the viewport changes expensive visual/receiver
                    // admission immediately rather than waiting for a second 0.55s tick.
                    if (cameraRelevant != lastCameraRelevant) nextVisualCheck = now;
                    // Under 55m the attacker stays alive, targetable and
                    // physically simulated even behind the camera; only
                    // renderer/shadow and animation submissions go to zero.
                    if (playerDistanceSqr < 55f * 55f)
                        SetDetailedRenderVisibility(cameraRelevant);
                    if (!cameraRelevant && playerDistanceSqr >= 55f * 55f)
                    {
                        // Evict offscreen visuals and remote hit receivers on
                        // this 120ms camera probe, NOT the slower 350ms AI tick.
                        // Keep close (<55m) enemies fully simulated and visible.
                        if (playerDistanceSqr >= 55f * 55f &&
                            visualRoot != null && visualRoot.activeSelf)
                        {
                            visualRoot.SetActive(false);
                            if (hasAnimations && graph.IsValid()) graph.Stop();
                        }
                        if (farVisualRoot != null && farVisualRoot.activeSelf)
                            farVisualRoot.SetActive(false);
                        // Offscreen midrange AI no longer needs a PhysX body.
                        if (playerDistanceSqr >=
                            FarSimulationDistance * FarSimulationDistance)
                            virtualBody = true;
                        if (virtualBody && capsule != null && capsule.enabled)
                            capsule.enabled = false;
                    }
                    lastCameraRelevant = cameraRelevant;
                }
            }
            else
            {
                // Exiting the 485m representation band must evict immediately.
                // Otherwise a camera-visible distant mesh and its stationary
                // PhysX receiver survive until the next 350ms simulation or
                // 550ms visual tick after a vehicle move or player teleport.
                cameraRelevant = false;
                if (lastCameraRelevant) nextVisualCheck = now;
                if (visualRoot != null && visualRoot.activeSelf)
                {
                    visualRoot.SetActive(false);
                    if (hasAnimations && graph.IsValid()) graph.Stop();
                }
                if (farVisualRoot != null && farVisualRoot.activeSelf)
                    farVisualRoot.SetActive(false);
                // Enter abstract-body mode without executing an extra AI or
                // damage tick, and unregister a stale far bullet receiver.
                virtualBody = true;
                if (capsule != null && capsule.enabled)
                    capsule.enabled = false;
                lastCameraRelevant = false;
            }
            // Throttle 90-485m hidden, virtual, receiver-free rivals only.
            dormantOffscreenVirtual = !dormantOutOfVisualRange &&
                playerDistanceSqr >= FarSimulationDistance * FarSimulationDistance &&
                !lastCameraRelevant && virtualBody &&
                (visualRoot == null || !visualRoot.activeSelf) &&
                (farVisualRoot == null || !farVisualRoot.activeSelf) &&
                (capsule == null || !capsule.enabled);
            if (dormantOffscreenVirtual)
                nextDormantProximityProbe = now + DormantProximityProbeInterval;
            // On-camera actors beyond 185m are still abstract fighters.
            // Their shared unskinned LOD and stationary bullet receiver do
            // not require a MonoBehaviour.Update every rendered frame.
            // Reuse the existing 120ms camera/proximity and 350ms AI clocks.
            // Nearby physical combat remains fully frame-driven.
            bool dormantVisibleFarVirtual = !dormantOutOfVisualRange &&
                lastCameraRelevant && virtualBody &&
                playerDistanceSqr >= 185f * 185f &&
                (visualRoot == null || !visualRoot.activeSelf);
            if (dormantVisibleFarVirtual)
                nextDormantProximityProbe = now + 0.12f;
            SetCentralDormancy(dormantOutOfVisualRange || dormantOffscreenVirtual ||
                dormantVisibleFarVirtual);
            // Camera visibility controls rendering/physical hit receivers,
            // not AI tick frequency: 4x scoped enemies must not turn 100
            // distant AI brains into full-rate Update/PhysX processing.
            bool farSimulation = playerDistanceSqr >=
                FarSimulationDistance * FarSimulationDistance;
            bool justEnteredCamera = cameraRelevant && !wasCameraRelevant;
            // A camera-entry event may admit a render mesh and hit receiver,
            // but must never advance the independent abstract combat clock.
            bool simulationDue = !farSimulation || now >= nextFarSimulation;
            // A denied native model/receiver admission must retry on the
            // presentation clock, not wait for another 350ms combat tick.
            // The independent simulationDue gate below still owns all AI.
            bool presentationRetryDue = cameraRelevant &&
                now >= nextVisualCheck && now >= staggeredVisualDelay &&
                (!HasVisibleBody || capsule == null || !capsule.enabled);
            if (farSimulation && now < nextFarSimulation && !justEnteredCamera &&
                !presentationRetryDue) return;
            // Near the player use the actual shared floor pickup record.
            // Off-screen guns are not streamed: simulate ONE bounded scavenge
            // from the ready catalog without creating any distant gun meshes.
            // Camera-only presentation must not advance scavenging state.
            if (simulationDue && carriedGun == null && now >= nextLootAt)
            {
                nextLootAt = now + 3.5f;
                AarisWeaponSpec found = null;
                AarisWeaponState scavenged = null;
                if (playerDistanceSqr < 76f * 76f)
                    owner.TryLootForRival(transform, out scavenged);
                else if (!remoteLoadoutGiven && AarisWeaponCatalog.Ready)
                {
                    remoteLoadoutGiven = true;
                    found = AarisWeaponCatalog.Select(ref remoteLootSeed, false);
                }
                if (scavenged != null && scavenged.Spec != null)
                {
                    // Nearby bots inherit the actual physical pickup's rounds.
                    // OnDied will therefore return only genuinely unspent ammo
                    // to the player's death crate, never a free full reload.
                    carriedGun = scavenged.Spec;
                    carriedMagazine = Mathf.Clamp(scavenged.Magazine, 0,
                        carriedGun.magazineSize);
                    carriedReserve = Mathf.Max(0, scavenged.Reserve);
                }
                else if (found != null)
                {
                    // Distant virtual encounter gets its one seeded loadout;
                    // unlike an existing physical floor pickup, it has no
                    // previously authored magazine state to preserve.
                    carriedGun = found;
                    carriedMagazine = found.magazineSize;
                    carriedReserve = found.reserveAmmo;
                }
            }
            float dt = Mathf.Min(Time.deltaTime, 0.12f);
            if (simulationDue)
            {
                if (farSimulation)
                    dt = Mathf.Clamp(now - lastSimulationTime, 0f, 0.46f);
                lastSimulationTime = now;
                if (farSimulation)
                {
#if UNITY_ANDROID && !UNITY_EDITOR
                    float nextTick = (Mathf.Floor((now - farSimulationPhase) /
                        FarSimulationInterval) + 1f) * FarSimulationInterval + farSimulationPhase;
                    if (nextTick <= now + 0.0001f) nextTick += FarSimulationInterval;
                    nextFarSimulation = nextTick;
#else
                    nextFarSimulation = now + FarSimulationInterval;
#endif
                }
            }
            // Only instantiate expensive humanoids for local encounters.
            // Keep remote logical fighters alive and avoid imported visual spikes.
            if (now >= nextVisualCheck && now >= staggeredVisualDelay)
            {
                nextVisualCheck = now + .55f;
                if (!visualBuilt && playerDistanceSqr < 105f * 105f)
                {
                    // An unseen 55-105 m opponent needs no resident Humanoid,
                    // PlayableGraph, skinning buffers or native import yet.
                    // When the camera turns, the lightweight shared LOD can
                    // appear while the high-quality rig is admitted safely.
                    if (playerDistanceSqr < 55f * 55f || cameraRelevant)
                    {
                        bool permitted = true;
#if UNITY_ANDROID && !UNITY_EDITOR
                        permitted = AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild();
#endif
                        if (permitted) BuildVisual();
                    }
                }
                if (visualRoot != null)
                {
                    // Near threats remain fully detailed; unseen 55-155m
                    // rivals have no mesh, shadow pass or active animation graph.
                    // Camera entry forces an immediate visual check above.
                    bool visible = playerDistanceSqr < 155f * 155f &&
                        (playerDistanceSqr < 55f * 55f || cameraRelevant);
                    if (visualRoot.activeSelf != visible)
                    {
                        visualRoot.SetActive(visible);
                        if (hasAnimations && graph.IsValid())
                        {
                            if (visible) graph.Play();
                            else graph.Stop();
                        }
                    }
                    if (visible) SetDetailedRenderVisibility(cameraRelevant);
                }
                // The real rig is never replaced inside its current 155 m
                // quality band. Beyond it, a single shared unskinned mesh
                // remains visible within the longest 480 m 4x rifle range.
                // Keep this check on the existing 0.55 s staggered visual tick.
                bool farBand = cameraRelevant &&
                    playerDistanceSqr < FarVisualDistance * FarVisualDistance &&
                    (visualRoot == null || !visualRoot.activeSelf);
                if (farBand && farVisualRoot == null)
                {
#if UNITY_ANDROID && !UNITY_EDITOR
                    // Never contend with an essential loot/player native build.
                    if (AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())
                        BuildDistantVisual();
#else
                    BuildDistantVisual();
#endif
                }
                if (farVisualRoot != null && farVisualRoot.activeSelf != farBand)
                    farVisualRoot.SetActive(farBand);
                // Retry a denied optional model on the next camera probe;
                // no additional AI, physics sight or loot tick is admitted.
                if (cameraRelevant && !HasVisibleBody)
                    nextVisualCheck = now + 0.12f;
                RetireHiddenRig(now, playerDistanceSqr);
            }
            // Simulation LOD must be independent of camera visibility.
            // At 185m+ even on-screen 4x targets use abstract movement/combat,
            // not CharacterController.Move or per-rival physical sight rays.
            // Retain a stationary capsule ONLY for a camera-visible long-range
            // physical bullet receiver; outside the view it is unregistered.
            // 165/185m hysteresis prevents toggling physical simulation.
            // Camera visibility and proximity independently control physics.
            // Preserve the existing 165/185m return hysteresis for scope hits.
            bool shouldVirtualize = playerDistanceSqr >= 185f * 185f ||
                (playerDistanceSqr >= FarSimulationDistance * FarSimulationDistance &&
                 !cameraRelevant);
            bool shouldRestorePhysics = playerDistanceSqr <= 165f * 165f &&
                (playerDistanceSqr < FarSimulationDistance * FarSimulationDistance ||
                 cameraRelevant);
            if (!virtualBody && shouldVirtualize)
                virtualBody = true;
            else if (virtualBody && shouldRestorePhysics)
            {
                virtualBody = false;
                verticalSpeed = -1.5f;
            }
            bool needsPhysicalReceiver = !virtualBody ||
                (cameraRelevant && HasVisibleBody &&
                 playerDistanceSqr < FarVisualDistance * FarVisualDistance);
            if (needsPhysicalReceiver && capsule == null)
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                // One new native receiver per frame; remote math still ticks.
                if (lastReceiverBuildFrame != Time.frameCount)
                {
                    lastReceiverBuildFrame = Time.frameCount;
                    EnsurePhysicalReceiver();
                }
#else
                EnsurePhysicalReceiver();
#endif
            }
            if (capsule != null && capsule.enabled != needsPhysicalReceiver)
                capsule.enabled = needsPhysicalReceiver;
            // The one-per-frame native receiver budget may also be denied
            // on camera entry. Do not leave a visible model unhittable
            // until the normal 550ms presentation deadline.
            if (cameraRelevant && needsPhysicalReceiver && capsule == null)
                nextVisualCheck = Mathf.Min(nextVisualCheck, now + 0.12f);
            RetireUnusedPhysicalReceiver(now, playerDistanceSqr, needsPhysicalReceiver);
            // Camera-entry presentation cannot spend an extra movement, attack,
            // loot or virtual-combat tick before its scheduled simulation turn.
            if (!simulationDue) return;
            if (virtualBody)
            {
                TickVirtualCombat(now, dt);
                return;
            }
            // A new on-camera actor may wait one frame for native admission.
            if (capsule == null) return;
            // Prewarm physical arrays when approaching 165m, before LOS.
            EnsurePhysicalCombatBuffers();
            // Snapshot physical pose before movement. Targets, stance-aware LOS
            // and post-Move travel remain authoritative native observations.
            Vector3 selfPosition = transform.position;
            Vector3 selfForward = transform.forward;
            if (now >= nextThink)
            {
                nextThink = now + .35f + UnityEngine.Random.Range(0f, .14f);
                if (owner.MayAttackPlayer(this))
                {
                    target = playerTarget;
                    targetHealth = playerTargetHealth;
                }
                else if (recentRivalAttacker != null && recentRivalAttacker.IsAlive &&
                    now < recentRivalAttackerUntil &&
                    (recentRivalAttacker.transform.position - selfPosition).sqrMagnitude <
                        110f * 110f)
                {
                    // A hit from another bot briefly overrides nearest-bot hunting.
                    // Never assign the player here: the two-slot cap has priority.
                    target = recentRivalAttacker.transform;
                    targetHealth = recentRivalAttacker.Health;
                }
                else if (!owner.FindRivalOpponent(this, out target, out targetHealth))
                {
                    target = null;
                    targetHealth = null;
                }
                // Only target changes refresh this reference; stance center/height
                // remain live so prone and crouch affect sight immediately.
                if (target != cachedTargetCapsuleOwner)
                {
                    cachedTargetCapsuleOwner = target;
                    targetCapsule = target == playerTarget ? playerTargetCapsule :
                        target != null ? target.GetComponent<CharacterController>() : null;
                }
                canSee = target != null && targetHealth != null && !targetHealth.IsDead &&
                    (target.position - transform.position).sqrMagnitude < 42f * 42f && HasSight();
                // If the nearest rival is behind a wall, a second visible
                // opponent in the same 42m combat band is more meaningful
                // than endlessly staring at the first obstruction. Only
                // run this bounded search on an actual occluded rival;
                // never steal one of the two reserved player-attack slots.
                if (!canSee && target != null && target != playerTarget &&
                    (target.position - selfPosition).sqrMagnitude < 42f * 42f &&
                    owner.TryFindVisibleRivalAlternative(this, target,
                        out Transform clearRival, out AarisOfflineHealth clearHealth))
                {
                    target = clearRival;
                    targetHealth = clearHealth;
                    cachedTargetCapsuleOwner = target;
                    targetCapsule = target.GetComponent<CharacterController>();
                    // Revalidate after adopting the real target capsule.
                    canSee = HasSight();
                }
                if (!canSee)
                {
                    turnOffset += UnityEngine.Random.Range(-35f, 35f);
                    // Escape close cover on the scheduled think tick.
                    // The old second think check ran after nextThink advanced,
                    // so this near-obstacle branch was never reached.
                    if (target != null &&
                        (target.position - transform.position).sqrMagnitude < 81f)
                        turnOffset = UnityEngine.Random.Range(-55f, 55f);
                }
                if (medkits > 0 && !canSee && health.CurrentHealth < 90f)
                {
                    medkits--;
                    health.RestoreHealth(45f);
                }
            }
            bool hasTarget = target != null && targetHealth != null && !targetHealth.IsDead;
            Vector3 diff = hasTarget ? target.position - selfPosition :
                Quaternion.Euler(0f, turnOffset, 0f) * selfForward * 25f;
            diff.y = 0f;
            float distance = diff.magnitude;
            Vector3 toward = distance > .01f ? diff / distance : selfForward;
            bool outsideSafeArea = owner.TryZoneRetreatDirection(transform.position,
                out Vector3 retreatDirection);
            // Nearby player contact retains combat priority; distant bots
            // should not ignore a closing circle just to chase the player.
            bool retreat = outsideSafeArea && (distance > 23f || !canSee);
            bool seekingCover = !retreat && now < coverUntil &&
                (coverDestination - selfPosition).sqrMagnitude > 1f;
            Vector3 coverHeading = coverDestination - selfPosition;
            coverHeading.y = 0f;
            Vector3 travelDirection = seekingCover ? coverHeading.normalized :
                retreat ? retreatDirection : toward;
            // Unarmed rivals close to contact range; no imaginary SMG shots.
            float moveSpeed = seekingCover ? 3.45f : retreat ? 3.2f :
                carriedGun == null ? (distance > 1.7f ? 2.85f : 0f) :
                distance > 12f ? 2.85f : distance < 7f ? -1.15f : 0f;
            // Tactical cover and safe-zone escape must outrank random hunt
            // steering. Otherwise a recent hit's turnOffset can rotate the bot
            // away from its chosen cover position or the safe-zone center.
            Vector3 steering = seekingCover || retreat ? travelDirection :
                Quaternion.Euler(0f, turnOffset, 0f) * travelDirection;
            Vector3 desired = steering * moveSpeed;
            if (canSee && !retreat && !seekingCover && carriedGun != null)
                desired *= .23f; // Armed only; unarmed bots close to melee.
            Vector3 displacement = desired * dt;
            if (displacement.sqrMagnitude > 0.000001f &&
                owner.WouldCrossInactiveCover(transform.position, displacement))
            {
                // Choose a consistent tangent around remote house geometry,
                // instead of walking invisibly through a disabled collider.
                Vector3 first = Quaternion.Euler(0f, avoidanceHandedness * 80f, 0f)
                    * displacement;
                Vector3 second = Quaternion.Euler(0f, -avoidanceHandedness * 80f, 0f)
                    * displacement;
                if (!owner.WouldCrossInactiveCover(selfPosition, first))
                    displacement = first;
                else if (!owner.WouldCrossInactiveCover(selfPosition, second))
                    displacement = second;
                else
                    displacement = Vector3.zero;
            }
            // Follow the direction of actual planned movement while avoiding
            // remote cover or retreating to safety. Otherwise occluded rivals
            // moonwalk sideways/backwards while running across open terrain.
            // Keep eyes/body on the player during clear, active combat.
            // Rotation only: capsule collision and shot/zone gates are unchanged.
            Vector3 facing = distance > .05f ? toward : selfForward;
            if ((!canSee || retreat) && displacement.sqrMagnitude > 0.000001f)
                facing = displacement.normalized;
            if (facing.sqrMagnitude > .01f)
                transform.rotation = Quaternion.Slerp(transform.rotation,
                    Quaternion.LookRotation(facing), 1f - Mathf.Exp(-5f * dt));
            verticalSpeed = capsule.isGrounded ? -1.5f :
                Mathf.Max(-24f, verticalSpeed - 19.6f * dt);
            Vector3 beforeMove = transform.position;
            CollisionFlags hit = capsule.Move(displacement +
                Vector3.up * (verticalSpeed * dt));
            // Preserve exact post-collision native registration and motion.
            owner.TrackRivalPosition(spatialSlot, this, transform.position);
            if ((hit & CollisionFlags.Sides) != 0 && !canSee)
                turnOffset = -turnOffset + UnityEngine.Random.Range(-28f, 28f);

            if (hasAnimations && visualRoot != null && visualRoot.activeSelf &&
                !detailedRenderingSuppressed)
            {
                // Animate based on *actual* capsule travel. Against a house
                // or blocked corner the rival must not sprint in place.
                Vector3 realTravel = transform.position - beforeMove;
                realTravel.y = 0f;
                float groundSpeed = realTravel.magnitude / Mathf.Max(0.001f, dt);
                float wanted = Mathf.Clamp01(groundSpeed / 2.9f);
                UpdateAnimationBlend(wanted, dt);
            }
            // Reload outside the line-of-sight gate: a rival taking cover
            // should refill while sheltered, not wait until an opponent steps
            // into a clear firing lane. nextAttack is the existing reload
            // cooldown, so this cannot refill repeatedly on consecutive frames.
            if (carriedGun != null && carriedMagazine == 0 && now >= nextAttack)
            {
                if (carriedReserve <= 0)
                {
                    // Finite reserve ammo: fall back to real melee/scavenging.
                    carriedGun = null;
                    nextLootAt = now + 1f;
                    nextAttack = now + 0.7f;
                }
                else
                {
                    int refill = Mathf.Min(carriedGun.magazineSize, carriedReserve);
                    carriedMagazine = refill;
                    carriedReserve -= refill;
                    nextAttack = now + carriedGun.reloadSeconds;
                }
            }

            // Punch only within contact range if no firearm is equipped.
            if (hasTarget && canSee &&
                distance <= (carriedGun == null ? 2.25f : 38f) &&
                (target != playerTarget || owner.MayAttackPlayer(this)) &&
                Vector3.Dot(transform.forward, toward) > .84f &&
                Time.time >= nextAttack && TryGetSightHit(target, out RaycastHit shotHit) &&
                MuzzlePathClear(target))
            {
                // Physical loot changes the bot's magazine, fire interval,
                // damage and gunshot sound; no runtime gun model clone required.
                float fireInterval = carriedGun != null
                    ? Mathf.Clamp(carriedGun.ShotInterval * 3f, .35f, 2.4f)
                    : 1.15f;
                nextAttack = Time.time + fireInterval +
                    UnityEngine.Random.Range(.1f, .35f);
                if (carriedGun != null)
                {
                    carriedMagazine--;
                    // Enemy fire must be directional world audio, not an
                    // unattenuated local player gunshot in the 4-voice mix.
                    AarisAudioDirector.GunShot(carriedGun.category, carriedGun.id,
                        transform.position + Vector3.up, isPlayerShot: false);
                    // Visual-only. The existing physical shotHit decides damage.
                    if (owner != null)
                        owner.ShowRivalGunfire(transform, shotHit.point, carriedGun);
                }
                else
                {
                    AarisAudioDirector.Punch(transform.position + Vector3.up);
                }
                float damage = carriedGun != null
                    ? Mathf.Clamp(carriedGun.damage * .23f, 4f, 26f) : 6f;
                // Keep the same physical LOS for guns and punches. A punch
                // has no bullet hit coordinate and must never receive the
                // headshot multiplier reserved for physical gun impacts.
                // The unpositioned receiver still reports real body HP loss
                // and its pooled damage number at the victim bounds center.
                if (carriedGun != null)
                    AarisEquippedGun.ApplyDamage(shotHit.collider, damage,
                        gameObject, shotHit.point, carriedGun.headshotMultiplier);
                else
                    AarisEquippedGun.ApplyDamage(shotHit.collider, damage,
                        gameObject);
            }
        }

        // Distant bots stay fully alive as logical actors without per-bot
        // PhysX movement, projectiles, animation, IK or weapon models.
        private void TickVirtualCombat(float now, float dt)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using var aiProfilerScope = k_RemoteCombat.Auto();
#endif
            // Snapshot the native pose once per time-sliced math combat turn,
            // instead of repeatedly crossing into Unity's Transform getter.
            Vector3 selfPosition = transform.position;
            Vector3 selfForward = transform.forward;
            if (now >= nextThink)
            {
                nextThink = now + 0.55f + NextVirtualRandom() * 0.15f;
                if (recentRivalAttacker != null && recentRivalAttacker.IsAlive &&
                    now < recentRivalAttackerUntil &&
                    (recentRivalAttacker.transform.position - selfPosition)
                        .sqrMagnitude < 110f * 110f)
                {
                    target = recentRivalAttacker.transform;
                    targetHealth = recentRivalAttacker.Health;
                }
                else if (!owner.FindRivalOpponent(this, out target, out targetHealth))
                {
                    target = null;
                    targetHealth = null;
                }
                targetCapsule = null;
                cachedTargetCapsuleOwner = null;
                // Preserve the existing single-use medkit behavior without
                // any distant animation, physics query or audio source.
                if (medkits > 0 && health.CurrentHealth < 90f)
                {
                    medkits--;
                    health.RestoreHealth(45f);
                }
            }
            bool hasTarget = target != null && targetHealth != null &&
                !targetHealth.IsDead && target != playerTarget;
            Vector3 targetPosition = hasTarget ? target.position : Vector3.zero;
            Vector3 diff = hasTarget
                ? targetPosition - selfPosition : selfForward * 18f;
            diff.y = 0f;
            float distance = diff.magnitude;
            Vector3 toward = distance > 0.001f ? diff / distance : selfForward;
            bool retreat = owner.TryZoneRetreatDirection(selfPosition,
                out Vector3 zoneDirection);
            Vector3 direction = retreat ? zoneDirection : toward;
            float speed = retreat ? 3.2f :
                hasTarget && distance < (carriedGun != null ? 13f : 2f) ? 0f : 2.85f;
            Vector3 movement = direction * (speed * dt);
            if (movement.sqrMagnitude > 0.000001f)
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    // Try both sides of blocked cover or an invalid terrain
                    // edge. Staying pointed into the same coastline or wall
                    // indefinitely keeps an otherwise healthy rival stuck.
                    Vector3 step = attempt == 0 ? movement :
                        Quaternion.Euler(0f, avoidanceHandedness *
                            (attempt == 1 ? 95f : -95f), 0f) * movement;
                    if (owner.WouldCrossInactiveCover(selfPosition, step)) continue;
                    Vector3 next = selfPosition + step;
                    if (!owner.TryVirtualGround(next.x, next.z, out float ground)) continue;
                    next.y = ground + 0.08f;
                    transform.position = next;
                    owner.TrackRivalPosition(spatialSlot, this, next);
                    transform.rotation = Quaternion.LookRotation(step);
                    break;
                }
            }
            if (carriedGun != null && carriedMagazine == 0 && now >= nextAttack)
            {
                if (carriedReserve > 0)
                {
                    int refill = Mathf.Min(carriedGun.magazineSize, carriedReserve);
                    carriedMagazine = refill;
                    carriedReserve -= refill;
                    nextAttack = now + carriedGun.reloadSeconds;
                }
                else
                {
                    carriedGun = null;
                    nextLootAt = now + 1f;
                    nextAttack = now + 0.7f;
                }
            }
            // Never synthesize damage to the player or near-player rivals:
            // those interactions require actual colliders and clear muzzle LOS.
            if (!hasTarget || now < nextAttack ||
                distance > (carriedGun == null ? 2.25f : 38f) ||
                (targetPosition - playerTarget.position).sqrMagnitude <
                    FarSimulationDistance * FarSimulationDistance)
                return;
            // Abstract damage is valid only between two invisible fighters:
            // never hit the player or an on-camera physically rendered bot.
            // Reuse the component only for this exact target + health. The
            // native lookup repeats on an opponent switch or if the old
            // component was destroyed; visibility/physics remain live checks.
            if (cachedVirtualDamageTarget != target ||
                cachedVirtualDamageHealth != targetHealth ||
                cachedVirtualDamageRival == null)
            {
                cachedVirtualDamageTarget = target;
                cachedVirtualDamageHealth = targetHealth;
                if (!target.TryGetComponent<AarisOfflineRival>(
                        out cachedVirtualDamageRival))
                    return;
            }
            AarisOfflineRival hiddenTarget = cachedVirtualDamageRival;
            if (!hiddenTarget.virtualBody || hiddenTarget.lastCameraRelevant ||
                hiddenTarget.HasVisibleBody)
                return;
            float interval = carriedGun != null
                ? Mathf.Clamp(carriedGun.ShotInterval * 3f, 0.35f, 2.4f)
                : 1.15f;
            nextAttack = now + interval + NextVirtualRandom() * 0.25f;
            if (carriedGun != null) carriedMagazine--;
            float hitChance = carriedGun != null
                ? Mathf.Lerp(0.72f, 0.30f, distance / 38f) : 0.58f;
            if (NextVirtualRandom() >= hitChance) return;
            float damage = carriedGun != null
                ? Mathf.Clamp(carriedGun.damage * 0.23f, 4f, 26f) : 6f;
            targetHealth.ApplyDamage(damage, gameObject);
        }

        private float NextVirtualRandom()
        {
            remoteLootSeed = remoteLootSeed * 1664525u + 1013904223u;
            return (remoteLootSeed & 0x00FFFFFFu) * (1f / 16777216f);
        }

        private void UpdateAnimationBlend(float wanted, float dt)
        {
            float next = Mathf.MoveTowards(visualBlend, wanted, dt * 4f);
            // Graph time and clip animation keep running every frame. Only
            // skip the two native mixer writes when their exact value holds.
            if (next == visualBlend) return;
            visualBlend = next;
            animations.SetInputWeight(0, 1f - visualBlend);
            animations.SetInputWeight(1, visualBlend);
        }

        // Selection must check player LOS without mutating a bot-vs-bot target.
        // An unarmed rival 30-40m away cannot hurt the player yet. It must
        // not reserve one of only two player-attack slots ahead of an armed
        // rival with real LOS. Nearby unarmed opponents can still pursue and
        // punch at contact range.
        private const float UnarmedPlayerInterestRadius = 12f;
        internal bool HasPlayerSightForSelection()
        {
            if (!HasVisibleBody || playerTarget == null || playerTargetHealth == null ||
                playerTargetHealth.IsDead) return false;
            Vector3 towardPlayer = playerTarget.position - transform.position;
            float playerRangeSqr = towardPlayer.sqrMagnitude;
            if ((carriedGun == null || (carriedMagazine <= 0 && carriedReserve <= 0)) &&
                playerRangeSqr > UnarmedPlayerInterestRadius * UnarmedPlayerInterestRadius)
                return false;
            // A nearby 12m radar contact is visible in any direction, but at
            // 12-42m a bot must actually face the player before reserving one
            // of only two attack slots. The 120-degree horizontal sight cone
            // rejects rear contacts BEFORE any expensive LOS physics ray.
            if (playerRangeSqr > UnarmedPlayerInterestRadius * UnarmedPlayerInterestRadius)
            {
                towardPlayer.y = 0f;
                float planarSqr = towardPlayer.sqrMagnitude;
                float facing = Vector3.Dot(transform.forward, towardPlayer);
                if (facing <= 0f || facing * facing < planarSqr * 0.25f)
                    return false;
            }
            // Radar/sight never bypasses actual cover collision.
            return HasSightTo(playerTarget);
        }

        private bool HasSight() { return HasSightTo(target); }

        // The director uses this only after the nearest bot target fails
        // LOS, with at most three alternate candidates per think tick.
        internal bool HasSightToRival(Transform victim) => HasSightTo(victim);

        private bool HasSightTo(Transform victim)
        {
            // The long eye ray starts 48 cm in front of the rival. A thin
            // nearby wall can sit completely behind that ray origin. Apply
            // the same short muzzle obstruction to target acquisition, not
            // only to the final shot: otherwise an occluded bot can reserve
            // one of just two player-attack slots and freeze behind cover.
            // The short RaycastNonAlloc reuses this bot's existing hit array.
            return victim != null && MuzzlePathClear(victim) &&
                TryGetSightHit(victim, out RaycastHit ignored);
        }

        private bool TryGetSightHit(Transform victim, out RaycastHit shotHit)
        {
            shotHit = default(RaycastHit);
            if (victim == null) return false;
            EnsurePhysicalCombatBuffers();
            Vector3 eye = transform.position + transform.forward * .48f + Vector3.up * 1.28f;
            CharacterController body = victim == playerTarget ? playerTargetCapsule :
                victim == target ? targetCapsule : null;
            // Aim inside the live collision capsule, never at a fixed standing
            // 1.07m point above a crouched/prone player. The physics ray still
            // requires the victim to be the FIRST unobstructed collider.
            Vector3 aimPoint = body != null
                ? victim.TransformPoint(body.center) : victim.position + Vector3.up * 1.07f;
            Vector3 to = aimPoint - eye;
            float distance = to.magnitude;
            if (distance < .1f) return false;
            Ray ray = new Ray(eye, to / distance);
            int count = Physics.RaycastNonAlloc(ray, sightHits, distance,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            RaycastHit[] hits = sightHits;
            if (count >= sightHits.Length)
            {
                // Saturated NonAlloc hits are unordered and may not contain
                // the closest obstacle. Retry in a reusable larger buffer.
                hits = crowdedSightHits;
                count = Physics.RaycastNonAlloc(ray, crowdedSightHits, distance,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                if (count >= crowdedSightHits.Length)
                {
                    // Saturated NonAlloc results are unordered. Obtain the
                    // actual first surface without allocating RaycastAll.
                    // If it is our own collider, fail closed rather than
                    // risk ignoring cover and allowing a shot through a wall.
                    if (!Physics.Raycast(ray, out RaycastHit nearestHit, distance,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                        return false;
                    Collider firstCollider = nearestHit.collider;
                    if (firstCollider == null ||
                        firstCollider.transform.IsChildOf(transform))
                        return false;
                    shotHit = nearestHit;
                    return firstCollider.transform.IsChildOf(victim);
                }
            }
            float first = distance + 1f;
            Collider closest = null;
            for (int i = 0; i < count; i++)
            {
                var hit = hits[i];
                if (hit.collider == null || hit.collider.transform.IsChildOf(transform))
                    continue;
                if (hit.distance < first)
                {
                    first = hit.distance;
                    closest = hit.collider;
                    shotHit = hit;
                }
            }
            return closest != null && closest.transform.IsChildOf(victim);
        }

        // The LOS origin may lie beyond a thin nearby wall. A scheduled shot
        // must also clear the short body-to-muzzle path, not just the long ray.
        // Reuse the per-bot hit buffer; no per-shot managed allocations.
        private bool MuzzlePathClear(Transform intendedVictim)
        {
            EnsurePhysicalCombatBuffers();
            Vector3 bodyEye = transform.position + Vector3.up * 1.28f;
            Ray ray = new Ray(bodyEye, transform.forward);
            int count = Physics.RaycastNonAlloc(ray, sightHits, .48f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            RaycastHit[] hits = sightHits;
            if (count >= sightHits.Length)
            {
                // A full NonAlloc result is unordered, not necessarily blocked:
                // it can contain only the shooter's own weapon/rig colliders.
                // Retry with the already-owned larger buffer on rare dense rays.
                hits = crowdedSightHits;
                count = Physics.RaycastNonAlloc(ray, crowdedSightHits, .48f,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                // Still saturated? Fail closed; do not let any missing wall
                // permit damage or reserve a player attacker slot.
                if (count >= crowdedSightHits.Length) return false;
            }
            for (int i = 0; i < count; i++)
            {
                Collider hit = hits[i].collider;
                if (hit != null && !hit.transform.IsChildOf(transform) &&
                    (intendedVictim == null ||
                     !hit.transform.IsChildOf(intendedVictim)))
                    return false;
            }
            return true;
        }

        private void OnDamaged(AarisOfflineHealth victim, float loss, GameObject attacker)
        {
            if (victim == null || loss <= 0f) return;
            if (victim.IsDead && owner != null)
                owner.ReportRivalElimination(attacker, gameObject);
            if (victim.IsDead || attacker == null || attacker == gameObject) return;
            // Damage callbacks only arrive on real HP loss. Prefer a recent
            // living rival aggressor over a random nearby rival for four seconds.
            // This must never let a player bypass MayAttackPlayer's two slots.
            if (attacker.TryGetComponent<AarisOfflineRival>(out AarisOfflineRival rivalAttacker) &&
                rivalAttacker != this && rivalAttacker.IsAlive)
            {
                recentRivalAttacker = rivalAttacker;
                recentRivalAttackerUntil = Time.time + 4f;
            }
            // Shotguns may report eight damage events in one frame. React to
            // the burst once instead of accumulating 8 * 65 degrees of
            // wandering and an unbounded turnOffset on successive pellets.
            // Subsequent genuine bursts can still provoke another reaction.
            if (Time.time >= nextHitReaction)
            {
                nextHitReaction = Time.time + 0.22f;
                turnOffset += avoidanceHandedness * 65f;
                nextThink = 0f;
            }
            // A cover search is more expensive: retain its 1.5 s cadence.
            // Remote abstract damage must never trigger a physics cover query.
            if (virtualBody || Time.time < nextCoverQuery) return;
            EnsurePhysicalCombatBuffers();
            nextCoverQuery = Time.time + 1.5f;
            // Cover candidates are evaluated in one synchronous physics
            // callback. Snapshot both live poses before the overlap; retain
            // the same collider filtering, costs and native cover raycasts.
            Transform selfTransform = transform;
            Vector3 selfPosition = selfTransform.position;
            Vector3 attackerPosition = attacker.transform.position;
            Vector3 away = selfPosition - attackerPosition;
            away.y = 0f;
            if (away.sqrMagnitude < .01f) return;
            // Only the squared near-zero guard needs 'away'. Its normalized
            // vector was never consumed by cover ranking or raycast.
            Vector3 coverSearchCenter = selfPosition + Vector3.up;
            int count = Physics.OverlapSphereNonAlloc(coverSearchCenter,
                11f, coverCandidates, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);
            Collider[] candidates = coverCandidates;
            if (count >= coverCandidates.Length)
            {
                // NonAlloc overlap hits are unordered. The first 24 may
                // contain only foliage, missing useful building cover.
                candidates = crowdedCoverCandidates;
                count = Physics.OverlapSphereNonAlloc(coverSearchCenter,
                    11f, crowdedCoverCandidates, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);
            }
            float best = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                Collider obstacle = candidates[i];
                if (obstacle == null || obstacle is TerrainCollider ||
                    obstacle is CharacterController ||
                    obstacle.transform.IsChildOf(selfTransform)) continue;
                Bounds bounds = obstacle.bounds;
                if (bounds.size.y < .9f || bounds.size.y > 18f) continue;
                // Only the far side relative to the shooter is valid cover.
                Vector3 coverAxis = bounds.center - attackerPosition;
                coverAxis.y = 0f;
                if (coverAxis.sqrMagnitude < .01f) continue;
                Vector3 candidate = bounds.center + coverAxis.normalized *
                    (Mathf.Max(bounds.extents.x, bounds.extents.z) + 1.1f);
                candidate.y = selfPosition.y;
                float cost = (candidate - selfPosition).sqrMagnitude;
                if (cost < 2f || cost > 160f || cost >= best) continue;
                // A low obstacle must really stop a standing-height shot.
                // Only queried on the existing 1.5s damage/cover cooldown.
                Vector3 coverFrom = attackerPosition + Vector3.up * 1.25f;
                Vector3 coverRay = candidate + Vector3.up - coverFrom;
                float coverDistance = coverRay.magnitude;
                if (coverDistance < .2f ||
                    !obstacle.Raycast(new Ray(coverFrom, coverRay / coverDistance),
                        out RaycastHit blocker, coverDistance)) continue;
                best = cost;
                coverDestination = candidate;
            }
            if (best < float.PositiveInfinity)
                coverUntil = Time.time + 2f;
        }

        internal void TakeZoneDamage(float amount)
        {
            if (health != null && !health.IsDead) health.ApplyDamage(amount, null);
        }

        private void OnDied(AarisOfflineHealth defeated)
        {
            // Unregister dead opponents before destruction delay.
            if (centrallyDormant) SetCentralDormancy(false);
            if (capsule != null) capsule.enabled = false;
            if (owner != null)
            {
                owner.DropRivalLoot(transform.position, carriedGun,
                    carriedMagazine, carriedReserve, medkits, remoteLootSeed);
                owner.RivalDefeated(this);
            }
            // Keep the existing 1.2s corpse lifetime, but remove dead agents
            // from the native Update dispatcher and stop their PlayableGraph.
            // HP, death crate, kill-feed and spatial cleanup already happened.
            if (graph.IsValid()) graph.Stop();
            enabled = false;
            Destroy(gameObject, 1.2f);
        }

        private void OnDestroy()
        {
            if (centrallyDormant && owner != null)
                owner.RegisterDormantRival(spatialSlot, this, false);
            if (owner != null)
                owner.UntrackRivalPosition(spatialSlot, this);
            if (health != null)
            {
                health.Died -= OnDied;
                health.Damaged -= OnDamaged;
            }
            if (graph.IsValid()) graph.Destroy();
        }
    }
}
