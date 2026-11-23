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
        // A deliberate PICK may try a few nearby crates if the nearest one
        // cannot transfer any supplies. This cap prevents worst-case model or
        // terrain work from scaling with 100 overlapping dead rivals.
        private const int MaximumPickupAttempts = 4;
        private sealed class Drop
        {
            internal Vector3 position;
            internal AarisWeaponState gun;
            internal int looseAmmo, medkits;
            internal GameObject visual;
            internal bool HasContent => gun != null || medkits > 0;
        }

        private static AarisDeathCrateWorld instance;
        private static Material metal, trim, beamMaterial;
        private static Mesh beamMesh;
        private readonly List<Drop> drops = new List<Drop>(100);
        private readonly Drop[] attemptedPickups = new Drop[MaximumPickupAttempts];
        private readonly Stack<GameObject> unusedVisuals = new Stack<GameObject>(MaxVisuals);
        private Transform player;
        private float nextPoll;
        private int liveVisuals;
        private bool cachedPickupNear;

        private void Awake() { instance = this; }
        internal void Bind(Transform target) { player = target; nextPoll = 0f; }

        internal void Spawn(Vector3 position, AarisWeaponSpec gun,
            int magazine, int reserve, int medkits, uint seed)
        {
            // OnDied disables the rival capsule first. Anchor on a real floor.
            Vector3 probe = position + Vector3.up * 1.85f;
            if (Physics.Raycast(probe, Vector3.down, out RaycastHit ground, 5.5f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) &&
                ground.normal.y > 0.45f)
                position.y = ground.point.y;
            AarisWeaponState state = gun != null && gun.Valid
                ? new AarisWeaponState(gun, Mathf.Clamp(magazine, 0, gun.magazineSize),
                    0, 0f, 0f, false, seed) : null;
            // Reserve ammo stays separately tracked so it can be transferred to
            // a matching carried gun without duplicating the death weapon.
            // A rival that spent its last medkit must not conjure a fresh one.
            // Unarmed rivals with no remaining supplies leave no empty crate.
            if (state == null && medkits <= 0) return;
            drops.Add(new Drop
            {
                position = position,
                gun = state,
                looseAmmo = state != null ? Mathf.Max(0, reserve) : 0,
                medkits = Mathf.Max(0, medkits)
            });
            // Do not allocate meshes, lights or native materials on the death hit.
            nextPoll = 0f;
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
                ? instance.Nearest(seeker.position, PickupRadius, true) : null;
            Drop drop = inReach ?? instance.Nearest(seeker.position, 48f, false);
            if (drop == null) return false;
            canPick = inReach != null;
            weapon = drop.gun != null ? drop.gun.Spec.displayName : "MEDKIT";
            distance = Vector3.Distance(seeker.position, drop.position);
            return true;
        }

        internal static bool TryLootNearest(Transform seeker, AarisWeaponInventory inventory)
        {
            if (instance == null || instance.player != seeker || inventory == null ||
                Time.timeScale <= 0f) return false;

            // One unreachable/full crate must not permanently block another
            // reachable crate in the same 2.55m interaction radius. Reuse the
            // fixed scratch array; there are no per-tap collections.
            int attempted = 0;
            bool taken = false;
            while (attempted < MaximumPickupAttempts)
            {
                Drop drop = instance.Nearest(seeker.position, PickupRadius, true,
                    instance.attemptedPickups, attempted);
                if (drop == null) break;
                instance.attemptedPickups[attempted++] = drop;
                if (TryLootDrop(seeker, inventory, drop))
                {
                    taken = true;
                    break;
                }
            }
            // Do not keep removed crate records alive in the scratch array.
            for (int i = 0; i < attempted; i++)
                instance.attemptedPickups[i] = null;
            return taken;
        }

        private static bool TryLootDrop(Transform seeker,
            AarisWeaponInventory inventory, Drop drop)
        {
            bool taken = false;
            // If this exact gun is already carried, collect usable ammo and
            // medkits BEFORE considering a costly redundant model exchange.
            // A second deliberate PICK can still exchange the gun afterwards.
            if (drop.gun != null && inventory.CarriesDeathCrateWeapon(drop.gun.Spec))
            {
                taken |= TransferAmmoAndMedkits(seeker, inventory, drop);
                if (taken)
                {
                    instance.cachedPickupNear = false;
                    instance.nextPoll = 0f;
                    return true;
                }
            }
            // A full two-slot inventory needs a reserved, reachable floor
            // record for the outgoing hand gun BEFORE changing ownership.
            // The inventory routes that case through its normal loot world.
            if (drop.gun != null)
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
            // When exchange is blocked, still collect only the real rounds
            // and medkits that fit. Partial reserve transfers remain in crate.
            if (!taken)
                taken = TransferAmmoAndMedkits(seeker, inventory, drop);
            if (!drop.HasContent)
            {
                instance.Release(drop);
                instance.drops.Remove(drop);
            }
            if (taken)
            {
                // The HUD's reachability cache must not advertise consumed loot
                // before the next scheduled quarter-second world poll.
                instance.cachedPickupNear = false;
                instance.nextPoll = 0f;
            }
            return taken;
        }

        private static bool TransferAmmoAndMedkits(Transform seeker,
            AarisWeaponInventory inventory, Drop drop)
        {
            bool taken = false;
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
            // Empty crates must not linger after a supplies-only pickup.
            if (!drop.HasContent)
            {
                instance.Release(drop);
                instance.drops.Remove(drop);
            }
            return taken;
        }

        // Check line of sight only for crates within pickup reach. A nearby
        // box across a solid building wall must not be looted through it.
        private static bool Reachable(Vector3 from, Vector3 to)
        {
            return !Physics.Linecast(from + Vector3.up * 1.02f,
                to + Vector3.up * 0.32f, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);
        }

        private Drop Nearest(Vector3 point, float radius, bool mustReach,
            Drop[] excluded = null, int excludedCount = 0)
        {
            float best = radius * radius;
            Drop selected = null;
            for (int i = 0; i < drops.Count; i++)
            {
                Drop drop = drops[i];
                if (!drop.HasContent || Mathf.Abs(drop.position.y - point.y) > 3f)
                    continue;
                float d2 = (drop.position - point).sqrMagnitude;
                if (d2 >= best) continue;
                if (excludedCount > 0)
                {
                    bool alreadyTried = false;
                    for (int j = 0; j < excludedCount; j++)
                    {
                        if (excluded[j] == drop)
                        {
                            alreadyTried = true;
                            break;
                        }
                    }
                    if (alreadyTried) continue;
                }
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
            cachedPickupNear = Nearest(here, PickupRadius, true) != null;
            int bestIndex = -1;
            float bestDistance = VisualRadius * VisualRadius;
            float furthestVisualDistance = -1f;
            Drop furthestVisible = null;
            for (int i = 0; i < drops.Count; i++)
            {
                Drop drop = drops[i];
                float distance = (drop.position - here).sqrMagnitude;
                if (drop.visual != null)
                {
                    if (distance > VisualRadius * VisualRadius || !drop.HasContent)
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
                if (distance < bestDistance && drop.HasContent)
                {
                    bestIndex = i;
                    bestDistance = distance;
                }
            }
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

        private static void Part(Transform root, string label,
            Vector3 offset, Vector3 scale, Material material)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = label;
            part.transform.SetParent(root, false);
            part.transform.localPosition = offset;
            part.transform.localScale = scale;
            Collider collider = part.GetComponent<Collider>();
            if (collider != null) { collider.enabled = false; Destroy(collider); }
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
