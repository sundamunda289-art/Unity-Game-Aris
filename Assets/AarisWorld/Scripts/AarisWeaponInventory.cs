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
        private Transform rightUpperArm, rightForearm;
        private CharacterController capsule;
        private float nextRigSearch;
        private bool aiming, fistMode;
        public bool IsSprintHolstered { get; private set; }

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
            capsule = GetComponent<CharacterController>();
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
                leftForearm != null && leftHand != null &&
                rightUpperArm != null && rightForearm != null) return;
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
                rightUpperArm = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                rightForearm = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
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
            if (fistMode || IsSprintHolstered)
            {
                PlaceOnBack(weaponRoot, stowedRoot != null ? false : true);
                return;
            }
            if (weaponRoot.parent != transform)
                weaponRoot.SetParent(transform, false);
            // All five reviewed rifle/SMG FBXs point down local +Z.
            // Anchor the receiver just ahead of the chest, not to the
            // animated pistol wrist: the barrel must follow the camera
            // when firing/ADS/prone, regardless of the body animation.
            if (chest != null && hand != null && rightUpperArm != null &&
                rightForearm != null)
            {
                Vector3 forward = transform.forward;
                Camera view = motor != null ? motor.AimCamera : null;
                if (view != null && (aiming || (combat != null &&
                    (combat.FireHeld ||
                     combat.Stance == AarisBattleActions.PlayerStance.Prone))))
                    forward = view.transform.forward;
                if (forward.sqrMagnitude < 0.01f) forward = transform.forward;
                Quaternion aimRotation = Quaternion.LookRotation(forward, Vector3.up);
                Vector3 receiver = chest.position +
                    transform.right * 0.12f + transform.forward * 0.13f -
                    Vector3.up * 0.105f;
                weaponRoot.SetPositionAndRotation(receiver, aimRotation);
            }
            else
            {
                // Missing Humanoid avatar: retain the working legacy grip
                // instead of hiding/dropping an otherwise usable weapon.
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
        }

        // No per-frame allocations or Animation Rigging dependency: the
        // existing Humanoid bones form analytic shoulder/elbow/wrist chains.
        // Both wrists are solved AFTER the PlayableGraph (execution order +310).
        private static void ReachArm(Transform upper, Transform forearm,
            Transform wrist, Vector3 desired, Vector3 fallbackPole)
        {
            if (upper == null || forearm == null || wrist == null) return;
            Vector3 shoulder = upper.position;
            Vector3 elbow = forearm.position;
            float upperLength = Vector3.Distance(shoulder, elbow);
            float foreLength = Vector3.Distance(elbow, wrist.position);
            if (upperLength < 0.05f || foreLength < 0.05f) return;
            Vector3 toGrip = desired - shoulder;
            float rawDistance = toGrip.magnitude;
            if (rawDistance < 0.05f) return;
            Vector3 direction = toGrip / rawDistance;
            float reach = Mathf.Clamp(rawDistance,
                Mathf.Abs(upperLength - foreLength) + 0.01f,
                Mathf.Max(0.06f, upperLength + foreLength - 0.015f));
            Vector3 target = shoulder + direction * reach;
            float along = (upperLength * upperLength -
                foreLength * foreLength + reach * reach) / (2f * reach);
            float perpendicular = Mathf.Sqrt(Mathf.Max(0f,
                upperLength * upperLength - along * along));
            Vector3 pole = Vector3.ProjectOnPlane(elbow - shoulder, direction);
            if (pole.sqrMagnitude < 0.0001f)
                pole = Vector3.ProjectOnPlane(fallbackPole, direction);
            if (pole.sqrMagnitude < 0.0001f) return;
            Vector3 targetElbow = shoulder + direction * along +
                pole.normalized * perpendicular;
            Vector3 oldUpper = forearm.position - shoulder;
            Vector3 newUpper = targetElbow - shoulder;
            if (oldUpper.sqrMagnitude < 0.001f || newUpper.sqrMagnitude < 0.001f)
                return;
            upper.rotation = Quaternion.FromToRotation(oldUpper, newUpper) *
                upper.rotation;
            Vector3 oldForearm = wrist.position - forearm.position;
            Vector3 newForearm = target - forearm.position;
            if (oldForearm.sqrMagnitude < 0.001f ||
                newForearm.sqrMagnitude < 0.001f) return;
            forearm.rotation = Quaternion.FromToRotation(oldForearm, newForearm) *
                forearm.rotation;
        }

        private void AlignSupportHand()
        {
            if (!HasGun || IsSprintHolstered || hand == null ||
                weaponRoot == null || leftUpperArm == null ||
                leftForearm == null || leftHand == null ||
                rightUpperArm == null || rightForearm == null) return;
            // Receiver at right trigger, front grip down barrel +Z.
            // Gun size/foregrip calibration is owned by the separate weapon
            // presentation-length review; preserve one consistent root axis.
            ReachArm(rightUpperArm, rightForearm, hand,
                weaponRoot.TransformPoint(new Vector3(0.015f, -0.045f, -0.045f)),
                transform.right);
            ReachArm(leftUpperArm, leftForearm, leftHand,
                weaponRoot.TransformPoint(new Vector3(-0.045f, -0.035f, 0.31f)),
                -transform.right);
        }

        private void LateUpdate()
        {
            if (equipped == null)
            {
                IsSprintHolstered = false;
                return;
            }
            // Never holster on a slow crouch/crawl, while ADS, or during
            // a trigger pull. The actual motor speed gates this, not just
            // a still-latched sprint HUD toggle.
            Vector3 speed = capsule != null ? capsule.velocity : Vector3.zero;
            float planarSpeedSqr = speed.x * speed.x + speed.z * speed.z;
            bool sprintIntent = motor != null && motor.IsAutoSprinting ||
                Input.GetKey(KeyCode.LeftShift);
            IsSprintHolstered = !fistMode && !aiming && combat != null &&
                !combat.FireHeld &&
                combat.Stance == AarisBattleActions.PlayerStance.Standing &&
                capsule != null && capsule.isGrounded &&
                sprintIntent && planarSpeedSqr > 3.75f * 3.75f;
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
