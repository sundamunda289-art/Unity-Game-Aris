using UnityEngine;

namespace Aaris.MapReview
{
    /// <summary>Grounded map inspection: saved human, terrain collisions and a shoulder view.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(CharacterController))]
    public sealed class AarisThirdPersonPlayer : MonoBehaviour
    {
        [SerializeField] private Camera followCamera;
        [SerializeField] private Transform leftArm, rightArm, leftLeg, rightLeg;
        [SerializeField] private float walkSpeed = 4.2f, runSpeed = 6.2f;
        [Header("Battle Royale locomotion — metres per second")]
        [SerializeField, Min(0f)] private float unarmedSprintSpeed = 6.5f;
        [SerializeField, Min(0f)] private float armedSprintSpeed = 5.7f;
        [SerializeField, Min(0f)] private float crouchMoveSpeed = 2.4f;
        [SerializeField, Min(0f)] private float proneMoveSpeed = 1.45f;
        [SerializeField, Min(0f)] private float combatMoveSpeed = 2.0f;
        [SerializeField, Min(0f)] private float adsMoveSpeed = 2.65f;
        [SerializeField, Min(1f)] private float weaponTurnResponse = 17f;
        [SerializeField, Min(0.01f)] private float horizontalAcceleration = 13f;
        [SerializeField, Min(0.01f)] private float horizontalBraking = 19f;
        [Header("Jump, slope and third-person camera feel")]
        [SerializeField, Min(1.1f)] private float earlyJumpReleaseGravity = 1.35f;
        [SerializeField, Range(0.0f, 0.18f)] private float minimumJumpAscentSeconds = 0.12f;
        [SerializeField, Range(0f, 0.6f)] private float downhillProbeDistance = 0.32f;
        [SerializeField, Min(0.1f)] private float groundedDownVelocity = 5.5f;
        [SerializeField, Range(0.2f, 1f)] private float airborneSteering = 0.78f;
        [SerializeField, Range(0.05f, 0.6f)] private float stanceCameraBlendSeconds = 0.15f;
        [SerializeField] private float jumpHeight = 1.35f, gravity = 18.5f;
        [SerializeField] private float followDistance = 4.2f, pivotHeight = 1.45f;
        [SerializeField] private float lookSensitivity = 0.12f;
        [SerializeField] private float minLookPitch = -70f, maxLookPitch = 70f;
        [SerializeField] private float seaLevel = -38.07552f;
        [SerializeField] private AarisNearbyColliders nearbyColliders;
        private CharacterController controller;
        private Terrain[] terrains;
        // Layer 8 is reserved for the saved review player in TagManager and its prefab.
        private const int WorldMask = Physics.DefaultRaycastLayers & ~(1 << 8);
        private Vector3 spawn, cameraVelocity;
        private Quaternion spawnRotation;
        private Quaternion leftArmRest, rightArmRest, leftLegRest, rightLegRest;
        private float yaw, pitch = 12f, verticalVelocity, gait, lastGrounded = -10f, jumpUntil = -10f;
        private float pendingAerialYaw, pendingAerialPitch;
        private bool hasPendingAerialLook;
        private float uiScale = 1f, travelSpeed;
        private bool running, previousSimulateMouse, inputConfigured;
        private int moveFinger = -1, lookFinger = -1;
        private Vector2 moveInput;
        private Vector3 lastMouse;
        private Rect jumpButton, runButton, homeButton, reportButton, fpsButton;
        private float reportCopiedUntil;
        private Vector2 stickCenter;
        private GUIStyle buttonStyle, labelStyle;
        private Texture2D disc;
        private AarisBattleActions battle;
        private AarisWeaponInventory weaponInventory;
        private float nextOptionalComponentRefresh;
        private Vector3 horizontalVelocity;
        private float normalCameraFov;
        private float weaponAimFov = -1f;

        /// <summary>Single camera authority: scopes cannot fight sprint FOV.</summary>
        public void SetWeaponAimFov(float fov)
        {
            weaponAimFov = fov > 0f ? Mathf.Clamp(fov, 8f, normalCameraFov) : -1f;
        }

        public void ApplyWeaponRecoil(float pitchKick, float yawKick)
        {
            pitch = Mathf.Clamp(pitch - Mathf.Clamp(pitchKick, 0f, 20f),
                minLookPitch, maxLookPitch);
            yaw += Mathf.Clamp(yawKick, -5f, 5f);
        }
        // Skip repeated native limb Transform writes once the legacy fallback
        // has reached a grounded idle pose; motion and punch edges re-arm it.
        private bool legacyIdlePoseApplied;
        private float savedGroundStepOffset;
        private float smoothedEyeHeight, eyeHeightVelocity;
        private bool jumpButtonHeld;
        private float lastJumpStarted = -10f;
        private float lastLanding = -10f;
        public bool IsInAir => controller != null && !controller.isGrounded;
        public float LastLandingTime => lastLanding;
        public float LastJumpStartedTime => lastJumpStarted;
        public Camera AimCamera => followCamera;

