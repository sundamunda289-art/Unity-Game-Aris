using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Aaris.MapReview
{
    /// <summary>
    /// Compact utility HUD: ADS, PICK, DROP, RELOAD, SWAP and FIST/DRAW.
    /// The original five movement/combat buttons remain unchanged.
    /// Only PICK is available while empty-handed near floor loot.
    /// </summary>
    public sealed class AarisWeaponHUD : MonoBehaviour
    {
        private static AarisWeaponHUD instance;
        private readonly List<RectTransform> hitRects = new List<RectTransform>(6);
        private readonly List<Button> buttons = new List<Button>(6);
        private readonly List<Text> captions = new List<Text>(6);
        private AarisWeaponInventory inventory;
        private Text nearbyGunHint;
        private Transform nearbyGunOwner;
        private float nextNearbyGunHintAt;
        private Canvas canvas;
        private RectTransform safeRoot;
        private Vector2 previousScreen;
        private Rect previousSafe;
        private int shownMagazine = -1, shownReserve = -1;
        private bool shownReloading;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            EnsureForPlayer(FindFirstObjectByType<AarisThirdPersonPlayer>());
        }

        internal static void EnsureForPlayer(AarisThirdPersonPlayer player)
        {
            if (player == null || FindFirstObjectByType<AarisWeaponHUD>() != null) return;
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
            nearbyGunOwner = inv.transform;
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
            canvas = go.GetComponent<Canvas>();
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
            Add("SWAP", new Vector2(-141f, -137f), () => inventory.SwapWeapons());
            Add("FIST", new Vector2(-57f, -137f), () => inventory.ToggleFists());
            AddNearbyGunHint();
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
            image.color = Color.white;
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

        private void AddNearbyGunHint()
        {
            // Text-only, raycast-free location feedback: no extra 3D mesh,
            // camera scan, lighting pass or mobile control obstruction.
            var hint = new GameObject("Nearby gun direction hint",
                typeof(RectTransform), typeof(Text));
            hint.transform.SetParent(safeRoot, false);
            var rect = hint.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, 1f);
            rect.pivot = new Vector2(.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -18f);
            rect.sizeDelta = new Vector2(320f, 30f);
            nearbyGunHint = hint.GetComponent<Text>();
            nearbyGunHint.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            nearbyGunHint.alignment = TextAnchor.MiddleCenter;
            nearbyGunHint.fontSize = 15;
            nearbyGunHint.color = new Color(.9f, .95f, 1f, .9f);
            nearbyGunHint.raycastTarget = false;
            nearbyGunHint.enabled = false;
        }

        private void RefreshNearbyGunHint()
        {
            if (nearbyGunHint == null || nearbyGunOwner == null)
                return;
            AarisWeaponPickup closest = null;
            float closestSquare = 48f * 48f;
            Vector3 playerPosition = nearbyGunOwner.position;
            // Only the <=40 live, already-streamed pickups are checked.
            // No physics query, FindObjectsByType call or world activation.
            foreach (var pickup in AarisWeaponPickup.ActivePickups)
            {
                if (pickup == null || pickup.IsConsumed || pickup.Spec == null ||
                    !pickup.gameObject.activeInHierarchy) continue;
                Vector3 direction = pickup.transform.position - playerPosition;
                if (Mathf.Abs(direction.y) > 4f) continue;
                float distanceSquare = direction.sqrMagnitude;
                if (distanceSquare >= closestSquare) continue;
                closest = pickup;
                closestSquare = distanceSquare;
            }
            if (closest == null)
            {
                nearbyGunHint.enabled = false;
                return;
            }
            Vector3 toGun = closest.transform.position - playerPosition;
            float side = Vector3.Dot(nearbyGunOwner.right, toGun.normalized);
            string directionLabel = side > .30f ? "RIGHT" :
                side < -.30f ? "LEFT" : "AHEAD";
            // Proximity only; a solid wall can still obstruct pickup.
            nearbyGunHint.text = "GUN NEARBY  " + closest.Spec.displayName +
                "  " + Mathf.RoundToInt(Mathf.Sqrt(closestSquare)) + "m  " + directionLabel;
            nearbyGunHint.enabled = true;
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
            if (canvas == null || !canvas.isActiveAndEnabled) return;
            UpdateArea(false);
            bool armed = inventory != null && inventory.HasGun;
            // The gun hint is refreshed at most once per 1.25s, never by a
            // scene-wide search or during each mobile HUD frame.
            if (armed)
            {
                if (nearbyGunHint != null) nearbyGunHint.enabled = false;
                nextNearbyGunHintAt = 0f;
            }
            else if (Time.unscaledTime >= nextNearbyGunHintAt)
            {
                nextNearbyGunHintAt = Time.unscaledTime + 1.25f;
                RefreshNearbyGunHint();
            }
            bool canPick = inventory != null && inventory.PickupAvailable;
            bool hasStored = inventory != null && inventory.HasStowedGun;
            bool hasAny = inventory != null && inventory.HasAnyGun;
            for (int i = 0; i < buttons.Count; i++)
            {
                bool show = i == 1 ? canPick :
                    i == 4 ? hasStored : i == 5 ? hasAny : armed;
                if (buttons[i].gameObject.activeSelf != show)
                    buttons[i].gameObject.SetActive(show);
            }
            string fistLabel = inventory != null && inventory.FistMode ? "DRAW" : "FIST";
            if (captions[5].text != fistLabel) captions[5].text = fistLabel;
            if (!armed)
            {
                // A drop while aiming must not retain stale ADS text or
                // cached ammo when another weapon is picked up.
                if (captions[0].text != "ADS") captions[0].text = "ADS";
                shownMagazine = shownReserve = -1;
                shownReloading = false;
                return;
            }
            string aimCaption = inventory.Aiming ? "ADS ON" : "ADS";
            if (captions[0].text != aimCaption)
                captions[0].text = aimCaption;
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
            if (instance == null || !instance.isActiveAndEnabled || instance.canvas == null ||
                !instance.canvas.isActiveAndEnabled) return false;
            foreach (var rect in instance.hitRects)
                if (rect != null && rect.gameObject.activeInHierarchy &&
                    RectTransformUtility.RectangleContainsScreenPoint(rect, screenPosition, null))
                    return true;
            return false;
        }

        private void OnEnable()
        {
            // OnDisable clears the singleton used by touch capture, but the
            // scene-load bootstrap will not re-run on a HUD reactivation.
            // OnEnable also runs before Create() on initial AddComponent:
            // only reclaim a fully constructed canvas, never another HUD.
            if (canvas == null || (instance != null && instance != this)) return;
            instance = this;
            UpdateArea(true);
        }

        private void OnDisable()
        {
            if (instance == this) instance = null;
        }
    }
}
