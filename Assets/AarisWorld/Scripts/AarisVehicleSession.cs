using UnityEngine;
using UnityEngine.UI;

namespace Aaris.MapReview
{
    /// <summary>
    /// Explicit mode transition between the existing on-foot player motor
    /// and the vehicle motor. Exactly one is allowed to own movement/camera.
    /// Exit performs a real collision/ground check before restoring the
    /// CharacterController; otherwise it safely stays in the car.
    /// </summary>
    // Camera follows interpolated physics AFTER gameplay Update.
    [DisallowMultipleComponent, DefaultExecutionOrder(250)]
    public sealed class AarisVehicleSession : MonoBehaviour
    {
        private readonly Collider[] exitOverlap = new Collider[32];
        private readonly RaycastHit[] exitGround = new RaycastHit[32];
        private readonly RaycastHit[] entrySight = new RaycastHit[24];
        private readonly RaycastHit[] cameraObstacles = new RaycastHit[32];
        private AarisVehicleWorld world;
        private AarisThirdPersonPlayer footMotor;
        private CharacterController capsule;
        private AarisBattleActions actions;
        private AarisWeaponInventory inventory;
        private AarisWeaponLootWorld lootWorld;
        private bool previousBattleActive, previousInventoryActive, previousLootActive;
        private AarisVehicleMotor car;
        private Camera followCamera;
        private Transform originalParent;
        private Renderer[] playerRenderers;
        private bool[] wasRendererEnabled;
        private Canvas[] footCanvases;
        private GraphicRaycaster[] footRaycasters;
        private float nextVehicleSearch;
        private float touchMove, touchSteer;
        private AarisVehicleMotor nearbyCar;
        private Vector3 cameraVelocity;
        public bool IsDriving => car != null;
        public bool CanEnter => nearbyCar != null && !IsDriving &&
            footMotor != null && footMotor.isActiveAndEnabled;
        public AarisVehicleMotor DrivenCar => car;

        public void Configure(AarisVehicleWorld vehicleWorld, AarisThirdPersonPlayer player)
        {
            world = vehicleWorld;
            footMotor = player;
            capsule = GetComponent<CharacterController>();
            actions = GetComponent<AarisBattleActions>();
            inventory = GetComponent<AarisWeaponInventory>();
            lootWorld = FindFirstObjectByType<AarisWeaponLootWorld>();
            followCamera = player.AimCamera;
            originalParent = transform.parent;
        }

