using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// Exactly ONE carried weapon. Taking a second gun leaves the first at
    /// the pickup location as persistent world loot with its real ammo state.
    /// Dropping voluntarily restores the original unarmed punch.
    /// No second gun is stored on the back or hidden in a second slot.
    /// </summary>
    // Run after AarisCharacterVisual (order +200): its Playables update
    // the hand bones before this class places the gun at that pose.
    [DefaultExecutionOrder(310)]
    [DisallowMultipleComponent, RequireComponent(typeof(AarisBattleActions))]
    public sealed class AarisWeaponInventory : MonoBehaviour
    {
        private AarisEquippedGun equipped;
        private Transform weaponRoot;
        private AarisBattleActions combat;
        private AarisThirdPersonPlayer motor;
        private AarisWeaponLootWorld lootWorld;
        private Transform hand;
        private float nextRigSearch;
        private bool aiming;

        public AarisEquippedGun Current => equipped;
        public bool Aiming => aiming;
        public bool HasGun => equipped != null && equipped.Configured;
        public bool PickupAvailable => lootWorld != null && lootWorld.HasPickupInRange;

        private void Awake()
        {
            combat = GetComponent<AarisBattleActions>();
            motor = GetComponent<AarisThirdPersonPlayer>();
        }

        public void BindLootWorld(AarisWeaponLootWorld world) => lootWorld = world;
        public void RequestPickup() { if (lootWorld != null) lootWorld.RequestPickup(); }
        public bool DropCurrent() => lootWorld != null && lootWorld.TryDropEquipped();

        /// <summary>
        /// An atomic exchange. Preload the incoming FBX before detaching the
        /// existing gun. The previous gun is returned as a complete state
        /// snapshot so the world can leave it on the ground.
        /// </summary>
        public bool TryPickup(AarisWeaponState incoming, out AarisWeaponState dropped)
        {
            dropped = null;
            if (incoming == null || incoming.Spec == null || !incoming.Spec.Valid)
                return false;
            GameObject prefab = AarisWeaponCatalog.ModelFor(incoming.Spec);
            if (prefab == null) return false;

            var root = new GameObject("Aaris Equipped " + incoming.Spec.id);
            root.transform.SetParent(transform, false);
            var visible = Instantiate(prefab, root.transform);
            // A multi-mesh FBX needs the bounds of ALL rendered parts.
            // Get the renderers once; this runs only when a weapon is picked up.
            var renderers = visible.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                Destroy(root);
                Debug.LogError("Aaris: gun has no visible mesh: " + incoming.Spec.id);
                return false;
            }
            foreach (var collider in visible.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;
            var mesh = visible.transform;
            Bounds combinedBounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                combinedBounds.Encapsulate(renderers[i].bounds);
            float longest = Mathf.Max(combinedBounds.size.x,
                combinedBounds.size.y, combinedBounds.size.z);
            if (longest < 0.001f)
            {
                Destroy(root);
                return false;
            }
            mesh.localScale *= Mathf.Clamp(0.86f / longest, 0.001f, 10f) *
                incoming.Spec.handScale;
            mesh.localPosition = Vector3.zero;
            mesh.localRotation = Quaternion.identity;

            var next = root.AddComponent<AarisEquippedGun>();
            next.Configure(incoming);
            if (!next.Configured)
            {
                Destroy(root);
                return false;
            }

            // Commit the replacement only after the new gun is valid.
            dropped = equipped != null ? equipped.CaptureState() : null;
            Transform old = weaponRoot;
            if (combat != null) combat.SetFireHeld(false);
            aiming = false;
            if (motor != null) motor.SetWeaponAimFov(-1f);
            equipped = next;
            weaponRoot = root.transform;
            if (combat != null) combat.Equip(next);
            AttachModelToHand();
            if (old != null)
            {
                old.gameObject.SetActive(false); // No duplicate weapon render this frame.
                Destroy(old.gameObject);
            }
            Debug.Log("Aaris: carrying one gun: " + next.DisplayName +
                (dropped != null ? " (previous weapon dropped)" : ""));
            return true;
        }

        /// <summary>
        /// Called only after the world validates a physical floor and
        /// reserves a drop pickup. Cannot lose a gun on invalid terrain.
        /// </summary>
        public bool TryRemove(out AarisWeaponState removed)
        {
            removed = equipped != null ? equipped.CaptureState() : null;
            if (removed == null) return false;
            if (combat != null)
            {
                combat.SetFireHeld(false);
                combat.Equip(null); // Fire returns to melee Punch.
            }
            aiming = false;
            if (motor != null) motor.SetWeaponAimFov(-1f);
            Transform old = weaponRoot;
            equipped = null;
            weaponRoot = null;
            if (old != null)
            {
                old.gameObject.SetActive(false); // No duplicate weapon render this frame.
                Destroy(old.gameObject);
            }
            return true;
        }

        public bool Reload() => equipped != null && equipped.BeginReload();

        public void ToggleAim()
        {
            aiming = !aiming && HasGun;
            if (aiming && motor != null) motor.CancelAutoSprint();
            if (motor != null)
                motor.SetWeaponAimFov(aiming ? equipped.ScopeFov : -1f);
        }

        private void AttachModelToHand()
        {
            if (weaponRoot == null || equipped == null) return;
            if (hand == null && Time.unscaledTime >= nextRigSearch)
            {
                nextRigSearch = Time.unscaledTime + 1f;
                foreach (var animator in GetComponentsInChildren<Animator>(true))
                {
                    if (animator == null || animator.avatar == null ||
                        !animator.avatar.isHuman) continue;
                    hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                    if (hand != null) break;
                }
            }
            // Keep the equipped weapon as a CHILD OF PLAYER, not the
            // imported FBX bone. Imported humanoid rigs can have 0.01x or
            // 100x transforms. Parenting the gun under that bone scaled it
            // unpredictably, often making it tiny or enormous.
            if (weaponRoot.parent != transform)
                weaponRoot.SetParent(transform, false);
            Quaternion grip = Quaternion.Euler(equipped.Spec.handRotation);
            if (hand != null)
                weaponRoot.SetPositionAndRotation(
                    hand.TransformPoint(equipped.Spec.handOffset),
                    hand.rotation * grip);
            else
                weaponRoot.SetPositionAndRotation(
                    transform.TransformPoint(new Vector3(0.29f, 1.1f, 0.3f)),
                    transform.rotation * grip);
        }

        private void LateUpdate()
        {
            // Follow the LAST evaluated humanoid pose every rendered frame,
            // not only the frame in which a weapon is first equipped.
            if (equipped != null) AttachModelToHand();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.E)) RequestPickup();
            if (Input.GetKeyDown(KeyCode.G)) DropCurrent();
            if (Input.GetKeyDown(KeyCode.R)) Reload();
            // Mouse-right is already used by the camera-look motor.
            // ADS on desktop uses V; mobile has the explicit ADS button.
            if (Input.GetKeyDown(KeyCode.V)) ToggleAim();
        }

        private void OnDisable()
        {
            aiming = false;
            if (motor != null) motor.SetWeaponAimFov(-1f);
            if (combat != null) combat.SetFireHeld(false);
        }
    }
}
