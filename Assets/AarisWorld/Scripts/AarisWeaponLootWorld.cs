using System;
using System.Collections.Generic;
using UnityEngine;

namespace Aaris.MapReview
{
    [Serializable]
    internal sealed class AarisLootCandidate
    {
        public string kind, origin;
        public float x, y, z;
        public int slot;
    }

    [Serializable]
    internal sealed class AarisLootDocument
    {
        public int schema, sizeMetres;
        public AarisLootCandidate[] candidates;
    }

    /// <summary>
    /// A physical dropped gun retains magazine/reload state even when its
    /// mesh is streamed out at distance. Negative indices never overlap
    /// with authored world candidates.
    /// </summary>
    internal sealed class AarisDroppedWeapon
    {
        public int id;
        public AarisWeaponState state;
        public Vector3 position;
        public float blockedUntil, nextVisualAttemptAt;
        public AarisWeaponPickup visible;
    }

    /// <summary>
    /// Author-baked points + match-seeded, weighted loot. Only nearby areas
    /// become actual GameObjects, AFTER AarisNearbyColliders has enabled local
    /// building floors and walls. This avoids a 2x2km physics scan on boot.
    /// </summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(-80)]
    public sealed class AarisWeaponLootWorld : MonoBehaviour
    {
        private const float ActivationMetres = 48f; // below the 55m collider streaming radius
        private const float ReleaseMetres = 75f;
        private const int MaximumActiveLoot = 40;
        private const int StreamFramesPerRefresh = 3;
        private const int MaximumBuildAttemptsPerFrame = 2;
        private const int MaximumCandidateProbesPerFrame = 6;
        private const float RetryDelaySeconds = 3f;
        private const int MaximumDroppedLoot = 320;
        private const float PickupDistance = 2.15f;
        private const float StarterLootRange = 10f;
        private const float SeaLevel = -38.07552f;
        private const float CandidateCellMetres = 64f;

        private readonly List<AarisWeaponPickup> active = new List<AarisWeaponPickup>(32);
        private readonly List<AarisDroppedWeapon> dropped = new List<AarisDroppedWeapon>(16);
        private readonly RaycastHit[] pickupObstructions = new RaycastHit[24];
        private readonly RaycastHit[] dropFloorHits = new RaycastHit[32];
        private readonly RaycastHit[] candidateFloorHits = new RaycastHit[32];
        private readonly Collider[] dropBlockers = new Collider[32];
        // Immutable map candidate slots stay indexed, while only nearby cells
        // are considered for instantiation each 330ms. No world/loot state is
        // discarded when the player moves out of range.
        private readonly Dictionary<long, List<int>> candidateCells =
            new Dictionary<long, List<int>>();
        private readonly List<int> nearbyCandidateIndices = new List<int>(32);
        private AarisLootCandidate[] candidates;
        private AarisWeaponSpec[] choices;
        private bool[] consumed;
        private float[] nextCandidateProbeAt;
        private AarisWeaponInventory inventory;
        private Transform player;
        private float nextRefresh, requestPickupUntil;
        private int streamFramesRemaining;
        private int nextDropId = -1;
        private bool allowInitialAutoPickup = true;
        private bool starterLootSettled;
        private uint rng, matchSeed;
        public bool HasPickupInRange { get; private set; }

        /// <summary>Explicit pickup replaces the single carried gun.</summary>
        public void RequestPickup() => requestPickupUntil = Time.unscaledTime + 0.7f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            var motor = FindFirstObjectByType<AarisThirdPersonPlayer>();
            if (motor == null || FindFirstObjectByType<AarisWeaponLootWorld>() != null) return;
            var inventory = motor.GetComponent<AarisWeaponInventory>();
            if (inventory == null) inventory = motor.gameObject.AddComponent<AarisWeaponInventory>();
            var root = new GameObject("Aaris Match Weapon Loot (offline, streamed)");
            var world = root.AddComponent<AarisWeaponLootWorld>();
            world.player = motor.transform;
            world.inventory = inventory;
            world.Initialize();
            if (world.enabled) inventory.BindLootWorld(world);
        }

