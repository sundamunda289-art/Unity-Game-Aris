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
    [DisallowMultipleComponent, DefaultExecutionOrder(150)]
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
        private CharacterController capsule;
        private AarisBattleActions combat;
        private Camera view;
        private AarisOfflineDropHud hud;
        private AarisVehicleSession vehicle;
        private Canvas battleCanvas, weaponCanvas;
        private bool battleCanvasWasEnabled, weaponCanvasWasEnabled, combatWasEnabled;
        private GameObject plane, canopy;
        private Transform[] propellers;
        private Renderer[] initialPlayerRenderers;
        private bool[] previousRendererStates;
        private Vector3 pathStart, pathEnd, horizontalVelocity;
        private Quaternion cachedPlayerRotation;
        private float flightProgress, pathDuration, downwardSpeed, yaw, pitch, openingProgress, airborneSince;
        private int lookFinger = -1;
        private Vector3 previousMouse;
        private DropPhase phase = DropPhase.InPlane;
        private readonly RaycastHit[] groundHits = new RaycastHit[72];
        private readonly RaycastHit[] cameraHits = new RaycastHit[28];
        private readonly Terrain[] terrainBuffer = new Terrain[4];

        public DropPhase Phase => phase;
        public bool HasLanded => phase == DropPhase.Landed;
        public bool IsFreefall => phase == DropPhase.Freefall;
        public bool IsUnderCanopy => phase == DropPhase.Opening || phase == DropPhase.Gliding;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            var player = FindFirstObjectByType<AarisThirdPersonPlayer>();
            if (player != null && player.GetComponent<AarisOfflineDrop>() == null)
                player.gameObject.AddComponent<AarisOfflineDrop>();
        }

        private void Awake()
        {
            footMotor = GetComponent<AarisThirdPersonPlayer>();
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
            // Never start aerial transition while driving.
            if (vehicle != null && vehicle.IsDriving) { enabled = false; return; }
            cachedPlayerRotation = transform.rotation;
            initialPlayerRenderers = GetComponentsInChildren<Renderer>(true);
            previousRendererStates = new bool[initialPlayerRenderers.Length];
            for (int i = 0; i < initialPlayerRenderers.Length; i++)
                previousRendererStates[i] = initialPlayerRenderers[i].enabled;

            combatWasEnabled = combat != null && combat.enabled;
            if (combat != null) { combat.SetFireHeld(false); combat.enabled = false; }
            footMotor.enabled = false;
            // Original combat HUD is retained, only hidden during the drop.
            var battleHud = FindFirstObjectByType<AarisBattleHUD>();
            battleCanvas = battleHud != null ? battleHud.GetComponentInChildren<Canvas>(true) : null;
            battleCanvasWasEnabled = battleCanvas != null && battleCanvas.enabled;
            if (battleCanvas != null) battleCanvas.enabled = false;
            var weaponHud = FindFirstObjectByType<AarisWeaponHUD>();
            weaponCanvas = weaponHud != null ? weaponHud.GetComponentInChildren<Canvas>(true) : null;
            weaponCanvasWasEnabled = weaponCanvas != null && weaponCanvas.enabled;
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
            previousMouse = Input.mousePosition;
        }

        private void BuildFlightPath()
        {
            var terrains = Terrain.activeTerrains;
            float minX = 1000f, maxX = -1000f, minZ = 1000f, maxZ = -1000f;
            float tallest = float.NegativeInfinity;
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
                            // SampleHeight already returns world-space elevation, including the tile's Y offset.
                            // Adding p.y again makes the aircraft too high/low on offset Terrain tiles.
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
            float dt = Mathf.Clamp(Time.deltaTime, 0f, 0.11f);
            if (dt <= 0f) return;
            ReadLookInput();
            if (phase == DropPhase.InPlane)
            {
                flightProgress = Mathf.Clamp01(flightProgress + dt / pathDuration);
                if (plane != null)
                {
                    plane.transform.position = Vector3.Lerp(pathStart, pathEnd, flightProgress);
                    transform.position = plane.transform.position + Vector3.down * 6f;
                }
                if (propellers != null)
                    for (int i = 0; i < propellers.Length; i++)
                        if (propellers[i] != null) propellers[i].Rotate(0f, 0f, 1500f * dt, Space.Self);
                if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.F)) OnAction();
                if (flightProgress >= 0.95f) Eject();
            }
            else
            {
                float height = DistanceToSupport();
                if (Input.GetKeyDown(KeyCode.Space) && phase == DropPhase.Freefall) OpenCanopy();
                if (phase == DropPhase.Freefall)
                {
                    if (height <= autoDeployHeight) OpenCanopy();
                    if (phase == DropPhase.Freefall) UpdateFreefall(dt);
                }
                if (phase == DropPhase.Opening || phase == DropPhase.Gliding)
                    UpdateGlide(dt);
                if (hud != null)
                    hud.SetTelemetry(height, Mathf.Abs(downwardSpeed) * 3.6f, phase);
            }
        }

        private void ReadLookInput()
        {
            if (Input.touchCount > 0)
            {
                for (int i = 0; i < Input.touchCount; i++)
                {
                    Touch t = Input.GetTouch(i);
                    if (t.fingerId == lookFinger)
                    {
                        if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                            lookFinger = -1;
                        else if (t.phase == TouchPhase.Moved)
                        {
                            yaw += t.deltaPosition.x * 0.11f;
                            pitch = Mathf.Clamp(pitch - t.deltaPosition.y * 0.09f, -55f, 70f);
                        }
                    }
                    else if (lookFinger < 0 && t.phase == TouchPhase.Began &&
                        t.position.x > Screen.width * 0.5f &&
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
                    yaw += delta.x * 0.12f;
                    pitch = Mathf.Clamp(pitch - delta.y * 0.11f, -55f, 70f);
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
            float targetSink = pitch <= -30f ? -freefallFastSink : -freefallSlowSink;
            downwardSpeed = Mathf.MoveTowards(downwardSpeed, targetSink, 35f * dt);
            Vector3 forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            Vector3 right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
            Vector3 desired = (forward * Mathf.Max(0.28f, input.y) * freefallForwardSpeed
                + right * input.x * 15f) * (pitch <= -30f ? 0.62f : 1f);
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
                    float s = Mathf.SmoothStep(0.05f, 1f, openingProgress);
                    canopy.transform.localScale = new Vector3(s, s, s);
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
            Vector3 movement = (horizontalVelocity + Vector3.up * downwardSpeed) * dt;
            Vector3 candidate = transform.position + movement;
            if (Mathf.Abs(candidate.x) > 975f) movement.x = 0f;
            if (Mathf.Abs(candidate.z) > 975f) movement.z = 0f;
            CollisionFlags flags = capsule.Move(movement);
            if ((flags & CollisionFlags.Below) != 0 && downwardSpeed < -0.1f) Land();
            else if (phase == DropPhase.Gliding && DistanceToSupport() <= 0.48f)
                Land();
        }

        private float DistanceToSupport()
        {
            Vector3 position = transform.position;
            float support = -38.07552f;
            var terrains = Terrain.activeTerrains;
            if (terrains != null)
                foreach (var t in terrains)
                {
                    if (t == null || t.terrainData == null) continue;
                    Vector3 p = t.transform.position, sz = t.terrainData.size;
                    if (position.x >= p.x && position.x <= p.x + sz.x &&
                        position.z >= p.z && position.z <= p.z + sz.z)
                        support = Mathf.Max(support, p.y + t.SampleHeight(position));
                }

            int n = Physics.RaycastNonAlloc(
                new Ray(position + Vector3.up * 0.75f, Vector3.down),
                groundHits, 1600f, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);
            if (n < groundHits.Length)
                for (int i = 0; i < n; i++)
                {
                    RaycastHit h = groundHits[i];
                    if (h.collider == null || h.collider.transform.IsChildOf(transform) ||
                        h.normal.y < 0.5f || h.point.y > position.y + 0.05f) continue;
                    support = Mathf.Max(support, h.point.y);
                }
            // Saturated physics queries fail safely: deploy early rather than fall through cover.
            if (n >= groundHits.Length) return 0f;
            return Mathf.Max(0f, position.y - support);
        }

        public void OnAction()
        {
            if (phase == DropPhase.InPlane) Eject();
            else if (phase == DropPhase.Freefall) OpenCanopy();
        }

        private void Eject()
        {
            if (phase != DropPhase.InPlane || plane == null) return;
            phase = DropPhase.Freefall;
            transform.position = plane.transform.position - Vector3.up * 7f;
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            ShowPlayer();
            capsule.enabled = true;
            downwardSpeed = -3f;
            horizontalVelocity = Vector3.zero;
            airborneSince = Time.time;
            if (hud != null) hud.SetAction("OPEN", true);
            if (plane != null) Destroy(plane, 4.5f);
            plane = null;
        }

        private void OpenCanopy()
        {
            if (phase != DropPhase.Freefall || Time.time - airborneSince < 0.35f) return;
            phase = DropPhase.Opening;
            openingProgress = 0f;
            canopy = AarisOfflineDropVisuals.CreateElongatedCanopy(transform);
            canopy.transform.localScale = Vector3.one * 0.05f;
            if (hud != null) hud.SetAction("OPEN", false);
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
            capsule.enabled = true;
            if (combat != null) combat.enabled = combatWasEnabled;
            if (battleCanvas != null) battleCanvas.enabled = battleCanvasWasEnabled;
            if (weaponCanvas != null) weaponCanvas.enabled = weaponCanvasWasEnabled;
            if (hud != null) { Destroy(hud.gameObject); hud = null; }
            // Motor restores its own cached yaw, camera, and on-foot gravity.
            footMotor.ResumeFromAerialDrop();
            footMotor.enabled = true;
            phase = DropPhase.Landed;
            Debug.Log("Aaris offline drop: parachute touchdown; on-foot controls restored.");
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
            if (n >= cameraHits.Length) return focus + direction * 0.35f;
            float allowed = distance;
            for (int i = 0; i < n; i++)
            {
                var hit = cameraHits[i];
                if (hit.collider == null || hit.collider.transform.IsChildOf(transform) ||
                    (plane != null && hit.collider.transform.IsChildOf(plane.transform))) continue;
                allowed = Mathf.Min(allowed, Mathf.Max(0.35f, hit.distance - 0.2f));
            }
            return focus + direction * allowed;
        }

        private void LateUpdate()
        {
            if (view == null || phase == DropPhase.Landed) return;
            Vector3 pos, focus;
            if (phase == DropPhase.InPlane && plane != null)
            {
                pos = plane.transform.position - plane.transform.forward * 28f + Vector3.up * 10f;
                focus = plane.transform.position + plane.transform.forward * 4f;
            }
            else
            {
                Vector3 behind = Quaternion.Euler(-pitch, yaw, 0f) * Vector3.back;
                pos = transform.position + behind * (phase == DropPhase.Freefall ? 9.5f : 8.5f)
                    + Vector3.up * (phase == DropPhase.Freefall ? 3.2f : 4.6f);
                focus = transform.position + Vector3.up * 2.8f;
            }
            pos = CameraSafePosition(focus, pos);
            view.transform.position = Vector3.Lerp(view.transform.position,
                pos, 1f - Mathf.Exp(-7f * Time.deltaTime));
            view.transform.rotation = Quaternion.Slerp(view.transform.rotation,
                Quaternion.LookRotation(focus - view.transform.position, Vector3.up),
                1f - Mathf.Exp(-9f * Time.deltaTime));
        }

        private void OnDisable()
        {
            if (phase == DropPhase.Landed || footMotor == null) return;
            if (capsule != null) capsule.enabled = true;
            if (combat != null) combat.enabled = combatWasEnabled;
            if (battleCanvas != null) battleCanvas.enabled = battleCanvasWasEnabled;
            if (weaponCanvas != null) weaponCanvas.enabled = weaponCanvasWasEnabled;
            ShowPlayer();
            footMotor.enabled = true;
        }
    }

    /// <summary>Dedicated portrait/landscape-safe touch HUD; no 1920x1080 hard-coded overlay.</summary>
    public sealed class AarisOfflineDropHud : MonoBehaviour
    {
        private AarisOfflineDrop owner;
        private Canvas canvas;
        private RectTransform safe;
        private Button action;
        private Text actionLabel, altitude;
        private AarisOfflineDropStick stick;
        public Vector2 MoveInput => stick != null ? stick.Value : Vector2.zero;

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
            buttonRect.anchorMin = buttonRect.anchorMax = new Vector2(0.84f, 0.55f);
            buttonRect.sizeDelta = new Vector2(145f, 70f);
            buttonRoot.GetComponent<Image>().color = new Color(0.96f, 0.51f, 0.14f, 0.96f);
            action = buttonRoot.GetComponent<Button>();
            action.onClick.AddListener(() => { if (owner != null) owner.OnAction(); });
            actionLabel = Label(buttonRoot.transform, "EJECT", 30, TextAnchor.MiddleCenter);
            Stretch(actionLabel.rectTransform);

            var altitudeRoot = new GameObject("Flight Telemetry", typeof(RectTransform));
            altitudeRoot.transform.SetParent(safe, false);
            var altRect = altitudeRoot.GetComponent<RectTransform>();
            altRect.anchorMin = altRect.anchorMax = new Vector2(0.80f, 0.89f);
            altRect.sizeDelta = new Vector2(220f, 75f);
            altitude = Label(altitudeRoot.transform, "ALT  -- m\nSPD  -- km/h", 20, TextAnchor.MiddleRight);
            Stretch(altitude.rectTransform);

            var baseStick = new GameObject("Skydiving Stick", typeof(RectTransform),
                typeof(Image), typeof(AarisOfflineDropStick));
            baseStick.transform.SetParent(safe, false);
            var rect = baseStick.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.12f, 0.20f);
            rect.sizeDelta = new Vector2(142f, 142f);
            baseStick.GetComponent<Image>().color = new Color(0.13f, 0.20f, 0.27f, 0.63f);
            stick = baseStick.GetComponent<AarisOfflineDropStick>();
            stick.Build();
        }

        private void ApplySafeArea()
        {
            Rect area = Screen.safeArea;
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
            if (altitude != null)
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
            knob.GetComponent<Image>().color = new Color(0.25f, 0.77f, 0.93f, 0.92f);
            knob.GetComponent<Image>().raycastTarget = false;
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
            if (finger != data.pointerId) return;
            finger = int.MinValue;
            Value = Vector2.zero;
            if (knob != null) knob.anchoredPosition = Vector2.zero;
        }
        private void OnDisable()
        {
            finger = int.MinValue;
            Value = Vector2.zero;
        }
    }
}
