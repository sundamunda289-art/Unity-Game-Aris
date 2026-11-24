using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Aaris.MapReview
{
    /// <summary>
    /// One match-wide, distance-streamed death-crate store. All 100 defeated
    /// rivals retain their actual loot without 100 active render hierarchies.
    /// A single 0.25s scan manages interaction and at most 16 pooled visuals.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AarisDeathCrateWorld : MonoBehaviour
    {
        private const float PickupRadius = 2.55f;
        private const float VisualRadius = 95f;
        private const int MaxVisuals = 16;
        private sealed class Drop
        {
            internal Vector3 position;
            internal AarisWeaponState gun;
            internal int looseAmmo, medkits;
            internal GameObject visual;
            internal Transform beamPose;
            internal bool pendingGroundSnap;
            // Depleted bots leave an honest, briefly visible empty marker.
            internal float markerUntil;
            // One PICK interaction can bypass an uncollectable closer crate
            // without allocating temporary candidate collections.
            internal int lastPickupProbe;
            internal bool HasContent => gun != null || medkits > 0;
        }

        private static AarisDeathCrateWorld instance;
        private static Material metal, trim, beamMaterial;
        private static Mesh beamMesh, crateCubeMesh;
        private readonly List<Drop> drops = new List<Drop>(100);
        private readonly Stack<Drop> unusedDrops = new Stack<Drop>(100);
        private readonly Stack<GameObject> unusedVisuals = new Stack<GameObject>(MaxVisuals);
        private Transform player;
        // The authoritative capsule changes height during standing, crouch
        // and prone transitions. Cache the component, not its old height.
        private CharacterController playerCapsule;
        private float nextPoll;
        private int pickupProbeSerial;
        private int liveVisuals;
        private bool cachedPickupNear;
        private RaycastHit[] pickupHits = new RaycastHit[16];
        // Death-time only: retain the probe buffer across all 100 bot deaths.
        // Never let a live rival capsule or moving vehicle become crate floor.
        private RaycastHit[] spawnGroundHits = new RaycastHit[16];

        private void Awake()
        {
            instance = this;
            // One logical record per rival; no Drop allocation on normal deaths.
            for (int i = 0; i < 100; i++) unusedDrops.Push(new Drop());
        }

        private void RecycleDrop(Drop drop)
        {
            Release(drop);
            drop.gun = null;
            drop.looseAmmo = drop.medkits = drop.lastPickupProbe = 0;
            drop.pendingGroundSnap = false;
            drop.markerUntil = 0f;
            drop.position = Vector3.zero;
            if (unusedDrops.Count < 100) unusedDrops.Push(drop);
        }
        internal void Bind(Transform target)
        {
            player = target;
            playerCapsule = target != null
                ? target.GetComponent<CharacterController>() : null;
            nextPoll = 0f;
        }

        internal void Spawn(Vector3 position, AarisWeaponSpec gun,
            int magazine, int reserve, int medkits, uint seed)
        {
            AarisWeaponState state = gun != null && gun.Valid
                ? new AarisWeaponState(gun, Mathf.Clamp(magazine, 0, gun.magazineSize),
                    0, 0f, 0f, false, seed) : null;
            // Reserve ammo stays separately tracked so it can be transferred to
            // a matching carried gun without duplicating the death weapon.
            // A rival that spent its last medkit must not conjure a fresh one.
            // Do not mint resources when a bot spent everything. An empty
            // death marker is visible but never actionable or permanent.
            bool emptyMarker = state == null && medkits <= 0;
            // No distant loot renderer exists: delay physics until within
            // the 95m interaction/visual band, preserving bridge-floor snapping.
            bool deferGroundSnap = player != null &&
                (position - player.position).sqrMagnitude >
                    (VisualRadius + 20f) * (VisualRadius + 20f);
            if (!deferGroundSnap)
                position = SnapToStationaryGround(position);
            // Unexpected extra synthetic spawns must not silently delete loot.
            Drop record = unusedDrops.Count > 0 ? unusedDrops.Pop() : new Drop();
            record.position = position;
            record.pendingGroundSnap = deferGroundSnap;
            record.gun = state;
            record.looseAmmo = state != null ? Mathf.Max(0, reserve) : 0;
            record.medkits = Mathf.Max(0, medkits);
            record.markerUntil = emptyMarker ? Time.time + 18f : 0f;
            drops.Add(record);
            // Do not allocate meshes, lights or native materials on the death hit.
            nextPoll = 0f;
        }

        private Vector3 SnapToStationaryGround(Vector3 position)
        {
            // Ignore moving actors and use highest stationary support.
            Vector3 probe = position + Vector3.up * 1.85f;
            int groundCount = AarisPhysicsHitBuffer.Raycast(
                new Ray(probe, Vector3.down), 5.5f,
                Physics.DefaultRaycastLayers, ref spawnGroundHits, 256,
                out RaycastHit[] supportHits);
            float bestSupportY = float.NegativeInfinity;
            for (int i = 0; i < groundCount; i++)
            {
                RaycastHit hit = supportHits[i];
                Collider surface = hit.collider;
                if (surface == null ||
                    surface.attachedRigidbody != null ||
                    surface.GetComponentInParent<CharacterController>() != null ||
                    hit.normal.y <= 0.45f ||
                    hit.point.y > position.y + 0.35f ||
                    hit.point.y <= bestSupportY)
                    continue;
                bestSupportY = hit.point.y;
            }
            if (bestSupportY > float.NegativeInfinity)
                position.y = bestSupportY;
            return position;
        }

        internal static bool HasPickupNear(Transform seeker)
        {
            return instance != null && instance.player == seeker &&
                instance.cachedPickupNear;
        }

        internal static bool TryGetNearbyHint(Transform seeker,
            out string weapon, out float distance, out bool canPick)
        {
            weapon = null;
            distance = 0f;
            canPick = false;
            if (instance == null || instance.player != seeker) return false;
            // Use the same reachable target as the PICK button. Proximity
            // labels for farther crates must not promise an instant pickup.
            Drop inReach = instance.cachedPickupNear
                ? instance.Nearest(seeker.position, PickupRadius, true, 0, true) : null;
            Drop drop = inReach ?? instance.Nearest(seeker.position, 48f, false);
            if (drop == null) return false;
            canPick = inReach != null;
            weapon = drop.gun != null ? drop.gun.Spec.displayName : "MEDKIT";
            // Label the resource the button can ACTUALLY receive: a full
            // inventory without a floor-exchange world may take ammo or meds,
            // but cannot swap its gun through the crate.
            if (inReach != null && drop.gun != null &&
                seeker.TryGetComponent(out AarisWeaponInventory inventory) &&
                !inventory.CanPickupDeathCrateGun)
                weapon = drop.looseAmmo > 0 &&
                    inventory.CanReceiveDeathCrateAmmo(drop.gun.Spec)
                    ? drop.gun.Spec.displayName + " AMMO" : "MEDKIT";
            distance = Vector3.Distance(seeker.position, drop.position);
            return true;
        }

        internal static bool TryLootNearest(Transform seeker, AarisWeaponInventory inventory)
        {
            if (instance == null || instance.player != seeker || inventory == null ||
                Time.timeScale <= 0f) return false;
            // A nearer medkit-only crate can be uncollectable when supplies
            // are full (or medical UI is absent). Do not let it permanently
            // block a reachable gun/medkit in the same pickup radius.
            int probe = ++instance.pickupProbeSerial;
            if (probe <= 0)
            {
                // Handle unlikely integer rollover without stale exclusions.
                probe = instance.pickupProbeSerial = 1;
                for (int i = 0; i < instance.drops.Count; i++)
                    instance.drops[i].lastPickupProbe = 0;
            }
            int attempts = instance.drops.Count;
            for (int i = 0; i < attempts; i++)
            {
                Drop drop = instance.Nearest(seeker.position, PickupRadius,
                    true, probe, true);
                if (drop == null) break;
                drop.lastPickupProbe = probe;

                bool taken = false;
                // A full two-slot inventory needs a reserved, reachable floor
                // record for the outgoing hand gun BEFORE changing ownership.
                // The inventory routes that case through its normal loot world.
                if (drop.gun != null && inventory.CanPickupDeathCrateGun)
                {
                    AarisWeaponState transfer = new AarisWeaponState(drop.gun.Spec,
                        drop.gun.Magazine, drop.looseAmmo, 0f, 0f, false,
                        drop.gun.SpreadSeed);
                    if (inventory.TryPickupDeathCrateGun(transfer))
                    {
                        drop.gun = null;
                        drop.looseAmmo = 0;
                        taken = true;
                    }
                }
                // If both gun slots are full, loose ammo can still be collected.
                // Unused rounds remain in the crate until there is reserve space.
                if (drop.gun != null && drop.looseAmmo > 0)
                {
                    int added = inventory.AddDeathCrateAmmo(drop.gun.Spec,
                        drop.looseAmmo);
                    drop.looseAmmo -= added;
                    taken |= added > 0;
                }
                if (drop.medkits > 0 &&
                    seeker.TryGetComponent(out AarisOfflineTacticalHUD medical))
                {
                    int added = medical.AddDeathCrateMedkits(drop.medkits);
                    drop.medkits -= added;
                    taken |= added > 0;
                }
                if (!drop.HasContent)
                {
                    instance.drops.Remove(drop);
                    instance.RecycleDrop(drop);
                }
                if (taken)
                {
                    // The HUD's reachability cache must not advertise consumed
                    // loot before the next quarter-second world poll.
                    instance.cachedPickupNear = false;
                    instance.nextPoll = 0f;
                    return true;
                }
            }
            return false;
        }

        // Check line of sight only for crates within pickup reach. A nearby
        // box across a solid building wall must not be looted through it.
        private bool Reachable(Vector3 from, Vector3 to)
        {
            // A standing-height eye ray previously cleared low cover even
            // while the player was physically prone behind it. Use the LIVE
            // CharacterController dimensions (also during stance transitions).
            // Never cache its height across a crouch/prone resize.
            if (playerCapsule == null && player != null)
                player.TryGetComponent(out playerCapsule);
            float rayLift = 1.02f;
            if (playerCapsule != null)
            {
                float footOffset = playerCapsule.center.y -
                    playerCapsule.height * .5f;
                rayLift = Mathf.Clamp(footOffset +
                    playerCapsule.height * .65f, .33f, 1.02f);
            }
            Vector3 origin = from + Vector3.up * rayLift;
            Vector3 delta = to + Vector3.up * .32f - origin;
            float distance = delta.magnitude;
            if (distance < .001f) return true;
            int count = AarisPhysicsHitBuffer.Raycast(new Ray(origin, delta / distance),
                distance, Physics.DefaultRaycastLayers, ref pickupHits, 256,
                out RaycastHit[] hits);
            // At prone/stance transitions the pickup ray may begin above the
            // capsule and cross it. Own bodies and held-gun attachments must
            // not be mistaken for a wall between the player and the crate.
            for (int i = 0; i < count; i++)
            {
                Collider hit = hits[i].collider;
                if (hit == null || (player != null &&
                    hit.transform.IsChildOf(player))) continue;
                return false;
            }
            return true;
        }

        private Drop Nearest(Vector3 point, float radius, bool mustReach,
            int excludeProbe = 0, bool actionableOnly = false)
        {
            // Once per lookup, not once per candidate; avoid a false PICK
            // prompt for medkit-only crates when no medkit can be stored.
            bool canTakeMedkits = !actionableOnly ||
                (player != null &&
                 player.TryGetComponent(out AarisOfflineTacticalHUD medical) &&
                 medical.CanReceiveDeathCrateMedkits);
            AarisWeaponInventory inventory = null;
            if (actionableOnly && player != null)
                player.TryGetComponent(out inventory);
            bool canTakeGun = inventory != null &&
                inventory.CanPickupDeathCrateGun;
            float best = radius * radius;
            Drop selected = null;
            for (int i = 0; i < drops.Count; i++)
            {
                Drop drop = drops[i];
                if (drop.pendingGroundSnap ||
                    !drop.HasContent || (excludeProbe != 0 &&
                    drop.lastPickupProbe == excludeProbe) ||
                    Mathf.Abs(drop.position.y - point.y) > 3f)
                    continue;
                if (actionableOnly)
                {
                    bool takeGun = drop.gun != null && canTakeGun;
                    bool takeAmmo = drop.gun != null && drop.looseAmmo > 0 &&
                        inventory != null &&
                        inventory.CanReceiveDeathCrateAmmo(drop.gun.Spec);
                    bool takeMedkits = drop.medkits > 0 && canTakeMedkits;
                    if (!takeGun && !takeAmmo && !takeMedkits) continue;
                }
                float d2 = (drop.position - point).sqrMagnitude;
                if (d2 >= best) continue;
                if (mustReach && !Reachable(point, drop.position)) continue;
                best = d2;
                selected = drop;
            }
            return selected;
        }

        private void Update()
        {
            if (player == null || Time.timeScale <= 0f ||
                Time.time < nextPoll) return;
            nextPoll = Time.time + 0.25f;
            Vector3 here = player.position;
            // At most two formerly-distant floor probes per 250ms scan.
            int groundSnapBudget = 2;
            int bestIndex = -1;
            float bestDistance = VisualRadius * VisualRadius;
            float furthestVisualDistance = -1f;
            Drop furthestVisible = null;
            for (int i = 0; i < drops.Count; i++)
            {
                Drop drop = drops[i];
                bool showMarker = drop.HasContent || Time.time < drop.markerUntil;
                if (!showMarker)
                {
                    drops.RemoveAt(i--);
                    RecycleDrop(drop);
                    continue;
                }
                float distance = (drop.position - here).sqrMagnitude;
                if (drop.pendingGroundSnap &&
                    distance < (VisualRadius + 20f) * (VisualRadius + 20f) &&
                    groundSnapBudget > 0)
                {
                    drop.position = SnapToStationaryGround(drop.position);
                    drop.pendingGroundSnap = false;
                    groundSnapBudget--;
                    distance = (drop.position - here).sqrMagnitude;
                }
                // Until floor admission finishes, never draw or let the
                // player take a crate at stale vertical coordinates.
                if (drop.pendingGroundSnap) continue;
                if (drop.visual != null)
                {
                    // Subtle loot-beam breath at the existing 4 Hz world poll.
                    // Empty death markers remain dim/short, never look actionable.
                    if (drop.beamPose != null)
                    {
                        float pulse = drop.HasContent
                            ? 1f + .06f * Mathf.Sin(Time.time * 2.8f) : .48f;
                        drop.beamPose.localScale = new Vector3(pulse,
                            drop.HasContent ? 1f : .48f, pulse);
                    }
                    if (distance > VisualRadius * VisualRadius || !showMarker)
                    {
                        Release(drop);
                        continue;
                    }
                    if (distance > furthestVisualDistance)
                    {
                        furthestVisualDistance = distance;
                        furthestVisible = drop;
                    }
                    continue;
                }
                if (distance < bestDistance && showMarker)
                {
                    bestIndex = i;
                    bestDistance = distance;
                }
            }
            // PICK cache must observe newly snapped floors on the same poll.
            cachedPickupNear = Nearest(here, PickupRadius, true, 0, true) != null;
            if (bestIndex < 0) return;
            // When all 16 visuals are in use, recycle the farthest one for
            // a nearer unseen crate. A single swap per 0.25s poll keeps the
            // closest firefight loot visible without building extra models.
            if (liveVisuals >= MaxVisuals)
            {
                if (furthestVisible == null ||
                    bestDistance + 0.25f >= furthestVisualDistance) return;
                Release(furthestVisible);
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            if (unusedVisuals.Count == 0 &&
                !AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild()) return;
#endif
            Drop chosen = drops[bestIndex];
            GameObject view = unusedVisuals.Count > 0
                ? unusedVisuals.Pop() : CreateVisual();
            if (view == null) return;
            chosen.visual = view;
            // Cache only when assigning a pooled visual: never Find in Update.
            chosen.beamPose = view.transform.Find("Vertical loot marker");
            if (chosen.beamPose != null)
                chosen.beamPose.localScale = chosen.HasContent
                    ? Vector3.one : new Vector3(.48f, .48f, .48f);
            view.transform.position = chosen.position;
            view.SetActive(true);
            liveVisuals++;
        }

        private void Release(Drop drop)
        {
            if (drop.visual == null) return;
            drop.visual.SetActive(false);
            if (unusedVisuals.Count < MaxVisuals)
                unusedVisuals.Push(drop.visual);
            else Destroy(drop.visual);
            drop.visual = null;
            drop.beamPose = null;
            liveVisuals = Mathf.Max(0, liveVisuals - 1);
        }

        private static Material Paint(ref Material cached, Color color,
            string name, bool transparent)
        {
            if (cached != null) return cached;
            Shader shader = Shader.Find(transparent
                ? "Universal Render Pipeline/Particles/Unlit"
                : "Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) return null;
            cached = new Material(shader) { name = name, enableInstancing = true };
            if (cached.HasProperty("_BaseColor")) cached.SetColor("_BaseColor", color);
            if (cached.HasProperty("_Color")) cached.SetColor("_Color", color);
            if (cached.HasProperty("_EmissionColor"))
            {
                cached.EnableKeyword("_EMISSION");
                cached.SetColor("_EmissionColor", color * (transparent ? 1.2f : 0.15f));
            }
            if (transparent)
            {
                if (cached.HasProperty("_Surface")) cached.SetFloat("_Surface", 1f);
                if (cached.HasProperty("_SrcBlend")) cached.SetFloat("_SrcBlend", 5f);
                if (cached.HasProperty("_DstBlend")) cached.SetFloat("_DstBlend", 10f);
                if (cached.HasProperty("_ZWrite")) cached.SetFloat("_ZWrite", 0f);
                cached.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                cached.renderQueue = (int)RenderQueue.Transparent;
            }
            return cached;
        }

        // Unity's CreatePrimitive allocates a BoxCollider per mesh, even when
        // the collider is immediately disabled and Destroy-scheduled. Sixteen
        // streamed crates previously built 48 throwaway native colliders.
        // Share one lit, UV-complete cube mesh across every crate part instead.
        private static Mesh CrateCubeMesh()
        {
            if (crateCubeMesh != null) return crateCubeMesh;
            var result = new Mesh { name = "Aaris Shared Death Crate Cube" };
            Vector3[] faceNormals =
            {
                Vector3.forward, Vector3.back, Vector3.right,
                Vector3.left, Vector3.up, Vector3.down
            };
            Vector3[] faceRights =
            {
                Vector3.right, Vector3.left, Vector3.back,
                Vector3.forward, Vector3.right, Vector3.right
            };
            Vector3[] faceUps =
            {
                Vector3.up, Vector3.up, Vector3.up,
                Vector3.up, Vector3.back, Vector3.forward
            };
            var vertices = new Vector3[24];
            var normals = new Vector3[24];
            var uv = new Vector2[24];
            var triangles = new int[36];
            for (int face = 0; face < 6; face++)
            {
                int vi = face * 4;
                int ti = face * 6;
                Vector3 center = faceNormals[face] * .5f;
                Vector3 side = faceRights[face] * .5f;
                Vector3 up = faceUps[face] * .5f;
                vertices[vi] = center - side - up;
                vertices[vi + 1] = center + side - up;
                vertices[vi + 2] = center + side + up;
                vertices[vi + 3] = center - side + up;
                for (int j = 0; j < 4; j++)
                    normals[vi + j] = faceNormals[face];
                uv[vi] = Vector2.zero;
                uv[vi + 1] = Vector2.right;
                uv[vi + 2] = Vector2.one;
                uv[vi + 3] = Vector2.up;
                triangles[ti] = vi;
                triangles[ti + 1] = vi + 1;
                triangles[ti + 2] = vi + 2;
                triangles[ti + 3] = vi;
                triangles[ti + 4] = vi + 2;
                triangles[ti + 5] = vi + 3;
            }
            result.vertices = vertices;
            result.normals = normals;
            result.uv = uv;
            result.triangles = triangles;
            result.RecalculateBounds();
            result.UploadMeshData(true);
            crateCubeMesh = result;
            return result;
        }

        private static void Part(Transform root, string label,
            Vector3 offset, Vector3 scale, Material material)
        {
            var part = new GameObject(label, typeof(MeshFilter), typeof(MeshRenderer));
            part.transform.SetParent(root, false);
            part.transform.localPosition = offset;
            part.transform.localScale = scale;
            part.GetComponent<MeshFilter>().sharedMesh = CrateCubeMesh();
            MeshRenderer renderer = part.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        private static Mesh BeamGeometry()
        {
            if (beamMesh != null) return beamMesh;
            var mesh = new Mesh { name = "Aaris Shared Death Crate Beam" };
            mesh.vertices = new[]
            {
                new Vector3(-.18f, 0f, 0f), new Vector3(.18f, 0f, 0f),
                new Vector3(-.06f, 4.1f, 0f), new Vector3(.06f, 4.1f, 0f),
                new Vector3(0f, 0f, -.18f), new Vector3(0f, 0f, .18f),
                new Vector3(0f, 4.1f, -.06f), new Vector3(0f, 4.1f, .06f)
            };
            mesh.colors32 = new[]
            {
                new Color32(70, 218, 255, 100), new Color32(70, 218, 255, 100),
                new Color32(70, 218, 255, 0), new Color32(70, 218, 255, 0),
                new Color32(70, 218, 255, 100), new Color32(70, 218, 255, 100),
                new Color32(70, 218, 255, 0), new Color32(70, 218, 255, 0)
            };
            mesh.triangles = new[] { 0, 2, 1, 1, 2, 3, 4, 6, 5, 5, 6, 7,
                1, 2, 0, 3, 2, 1, 5, 6, 4, 7, 6, 5 };
            mesh.RecalculateBounds();
            beamMesh = mesh;
            return mesh;
        }

        private GameObject CreateVisual()
        {
            var shell = Paint(ref metal, new Color(.16f, .24f, .25f), "Aaris Crate Metal", false);
            var edge = Paint(ref trim, new Color(.57f, .43f, .18f), "Aaris Crate Brass", false);
            var glow = Paint(ref beamMaterial,
                new Color(.28f, .76f, 1f, .30f), "Aaris Crate Vertical Glow", true);
            var root = new GameObject("Aaris Pooled Bot Death Crate");
            root.transform.SetParent(transform, false);
            Part(root.transform, "Armoured crate body", new Vector3(0f, .22f, 0f),
                new Vector3(.68f, .42f, .48f), shell);
            Part(root.transform, "Metal crate lid", new Vector3(0f, .46f, 0f),
                new Vector3(.73f, .09f, .53f), edge);
            Part(root.transform, "Front latch", new Vector3(0f, .29f, -.25f),
                new Vector3(.15f, .15f, .03f), edge);
            if (glow != null)
            {
                var beam = new GameObject("Vertical loot marker",
                    typeof(MeshFilter), typeof(MeshRenderer));
                beam.transform.SetParent(root.transform, false);
                beam.transform.localPosition = new Vector3(0f, .50f, 0f);
                beam.GetComponent<MeshFilter>().sharedMesh = BeamGeometry();
                var render = beam.GetComponent<MeshRenderer>();
                render.sharedMaterial = glow;
                render.shadowCastingMode = ShadowCastingMode.Off;
                render.receiveShadows = false;
            }
            return root;
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
        }
    }
}
