using UnityEngine;
using UnityEngine.UI;

namespace Aaris.MapReview
{
    /// <summary>
    /// Offline, non-intercepting HUD. All shot feedback is prebuilt; targeting
    /// queries run at 10 Hz instead of in the render loop. No online assets.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AarisCombatFeedbackHUD : MonoBehaviour
    {
        private static AarisCombatFeedbackHUD instance;
                private RaycastHit[] assistedHits = new RaycastHit[32];
        private readonly Text[] damageLabels = new Text[8];
        private readonly float[] damageUntil = new float[8];
        private readonly string[] damageStrings = new string[601];
        private readonly string[] healthStrings = new string[201];
        private AarisThirdPersonPlayer player;
        private AarisWeaponInventory inventory;
        private AarisOfflineHealth health;
        private AarisOfflineBRDirector director;
        private Camera aimCamera;
        private Canvas canvas;
        private RectTransform safeRoot;
        private Text healthLabel, scopeLabel, directionLabel;
        private Image healthFill, flash, reticle, twoXRing, fourXRing;
        private readonly Image[] fourXLines = new Image[4];
        private int lastHealth = -1, lastMaximum = -1, labelCursor;
        private float flashUntil, directionUntil, nextAim, nextSafeArea, nextDirectorLookup;
        private Vector2 previousScreen;
        private Rect previousSafe;
        private Vector3 assistedDirection;
        private AarisEquippedGun assistedGun;
        private float assistedUntil;
        private bool locked;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            EnsureForPlayer(FindFirstObjectByType<AarisThirdPersonPlayer>());
        }

        internal static void EnsureForPlayer(AarisThirdPersonPlayer actor)
        {
            if (actor != null && actor.GetComponent<AarisCombatFeedbackHUD>() == null)
                actor.gameObject.AddComponent<AarisCombatFeedbackHUD>();
        }

        private void Awake()
        {
            player = GetComponent<AarisThirdPersonPlayer>();
            inventory = GetComponent<AarisWeaponInventory>();
            health = GetComponent<AarisOfflineHealth>();
            if (health == null) health = gameObject.AddComponent<AarisOfflineHealth>();
            for (int i = 0; i < damageStrings.Length; i++) damageStrings[i] = i.ToString();
            for (int i = 0; i < healthStrings.Length; i++) healthStrings[i] = "HP " + i + " / 200";
        }

        private void Start()
        {
            if (player == null || (instance != null && instance != this))
            {
                enabled = false;
                return;
            }
            instance = this;
            health.Damaged += OnPlayerDamaged;
            health.HealthChanged += OnHealthChanged;
            director = FindFirstObjectByType<AarisOfflineBRDirector>();
            BuildHud();
            OnHealthChanged(health);
        }

