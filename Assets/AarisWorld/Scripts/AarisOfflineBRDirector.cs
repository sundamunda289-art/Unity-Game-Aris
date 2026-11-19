using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
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
        private const int RivalCount = 6;
        private const float OceanCutoff = -37.35f;
        private static readonly float[] ZoneRadii = { 1080f, 660f, 360f, 175f, 65f };
        private static readonly float[] CloseStarts = { 45f, 165f, 285f, 400f };
        private static readonly float[] CloseEnds = { 140f, 245f, 360f, 465f };
        private readonly Vector2[] centers = new Vector2[5];
        private readonly AarisOfflineRival[] rivals = new AarisOfflineRival[RivalCount];
        // Successfully populated slots stay claimed even if their rival
        // loses later: retry waves must never resurrect defeated opponents.
        private readonly bool[] populatedSlots = new bool[RivalCount];
        private const int MaximumSpawnPasses = 4;
        private int spawnPasses = 1;
        private readonly Collider[] spawnHits = new Collider[24];
        // Only used when dense authored cover saturates the normal overlap
        // query; an exhaustive allocating fallback remains for >96 hits.
        private readonly Collider[] crowdedSpawnHits = new Collider[96];
        private readonly System.Random random = new System.Random();
        private Transform player;
        private AarisOfflineHealth playerHealth;
        private AarisOfflineDrop drop;
        private AarisOfflineMatchEnd matchEnd;
        private AarisNearbyColliders nearbyColliders;
        private Terrain[] terrains;
        private int attemptedSlots, activeRivals, spawnedRivals;
        private float startedAt, nextSpawn, nextZoneTick, nextDamageTick, nextRingDraw;
        private float radius;
        private Vector2 center;
        private bool started, spawnComplete;
        private Canvas statusCanvas;
        private Text status;
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
                ResourceRequest request =
                    Resources.LoadAsync<GameObject>(RivalVisualResourcePaths[i]);
                yield return request;
                GameObject candidate = request.asset as GameObject;
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
            ResourceRequest idleRequest = Resources.LoadAsync<AnimationClip>(idlePath);
            idleRequest.priority = -1;
            yield return idleRequest;
            var idle = idleRequest.asset as AnimationClip;
            if (IsUsableRivalClip(idle)) prewarmedRivalIdle = idle;
            yield return null;
            ResourceRequest runRequest = Resources.LoadAsync<AnimationClip>(runPath);
            runRequest.priority = -1;
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

        private void Start()
        {
            if (player == null) { enabled = false; return; }
            playerHealth = player.GetComponent<AarisOfflineHealth>();
            if (playerHealth == null)
                playerHealth = player.gameObject.AddComponent<AarisOfflineHealth>();
            matchEnd = player.GetComponent<AarisOfflineMatchEnd>();
            if (matchEnd == null)
                matchEnd = player.gameObject.AddComponent<AarisOfflineMatchEnd>();
            drop = player.GetComponent<AarisOfflineDrop>();
            terrains = Terrain.activeTerrains;
            nearbyColliders = FindFirstObjectByType<AarisNearbyColliders>();
            StartCoroutine(PrewarmRivalVisuals());
        }

        private void Update()
        {
            if (player == null || playerHealth == null || playerHealth.IsDead ||
                matchEnd == null || matchEnd.IsFinished) return;
            if (!started)
            {
                // Runtime boot order is unspecified. Do not start an unseen
                // ground battle while the real player is still parachuting.
                if (drop == null) drop = player.GetComponent<AarisOfflineDrop>();
                if (drop != null && !drop.HasLanded) return;
                // Don't start the match clock while the first character
                // resource is integrating into Unity on constrained phones.
                if (!rivalVisualPrewarmComplete) return;
                Begin();
            }

            float elapsed = Time.time - startedAt;
            if (!spawnComplete && Time.time >= nextSpawn)
            {
                // Exactly one authored encounter attempt per half-second.
                // Avoid a burst of costly Humanoid imports on 2GB phones.
                int slot = attemptedSlots++;
                if (!populatedSlots[slot]) TrySpawnSlot(slot);
                nextSpawn = Time.time + 0.5f;
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

        private void Begin()
        {
            started = true;
            startedAt = Time.time;
            nextSpawn = Time.time + 0.6f;
            terrains = Terrain.activeTerrains;
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
            float sectorAngle = Mathf.PI * 2f * (index + 0.5f) / RivalCount;
            for (int attempt = 0; attempt < 9; attempt++)
            {
                float angle = sectorAngle + (float)(random.NextDouble() - .5) * .85f;
                float distance = 72f + (float)random.NextDouble() * 80f;
                float x = player.position.x + Mathf.Cos(angle) * distance;
                float z = player.position.z + Mathf.Sin(angle) * distance;
                if (!TryGetGround(x, z, out float ground)) continue;
                Vector3 point = new Vector3(x, ground + 0.08f, z);
                if (!SpawnClear(point)) continue;

                var actor = new GameObject("Aaris Offline Rival " + (index + 1));
                actor.transform.SetPositionAndRotation(point,
                    Quaternion.Euler(0f, (float)random.NextDouble() * 360f, 0f));
                var capsule = actor.AddComponent<CharacterController>();
                capsule.height = 1.8f;
                capsule.radius = 0.31f;
                capsule.center = new Vector3(0f, 0.9f, 0f);
                capsule.stepOffset = 0.28f;
                capsule.skinWidth = 0.05f;
                capsule.slopeLimit = 43f;
                var health = actor.AddComponent<AarisOfflineHealth>();
                var rival = actor.AddComponent<AarisOfflineRival>();
                rival.Initialize(this, health, player, playerHealth);
                rivals[index] = rival;
                populatedSlots[index] = true;
                activeRivals++;
                spawnedRivals++;
                return;
            }
        }

        private bool TryGetGround(float x, float z, out float height)
        {
            height = 0f;
            if (terrains == null || terrains.Length == 0) return false;
            for (int i = 0; i < terrains.Length; i++)
            {
                Terrain tile = terrains[i];
                if (tile == null || tile.terrainData == null) continue;
                Vector3 origin = tile.transform.position, size = tile.terrainData.size;
                if (x < origin.x + 2f || x > origin.x + size.x - 2f ||
                    z < origin.z + 2f || z > origin.z + size.z - 2f) continue;
                float u = (x - origin.x) / size.x;
                float v = (z - origin.z) / size.z;
                if (tile.terrainData.GetInterpolatedNormal(u, v).y < 0.78f) continue;
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
        internal bool WouldCrossInactiveCover(Vector3 from, Vector3 displacement)
        {
            if (nearbyColliders == null || displacement.sqrMagnitude < 0.000001f)
                return false;
            Vector3 lookAhead = displacement.normalized *
                Mathf.Max(displacement.magnitude, 0.72f);
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

        internal void RivalDefeated(AarisOfflineRival rival)
        {
            if (activeRivals > 0) activeRivals--;
            if (spawnComplete && spawnedRivals > 0 && activeRivals == 0 &&
                playerHealth != null && !playerHealth.IsDead &&
                matchEnd != null && !matchEnd.IsFinished)
                matchEnd.WinMatch();
        }

        internal bool CanEngage => started && !playerHealth.IsDead &&
            matchEnd != null && !matchEnd.IsFinished;

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
        private AarisOfflineBRDirector owner;
        private AarisOfflineHealth health, targetHealth;
        private Transform target;
        private CharacterController capsule;
        private readonly RaycastHit[] sightHits = new RaycastHit[20];
        // Kept per rival: retain correct closest-visible-target selection
        // without RaycastAll allocations when >20 cover hits overlap.
        private readonly RaycastHit[] crowdedSightHits = new RaycastHit[128];
        private float nextThink, nextAttack, verticalSpeed, turnOffset, visualBlend;
        // Outside the combat range, save mobile CPU by updating far capsules
        // at 10 Hz. Keep near opponents at full gameplay frame rate.
        private const float FarSimulationDistance = 90f;
        private const float FarSimulationInterval = 0.10f;
        // Sparse far ticks can span two frames on a 15 FPS phone; do not
        // truncate ordinary 133 ms travel, but cap extreme app-resume hitches.
        private float nextFarSimulation, lastSimulationTime;
        private float avoidanceHandedness;
        private bool canSee;
        private PlayableGraph graph;
        private AnimationMixerPlayable animations;
        private bool hasAnimations;
        public bool IsAlive => health != null && !health.IsDead;

        internal void Initialize(AarisOfflineBRDirector director, AarisOfflineHealth self,
            Transform victim, AarisOfflineHealth victimHealth)
        {
            owner = director;
            health = self;
            target = victim;
            targetHealth = victimHealth;
            capsule = GetComponent<CharacterController>();
            health.Died += OnDied;
            turnOffset = UnityEngine.Random.Range(-12f, 12f);
            avoidanceHandedness = UnityEngine.Random.value < 0.5f ? 1f : -1f;
            lastSimulationTime = Time.time;
            nextFarSimulation = Time.time;
            BuildVisual();
        }

        private void BuildVisual()
        {
            // The bundled CC0 humanoid is shared by all rivals. Avoid six
            // full hero-quality tactical rigs on entry-level 2GB devices.
            if (owner == null) return;
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
            var animator = visual.GetComponentInChildren<Animator>(true);
            if (animator == null) animator = visual.AddComponent<Animator>();
            if (animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
                animator.avatar = cachedAvatar;
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
            // A failed Avatar import must not hide a valid 3D model.
            if (animator.avatar == null || !animator.avatar.isValid ||
                !animator.avatar.isHuman || idle == null || run == null) return;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
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

        private void BuildFallbackSilhouette()
        {
            // This is a visible last resort, not a replacement for the
            // bundled realistic models. It adds no secondary physics collider.
            var fallback = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            fallback.name = "Rival fallback silhouette";
            fallback.transform.SetParent(transform, false);
            fallback.transform.localPosition = Vector3.up * .90f;
            fallback.transform.localScale = new Vector3(.62f, .9f, .62f);
            Collider collider = fallback.GetComponent<Collider>();
            if (collider != null) { collider.enabled = false; Destroy(collider); }
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
            if (owner == null || !owner.CanEngage || !IsAlive ||
                target == null || targetHealth == null || targetHealth.IsDead) return;
            Vector3 diff = target.position - transform.position;
            diff.y = 0f;
            float distance = diff.magnitude;
            // Distant rivals cannot see (42m) or attack (38m) the player.
            // Keep collision, cover avoidance and zone retreat simulated,
            // without paying for every far-away physics query each frame.
            bool farSimulation = distance >= FarSimulationDistance;
            if (farSimulation && Time.time < nextFarSimulation) return;
            // Preserve the native-frame near response, including low-FPS pacing.
            float dt = Mathf.Min(Time.deltaTime, 0.12f);
            if (farSimulation)
                dt = Mathf.Clamp(Time.time - lastSimulationTime, 0f, 0.22f);
            lastSimulationTime = Time.time;
            if (farSimulation)
                nextFarSimulation = Time.time + FarSimulationInterval;
            Vector3 toward = distance > .01f ? diff / distance : transform.forward;
            if (Time.time >= nextThink)
            {
                nextThink = Time.time + .23f + UnityEngine.Random.Range(0f, .09f);
                canSee = distance <= 42f && HasSight();
                if (!canSee && distance < 9f) turnOffset = UnityEngine.Random.Range(-55f, 55f);
            }
            bool outsideSafeArea = owner.TryZoneRetreatDirection(transform.position,
                out Vector3 retreatDirection);
            // Nearby player contact retains combat priority; distant bots
            // should not ignore a closing circle just to chase the player.
            bool retreat = outsideSafeArea && (distance > 23f || !canSee);
            Vector3 travelDirection = retreat ? retreatDirection : toward;
            float moveSpeed = retreat ? 3.2f :
                distance > 12f ? 2.85f : distance < 7f ? -1.15f : 0f;
            Vector3 desired = Quaternion.Euler(0f, turnOffset, 0f) *
                travelDirection * moveSpeed;
            if (canSee && !retreat) desired *= .23f; // Hold to fire without skating.
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
                if (!owner.WouldCrossInactiveCover(transform.position, first))
                    displacement = first;
                else if (!owner.WouldCrossInactiveCover(transform.position, second))
                    displacement = second;
                else
                    displacement = Vector3.zero;
            }
            // Follow the direction of actual planned movement while avoiding
            // remote cover or retreating to safety. Otherwise occluded rivals
            // moonwalk sideways/backwards while running across open terrain.
            // Keep eyes/body on the player during clear, active combat.
            // Rotation only: capsule collision and shot/zone gates are unchanged.
            Vector3 facing = distance > .05f ? toward : transform.forward;
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
            if ((hit & CollisionFlags.Sides) != 0 && !canSee)
                turnOffset = -turnOffset + UnityEngine.Random.Range(-28f, 28f);

            if (hasAnimations)
            {
                // Animate based on *actual* capsule travel. Against a house
                // or blocked corner the rival must not sprint in place.
                Vector3 realTravel = transform.position - beforeMove;
                realTravel.y = 0f;
                float groundSpeed = realTravel.magnitude / Mathf.Max(0.001f, dt);
                float wanted = Mathf.Clamp01(groundSpeed / 2.9f);
                UpdateAnimationBlend(wanted, dt);
            }
            if (canSee && distance <= 38f &&
                Vector3.Dot(transform.forward, toward) > .84f &&
                Time.time >= nextAttack && HasSight())
            {
                // Offline damage contract, not real-world ballistics.
                nextAttack = Time.time + UnityEngine.Random.Range(1.15f, 1.65f);
                AarisAudioDirector.GunShot("SMG", "Rival", transform.position + Vector3.up);
                targetHealth.ApplyDamage(6f, gameObject);
            }
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

        private bool HasSight()
        {
            Vector3 eye = transform.position + transform.forward * .48f + Vector3.up * 1.28f;
            Vector3 to = target.position + Vector3.up * 1.07f - eye;
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
                    // Preserve exhaustive visibility on pathological overlap:
                    // never treat an incomplete ray as line of sight.
                    hits = Physics.RaycastAll(ray, distance,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    count = hits.Length;
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
                { first = hit.distance; closest = hit.collider; }
            }
            return closest != null && closest.transform.IsChildOf(target);
        }

        internal void TakeZoneDamage(float amount)
        {
            if (health != null && !health.IsDead) health.ApplyDamage(amount, null);
        }

        private void OnDied(AarisOfflineHealth defeated)
        {
            if (capsule != null) capsule.enabled = false;
            if (owner != null) owner.RivalDefeated(this);
            Destroy(gameObject, 1.2f);
        }

        private void OnDestroy()
        {
            if (health != null) health.Died -= OnDied;
            if (graph.IsValid()) graph.Destroy();
        }
    }
}
