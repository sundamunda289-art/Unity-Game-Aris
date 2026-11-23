using UnityEngine;
using UnityEngine.Rendering;

namespace Aaris.MapReview
{
    /// <summary>
    /// Shared bot gunfire presentation. Only true physically simulated
    /// near-range gunshots emit; virtual offscreen combat stays math-only.
    /// Six short-lived muzzle/tracer slots and twelve independent ballistic
    /// casing slots; no per-bot renderer or Update.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AarisRivalGunfireVfx : MonoBehaviour
    {
        private const int MaximumSlots = 6;
        private const int MaximumCasingSlots = 12;
        private sealed class Slot
        {
            internal LineRenderer tracer;
            internal Renderer flash;
            internal Transform flashPose;
            internal Light burst;
            internal Vector3 origin, end;
            internal float shotAt, tracerUntil, flashUntil;
        }

        private sealed class CasingSlot
        {
            internal Renderer renderer;
            internal Transform pose;
            internal Vector3 velocity, spin;
            internal float until, groundY;
            // Pump and bolt actions eject after cycling, not on the muzzle flash.
            // This deadline belongs to the casing slot, not a new coroutine.
            internal float ejectAt;
            internal bool grounded, queued;
        }

        private readonly Slot[] slots = new Slot[MaximumSlots];
        private readonly CasingSlot[] casings = new CasingSlot[MaximumCasingSlots];
        private static Material brassMaterial, shotgunMaterial;
        private Transform observer;
        private int nextSlot, nextCasingSlot;

        internal void Bind(Transform player) { observer = player; }

        private static Material MaterialFor(bool brass, bool shotgun = false)
        {
            if (!brass) return AarisEquippedGun.SharedGunfireGlowMaterial();
            Material cached = shotgun ? shotgunMaterial : brassMaterial;
            if (cached != null) return cached;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) return null;
            Color color = shotgun
                ? new Color(.55f, .11f, .07f) : new Color(.85f, .63f, .24f);
            cached = new Material(shader)
            {
                name = "Aaris Bot Casing Metal",
                enableInstancing = true
            };
            if (cached.HasProperty("_BaseColor")) cached.SetColor("_BaseColor", color);
            if (cached.HasProperty("_Color")) cached.SetColor("_Color", color);
            if (cached.HasProperty("_EmissionColor"))
            {
                cached.EnableKeyword("_EMISSION");
                cached.SetColor("_EmissionColor", color);
            }
            if (cached.HasProperty("_Metallic")) cached.SetFloat("_Metallic", .72f);
            if (cached.HasProperty("_Smoothness")) cached.SetFloat("_Smoothness", .38f);
            if (shotgun) shotgunMaterial = cached;
            else brassMaterial = cached;
            return cached;
        }

        private Slot CreateSlot()
        {
            var slot = new Slot();
            GameObject tracer = new GameObject("Aaris Bot Reused Tracer");
            tracer.transform.SetParent(transform, false);
            slot.tracer = tracer.AddComponent<LineRenderer>();
            slot.tracer.useWorldSpace = true;
            slot.tracer.positionCount = 2;
            slot.tracer.widthMultiplier = .008f;
            slot.tracer.alignment = LineAlignment.View;
            slot.tracer.startColor = new Color(1f, .93f, .63f, .84f);
            slot.tracer.endColor = new Color(1f, .58f, .16f, .12f);
            slot.tracer.sharedMaterial = MaterialFor(false);
            slot.tracer.shadowCastingMode = ShadowCastingMode.Off;
            slot.tracer.receiveShadows = false;
            slot.tracer.enabled = false;

            var flash = new GameObject("Aaris Bot Reused Tapered Barrel Fire",
                typeof(MeshFilter), typeof(MeshRenderer));
            flash.transform.SetParent(transform, false);
            flash.GetComponent<MeshFilter>().sharedMesh =
                AarisEquippedGun.SharedMuzzleFlameMesh();
            slot.flashPose = flash.transform;
            slot.flash = flash.GetComponent<MeshRenderer>();
            slot.flash.sharedMaterial = MaterialFor(false);
            slot.flash.shadowCastingMode = ShadowCastingMode.Off;
            slot.flash.receiveShadows = false;
            slot.flash.enabled = false;
            var light = new GameObject("Aaris Pooled Bot Barrel Light");
            light.transform.SetParent(slot.flashPose, false);
            slot.burst = light.AddComponent<Light>();
            slot.burst.type = LightType.Point;
            slot.burst.color = new Color(1f, .60f, .25f);
            slot.burst.range = 2.3f;
            slot.burst.intensity = 1.2f;
            slot.burst.shadows = LightShadows.None;
            slot.burst.enabled = false;
            return slot;
        }