        // The original lightweight placeholder has articulated arm/leg pivots.
        // If an Android build cannot initialize the optional Humanoid rig,
        // never leave this visible fallback frozen in a horizontal T-pose
        // during the airplane / parachute sequence. Visuals only: no physics.
        internal void SetLegacyAerialPose(bool beneathCanopy)
        {
            // The aerial visual can change the limbs while the foot motor is off.
            // The next grounded Update must restore the idle pose once.
            legacyIdlePoseApplied = false;
            // Angles fold a horizontal legacy T-pose into a suspended,
            // riser-holding pose (or a slightly spread freefall posture).
            float armAngle = beneathCanopy ? 48f : -38f;
            if (leftArm != null)
                leftArm.localRotation = leftArmRest *
                    Quaternion.Euler(-10f, 0f, -armAngle);
            if (rightArm != null)
                rightArm.localRotation = rightArmRest *
                    Quaternion.Euler(-10f, 0f, armAngle);
            if (leftLeg != null)
                leftLeg.localRotation = leftLegRest * Quaternion.Euler(13f, 0f, -6f);
            if (rightLeg != null)
                rightLeg.localRotation = rightLegRest * Quaternion.Euler(-9f, 0f, 6f);
        }

        internal void RestoreLegacyAerialPose()
        {
            legacyIdlePoseApplied = false;
            if (leftArm != null) leftArm.localRotation = leftArmRest;
            if (rightArm != null) rightArm.localRotation = rightArmRest;
            if (leftLeg != null) leftLeg.localRotation = leftLegRest;
            if (rightLeg != null) rightLeg.localRotation = rightLegRest;
        }
        public bool IsAutoSprinting => running;

        public void CancelAutoSprint() => running = false;

        public void ToggleAutoSprint()
        {
            if (battle == null) battle = GetComponent<AarisBattleActions>();
            if (weaponInventory == null) weaponInventory = GetComponent<AarisWeaponInventory>();
            if (!running && weaponInventory != null && weaponInventory.Aiming) return;
            if (!running && battle != null && !battle.TryStand()) return;
            running = !running;
        }

        public void RequestJump()
        {
            if (battle == null) battle = GetComponent<AarisBattleActions>();
            // A crouched jump is one action, not two taps. Retain a valid
            // grounded timestamp across the CharacterController resize,
            // without granting an extra mid-air jump when there's no floor.
            if (battle != null && battle.Stance != AarisBattleActions.PlayerStance.Standing)
            {
                bool groundedBeforeStand = controller != null && controller.isGrounded;
                if (!battle.TryStand()) return; // Closed ceilings still block.
                if (groundedBeforeStand) lastGrounded = Time.unscaledTime;
            }
            // Buffered jump survives a short resize/landing transition.
            jumpUntil = Time.unscaledTime + 0.20f;
        }

        public void SetJumpHeld(bool held)
        {
            jumpButtonHeld = held;
            if (held) RequestJump();
        }

        private void Awake()
        {
            controller = GetComponent<CharacterController>();
            // Resolve normal gameplay components once. Optional components added
            // later are still discovered by a bounded retry in Update.
            battle = GetComponent<AarisBattleActions>();
            weaponInventory = GetComponent<AarisWeaponInventory>();
            savedGroundStepOffset = controller.stepOffset;
            terrains = Terrain.activeTerrains;
            if (followCamera == null) followCamera = Camera.main;
            if (followCamera == null) { enabled = false; Debug.LogError("Saved player camera is missing."); return; }
            normalCameraFov = followCamera.fieldOfView;
            smoothedEyeHeight = pivotHeight;
            previousSimulateMouse = Input.simulateMouseWithTouches;
            Input.simulateMouseWithTouches = false;
            inputConfigured = true;
            // AarisMobileRuntime is the FPS authority when present (execution order -300).
            // Do not override a 30/60 FPS device setting from another Awake().
            if (FindFirstObjectByType<AarisMobileRuntime>() == null)
                Application.targetFrameRate = 60;
            if (nearbyColliders == null) nearbyColliders = FindFirstObjectByType<AarisNearbyColliders>();
            spawn = transform.position;
            spawnRotation = transform.rotation;
            spawn.y = GroundHeight(spawn) + controller.skinWidth + 0.05f;
            controller.enabled = false;
            transform.position = spawn;
            controller.enabled = true;
            yaw = transform.eulerAngles.y;
            leftArmRest = leftArm != null ? leftArm.localRotation : Quaternion.identity;
            rightArmRest = rightArm != null ? rightArm.localRotation : Quaternion.identity;
            leftLegRest = leftLeg != null ? leftLeg.localRotation : Quaternion.identity;
            rightLegRest = rightLeg != null ? rightLeg.localRotation : Quaternion.identity;
            lastMouse = Input.mousePosition;
            PlaceCamera(true);
            // The saved player and any later player spawn use the same audio setup.
            if (GetComponent<AarisAudioDirector>() == null)
                gameObject.AddComponent<AarisAudioDirector>();
        }

        private void OnDestroy()
        {
            if (inputConfigured) Input.simulateMouseWithTouches = previousSimulateMouse;
            if (disc != null) Destroy(disc);
        }
        private void OnApplicationFocus(bool focus) { ClearInput(); lastMouse = Input.mousePosition; }
        private void OnApplicationPause(bool paused) { if (paused) ClearInput(); }
        private void ClearInput()
        {
            moveFinger = lookFinger = -1;
            moveInput = Vector2.zero;
            jumpUntil = -10f;
            jumpButtonHeld = false;
            running = false;
            horizontalVelocity = Vector3.zero;
            if (battle != null) battle.SetFireHeld(false);
        }

