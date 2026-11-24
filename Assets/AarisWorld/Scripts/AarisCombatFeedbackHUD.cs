using UnityEngine;
using UnityEngine.UI;

namespace Aaris.MapReview
{
    /// <summary>
    /// Read-only combat overlay. Health follows the authoritative damage receiver;
    /// aiming uses the same visibility query as gunfire. No per-hit GameObjects.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AarisCombatFeedbackHUD : MonoBehaviour
    {
        private static AarisCombatFeedbackHUD instance;
        private static readonly string[] DamageLabels = CreateDamageLabels();
        // Cached once: no string allocations on ordinary HP hit events.
        private static readonly string[] Hp200Labels = CreateHp200Labels();
        private readonly Text[] numbers = new Text[10];
        private readonly float[] numberUntil = new float[10];
        private readonly Vector2[] numberOrigin = new Vector2[10];
        private readonly Image[] crosshair = new Image[4];
        private AarisOfflineHealth health;
        private AarisThirdPersonPlayer motor;
        private AarisWeaponInventory inventory;
        private AarisOfflineDrop drop;
        private Camera aimCamera;
        private Canvas canvas;
        private RectTransform canvasRect, healthSafeRoot;
        private Rect lastHealthSafeArea;
        private Vector2 lastHealthScreen;
        private float nextHealthSafeCheck;
        private Image healthFill, hitFlash, opticalRing;
        private Text healthText, scopeText, hitDirection;
        private float displayedHealthFillWidth = -1f, targetHealthFillWidth;
        private float nextAimCheck, nextLockedSightCheck, hitFadeUntil;
        // Rechecking a confirmed lock used to raycast through native PhysX
        // every rendered frame (60+ times/s) between 90ms acquisitions.
        // The 45ms maximum delay is presentation-only: live gun damage still
        // uses its independent authoritative camera/muzzle/cover queries.
        private const float LockedSightCheckInterval = .045f;

        internal static bool ScheduleLockedSightCheck(float now, ref float nextCheck)
        {
            if (now < nextCheck) return false;
            nextCheck = now + LockedSightCheckInterval;
            return true;
        }
        private AarisOfflineRival lockedRival;
        private AarisEquippedGun lastAimGun;
        private Camera lastAimCamera;
        private int lastAimScope = -1;
        private int numberIndex;
        private int lastHealth = -1, lastMax = -1;
        private int lastReticleTier = -1;
        private string lastScope;
        private bool lastLock;

        private static string[] CreateDamageLabels()
        {
            var labels = new string[501];
            for (int i = 0; i < labels.Length; i++) labels[i] = i.ToString();
            return labels;
        }

        private static string[] CreateHp200Labels()
        {
            var labels = new string[201];
            for (int i = 0; i < labels.Length; i++)
                labels[i] = "HP " + i + " / 200";
            return labels;
        }

        private static string ScopeHudText(int power, bool aiming)
        {
            switch (power)
            {
                case 4: return aiming ? "4X ADS" : "4X";
                case 2: return aiming ? "2X ADS" : "2X";
                case 1: return aiming ? "NORMAL ADS" : "NORMAL";
                default: return aiming ? "NO SCOPE ADS" : "NO SCOPE";
            }
        }

        // Only change the existing four UI strokes when the weapon tier changes.
        private void RefreshReticleGeometry(int tier)
        {
            if (lastReticleTier == tier) return;
            lastReticleTier = tier;
            float gap = tier == 0 ? 21f : tier == 1 ? 16f : tier == 2 ? 13f : 10f;
            float stroke = tier == 0 ? 12f : tier == 1 ? 9f : tier == 2 ? 7f : 6f;
            for (int i = 0; i < crosshair.Length; i++)
            {
                RectTransform segment = crosshair[i].rectTransform;
                segment.anchoredPosition = i == 0 ? new Vector2(-gap, 0f) :
                    i == 1 ? new Vector2(gap, 0f) :
                    i == 2 ? new Vector2(0f, -gap) : new Vector2(0f, gap);
                segment.sizeDelta = i < 2 ? new Vector2(stroke, 3f) :
                    new Vector2(3f, stroke);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            EnsureForPlayer(FindFirstObjectByType<AarisThirdPersonPlayer>());
        }

        internal static void EnsureForPlayer(AarisThirdPersonPlayer player)
        {
            if (player == null || player.GetComponent<AarisCombatFeedbackHUD>() != null) return;
            if (player.GetComponent<AarisOfflineHealth>() == null)
                player.gameObject.AddComponent<AarisOfflineHealth>();
            player.gameObject.AddComponent<AarisCombatFeedbackHUD>();
        }

        private void Awake()
        {
            if (instance != null && instance != this) { enabled = false; return; }
            instance = this;
            motor = GetComponent<AarisThirdPersonPlayer>();
            health = GetComponent<AarisOfflineHealth>();
            inventory = GetComponent<AarisWeaponInventory>();
            drop = GetComponent<AarisOfflineDrop>();
            Build();
            if (health != null)
            {
                health.HealthChanged += HealthChanged;
                health.Damaged += Damaged;
                HealthChanged(health);
            }
        }

        private static RectTransform Rect(Transform parent, string label,
            Vector2 anchor, Vector2 size, Vector2 offset)
        {
            var go = new GameObject(label, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = anchor;
            rect.sizeDelta = size;
            rect.anchoredPosition = offset;
            return rect;
        }

        private static Image Block(Transform parent, string name, Vector2 anchor,
            Vector2 size, Vector2 offset, Color color)
        {
            var rect = Rect(parent, name, anchor, size, offset);
            var graphic = rect.gameObject.AddComponent<Image>();
            graphic.color = color;
            graphic.raycastTarget = false;
            return graphic;
        }

        private static Text Label(Transform parent, string name, Vector2 anchor,
            Vector2 size, Vector2 offset, int fontSize, TextAnchor align)
        {
            var rect = Rect(parent, name, anchor, size, offset);
            var label = rect.gameObject.AddComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = fontSize;
            label.alignment = align;
            label.color = Color.white;
            label.raycastTarget = false;
            return label;
        }

        private void Build()
        {
            var root = new GameObject("Combat feedback", typeof(RectTransform),
                typeof(Canvas), typeof(CanvasScaler));
            root.transform.SetParent(transform, false);
            canvasRect = root.GetComponent<RectTransform>();
            canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 186;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(960f, 540f);
            scaler.matchWidthOrHeight = .5f;

            hitFlash = Block(root.transform, "Incoming red flash",
                new Vector2(.5f, .5f), new Vector2(4000f, 4000f),
                Vector2.zero, new Color(1f, .03f, .03f, 0f));
            // Avoid a fullscreen transparent uGUI quad and canvas dirties in
            // idle play. Only a real incoming HP hit needs this overlay.
            hitFlash.gameObject.SetActive(false);
            // The movement joystick occupies the lower-left corner. Keep HP
            // above it and inside the device cutout-safe display rectangle.
            var hpArea = new GameObject("HP safe area", typeof(RectTransform));
            hpArea.transform.SetParent(root.transform, false);
            healthSafeRoot = hpArea.GetComponent<RectTransform>();
            healthSafeRoot.anchorMin = Vector2.zero;
            healthSafeRoot.anchorMax = Vector2.one;
            healthSafeRoot.offsetMin = healthSafeRoot.offsetMax = Vector2.zero;
            RefreshHealthSafeArea(true);
            Block(healthSafeRoot, "HP background", new Vector2(0f, 1f),
                new Vector2(258f, 51f), new Vector2(160f, -90f),
                new Color(.035f, .05f, .065f, .88f));
            var track = Block(healthSafeRoot, "HP track", new Vector2(0f, 1f),
                new Vector2(220f, 10f), new Vector2(160f, -110f),
                new Color(.23f, .21f, .22f, .95f));
            healthFill = Block(track.transform, "HP fill", new Vector2(0f, .5f),
                new Vector2(220f, 10f), Vector2.zero,
                new Color(.20f, .92f, .42f, 1f));
            // Plain uGUI images without a Sprite can ignore Image.fillAmount;
            // use explicit geometry width so the bar always visually shrinks.
            healthFill.rectTransform.pivot = new Vector2(0f, .5f);
            healthText = Label(healthSafeRoot, "Readable HP 200", new Vector2(0f, 1f),
                new Vector2(244f, 30f), new Vector2(160f, -83f),
                21, TextAnchor.MiddleCenter);
            healthText.fontStyle = FontStyle.Bold;

            opticalRing = Block(root.transform, "2X 4X scope optic",
                new Vector2(.5f, .5f), new Vector2(174f, 174f),
                Vector2.zero, new Color(1f, 1f, 1f, .56f));
            opticalRing.sprite = AarisHudGlyphs.Ring;
            opticalRing.gameObject.SetActive(false);
            var reticleRoot = Rect(root.transform, "Red lock reticle",
                new Vector2(.5f, .5f), new Vector2(60f, 60f), Vector2.zero);
            Vector2[] positions = {
                new Vector2(-16f, 0f), new Vector2(16f, 0f),
                new Vector2(0f, -16f), new Vector2(0f, 16f)
            };
            for (int i = 0; i < crosshair.Length; i++)
                crosshair[i] = Block(reticleRoot, "Reticle segment", new Vector2(.5f, .5f),
                    i < 2 ? new Vector2(10f, 3f) : new Vector2(3f, 10f),
                    positions[i], Color.white);
            scopeText = Label(root.transform, "Scope tier", new Vector2(.5f, .5f),
                new Vector2(156f, 26f), new Vector2(0f, -48f),
                15, TextAnchor.MiddleCenter);
            hitDirection = Label(root.transform, "Incoming direction",
                new Vector2(.5f, .75f), new Vector2(240f, 32f),
                Vector2.zero, 20, TextAnchor.MiddleCenter);
            hitDirection.color = new Color(1f, .2f, .2f, 1f);
            hitDirection.gameObject.SetActive(false);

            for (int i = 0; i < numbers.Length; i++)
            {
                numbers[i] = Label(root.transform, "Pooled damage number",
                    new Vector2(.5f, .5f), new Vector2(94f, 30f),
                    Vector2.zero, 22, TextAnchor.MiddleCenter);
                numbers[i].fontStyle = FontStyle.Bold;
                numbers[i].gameObject.SetActive(false);
            }
            AarisHudGlyphs.ApplyCanvasShader(root.transform);
        }

        // Safe-area layout changes only after a rotation/cutout change;
        // no per-frame RectTransform writes or strings in the HP hot path.
        private void RefreshHealthSafeArea(bool force)
        {
            if (healthSafeRoot == null || Screen.width <= 0 || Screen.height <= 0 ||
                (!force && Time.unscaledTime < nextHealthSafeCheck)) return;
            nextHealthSafeCheck = Time.unscaledTime + 0.4f;
            Rect safe = Screen.safeArea;
            Vector2 size = new Vector2(Screen.width, Screen.height);
            if (!force && size == lastHealthScreen && safe == lastHealthSafeArea)
                return;
            lastHealthScreen = size;
            lastHealthSafeArea = safe;
            healthSafeRoot.anchorMin = new Vector2(safe.xMin / size.x, safe.yMin / size.y);
            healthSafeRoot.anchorMax = new Vector2(safe.xMax / size.x, safe.yMax / size.y);
            healthSafeRoot.offsetMin = healthSafeRoot.offsetMax = Vector2.zero;
        }

        private void HealthChanged(AarisOfflineHealth changed)
        {
            if (changed == null || healthText == null) return;
            int current = Mathf.CeilToInt(changed.CurrentHealth);
            int max = Mathf.CeilToInt(changed.MaximumHealth);
            // Fractional HP loss must not continue to advertise a FULL bar.
            // Preserve 1 HP for a living combatant rather than rounding down
            // to 0, but show 199/200 if the actual HP fell below 200.
            if (changed.CurrentHealth < changed.MaximumHealth && current >= max)
                current = Mathf.Max(0, max - 1);
            if (current != lastHealth || max != lastMax)
            {
                lastHealth = current; lastMax = max;
                if (max == 200 && current >= 0 && current < Hp200Labels.Length)
                    healthText.text = Hp200Labels[current];
                else
                    healthText.text = "HP " + current + " / " + max;
                healthFill.color = current <= max * .25f
                    ? new Color(1f, .23f, .18f, 1f)
                    : new Color(.20f, .92f, .42f, 1f);
            }
            // Keep the actual fill width authoritative even if sub-1 HP
            // damage or healing leaves the rounded integer label unchanged.
            targetHealthFillWidth = 220f * Mathf.Clamp01(changed.CurrentHealth /
                Mathf.Max(1f, changed.MaximumHealth));
            if (displayedHealthFillWidth < 0f) UpdateHealthFill(0f);
        }

        private void UpdateHealthFill(float deltaTime)
        {
            if (displayedHealthFillWidth == targetHealthFillWidth) return;
            float fillWidth = displayedHealthFillWidth < 0f ? targetHealthFillWidth :
                Mathf.Lerp(displayedHealthFillWidth, targetHealthFillWidth,
                    1f - Mathf.Exp(-18f * deltaTime));
            if (Mathf.Abs(fillWidth - targetHealthFillWidth) < .05f)
                fillWidth = targetHealthFillWidth;
            if (fillWidth == displayedHealthFillWidth) return;
            displayedHealthFillWidth = fillWidth;
            healthFill.rectTransform.sizeDelta = new Vector2(fillWidth, 10f);
        }

        // The weapon fires through the motor's authored camera, which need not
        // have the MainCamera tag. The red reticle, incoming-hit direction and
        // floating damage projection must use that same live camera reference.
        // Never fall back to a different active camera when the motor is present.
        private void RefreshAimCamera()
        {
            aimCamera = motor != null ? motor.AimCamera : null;
        }

        private void Damaged(AarisOfflineHealth receiver, float amount, GameObject attacker)
        {
            if (receiver != health) return;
            hitFadeUntil = Time.unscaledTime + .35f;
            if (hitFlash != null && !hitFlash.gameObject.activeSelf)
                hitFlash.gameObject.SetActive(true);
            if (attacker == null) { hitDirection.gameObject.SetActive(false); return; }
            Vector3 from = attacker.transform.position - transform.position;
            from.y = 0f;
            if (from.sqrMagnitude < .001f)
            {
                // Self-inflicted damage (e.g. a grenade) has no incoming
                // bearing. Never reuse the last enemy's HIT LEFT/RIGHT label.
                hitDirection.gameObject.SetActive(false);
                return;
            }
            from.Normalize();
            // Camera-relative in independent look/strafe: screen FRONT
            // need not equal the player's body forward.
            RefreshAimCamera();
            Vector3 facing = aimCamera != null
                ? aimCamera.transform.forward : transform.forward;
            facing.y = 0f;
            if (facing.sqrMagnitude < .0001f) facing = transform.forward;
            facing.y = 0f;
            facing.Normalize();
            float side = Vector3.Dot(Vector3.Cross(Vector3.up, facing), from);
            float front = Vector3.Dot(facing, from);
            hitDirection.text = Mathf.Abs(side) > Mathf.Abs(front)
                ? (side > 0f ? "HIT RIGHT" : "HIT LEFT")
                : (front > 0f ? "HIT FRONT" : "HIT BEHIND");
            hitDirection.gameObject.SetActive(true);
        }

        public static void NotifyOutgoingDamage(GameObject attacker, float amount, bool headshot, Vector3 worldHitPosition)
        {
            if (instance == null || attacker == null || instance.gameObject != attacker ||
                !instance.isActiveAndEnabled || amount <= 0f) return;
            instance.AddNumber(amount, headshot, worldHitPosition);
        }

        private void AddNumber(float amount, bool headshot, Vector3 worldHitPosition)
        {
            // A confirmed hit behind the camera used to borrow a default
            // crosshair location and look like damage to a visible target.
            // Project BEFORE claiming a pooled label so offscreen shotgun
            // pellets cannot evict an actually visible damage number.
            // The separate real-HP hit marker still fires through WeaponHUD.
            RefreshAimCamera();
            if (aimCamera == null || canvasRect == null) return;
            Vector3 screen = aimCamera.WorldToScreenPoint(worldHitPosition);
            Rect safe = Screen.safeArea;
            if (screen.z <= 0f || screen.x < safe.xMin || screen.x > safe.xMax ||
                screen.y < safe.yMin || screen.y > safe.yMax) return;
            // One projection per confirmed visible hit, no per-frame work.
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                canvasRect, new Vector2(screen.x, screen.y), null, out Vector2 local))
                return;

            int index = numberIndex++ % numbers.Length;
            int rounded = Mathf.Clamp(Mathf.CeilToInt(amount), 0, DamageLabels.Length - 1);
            var label = numbers[index];
            label.text = DamageLabels[rounded];
            label.color = headshot ? new Color(1f, .36f, .12f, 1f) :
                new Color(1f, .88f, .35f, 1f);
            Vector2 origin = local +
                new Vector2(((index % 5) - 2) * 22f, 21f + (index % 3) * 12f);
            numberOrigin[index] = origin;
            label.rectTransform.anchoredPosition = origin;
            numberUntil[index] = Time.unscaledTime + .75f;
            label.gameObject.SetActive(true);
        }

        private void LateUpdate()
        {
            if (canvas == null) return;
            RefreshHealthSafeArea(false);
            UpdateHealthFill(Time.unscaledDeltaTime);
            float now = Time.unscaledTime;
            if (hitFlash != null && hitFlash.gameObject.activeSelf)
            {
                float remaining = hitFadeUntil - now;
                if (remaining <= 0f)
                    hitFlash.gameObject.SetActive(false);
                else
                    hitFlash.color = new Color(1f, .03f, .03f,
                        Mathf.Clamp01(remaining / .35f) * .2f);
            }
            if (hitDirection != null && hitDirection.gameObject.activeSelf &&
                now >= hitFadeUntil) hitDirection.gameObject.SetActive(false);
            for (int i = 0; i < numbers.Length; i++)
            {
                var label = numbers[i];
                if (label == null || !label.gameObject.activeSelf) continue;
                float remaining = numberUntil[i] - now;
                if (remaining <= 0f) { label.gameObject.SetActive(false); continue; }
                label.rectTransform.anchoredPosition =
                    numberOrigin[i] + Vector2.up * ((.75f - remaining) * 52f);
            }
            if (inventory == null) inventory = GetComponent<AarisWeaponInventory>();
            // Keep the Red-Lock marker honest: if fire input is suspended,
            // hide the optic without hiding the authoritative HP bar.
            bool armed = inventory != null && inventory.CanShowCombatReticle &&
                (drop == null || drop.HasLanded);
            RefreshAimCamera();
            AarisEquippedGun gun = armed ? inventory.Current : null;
            int scope = armed ? gun.Spec.scopePower : 0;
            int activeScope = armed ? inventory.ActiveScopePower : 0;
            RefreshReticleGeometry(activeScope);
            if (gun != lastAimGun || aimCamera != lastAimCamera || activeScope != lastAimScope)
            {
                nextAimCheck = now;
                nextLockedSightCheck = now;
                lastAimGun = gun;
                lastAimCamera = aimCamera;
                lastAimScope = activeScope;
            }
            if (!armed || aimCamera == null || !aimCamera.isActiveAndEnabled)
                lockedRival = null;
            else if (now >= nextAimCheck)
            {
                nextAimCheck = now + .09f;
                nextLockedSightCheck = now + LockedSightCheckInterval;
                AarisOfflineBRDirector.TryAcquireAimLock(aimCamera, transform,
                    gun.Spec.rangeMetres, AarisEquippedGun.AimAssistCone(activeScope),
                    out _, out lockedRival);
            }
            else if (lockedRival != null &&
                ScheduleLockedSightCheck(now, ref nextLockedSightCheck) &&
                !AarisOfflineBRDirector.CanKeepAimLock(aimCamera, transform, lockedRival,
                    gun.Spec.rangeMetres, AarisEquippedGun.AimAssistCone(activeScope)))
            {
                // Force immediate reacquisition on the next visual tick rather
                // than retaining a red marker for a now-occluded opponent.
                lockedRival = null;
                nextAimCheck = now;
            }
            bool lockOn = lockedRival != null;
            string labelText = armed
                ? ScopeHudText(scope, inventory.Aiming) : "";
            if (scopeText.text != labelText) scopeText.text = labelText;
            bool showOptic = armed && inventory.Aiming && scope >= 2;
            if (opticalRing != null)
            {
                if (opticalRing.gameObject.activeSelf != showOptic)
                    opticalRing.gameObject.SetActive(showOptic);
                if (showOptic)
                {
                    float size = scope == 4 ? 226f : 174f;
                    if (opticalRing.rectTransform.sizeDelta.x != size)
                        opticalRing.rectTransform.sizeDelta = new Vector2(size, size);
                }
            }
            if (lastLock != lockOn || lastScope != labelText)
            {
                lastLock = lockOn; lastScope = labelText;
                Color color = lockOn ? new Color(1f, .12f, .10f, 1f) : Color.white;
                for (int i = 0; i < crosshair.Length; i++)
                    crosshair[i].color = color;
                if (opticalRing != null) opticalRing.color =
                    lockOn ? new Color(1f, .15f, .12f, .7f) :
                        new Color(1f, 1f, 1f, .56f);
            }
            for (int i = 0; i < crosshair.Length; i++)
                if (crosshair[i].gameObject.activeSelf != armed)
                    crosshair[i].gameObject.SetActive(armed);
        }

        private void OnDestroy()
        {
            if (health != null)
            {
                health.HealthChanged -= HealthChanged;
                health.Damaged -= Damaged;
            }
            if (instance == this) instance = null;
        }
    }
}
