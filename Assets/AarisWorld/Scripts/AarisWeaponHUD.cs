using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Aaris.MapReview
{
    /// <summary>
    /// Compact utility HUD: ADS, intentional PICK exchange, DROP and RELOAD.
    /// The original five movement/combat buttons remain unchanged.
    /// Only PICK is available while empty-handed near floor loot.
    /// </summary>
    public sealed class AarisWeaponHUD : MonoBehaviour
    {
        private static AarisWeaponHUD instance;
        private readonly List<RectTransform> hitRects = new List<RectTransform>(4);
        private readonly List<Button> buttons = new List<Button>(4);
        private readonly List<Text> captions = new List<Text>(4);
        private AarisWeaponInventory inventory;
        private RectTransform safeRoot;
        private Vector2 previousScreen;
        private Rect previousSafe;
        private int shownMagazine = -1, shownReserve = -1;
        private bool shownReloading;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (FindFirstObjectByType<AarisWeaponHUD>() != null) return;
            var player = FindFirstObjectByType<AarisThirdPersonPlayer>();
            if (player == null) return;
            var inv = player.GetComponent<AarisWeaponInventory>();
            if (inv == null) inv = player.gameObject.AddComponent<AarisWeaponInventory>();
            var go = new GameObject("Aaris Weapon Utility HUD");
            var ui = go.AddComponent<AarisWeaponHUD>();
            ui.Create(inv);
        }

        private void Create(AarisWeaponInventory inv)
        {
            instance = this;
            inventory = inv;
            if (FindFirstObjectByType<EventSystem>() == null)
            {
                var system = new GameObject("Aaris Input EventSystem");
                system.AddComponent<EventSystem>();
                system.AddComponent<StandaloneInputModule>();
            }
            var go = new GameObject("Weapon Canvas",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            go.transform.SetParent(transform, false);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 181;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(960, 540);
            scaler.matchWidthOrHeight = .5f;

            var area = new GameObject("Safe Area", typeof(RectTransform));
            area.transform.SetParent(go.transform, false);
            safeRoot = area.GetComponent<RectTransform>();
            Add("ADS", new Vector2(-309f, -57f), () => inventory.ToggleAim());
            Add("PICK", new Vector2(-225f, -57f), () => inventory.RequestPickup());
            Add("DROP", new Vector2(-141f, -57f), () => inventory.DropCurrent());
            Add("RELOAD", new Vector2(-57f, -57f), () => inventory.Reload());
            AarisHudGlyphs.ApplyCanvasShader(go.transform);
            UpdateArea(true);
        }

        private void Add(string label, Vector2 offset, UnityEngine.Events.UnityAction callback)
        {
            var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(safeRoot, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(.5f, .5f);
            rect.anchoredPosition = offset;
            rect.sizeDelta = new Vector2(66f, 66f);
            var image = go.GetComponent<Image>();
            image.sprite = AarisHudGlyphs.Ring;
            image.color = new Color(.85f, .93f, 1f, .93f);
            var button = go.GetComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(callback);
            var txt = new GameObject("Label", typeof(RectTransform), typeof(Text));
            txt.transform.SetParent(go.transform, false);
            var textRect = txt.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = textRect.offsetMax = Vector2.zero;
            var text = txt.GetComponent<Text>();
            text.text = label;
            text.color = Color.white;
            text.fontSize = 13;
            text.alignment = TextAnchor.MiddleCenter;
            text.raycastTarget = false;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            hitRects.Add(rect);
            buttons.Add(button);
            captions.Add(text);
        }

        private void UpdateArea(bool force)
        {
            Rect safe = Screen.safeArea;
            Vector2 screen = new Vector2(Screen.width, Screen.height);
            if (!force && previousScreen == screen && previousSafe == safe) return;
            previousScreen = screen;
            previousSafe = safe;
            if (Screen.width == 0 || Screen.height == 0) return;
            safeRoot.anchorMin = new Vector2(safe.xMin / Screen.width, safe.yMin / Screen.height);
            safeRoot.anchorMax = new Vector2(safe.xMax / Screen.width, safe.yMax / Screen.height);
            safeRoot.offsetMin = safeRoot.offsetMax = Vector2.zero;
        }

        private void LateUpdate()
        {
            UpdateArea(false);
            bool armed = inventory != null && inventory.HasGun;
            bool canPick = inventory != null && inventory.PickupAvailable;
            for (int i = 0; i < buttons.Count; i++)
            {
                bool show = i == 1 ? canPick : armed;
                if (buttons[i].gameObject.activeSelf != show)
                    buttons[i].gameObject.SetActive(show);
            }
            if (!armed) return;
            captions[0].text = inventory.Aiming ? "ADS ON" : "ADS";
            var gun = inventory.Current;
            if (shownMagazine != gun.Magazine || shownReserve != gun.Reserve || shownReloading != gun.Reloading)
            {
                shownMagazine = gun.Magazine;
                shownReserve = gun.Reserve;
                shownReloading = gun.Reloading;
                captions[3].text = shownReloading ? "LOADING" : shownMagazine + "/" + shownReserve;
            }
        }

        public static bool CapturesScreenPosition(Vector2 screenPosition)
        {
            // PICK must also consume touch when the player is unarmed.
            if (instance == null || !instance.isActiveAndEnabled) return false;
            foreach (var rect in instance.hitRects)
                if (rect != null && rect.gameObject.activeInHierarchy &&
                    RectTransformUtility.RectangleContainsScreenPoint(rect, screenPosition, null))
                    return true;
            return false;
        }

        private void OnDisable()
        {
            if (instance == this) instance = null;
        }
    }
}