        private void Layout()
        {
            uiScale = Mathf.Clamp(Mathf.Min(Screen.width / 960f, Screen.height / 540f), 0.5f, 3f);
            Rect safe = Screen.safeArea;
            float left = safe.xMin / uiScale + 12f;
            float right = safe.xMax / uiScale - 12f;
            float bottom = (Screen.height - safe.yMin) / uiScale - 12f;
            float top = (Screen.height - safe.yMax) / uiScale + 12f;
            stickCenter = new Vector2(left + 88f, bottom - 90f);
            jumpButton = new Rect(right - 76f, bottom - 78f, 76f, 72f);
            runButton = new Rect(right - 162f, bottom - 78f, 76f, 72f);
            homeButton = new Rect(left, top, 92f, 36f);
            reportButton = new Rect(right - 100f, top, 100f, 36f);
            fpsButton = new Rect(right - 212f, top, 100f, 36f);
        }
        internal Vector2 MoveStickScreenCenter =>
            new Vector2(stickCenter.x * uiScale, Screen.height - stickCenter.y * uiScale);
        internal float MoveStickScreenScale => uiScale;
        internal Vector2 MoveStickInput => moveInput;

        private Vector2 ToGUI(Vector2 p) => new Vector2(p.x / uiScale, (Screen.height - p.y) / uiScale);
        private bool OverButton(Vector2 p) =>
            (!AarisBattleHUD.IsVisible && (jumpButton.Contains(p) || runButton.Contains(p))) ||
            (Debug.isDebugBuild && (homeButton.Contains(p) ||
                reportButton.Contains(p) || fpsButton.Contains(p)));
        private void PressButton(Vector2 p)
        {
            if (!AarisBattleHUD.IsVisible && jumpButton.Contains(p)) RequestJump();
            else if (!AarisBattleHUD.IsVisible && runButton.Contains(p)) ToggleAutoSprint();
            else if (homeButton.Contains(p)) ResetPlayer();
            else if (reportButton.Contains(p))
            {
                GUIUtility.systemCopyBuffer = AarisMobileRuntime.LatestReport;
                reportCopiedUntil = Time.unscaledTime + 2;
            }
            else if (fpsButton.Contains(p)) Application.targetFrameRate = Application.targetFrameRate == 60 ? 30 : 60;
        }
        private void Look(Vector2 delta)
        {
            // Match the airborne camera's device-independent drag response.
            // Keep the serialized sensitivity as a user-tunable multiplier.
            // Upward touch movement looks up in both gameplay phases.
            float scale = Mathf.Clamp(720f / Mathf.Max(360f, Screen.height), 0.75f, 1.5f);
            float sensitivity = lookSensitivity / 0.12f;
            yaw += delta.x * 0.18f * scale * sensitivity;
            pitch = Mathf.Clamp(pitch - delta.y * 0.15f * scale * sensitivity,
                minLookPitch, maxLookPitch);
        }
        // Battle HUD's Fire/Jump drag uses the SAME camera authority as the
        // free-look touch finger. The HUD previously called this method but
        // the motor never defined it, breaking Unity C# compilation.
        // Ignore drags while another system owns the camera (flight/vehicle).
        public void ForwardLookDelta(Vector2 pixels)
        {
            if (!isActiveAndEnabled || pixels.sqrMagnitude < 0.0001f) return;
            Look(pixels);
        }
        private void ReadInput()
        {
            Layout();
            for (int i = 0; i < Input.touchCount; i++)
            {
                Touch t = Input.GetTouch(i);
                Vector2 p = ToGUI(t.position);
                if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                {
                    if (moveFinger == t.fingerId) { moveFinger = -1; moveInput = Vector2.zero; }
                    if (lookFinger == t.fingerId) lookFinger = -1;
                    continue;
                }
                if (t.phase == TouchPhase.Began)
                {
                    // The joystick takes ownership BEFORE HUD capture rejects
                    // touches in its visual ring. A second finger in the ring
                    // cannot become a camera swipe or steal the first finger.
                    if (moveFinger == -1 && (p - stickCenter).sqrMagnitude < 110f * 110f)
                        moveFinger = t.fingerId;
                    else
                    {
                        if (AarisBattleHUD.CapturesScreenPosition(t.position) ||
                            AarisWeaponHUD.CapturesScreenPosition(t.position)) continue;
                        if (OverButton(p)) { PressButton(p); continue; }
                        if (lookFinger == -1 && p.x > Screen.width / uiScale * 0.4f)
                            lookFinger = t.fingerId;
                    }
                }
                if (moveFinger == t.fingerId)
                {
                    Vector2 delta = (p - stickCenter) / 68f;
                    moveInput = Vector2.ClampMagnitude(new Vector2(delta.x, -delta.y), 1f);
                }
                else if (lookFinger == t.fingerId && t.phase == TouchPhase.Moved) Look(t.deltaPosition);
            }
            if (Input.touchCount == 0)
            {
                moveFinger = lookFinger = -1;
                moveInput = new Vector2((Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1f : 0f)
                    - (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1f : 0f),
                    (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1f : 0f)
                    - (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1f : 0f));
                Vector3 mouse = Input.mousePosition;
                Vector2 gui = ToGUI(mouse);
                if (Input.GetMouseButtonDown(0) && OverButton(gui)) PressButton(gui);
                else if (Input.GetMouseButton(0) && (gui - stickCenter).sqrMagnitude < 110f * 110f)
                    moveInput = Vector2.ClampMagnitude(new Vector2(gui.x - stickCenter.x, stickCenter.y - gui.y) / 68f, 1f);
                if (Input.GetMouseButton(1)) Look(mouse - lastMouse);
                lastMouse = mouse;
            }
            if (Input.GetKeyDown(KeyCode.Space)) RequestJump();
            if (Input.GetKeyDown(KeyCode.C)) { if (battle != null) battle.ToggleCrouch(); CancelAutoSprint(); }
            if (Input.GetKeyDown(KeyCode.Z)) { if (battle != null) battle.ToggleProne(); CancelAutoSprint(); }
            // E belongs to weapon pickup. Auto-sprint uses T, avoiding a
            // simultaneous sprint toggle when exchanging guns.
            if (Input.GetKeyDown(KeyCode.T)) ToggleAutoSprint();
            if (battle != null && Input.GetKeyDown(KeyCode.LeftControl)) battle.SetFireHeld(true);
            if (battle != null && Input.GetKeyUp(KeyCode.LeftControl)) battle.SetFireHeld(false);
            // F belongs exclusively to vehicle DRIVE (never reset player).
            if (Input.GetKeyDown(KeyCode.H)) ResetPlayer();
        }

