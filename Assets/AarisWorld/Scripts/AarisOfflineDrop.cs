using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Aaris.MapReview
{
    /// <summary>
    /// Offline BR entry: one owner of CharacterController and camera at a time.
    /// This component temporarily suspends the existing on-foot motor and restores it
    /// after actual CollisionFlags.Below / terrain or roof contact. No network code.
    /// </summary>
    [RequireComponent(typeof(AarisThirdPersonPlayer), typeof(CharacterController))]
    // Run after AarisCharacterVisual so the drop snapshots the final rig, including hidden placeholders.
    [DisallowMultipleComponent, DefaultExecutionOrder(225)]
    public sealed class AarisOfflineDrop : MonoBehaviour
    {
        public enum DropPhase { InPlane, Freefall, Opening, Gliding, Landing, Landed }

        [Header("Match flight")]
        [SerializeField, Range(200f, 650f)] private float altitudeAboveTerrain = 420f;
        [SerializeField, Range(25f, 120f)] private float planeSpeed = 76f;
        [SerializeField, Range(10f, 100f)] private float autoDeployHeight = 85f;
        [Header("Aerial movement")]
        [SerializeField, Range(10f, 60f)] private float glideForwardSpeed = 17f;
        [SerializeField, Range(3f, 18f)] private float glideSinkSpeed = 9.5f;
        [SerializeField, Range(10f, 70f)] private float freefallForwardSpeed = 34f;
        [SerializeField, Range(15f, 70f)] private float freefallSlowSink = 25f;
        [SerializeField, Range(20f, 90f)] private float freefallFastSink = 54f;

        private AarisThirdPersonPlayer footMotor;
        private AarisCharacterVisual characterVisual;
        private CharacterController capsule;
        private AarisBattleActions combat;
        private Camera view;
        private AarisOfflineDropHud hud;
        private AarisVehicleSession vehicle;
        private Canvas battleCanvas, weaponCanvas;
        private GraphicRaycaster battleRaycaster, weaponRaycaster;
        private bool battleCanvasWasEnabled, weaponCanvasWasEnabled, combatWasEnabled;
        private bool battleRaycasterWasEnabled, weaponRaycasterWasEnabled, controlsSuspended;
        private bool canopyRequested;
        private float nextHeightSample, nextContactSample;
        private float noGroundContactSince = -1f;
        private GameObject plane, canopy;
        private Transform[] propellers;
        private Renderer[] initialPlayerRenderers;
        private bool[] previousRendererStates;
        private Vector3 pathStart, pathEnd, horizontalVelocity, preFlightGroundPosition;
        private Quaternion cachedPlayerRotation;
        private float flightProgress, pathDuration, downwardSpeed, yaw, pitch, openingProgress, airborneSince;
        private float propellerAngle;
        private int lookFinger = -1;
        private Vector3 previousMouse;
        private DropPhase phase = DropPhase.InPlane;
        private readonly RaycastHit[] groundHits = new RaycastHit[72];
        private readonly RaycastHit[] cameraHits = new RaycastHit[64];
        private Terrain[] flightTerrains;
        private const float InitialCanopyScale = 0.30f;
        private const float SeaSurfaceHeight = -38.07552f;

        public DropPhase Phase => phase;
        public bool HasLanded => phase == DropPhase.Landed;
        public bool IsFreefall => phase == DropPhase.Freefall;
        public bool IsUnderCanopy => phase == DropPhase.Opening || phase == DropPhase.Gliding;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            EnsureForPlayer(FindFirstObjectByType<AarisThirdPersonPlayer>());
        }

        internal static void EnsureForPlayer(AarisThirdPersonPlayer player)
        {
            if (player != null && player.GetComponent<AarisOfflineDrop>() == null)
                player.gameObject.AddComponent<AarisOfflineDrop>();
        }

        private void Awake()
        {
            footMotor = GetComponent<AarisThirdPersonPlayer>();
            characterVisual = GetComponent<AarisCharacterVisual>();
            capsule = GetComponent<CharacterController>();
            combat = GetComponent<AarisBattleActions>();
            vehicle = GetComponent<AarisVehicleSession>();
            view = footMotor != null ? footMotor.AimCamera : Camera.main;
        }

        private void Start()
        {
            if (footMotor == null || capsule == null || view == null)
            {
                Debug.LogError("Aaris offline drop: existing motor, capsule or game camera missing.");
                enabled = false;
                return;
            }
            // AfterSceneLoad bootstrap methods can run in any order. Other
            // systems may have attached the battle, visual and vehicle
            // components AFTER this component's Awake. Refresh once here
            // before suspending their input/camera authority for flight.
            if (combat == null) combat = GetComponent<AarisBattleActions>();
            if (vehicle == null) vehicle = GetComponent<AarisVehicleSession>();
            if (characterVisual == null) characterVisual = GetComponent<AarisCharacterVisual>();
            // Never start aerial transition while driving.
            if (vehicle != null && vehicle.IsDriving) { enabled = false; return; }
            cachedPlayerRotation = transform.rotation;
            preFlightGroundPosition = transform.position;
            initialPlayerRenderers = GetComponentsInChildren<Renderer>(true);
            previousRendererStates = new bool[initialPlayerRenderers.Length];
            for (int i = 0; i < initialPlayerRenderers.Length; i++)
                previousRendererStates[i] = initialPlayerRenderers[i].enabled;

            controlsSuspended = true;
            combatWasEnabled = combat != null && combat.enabled;
            if (combat != null) { combat.SetFireHeld(false); combat.enabled = false; }
            footMotor.enabled = false;
            // Original combat HUD is retained, only hidden during the drop.
            var battleHud = FindFirstObjectByType<AarisBattleHUD>();
            if (battleHud != null) battleHud.CancelVehicleInputs();
            battleCanvas = battleHud != null ? battleHud.GetComponentInChildren<Canvas>(true) : null;
            var weaponHud = FindFirstObjectByType<AarisWeaponHUD>();
            weaponCanvas = weaponHud != null ? weaponHud.GetComponentInChildren<Canvas>(true) : null;
            battleRaycaster = battleCanvas != null ? battleCanvas.GetComponent<GraphicRaycaster>() : null;
            weaponRaycaster = weaponCanvas != null ? weaponCanvas.GetComponent<GraphicRaycaster>() : null;
            battleCanvasWasEnabled = battleCanvas != null && battleCanvas.enabled;
            weaponCanvasWasEnabled = weaponCanvas != null && weaponCanvas.enabled;
            battleRaycasterWasEnabled = battleRaycaster != null && battleRaycaster.enabled;
            weaponRaycasterWasEnabled = weaponRaycaster != null && weaponRaycaster.enabled;
            if (battleRaycaster != null) battleRaycaster.enabled = false;
            if (weaponRaycaster != null) weaponRaycaster.enabled = false;
            if (battleCanvas != null) battleCanvas.enabled = false;
            if (weaponCanvas != null) weaponCanvas.enabled = false;

            BuildFlightPath();
            plane = AarisOfflineDropVisuals.CreatePlane(pathStart,
                Quaternion.LookRotation(pathEnd - pathStart), out propellers);
            capsule.enabled = false;
            HidePlayer();
            transform.position = pathStart + Vector3.down * 6f;
            hud = AarisOfflineDropHud.Create(this);
            hud.SetAction("EJECT", true);
            phase = DropPhase.InPlane;
            // Before the first flight frame, the saved walking camera is
            // still positioned at the ground spawn. Snap it to the real
            // aircraft once instead of lerping through terrain for seconds.
            // Every subsequent swipe continues through the normal orbit.
            if (view != null && plane != null)
            {
                Vector3 orbit = Quaternion.Euler(-pitch, yaw, 0f) * Vector3.back;
                Vector3 initialPosition = plane.transform.position + orbit * 28f +
                    Vector3.up * 9f;
                Vector3 focus = plane.transform.position + plane.transform.forward * 4f;
                view.transform.SetPositionAndRotation(initialPosition,
                    Quaternion.LookRotation(focus - initialPosition, Vector3.up));
            }
            previousMouse = Input.mousePosition;
        }

        private void BuildFlightPath()
        {
            flightTerrains = Terrain.activeTerrains;
            var terrains = flightTerrains;
            float minX = 1000f, maxX = -1000f, minZ = 1000f, maxZ = -1000f;
            float tallest = -50f;
            if (terrains != null && terrains.Length > 0)
            {
                foreach (var terrain in terrains)
                {
                    if (terrain == null || terrain.terrainData == null) continue;
                    Vector3 p = terrain.transform.position, s = terrain.terrainData.size;
                    minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x + s.x);
                    minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z + s.z);
                    for (int a = 0; a <= 4; a++)
                        for (int b = 0; b <= 4; b++)
                        {
                            float x = p.x + s.x * (a / 4f), z = p.z + s.z * (b / 4f);
                            // Unity Terrain.SampleHeight returns the sampled world-space Y.
                            // Adding the Terrain tile's Y again corrupts flight clearance.
                            tallest = Mathf.Max(tallest, terrain.SampleHeight(new Vector3(x, 0f, z)));
                        }
                }
            }
            if (maxX <= minX || maxZ <= minZ)
            {
                minX = minZ = -1000f; maxX = maxZ = 1000f;
                tallest = 100f;
            }
            Vector3 center = new Vector3((minX + maxX) * 0.5f,
                tallest + altitudeAboveTerrain, (minZ + maxZ) * 0.5f);
            float heading = Random.Range(0f, Mathf.PI * 2f);
            Vector3 direction = new Vector3(Mathf.Cos(heading), 0f, Mathf.Sin(heading));
            float halfDistance = Mathf.Min(maxX - minX, maxZ - minZ) * 0.46f;
            pathStart = center - direction * halfDistance;
            pathEnd = center + direction * halfDistance;
            pathDuration = Vector3.Distance(pathStart, pathEnd) / Mathf.Max(1f, planeSpeed);
            yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            pitch = 13f;
        }

        private void Update()
        {
            if (phase == DropPhase.Landed || view == null) return;
            // Keep up to 200 ms of elapsed flight time on slower mobile frames.
            // MoveAirborne below uses bounded capsule sweeps rather than one long step.
            float dt = Mathf.Clamp(Time.deltaTime, 0f, 0.20f);
            if (dt <= 0f) return;
            ReadLookInput();
            AnimatePlaneRotors();
            if (phase == DropPhase.InPlane)
            {
                flightProgress = Mathf.Clamp01(flightProgress + dt / pathDuration);
                if (plane != null)
                {
                    plane.transform.position = Vector3.Lerp(pathStart, pathEnd, flightProgress);
                    transform.position = plane.transform.position + Vector3.down * 6f;
                }
                if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.F)) OnAction();
                if (flightProgress >= 0.95f) Eject();
            }
            else
            {
                AdvanceDepartingAircraft(dt);
                // At 54 m/s a tenth of a second is 5.4 m. Deployment has a
                // much larger margin, and touchdown always checks the capsule.
                bool sampleHeight = phase == DropPhase.Freefall
                    ? Time.time >= nextHeightSample : hud != null && hud.TelemetryDue;
                float height = 0f;
                bool reliableHeight = !sampleHeight || TryGetDistanceToSupport(out height, 1600f);
                if (sampleHeight) nextHeightSample = Time.time + 0.10f;
                if (Input.GetKeyDown(KeyCode.Space) && phase == DropPhase.Freefall) canopyRequested = true;
                if (phase == DropPhase.Freefall)
                {
                    if (sampleHeight && (!reliableHeight || height <= autoDeployHeight))
                        canopyRequested = true;
                    if (canopyRequested) OpenCanopy();
                }
                if (phase == DropPhase.Freefall || phase == DropPhase.Opening ||
                    phase == DropPhase.Gliding)
                    SimulateAirborne(dt);
                if (sampleHeight && reliableHeight && hud != null)
                    hud.SetTelemetry(height, Mathf.Abs(downwardSpeed) * 3.6f, phase);
            }
        }

        // Screen-space swipe up must lift the view; the orbit uses -pitch,
        // so increasing pitch positions the camera below its focal point.
        // Normalize by screen height for consistent touch response on Android.
        private void AnimatePlaneRotors()
        {
            if (plane == null || propellers == null) return;
            // All four rotors share one mesh and one angle accumulator.
            propellerAngle = Mathf.Repeat(propellerAngle +
                1150f * Mathf.Min(Time.unscaledDeltaTime, .05f), 360f);
            for (int i = 0; i < propellers.Length; i++)
                if (propellers[i] != null)
                    propellers[i].localRotation = Quaternion.Euler(0f, 0f,
                        (i & 1) == 0 ? propellerAngle : -propellerAngle);
        }

        private void AdvanceDepartingAircraft(float dt)
        {
            if (plane == null) return;
            if (Time.time - airborneSince >= 4.5f)
            {
                Destroy(plane);
                plane = null;
                propellers = null;
                return;
            }
            // Ejection retains a visible aircraft that continues to fly away,
            // without adding Rigidbody physics or per-frame model allocations.
            plane.transform.position += plane.transform.forward * planeSpeed * dt;
        }

        private void SimulateAirborne(float elapsed)
        {
            // At >=20 FPS this is one movement step. During a hitch,
            // split capsule motion into safe <=50 ms sweeps, with the full
            // elapsed interval preserved and at most four steps per frame.
            const float MaxSweepSeconds = 0.05f;
            int steps = Mathf.Clamp(Mathf.CeilToInt(elapsed / MaxSweepSeconds), 1, 4);
            float stepDt = elapsed / steps;
            for (int i = 0; i < steps; i++)
            {
                if (phase == DropPhase.Freefall) UpdateFreefall(stepDt);
                else if (phase == DropPhase.Opening || phase == DropPhase.Gliding)
                    UpdateGlide(stepDt);
                else break; // Once the player lands, stop airborne movement.
            }
        }

        private void ApplyLookDrag(Vector2 pixels)
        {
            float scale = Mathf.Clamp(720f / Mathf.Max(360f, Screen.height), 0.75f, 1.5f);
            yaw += pixels.x * 0.18f * scale;
            pitch = Mathf.Clamp(pitch + pixels.y * 0.15f * scale, -55f, 70f);
        }

        // Android may remove a canceled camera touch without a final Ended
        // sample while the movement thumb remains down. Clear stale ownership
        // so the next valid right-side swipe can acquire the camera.
        private void OnApplicationPause(bool paused)
        {
            if (paused) lookFinger = -1;
            previousMouse = Input.mousePosition;
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused) lookFinger = -1;
            previousMouse = Input.mousePosition;
        }

        private void ReadLookInput()
        {
            if (Input.touchCount > 0)
            {
                if (lookFinger >= 0)
                {
                    bool stillTouching = false;
                    for (int i = 0; i < Input.touchCount; i++)
                    {
                        Touch touch = Input.GetTouch(i);
                        if (touch.fingerId == lookFinger &&
                            touch.phase != TouchPhase.Ended &&
                            touch.phase != TouchPhase.Canceled)
                        {
                            stillTouching = true;
                            break;
                        }
                    }
                    if (!stillTouching) lookFinger = -1;
                }
                for (int i = 0; i < Input.touchCount; i++)
                {
                    Touch t = Input.GetTouch(i);
                    if (t.fingerId == lookFinger)
                    {
                        if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                            lookFinger = -1;
                        else if (t.phase == TouchPhase.Moved)
                        {
                            ApplyLookDrag(t.deltaPosition);
                        }
                    }
                    else if (lookFinger < 0 && t.phase == TouchPhase.Began &&
                        t.position.x > Screen.width * 0.5f &&
                        // EventSystem may not have processed the new touch yet
                        // this frame; reserve the actual EJECT / OPEN rectangle
                        // before ownership can be handed to camera look.
                        (hud == null || !hud.CapturesScreenPosition(t.position)) &&
                        (EventSystem.current == null ||
                         !EventSystem.current.IsPointerOverGameObject(t.fingerId)))
                        lookFinger = t.fingerId;
                }
            }
            else
            {
                lookFinger = -1;
                if (Input.GetMouseButton(1))
                {
                    Vector3 delta = Input.mousePosition - previousMouse;
                    ApplyLookDrag(new Vector2(delta.x, delta.y));
                }
                previousMouse = Input.mousePosition;
            }
        }

        private Vector2 MovementIntent()
        {
            Vector2 keyboard = new Vector2(
                (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1f : 0f) -
                (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1f : 0f),
                (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1f : 0f) -
                (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1f : 0f));
            if (keyboard.sqrMagnitude > 0.01f) return Vector2.ClampMagnitude(keyboard, 1f);
            return hud != null ? hud.MoveInput : Vector2.zero;
        }

        private void UpdateFreefall(float dt)
        {
            Vector2 input = MovementIntent();
            // Camera pitch is a VIEW preference, not a dive command.
            // Previously looking up/down during freefall changed the
            // player's fall rate and forward motion, making touch look feel
            // inverted and unpredictable. Only deliberate left-stick forward
            // input accelerates the dive; free-look remains independent.
            bool fastDive = input.y > 0.75f;
            float targetSink = fastDive ? -freefallFastSink : -freefallSlowSink;
            downwardSpeed = Mathf.MoveTowards(downwardSpeed, targetSink, 35f * dt);
            Vector3 forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            Vector3 right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
            Vector3 desired = (forward * Mathf.Max(0.28f, input.y) * freefallForwardSpeed
                + right * input.x * 15f) * (fastDive ? 0.62f : 1f);
            horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, desired, 40f * dt);
            transform.rotation = Quaternion.Slerp(transform.rotation,
                Quaternion.Euler(0f, yaw, 0f), 1f - Mathf.Exp(-4f * dt));
            MoveAirborne(dt);
        }

        private void UpdateGlide(float dt)
        {
            Vector2 input = MovementIntent();
            if (phase == DropPhase.Opening)
            {
                openingProgress = Mathf.Min(1f, openingProgress + dt / 0.52f);
                if (canopy != null)
                {
                    float s = Mathf.SmoothStep(InitialCanopyScale, 1f, openingProgress);
                    SetCanopyOpeningScale(s);
                }
                if (openingProgress >= 1f) phase = DropPhase.Gliding;
            }
            float steering = input.x;
            yaw += steering * 48f * dt;
            float speed = glideForwardSpeed * Mathf.Lerp(0.68f, 1.15f, (input.y + 1f) * 0.5f);
            Vector3 forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            horizontalVelocity = Vector3.MoveTowards(horizontalVelocity,
                forward * speed, 28f * dt);
            downwardSpeed = Mathf.MoveTowards(downwardSpeed, -glideSinkSpeed, 65f * dt);
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0f, yaw, 0f),
                1f - Mathf.Exp(-8f * dt));
            if (canopy != null)
            {
                Quaternion bank = Quaternion.Euler(input.y * 5f, 0f, -steering * 17f);
                canopy.transform.localRotation = Quaternion.Slerp(
                    canopy.transform.localRotation, bank, 1f - Mathf.Exp(-5f * dt));
            }
            MoveAirborne(dt);
        }

        private void MoveAirborne(float dt)
        {
            if (capsule == null || !capsule.enabled) return;
            Vector3 beforeMove = transform.position;
            Vector3 movement = (horizontalVelocity + Vector3.up * downwardSpeed) * dt;
            Vector3 candidate = transform.position + movement;
            if (Mathf.Abs(candidate.x) > 975f && Mathf.Abs(candidate.x) > Mathf.Abs(transform.position.x))
                movement.x = 0f;
            if (Mathf.Abs(candidate.z) > 975f && Mathf.Abs(candidate.z) > Mathf.Abs(transform.position.z))
                movement.z = 0f;
            CollisionFlags flags = capsule.Move(movement);
            if ((flags & CollisionFlags.Below) != 0 && downwardSpeed < -0.1f)
            {
                Land();
                return;
            }
            if (IsUnderCanopy && downwardSpeed < -0.1f && Time.time >= nextContactSample)
            {
                nextContactSample = Time.time + 0.10f;
                if (TryGetDistanceToSupport(out float height, 1.25f) && height <= 0.48f)
                {
                    if (noGroundContactSince < 0f) noGroundContactSince = Time.time;
                    if (TryNativeTerrainTouchdown(beforeMove)) return;
                    // ALT 0 over water is not a collider contact. Do not inject
                    // another 0.5 m downward unless a real walkable floor exists.
                    Ray probe = new Ray(transform.position + Vector3.up * 0.75f, Vector3.down);
                    if (Physics.Raycast(probe, out RaycastHit floor, 1.60f,
                            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) &&
                        floor.collider != null && !floor.collider.transform.IsChildOf(transform) &&
                        floor.point.y >= SeaSurfaceHeight + 0.35f &&
                        floor.normal.y >= Mathf.Cos(capsule.slopeLimit * Mathf.Deg2Rad) &&
                        (capsule.Move(Vector3.down * 0.50f) & CollisionFlags.Below) != 0)
                    {
                        Land();
                        return;
                    }
                    // The terrain height field or sea plane can report ALT 0
                    // before the physics body has actually touched ground.
                    // A prolonged no-contact glide is a softlock; recover
                    // after normal collision and native ground probes fail.
                    if (Time.time - noGroundContactSince >= 1.25f)
                    {
                        RecoverFromOceanFall();
                        return;
                    }
                }
                else noGroundContactSince = -1f;
            }
            // This offline mode has no swimming logic. Never remain stuck
            // below the ocean with a shore-clamped aerial camera.
            if (transform.position.y < SeaSurfaceHeight - 2f)
                RecoverFromOceanFall();
        }

        private bool TryNativeTerrainTouchdown(Vector3 beforeMove)
        {
            if (flightTerrains == null) return false;
            Vector3 now = transform.position;
            for (int i = 0; i < flightTerrains.Length; i++)
            {
                Terrain tile = flightTerrains[i];
                if (tile == null || tile.terrainData == null) continue;
                Vector3 origin = tile.transform.position;
                Vector3 size = tile.terrainData.size;
                if (now.x < origin.x || now.x > origin.x + size.x ||
                    now.z < origin.z || now.z > origin.z + size.z) continue;
                float groundY = tile.SampleHeight(now);
                // Only a nearby, walkable, above-water surface qualifies.
                // This bounded correction handles a missed Terrain collider
                // contact without snapping distant airborne players or ocean.
                if (groundY < SeaSurfaceHeight + 0.35f ||
                    now.y > groundY + 0.25f ||
                    now.y < groundY - 1.0f ||
                    beforeMove.y < groundY - 1.0f) continue;
                float u = Mathf.Clamp01((now.x - origin.x) / size.x);
                float v = Mathf.Clamp01((now.z - origin.z) / size.z);
                if (tile.terrainData.GetInterpolatedNormal(u, v).y <
                    Mathf.Cos(capsule.slopeLimit * Mathf.Deg2Rad)) continue;
                capsule.enabled = false;
                transform.position = new Vector3(now.x,
                    groundY + Mathf.Max(0.05f, capsule.skinWidth), now.z);
                capsule.enabled = true;
                Land();
                return true;
            }
            return false;
        }

        private void RecoverFromOceanFall()
        {
            // Until swimming/death exists, safely return a submerged player
            // to the existing validated on-foot spawn rather than softlock.
            capsule.enabled = false;
            transform.SetPositionAndRotation(preFlightGroundPosition, cachedPlayerRotation);
            capsule.enabled = true;
            Land();
            Debug.LogWarning("AARIS_DROP_OCEAN_RECOVERY: returned to saved on-foot spawn.");
        }

        private bool TryGetDistanceToSupport(out float height, float rayLength)
        {
            Vector3 position = transform.position;
            float support = SeaSurfaceHeight;
            var terrains = flightTerrains;
            if (terrains != null)
                foreach (var t in terrains)
                {
                    if (t == null || t.terrainData == null) continue;
                    Vector3 p = t.transform.position, sz = t.terrainData.size;
                    if (position.x >= p.x && position.x <= p.x + sz.x &&
                        position.z >= p.z && position.z <= p.z + sz.z)
                        support = Mathf.Max(support, t.SampleHeight(position));
                }

            Ray groundRay = new Ray(position + Vector3.up * 0.75f, Vector3.down);
            int n = Physics.RaycastNonAlloc(groundRay,
                groundHits, rayLength, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);
            RaycastHit[] supportHits = groundHits;
            if (n >= groundHits.Length)
            {
                // Full NonAlloc buffers are unordered and incomplete. Resolve
                // all contacts only on rare saturation, rather than disabling
                // parachute landing on crowded ground or bridge geometry.
                supportHits = Physics.RaycastAll(groundRay, rayLength,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                n = supportHits.Length;
            }
            for (int i = 0; i < n; i++)
            {
                RaycastHit h = supportHits[i];
                if (h.collider == null || h.collider.transform.IsChildOf(transform) ||
                    h.normal.y < 0.5f || h.point.y > position.y + 0.05f) continue;
                support = Mathf.Max(support, h.point.y);
            }
            height = Mathf.Max(0f, position.y - support);
            return true;
        }

        public void OnAction()
        {
            if (phase == DropPhase.InPlane) Eject();
            else if (phase == DropPhase.Freefall)
            {
                canopyRequested = true;
                OpenCanopy();
            }
        }

        private void Eject()
        {
            if (phase != DropPhase.InPlane || plane == null) return;
            phase = DropPhase.Freefall;
            transform.position = plane.transform.position - Vector3.up * 7f;
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            ShowPlayer();
            ApplyLegacyFallbackPose(false);
            capsule.stepOffset = 0f;
            capsule.enabled = true;
            downwardSpeed = -3f;
            // Preserve some aircraft momentum for a smooth, controllable exit.
            horizontalVelocity = plane.transform.forward *
                Mathf.Min(freefallForwardSpeed, planeSpeed * 0.55f);
            airborneSince = Time.time;
            if (hud != null) hud.SetAction("OPEN", true);
            // AdvanceDepartingAircraft handles the short-lived flyaway.
        }

        private void OpenCanopy()
        {
            if (phase != DropPhase.Freefall || Time.time - airborneSince < 0.35f) return;
            canopyRequested = false;
            phase = DropPhase.Opening;
            openingProgress = 0f;
            canopy = AarisOfflineDropVisuals.CreateElongatedCanopy(transform);
            SetCanopyOpeningScale(InitialCanopyScale);
            ApplyLegacyFallbackPose(true);
            if (hud != null) hud.SetAction("OPEN", false);
        }

        private void SetCanopyOpeningScale(float scale)
        {
            if (canopy == null) return;
            // The suspension rig has a 4.35m canopy centre and a 1.55m
            // base offset at full size. Scale the art while maintaining the
            // wing ABOVE the head throughout its half-second deployment.
            // Otherwise a just-opened 30%-scale canopy visually intersects
            // the character, who appears to stand on the wing.
            float s = Mathf.Clamp(scale, InitialCanopyScale, 1f);
            canopy.transform.localScale = Vector3.one * s;
            canopy.transform.localPosition = Vector3.up * (4.35f - 2.8f * s);
        }

        private void Land()
        {
            if (phase == DropPhase.Landing || phase == DropPhase.Landed) return;
            phase = DropPhase.Landing;
            if (canopy != null)
            {
                canopy.transform.SetParent(null, true);
                Destroy(canopy, 1.25f);
                canopy = null;
            }
            if (plane != null) { Destroy(plane); plane = null; }
            ShowPlayer();
            if (footMotor != null) footMotor.RestoreLegacyAerialPose();
            capsule.enabled = true;
            RestoreFootControls();
            phase = DropPhase.Landed;
            Debug.Log("Aaris offline drop: parachute touchdown; on-foot controls restored.");
        }

        private void ApplyLegacyFallbackPose(bool underCanopy)
        {
            // Native Android builds may lack a valid optional imported Avatar.
            // Preserve their animated locomotion if present, and pose only the
            // saved lightweight placeholder if the real Humanoid failed to load.
            if (footMotor != null && (characterVisual == null || !characterVisual.HasWorkingHumanoid))
                footMotor.SetLegacyAerialPose(underCanopy);
        }

        private void HidePlayer()
        {
            if (initialPlayerRenderers == null) return;
            for (int i = 0; i < initialPlayerRenderers.Length; i++)
                if (initialPlayerRenderers[i] != null) initialPlayerRenderers[i].enabled = false;
        }

        private void ShowPlayer()
        {
            if (initialPlayerRenderers == null) return;
            for (int i = 0; i < initialPlayerRenderers.Length; i++)
                if (initialPlayerRenderers[i] != null) initialPlayerRenderers[i].enabled = previousRendererStates[i];
        }

        private Vector3 CameraSafePosition(Vector3 focus, Vector3 goal)
        {
            Vector3 delta = goal - focus;
            float distance = delta.magnitude;
            if (distance < 0.1f) return goal;
            Vector3 direction = delta / distance;
            int n = Physics.SphereCastNonAlloc(new Ray(focus, direction), 0.17f,
                cameraHits, distance, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);
            RaycastHit[] hits = cameraHits;
            if (n >= cameraHits.Length)
            {
                // A saturated NonAlloc buffer has no guaranteed nearest hit.
                // Never collapse the camera to 35 cm just because foliage or
                // nearby geometry fills it. Allocate only on the rare overflow
                // and still exclude the player and departing aircraft.
                hits = Physics.SphereCastAll(new Ray(focus, direction), 0.17f,
                    distance, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);
                n = hits.Length;
            }
            float allowed = distance;
            for (int i = 0; i < n; i++)
            {
                var hit = hits[i];
                if (hit.collider == null || hit.collider.transform.IsChildOf(transform) ||
                    (plane != null && hit.collider.transform.IsChildOf(plane.transform))) continue;
                allowed = Mathf.Min(allowed, Mathf.Max(0.35f, hit.distance - 0.2f));
            }
            return focus + direction * allowed;
        }

        // Terrain.SampleHeight returns world-space Y even for negative-Y
        // tiles. Never add terrain.transform.position.y a second time.
        private Vector3 ClampCameraAboveTerrain(Vector3 point)
        {
            var tiles = flightTerrains;
            if (tiles == null) return point;
            foreach (var tile in tiles)
            {
                if (tile == null || tile.terrainData == null) continue;
                Vector3 origin = tile.transform.position;
                Vector3 size = tile.terrainData.size;
                if (point.x < origin.x || point.x > origin.x + size.x ||
                    point.z < origin.z || point.z > origin.z + size.z) continue;
                // Overlapping terrain tiles can differ at their edges.
                // Clamp to the highest actual surface rather than trusting
                // whichever tile happens to be enumerated first.
                point.y = Mathf.Max(point.y, tile.SampleHeight(point) + 0.8f);
            }
            return point;
        }

        private void LateUpdate()
        {
            if (view == null || phase == DropPhase.Landed) return;
            Vector3 pos, focus;
            if (phase == DropPhase.InPlane && plane != null)
            {
                // The previous fixed chase camera ignored all plane-phase
                // swipes. Orbit the actual aircraft so players can inspect
                // above, below and either side before ejecting.
                Vector3 orbit = Quaternion.Euler(-pitch, yaw, 0f) * Vector3.back;
                pos = plane.transform.position + orbit * 28f + Vector3.up * 9f;
                focus = plane.transform.position + plane.transform.forward * 4f;
            }
            else
            {
                Vector3 behind = Quaternion.Euler(-pitch, yaw, 0f) * Vector3.back;
                pos = transform.position + behind * (phase == DropPhase.Freefall ? 9.5f : 8.5f)
                    + Vector3.up * (phase == DropPhase.Freefall ? 3.2f : 4.6f);
                focus = transform.position + Vector3.up * 2.8f;
            }
            // The aircraft flies hundreds of metres above the highest saved
            // terrain point; its own collider is excluded from this sweep.
            // Skip the two unnecessary per-frame physics casts in plane mode.
            if (phase != DropPhase.InPlane) pos = CameraSafePosition(focus, pos);
            Vector3 next = Vector3.Lerp(view.transform.position,
                pos, 1f - Mathf.Exp(-7f * Time.deltaTime));
            // The camera can cross a wall during the interpolated step even
            // when the final target is safe. Sweep that actual step too.
            if (phase != DropPhase.InPlane &&
                (next - pos).sqrMagnitude > 0.00000001f)
                next = CameraSafePosition(focus, next);
            // Near a mountain, a collision sweep alone may start below the
            // native Terrain surface. Check only the active tile on descent.
            if (phase != DropPhase.InPlane)
                next = ClampCameraAboveTerrain(next);
            view.transform.position = next;
            view.transform.rotation = Quaternion.Slerp(view.transform.rotation,
                Quaternion.LookRotation(focus - next, Vector3.up),
                1f - Mathf.Exp(-9f * Time.deltaTime));
        }

        private void RestoreFootControls()
        {
            controlsSuspended = false;
            if (hud != null)
            {
                hud.gameObject.SetActive(false);
                Destroy(hud.gameObject);
                hud = null;
            }
            if (combat != null) combat.enabled = combatWasEnabled;
            if (battleCanvas != null) battleCanvas.enabled = battleCanvasWasEnabled;
            if (weaponCanvas != null) weaponCanvas.enabled = weaponCanvasWasEnabled;
            if (battleRaycaster != null) battleRaycaster.enabled = battleRaycasterWasEnabled;
            if (weaponRaycaster != null) weaponRaycaster.enabled = weaponRaycasterWasEnabled;
            if (footMotor != null)
            {
                // Aerial orbit uses -pitch; the grounded orbit uses +pitch.
                // Convert the sign once to keep the same look direction.
                footMotor.PrepareAerialCameraHandoff(yaw, -pitch);
                footMotor.ResumeFromAerialDrop();
                footMotor.enabled = true;
            }
        }

        private void OnDisable()
        {
            if (!controlsSuspended) return;
            if (plane != null) Destroy(plane);
            if (canopy != null) Destroy(canopy);
            if (capsule != null) capsule.enabled = true;
            ShowPlayer();
            if (footMotor != null) footMotor.RestoreLegacyAerialPose();
            RestoreFootControls();
        }
    }

    /// <summary>Dedicated portrait/landscape-safe touch HUD; no 1920x1080 hard-coded overlay.</summary>
    public sealed class AarisOfflineDropHud : MonoBehaviour
    {
        private AarisOfflineDrop owner;
        private Canvas canvas;
        private RectTransform safe;
        private Button action;
        private RectTransform actionHitRect, stickHitRect;
        private Text actionLabel, altitude;
        private Rect previousSafeArea;
        private Vector2Int previousScreen;
        private float nextTelemetry;
        private AarisOfflineDropStick stick;
        public Vector2 MoveInput => stick != null ? stick.Value : Vector2.zero;
        internal bool TelemetryDue => Time.unscaledTime >= nextTelemetry;

        // Direct screen-space ownership check on TouchPhase.Began, independent
        // of EventSystem.Update order. ScreenSpaceOverlay uses a null camera.
        internal bool CapturesScreenPosition(Vector2 screenPosition) =>
            (actionHitRect != null && actionHitRect.gameObject.activeInHierarchy &&
             RectTransformUtility.RectangleContainsScreenPoint(
                 actionHitRect, screenPosition, null)) ||
            (stickHitRect != null && stickHitRect.gameObject.activeInHierarchy &&
             RectTransformUtility.RectangleContainsScreenPoint(
                 stickHitRect, screenPosition, null));

        public static AarisOfflineDropHud Create(AarisOfflineDrop owner)
        {
            var hud = new GameObject("Aaris Offline Drop HUD", typeof(AarisOfflineDropHud))
                .GetComponent<AarisOfflineDropHud>();
            hud.Initialize(owner);
            return hud;
        }

        private void Initialize(AarisOfflineDrop value)
        {
            owner = value;
            if (FindFirstObjectByType<EventSystem>() == null)
            {
                var eventRoot = new GameObject("Aaris Drop EventSystem", typeof(EventSystem),
                    typeof(StandaloneInputModule));
            }
            var root = new GameObject("Drop Canvas", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            root.transform.SetParent(transform, false);
            canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 270;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(960f, 540f);
            scaler.matchWidthOrHeight = 0.5f;
            safe = new GameObject("Safe Area", typeof(RectTransform)).GetComponent<RectTransform>();
            safe.SetParent(root.transform, false);
            safe.offsetMin = safe.offsetMax = Vector2.zero;
            ApplySafeArea();

            var buttonRoot = new GameObject("Action", typeof(RectTransform), typeof(Image),
                typeof(Button));
            buttonRoot.transform.SetParent(safe, false);
            RectTransform buttonRect = buttonRoot.GetComponent<RectTransform>();
            actionHitRect = buttonRect;
            buttonRect.anchorMin = buttonRect.anchorMax = new Vector2(0.84f, 0.55f);
            buttonRect.sizeDelta = new Vector2(110f, 110f);
            var actionImage = buttonRoot.GetComponent<Image>();
            actionImage.sprite = AarisHudGlyphs.Ring;
            actionImage.color = new Color(1f, 0.87f, 0.68f, 1f);
            action = buttonRoot.GetComponent<Button>();
            action.targetGraphic = actionImage;
            action.onClick.AddListener(() => { if (owner != null) owner.OnAction(); });
            actionLabel = Label(buttonRoot.transform, "EJECT", 23, TextAnchor.MiddleCenter);
            Stretch(actionLabel.rectTransform);

            var altitudeRoot = new GameObject("Flight Telemetry", typeof(RectTransform));
            altitudeRoot.transform.SetParent(safe, false);
            var altRect = altitudeRoot.GetComponent<RectTransform>();
            altRect.anchorMin = altRect.anchorMax = new Vector2(0.80f, 0.89f);
            altRect.sizeDelta = new Vector2(220f, 84f);
            altitude = Label(altitudeRoot.transform, "ALT  -- m\nSPD  -- km/h", 20, TextAnchor.MiddleRight);
            Stretch(altitude.rectTransform);

            var baseStick = new GameObject("Skydiving Stick", typeof(RectTransform),
                typeof(Image), typeof(AarisOfflineDropStick));
            baseStick.transform.SetParent(safe, false);
            var rect = baseStick.GetComponent<RectTransform>();
            stickHitRect = rect;
            rect.anchorMin = rect.anchorMax = new Vector2(0.12f, 0.20f);
            rect.sizeDelta = new Vector2(142f, 142f);
            baseStick.GetComponent<Image>().sprite = AarisHudGlyphs.Ring;
            stick = baseStick.GetComponent<AarisOfflineDropStick>();
            stick.Build();
            AarisHudGlyphs.ApplyCanvasShader(root.transform);
        }

        private void ApplySafeArea()
        {
            Rect area = Screen.safeArea;
            var screen = new Vector2Int(Screen.width, Screen.height);
            if (screen.x <= 0 || screen.y <= 0 ||
                (previousScreen == screen && previousSafeArea == area)) return;
            previousScreen = screen;
            previousSafeArea = area;
            safe.anchorMin = new Vector2(area.xMin / Screen.width, area.yMin / Screen.height);
            safe.anchorMax = new Vector2(area.xMax / Screen.width, area.yMax / Screen.height);
            safe.offsetMin = safe.offsetMax = Vector2.zero;
        }

        private void Update() { if (safe != null) ApplySafeArea(); }
        public void SetAction(string label, bool visible)
        {
            if (action != null) action.gameObject.SetActive(visible);
            if (actionLabel != null) actionLabel.text = label;
        }
        public void SetTelemetry(float metres, float kmph, AarisOfflineDrop.DropPhase phase)
        {
            if (altitude == null || !TelemetryDue) return;
            nextTelemetry = Time.unscaledTime + 0.15f;
            altitude.text = "ALT " + Mathf.CeilToInt(metres) + " m\nSPD " +
                Mathf.RoundToInt(kmph) + " km/h\n" + phase;
        }
        private static Text Label(Transform parent, string value, int fontSize, TextAnchor align)
        {
            var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(parent, false);
            var text = textObject.GetComponent<Text>();
            text.text = value;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = fontSize;
            text.alignment = align;
            text.color = Color.white;
            text.raycastTarget = false;
            return text;
        }
        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }
    }

    public sealed class AarisOfflineDropStick : MonoBehaviour,
        IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        private RectTransform area, knob;
        private int finger = int.MinValue;
        public Vector2 Value { get; private set; }

        public void Build()
        {
            area = GetComponent<RectTransform>();
            var child = new GameObject("Knob", typeof(RectTransform), typeof(Image));
            child.transform.SetParent(area, false);
            knob = child.GetComponent<RectTransform>();
            knob.sizeDelta = new Vector2(57f, 57f);
            var image = knob.GetComponent<Image>();
            image.sprite = AarisHudGlyphs.Knob;
            image.raycastTarget = false;
        }
        public void OnPointerDown(PointerEventData data)
        {
            if (finger != int.MinValue) return;
            finger = data.pointerId;
            OnDrag(data);
        }
        public void OnDrag(PointerEventData data)
        {
            if (finger != data.pointerId || area == null) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                area, data.position, data.pressEventCamera, out Vector2 local)) return;
            Value = Vector2.ClampMagnitude(local / 60f, 1f);
            if (knob != null) knob.anchoredPosition = Value * 51f;
        }
        public void OnPointerUp(PointerEventData data)
        {
            if (finger == data.pointerId) ResetStick();
        }
        private void ResetStick()
        {
            finger = int.MinValue;
            Value = Vector2.zero;
            if (knob != null) knob.anchoredPosition = Vector2.zero;
        }
        private void OnDisable() => ResetStick();
        private void OnApplicationPause(bool paused) { if (paused) ResetStick(); }
        private void OnApplicationFocus(bool focused) { if (!focused) ResetStick(); }
    }
}