        private void Initialize()
        {
            if (!AarisWeaponCatalog.TryLoad())
            {
                enabled = false;
                return;
            }
            TextAsset text = Resources.Load<TextAsset>("AarisWeapons/loot_candidates");
            if (text == null)
            {
                Debug.LogError("Aaris: no embedded offline authored loot locations.");
                enabled = false;
                return;
            }
            AarisLootDocument doc;
            try { doc = JsonUtility.FromJson<AarisLootDocument>(text.text); }
            catch (Exception e)
            {
                Debug.LogError("Aaris: invalid loot JSON: " + e.Message);
                enabled = false;
                return;
            }
            if (doc == null || doc.schema != 1 || doc.sizeMetres != 2000 ||
                doc.candidates == null || doc.candidates.Length < 50)
            {
                Debug.LogError("Aaris: loot candidates do not describe the 2km map.");
                enabled = false;
                return;
            }
            // Different each match; deterministic once seed is chosen. A
            // future authoritative server can provide this seed to clients.
            matchSeed = unchecked((uint)DateTime.UtcNow.Ticks) ^
                unchecked((uint)Environment.TickCount) ^ 0x59371FA3u;
            rng = matchSeed;

            // 4 tiles hold around 7,500 native trees. Sample just a few
            // distributed tree cover nodes ONCE at match boot, never every
            // frame and never one loot item for every vegetation instance.
            var allCandidates = new List<AarisLootCandidate>(doc.candidates);
            AppendNativeTreeCover(allCandidates);
            candidates = allCandidates.ToArray();
            consumed = new bool[candidates.Length];
            nextCandidateProbeAt = new float[candidates.Length];
            choices = new AarisWeaponSpec[candidates.Length];
            int selected = 0;
            for (int i = 0; i < candidates.Length; i++)
            {
                var c = candidates[i];
                // Apply the probability to ALL sectors. An early cap on
                // sorted candidates unfairly stocked the west side first
                // and left the east half of the 2km map with no outdoor loot.
                // Memory is bounded by 514 authored slots; instantiated
                // GameObjects are separately capped at MaximumActiveLoot.
                if (!WithinMap(c)) continue;
                bool indoor = c.kind == "indoor";
                float chance = indoor ? 0.72f :
                    c.kind == "tree" ? 0.75f : 0.32f;
                if (AarisWeaponRandom.Next01(ref rng) > chance) continue;
                var spec = AarisWeaponCatalog.Select(ref rng, indoor);
                if (spec == null) continue;
                choices[i] = spec;
                selected++;
            }
            IndexPlannedCandidates();
            Debug.Log("Aaris: match seed " + matchSeed + ", planned " + selected +
                " offline pickups (actual ground/collision validated near player).");
        }

        private static int CandidateCell(float coordinate) =>
            Mathf.FloorToInt(coordinate / CandidateCellMetres);
        private static long CandidateCellKey(int x, int z) =>
            ((long)x << 32) ^ (uint)z;

        private void IndexPlannedCandidates()
        {
            candidateCells.Clear();
            for (int i = 0; i < candidates.Length; i++)
            {
                if (choices[i] == null) continue;
                var c = candidates[i];
                long key = CandidateCellKey(CandidateCell(c.x), CandidateCell(c.z));
                if (!candidateCells.TryGetValue(key, out List<int> cell))
                {
                    cell = new List<int>(8);
                    candidateCells.Add(key, cell);
                }
                cell.Add(i);
            }
        }

        private void CollectNearbyCandidateIndices(Vector3 center)
        {
            nearbyCandidateIndices.Clear();
            // Expanded cell range includes points in all adjacent cells near
            // boundaries; final precise 42m circle check remains unchanged.
            int firstX = CandidateCell(center.x - ActivationMetres);
            int lastX = CandidateCell(center.x + ActivationMetres);
            int firstZ = CandidateCell(center.z - ActivationMetres);
            int lastZ = CandidateCell(center.z + ActivationMetres);
            for (int x = firstX; x <= lastX; x++)
                for (int z = firstZ; z <= lastZ; z++)
                    if (candidateCells.TryGetValue(CandidateCellKey(x, z), out List<int> cell))
                        nearbyCandidateIndices.AddRange(cell);
            // Preserve spawn priority (original candidate index order)
            // while distributing construction across frames.
            nearbyCandidateIndices.Sort();
        }