        private void Update()
        {
            // Avoid native component searches on every Android frame when
            // optional battle/weapon scripts are absent in a map-only scene.
            // Late-added components still become available within one second.
            if ((battle == null || weaponInventory == null) &&
                Time.unscaledTime >= nextOptionalComponentRefresh)
            {
                nextOptionalComponentRefresh = Time.unscaledTime + 1f;
                if (battle == null) battle = GetComponent<AarisBattleActions>();
                if (weaponInventory == null) weaponInventory = GetComponent<AarisWeaponInventory>();
            }
            ReadInput();
            // Do not silently discard elapsed time on 10–20 FPS phones.
            // A 50ms cap previously made a character at 10 FPS move at
            // HALF speed. Cap only exceptional >150ms hitches for safety.
            float dt = Mathf.Clamp(Time.deltaTime, 0f, 0.15f);
            if (dt <= 0f) return;
            float now = Time.unscaledTime;
            // CharacterController.isGrounded reflects the PREVIOUS Move.
            // Single movement authority retains short coyote/buffer windows.
            bool wasGrounded = controller.isGrounded;
            // Both downhill checks run before Move, so a missed support sweep
            // cannot acquire different geometry later in this same frame.
            // Reuse the negative result instead of repeating an expensive
            // Physics.SphereCast on every airborne frame.
            bool downhillProbeChecked = false;
            if (!wasGrounded && verticalVelocity <= 0f &&
                now - lastJumpStarted > 0.18f)
            {
                downhillProbeChecked = true;
                wasGrounded = HasWalkableGroundWithinStep();
            }
            if (wasGrounded)
            {
                lastGrounded = now;
                if (verticalVelocity < 0f) verticalVelocity = -groundedDownVelocity;
            }
            bool launchJump = now <= jumpUntil && now - lastGrounded <= 0.15f;
            // A prone/crouched capsule must not jump through walls or ceilings.
            // Stand only when clearance permits; keep a single motor authority.
            if (launchJump && battle != null && !battle.TryStand())
            {
                launchJump = false;
                jumpUntil = -10f;
            }
            if (launchJump)
            {
                verticalVelocity = Mathf.Sqrt(2f * gravity * jumpHeight);
                lastJumpStarted = now;
                jumpUntil = lastGrounded = -10f;
            }

            // CharacterController.stepOffset is for grounded stair traversal.
            // Applying it while rising in a jump may snap the capsule up
            // the edge of a wall. Restore the selected stance's step height
            // as soon as a grounded Move succeeds.
            float groundedStep = battle != null
                ? battle.GroundStepOffset : savedGroundStepOffset;
            float requestedStep = !launchJump && (wasGrounded || verticalVelocity <= 0f)
                ? groundedStep : 0f;
            // Stance and jump transitions still apply immediately; stable
            // frames avoid reconfiguring the native CharacterController.
            if (!Mathf.Approximately(controller.stepOffset, requestedStep))
                controller.stepOffset = requestedStep;

            // Sole controller motor: no extra movement component or double
            // gravity. Touch, keyboard and auto-sprint use one intent pipeline.
            if (running && moveInput.y < -0.25f) running = false;
            // Shooting while grounded switches to combat gait. In a jump the
            // previous running momentum is already in horizontalVelocity;
            // do not erase sprint intent merely because Fire is held mid-air.
            // Landing restores the standard slower grounded combat gait.
            bool airborneIntent = launchJump || !wasGrounded;
            if (battle != null && battle.FireHeld && !airborneIntent) running = false;
            bool isAiming = weaponInventory != null && weaponInventory.Aiming;
            if (isAiming) running = false;
            bool sprinting = (running || Input.GetKey(KeyCode.LeftShift)) &&
                !isAiming && (battle == null || !battle.FireHeld || airborneIntent);
            // Holding Shift while crouched/prone must not silently force
            // a stand transition or apply sprint speed under low ceilings.
            if (sprinting && battle != null &&
                battle.Stance != AarisBattleActions.PlayerStance.Standing)
                sprinting = false;
            Vector2 intent = moveInput;
            if (running && intent.sqrMagnitude < 0.04f) intent = Vector2.up;

            Vector3 wish = Quaternion.Euler(0f, yaw, 0f) *
                new Vector3(intent.x, 0f, intent.y);
            wish = Vector3.ClampMagnitude(wish, 1f);

            float speed = sprinting
                ? (battle != null && battle.IsArmed ? armedSprintSpeed : unarmedSprintSpeed)
                : walkSpeed;
            if (battle != null && !sprinting)
            {
                if (battle.Stance == AarisBattleActions.PlayerStance.Crouching) speed = crouchMoveSpeed;
                else if (battle.Stance == AarisBattleActions.PlayerStance.Prone) speed = proneMoveSpeed;
                else if (battle.FireHeld) speed = combatMoveSpeed;
            }
            // ADS is deliberate combat movement, not a sprint. Crouch and
            // prone stay slower; no hidden second transform/movement motor.
            if (isAiming) speed = Mathf.Min(speed, adsMoveSpeed);

            Vector3 desiredVelocity = wish * speed;
            float response = desiredVelocity.sqrMagnitude < 0.001f
                ? horizontalBraking : horizontalAcceleration;
            if (!wasGrounded) response *= airborneSteering;
            horizontalVelocity = Vector3.MoveTowards(horizontalVelocity,
                desiredVelocity, response * dt);
            // On a stance change, do not let sprint inertia carry a prone or
            // crouched capsule faster than its authored movement ceiling.
            if (battle != null && battle.Stance != AarisBattleActions.PlayerStance.Standing)
                horizontalVelocity = Vector3.ClampMagnitude(horizontalVelocity, speed);
            Vector3 candidate = transform.position + horizontalVelocity * dt;
            // Original 2km map bounds and shoreline/bridge checks survive.
            // No horizontal displacement: let CharacterController handle vertical
            // grounding without repeating terrain samples and shoreline/prone
            // obstacle raycasts. Full checks still run whenever the player moves.
            if (horizontalVelocity.sqrMagnitude > 0.000001f)
            {
                if (Mathf.Abs(candidate.x) > 975f || Mathf.Abs(candidate.z) > 975f ||
                    !SafeFooting(candidate))
                    horizontalVelocity = Vector3.zero; // shoreline / map boundary
                else if (battle != null &&
                         !battle.CanAdvanceProne(candidate, horizontalVelocity))
                    horizontalVelocity = ProneWallSlide(horizontalVelocity, dt);
            }

            // Camera-relative strafe during ADS and firing. Without this,
            // a standing player could shoot sideways while the gun and torso
            // still faced their LAST walking direction.
            bool combatFacing = battle != null && battle.IsArmed &&
                (isAiming || battle.FireHeld);
            if (combatFacing)
            {
                transform.rotation = Quaternion.Slerp(transform.rotation,
                    Quaternion.Euler(0f, yaw, 0f),
                    1f - Mathf.Exp(-weaponTurnResponse * dt));
            }
            else if (horizontalVelocity.sqrMagnitude > 0.0025f)
                transform.rotation = Quaternion.Slerp(transform.rotation,
                    Quaternion.LookRotation(horizontalVelocity),
                    1f - Mathf.Exp(-14f * dt));

            // Releasing Jump early shortens the arc; holding it gives the
            // designed full height without any second gravity controller.
            bool jumpingHeld = jumpButtonHeld || Input.GetKey(KeyCode.Space);
            // Guarantee the take-off impulse survives at least one real
            // input sampling window. An extremely quick tap otherwise loses
            // most of its height before the first Android physics frame,
            // especially at 10-20 FPS or when jumping from a stair tread.
            bool minimumAscentElapsed = now - lastJumpStarted >= minimumJumpAscentSeconds;
            float gravityMultiplier = verticalVelocity > 0f &&
                !jumpingHeld && minimumAscentElapsed
                ? earlyJumpReleaseGravity : 1f;
            float previousVerticalVelocity = verticalVelocity;
            verticalVelocity = Mathf.Max(verticalVelocity - gravity * gravityMultiplier * dt, -40f);

            // A short support sweep reduces floating when downhill terrain
            // drops away between frames. Skip while ascending from a jump.
            if (!downhillProbeChecked && !wasGrounded && !launchJump &&
                verticalVelocity <= 0f && now - lastJumpStarted > 0.16f &&
                downhillProbeDistance > 0f)
            {
                float radius = Mathf.Max(0.07f, controller.radius - controller.skinWidth);
                Vector3 feet = transform.TransformPoint(
                    controller.center + Vector3.down * (controller.height * 0.5f - radius));
                Vector3 start = feet + Vector3.up * 0.04f;
                if (Physics.SphereCast(start, radius, Vector3.down,
                    out RaycastHit support, downhillProbeDistance + 0.04f,
                    WorldMask, QueryTriggerInteraction.Ignore) &&
                    support.normal.y >= Mathf.Cos(controller.slopeLimit * Mathf.Deg2Rad))
                {
                    verticalVelocity = Mathf.Min(verticalVelocity, -groundedDownVelocity);
                }
            }

            // Trapezoidal displacement integration yields the same jump
            // apex at 10, 30 and 120 FPS under constant acceleration,
            // unlike end-velocity Euler which lost ~1/3 jump height at 10 FPS.
            float verticalStepSpeed = (previousVerticalVelocity + verticalVelocity) * 0.5f;
            CollisionFlags flags = controller.Move(
                (horizontalVelocity + Vector3.up * verticalStepSpeed) * dt);
            if ((flags & CollisionFlags.Above) != 0 && verticalVelocity > 0f)
                verticalVelocity = 0f;
            if ((flags & CollisionFlags.Below) != 0)
            {
                if (!wasGrounded && !launchJump) lastLanding = now;
                lastGrounded = now;
                if (verticalVelocity < 0f) verticalVelocity = -groundedDownVelocity;
                if (!Mathf.Approximately(controller.stepOffset, groundedStep))
                    controller.stepOffset = groundedStep;
            }
            Vector3 velocity = controller.velocity;
            travelSpeed = new Vector2(velocity.x, velocity.z).magnitude;
            AnimateWalk(dt);

            float targetFov = weaponAimFov > 0f ? weaponAimFov :
                normalCameraFov + (sprinting && travelSpeed > 4f ? 6f : 0f);
            if (followCamera != null)
            {
                float currentFov = followCamera.fieldOfView;
                float fovError = targetFov - currentFov;
                // Avoid a native camera property write forever after the
                // sprint / ADS blend has visually converged. Snap its last
                // tiny fraction so subsequent steady frames do no work.
                if (Mathf.Abs(fovError) > 0.001f)
                    followCamera.fieldOfView = Mathf.Abs(fovError) <= 0.1f
                        ? targetFov
                        : Mathf.Lerp(currentFov, targetFov, 1f - Mathf.Exp(-5f * dt));
            }
        }

