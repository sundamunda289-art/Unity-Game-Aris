using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Aaris.MapReview
{
    /// <summary>Reliable Android pointer-down/up held arrow with cancel reset.</summary>
    public sealed class AarisVehicleArrowButton : MonoBehaviour,
        IPointerDownHandler, IPointerUpHandler, ICancelHandler
    {
        private readonly HashSet<int> fingers = new HashSet<int>();
        // Reused only while an arrow is held; no per-frame allocation.
        private readonly List<int> staleFingers = new List<int>(4);
        private AarisVehicleHUD owner;
        private int direction;
        private Image ring;
        public void Initialize(AarisVehicleHUD hud, int command, Image image)
        {
            owner = hud;
            direction = command;
            ring = image;
        }
        public void OnPointerDown(PointerEventData e)
        {
            // Touch and the primary mouse button are valid drive inputs.
            // A secondary mouse click must never latch throttle/steering.
            if (e == null || e.button != PointerEventData.InputButton.Left ||
                !fingers.Add(e.pointerId)) return;
            if (fingers.Count == 1) State(true);
        }
        public void OnPointerUp(PointerEventData e)
        {
            if (e != null) ReleasePointer(e.pointerId);
        }

        private void ReleasePointer(int pointerId)
        {
            if (!fingers.Remove(pointerId)) return;
            if (fingers.Count == 0) State(false);
        }

        // Android can drop PointerUp/Cancel while the app remains focused
        // (system gesture, touch snapshot loss, device/input interruption).
        // Reconcile classic StandaloneInputModule pointer ownership after its
        // EventSystem.Update so a lost finger cannot leave a car accelerating
        // or steering forever. The scratch list is reused and GetTouch is
        // allocation-free; other input modules keep their own pointer IDs.
        private void LateUpdate()
        {
            if (fingers.Count == 0) return;
            // If the EventSystem disappears mid-press, no PointerUp can arrive.
            // Reset both the visible arrow and the authoritative held axis.
            if (EventSystem.current == null)
            {
                Reset();
                return;
            }
            // Non-standalone modules may use different pointer IDs. Never
            // guess which finger owns this arrow; release only after all
            // physical touch and primary mouse input has finished.
            if (!(EventSystem.current.currentInputModule is StandaloneInputModule))
            {
                if (Input.touchCount == 0 && !Input.GetMouseButton(0)) Reset();
                return;
            }

            staleFingers.Clear();
            foreach (int pointerId in fingers)
            {
                if (!IsPointerPhysicallyHeld(pointerId))
                    staleFingers.Add(pointerId);
            }
            for (int i = 0; i < staleFingers.Count; i++)
                ReleasePointer(staleFingers[i]);
            staleFingers.Clear();
        }

        private static bool IsPointerPhysicallyHeld(int pointerId)
        {
            if (pointerId == PointerInputModule.kMouseLeftId)
                return Input.GetMouseButton(0);

            int count = Input.touchCount;
            for (int i = 0; i < count; i++)
            {
                Touch touch = Input.GetTouch(i);
                if (touch.fingerId == pointerId &&
                    touch.phase != TouchPhase.Ended &&
                    touch.phase != TouchPhase.Canceled)
                    return true;
            }
            return false;
        }

        public void OnCancel(BaseEventData e) => Reset();
        private void OnDisable() => Reset();
        private void OnApplicationPause(bool paused) { if (paused) Reset(); }
        private void OnApplicationFocus(bool focus) { if (!focus) Reset(); }
        public void Reset()
        {
            if (fingers.Count == 0) return;
            fingers.Clear();
            State(false);
        }
        private void State(bool pressed)
        {
            if (ring != null) ring.color = pressed
                ? new Color(1f, .85f, .62f, 1f) : Color.white;
            if (owner != null) owner.ArrowState(direction, pressed);
        }
    }

    /// <summary>
    /// One-shot antialiased procedural chevron sprite shared by all arrows.
    /// No runtime HTTP, repeated texture allocation or font-glyph risk.
    /// </summary>
    internal static class AarisVehicleGlyph
    {
        private static Sprite chevrons;
        public static Sprite Chevrons
        {
            get
            {
                if (chevrons != null) return chevrons;
                const int size = 128;
                var pixels = new Color32[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float nx = (x + .5f) / size;
                        float ny = (y + .5f) / size;
                        // Two sharp upward chevrons with finite thickness;
                        // smoothstep gives crisp anti-aliasing on phone.
                        float top = Mathf.Abs((ny - .57f) + Mathf.Abs(nx - .5f) * .88f);
                        float lower = Mathf.Abs((ny - .34f) + Mathf.Abs(nx - .5f) * .88f);
                        float dist = Mathf.Min(top, lower);
                        float coverage = Mathf.Clamp01((.070f - dist) * 128f / 1.5f);
                        if (nx < .20f || nx > .80f) coverage = 0f;
                        pixels[y * size + x] = new Color32(255, 255, 255,
                            (byte)Mathf.RoundToInt(255f * coverage));
                    }
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                texture.name = "AarisPremiumDoubleChevron";
                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                chevrons = Sprite.Create(texture, new Rect(0, 0, size, size),
                    new Vector2(.5f, .5f), 100f);
                return chevrons;
            }
        }
    }

    /// <summary>
    /// Four premium up/down/left/right arrows ONLY while driving.
    /// No separate accelerator pedal or fake multi-speed teleport logic.
    /// </summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(210)]
    public sealed class AarisVehicleHUD : MonoBehaviour
    {
        private AarisVehicleSession session;
        private Canvas canvas;
        private RectTransform safeRoot;
        private GameObject arrowPanel;
        private GameObject driveButton;
        private Text driveCaption;
        private GameObject speedPanel;
        private Text speedCaption;
        private Text conditionCaption;
        private float nextSpeedUpdate;
        // Reuse the existing UI text while speed and damage stay unchanged.
        // No "km/h" or condition-string allocation at every 8 Hz HUD poll.
        private int lastRenderedKmh = -1;
        private string lastRenderedDirection;
        private int lastRenderedConditionPercent = -1;
        private AarisVehicleArrowButton[] arrows = new AarisVehicleArrowButton[4];
        private readonly bool[] pressed = new bool[4];
        private Vector2 lastScreen;
        private Rect lastSafe;

        public void Configure(AarisVehicleSession player)
        {
            session = player;
            var obj = new GameObject("Vehicle Arrow Canvas", typeof(RectTransform),
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            obj.transform.SetParent(transform, false);
            canvas = obj.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 250;
            var scaler = obj.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(960f, 540f);
            scaler.matchWidthOrHeight = .5f;
            var safe = new GameObject("Notch Safe Area", typeof(RectTransform));
            safe.transform.SetParent(obj.transform, false);
            safeRoot = safe.GetComponent<RectTransform>();

            arrowPanel = new GameObject("Four Arrows", typeof(RectTransform));
            arrowPanel.transform.SetParent(safeRoot, false);
            var panel = (RectTransform)arrowPanel.transform;
            panel.anchorMin = Vector2.zero;
            panel.anchorMax = Vector2.one;
            panel.offsetMin = panel.offsetMax = Vector2.zero;
            // Direction: 0 forward, 1 reverse, 2 left, 3 right.
            AddArrow(0, Vector2.zero, new Vector2(118f, 176f), 0f);
            AddArrow(1, Vector2.zero, new Vector2(118f, 70f), 180f);
            AddArrow(2, Vector2.right, new Vector2(-206f, 92f), 90f);
            AddArrow(3, Vector2.right, new Vector2(-99f, 92f), -90f);

            driveButton = new GameObject("Vehicle DRIVE or EXIT",
                typeof(RectTransform), typeof(Image), typeof(Button));
            driveButton.transform.SetParent(safeRoot, false);
            var rect = driveButton.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-78f, -144f);
            rect.sizeDelta = new Vector2(84f, 84f);
            var bg = driveButton.GetComponent<Image>();
            bg.sprite = AarisHudGlyphs.Ring;
            bg.color = Color.white;
            var button = driveButton.GetComponent<Button>();
            button.targetGraphic = bg;
            button.onClick.AddListener(() =>
            {
                if (isActiveAndEnabled && session != null)
                {
                    ClearArrows();
                    session.TryEnterOrExit();
                }
            });
            var labelObject = new GameObject("Caption", typeof(RectTransform), typeof(Text));
            labelObject.transform.SetParent(driveButton.transform, false);
            var labelRect = (RectTransform)labelObject.transform;
            labelRect.anchorMin = Vector2.zero; labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
            driveCaption = labelObject.GetComponent<Text>();
            driveCaption.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            driveCaption.alignment = TextAnchor.MiddleCenter;
            driveCaption.fontSize = 17;
            driveCaption.color = Color.white;
            driveCaption.raycastTarget = false;

            // Real physics speed + forward/reverse direction, not a fake
            // animated speedometer. Anchored to the notch-safe viewport.
            // No world-space gauges, physics queries or per-frame materials.
            speedPanel = new GameObject("Vehicle Speedometer",
                typeof(RectTransform), typeof(Image));
            speedPanel.transform.SetParent(safeRoot, false);
            var speedRect = (RectTransform)speedPanel.transform;
            speedRect.anchorMin = speedRect.anchorMax = new Vector2(.5f, 1f);
            speedRect.anchoredPosition = new Vector2(0f, -52f);
            speedRect.sizeDelta = new Vector2(194f, 68f);
            var speedBackground = speedPanel.GetComponent<Image>();
            speedBackground.color = new Color(.025f, .04f, .075f, .74f);
            speedBackground.raycastTarget = false;
            var speedTextObject = new GameObject("Live speed and direction",
                typeof(RectTransform), typeof(Text));
            speedTextObject.transform.SetParent(speedPanel.transform, false);
            var speedTextRect = (RectTransform)speedTextObject.transform;
            speedTextRect.anchorMin = new Vector2(0f, .30f);
            speedTextRect.anchorMax = Vector2.one;
            speedTextRect.offsetMin = speedTextRect.offsetMax = Vector2.zero;
            speedCaption = speedTextObject.GetComponent<Text>();
            speedCaption.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            speedCaption.fontSize = 22;
            speedCaption.fontStyle = FontStyle.Bold;
            speedCaption.alignment = TextAnchor.MiddleCenter;
            speedCaption.color = Color.white;
            speedCaption.raycastTarget = false;
            speedCaption.text = "0 km/h   N";

            // Small second line appears only after a real vehicle impact.
            // This reports persistent condition; it is not player health.
            var conditionObject = new GameObject("Vehicle Damage Condition",
                typeof(RectTransform), typeof(Text));
            conditionObject.transform.SetParent(speedPanel.transform, false);
            var conditionRect = (RectTransform)conditionObject.transform;
            conditionRect.anchorMin = Vector2.zero;
            conditionRect.anchorMax = new Vector2(1f, .32f);
            conditionRect.offsetMin = conditionRect.offsetMax = Vector2.zero;
            conditionCaption = conditionObject.GetComponent<Text>();
            conditionCaption.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            conditionCaption.fontSize = 13;
            conditionCaption.alignment = TextAnchor.MiddleCenter;
            conditionCaption.color = new Color(1f, .76f, .48f, 1f);
            conditionCaption.raycastTarget = false;
            conditionCaption.enabled = false;
            speedPanel.SetActive(false);
            arrowPanel.SetActive(false);
            driveButton.SetActive(false);
            AarisHudGlyphs.ApplyCanvasShader(obj.transform);
            UpdateSafe(true);
        }

        private void AddArrow(int direction, Vector2 anchor, Vector2 offset, float angle)
        {
            var obj = new GameObject("Drive " + direction,
                typeof(RectTransform), typeof(Image), typeof(AarisVehicleArrowButton));
            obj.transform.SetParent(arrowPanel.transform, false);
            var rect = (RectTransform)obj.transform;
            rect.anchorMin = rect.anchorMax = anchor;
            rect.anchoredPosition = offset;
            rect.sizeDelta = new Vector2(89f, 89f);
            var ring = obj.GetComponent<Image>();
            ring.sprite = AarisHudGlyphs.Ring;
            var child = new GameObject("Double Chevron", typeof(RectTransform), typeof(Image));
            child.transform.SetParent(obj.transform, false);
            var glyphRect = (RectTransform)child.transform;
            glyphRect.anchorMin = Vector2.zero;
            glyphRect.anchorMax = Vector2.one;
            glyphRect.offsetMin = new Vector2(20f, 20f);
            glyphRect.offsetMax = new Vector2(-20f, -20f);
            glyphRect.localRotation = Quaternion.Euler(0f, 0f, angle);
            var icon = child.GetComponent<Image>();
            icon.sprite = AarisVehicleGlyph.Chevrons;
            icon.raycastTarget = false;
            icon.color = Color.white;
            var input = obj.GetComponent<AarisVehicleArrowButton>();
            input.Initialize(this, direction, ring);
            arrows[direction] = input;
        }

        public void ArrowState(int dir, bool on)
        {
            if (dir < 0 || dir >= pressed.Length ||
                (on && !isActiveAndEnabled)) return;
            pressed[dir] = on;
            ApplyInput();
        }

        private void ApplyInput()
        {
            if (session == null) return;
            float move = (pressed[0] ? 1f : 0f) - (pressed[1] ? 1f : 0f);
            float turn = (pressed[3] ? 1f : 0f) - (pressed[2] ? 1f : 0f);
            session.SetSteering(move, turn);
        }

        private void ClearArrows()
        {
            foreach (var a in arrows) if (a != null) a.Reset();
            for (int i = 0; i < pressed.Length; i++) pressed[i] = false;
            ApplyInput();
        }

        private void UpdateSafe(bool force)
        {
            if (safeRoot == null) return;
            var screen = new Vector2(Screen.width, Screen.height);
            var safe = Screen.safeArea;
            if (!force && screen == lastScreen && safe == lastSafe) return;
            lastScreen = screen;
            lastSafe = safe;
            if (Screen.width <= 0 || Screen.height <= 0) return;
            safeRoot.anchorMin = new Vector2(safe.xMin / Screen.width,
                safe.yMin / Screen.height);
            safeRoot.anchorMax = new Vector2(safe.xMax / Screen.width,
                safe.yMax / Screen.height);
            safeRoot.offsetMin = safeRoot.offsetMax = Vector2.zero;
        }

        private void UpdateSpeedIndicator()
        {
            if (speedCaption == null || session == null ||
                Time.unscaledTime < nextSpeedUpdate) return;
            var vehicle = session.DrivenCar;
            if (vehicle == null) return;
            // UI updates at 8 Hz instead of allocating formatted strings on
            // every mobile frame. The motor supplies true PhysX velocity.
            nextSpeedUpdate = Time.unscaledTime + .125f;
            float metresPerSecond = vehicle.TotalSpeedMetresPerSecond;
            int kmh = float.IsNaN(metresPerSecond) || float.IsInfinity(metresPerSecond)
                ? 0 : Mathf.Clamp(Mathf.RoundToInt(metresPerSecond * 3.6f), 0, 350);
            float longitudinal = vehicle.SpeedMetresPerSecond;
            float throttle = vehicle.ThrottleInput;
            string direction = longitudinal < -.4f ||
                (Mathf.Abs(longitudinal) <= .4f && throttle < -.1f) ? "R" :
                longitudinal > .4f || throttle > .1f ? "F" : "N";
            if (kmh != lastRenderedKmh || direction != lastRenderedDirection)
            {
                string caption = kmh + " km/h   " + direction;
                if (speedCaption.text != caption) speedCaption.text = caption;
                lastRenderedKmh = kmh;
                lastRenderedDirection = direction;
            }
            // Brief amber cue for a strong collision. Brake indication
            // remains independent and returns after the impact cue expires.
            Color textColor = vehicle.IsRecentImpact
                ? new Color(1f, .82f, .42f, 1f)
                : vehicle.IsServiceBraking
                    ? new Color(1f, .64f, .56f, 1f) : Color.white;
            if (speedCaption.color != textColor) speedCaption.color = textColor;
            if (conditionCaption != null)
            {
                bool damaged = vehicle.Damage01 > .025f;
                if (conditionCaption.enabled != damaged)
                    conditionCaption.enabled = damaged;
                if (damaged)
                {
                    int percent = Mathf.RoundToInt((1f - vehicle.Damage01) * 100f);
                    if (percent != lastRenderedConditionPercent)
                    {
                        string condition = "CAR CONDITION " + percent + "%";
                        if (conditionCaption.text != condition)
                            conditionCaption.text = condition;
                        lastRenderedConditionPercent = percent;
                    }
                }
                else lastRenderedConditionPercent = -1;
            }
        }

        private void LateUpdate()
        {
            UpdateSafe(false);
            if (arrowPanel == null || driveButton == null) return;
            // A disabled/destroyed session cannot own vehicle input. Hide
            // both controls and release previously held arrow pointers.
            // Keep the car occupancy state intact: only a validated exit may
            // restore the walking CharacterController.
            if (session == null || !session.isActiveAndEnabled)
            {
                if (arrowPanel.activeSelf)
                {
                    ClearArrows();
                    arrowPanel.SetActive(false);
                }
                if (driveButton.activeSelf) driveButton.SetActive(false);
                if (speedPanel != null && speedPanel.activeSelf)
                    speedPanel.SetActive(false);
                return;
            }
            bool driving = session.IsDriving;
            if (arrowPanel.activeSelf != driving)
            {
                if (!driving) ClearArrows();
                arrowPanel.SetActive(driving);
            }
            if (speedPanel != null && speedPanel.activeSelf != driving)
            {
                speedPanel.SetActive(driving);
                nextSpeedUpdate = 0f;
                lastRenderedKmh = -1;
                lastRenderedDirection = null;
                lastRenderedConditionPercent = -1;
            }
            if (driving) UpdateSpeedIndicator();
            bool show = driving || session.CanEnter;
            if (driveButton.activeSelf != show) driveButton.SetActive(show);
            if (show)
            {
                string desiredCaption = driving ? "EXIT" : "DRIVE";
                if (driveCaption.text != desiredCaption)
                    driveCaption.text = desiredCaption;
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) ClearArrows();
        }
        private void OnApplicationFocus(bool focused)
        {
            if (!focused) ClearArrows();
        }
        private void OnDisable()
        {
            // Disabling only this MonoBehaviour leaves child UI objects active.
            // Release held input, then remove every interactive control until
            // LateUpdate can restore valid controls after re-enabling.
            ClearArrows();
            if (arrowPanel != null) arrowPanel.SetActive(false);
            if (driveButton != null) driveButton.SetActive(false);
            if (speedPanel != null) speedPanel.SetActive(false);
            lastRenderedKmh = -1;
            lastRenderedDirection = null;
            lastRenderedConditionPercent = -1;
        }
    }
}
