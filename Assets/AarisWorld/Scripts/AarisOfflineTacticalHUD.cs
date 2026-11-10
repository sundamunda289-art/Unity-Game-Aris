using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Aaris.MapReview
{
    /// <summary>
    /// Two small, offline tactical actions wired to the existing player and weapon
    /// camera. The regular five-button battle HUD is not replaced or rewritten.
    /// </summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(250)]
    public sealed class AarisOfflineTacticalHUD : MonoBehaviour
    {
        [SerializeField, Range(0, 10)] private int fragGrenades = 3;
        [SerializeField, Min(0.3f)] private float grenadeCooldown = 1.1f;

        private AarisThirdPersonPlayer motor;
        private AarisOfflineGlooWall gloo;
        private AarisGlooTouchButton wallTouch;
        private AarisOfflineDrop drop;
        private AarisVehicleSession vehicle;
        private Canvas canvas;
        private RectTransform safe;
        private Text wallLabel, grenadeLabel;
        private float nextThrow;
        private int shownWalls = -1, shownGrenades = -1;
        private Rect previousSafeArea;
        private Vector2Int previousScreen;
        private bool initialized;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            AarisThirdPersonPlayer player = FindFirstObjectByType<AarisThirdPersonPlayer>();
            if (player == null) return;
            bool hasDamageReceiver = false;
            foreach (var receiver in player.GetComponents<MonoBehaviour>())
                if (receiver is IAarisDamageable) { hasDamageReceiver = true; break; }
            if (!hasDamageReceiver) player.gameObject.AddComponent<AarisOfflineHealth>();
            if (player.GetComponent<AarisOfflineGlooWall>() == null)
                player.gameObject.AddComponent<AarisOfflineGlooWall>();
            if (player.GetComponent<AarisOfflineTacticalHUD>() == null)
                player.gameObject.AddComponent<AarisOfflineTacticalHUD>();
        }

        private void Start()
        {
            motor = GetComponent<AarisThirdPersonPlayer>();
            gloo = GetComponent<AarisOfflineGlooWall>();
            drop = GetComponent<AarisOfflineDrop>();
            vehicle = GetComponent<AarisVehicleSession>();
            if (motor == null || gloo == null) { enabled = false; return; }
            Build();
            initialized = true;
        }

        private void Update()
        {
            if (!initialized) return;
            if (drop == null) drop = GetComponent<AarisOfflineDrop>();
            bool onFoot = (drop == null || drop.HasLanded) &&
                (vehicle == null || !vehicle.IsDriving);
            if (canvas != null && canvas.enabled != onFoot) canvas.enabled = onFoot;
            if (!onFoot)
            {
                // Canvas.enabled=false does not invoke the touch button's OnDisable.
                // Clear its pointer ID so re-entering on-foot mode never locks WALL.
                if (wallTouch != null) wallTouch.CancelHold();
                else if (gloo.Aiming) gloo.CancelAim();
                return;
            }
            if (Input.GetKeyDown(KeyCode.Q)) ThrowFrag();
            if (shownWalls != gloo.Count)
            {
                shownWalls = gloo.Count;
                wallLabel.text = "WALL\n×" + shownWalls;
            }
            if (shownGrenades != fragGrenades)
            {
                shownGrenades = fragGrenades;
                grenadeLabel.text = "FRAG\n×" + shownGrenades;
            }
        }

        public bool TryGiveGrenades(int amount)
        {
            if (amount <= 0) return false;
            fragGrenades = Mathf.Clamp(fragGrenades + amount, 0, 10);
            return true;
        }

        public void ThrowFrag()
        {
            if (!enabled || fragGrenades <= 0 || motor == null || motor.AimCamera == null ||
                Time.time < nextThrow || (drop != null && !drop.HasLanded) ||
                (vehicle != null && vehicle.IsDriving)) return;
            Camera aim = motor.AimCamera;
            Vector3 forward = aim.transform.forward.normalized;
            Vector3 flat = Vector3.ProjectOnPlane(forward, Vector3.up).normalized;
            if (flat.sqrMagnitude < 0.1f) flat = transform.forward;
            Vector3 origin = transform.position + Vector3.up * 1.45f + flat * 0.85f;
            Vector3 velocity = forward * 16f + Vector3.up * 6f;
            AarisOfflineFragGrenade.Throw(origin, velocity, gameObject);
            fragGrenades--;
            nextThrow = Time.time + grenadeCooldown;
        }

        private void Build()
        {
            if (FindFirstObjectByType<EventSystem>() == null)
                new GameObject("Aaris Tactical Event System", typeof(EventSystem),
                    typeof(StandaloneInputModule));
            var canvasObject = new GameObject("Aaris Tactical Canvas", typeof(RectTransform),
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 200;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(960f, 540f);
            scaler.matchWidthOrHeight = 0.5f;

            safe = new GameObject("Safe Area", typeof(RectTransform)).GetComponent<RectTransform>();
            safe.SetParent(canvasObject.transform, false);
            safe.offsetMin = safe.offsetMax = Vector2.zero;
            RefreshSafeArea();

            wallLabel = CreateAction("Gloo Wall", new Vector2(1f, 0.64f),
                new Vector2(-343f, 0f), new Color(0.64f, 0.86f, 1f, 1f),
                out GameObject wallButtonObject);
            wallTouch = wallButtonObject.AddComponent<AarisGlooTouchButton>();
            wallTouch.Bind(gloo);
            grenadeLabel = CreateAction("Frag Grenade", new Vector2(1f, 0.27f),
                new Vector2(-343f, 0f), new Color(0.94f, 0.86f, 0.66f, 1f),
                out GameObject grenadeButton);
            var button = grenadeButton.AddComponent<Button>();
            button.targetGraphic = grenadeButton.GetComponent<Image>();
            button.onClick.AddListener(ThrowFrag);
            AarisHudGlyphs.ApplyCanvasShader(canvasObject.transform);
        }

        private Text CreateAction(string name, Vector2 anchor, Vector2 offset, Color color,
            out GameObject actionObject)
        {
            actionObject = new GameObject(name, typeof(RectTransform), typeof(Image));
            actionObject.transform.SetParent(safe, false);
            var rect = actionObject.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = anchor;
            rect.anchoredPosition = offset;
            rect.sizeDelta = new Vector2(78f, 78f);
            var image = actionObject.GetComponent<Image>();
            image.sprite = AarisHudGlyphs.Ring;
            image.color = color;
            var textObject = new GameObject("Count", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(actionObject.transform, false);
            var textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = textRect.offsetMax = Vector2.zero;
            var label = textObject.GetComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 16;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.raycastTarget = false;
            return label;
        }

        private void RefreshSafeArea()
        {
            if (safe == null || Screen.width <= 0 || Screen.height <= 0) return;
            Rect area = Screen.safeArea;
            var screen = new Vector2Int(Screen.width, Screen.height);
            if (previousScreen == screen && previousSafeArea == area) return;
            previousScreen = screen;
            previousSafeArea = area;
            safe.anchorMin = new Vector2(area.xMin / Screen.width, area.yMin / Screen.height);
            safe.anchorMax = new Vector2(area.xMax / Screen.width, area.yMax / Screen.height);
            safe.offsetMin = safe.offsetMax = Vector2.zero;
        }

        private void LateUpdate() => RefreshSafeArea();

        private void OnDisable() { if (wallTouch != null) wallTouch.CancelHold(); }

        private void OnDestroy()
        {
            if (canvas != null) Destroy(canvas.gameObject);
        }
    }

    /// <summary>Press-and-hold preview; release to deploy. Pointer cancel never consumes inventory.</summary>
    public sealed class AarisGlooTouchButton : MonoBehaviour,
        IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        private AarisOfflineGlooWall wall;
        private int holding = int.MinValue;

        public void Bind(AarisOfflineGlooWall value) => wall = value;
        public void OnPointerDown(PointerEventData data)
        {
            if (wall == null || holding != int.MinValue) return;
            holding = data.pointerId;
            wall.BeginAim();
        }
        public void OnPointerUp(PointerEventData data)
        {
            if (holding != data.pointerId) return;
            holding = int.MinValue;
            if (wall != null) wall.ReleaseAndDeploy();
        }
        public void OnPointerExit(PointerEventData data)
        {
            if (holding != data.pointerId) return;
            holding = int.MinValue;
            if (wall != null) wall.CancelAim();
        }
        public void CancelHold()
        {
            holding = int.MinValue;
            if (wall != null) wall.CancelAim();
        }
        private void OnDisable() => CancelHold();
        private void OnApplicationPause(bool paused) { if (paused) CancelHold(); }
        private void OnApplicationFocus(bool focused) { if (!focused) CancelHold(); }
    }
}