        private void AnimateWalk(float dt)
        {
            // A fully stationary fallback rig needs ONE final rest-pose write,
            // not four Transform writes every frame for minutes at 60 FPS.
            // Keep walking, airborne limbs and the full punch envelope live.
            float secondsSincePunch = battle != null
                ? Time.time - battle.LastPunchTime : float.PositiveInfinity;
            bool punchAnimating = secondsSincePunch >= 0f && secondsSincePunch <= 0.36f;
            bool settled = travelSpeed < 0.01f && controller.isGrounded && !punchAnimating;
            if (settled && legacyIdlePoseApplied) return;
            legacyIdlePoseApplied = settled;
            gait += travelSpeed * dt * 2.8f;
            float amplitude = Mathf.Clamp01(travelSpeed / runSpeed) * 36f;
            if (!controller.isGrounded) amplitude *= 0.2f;
            float angle = Mathf.Sin(gait) * amplitude;
            if (leftArm != null) leftArm.localRotation = leftArmRest * Quaternion.Euler(-angle, 0f, 0f);
            if (rightArm != null)
            {
                float punch = 0f;
                if (battle != null)
                {
                    float t = (Time.time - battle.LastPunchTime) / 0.36f;
                    if (t >= 0f && t <= 1f) punch = Mathf.Sin(t * Mathf.PI) * 78f;
                }
                rightArm.localRotation = rightArmRest * Quaternion.Euler(angle - punch, 0f, 0f);
            }
            if (leftLeg != null) leftLeg.localRotation = leftLegRest * Quaternion.Euler(angle, 0f, 0f);
            if (rightLeg != null) rightLeg.localRotation = rightLegRest * Quaternion.Euler(-angle, 0f, 0f);
        }
        private bool HasWalkableGroundWithinStep()
        {
            if (downhillProbeDistance <= 0f || controller == null) return false;
            float radius = Mathf.Max(.07f, controller.radius - controller.skinWidth);
            Vector3 footSphere = transform.TransformPoint(
                controller.center + Vector3.down * (controller.height * .5f - radius));
            Vector3 start = footSphere + Vector3.up * .04f;
            if (!Physics.SphereCast(start, radius, Vector3.down, out RaycastHit hit,
                downhillProbeDistance + .04f, WorldMask, QueryTriggerInteraction.Ignore))
                return false;
            return hit.normal.y >= Mathf.Cos(controller.slopeLimit * Mathf.Deg2Rad) &&
                hit.distance <= downhillProbeDistance + .04f;
        }

