using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Aaris.MapReview
{
    /// <summary>
    /// Exactly five transparent mobile action buttons, laid out in safe-area
    /// coordinates. Input and presentation are decoupled from movement/physics.
    /// Created once at scene load; no prefab, Inspector setup or online images.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AarisBattleHUD : MonoBehaviour
    {
        internal enum Action { Fire, Jump, Crouch, Prone, Sprint }

        private static AarisBattleHUD instance;
        public static bool IsVisible => instance != null && instance.isActiveAndEnabled &&
            instance.canvas != null && instance.canvas.isActiveAndEnabled;
        private Canvas canvas;
        private RectTransform moveRing, moveKnob;
        private readonly List<AarisHudButton> buttons = new List<AarisHudButton>(5);
        private AarisBattleActions actions;
        private AarisThirdPersonPlayer player;
        private RectTransform safeRoot;
        private Vector2 previousScreen;
        private Rect previousSafeArea;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            EnsureForPlayer(FindFirstObjectByType<AarisThirdPersonPlayer>());
        }

        internal static void EnsureForPlayer(AarisThirdPersonPlayer existing)
        {
            if (existing == null || FindFirstObjectByType<AarisBattleHUD>() != null) return;

            var state = existing.GetComponent<AarisBattleActions>();
            if (state == null) state = existing.gameObject.AddComponent<AarisBattleActions>();

            var hudObject = new GameObject("Aaris Mobile Battle HUD");
            var hud = hudObject.AddComponent<AarisBattleHUD>();
            hud.Build(existing, state);
        }

        private static void CreateEventSystemIfMissing()
        {
            if (FindFirstObjectByType<EventSystem>() != null) return;
            var es = new GameObject("Aaris UI Event System");
            es.AddComponent<EventSystem>();
            // The saved project explicitly uses Unity's classic Input Manager.
            es.AddComponent<StandaloneInputModule>();
        }

        private void Build(AarisThirdPersonPlayer motor, AarisBattleActions state)
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;
            actions = state;
            player = motor;
            CreateEventSystemIfMissing();

            var canvasObj = new GameObject("Mobile Battle Canvas", typeof(RectTransform),
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObj.transform.SetParent(transform, false);
            canvas = canvasObj.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 180;
            var scaler = canvasObj.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(960f, 540f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            var safeAreaObject = new GameObject("Safe Area", typeof(RectTransform));
            safeAreaObject.transform.SetParent(canvasObj.transform, false);
            safeRoot = safeAreaObject.GetComponent<RectTransform>();
            safeRoot.offsetMin = Vector2.zero;
            safeRoot.offsetMax = Vector2.zero;

            // Positions are local to the display cutout-safe rectangle. The
            // reference style uses a left sprint and right clustered actions.
            Add(Action.Sprint, new Vector2(0f, 0.66f), new Vector2(173f, 0f), 84f);
            Add(Action.Fire, new Vector2(1f, 0.49f), new Vector2(-218f, 0f), 108f);
            Add(Action.Jump, new Vector2(1f, 0.51f), new Vector2(-78f, 0f), 88f);
            Add(Action.Crouch, new Vector2(1f, 0.17f), new Vector2(-78f, 0f), 86f);
            Add(Action.Prone, new Vector2(1f, 0.15f), new Vector2(-193f, 0f), 84f);

            moveRing = AddStickGraphic("Move Stick", AarisHudGlyphs.Ring, safeRoot);
            moveKnob = AddStickGraphic("Move Knob", AarisHudGlyphs.Knob, moveRing);
            AarisHudGlyphs.ApplyCanvasShader(canvasObj.transform);
            UpdateSafeArea(true);
            Debug.Log("Aaris HUD: five original offline action buttons enabled: Fire/Punch, Jump, Crouch, Prone, Sprint.");
        }

        private void Add(Action action, Vector2 anchor, Vector2 offset, float diameter)
        {
            var item = new GameObject("HUD " + action, typeof(RectTransform),
                typeof(Image), typeof(AarisHudButton));
            item.transform.SetParent(safeRoot, false);
            var rect = item.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = offset;
            rect.sizeDelta = new Vector2(diameter, diameter);

            var background = item.GetComponent<Image>();
            background.sprite = AarisHudGlyphs.Ring;
            background.type = Image.Type.Simple;
            background.preserveAspect = true;
            background.raycastTarget = true;

            var iconObj = new GameObject("White " + action + " Silhouette",
                typeof(RectTransform), typeof(Image));
            iconObj.transform.SetParent(item.transform, false);
            var iconRect = iconObj.GetComponent<RectTransform>();
            iconRect.anchorMin = Vector2.zero;
            iconRect.anchorMax = Vector2.one;
            iconRect.offsetMin = new Vector2(diameter * 0.23f, diameter * 0.23f);
            iconRect.offsetMax = new Vector2(-diameter * 0.23f, -diameter * 0.23f);
            var icon = iconObj.GetComponent<Image>();
            icon.sprite = AarisHudGlyphs.Get((AarisHudGlyphs.Glyph)action);
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            icon.color = new Color(1f, 1f, 1f, 0.97f);

            var button = item.GetComponent<AarisHudButton>();
            button.Configure(this, action, background);
            buttons.Add(button);
        }

        private static RectTransform AddStickGraphic(string name, Sprite sprite, RectTransform parent)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(Image));
            var image = obj.GetComponent<Image>();
            image.rectTransform.SetParent(parent, false);
            image.sprite = sprite;
            image.raycastTarget = false;
            return image.rectTransform;
        }

        private void UpdateSafeArea(bool force)
        {
            var safe = Screen.safeArea;
            var resolution = new Vector2(Screen.width, Screen.height);
            if (!force && previousScreen == resolution && previousSafeArea == safe) return;
            previousScreen = resolution;
            previousSafeArea = safe;
            if (Screen.width <= 0 || Screen.height <= 0) return;
            safeRoot.anchorMin = new Vector2(safe.xMin / Screen.width, safe.yMin / Screen.height);
            safeRoot.anchorMax = new Vector2(safe.xMax / Screen.width, safe.yMax / Screen.height);
            safeRoot.offsetMin = safeRoot.offsetMax = Vector2.zero;
        }

        private void LateUpdate()
        {
            if (!IsVisible) return;
            UpdateSafeArea(false);
            if (player != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(
                safeRoot, player.MoveStickScreenCenter, null, out Vector2 stickPosition))
            {
                float scale = player.MoveStickScreenScale / canvas.scaleFactor;
                // Writing unchanged RectTransforms dirties the mobile Canvas.
                // Idle frames must not rebuild the joystick UI.
                Vector2 ringSize = Vector2.one * (152f * scale);
                Vector2 knobPosition = player.MoveStickInput * (44f * scale);
                Vector2 knobSize = Vector2.one * (56f * scale);
                if (moveRing.anchoredPosition != stickPosition)
                    moveRing.anchoredPosition = stickPosition;
                if (moveRing.sizeDelta != ringSize)
                    moveRing.sizeDelta = ringSize;
                if (moveKnob.anchoredPosition != knobPosition)
                    moveKnob.anchoredPosition = knobPosition;
                if (moveKnob.sizeDelta != knobSize)
                    moveKnob.sizeDelta = knobSize;
            }
            foreach (var button in buttons)
            {
                bool selected = button.Kind == Action.Sprint && player != null && player.IsAutoSprinting ||
                    button.Kind == Action.Fire && actions != null && actions.FireHeld ||
                    button.Kind == Action.Crouch && actions != null &&
                        actions.Stance == AarisBattleActions.PlayerStance.Crouching ||
                    button.Kind == Action.Prone && actions != null &&
                        actions.Stance == AarisBattleActions.PlayerStance.Prone;
                button.SetSelected(selected);
            }
        }

        internal void Down(Action action)
        {
            if (actions == null || player == null) return;
            switch (action)
            {
                case Action.Fire: actions.SetFireHeld(true); break;
                case Action.Jump: player.SetJumpHeld(true); break;
                // Stance authority cancels auto-run only on an accepted low pose.
                case Action.Crouch: actions.ToggleCrouch(); break;
                case Action.Prone: actions.ToggleProne(); break;
                case Action.Sprint: player.ToggleAutoSprint(); break;
            }
        }

        internal void ForwardLookDelta(Vector2 pixels)
        {
            if (IsVisible && player != null) player.ForwardLookDelta(pixels);
        }

        internal void Up(Action action)
        {
            if (action == Action.Fire && actions != null) actions.SetFireHeld(false);
            if (action == Action.Jump && player != null) player.SetJumpHeld(false);
        }

        /// <summary>Use on touch BEGAN to keep HUD fingers out of the camera joystick.</summary>
        public static bool CapturesScreenPosition(Vector2 screenPosition)
        {
            if (!IsVisible) return false;
            foreach (var b in instance.buttons)
                if (b != null && b.IsWithin(screenPosition)) return true;
            // The decorative stick is not a raycast target. Capture its
            // screen-space area so a second thumb never becomes lookFinger.
            // The player motor claims the FIRST stick finger before this test.
            if (instance.player != null)
            {
                Vector2 delta = screenPosition - instance.player.MoveStickScreenCenter;
                float radius = 110f * instance.player.MoveStickScreenScale;
                if (delta.sqrMagnitude <= radius * radius) return true;
            }
            return false;
        }

        /// <summary>Clear held Fire/Jump before switching control modes.</summary>
        public void CancelVehicleInputs() => ReleaseAllPointers();

        private void ReleaseAllPointers()
        {
            // On Android a fingerprint can disappear during app switching
            // without any PointerUp/Cancel from the UI EventSystem.
            // Otherwise the next touch cannot press these buttons again.
            foreach (var button in buttons)
                if (button != null) button.CancelHold();
            if (actions != null) actions.SetFireHeld(false);
            if (player != null) player.SetJumpHeld(false);
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) ReleaseAllPointers();
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused) ReleaseAllPointers();
        }

        private void OnEnable()
        {
            // A paused or temporarily disabled HUD must reclaim its shared
            // input visibility after reactivation. Boot() only runs at scene
            // load, so OnDisable's cleared instance was previously never set
            // again, leaving all five controls unresponsive on re-enable.
            // OnEnable precedes Build() for a newly added component: only
            // register an already constructed canvas and never steal another HUD.
            if (canvas == null || (instance != null && instance != this)) return;
            instance = this;
            UpdateSafeArea(true);
        }

        private void OnDisable()
        {
            ReleaseAllPointers();
            if (instance == this) instance = null;
        }
    }
}
