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
        [SerializeField] private float jumpHeight = 1.0f, gravity = 24f;
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

        private void Awake()
        {
            controller = GetComponent<CharacterController>();
            terrains = Terrain.activeTerrains;
            if (followCamera == null) followCamera = Camera.main;
            if (followCamera == null) { enabled = false; Debug.LogError("Saved player camera is missing."); return; }
            previousSimulateMouse = Input.simulateMouseWithTouches;
            Input.simulateMouseWithTouches = false;
            inputConfigured = true;
            // The mobile runtime owns the startup cap; only the player FPS button changes it.
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
        }

        private void OnDestroy()
        {
            if (inputConfigured) Input.simulateMouseWithTouches = previousSimulateMouse;
            if (disc != null) Destroy(disc);
        }
        private void OnApplicationFocus(bool focus) { ClearInput(); lastMouse = Input.mousePosition; }
        private void OnApplicationPause(bool paused) { if (paused) ClearInput(); }
        private void ClearInput() { moveFinger = lookFinger = -1; moveInput = Vector2.zero; jumpUntil = -10f; }

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
        private Vector2 ToGUI(Vector2 p) => new Vector2(p.x / uiScale, (Screen.height - p.y) / uiScale);
        private bool OverButton(Vector2 p) => jumpButton.Contains(p) || runButton.Contains(p) || homeButton.Contains(p)
            || reportButton.Contains(p) || fpsButton.Contains(p);
        private void PressButton(Vector2 p)
        {
            if (jumpButton.Contains(p)) jumpUntil = Time.unscaledTime + 0.15f;
            else if (runButton.Contains(p)) running = !running;
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
            yaw += delta.x * lookSensitivity / uiScale;
            pitch = Mathf.Clamp(pitch - delta.y * lookSensitivity / uiScale, minLookPitch, maxLookPitch);
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
                    if (OverButton(p)) { PressButton(p); continue; }
                    if (moveFinger == -1 && (p - stickCenter).sqrMagnitude < 110f * 110f) moveFinger = t.fingerId;
                    else if (lookFinger == -1 && p.x > Screen.width / uiScale * 0.4f) lookFinger = t.fingerId;
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
            if (Input.GetKeyDown(KeyCode.Space)) jumpUntil = Time.unscaledTime + 0.15f;
            if (Input.GetKeyDown(KeyCode.F)) ResetPlayer();
        }

        private void Update()
        {
            ReadInput();
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            if (dt <= 0f) return;
            float now = Time.unscaledTime;
            if (controller.isGrounded) { lastGrounded = now; if (verticalVelocity < 0f) verticalVelocity = -2f; }
            if (now <= jumpUntil && now - lastGrounded <= 0.1f)
            {
                verticalVelocity = Mathf.Sqrt(2f * gravity * jumpHeight);
                jumpUntil = lastGrounded = -10f;
            }
            Vector3 wish = Quaternion.Euler(0f, yaw, 0f) * new Vector3(moveInput.x, 0f, moveInput.y);
            wish = Vector3.ClampMagnitude(wish, 1f);
            float speed = running || Input.GetKey(KeyCode.LeftShift) ? runSpeed : walkSpeed;
            // No terrain or support raycast is needed with zero horizontal input.
            // Keep the exact same shoreline/bridge test for every non-zero move.
            // Gravity and CharacterController.Move must still run while idle.
            if (wish.sqrMagnitude > 0f)
            {
                Vector3 candidate = transform.position + wish * (speed * dt);
                // Review mode stops at the shoreline; swimming is separate gameplay.
                if (Mathf.Abs(candidate.x) > 975f || Mathf.Abs(candidate.z) > 975f || !SafeFooting(candidate))
                    wish = Vector3.zero;
            }
            if (wish.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(wish), 1f - Mathf.Exp(-14f * dt));
            verticalVelocity = Mathf.Max(verticalVelocity - gravity * dt, -40f);
            CollisionFlags flags = controller.Move((wish * speed + Vector3.up * verticalVelocity) * dt);
            if ((flags & CollisionFlags.Above) != 0 && verticalVelocity > 0f) verticalVelocity = 0f;
            Vector3 velocity = controller.velocity;
            travelSpeed = new Vector2(velocity.x, velocity.z).magnitude;
            AnimateWalk(dt);
        }

        private void AnimateWalk(float dt)
        {
            gait += travelSpeed * dt * 2.8f;
            float amplitude = Mathf.Clamp01(travelSpeed / runSpeed) * 36f;
            if (!controller.isGrounded) amplitude *= 0.2f;
            float angle = Mathf.Sin(gait) * amplitude;
            if (leftArm != null) leftArm.localRotation = leftArmRest * Quaternion.Euler(-angle, 0f, 0f);
            if (rightArm != null) rightArm.localRotation = rightArmRest * Quaternion.Euler(angle, 0f, 0f);
            if (leftLeg != null) leftLeg.localRotation = leftLegRest * Quaternion.Euler(angle, 0f, 0f);
            if (rightLeg != null) rightLeg.localRotation = rightLegRest * Quaternion.Euler(-angle, 0f, 0f);
        }
        private float GroundHeight(Vector3 position)
        {
            float ground = seaLevel - 10f;
            foreach (Terrain t in terrains)
            {
                if (t == null || t.terrainData == null) continue;
                Vector3 origin = t.transform.position, size = t.terrainData.size;
                if (position.x >= origin.x && position.x <= origin.x + size.x && position.z >= origin.z && position.z <= origin.z + size.z)
                    ground = Mathf.Max(ground, origin.y + t.SampleHeight(position));
            }
            return ground;
        }
        private bool SafeFooting(Vector3 position)
        {
            if (GroundHeight(position) >= seaLevel + 0.35f) return true;
            // Terrain may be underwater beneath a solid quay, ramp or bridge.
            // Query only near the feet so a roof overhead cannot masquerade as support.
            float rise = controller.stepOffset + 0.1f;
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
            Vector3 pivot = transform.position + Vector3.up * pivotHeight;
            Quaternion orbit = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 wanted = pivot - orbit * Vector3.forward * followDistance + orbit * Vector3.right * 0.30f;
            wanted = ObstructionSafe(pivot, wanted);
            Vector3 next = instant ? wanted : Vector3.SmoothDamp(followCamera.transform.position, wanted, ref cameraVelocity, 0.07f);
            next = ObstructionSafe(pivot, next);
            followCamera.transform.SetPositionAndRotation(next, Quaternion.LookRotation(pivot + orbit * Vector3.forward * 0.5f - next));
        }
        private void LateUpdate() { PlaceCamera(false); }
        private void ResetPlayer()
        {
            controller.enabled = false;
            transform.SetPositionAndRotation(spawn, spawnRotation);
            if (nearbyColliders != null) nearbyColliders.RefreshNow();
            controller.enabled = true;
            verticalVelocity = -2f; yaw = spawnRotation.eulerAngles.y; pitch = 12f;
            lastGrounded = -10f; gait = travelSpeed = 0f;
            cameraVelocity = Vector3.zero;
            ClearInput(); PlaceCamera(true);
        }

        private void OnGUI()
        {
            Layout();
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
            GUI.color = new Color(0.08f, 0.12f, 0.17f, 0.65f);
            GUI.DrawTexture(new Rect(stickCenter.x - 76f, stickCenter.y - 76f, 152f, 152f), disc);
            GUI.color = new Color(0.70f, 0.86f, 0.92f, 0.90f);
            Vector2 knob = stickCenter + new Vector2(moveInput.x, -moveInput.y) * 44f;
            GUI.DrawTexture(new Rect(knob.x - 28f, knob.y - 28f, 56f, 56f), disc);
            GUI.color = Color.white;
            GUI.Box(jumpButton, "JUMP", buttonStyle);
            GUI.Box(runButton, running ? "RUN: ON" : "WALK", buttonStyle);
            GUI.Box(homeButton, "HOME", buttonStyle);
            GUI.Box(reportButton, Time.unscaledTime < reportCopiedUntil ? "COPIED" : "REPORT", buttonStyle);
            GUI.Box(fpsButton, Application.targetFrameRate == 60 ? "60 FPS" : "30 FPS", buttonStyle);
            GUI.Label(new Rect(homeButton.xMax + 12f, homeButton.y, 420f, 44f),
                "Move: left stick  |  Look: drag right  |  Human height: 1.78 m", labelStyle);
            GUI.matrix = matrix; GUI.color = oldColor;
        }
    }
}