        // Slide only along already requested movement, never drift sideways
        // without input. Prone clearance and terrain are rechecked for each
        // alternative; solid doors/walls and shorelines remain impassable.
        private Vector3 ProneWallSlide(Vector3 velocity, float dt)
        {
            Vector3 side = transform.right * Vector3.Dot(velocity, transform.right);
            Vector3 ahead = transform.forward * Vector3.Dot(velocity, transform.forward);
            Vector3 first = side.sqrMagnitude >= ahead.sqrMagnitude ? side : ahead;
            Vector3 second = side.sqrMagnitude >= ahead.sqrMagnitude ? ahead : side;
            for (int i = 0; i < 2; i++)
            {
                Vector3 test = (i == 0 ? first : second) * .8f;
                if (test.sqrMagnitude < .0001f) continue;
                Vector3 next = transform.position + test * dt;
                if (Mathf.Abs(next.x) <= 975f && Mathf.Abs(next.z) <= 975f &&
                    SafeFooting(next) && battle.CanAdvanceProne(next, test))
                    return test;
            }
            return Vector3.zero;
        }

        private float GroundHeight(Vector3 position)
        {
            float ground = seaLevel - 10f;
            foreach (Terrain t in terrains)
            {
                if (t == null || t.terrainData == null) continue;
                Vector3 origin = t.transform.position, size = t.terrainData.size;
                if (position.x >= origin.x && position.x <= origin.x + size.x && position.z >= origin.z && position.z <= origin.z + size.z)
                    // Terrain.SampleHeight already returns a world-space elevation.
                    // On offset native tiles, double-adding origin.y misclassifies
                    // safe land as sea/water and can interrupt on-foot movement.
                    ground = Mathf.Max(ground, t.SampleHeight(position));
            }
            return ground;
        }
        private bool SafeFooting(Vector3 position)
        {
            if (GroundHeight(position) >= seaLevel + 0.35f) return true;
            // Terrain may be underwater beneath a solid quay, ramp or bridge.
            // Query only near the feet so a roof overhead cannot masquerade as support.
            // The stepOffset is temporarily zero during jumps. Keep the
            // original ground-probe range when moving over bridge steps.
            float rise = savedGroundStepOffset + 0.1f;
            if (!Physics.Raycast(position + Vector3.up * rise, Vector3.down, out RaycastHit support,
                rise + jumpHeight + 0.5f, WorldMask, QueryTriggerInteraction.Ignore)) return false;
            return support.point.y >= seaLevel + 0.35f
                && support.normal.y >= Mathf.Cos(controller.slopeLimit * Mathf.Deg2Rad);
        }
        private Vector3 ObstructionSafe(Vector3 pivot, Vector3 target)
        {
            Vector3 delta = target - pivot;
            float length = delta.magnitude;
            if (length < 0.01f) return target;
            // Closest-hit query avoids a full, unordered hit buffer in dense forest.
            float allowed = Physics.SphereCast(pivot, 0.18f, delta / length, out RaycastHit hit,
                length, WorldMask, QueryTriggerInteraction.Ignore)
                ? Mathf.Min(length, Mathf.Max(0.12f, hit.distance - 0.08f)) : length;
            return pivot + delta / length * allowed;
        }
        private void PlaceCamera(bool instant)
        {
            if (followCamera == null) return;
            float targetEyeHeight = battle != null ? battle.CameraEyeHeight : pivotHeight;
            if (instant)
            {
                smoothedEyeHeight = targetEyeHeight;
                eyeHeightVelocity = 0f;
            }
            else
            {
                smoothedEyeHeight = Mathf.SmoothDamp(smoothedEyeHeight,
                    targetEyeHeight, ref eyeHeightVelocity, stanceCameraBlendSeconds,
                    Mathf.Infinity, Mathf.Min(Time.deltaTime, 0.05f));
            }
            Vector3 pivot = transform.position + Vector3.up * smoothedEyeHeight;
            Quaternion orbit = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 wanted = pivot - orbit * Vector3.forward * followDistance + orbit * Vector3.right * 0.30f;
            wanted = ObstructionSafe(pivot, wanted);
            Vector3 next = instant ? wanted : Vector3.SmoothDamp(followCamera.transform.position, wanted, ref cameraVelocity, 0.07f);
            // The target has already passed the first collision sweep. On a
            // settled camera (or instant reset), casting the same segment again
            // is redundant. Keep the second sweep during camera interpolation
            // so a smoothing frame never clips through a nearby obstruction.
            if ((next - wanted).sqrMagnitude > 0.00000001f)
                next = ObstructionSafe(pivot, next);
            followCamera.transform.SetPositionAndRotation(next, Quaternion.LookRotation(pivot + orbit * Vector3.forward * 0.5f - next));
        }
        private void LateUpdate() { PlaceCamera(false); }