        private CasingSlot CreateCasingSlot()
        {
            // Construct only on a real nearby shot, never for all 100 bots.
            // A casing carries no Rigidbody, collider or separate Update.
            var casing = new GameObject("Aaris Bot Reused Independent Ejected Casing",
                typeof(MeshFilter), typeof(MeshRenderer));
            casing.GetComponent<MeshFilter>().sharedMesh =
                AarisEquippedGun.SharedCasingMesh();
            casing.transform.SetParent(transform, false);
            var slot = new CasingSlot
            {
                pose = casing.transform,
                renderer = casing.GetComponent<MeshRenderer>()
            };
            slot.renderer.sharedMaterial = MaterialFor(true);
            slot.renderer.shadowCastingMode = ShadowCastingMode.Off;
            slot.renderer.receiveShadows = false;
            slot.renderer.enabled = false;
            return slot;
        }

        internal void Emit(Transform shooter, Vector3 target, bool shotgun)
        {
            // Preserve the old entry point for PlayMode fixtures and legacy callers.
            EmitWithBarrel(shooter, target, shotgun, .52f, .22f);
        }

        private void EmitWithBarrel(Transform shooter, Vector3 target,
            bool shotgun, float barrelReach, float portReach)
        {
            if (observer == null || shooter == null || Time.timeScale <= 0f ||
                (shooter.position - observer.position).sqrMagnitude > 70f * 70f)
                return;
            Vector3 from = shooter.position + Vector3.up * 1.29f +
                shooter.forward * barrelReach;
            float length = (target - from).magnitude;
            if (length < .18f) return;
            int index = nextSlot++ % MaximumSlots;
            Slot slot = slots[index];
            if (slot == null) slots[index] = slot = CreateSlot();
            float now = Time.time;
            slot.origin = from;
            slot.end = from + (target - from) *
                (Mathf.Min(length, 50f) / length);
            slot.shotAt = now;
            slot.tracerUntil = now +
                Mathf.Clamp(Mathf.Min(length, 50f) / 350f, .025f, .14f);
            slot.flashUntil = now + .045f;
            slot.tracer.SetPosition(0, from);
            slot.tracer.SetPosition(1, Vector3.MoveTowards(from, slot.end, 1.65f));
            slot.tracer.enabled = true;
            slot.flashPose.position = from;
            slot.flashPose.rotation = Quaternion.LookRotation(target - from, Vector3.up);
            slot.flashPose.localScale = shotgun
                ? new Vector3(.22f, .13f, .30f)
                : new Vector3(.15f, .09f, .24f);
            slot.flash.enabled = true;
            // At most two shadowless bot bursts, only close to the observer.
            int activeBursts = 0;
            for (int i = 0; i < slots.Length; i++)
                if (slots[i] != null && slots[i] != slot &&
                    slots[i].burst.enabled) activeBursts++;
            slot.burst.enabled = activeBursts < 2 &&
                (from - observer.position).sqrMagnitude <= 18f * 18f;

            // The next short muzzle/tracer slot must never erase an earlier
            // falling shell. Casings own a separately bounded ring pool.
            int casingIndex = nextCasingSlot++ % MaximumCasingSlots;
            CasingSlot casing = casings[casingIndex];
            if (casing == null)
                casings[casingIndex] = casing = CreateCasingSlot();
            casing.renderer.sharedMaterial = MaterialFor(true, shotgun);
            Vector3 port = shooter.position + Vector3.up * 1.32f +
                shooter.forward * portReach + shooter.right * .18f;
            casing.pose.position = port;
            casing.pose.rotation = shooter.rotation * Quaternion.Euler(90f, 0f, 0f);
            casing.pose.localScale = shotgun
                ? new Vector3(.008f, .023f, .008f)
                : new Vector3(.006f, .015f, .006f);
            casing.velocity = shooter.right * 2.25f + Vector3.up * 1.65f -
                shooter.forward * .2f;
            casing.spin = new Vector3(520f, 940f, 650f);
            casing.grounded = false;
            // One ground probe on an accepted nearby shot, none in Update.
            if (AarisEquippedGun.TryFindCasingGround(port, shooter,
                out RaycastHit ground))
                casing.groundY = ground.point.y + .012f;
            else casing.groundY = shooter.position.y + .012f;
            casing.groundY = Mathf.Min(casing.groundY, port.y - .07f);
            casing.until = now + .85f;
            casing.queued = false;
            casing.renderer.enabled = true;
        }

