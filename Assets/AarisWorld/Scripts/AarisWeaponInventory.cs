using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// Two physical weapon slots: the active gun follows the right hand and
    /// the previous gun stays visible on a back socket. Picking up a third
    /// reserves the oldest stowed gun on the floor before transfer.
    /// Fist mode holsters rather than silently destroying owned weapons.
    /// </summary>
    // Run after AarisCharacterVisual (order +200): its Playables update
    // the hand bones before this class places the gun at that pose.
    [DefaultExecutionOrder(310)]
    [DisallowMultipleComponent, RequireComponent(typeof(AarisBattleActions))]
    public sealed class AarisWeaponInventory : MonoBehaviour
    {
        private AarisEquippedGun equipped;
        private Transform weaponRoot;
        private AarisEquippedGun holstered;
        private Transform holsterRoot;
        private Transform chest;
        private bool fistsMode;
        private AarisBattleActions combat;
        private AarisThirdPersonPlayer motor;
        private AarisWeaponLootWorld lootWorld;
        private Transform hand;
        private float nextRigSearch;
        private bool aiming;

        public AarisEquippedGun Current => equipped;
        public bool Aiming => aiming;
        public bool HasGun => !fistsMode && equipped != null && equipped.Configured;
        public bool HasHolsteredGun => holstered != null && holstered.Configured;
        public AarisWeaponState ReplacementOnPickup =>
            holstered != null ? holstered.CaptureState() : null;
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
        /// Atomic two-slot pickup. Preload and validate the FBX before changing
        /// ownership. If both slots are occupied, the oldest stowed state has
        /// already been reserved as persistent world loot by the caller.
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
            mesh.localPosition = Vector3.zero;
            mesh.localRotation = Quaternion.identity;
            // Renderer.bounds is an asynchronously updated world-space AABB.
            // Reading it again immediately after scaling the FBX caused
            // 5-9 metre guns in a real Unity Play Mode audit on the Android
            // character. Measure the source MESH corners instead and scale
            // those root-local bounds mathematically (one-off equip only).
            if (!AarisWeaponMeshBounds.TryMeasure(root.transform,
                renderers, out Bounds combinedBounds))
            {
                Destroy(root);
                Debug.LogError("Aaris: weapon has invalid mesh geometry: " +
                    incoming.Spec.id);
                return false;
            }
            float longest = Mathf.Max(combinedBounds.size.x,
                combinedBounds.size.y, combinedBounds.size.z);
            float scale = Mathf.Clamp(0.86f / longest, 0.0000001f, 10f) *
                incoming.Spec.handScale;
            mesh.localScale *= scale;
            // Keep the entire gun body centered a short distance forward
            // from the animated right wrist, regardless of imported FBX pivot.
            mesh.localPosition = new Vector3(0f, -0.025f, 0.19f) -
                combinedBounds.center * scale;

            var next = root.AddComponent<AarisEquippedGun>();
            next.Configure(incoming);
            if (!next.Configured)
            {
                Destroy(root);
                return false;
            }

            // A previous right-hand gun becomes a physical back-holstered
            // weapon. Only the third gun evicts the existing back slot.
            dropped = holstered != null ? holstered.CaptureState() : null;
            Transform evictedHolster = holsterRoot;
            if (combat != null) combat.SetFireHeld(false);
            aiming = false;
            fistsMode = false;
            if (motor != null) motor.SetWeaponAimFov(-1f);
            holstered = equipped;
            holsterRoot = weaponRoot;
            equipped = next;
            weaponRoot = root.transform;
            if (combat != null) combat.Equip(next);
            AttachModelToHand();
            AttachModelToBack(holsterRoot, true);
            if (evictedHolster != null)
            {
                evictedHolster.gameObject.SetActive(false);
                Destroy(evictedHolster.gameObject);
            }
            if (Debug.isDebugBuild)
                Debug.Log("AARIS_WEAPON_EQUIP " + next.DisplayName +
                    " holstered=" + (holstered != null) +
                    " exchanged=" + (dropped != null));
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
            equipped = holstered;
            weaponRoot = holsterRoot;
            holstered = null;
            holsterRoot = null;
            fistsMode = false;
            if (combat != null) combat.Equip(equipped);
            if (old != null)
            {
                old.gameObject.SetActive(false);
                Destroy(old.gameObject);
            }
            if (equipped != null) AttachModelToHand();
            return true;
        }

        public bool SwitchWeapon()
        {
            if (equipped == null || holstered == null) return false;
            if (combat != null) combat.SetFireHeld(false);
            AarisEquippedGun oldEquipped = equipped;
            Transform oldRoot = weaponRoot;
            equipped = holstered;
            weaponRoot = holsterRoot;
            holstered = oldEquipped;
            holsterRoot = oldRoot;
            fistsMode = false;
            aiming = false;
            if (motor != null) motor.SetWeaponAimFov(-1f);
            if (combat != null) combat.Equip(equipped);
            AttachModelToHand();
            AttachModelToBack(holsterRoot, true);
            return true;
        }

        public void ToggleFists()
        {
            if (equipped == null) return;
            if (combat != null) combat.SetFireHeld(false);
            fistsMode = !fistsMode;
            aiming = false;
            if (motor != null) motor.SetWeaponAimFov(-1f);
            if (combat != null) combat.Equip(fistsMode ? null : equipped);
            if (fistsMode) AttachModelToBack(weaponRoot, true);
            else AttachModelToHand();
        }

        public bool Reload() => HasGun && equipped.BeginReload();

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
            FindRigBones();
            // Keep the equipped weapon as a CHILD OF PLAYER, not the
            // imported FBX bone. Imported humanoid rigs can have 0.01x or
            // 100x transforms. Parenting the gun under that bone scaled it
            // unpredictably, often making it tiny or enormous.
            if (weaponRoot.parent != transform)
                weaponRoot.SetParent(transform, false);
            Quaternion grip = Quaternion.Euler(equipped.Spec.handRotation);
            if (hand != null)
                weaponRoot.SetPositionAndRotation(
                    // Treat authored grip offsets as metres, not scaled FBX bone units.
                    // Some imported humanoids have 0.01x or 100x bone scale.
                    hand.position + hand.rotation * equipped.Spec.handOffset,
                    hand.rotation * grip);
            else
                weaponRoot.SetPositionAndRotation(
                    transform.TransformPoint(new Vector3(0.29f, 1.1f, 0.3f)),
                    transform.rotation * grip);
        }

        private void FindRigBones()
        {
            if (hand != null && chest != null) return;
            if (Time.unscaledTime < nextRigSearch) return;
            nextRigSearch = Time.unscaledTime + 1f;
            foreach (var animator in GetComponentsInChildren<Animator>(true))
            {
                if (animator == null || animator.avatar == null ||
                    !animator.avatar.isHuman) continue;
                if (hand == null) hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                if (chest == null) chest = animator.GetBoneTransform(HumanBodyBones.Chest);
                if (chest == null) chest = animator.GetBoneTransform(HumanBodyBones.Spine);
                if (hand != null && chest != null) break;
            }
        }

        private void AttachModelToBack(Transform root, bool rightSide)
        {
            if (root == null) return;
            if (root.parent != transform) root.SetParent(transform, false);
            float side = rightSide ? 1f : -1f;
            // The visual mesh is normalized to real metres on pickup. Keep
            // the socket in PLAYER world space, not under scaled FBX bones.
            Vector3 shoulder = chest != null ? chest.position :
                transform.TransformPoint(new Vector3(0f, 1.28f, 0f));
            Vector3 position = shoulder + transform.right * (side * .20f) -
                transform.forward * .24f - transform.up * .05f;
            Quaternion rotation = transform.rotation *
                Quaternion.Euler(16f, side * 32f, side * 53f);
            root.SetPositionAndRotation(position, rotation);
        }

        private void LateUpdate()
        {
            // Animation graph finishes before this component (execution +310).
            // Both carried meshes remain on the player, with zero physics
            // queries, no mesh cloning and no per-frame FindObjectsOfType.
            if (equipped == null && holstered == null) return;
            FindRigBones();
            if (equipped != null)
            {
                if (fistsMode) AttachModelToBack(weaponRoot, true);
                else AttachModelToHand();
            }
            if (holstered != null)
                AttachModelToBack(holsterRoot, !fistsMode);
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.E)) RequestPickup();
            if (Input.GetKeyDown(KeyCode.G)) DropCurrent();
            if (Input.GetKeyDown(KeyCode.Q)) SwitchWeapon();
            if (Input.GetKeyDown(KeyCode.X)) ToggleFists();
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