        /// <summary>
        /// Restore sole on-foot movement authority after a real aerial touchdown.
        /// Refresh cached yaw/gravity/camera AND reset the HOME point to the landing spot.
        /// This is deliberately not an extra motor and never teleports the player.
        /// </summary>
        /// <summary>
        /// Preserve flight camera direction across motor ownership changes.
        /// Keeping the original no-argument touchdown API also lets older
        /// scene/controller callers continue using their default look.
        /// </summary>
        public void PrepareAerialCameraHandoff(float lookYaw, float lookPitch)
        {
            pendingAerialYaw = lookYaw;
            pendingAerialPitch = lookPitch;
            hasPendingAerialLook = true;
        }

        public void ResumeFromAerialDrop()
        {
            if (controller == null) controller = GetComponent<CharacterController>();
            if (controller == null) return;
            yaw = hasPendingAerialLook ? pendingAerialYaw : transform.eulerAngles.y;
            pitch = hasPendingAerialLook
                ? Mathf.Clamp(pendingAerialPitch, minLookPitch, maxLookPitch) : 12f;
            hasPendingAerialLook = false;
            spawn = transform.position;
            spawnRotation = transform.rotation;
            horizontalVelocity = Vector3.zero;
            verticalVelocity = -groundedDownVelocity;
            lastGrounded = Time.unscaledTime;
            lastLanding = Time.unscaledTime;
            lastJumpStarted = jumpUntil = -10f;
            travelSpeed = gait = 0f;
            legacyIdlePoseApplied = false;
            running = jumpButtonHeld = false;
            moveFinger = lookFinger = -1;
            moveInput = Vector2.zero;
            controller.stepOffset = battle != null ? battle.GroundStepOffset : savedGroundStepOffset;
            cameraVelocity = Vector3.zero;
            if (followCamera != null) followCamera.fieldOfView = normalCameraFov;
            weaponAimFov = -1f;
            lastMouse = Input.mousePosition;
            PlaceCamera(true);
        }