        // Weapon-specific bot presentation, while retaining the legacy Emit
        // entry point used by existing PlayMode fixtures. All six combat
        // classes share the same six tracer and twelve casing slots; the
        // source of damage still remains the director's authoritative ray.
        internal void EmitForGun(Transform shooter, Vector3 target,
            AarisWeaponSpec gun)
        {
            if (gun == null) return;
            int before = nextSlot;
            // A compact pistol and a long sniper cannot share a muzzle origin.
            // Clamp the authored metre length for imperfect imported FBX pivots.
            // Director PhysX damage and targeting remain authoritative.
            float barrelReach = Mathf.Clamp(
                gun.presentationLengthMetres * .60f, .27f, .78f);
            float portReach = Mathf.Clamp(
                gun.presentationLengthMetres * .25f, .075f, .34f);
            EmitWithBarrel(shooter, target, gun.category == "Shotgun",
                barrelReach, portReach);
            if (nextSlot == before) return; // Out of range, paused or invalid.

            Slot flash = slots[(nextSlot - 1) % MaximumSlots];
            CasingSlot casing = casings[(nextCasingSlot - 1) % MaximumCasingSlots];
            if (flash == null || casing == null) return;
            float scale = gun.category == "Pistol" ? .63f :
                gun.category == "SMG" ? .79f :
                gun.category == "Sniper" ? 1.25f :
                gun.category == "DMR" ? 1.08f :
                gun.id == "BattleRifle" ? 1.14f : 1f;
            // Scale the shared tapered flame without creating a material or
            // extra Light. Distinct low-profile pistol and bright sniper shots.
            flash.flashPose.localScale *= scale;
            flash.burst.intensity = 1.2f * scale;
            if (gun.category == "Pistol") casing.pose.localScale *= .76f;
            else if (gun.category == "SMG") casing.pose.localScale *= .88f;
            else if (gun.category == "Sniper") casing.pose.localScale *= 1.18f;

            float delay = gun.category == "Shotgun" ? .31f :
                gun.category == "Sniper" ? .29f : 0f;
            if (delay <= 0f) return;
            casing.ejectAt = Time.time + delay;
            casing.until += delay;
            casing.queued = true;
            casing.renderer.enabled = false;
        }

        private void Update()
        {
            float now = Time.time;
            // Match player casings: 10-15 FPS Android frames still advance
            // the metal shell through real elapsed time, with a hitch cap.
            float dt = Mathf.Min(Time.deltaTime, .12f);
            for (int i = 0; i < slots.Length; i++)
            {
                Slot slot = slots[i];
                if (slot == null) continue;
                if (slot.flash.enabled && now >= slot.flashUntil)
                {
                    slot.flash.enabled = false;
                    slot.burst.enabled = false;
                }
                if (slot.tracer.enabled)
                {
                    if (now >= slot.tracerUntil) slot.tracer.enabled = false;
                    else
                    {
                        float head = Mathf.Clamp01((now - slot.shotAt +
                            Mathf.Clamp(dt, .001f, .016f)) /
                            Mathf.Max(.001f, slot.tracerUntil - slot.shotAt));
                        Vector3 segment = slot.end - slot.origin;
                        float metres = segment.magnitude;
                        float tail = Mathf.Max(0f, head - 1.65f / Mathf.Max(.01f, metres));
                        slot.tracer.SetPosition(0, slot.origin + segment * tail);
                        slot.tracer.SetPosition(1, slot.origin + segment * head);
                    }
                }
            }
            for (int i = 0; i < casings.Length; i++)
            {
                CasingSlot casing = casings[i];
                if (casing != null && casing.queued && now >= casing.ejectAt)
                {
                    casing.queued = false;
                    casing.renderer.enabled = true;
                }
                if (casing == null || !casing.renderer.enabled) continue;
                if (now >= casing.until)
                {
                    casing.renderer.enabled = false;
                    continue;
                }
                if (casing.grounded) continue;
                casing.velocity += Physics.gravity * dt;
                Vector3 next = casing.pose.position + casing.velocity * dt;
                if (next.y <= casing.groundY && casing.velocity.y < 0f)
                {
                    next.y = casing.groundY;
                    casing.velocity = new Vector3(
                        casing.velocity.x * .58f,
                        -casing.velocity.y * .28f,
                        casing.velocity.z * .58f);
                    casing.spin *= .45f;
                    if (casing.velocity.y < .5f) casing.grounded = true;
                }
                casing.pose.position = next;
                casing.pose.Rotate(casing.spin * dt, Space.World);
            }
        }
    }
}
