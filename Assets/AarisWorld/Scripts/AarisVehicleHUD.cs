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
            if (e == null || !fingers.Add(e.pointerId)) return;
            if (fingers.Count == 1) State(true);
        }
        public void OnPointerUp(PointerEventData e)
        {
            if (e == null || !fingers.Remove(e.pointerId)) return;
            if (fingers.Count == 0) State(false);
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
                ? new Color(.52f, .90f, 1f, 1f) : Color.white;
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
            bg.color = new Color(.55f, .81f, 1f, 1f);
            var button = driveButton.GetComponent<Button>();
            button.targetGraphic = bg;
            button.onClick.AddListener(() =>
            {
                if (session != null)
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
            if (dir < 0 || dir >= pressed.Length) return;
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

        private void LateUpdate()
        {
            UpdateSafe(false);
            if (session == null || arrowPanel == null || driveButton == null) return;
            bool driving = session.IsDriving;
            if (arrowPanel.activeSelf != driving)
            {
                if (!driving) ClearArrows();
                arrowPanel.SetActive(driving);
            }
            bool show = driving || session.CanEnter;
            if (driveButton.activeSelf != show) driveButton.SetActive(show);
            if (show) driveCaption.text = driving ? "EXIT" : "DRIVE";
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) ClearArrows();
        }
        private void OnApplicationFocus(bool focused)
        {
            if (!focused) ClearArrows();
        }
        private void OnDisable() => ClearArrows();
    }
}