        /// <summary>Release the vehicle chase camera back to the walking motor.</summary>
        public void ResetCameraAfterVehicle()
        {
            // Clear any stale left/right finger IDs captured before entering
            // the car; a held arrow must not turn into an on-foot joystick.
            ClearInput();
            // The disabled foot motor retains its pre-drive vertical speed.
            // Restore a grounded baseline only AFTER the vehicle controller
            // has verified a real floor and enabled the CharacterController.
            horizontalVelocity = Vector3.zero;
            verticalVelocity = -groundedDownVelocity;
            lastGrounded = Time.unscaledTime;
            lastLanding = Time.unscaledTime;
            lastJumpStarted = jumpUntil = -10f;
            travelSpeed = gait = 0f;
            legacyIdlePoseApplied = false;
            yaw = transform.eulerAngles.y;
            controller.stepOffset = battle != null
                ? battle.GroundStepOffset : savedGroundStepOffset;
            cameraVelocity = Vector3.zero;
            lastMouse = Input.mousePosition;
            PlaceCamera(true);
        }
        private void ResetPlayer()
        {
            controller.enabled = false;
            transform.SetPositionAndRotation(spawn, spawnRotation);
            if (nearbyColliders != null) nearbyColliders.RefreshNow();
            controller.enabled = true;
            verticalVelocity = -2f; yaw = spawnRotation.eulerAngles.y; pitch = 12f;
            horizontalVelocity = Vector3.zero;
            running = false;
            if (battle != null) { battle.SetFireHeld(false); battle.TryStand(); }
            controller.stepOffset = battle != null
                ? battle.GroundStepOffset : savedGroundStepOffset;
            lastGrounded = -10f; gait = travelSpeed = 0f;
            legacyIdlePoseApplied = false;
            lastJumpStarted = lastLanding = -10f;
            if (followCamera != null) followCamera.fieldOfView = normalCameraFov;
            weaponAimFov = -1f;
            cameraVelocity = Vector3.zero;
            ClearInput(); PlaceCamera(true);
        }

        private void OnGUI()
        {
            bool fallback = !AarisBattleHUD.IsVisible;
            if (Event.current.type != EventType.Repaint || (!fallback && !Debug.isDebugBuild)) return;
            if (buttonStyle == null)
            {
                buttonStyle = new GUIStyle(GUI.skin.box) { fontSize = 17, alignment = TextAnchor.MiddleCenter };
                labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
                disc = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                Color[] pixels = new Color[64 * 64];
                for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
                    pixels[y * 64 + x] = (new Vector2(x - 31.5f, y - 31.5f)).sqrMagnitude < 31f * 31f ? Color.white : Color.clear;
                disc.SetPixels(pixels); disc.Apply(false, true);
            }
            Matrix4x4 matrix = GUI.matrix; Color oldColor = GUI.color;
            GUI.matrix = Matrix4x4.Scale(new Vector3(uiScale, uiScale, 1f));
            if (fallback)
            {
                GUI.color = new Color(0.13f, 0.13f, 0.13f, 0.48f);
                GUI.DrawTexture(new Rect(stickCenter.x - 76f, stickCenter.y - 76f, 152f, 152f), disc);
                GUI.color = new Color(0.94f, 0.94f, 0.94f, 0.88f);
                Vector2 knob = stickCenter + new Vector2(moveInput.x, -moveInput.y) * 44f;
                GUI.DrawTexture(new Rect(knob.x - 28f, knob.y - 28f, 56f, 56f), disc);
                GUI.color = Color.white;
                GUI.Box(jumpButton, "JUMP", buttonStyle);
                GUI.Box(runButton, running ? "RUN: ON" : "WALK", buttonStyle);
            }
            if (Debug.isDebugBuild)
            {
                GUI.Box(homeButton, "HOME", buttonStyle);
                GUI.Box(reportButton, Time.unscaledTime < reportCopiedUntil ? "COPIED" : "REPORT", buttonStyle);
                GUI.Box(fpsButton, Application.targetFrameRate == 60 ? "60 FPS" : "30 FPS", buttonStyle);
                GUI.Label(new Rect(homeButton.xMax + 12f, homeButton.y, 420f, 44f),
                    "Move: left stick  |  Look: drag right  |  Human height: 1.78 m", labelStyle);
            }
            GUI.matrix = matrix; GUI.color = oldColor;
        }
    }
}
