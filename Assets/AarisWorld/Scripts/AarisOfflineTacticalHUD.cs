using System.Collections.Generic;
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

        [Header("Medical supplies (streamed offline loot)")]
        [SerializeField, Range(1, 5)] private int maxMedkits = 5;
        [SerializeField, Range(25f, 100f)] private float medicalHealing = 75f;
        private const int MaxMedicalVisuals = 8;
        private sealed class MedicalSite
        {
            public Vector3 position;
            public GameObject visual;
            public bool taken;
            public float retry;
        }
        private readonly List<MedicalSite> medicalSites = new List<MedicalSite>(80);
        private readonly Collider[] medicalHits = new Collider[20];
        private readonly RaycastHit[] medicalSightHits = new RaycastHit[24];
        private Terrain[] medicalTerrains;
        private AarisOfflineHealth medicalHealth;
        private AarisBattleActions medicalBattle;
        private CharacterController medicalController;
        private Text medicalText;
        private int medkits, shownMedkits = -1, shownSeconds = -1;
        private bool healing;
        private float healEnd, healthAtHealStart, nextMedicalScan;
        private static Material medcasePaint, medcrossPaint;
        private AarisThirdPersonPlayer motor;
        private AarisOfflineGlooWall gloo;
        private AarisGlooTouchButton wallTouch;
        private AarisOfflineDrop drop;
        private AarisVehicleSession vehicle;
        private Canvas canvas;
        private GraphicRaycaster raycaster;
        private RectTransform safe;
        private Text wallLabel, grenadeLabel;
        private float nextThrow;
        private float nextOptionalComponentRefresh;
        private int shownWalls = -1, shownGrenades = -1;
        private Rect previousSafeArea;
        private Vector2Int previousScreen;
        private bool initialized;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            EnsureForPlayer(FindFirstObjectByType<AarisThirdPersonPlayer>());
        }

        internal static void EnsureForPlayer(AarisThirdPersonPlayer player)
        {
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
            medicalHealth = GetComponent<AarisOfflineHealth>();
            medicalBattle = GetComponent<AarisBattleActions>();
            medicalController = GetComponent<CharacterController>();
            medicalTerrains = Terrain.activeTerrains;
            if (motor == null || gloo == null) { enabled = false; return; }
            LoadMedicalSites();
            Build();
            initialized = true;
        }

        // Stripped player prefabs can omit either optional component. Poll for
        // late additions at most once per second, never on every render frame.
        private void RefreshOptionalComponents(bool force = false)
        {
            if (!force && Time.unscaledTime < nextOptionalComponentRefresh) return;
            nextOptionalComponentRefresh = Time.unscaledTime + 1f;
            if (drop == null) drop = GetComponent<AarisOfflineDrop>();
            if (vehicle == null) vehicle = GetComponent<AarisVehicleSession>();
        }

        private void Update()
        {
            if (!initialized) return;
            RefreshOptionalComponents();
            bool onFoot = (drop == null || drop.HasLanded) &&
                (vehicle == null || !vehicle.IsDriving);
            if (canvas != null && canvas.enabled != onFoot)
            {
                canvas.enabled = onFoot;
                raycaster.enabled = onFoot;
            }
            if (!onFoot)
            {
                StopHealing();
                // Canvas.enabled=false does not invoke the touch button's OnDisable.
                // Clear its pointer ID so re-entering on-foot mode never locks WALL.
                if (wallTouch != null) wallTouch.CancelHold();
                else if (gloo.Aiming) gloo.CancelAim();
                return;
            }
            if (Input.GetKeyDown(KeyCode.Q)) ThrowFrag();
            if (Input.GetKeyDown(KeyCode.M)) StartHealing();
            UpdateHealing();
            StreamMedicalSites();
            if (healing)
            {
                int seconds = Mathf.CeilToInt(Mathf.Max(0f, healEnd - Time.time));
                if (seconds != shownSeconds)
                {
                    shownSeconds = seconds;
                    medicalText.text = "HEAL\n" + seconds + "s";
                }
            }
            else if (shownMedkits != medkits || shownSeconds >= 0)
            {
                shownMedkits = medkits;
                shownSeconds = -1;
                medicalText.text = "MED\n×" + medkits;
            }
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
            // An inventory at capacity must decline the pickup, rather than
            // falsely reporting success and consuming the source item.
            if (amount <= 0 || fragGrenades >= 10) return false;
            // Saturate BEFORE addition: a very large grant must never
            // overflow the integer counter and accidentally clear ammo.
            fragGrenades += Mathf.Min(amount, 10 - fragGrenades);
            return true;
        }

        public void ThrowFrag()
        {
            if (!enabled || fragGrenades <= 0 || motor == null || motor.AimCamera == null ||
                Time.time < nextThrow) return;
            // A throw is rare and irreversible: re-read late-added vehicle/drop
            // before consuming ammunition even inside the one-second poll window.
            RefreshOptionalComponents(true);
            if ((drop != null && !drop.HasLanded) ||
                (vehicle != null && vehicle.IsDriving)) return;
            Camera aim = motor.AimCamera;
            Vector3 forward = aim.transform.forward.normalized;
            Vector3 flat = Vector3.ProjectOnPlane(forward, Vector3.up).normalized;
            if (flat.sqrMagnitude < 0.1f) flat = transform.forward;
            Vector3 origin = transform.position + Vector3.up * 1.45f + flat * 0.85f;
            Vector3 velocity = forward * 16f + Vector3.up * 6f;
            AarisOfflineFragGrenade.Throw(origin, velocity, gameObject);
            AarisAudioDirector.GrenadeThrow(origin);
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
            raycaster = canvasObject.GetComponent<GraphicRaycaster>();
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
                new Vector2(-343f, 0f), Color.white,
                out GameObject wallButtonObject);
            wallTouch = wallButtonObject.AddComponent<AarisGlooTouchButton>();
            wallTouch.Bind(gloo);
            grenadeLabel = CreateAction("Frag Grenade", new Vector2(1f, 0.27f),
                new Vector2(-343f, 0f), new Color(0.94f, 0.86f, 0.66f, 1f),
                out GameObject grenadeButton);
            var button = grenadeButton.AddComponent<Button>();
            button.targetGraphic = grenadeButton.GetComponent<Image>();
            button.onClick.AddListener(ThrowFrag);
            medicalText = CreateAction("Medkit", new Vector2(1f, 0.45f),
                new Vector2(-255f, 0f), new Color(0.8f, 0.96f, 0.78f),
                out GameObject medButton);
            var healButton = medButton.AddComponent<Button>();
            healButton.targetGraphic = medButton.GetComponent<Image>();
            healButton.onClick.AddListener(StartHealing);
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

        private void LoadMedicalSites()
        {
            TextAsset json = Resources.Load<TextAsset>("AarisWeapons/loot_candidates");
            if (json == null) return;
            try
            {
                var doc = JsonUtility.FromJson<AarisLootDocument>(json.text);
                if (doc == null || doc.schema != 1 || doc.sizeMetres != 2000 ||
                    doc.candidates == null) return;
                for (int i = 0; i < doc.candidates.Length; i++)
                {
                    var c = doc.candidates[i];
                    if (c == null || c.kind != "open" || i % 5 != 0 ||
                        float.IsNaN(c.x) || float.IsNaN(c.z) ||
                        float.IsInfinity(c.x) || float.IsInfinity(c.z) ||
                        Mathf.Abs(c.x) > 955f || Mathf.Abs(c.z) > 955f) continue;
                    medicalSites.Add(new MedicalSite {
                        position = new Vector3(c.x, 0f, c.z) });
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("Aaris medkits: " + e.GetType().Name);
            }
        }

        private void StartHealing()
        {
            RefreshOptionalComponents(true);
            if (healing || medkits <= 0 || medicalHealth == null ||
                medicalHealth.IsDead || medicalHealth.CurrentHealth >=
                medicalHealth.MaximumHealth - 0.5f ||
                (drop != null && !drop.HasLanded) ||
                (vehicle != null && vehicle.IsDriving)) return;
            healing = true;
            healEnd = Time.time + 3f;
            healthAtHealStart = medicalHealth.CurrentHealth;
            shownSeconds = -1;
        }

        private void StopHealing()
        {
            if (!healing) return;
            healing = false;
            shownSeconds = shownMedkits = -1;
        }

        private void UpdateHealing()
        {
            if (!healing) return;
            Vector3 velocity = medicalController != null
                ? medicalController.velocity : Vector3.zero;
            if (medicalHealth == null || medicalHealth.IsDead ||
                medicalHealth.CurrentHealth + 0.001f < healthAtHealStart ||
                new Vector2(velocity.x, velocity.z).sqrMagnitude > 0.04f ||
                (medicalBattle != null && medicalBattle.FireHeld) ||
                (motor != null && motor.IsInAir))
            {
                StopHealing();
                return;
            }
            if (Time.time < healEnd) return;
            if (medkits > 0 && medicalHealth.CurrentHealth < medicalHealth.MaximumHealth)
            {
                medkits--;
                medicalHealth.RestoreHealth(medicalHealing);
            }
            StopHealing();
        }

        private void StreamMedicalSites()
        {
            if (Time.unscaledTime < nextMedicalScan ||
                medicalSites.Count == 0) return;
            nextMedicalScan = Time.unscaledTime + 0.65f;
            Vector3 playerPosition = transform.position;
            int live = 0;
            for (int i = 0; i < medicalSites.Count; i++)
            {
                var site = medicalSites[i];
                if (site.visual == null) continue;
                float dx = site.position.x - playerPosition.x;
                float dz = site.position.z - playerPosition.z;
                float distanceSq = dx * dx + dz * dz;
                if (distanceSq <= 2.15f * 2.15f && medkits < maxMedkits &&
                    Mathf.Abs(site.visual.transform.position.y - playerPosition.y) < 1.25f &&
                    CanCollectMedical(site))
                {
                    site.taken = true;
                    medkits++;
                    Destroy(site.visual);
                    site.visual = null;
                    continue;
                }
                if (distanceSq > 56f * 56f)
                {
                    Destroy(site.visual);
                    site.visual = null;
                }
                else live++;
            }
            if (live >= MaxMedicalVisuals) return;
            for (int i = 0; i < medicalSites.Count && live < MaxMedicalVisuals; i++)
            {
                var site = medicalSites[i];
                if (site.taken || site.visual != null ||
                    Time.unscaledTime < site.retry) continue;
                float dx = site.position.x - playerPosition.x;
                float dz = site.position.z - playerPosition.z;
                if (dx * dx + dz * dz > 42f * 42f) continue;
                if (TrySpawnMedicalCase(site)) live++;
                else site.retry = Time.unscaledTime + 5f;
            }
        }

        private bool CanCollectMedical(MedicalSite site)
        {
            // A medkit behind a real wall must not be collected through it.
            Vector3 from = transform.position + Vector3.up * 0.95f;
            Vector3 to = site.visual.transform.position + Vector3.up * 0.25f - from;
            float distance = to.magnitude;
            if (distance < 0.001f) return true;
            Ray ray = new Ray(from, to / distance);
            int count = Physics.RaycastNonAlloc(ray, medicalSightHits, distance,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            RaycastHit[] hits = medicalSightHits;
            if (count >= hits.Length)
            {
                hits = Physics.RaycastAll(ray, distance,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                count = hits.Length;
            }
            for (int i = 0; i < count; i++)
            {
                Collider c = hits[i].collider;
                if (c != null && !c.transform.IsChildOf(transform)) return false;
            }
            return true;
        }

        private bool TrySpawnMedicalCase(MedicalSite site)
        {
            if (medicalTerrains == null || medicalTerrains.Length == 0)
                medicalTerrains = Terrain.activeTerrains;
            if (medicalTerrains == null) return false;
            Vector3 p = site.position;
            float ground = float.NegativeInfinity;
            bool supported = false;
            for (int i = 0; i < medicalTerrains.Length; i++)
            {
                Terrain tile = medicalTerrains[i];
                if (tile == null || tile.terrainData == null) continue;
                Vector3 origin = tile.transform.position;
                Vector3 size = tile.terrainData.size;
                if (p.x < origin.x || p.x > origin.x + size.x ||
                    p.z < origin.z || p.z > origin.z + size.z) continue;
                float u = (p.x - origin.x) / size.x;
                float v = (p.z - origin.z) / size.z;
                if (tile.terrainData.GetInterpolatedNormal(u, v).y < 0.87f)
                    return false;
                ground = tile.SampleHeight(p);
                supported = true;
                break;
            }
            if (!supported || ground <= -37.35f) return false;
            Vector3 spot = new Vector3(p.x, ground + 0.08f, p.z);
            int count = Physics.OverlapBoxNonAlloc(spot + Vector3.up * 0.26f,
                new Vector3(0.31f, 0.16f, 0.24f), medicalHits,
                Quaternion.identity, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);
            if (count >= medicalHits.Length) return false;
            for (int i = 0; i < count; i++)
            {
                Collider c = medicalHits[i];
                if (c != null && !(c is TerrainCollider) &&
                    !c.transform.IsChildOf(transform)) return false;
            }
            var root = new GameObject("Aaris Streamed Medkit");
            root.transform.position = spot;
            Material shell = MedicalPaint(ref medcasePaint,
                new Color(0.24f, 0.29f, 0.25f), "Aaris Medkit Case");
            Material red = MedicalPaint(ref medcrossPaint,
                new Color(0.88f, 0.18f, 0.13f), "Aaris Medkit Cross");
            MedicalCube(root.transform, "Case", new Vector3(0f, 0.11f, 0f),
                new Vector3(0.52f, 0.21f, 0.38f), shell);
            MedicalCube(root.transform, "Cross A", new Vector3(0f, 0.219f, 0f),
                new Vector3(0.075f, 0.008f, 0.24f), red);
            MedicalCube(root.transform, "Cross B", new Vector3(0f, 0.220f, 0f),
                new Vector3(0.24f, 0.008f, 0.075f), red);
            site.visual = root;
            return true;
        }

        private static Material MedicalPaint(ref Material material, Color color, string name)
        {
            if (material != null) return material;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) return null;
            material = new Material(shader) { name = name };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            else material.color = color;
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0.13f);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.32f);
            material.enableInstancing = true;
            return material;
        }

        private static void MedicalCube(Transform parent, string name,
            Vector3 position, Vector3 scale, Material material)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localScale = scale;
            var collider = part.GetComponent<Collider>();
            if (collider != null) { collider.enabled = false; Destroy(collider); }
            var renderer = part.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        private void LateUpdate() => RefreshSafeArea();

        private void OnDisable()
        {
            StopHealing();
            if (wallTouch != null) wallTouch.CancelHold();
        }

        private void OnDestroy()
        {
            foreach (var site in medicalSites)
                if (site.visual != null) Destroy(site.visual);
            if (canvas != null) Destroy(canvas.gameObject);
        }
    }

    /// <summary>Press-and-hold preview; release to deploy. Pointer cancel never consumes inventory.</summary>
    public sealed class AarisGlooTouchButton : MonoBehaviour,
        IPointerDownHandler, IPointerUpHandler, ICancelHandler
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
            if (wall == null) return;
            for (int i = 0; i < Input.touchCount; i++)
            {
                Touch touch = Input.GetTouch(i);
                if (touch.fingerId != data.pointerId || touch.phase != TouchPhase.Canceled) continue;
                wall.CancelAim();
                return;
            }
            wall.ReleaseAndDeploy();
        }
        public void OnCancel(BaseEventData data) => CancelHold();

        public void CancelHold()
        {
            // Canvas can be hidden throughout a drive or skydive. Do not
            // redundantly toggle the inactive Gloo preview every Update.
            // A real pointer or active aim is still cancelled immediately.
            if (holding == int.MinValue && (wall == null || !wall.Aiming))
                return;
            holding = int.MinValue;
            if (wall != null) wall.CancelAim();
        }
        private void OnDisable() => CancelHold();
        private void OnApplicationPause(bool paused) { if (paused) CancelHold(); }
        private void OnApplicationFocus(bool focused) { if (!focused) CancelHold(); }
    }
}