        private void Update()
        {
            if (world == null || footMotor == null) return;
            if (!IsDriving && !footMotor.isActiveAndEnabled)
            {
                nearbyCar = null;
                return;
            }
            if (car == null && Time.unscaledTime >= nextVehicleSearch)
            {
                nextVehicleSearch = Time.unscaledTime + .25f;
                nearbyCar = world.NearestAvailable(transform.position, 4.0f);
            }
            if (Input.GetKeyDown(KeyCode.F) && !IsDriving) TryEnterOrExit();
            if (Input.GetKeyDown(KeyCode.X) && IsDriving) TryEnterOrExit();
            if (car != null)
            {
                // Four arrow keys / WASD match the four touchscreen arrows.
                // Touch state remains held until its own PointerUp/Cancel.
                float keyboardMove = (Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.W) ? 1f : 0f)
                    - (Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.S) ? 1f : 0f);
                float keyboardSteer = (Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.D) ? 1f : 0f)
                    - (Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.A) ? 1f : 0f);
                car.SetInput(Mathf.Abs(keyboardMove) > .05f ? keyboardMove : touchMove,
                    Mathf.Abs(keyboardSteer) > .05f ? keyboardSteer : touchSteer);
            }
        }

        private void LateUpdate()
        {
            // Update reads inputs; LateUpdate tracks Rigidbody interpolation.
            // Following the car in Update caused visible camera jitter.
            if (car != null && followCamera != null) FollowCarCamera();
        }

        public void SetSteering(float move, float steer)
        {
            // Public touch API may be called by UI or automation; never keep
            // non-finite values in a held virtual throttle/steering state.
            if (float.IsNaN(move) || float.IsInfinity(move)) move = 0f;
            if (float.IsNaN(steer) || float.IsInfinity(steer)) steer = 0f;
            touchMove = Mathf.Clamp(move, -1f, 1f);
            touchSteer = Mathf.Clamp(steer, -1f, 1f);
            if (car != null) car.SetInput(touchMove, touchSteer);
        }

        public bool TryEnterOrExit() => IsDriving ? TryExit() : TryEnter();

        private bool TryEnter()
        {
            if (!CanEnter || capsule == null || followCamera == null) return false;
            if ((nearbyCar.transform.position - transform.position).sqrMagnitude > 16f)
                return false;
            // The DRIVE action cannot reach through a house/rock wall.
            Vector3 eye = transform.position + Vector3.up * 1.1f;
            Vector3 target = nearbyCar.transform.position + Vector3.up * 1.1f;
            Vector3 delta = target - eye;
            float distance = delta.magnitude;
            if (distance > .01f)
            {
                int hitCount = Physics.RaycastNonAlloc(
                    new Ray(eye, delta / distance), entrySight, distance,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                RaycastHit[] sightHits = entrySight;
                if (hitCount >= entrySight.Length)
                {
                    // Do not reject an enter attempt merely because nearby
                    // modules filled the buffer; still test every real wall.
                    sightHits = Physics.RaycastAll(new Ray(eye, delta / distance),
                        distance, Physics.DefaultRaycastLayers,
                        QueryTriggerInteraction.Ignore);
                    hitCount = sightHits.Length;
                }
                for (int i = 0; i < hitCount; i++)
                {
                    var hit = sightHits[i];
                    if (hit.collider == null ||
                        hit.collider.transform.IsChildOf(transform) ||
                        hit.collider.transform.IsChildOf(nearbyCar.transform)) continue;
                    return false;
                }
            }
            if (!nearbyCar.TryTakeDriver(this)) return false;
            car = nearbyCar;
            nearbyCar = null;
            touchMove = touchSteer = 0f;
            if (actions != null) actions.SetFireHeld(false);
            // Pause the other on-foot input authorities, not merely their
            // canvases: E/G/V/R and loot auto-pickup must not work in a car.
            previousBattleActive = actions != null && actions.enabled;
            previousInventoryActive = inventory != null && inventory.enabled;
            previousLootActive = lootWorld != null && lootWorld.enabled;
            if (actions != null) actions.enabled = false;
            if (inventory != null) inventory.enabled = false;
            if (lootWorld != null) lootWorld.enabled = false;
            footMotor.SetJumpHeld(false);
            footMotor.CancelAutoSprint();

            var battleHud = FindFirstObjectByType<AarisBattleHUD>();
            if (battleHud != null) battleHud.CancelVehicleInputs();
            SetFootHudVisible(false);

            // Do not leave a walking physics motor enabled inside the car.
            footMotor.enabled = false;
            capsule.enabled = false;
            playerRenderers = GetComponentsInChildren<Renderer>(true);
            wasRendererEnabled = new bool[playerRenderers.Length];
            for (int i = 0; i < playerRenderers.Length; i++)
            {
                wasRendererEnabled[i] = playerRenderers[i].enabled;
                playerRenderers[i].enabled = false;
            }
            transform.SetParent(car.transform, false);
            transform.localPosition = new Vector3(-.42f, .64f, .1f);
            transform.localRotation = Quaternion.identity;
            cameraVelocity = Vector3.zero;
            Debug.Log("AARIS_VEHICLE_ENTER " + car.VehicleId);
            return true;
        }

        private void SetFootHudVisible(bool show)
        {
            // Toggle only the two existing on-foot canvases and raycasters;
            // do not destroy their EventSystem, touch state or gun data.
            if (footCanvases == null)
            {
                var battle = FindFirstObjectByType<AarisBattleHUD>();
                var weapons = FindFirstObjectByType<AarisWeaponHUD>();
                var first = battle != null ? battle.GetComponentInChildren<Canvas>(true) : null;
                var second = weapons != null ? weapons.GetComponentInChildren<Canvas>(true) : null;
                footCanvases = new[] { first, second };
                footRaycasters = new[]
                {
                    first != null ? first.GetComponent<GraphicRaycaster>() : null,
                    second != null ? second.GetComponent<GraphicRaycaster>() : null
                };
            }
            foreach (var canvas in footCanvases)
                if (canvas != null) canvas.enabled = show;
            foreach (var raycaster in footRaycasters)
                if (raycaster != null) raycaster.enabled = show;
        }

        private bool TryFindExit(out Vector3 position)
        {
            position = default(Vector3);
            if (car == null) return false;
            var t = car.transform;
            float seaLevel = -38.07552f;
            // A roll/pitch of 90-180 degrees must not send exit candidates
            // into the air or underground. Search four horizontal directions.
            Vector3 flatForward = Vector3.ProjectOnPlane(t.forward, Vector3.up);
            if (flatForward.sqrMagnitude < .01f)
                flatForward = Vector3.ProjectOnPlane(t.right, Vector3.up);
            if (flatForward.sqrMagnitude < .01f)
                flatForward = Quaternion.Euler(0f, t.eulerAngles.y, 0f) * Vector3.forward;
            flatForward.Normalize();
            Vector3 flatRight = Vector3.Cross(Vector3.up, flatForward);
            for (int side = 0; side < 4; side++)
            {
                Vector3 option = t.position + (side == 0 ? flatRight * 3.2f :
                    side == 1 ? -flatRight * 3.2f :
                    side == 2 ? -flatForward * 3.5f : flatForward * 3.5f);
                if (Mathf.Abs(option.x) > 973f || Mathf.Abs(option.z) > 973f) continue;
                int hits = Physics.RaycastNonAlloc(
                    new Ray(option + Vector3.up * 2.5f, Vector3.down), exitGround, 5f,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                RaycastHit[] supports = exitGround;
                if (hits >= exitGround.Length)
                {
                    supports = Physics.RaycastAll(
                        new Ray(option + Vector3.up * 2.5f, Vector3.down),
                        5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    hits = supports.Length;
                }
                float best = float.PositiveInfinity;
                Vector3 support = Vector3.zero;
                Collider floor = null;
                for (int i = 0; i < hits; i++)
                {
                    var hit = supports[i];
                    if (hit.collider == null || hit.collider.transform.IsChildOf(t) ||
                        hit.collider.transform.IsChildOf(transform) ||
                        hit.normal.y < .707f || hit.point.y < seaLevel + .35f ||
                        // A roof/bridge above the actual street surface
                        // is not a safe car exit landing platform.
                        Mathf.Abs(hit.point.y - (option.y - .71f)) > 1.5f ||
                        hit.distance >= best) continue;
                    best = hit.distance;
                    support = hit.point;
                    floor = hit.collider;
                }
                if (floor == null) continue;
                float r = Mathf.Max(.12f, capsule.radius - capsule.skinWidth);
                Vector3 bottom = support + Vector3.up * (r + .1f);
                Vector3 top = support + Vector3.up * (capsule.height - r + .1f);
                int overlaps = Physics.OverlapCapsuleNonAlloc(bottom, top, r,
                    exitOverlap, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                Collider[] blockers = exitOverlap;
                if (overlaps >= exitOverlap.Length)
                {
                    blockers = Physics.OverlapCapsule(bottom, top, r,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    overlaps = blockers.Length;
                }
                bool safe = true;
                for (int i = 0; i < overlaps; i++)
                {
                    var obstacle = blockers[i];
                    if (obstacle == null || obstacle == floor ||
                        obstacle.transform.IsChildOf(transform)) continue;
                    safe = false;
                    break;
                }
                if (!safe) continue;
                position = support + Vector3.up * .08f;
                return true;
            }
            return false;
        }

        private bool TryExit()
        {
            if (car == null || !car.IsSafeToExit ||
                !TryFindExit(out Vector3 safeExit)) return false;
            AarisVehicleMotor previous = car;
            previous.ReleaseDriver(this);
            car = null;
            touchMove = touchSteer = 0f;
            transform.SetParent(originalParent, true);
            transform.SetPositionAndRotation(safeExit,
                Quaternion.Euler(0f, previous.transform.eulerAngles.y, 0f));
            if (playerRenderers != null)
                for (int i = 0; i < playerRenderers.Length; i++)
                    if (playerRenderers[i] != null)
                        playerRenderers[i].enabled = wasRendererEnabled[i];
            var nearby = FindFirstObjectByType<AarisNearbyColliders>();
            if (nearby != null) nearby.RefreshNow();
            capsule.enabled = true;
            footMotor.enabled = true;
            if (actions != null) actions.enabled = previousBattleActive;
            if (inventory != null) inventory.enabled = previousInventoryActive;
            if (lootWorld != null) lootWorld.enabled = previousLootActive;
            footMotor.ResetCameraAfterVehicle();
            SetFootHudVisible(true);
            nextVehicleSearch = 0f;
            Debug.Log("AARIS_VEHICLE_EXIT " + previous.VehicleId);
            return true;
        }

        private void FollowCarCamera()
        {
            if (car == null || followCamera == null) return;
            var vehicle = car.transform;
            Vector3 aim = vehicle.position + Vector3.up * 1.65f;
            Vector3 start = aim + Vector3.up * .85f;
            Vector3 requested = vehicle.position - vehicle.forward * 6.7f +
                Vector3.up * 3.1f;
            Vector3 ray = requested - start;
            float length = ray.magnitude;
            Vector3 goal = requested;
            if (length > .001f)
            {
                Vector3 direction = ray / length;
                // SphereCast (single-hit) can hit our own body first and
                // incorrectly ignore a wall BEHIND it. Scan bounded hits
                // and choose the closest obstacle not owned by car/player.
                int count = Physics.SphereCastNonAlloc(new Ray(start, direction),
                    .22f, cameraObstacles, length, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);
                float nearest = length;
                RaycastHit[] sight = cameraObstacles;
                if (count >= cameraObstacles.Length)
                {
                    // Include all barriers rather than forcing a camera jump
                    // to 45 cm when a distant dense building fills the buffer.
                    sight = Physics.SphereCastAll(new Ray(start, direction), .22f,
                        length, Physics.DefaultRaycastLayers,
                        QueryTriggerInteraction.Ignore);
                    count = sight.Length;
                }
                for (int i = 0; i < count; i++)
                {
                    var hit = sight[i];
                    if (hit.collider == null ||
                        hit.collider.transform.IsChildOf(vehicle) ||
                        hit.collider.transform.IsChildOf(transform))
                        continue;
                    nearest = Mathf.Min(nearest, hit.distance);
                }
                if (nearest < length)
                    goal = start + direction * Mathf.Max(.35f, nearest - .24f);
            }
            followCamera.transform.position = Vector3.SmoothDamp(
                followCamera.transform.position, goal, ref cameraVelocity,
                .12f, Mathf.Infinity, Mathf.Min(.05f, Time.deltaTime));
            Vector3 look = aim - followCamera.transform.position;
            if (look.sqrMagnitude > .001f)
                followCamera.transform.rotation = Quaternion.LookRotation(look);
            followCamera.fieldOfView = Mathf.Lerp(followCamera.fieldOfView,
                65f, 1f - Mathf.Exp(-5f * Time.deltaTime));
        }

        private void OnDisable()
        {
            if (car != null) car.SetInput(0f, 0f);
        }
    }
}
