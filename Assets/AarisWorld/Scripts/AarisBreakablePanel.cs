using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// Offline door/glass damageable for houses with genuine wall openings.
    /// Uses the existing gun/grenade IAarisDamageable path, not network messages.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AarisBreakablePanel : MonoBehaviour, IAarisDamageable
    {
        private const float NearRadius = 3.2f;
        private const float NearFloorTolerance = 1.8f;
        private const float OpenAngle = 95f;
        private const float OpenSpeed = 230f;
        private Transform leaf, player, house;
        private Collider solid;
        private Vector3 doorwayCenter, worldDoorwayCenter;
        private float nextFarCheck;
        private float health, lastNear = -999f, currentAngle, openDirection = 1f;
        private bool isDoor, destroyed;
        private readonly Collider[] blockers = new Collider[16];
        // Cached packaged CC0 effects: no network or per-frame resource lookup.
        private static AudioClip openingSound, closingSound;
        private static bool soundsLoaded;
        private AudioSource soundSource;
        private bool lastRequestedOpen;

        public bool IsBroken => destroyed;
        public bool IsOpen => isDoor && (destroyed || Mathf.Abs(currentAngle) > 80f);

        public void Configure(Transform panel, Collider panelCollider,
            Transform nearbyPlayer, bool door, float hitPoints, Transform houseRoot)
        {
            leaf = panel;
            solid = panelCollider;
            player = nearbyPlayer;
            isDoor = door;
            house = houseRoot;
            if (isDoor)
            {
                doorwayCenter = new Vector3(0f, 0f, transform.localPosition.z);
                // These modular houses are static after construction. Cache
                // their world doorway position rather than multiplying the
                // house transform on every Android frame.
                worldDoorwayCenter = house != null
                    ? house.TransformPoint(doorwayCenter) : transform.position;
                if (!soundsLoaded)
                {
                    openingSound = Resources.Load<AudioClip>(
                        "SFX/CC0/impactMetal_light_000");
                    closingSound = Resources.Load<AudioClip>(
                        "SFX/CC0/impactSoft_heavy_000");
                    soundsLoaded = true;
                }
                if (openingSound != null || closingSound != null)
                {
                    // Only actual interactive doors get one lightweight 3D voice.
                    soundSource = gameObject.AddComponent<AudioSource>();
                    soundSource.playOnAwake = false;
                    soundSource.spatialBlend = 1f;
                    soundSource.rolloffMode = AudioRolloffMode.Linear;
                    soundSource.minDistance = 1.2f;
                    soundSource.maxDistance = 14f;
                    soundSource.dopplerLevel = 0f;
                    soundSource.volume = 0.5f;
                }
            }
            health = Mathf.Max(1f, hitPoints);
            enabled = door; // windows do no per-frame polling
        }

        private void Update()
        {
            if (destroyed || leaf == null || !isDoor) return;
            bool near = false;
            if (player != null && house != null)
            {
                Vector3 approach = player.position - worldDoorwayCenter;
                // Closed doors far from the player need only a 4 Hz check.
                // Once approached/opened they continue animating at full
                // frame rate so closing safety and swing are unchanged.
                if (approach.sqrMagnitude > 16f * 16f &&
                    Mathf.Abs(currentAngle) < 0.01f &&
                    Time.time - lastNear > 1.5f)
                {
                    if (Time.time < nextFarCheck) return;
                    nextFarCheck = Time.time + 0.25f;
                }
                near = Mathf.Abs(approach.y) <= NearFloorTolerance &&
                    approach.x * approach.x + approach.z * approach.z <= NearRadius * NearRadius;
            }
            if (near)
            {
                lastNear = Time.time;
                // Decide the swing only while the panel is essentially closed.
                // A player approaching from the exterior (negative local Z)
                // gets an inward-opening door, not a solid door leaf pushed
                // into their CharacterController. The hinge side stays unchanged.
                if (Mathf.Abs(currentAngle) < 1f)
                    openDirection = house.InverseTransformPoint(player.position).z <
                        doorwayCenter.z ? -1f : 1f;
            }
            bool shouldOpen = near || Time.time - lastNear < 1.5f;
            // Never shut a moving solid door across a player, car or other
            // interactive collider. Saturated queries also fail open.
            if (!shouldOpen && Mathf.Abs(currentAngle) > .01f && ClosingBlocked())
                shouldOpen = true;
            float next = Mathf.MoveTowards(currentAngle, shouldOpen ? OpenAngle * openDirection : 0f,
                OpenSpeed * Time.deltaTime);
            if (Mathf.Abs(next - currentAngle) > .001f)
            {
                if (shouldOpen != lastRequestedOpen)
                {
                    // Trigger once per actual opening/closing transition.
                    // A blocked closing sweep never plays the closing cue.
                    AudioClip cue = shouldOpen ? openingSound : closingSound;
                    if (soundSource != null && cue != null)
                        soundSource.PlayOneShot(cue, shouldOpen ? 0.55f : 0.70f);
                }
                lastRequestedOpen = shouldOpen;
                currentAngle = next;
                transform.localRotation = Quaternion.Euler(0f, currentAngle, 0f);
            }
        }

        private bool ClosingBlocked()
        {
            if (leaf == null) return false;
            // Doorframe safety volume, regardless of current swing angle.
            Vector3 middle = house.TransformPoint(new Vector3(0f, 1.18f,
                transform.localPosition.z));
            int hits = Physics.OverlapBoxNonAlloc(middle, new Vector3(.9f, 1.20f, .95f),
                blockers, house.rotation, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);
            if (hits >= blockers.Length) return true;
            for (int i = 0; i < hits; i++)
            {
                var hit = blockers[i];
                if (hit == null || hit is TerrainCollider || hit.transform.IsChildOf(house))
                    continue;
                return true;
            }
            return false;
        }

        public void ApplyDamage(float amount, GameObject attacker)
        {
            if (destroyed || float.IsNaN(amount) || float.IsInfinity(amount) || amount <= 0f)
                return;
            health -= amount;
            if (health > 0f) return;
            destroyed = true;
            if (solid != null) solid.enabled = false;
            if (leaf != null)
            {
                // Four bounded decorative fragments; no projectiles, persistent
                // Rigidbody piles, debris collisions or per-instance textures.
                var renderer = leaf.GetComponent<Renderer>();
                var surface = renderer != null ? renderer.sharedMaterial : null;
                if (surface != null) SpawnFragments(surface);
                leaf.gameObject.SetActive(false);
            }
            enabled = false;
        }

        private void SpawnFragments(Material surface)
        {
            int count = isDoor ? 3 : 4;
            for (int i = 0; i < count; i++)
            {
                var shard = GameObject.CreatePrimitive(PrimitiveType.Cube);
                shard.name = isDoor ? "ShortLivedWoodFragment" : "ShortLivedGlassFragment";
                shard.transform.position = leaf.TransformPoint(
                    new Vector3(((i & 1) * 2 - 1) * .22f,
                        .25f + .3f * (i / 2), 0f));
                shard.transform.localScale = isDoor
                    ? new Vector3(.13f, .28f, .05f)
                    : new Vector3(.14f, .11f, .025f);
                shard.GetComponent<Renderer>().sharedMaterial = surface;
                shard.GetComponent<Collider>().enabled = false;
                var body = shard.AddComponent<Rigidbody>();
                body.mass = .09f;
                body.AddForce(new Vector3((i & 1) == 0 ? -1f : 1f, 1.6f,
                    (i & 2) == 0 ? -1f : 1f), ForceMode.Impulse);
                Object.Destroy(shard, 1.1f);
            }
        }
    }
}
