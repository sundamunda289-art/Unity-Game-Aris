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
        // Avoid allocating a full hit array every frame in crowded forests.
        private readonly RaycastHit[] crowdedCameraObstacles = new RaycastHit[128];
        private AarisVehicleWorld world;
        private AarisThirdPersonPlayer footMotor;
        private CharacterController capsule;
        private AarisBattleActions actions;
        private AarisWeaponInventory inventory;
        private AarisWeaponLootWorld lootWorld;
        private AarisNearbyColliders nearbyColliders;
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
        // Hide DRIVE for overturned, still-moving or unsupported vehicles.
        // The final authority remains AarisVehicleMotor.TryTakeDriver.
        public bool CanEnter => nearbyCar != null && nearbyCar.IsSafeToExit &&
            !IsDriving && footMotor != null && footMotor.isActiveAndEnabled;
        public AarisVehicleMotor DrivenCar => car;

        public void Configure(AarisVehicleWorld vehicleWorld, AarisThirdPersonPlayer player)
        {
            world = vehicleWorld;
            footMotor = player;
            capsule = GetComponent<CharacterController>();
            actions = GetComponent<AarisBattleActions>();
            inventory = GetComponent<AarisWeaponInventory>();
            lootWorld = FindFirstObjectByType<AarisWeaponLootWorld>();
            nearbyColliders = FindFirstObjectByType<AarisNearbyColliders>();
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
                Ray sight = new Ray(eye, delta / distance);
                int hitCount = Physics.RaycastNonAlloc(
                    sight, entrySight, distance,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                RaycastHit[] sightHits = entrySight;
                if (hitCount >= entrySight.Length)
                {
                    // Unsorted NonAlloc results can fill with player/car
                    // colliders before including a distant wall. Retry the
                    // COMPLETE query only on an explicit saturated DRIVE tap,
                    // retaining the same obstruction/ownership safety checks.
                    sightHits = Physics.RaycastAll(sight, distance,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
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
            // The four AfterSceneLoad Boot methods have no guaranteed
            // ordering. The vehicle session may have been configured before
            // the combat, weapon inventory or streamed loot world existed.
            // Resolve once on successful entry, never once per driving frame.
            if (actions == null) actions = GetComponent<AarisBattleActions>();
            if (inventory == null) inventory = GetComponent<AarisWeaponInventory>();
            if (lootWorld == null) lootWorld = FindFirstObjectByType<AarisWeaponLootWorld>();
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

            // The equipped weapon clears ADS intent when disabled, but the
            // shared camera must also restore its normal rendered FOV now.
            // Otherwise a scoped lens can remain active during the car chase.
            footMotor.PrepareVehicleCameraHandoff();
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
            // A late-created collider streamer may not exist at Configure.
            // Resolve only on an explicit exit request, never each frame.
            if (nearbyColliders == null)
                nearbyColliders = FindFirstObjectByType<AarisNearbyColliders>();
            var t = car.transform;
            float seaLevel = -38.07552f;
            // Exit candidates are computed on demand, without allocating
            // a new array or touching any driving/render quality settings.
            for (int side = 0; side < 4; side++)
            {
                Vector3 option = t.position + (side == 0 ? t.right * 3.2f :
                    side == 1 ? -t.right * 3.2f :
                    side == 2 ? -t.forward * 3.5f : t.forward * 3.5f);
                if (Mathf.Abs(option.x) > 973f || Mathf.Abs(option.z) > 973f) continue;
                Ray floorRay = new Ray(option + Vector3.up * 2.5f, Vector3.down);
                int hits = Physics.RaycastNonAlloc(floorRay, exitGround, 5f,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                RaycastHit[] groundHits = exitGround;
                if (hits >= exitGround.Length)
                {
                    // A full NonAlloc buffer is unsorted and may omit the only
                    // valid street surface. Complete this rare EXIT-tap query
                    // before applying the original slope/height safety rules.
                    groundHits = Physics.RaycastAll(floorRay, 5f,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    hits = groundHits.Length;
                }
                float best = float.PositiveInfinity;
                Vector3 support = Vector3.zero;
                Collider floor = null;
                for (int i = 0; i < hits; i++)
                {
                    var hit = groundHits[i];
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
                // Physics overlap cannot detect disabled streamed house walls.
                // Before an immediate exit after a fast drive or teleport,
                // conservatively check the capsule's full bounding box in the
                // existing cached house grid, without waking distant physics.
                Vector3 capsuleCenter = (bottom + top) * .5f;
                Vector3 capsuleHalfBounds = new Vector3(r,
                    Mathf.Abs(top.y - bottom.y) * .5f + r, r);
                if (nearbyColliders != null &&
                    nearbyColliders.HasPotentialInactiveObstacle(
                        capsuleCenter, capsuleHalfBounds, Quaternion.identity))
                    continue;
                int overlaps = Physics.OverlapCapsuleNonAlloc(bottom, top, r,
                    exitOverlap, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                Collider[] occupiedColliders = exitOverlap;
                if (overlaps >= exitOverlap.Length)
                {
                    // Never accept a partial collider set as free space. The
                    // complete allocation is only on an explicit exit attempt,
                    // never in Update/LateUpdate or the driving physics loop.
                    occupiedColliders = Physics.OverlapCapsule(bottom, top, r,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    overlaps = occupiedColliders.Length;
                }
                bool safe = true;
                for (int i = 0; i < overlaps; i++)
                {
                    var obstacle = occupiedColliders[i];
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
            // Smooth the intended orbit first, then collision-test the camera's
            // actual rendered position. Clamping the goal before SmoothDamp
            // allowed interpolation to cross walls on sharp vehicle turns.
            // Keep exactly one bounded physics sweep per frame on low-RAM phones.
            Vector3 next = Vector3.SmoothDamp(
                followCamera.transform.position, requested, ref cameraVelocity,
                .12f, Mathf.Infinity, Mathf.Min(.05f, Time.deltaTime));
            Vector3 ray = next - start;
            float length = ray.magnitude;
            if (length > .001f)
            {
                Vector3 direction = ray / length;
                // SphereCast (single-hit) can hit our own body first and
                // incorrectly ignore a wall BEHIND it. Scan bounded hits
                // and choose the closest obstacle not owned by car/player.
                Ray sweep = new Ray(start, direction);
                int count = Physics.SphereCastNonAlloc(sweep,
                    .22f, cameraObstacles, length, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);
                RaycastHit[] hits = cameraObstacles;
                if (count >= cameraObstacles.Length)
                {
                    // NonAlloc hits are unordered and incomplete on saturation.
                    // Forcing the camera to 45 cm makes crowded forest driving
                    // impossible. Retry in scratch storage without allocations,
                    // and query all hits only if even that storage is saturated,
                    // then apply the SAME owned-car/player exclusion rules.
                    hits = crowdedCameraObstacles;
                    count = Physics.SphereCastNonAlloc(sweep, .22f,
                        crowdedCameraObstacles, length,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    if (count >= crowdedCameraObstacles.Length)
                    {
                        // Preserve correct closest-hit selection even when
                        // 128 nonalloc slots are insufficient.
                        hits = Physics.SphereCastAll(sweep, .22f, length,
                            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                        count = hits.Length;
                    }
                }
                float nearest = length;
                for (int i = 0; i < count; i++)
                {
                    var hit = hits[i];
                    if (hit.collider == null ||
                        hit.collider.transform.IsChildOf(vehicle) ||
                        hit.collider.transform.IsChildOf(transform))
                        continue;
                    nearest = Mathf.Min(nearest, hit.distance);
                }
                if (nearest < length)
                {
                    next = start + direction * Mathf.Max(.35f, nearest - .24f);
                    // Stop smoothing velocity from repeatedly pushing through
                    // the same obstacle after this collision correction.
                    cameraVelocity = Vector3.zero;
                }
            }
            followCamera.transform.position = next;
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