        private static Text MakeText(Transform parent, string name, int fontSize,
            TextAnchor alignment, Color tint)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var label = go.GetComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = fontSize;
            label.alignment = alignment;
            label.color = tint;
            label.raycastTarget = false;
            return label;
        }

        private static Image MakeImage(Transform parent, string name, Color tint)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.color = tint;
            image.raycastTarget = false;
            return image;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        private void BuildHud()
        {
            var root = new GameObject("Combat feedback HUD", typeof(RectTransform),
                typeof(Canvas), typeof(CanvasScaler));
            root.transform.SetParent(transform, false);
            canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 182;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(960f, 540f);
            scaler.matchWidthOrHeight = .5f;

            var safe = new GameObject("Safe area", typeof(RectTransform));
            safe.transform.SetParent(root.transform, false);
            safeRoot = safe.GetComponent<RectTransform>();
            UpdateSafeArea(true);

            var panel = MakeImage(safeRoot, "HP background",
                new Color(.055f, .09f, .12f, .78f));
            var bar = panel.rectTransform;
            bar.anchorMin = bar.anchorMax = new Vector2(0f, 1f);
            bar.pivot = new Vector2(0f, 1f);
            bar.anchoredPosition = new Vector2(15f, -16f);
            bar.sizeDelta = new Vector2(246f, 55f);
            healthLabel = MakeText(bar, "HP 200 / 200", 20,
                TextAnchor.UpperLeft, Color.white);
            var hr = healthLabel.rectTransform;
            hr.anchorMin = hr.anchorMax = new Vector2(0f, 1f);
            hr.pivot = new Vector2(0f, 1f);
            hr.anchoredPosition = new Vector2(13f, -5f);
            hr.sizeDelta = new Vector2(221f, 28f);
            var track = MakeImage(bar, "HP track", new Color(.2f, .23f, .24f, .9f));
            var trackRect = track.rectTransform;
            trackRect.anchorMin = trackRect.anchorMax = new Vector2(.5f, 0f);
            trackRect.sizeDelta = new Vector2(220f, 10f);
            trackRect.anchoredPosition = new Vector2(0f, 10f);
            healthFill = MakeImage(trackRect, "Current HP", new Color(.31f, .94f, .55f, 1f));
            Stretch(healthFill.rectTransform);
            healthFill.type = Image.Type.Filled;
            healthFill.fillMethod = Image.FillMethod.Horizontal;

            reticle = MakeImage(safeRoot, "Visible aim lock", Color.white);
            var rr = reticle.rectTransform;
            rr.anchorMin = rr.anchorMax = new Vector2(.5f, .5f);
            rr.sizeDelta = new Vector2(5f, 5f);
            rr.anchoredPosition = Vector2.zero;
            for (int i = 0; i < 4; i++)
            {
                var stroke = MakeImage(safeRoot, "Reticle arm", Color.white);
                var r = stroke.rectTransform;
                r.anchorMin = r.anchorMax = new Vector2(.5f, .5f);
                r.sizeDelta = new Vector2(i < 2 ? 13f : 3f, i < 2 ? 3f : 13f);
                r.anchoredPosition = i == 0 ? new Vector2(-17f, 0f) :
                    i == 1 ? new Vector2(17f, 0f) :
                    i == 2 ? new Vector2(0f, 17f) : new Vector2(0f, -17f);
                // Children are independent to keep every element raycast-free.
                strokes[i] = stroke;
            }
            // Zero new meshes or cameras while ADS: prebuilt transparent
            // 2x ring and finer 4x reticle share the same safe-area canvas.
            twoXRing = MakeImage(safeRoot, "2X scope optical ring", Color.white);
            twoXRing.sprite = AarisHudGlyphs.Ring;
            twoXRing.rectTransform.anchorMin = twoXRing.rectTransform.anchorMax =
                new Vector2(.5f, .5f);
            twoXRing.rectTransform.sizeDelta = new Vector2(84f, 84f);
            twoXRing.enabled = false;
            fourXRing = MakeImage(safeRoot, "4X precision ring", Color.white);
            fourXRing.sprite = AarisHudGlyphs.Ring;
            fourXRing.rectTransform.anchorMin = fourXRing.rectTransform.anchorMax =
                new Vector2(.5f, .5f);
            fourXRing.rectTransform.sizeDelta = new Vector2(122f, 122f);
            fourXRing.enabled = false;
            for (int i = 0; i < fourXLines.Length; i++)
            {
                Image line = MakeImage(safeRoot, "4X precision hash", Color.white);
                RectTransform cross = line.rectTransform;
                cross.anchorMin = cross.anchorMax = new Vector2(.5f, .5f);
                cross.sizeDelta = i < 2 ? new Vector2(17f, 2f) : new Vector2(2f, 17f);
                cross.anchoredPosition = i == 0 ? new Vector2(-41f, 0f) :
                    i == 1 ? new Vector2(41f, 0f) :
                    i == 2 ? new Vector2(0f, 41f) : new Vector2(0f, -41f);
                line.enabled = false;
                fourXLines[i] = line;
            }

            scopeLabel = MakeText(safeRoot, "Scope tier", 15,
                TextAnchor.MiddleCenter, new Color(.92f, .96f, 1f, .95f));
            var sr = scopeLabel.rectTransform;
            sr.anchorMin = sr.anchorMax = new Vector2(.5f, .5f);
            sr.sizeDelta = new Vector2(156f, 27f);
            sr.anchoredPosition = new Vector2(0f, -52f);
            scopeLabel.text = "NO SCOPE";

            flash = MakeImage(safeRoot, "Damage flash", new Color(1f, .05f, .06f, 0f));
            Stretch(flash.rectTransform);
            flash.transform.SetAsFirstSibling();
            flash.enabled = false;
            directionLabel = MakeText(safeRoot, "Damage direction", 17,
                TextAnchor.MiddleCenter, new Color(1f, .42f, .38f, 1f));
            var dr = directionLabel.rectTransform;
            dr.anchorMin = dr.anchorMax = new Vector2(.5f, .5f);
            dr.sizeDelta = new Vector2(162f, 32f);
            dr.anchoredPosition = new Vector2(0f, 77f);
            directionLabel.enabled = false;

            for (int i = 0; i < damageLabels.Length; i++)
            {
                var text = MakeText(safeRoot, "Reused damage number", 23,
                    TextAnchor.MiddleCenter, new Color(1f, .84f, .3f, 1f));
                var rect = text.rectTransform;
                rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
                rect.sizeDelta = new Vector2(90f, 35f);
                rect.anchoredPosition = new Vector2(34f + i % 4 * 11f, 30f + i / 4 * 29f);
                text.enabled = false;
                damageLabels[i] = text;
            }
        }

        private readonly Image[] strokes = new Image[4];

        private void UpdateSafeArea(bool force)
        {
            if (!force && Time.unscaledTime < nextSafeArea) return;
            nextSafeArea = Time.unscaledTime + .5f;
            if (safeRoot == null || Screen.width < 1 || Screen.height < 1) return;
            Rect safe = Screen.safeArea;
            Vector2 screen = new Vector2(Screen.width, Screen.height);
            if (!force && screen == previousScreen && safe == previousSafe) return;
            previousScreen = screen;
            previousSafe = safe;
            safeRoot.anchorMin = new Vector2(safe.xMin / Screen.width, safe.yMin / Screen.height);
            safeRoot.anchorMax = new Vector2(safe.xMax / Screen.width, safe.yMax / Screen.height);
            safeRoot.offsetMin = safeRoot.offsetMax = Vector2.zero;
        }

        private void OnHealthChanged(AarisOfflineHealth affected)
        {
            if (affected != health || healthLabel == null) return;
            int hp = Mathf.Clamp(Mathf.CeilToInt(health.CurrentHealth), 0, 200);
            int max = Mathf.RoundToInt(health.MaximumHealth);
            if (hp != lastHealth || max != lastMaximum)
            {
                lastHealth = hp;
                lastMaximum = max;
                healthLabel.text = max == 200 ? healthStrings[hp] :
                    "HP " + hp + " / " + max;
                healthFill.fillAmount = Mathf.Clamp01(health.CurrentHealth /
                    Mathf.Max(1f, health.MaximumHealth));
            }
        }

        private void OnPlayerDamaged(AarisOfflineHealth victim, float actual, GameObject attacker)
        {
            if (victim != health || actual <= 0f) return;
            flashUntil = Time.unscaledTime + .32f;
            directionUntil = Time.unscaledTime + .65f;
            if (attacker == null || aimCamera == null)
                directionLabel.text = attacker == null ? "ZONE DAMAGE" : "INCOMING HIT";
            else
            {
                Vector3 toward = attacker.transform.position - transform.position;
                toward.y = 0f;
                Vector3 forward = aimCamera.transform.forward;
                forward.y = 0f;
                float side = Vector3.Cross(forward.normalized, toward.normalized).y;
                float front = Vector3.Dot(forward.normalized, toward.normalized);
                directionLabel.text = front < -.4f ? "HIT FROM BEHIND" :
                    side > .35f ? "HIT FROM RIGHT" :
                    side < -.35f ? "HIT FROM LEFT" : "HIT FROM FRONT";
            }
            directionLabel.enabled = true;
        }

        public static void NotifyOutgoingDamage(GameObject attacker, float amount, bool headshot)
        {
            if (instance == null || attacker == null || attacker != instance.gameObject ||
                amount <= 0f || Time.timeScale <= 0f) return;
            int index = instance.labelCursor++ % instance.damageLabels.Length;
            Text label = instance.damageLabels[index];
            label.text = instance.damageStrings[Mathf.Clamp(Mathf.CeilToInt(amount), 0, 600)];
            label.color = headshot ? new Color(1f, .34f, .18f, 1f) :
                new Color(1f, .88f, .35f, 1f);
            label.enabled = true;
            instance.damageUntil[index] = Time.unscaledTime + .48f;
        }

        internal static bool TryGetAssistedDirection(Camera camera, GameObject owner,
            AarisEquippedGun gun, out Vector3 direction)
        {
            direction = Vector3.zero;
            if (instance == null || instance.aimCamera != camera ||
                instance.gameObject != owner || instance.assistedGun != gun ||
                !instance.locked || Time.unscaledTime > instance.assistedUntil ||
                instance.inventory == null || !instance.inventory.Aiming) return false;
            direction = instance.assistedDirection;
            return direction.sqrMagnitude > .99f;
        }

        private void RefreshAim()
        {
            locked = false;
            assistedGun = null;
            if (inventory == null) inventory = GetComponent<AarisWeaponInventory>();
            var gun = inventory != null && inventory.HasGun ? inventory.Current : null;
            if (gun == null || health.IsDead || Time.timeScale <= 0f)
            {
                scopeLabel.text = "NO SCOPE";
                if (twoXRing != null) twoXRing.enabled = false;
                if (fourXRing != null) fourXRing.enabled = false;
                for (int i = 0; i < fourXLines.Length; i++)
                    if (fourXLines[i] != null) fourXLines[i].enabled = false;
                SetReticleColor(Color.white);
                return;
            }
            var spec = gun.Spec;
            int tier = spec.category == "Sniper" || spec.adsFov <= 25f ? 4 :
                spec.adsFov <= 44f ? 2 : 0;
            bool zoomed = inventory.Aiming;
            twoXRing.enabled = zoomed && tier == 2;
            fourXRing.enabled = zoomed && tier == 4;
            for (int i = 0; i < fourXLines.Length; i++)
                fourXLines[i].enabled = zoomed && tier == 4;
            string scopeText = tier == 4 ? "4X SCOPE" :
                tier == 2 ? "2X SCOPE" : "NO SCOPE";
            if (scopeLabel.text != scopeText) scopeLabel.text = scopeText;
            if (aimCamera == null || !aimCamera.isActiveAndEnabled)
            {
                SetReticleColor(Color.white);
                return;
            }
            // Aim magnetism applies only while explicitly aiming. It never
            // selects through cover and never bends an unscoped hip-fire shot.
            if (inventory.Aiming && director != null &&
                director.TryAcquireAimAssist(aimCamera, transform,
                    tier == 4 ? 1.25f : tier == 2 ? 2.25f : 3.0f,
                    Mathf.Min(90f, spec.rangeMetres), ref assistedHits,
                    out Vector3 point))
            {
                assistedDirection = (point - aimCamera.transform.position).normalized;
                assistedGun = gun;
                assistedUntil = Time.unscaledTime + .13f;
                locked = true;
            }
            else
            {
                var ray = new Ray(aimCamera.transform.position, aimCamera.transform.forward);
                int n = AarisPhysicsHitBuffer.Raycast(ray, spec.rangeMetres,
                    Physics.DefaultRaycastLayers, ref assistedHits, 256,
                    out RaycastHit[] hits);
                float nearest = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    Collider c = hits[i].collider;
                    if (c == null || c.transform.IsChildOf(transform) ||
                        hits[i].distance >= nearest) continue;
                    nearest = hits[i].distance;
                    locked = false;
                    for (Transform node = c.transform; node != null; node = node.parent)
                    {
                        var target = node.GetComponent<AarisOfflineHealth>();
                        if (target != null)
                        {
                            locked = !target.IsDead && target != health;
                            break;
                        }
                    }
                }
            }
            SetReticleColor(locked ? new Color(1f, .12f, .12f, 1f) : Color.white);
        }

        private void SetReticleColor(Color color)
        {
            if (reticle == null || reticle.color == color) return;
            reticle.color = color;
            for (int i = 0; i < strokes.Length; i++) strokes[i].color = color;
            if (twoXRing != null) twoXRing.color = color;
            if (fourXRing != null) fourXRing.color = color;
            for (int i = 0; i < fourXLines.Length; i++)
                if (fourXLines[i] != null) fourXLines[i].color = color;
        }

        private void LateUpdate()
        {
            if (canvas == null || health == null) return;
            UpdateSafeArea(false);
            if (aimCamera == null) aimCamera = Camera.main;
            if (director == null && Time.unscaledTime >= nextDirectorLookup)
            {
                nextDirectorLookup = Time.unscaledTime + 1.5f;
                director = FindFirstObjectByType<AarisOfflineBRDirector>();
            }
            if (Time.unscaledTime >= nextAim)
            {
                nextAim = Time.unscaledTime + .10f;
                RefreshAim();
            }
            if (flash != null && Time.unscaledTime < flashUntil)
            {
                flash.enabled = true;
                Color c = flash.color;
                c.a = .21f * Mathf.Clamp01((flashUntil - Time.unscaledTime) / .32f);
                flash.color = c;
            }
            else if (flash != null && flash.enabled) flash.enabled = false;
            if (directionLabel != null && directionLabel.enabled &&
                Time.unscaledTime >= directionUntil) directionLabel.enabled = false;
            for (int i = 0; i < damageLabels.Length; i++)
                if (damageLabels[i] != null && damageLabels[i].enabled &&
                    Time.unscaledTime >= damageUntil[i])
                    damageLabels[i].enabled = false;
        }

        private void OnDestroy()
        {
            if (health != null)
            {
                health.Damaged -= OnPlayerDamaged;
                health.HealthChanged -= OnHealthChanged;
            }
            if (instance == this) instance = null;
        }
    }
}