        private void AppendNativeTreeCover(List<AarisLootCandidate> result)
        {
            foreach (var terrain in Terrain.activeTerrains)
            {
                if (terrain == null || terrain.terrainData == null) continue;
                var data = terrain.terrainData;
                // TreeInstance[] is requested ONCE per tile, not once per
                // update. The saved map currently contains ~7.5k instances
                // in total. Keep the sample budget fixed for mobile memory.
                TreeInstance[] trees = data.treeInstances;
                if (trees == null || trees.Length == 0) continue;
                int accepted = 0, attempts = 0;
                Vector3 origin = terrain.transform.position;
                Vector3 size = data.size;
                while (accepted < 12 && attempts++ < 72)
                {
                    int idx = (int)(AarisWeaponRandom.Next(ref rng) % (uint)trees.Length);
                    Vector3 tree = origin + Vector3.Scale(trees[idx].position, size);
                    float angle = AarisWeaponRandom.Next01(ref rng) * Mathf.PI * 2f;
                    float distance = 2f + AarisWeaponRandom.Next01(ref rng) * 7f;
                    float x = tree.x + Mathf.Cos(angle) * distance;
                    float z = tree.z + Mathf.Sin(angle) * distance;
                    if (x < -955f || x > 955f || z < -955f || z > 955f) continue;
                    bool nearExistingTreeCandidate = false;
                    foreach (var c in result)
                    {
                        if (c.kind != "tree") continue;
                        float dx = c.x - x, dz = c.z - z;
                        if (dx * dx + dz * dz < 32f * 32f)
                        {
                            nearExistingTreeCandidate = true;
                            break;
                        }
                    }
                    if (nearExistingTreeCandidate) continue;
                    result.Add(new AarisLootCandidate
                    {
                        kind = "tree", x = x, y = 0f, z = z,
                        origin = "native Terrain tree", slot = idx
                    });
                    accepted++;
                }
            }
        }

        private static bool WithinMap(AarisLootCandidate c) =>
            c != null && c.x >= -957f && c.x <= 957f &&
            c.z >= -957f && c.z <= 957f;

        private static Terrain TerrainAt(float x, float z, Terrain[] terrains)
        {
            foreach (var t in terrains)
            {
                if (t == null || t.terrainData == null) continue;
                Vector3 p = t.transform.position;
                Vector3 s = t.terrainData.size;
                if (x >= p.x && x <= p.x + s.x && z >= p.z && z <= p.z + s.z)
                    return t;
            }
            return null;
        }

