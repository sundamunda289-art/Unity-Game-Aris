using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>
    /// Two physical weapon slots: one held and one back-holstered.
    /// Picking up a third weapon drops only the replaced hand slot, retaining
    /// magazine/reload state. Fist mode leaves both guns visibly holstered.
    /// Model roots stay under the player to avoid imported bone-scale errors.
    /// </summary>
    // Run after AarisCharacterVisual (order +200): its Playables update
    // the hand bones before this class places the gun at that pose.
    [DefaultExecutionOrder(310)]
    [DisallowMultipleComponent, RequireComponent(typeof(AarisBattleActions))]
    public sealed class AarisWeaponInventory : MonoBehaviour
    {
        private AarisEquippedGun equipped, stowed;
        private Transform weaponRoot, stowedRoot;
        private AarisBattleActions combat;
        private AarisThirdPersonPlayer motor;
        private AarisWeaponLootWorld lootWorld;
        private Transform hand, chest, leftUpperArm, leftForearm, leftHand;
        private float nextRigSearch;
        private bool aiming, fistMode;

        public AarisEquippedGun Current => equipped;
        public bool Aiming => aiming;
        public bool HasGun => equipped != null && equipped.Configured && !fistMode;
        public bool HasStowedGun => stowed != null && stowed.Configured;
        public bool HasAnyGun => equipped != null && equipped.Configured;
        public bool FistMode => fistMode;
        public bool RequiresExchangeDrop => equipped != null && stowed != null;
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

            // Commit only after the new visible FBX and weapon state are valid.
            // First replacement fills the spare/back slot without losing ammo.
            // A full inventory retains its spare and drops just the old hand gun.
            bool full = equipped != null && stowed != null;
            dropped = full ? equipped.CaptureState() : null;
            Transform removedRoot = full ? weaponRoot : null;
            if (!full && equipped != null)
            {
                stowed = equipped;
                stowedRoot = weaponRoot;
                stowed.enabled = false; // Stowed reload resolves when drawn.
            }
            ResetCombatTransition();
            equipped = next;
            weaponRoot = root.transform;
            equipped.enabled = true;
            fistMode = false;
            if (combat != null) combat.Equip(equipped);
            AttachModelsToSockets();
            if (removedRoot != null)
            {
                removedRoot.gameObject.SetActive(false);
                Destroy(removedRoot.gameObject);
            }
            Debug.Log("Aaris: equipped " + next.DisplayName +
                (dropped != null ? " (replaced hand gun dropped)" :
                 stowed != null ? " (previous gun on back)" : ""));
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
            ResetCombatTransition();
            Transform discarded = weaponRoot;
            equipped = stowed;
            weaponRoot = stowedRoot;
            stowed = null;
            stowedRoot = null;
            fistMode = false;
            if (equipped != null) equipped.enabled = true;
            if (combat != null) combat.Equip(equipped);
            if (discarded != null)
            {
                discarded.gameObject.SetActive(false);
                Destroy(discarded.gameObject);
            }
            AttachModelsToSockets();
            return true;
        }

        public bool Reload() => HasGun && equipped.BeginReload();

        /// <summary>Unarmed punching without deleting either carried FBX.</summary>
        public bool ToggleFists()
        {
            if (equipped == null) return false;
            ResetCombatTransition();
            fistMode = !fistMode;
            equipped.enabled = !fistMode;
            if (combat != null) combat.Equip(fistMode ? null : equipped);
            AttachModelsToSockets();
            return true;
        }

        /// <summary>Swap hand and back slots; preserve reload/ammo exactly.</summary>
        public bool SwapWeapons()
        {
            if (stowed == null || equipped == null) return false;
            ResetCombatTransition();
            AarisEquippedGun oldGun = equipped;
            equipped = stowed;
            stowed = oldGun;
            Transform oldRoot = weaponRoot;
            weaponRoot = stowedRoot;
            stowedRoot = oldRoot;
            fistMode = false;
            equipped.enabled = true;
            stowed.enabled = false;
            equipped.RefreshReload();
            if (combat != null) combat.Equip(equipped);
            AttachModelsToSockets();
            return true;
        }

        public void ToggleAim()
        {
            aiming = !aiming && HasGun;
            if (aiming && motor != null) motor.CancelAutoSprint();
            if (motor != null)
                motor.SetWeaponAimFov(aiming ? equipped.ScopeFov : -1f);
        }

        private void ResetCombatTransition()
        {
            if (combat != null) combat.SetFireHeld(false);
            aiming = false;
            if (motor != null) motor.SetWeaponAimFov(-1f);
        }

        private void FindHumanoidSockets()
        {
            if (hand != null && chest != null && leftUpperArm != null &&
                leftForearm != null && leftHand != null) return;
            if (Time.unscaledTime < nextRigSearch) return;
            nextRigSearch = Time.unscaledTime + 1f;
            foreach (var animator in GetComponentsInChildren<Animator>(true))
            {
                if (animator == null || animator.avatar == null ||
                    !animator.avatar.isValid || !animator.avatar.isHuman) continue;
                hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                chest = animator.GetBoneTransform(HumanBodyBones.UpperChest);
                if (chest == null) chest = animator.GetBoneTransform(HumanBodyBones.Chest);
                if (chest == null) chest = animator.GetBoneTransform(HumanBodyBones.Spine);
                leftUpperArm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                leftForearm = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
                if (hand != null) break;
            }
        }

        private void PlaceOnBack(Transform item, bool rightShoulder)
        {
            if (item == null) return;
            if (item.parent != transform) item.SetParent(transform, false);
            // World-metre sockets and owner-space rotation avoid 0.01/100x FBX
            // rig transforms. Two guns cross on the back in Fist mode.
            Vector3 origin = chest != null ? chest.position :
                transform.TransformPoint(new Vector3(0f, 1.35f, 0f));
            float side = rightShoulder ? 1f : -1f;
            item.SetPositionAndRotation(
                origin + transform.rotation * new Vector3(side * .17f, .02f, -.21f),
                transform.rotation * Quaternion.Euler(12f, side * 12f, side * 63f));
        }

        private void AttachModelsToSockets()
        {
            if (equipped == null || weaponRoot == null) return;
            FindHumanoidSockets();
            if (stowedRoot != null) PlaceOnBack(stowedRoot, true);
            if (fistMode)
            {
                PlaceOnBack(weaponRoot, stowedRoot != null ? false : true);
                return;
            }
            if (weaponRoot.parent != transform)
                weaponRoot.SetParent(transform, false);
            Quaternion grip = Quaternion.Euler(equipped.Spec.handRotation);
            if (hand != null)
                weaponRoot.SetPositionAndRotation(
                    hand.position + hand.rotation * equipped.Spec.handOffset,
                    hand.rotation * grip);
            else
                weaponRoot.SetPositionAndRotation(
                    transform.TransformPoint(new Vector3(.29f, 1.1f, .3f)),
                    transform.rotation * grip);
        }

        // A bounded analytic two-bone support-arm solver. It runs only when a
        // Humanoid and an equipped gun both exist; no extra Animator layer,
        // per-frame allocations, mesh raycasts or Animation Rigging dependency.
        // The right hand owns the weapon pose; the support hand aims for the
        // foregrip and bends using the existing elbow pole from the animation.
        private void AlignSupportHand()
        {
            if (!HasGun || hand == null || leftUpperArm == null ||
                leftForearm == null || leftHand == null) return;
            Vector3 shoulder = leftUpperArm.position;
            Vector3 elbow = leftForearm.position;
            Vector3 wrist = leftHand.position;
            float upperLength = Vector3.Distance(shoulder, elbow);
            float foreLength = Vector3.Distance(elbow, wrist);
            if (upperLength < .05f || foreLength < .05f) return;
            Vector3 desired = weaponRoot.TransformPoint(new Vector3(-.045f, -.035f, .31f));
            Vector3 delta = desired - shoulder;
            float distance = delta.magnitude;
            if (distance < .05f) return;
            Vector3 direction = delta / distance;
            float limit = Mathf.Max(.06f, upperLength + foreLength - .015f);
            float reach = Mathf.Clamp(distance,
                Mathf.Abs(upperLength - foreLength) + .01f, limit);
            Vector3 target = shoulder + direction * reach;
            float along = (upperLength * upperLength -
                foreLength * foreLength + reach * reach) / (2f * reach);
            float height = Mathf.Sqrt(Mathf.Max(0f,
                upperLength * upperLength - along * along));
            Vector3 pole = Vector3.ProjectOnPlane(elbow - shoulder, direction);
            if (pole.sqrMagnitude < .0001f)
                pole = Vector3.ProjectOnPlane(transform.forward, direction);
            if (pole.sqrMagnitude < .0001f) return;
            Vector3 elbowTarget = shoulder + direction * along +
                pole.normalized * height;
            Vector3 beforeUpper = leftForearm.position - shoulder;
            Vector3 afterUpper = elbowTarget - shoulder;
            if (beforeUpper.sqrMagnitude < .001f || afterUpper.sqrMagnitude < .001f)
                return;
            leftUpperArm.rotation = Quaternion.FromToRotation(beforeUpper, afterUpper) *
                leftUpperArm.rotation;
            Vector3 beforeFore = leftHand.position - leftForearm.position;
            Vector3 afterFore = target - leftForearm.position;
            if (beforeFore.sqrMagnitude < .001f || afterFore.sqrMagnitude < .001f)
                return;
            leftForearm.rotation = Quaternion.FromToRotation(beforeFore, afterFore) *
                leftForearm.rotation;
        }

        private void LateUpdate()
        {
            if (equipped == null) return;
            AttachModelsToSockets();
            AlignSupportHand();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.E)) RequestPickup();
            if (Input.GetKeyDown(KeyCode.G)) DropCurrent();
            if (Input.GetKeyDown(KeyCode.R)) Reload();
            if (Input.GetKeyDown(KeyCode.Alpha1)) SwapWeapons();
            if (Input.GetKeyDown(KeyCode.Alpha2)) ToggleFists();
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