        private bool TryResolve(AarisLootCandidate c, Terrain[] terrains, out Vector3 spot)
        {
            spot = default(Vector3);
            var terrain = TerrainAt(c.x, c.z, terrains);
            if (terrain == null) return false;
            bool indoor = c.kind == "indoor";
            // For outdoor positions use the native Terrain surface, NOT
            // the first arbitrary roof encountered from the sky.
            // Terrain.SampleHeight is already in world-space Y; do not add tile Y twice.
            // Indoor locations retain the authored floor height.
            float expectedGround = indoor ? c.y :
                terrain.SampleHeight(new Vector3(c.x, 0f, c.z));
            if (expectedGround <= SeaLevel + 0.7f) return false;
            Vector3 start = new Vector3(c.x, expectedGround + 1.9f, c.z);
            // A single down-ray can stop on the roof of a low house before
            // reaching its floor. Inspect ALL bounded hits and select the
            // supporting surface nearest the authored ground elevation.
            int hits = Physics.RaycastNonAlloc(new Ray(start, Vector3.down),
                candidateFloorHits, 3.3f, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);
            RaycastHit[] floors = candidateFloorHits;
            if (hits >= candidateFloorHits.Length)
            {
                // Only dense scenes use the allocating full query.
                floors = Physics.RaycastAll(new Ray(start, Vector3.down),
                    3.3f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                hits = floors.Length;
            }
            RaycastHit floor = default(RaycastHit);
            bool foundFloor = false;
            float closestHeight = .85f;
            for (int i = 0; i < hits; i++)
            {
                RaycastHit candidate = floors[i];
                if (candidate.collider == null ||
                    candidate.normal.y < (indoor ? .966f : .94f) ||
                    (!indoor && !(candidate.collider is TerrainCollider) &&
                     (candidate.collider.attachedRigidbody != null ||
                      Mathf.Abs(candidate.point.y - expectedGround) > .35f)))
                    continue;
                float difference = Mathf.Abs(candidate.point.y - expectedGround);
                if (difference >= closestHeight) continue;
                floor = candidate;
                closestHeight = difference;
                foundFloor = true;
            }
            if (!foundFloor) return false;

            if (indoor)
            {
                // AABB alone NEVER proves that an area has an interior.
                // Require a physical ceiling and a flat solid support.
                Vector3 above = floor.point + Vector3.up * 0.78f;
                if (!Physics.Raycast(above, Vector3.up, out RaycastHit roof, 15f,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    return false;
                if (roof.collider is TerrainCollider) return false;
                if (roof.distance < 1.05f) return false; // unusably low roof
            }
            // Outdoor static mesh floors must be within 35cm of the sampled
            // terrain, excluding elevated roof loot and moving props.

            Vector3 center = floor.point + Vector3.up * 0.43f;
            if (Physics.CheckCapsule(center + Vector3.down * .05f,
                center + Vector3.up * .25f, .19f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return false; // no inside-wall or furniture placement

            spot = floor.point + Vector3.up * .13f;
            return true;
        }

        private bool CanReachPickup(AarisWeaponPickup pickup) => CanReachPoint(pickup.transform.position);

        private bool CanReachPoint(Vector3 position)
        {
            // Range is not enough: a weapon behind a solid wall must not be
            // collected through that wall. Ignore only the player's own
            // colliders, not all colliders on the player layer.
            Vector3 eye = player.position + Vector3.up * .95f;
            Vector3 target = position + Vector3.up * .24f;
            Vector3 toTarget = target - eye;
            float distance = toTarget.magnitude;
            if (distance <= 0.001f) return true;
            int hits = Physics.RaycastNonAlloc(new Ray(eye, toTarget / distance),
                pickupObstructions, distance,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            RaycastHit[] sightHits = pickupObstructions;
            if (hits >= pickupObstructions.Length)
            {
                // A truncated ray can omit the wall in front of the loot.
                // Keep full occlusion checks, but allocate only on saturation.
                sightHits = Physics.RaycastAll(new Ray(eye, toTarget / distance),
                    distance, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);
                hits = sightHits.Length;
            }
            for (int i = 0; i < hits; i++)
            {
                var collider = sightHits[i].collider;
                if (collider == null || collider.transform.IsChildOf(player)) continue;
                return false;
            }
            return true;
        }

        private AarisDroppedWeapon FindDrop(int id)
        {
            foreach (var record in dropped)
                if (record.id == id) return record;
            return null;
        }

        private void DeleteDrop(int id)
        {
            for (int i = dropped.Count - 1; i >= 0; i--)
                if (dropped[i].id == id) { dropped.RemoveAt(i); return; }
        }

        private AarisDroppedWeapon CreateDropped(AarisWeaponState state,
            Vector3 point, float graceSeconds, bool deferVisual = false)
        {
            if (state == null || state.Spec == null ||
                dropped.Count >= MaximumDroppedLoot) return null;
            int id = nextDropId--;
            var record = new AarisDroppedWeapon
            {
                id = id, state = state, position = point,
                blockedUntil = Time.unscaledTime + graceSeconds
            };
            // During an exchange, defer the previous gun mesh to the next
            // bounded loot-stream tick. This avoids two FBX Instantiates
            // during the same mobile frame as the new equipped model.
            if (!deferVisual)
            {
                var root = new GameObject("Dropped " + state.Spec.displayName + " #" + (-id));
                root.transform.SetParent(transform, false);
                root.transform.position = point;
                var pickup = root.AddComponent<AarisWeaponPickup>();
                if (!pickup.Initialize(state, id))
                {
                    Destroy(root);
                    return null;
                }
                record.visible = pickup;
                active.Add(pickup);
            }
            dropped.Add(record);
            if (Debug.isDebugBuild)
                Debug.Log("AARIS_LOOT_DROPPED " + state.Spec.id +
                    " pos=" + point.ToString("F2"));
            return record;
        }

        private void UndoDrop(AarisDroppedWeapon record)
        {
            if (record == null) return;
            if (record.visible != null)
            {
                active.Remove(record.visible);
                Destroy(record.visible.gameObject);
            }
            DeleteDrop(record.id);
        }

        private bool TryFindDropSpot(out Vector3 position, bool requireReachable = false)
        {
            position = Vector3.zero;
            if (player == null) return false;
            Vector3 forward = player.forward;
            Vector3 right = player.right;
            // Fixed four positions: no managed Vector3[] allocation per drop.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                Vector3 place = player.position + (attempt == 0 ? forward :
                    attempt == 1 ? -forward : attempt == 2 ? right : -right) * .65f;
                if (place.x < -957f || place.x > 957f ||
                    place.z < -957f || place.z > 957f) continue;
                Ray ray = new Ray(place + Vector3.up * 1.6f, Vector3.down);
                int count = Physics.RaycastNonAlloc(ray, dropFloorHits, 3.5f,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                RaycastHit[] floorHits = dropFloorHits;
                if (count >= dropFloorHits.Length)
                {
                    floorHits = Physics.RaycastAll(ray, 3.5f,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    count = floorHits.Length;
                }
                float best = float.PositiveInfinity;
                Collider floor = null;
                Vector3 hitPoint = Vector3.zero;
                for (int i = 0; i < count; i++)
                {
                    var hit = floorHits[i];
                    if (hit.collider == null ||
                        hit.collider.transform.IsChildOf(player) ||
                        hit.normal.y < 0.83f || hit.distance >= best ||
                        hit.point.y <= SeaLevel + .7f ||
                        Mathf.Abs(hit.point.y - player.position.y) > .75f)
                        continue; // Do not throw a gun off a roof/cliff below the player.
                    floor = hit.collider;
                    hitPoint = hit.point;
                    best = hit.distance;
                }
                if (floor == null) continue;
                Vector3 middle = hitPoint + Vector3.up * .44f;
                int blockers = Physics.OverlapCapsuleNonAlloc(
                    middle + Vector3.down * .06f, middle + Vector3.up * .19f,
                    .22f, dropBlockers, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);
                Collider[] obstructionHits = dropBlockers;
                if (blockers >= dropBlockers.Length)
                {
                    obstructionHits = Physics.OverlapCapsule(
                        middle + Vector3.down * .06f, middle + Vector3.up * .19f,
                        .22f, Physics.DefaultRaycastLayers,
                        QueryTriggerInteraction.Ignore);
                    blockers = obstructionHits.Length;
                }
                bool clear = true;
                for (int i = 0; i < blockers; i++)
                {
                    var obstacle = obstructionHits[i];
                    if (obstacle == null || obstacle == floor ||
                        obstacle.transform.IsChildOf(player)) continue;
                    clear = false;
                    break;
                }
                if (!clear) continue;
                Vector3 candidate = hitPoint + Vector3.up * .13f;
                if (requireReachable && !CanReachPoint(candidate)) continue;
                position = candidate;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Preserve the equipped gun on validated ground before removing it.
        /// If no safe floor is found, keep the gun in hand; never delete it.
        /// </summary>
        public bool TryDropEquipped()
        {
            if (inventory == null || !inventory.HasGun ||
                !TryFindDropSpot(out Vector3 point)) return false;
            var state = inventory.Current.CaptureState();
            var record = CreateDropped(state, point, 1.25f);
            if (record == null) return false;
            if (!inventory.TryRemove(out AarisWeaponState removed))
            {
                UndoDrop(record);
                return false;
            }
            requestPickupUntil = 0f;
            // A voluntary DROP means the player intends to be unarmed.
            // Do not automatically equip another floor gun a frame later.
            allowInitialAutoPickup = false;
            return true;
        }

        private bool IsBlockedDrop(AarisWeaponPickup pickup)
        {
            if (pickup.CandidateIndex >= 0) return false;
            var record = FindDrop(pickup.CandidateIndex);
            return record == null || Time.unscaledTime < record.blockedUntil;
        }

        private bool TryTake(AarisWeaponPickup pickup)
        {
            if (pickup == null || pickup.State == null || inventory == null ||
                IsBlockedDrop(pickup) || !pickup.TryReserve()) return false;

            // Reserve the previous gun at the location already proven to be
            // a reachable, clear loot spot. This prevents inventing a second
            // floor position inside a wall while switching weapons.
            var outgoing = inventory.Current != null
                ? inventory.Current.CaptureState() : null;
            var replacement = outgoing != null
                ? CreateDropped(outgoing, pickup.transform.position, 1.25f,
                    deferVisual: true) : null;
            if (outgoing != null && replacement == null)
            {
                pickup.ReleaseReservation();
                return false;
            }

            if (!inventory.TryPickup(pickup.State, out AarisWeaponState exchanged))
            {
                UndoDrop(replacement);
                pickup.ReleaseReservation();
                return false;
            }

            // Commit the loot transaction only after the new gun is held.
            int id = pickup.CandidateIndex;
            if (id >= 0)
                consumed[id] = true;
            else
                DeleteDrop(id);
            pickup.CommitConsumed();
            active.Remove(pickup);
            Destroy(pickup.gameObject);
            requestPickupUntil = 0f;
            HasPickupInRange = false;
            if (Debug.isDebugBuild)
                Debug.Log("AARIS_LOOT_COLLECTED " + inventory.Current.DisplayName +
                    " from=" + id + " saved_old=" + (exchanged != null));
            return true;
        }

        private void Update()
        {
            if (player == null || inventory == null || candidates == null) return;
            float now = Time.unscaledTime;
            Vector3 playerPosition = player.position;
            if (now >= nextRefresh)
            {
                nextRefresh = now + .33f;
                streamFramesRemaining = RefreshPickups(playerPosition, now) ? 0 : StreamFramesPerRefresh;
            }
            if (streamFramesRemaining == 0) return;

            streamFramesRemaining--;
            if (!StreamNearby(playerPosition, now))
            {
                streamFramesRemaining = 0;
                EnsureStarterLoot(playerPosition);
            }
        }

        private bool RefreshPickups(Vector3 playerPosition, float now)
        {
            float releaseSquared = ReleaseMetres * ReleaseMetres;
            float pickupSquared = PickupDistance * PickupDistance;
            bool explicitPickup = now <= requestPickupUntil;
            AarisWeaponPickup bestPickup = null;
            float bestDistance = pickupSquared;

            // Unload far visuals, never their planned match state.
            for (int i = active.Count - 1; i >= 0; i--)
            {
                var pickup = active[i];
                if (pickup == null)
                {
                    active.RemoveAt(i);
                    continue;
                }
                float d2 = (pickup.transform.position - playerPosition).sqrMagnitude;
                if (d2 > releaseSquared)
                {
                    if (pickup.CandidateIndex < 0)
                    {
                        var rec = FindDrop(pickup.CandidateIndex);
                        if (rec != null) rec.visible = null;
                    }
                    active.RemoveAt(i);
                    Destroy(pickup.gameObject);
                    continue;
                }
                if (d2 <= bestDistance && !IsBlockedDrop(pickup) &&
                    CanReachPickup(pickup))
                {
                    bestPickup = pickup;
                    bestDistance = d2;
                }
            }
            HasPickupInRange = bestPickup != null;
            return bestPickup != null &&
                (explicitPickup || (!inventory.HasGun && allowInitialAutoPickup &&
                    bestPickup.CandidateIndex >= 0)) && TryTake(bestPickup);
        }

        private bool StreamNearby(Vector3 playerPosition, float now)
        {
            if (active.Count >= MaximumActiveLoot) return false;
            float loadSquared = ActivationMetres * ActivationMetres;
            int buildAttempts = 0;

            // Stream dropped guns first; they retain ammo and do not reroll.
            foreach (var record in dropped)
            {
                if (record.visible != null) continue;
                if (now < record.nextVisualAttemptAt) continue;
                float dist = (record.position - playerPosition).sqrMagnitude;
                if (dist > loadSquared) continue;
                if (active.Count >= MaximumActiveLoot) return false;
                if (buildAttempts == MaximumBuildAttemptsPerFrame) return true;
                buildAttempts++;
                var root = new GameObject("Dropped " + record.state.Spec.displayName);
                root.transform.SetParent(transform, false);
                root.transform.position = record.position;
                var pickup = root.AddComponent<AarisWeaponPickup>();
                if (!pickup.Initialize(record.state, record.id))
                {
                    record.nextVisualAttemptAt = now + RetryDelaySeconds;
                    Destroy(root);
                    continue;
                }
                record.visible = pickup;
                active.Add(pickup);
            }

            int probes = 0;
            Terrain[] terrains = null;
            CollectNearbyCandidateIndices(playerPosition);
            foreach (int i in nearbyCandidateIndices)
            {
                if (active.Count >= MaximumActiveLoot) return false;
                if (choices[i] == null || consumed[i] || IsCurrentlyActive(i)) continue;
                var c = candidates[i];
                float dx = c.x - playerPosition.x, dz = c.z - playerPosition.z;
                if (dx * dx + dz * dz >= loadSquared) continue;
                if (now < nextCandidateProbeAt[i]) continue;
                if (buildAttempts == MaximumBuildAttemptsPerFrame ||
                    probes == MaximumCandidateProbesPerFrame) return true;

                probes++;
                terrains ??= Terrain.activeTerrains;
                if (!TryResolve(c, terrains, out Vector3 spot))
                {
                    nextCandidateProbeAt[i] = now + RetryDelaySeconds;
                    continue;
                }
                buildAttempts++;
                var root = new GameObject("Loot " + choices[i].displayName + " #" + i);
                root.transform.SetParent(transform, false);
                root.transform.position = spot;
                var pickup = root.AddComponent<AarisWeaponPickup>();
                var state = new AarisWeaponState(choices[i],
                    matchSeed ^ (uint)(i + 1));
                if (!pickup.Initialize(state, i))
                {
                    nextCandidateProbeAt[i] = now + RetryDelaySeconds;
                    Destroy(root);
                    continue;
                }
                active.Add(pickup);
                if (Debug.isDebugBuild)
                    Debug.Log("AARIS_LOOT_PLACED " + choices[i].id +
                        " kind=" + c.kind + " origin=" + c.origin +
                        " index=" + i + " pos=" + spot.ToString("F2"));
            }

            // Leave a starter fallback for a frame with a construction slot.
            return buildAttempts == MaximumBuildAttemptsPerFrame;
        }

        private void EnsureStarterLoot(Vector3 playerPosition)
        {
            if (starterLootSettled || !allowInitialAutoPickup || active.Count >= MaximumActiveLoot)
                return;
            if (inventory.HasGun)
            {
                starterLootSettled = true;
                return;
            }
            if (player.TryGetComponent(out AarisOfflineDrop flight) && !flight.HasLanded) return;
            if (!player.TryGetComponent(out CharacterController controller) || !controller.isGrounded) return;

            foreach (var pickup in active)
            {
                if (pickup != null && !IsBlockedDrop(pickup) &&
                    (pickup.transform.position - playerPosition).sqrMagnitude <= StarterLootRange * StarterLootRange &&
                    CanReachPickup(pickup))
                {
                    starterLootSettled = true;
                    return;
                }
            }

            // One fallback per match. Use the existing safe floor and pickup path.
            if (!TryFindDropSpot(out Vector3 point, requireReachable: true)) return;
            var spec = AarisWeaponCatalog.Select(ref rng, false);
            if (spec == null) return;
            var state = new AarisWeaponState(spec, matchSeed ^ 0x4F67A2C1u);
            starterLootSettled = CreateDropped(state, point, 0f) != null;
        }

        private bool IsCurrentlyActive(int candidateIndex)
        {
            foreach (var item in active)
                if (item != null && item.CandidateIndex == candidateIndex) return true;
            return false;
        }
    }
}
