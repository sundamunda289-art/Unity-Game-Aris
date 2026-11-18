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
        private readonly RaycastHit[] vehicleAccessHits = new RaycastHit[24];
        private readonly RaycastHit[] cameraObstacles = new RaycastHit[32];
        // Avoid allocating a full hit array every frame in crowded forests.
        private readonly RaycastHit[] crowdedCameraObstacles = new RaycastHit[128];
        private AarisVehicleWorld world;
        private AarisThirdPersonPlayer footMotor;
        private AarisCharacterVisual characterVisual;
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
        private bool[] previousFootCanvasEnabled;
        private bool[] previousFootRaycasterEnabled;
        private bool footHudHidden;
        private float nextVehicleSearch;
        private float touchMove, touchSteer;
        private AarisVehicleMotor nearbyCar;
        private Vector3 cameraVelocity;
        // Car-to-car camera framing is measured ONCE at successful entry.
        // The fitted BoxCollider already agrees with the visible authored
        // body; no renderer/bounds queries in the 60/120 FPS camera path.
        private float chaseBodyLength = 4.20f;
        private float chaseAimHeight = 1.28f;
        // Cached horizontal chase heading survives steep pitch/airborne flips.
        // On a hill, following the car's full 3D forward vector would shove
        // the camera through the road by chaseDistance * sin(pitch).
        private Vector3 lastChaseHeading = Vector3.forward;
        public bool IsDriving => car != null;
        // Hide DRIVE for overturned, still-moving or unsupported vehicles.
        // The final authority remains AarisVehicleMotor.TryTakeDriver.
        public bool CanEnter => nearbyCar != null &&
            nearbyCar.isActiveAndEnabled && nearbyCar.IsSafeToExit &&
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
            // EventSystem/button callbacks can arrive after this component
            // was disabled. Never hand fresh drive input to a suspended car.
            if (!isActiveAndEnabled || car == null) return;
            // Public touch API may be called by UI or automation; never keep
            // non-finite values in a held virtual throttle/steering state.
            if (float.IsNaN(move) || float.IsInfinity(move)) move = 0f;
            if (float.IsNaN(steer) || float.IsInfinity(steer)) steer = 0f;
            touchMove = Mathf.Clamp(move, -1f, 1f);
            touchSteer = Mathf.Clamp(steer, -1f, 1f);
            car.SetInput(touchMove, touchSteer);
        }

        public bool TryEnterOrExit()
        {
            // A lingering DRIVE/EXIT click must not mutate movement ownership
            // while the authoritative session is disabled.
            if (!isActiveAndEnabled) return false;
            return IsDriving ? TryExit() : TryEnter();
        }

        private bool TryEnter()
        {
            if (!CanEnter || capsule == null || followCamera == null) return false;
            if ((nearbyCar.transform.position - transform.position).sqrMagnitude > 16f)
                return false;
            // A sudden sprint/teleport can precede the streamed wall's next
            // 1-second refresh. Synchronize only on a deliberate DRIVE tap.
            if (nearbyColliders == null)
                nearbyColliders = FindFirstObjectByType<AarisNearbyColliders>();
            if (nearbyColliders != null) nearbyColliders.RefreshNow();
            // The DRIVE action cannot reach through a house/rock wall.
            Vector3 eye = transform.position + Vector3.up * 1.1f;
            Vector3 target = nearbyCar.transform.position + Vector3.up * 1.1f;
            if (!HasClearVehicleAccess(eye, target, nearbyCar.transform))
            {
                // A nearer car behind a wall must not hide another accessible
                // parked vehicle. This scene query runs on a failed tap ONLY.
                nearbyCar = FindReachableCarOnTap();
                if (nearbyCar == null) return false;
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
            // Use the physical bounds that were already fitted to the real
            // chassis and authored tyres at spawn, not a sedan-size camera.
            // Once per entry avoids a collider lookup every render frame.
            BoxCollider shell = car.GetComponent<BoxCollider>();
            if (shell != null &&
                shell.size.y > .45f && shell.size.z > 2.0f &&
                !float.IsNaN(shell.size.sqrMagnitude) &&
                !float.IsInfinity(shell.size.sqrMagnitude))
            {
                chaseBodyLength = Mathf.Clamp(shell.size.z, 3.0f, 5.3f);
                float roof = shell.center.y + shell.size.y * .5f;
                chaseAimHeight = Mathf.Clamp(
                    shell.center.y + shell.size.y * .62f,
                    .68f, Mathf.Max(.78f, roof + .12f));
                chaseAimHeight = Mathf.Clamp(chaseAimHeight, .68f, 1.65f);
            }
            else
            {
                chaseBodyLength = 4.20f;
                chaseAimHeight = 1.28f;
            }
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
            if (characterVisual == null) characterVisual = GetComponent<AarisCharacterVisual>();
            if (characterVisual != null) characterVisual.SetPresentationPaused(true);
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
            // Never reuse a previous car's heading when boarding this one.
            Vector3 entryHeading = Vector3.ProjectOnPlane(
                car.transform.forward, Vector3.up);
            if (entryHeading.sqrMagnitude < .04f)
                entryHeading = Vector3.ProjectOnPlane(
                    transform.forward, Vector3.up);
            lastChaseHeading = entryHeading.sqrMagnitude >= .04f
                ? entryHeading.normalized : Vector3.forward;
            Debug.Log("AARIS_VEHICLE_ENTER " + car.VehicleId);
            return true;
        }

        private AarisVehicleMotor FindReachableCarOnTap()
        {
            AarisVehicleMotor nearest = null;
            float nearestSquared = 4f * 4f;
            Vector3 origin = transform.position;
            Vector3 eye = origin + Vector3.up * 1.1f;
            // This allocation-free-in-normal-Update fallback only scans
            // live vehicle motors if the primary DRIVE ray is blocked.
            foreach (AarisVehicleMotor candidate in
                FindObjectsByType<AarisVehicleMotor>(FindObjectsSortMode.None))
            {
                if (candidate == null || !candidate.isActiveAndEnabled ||
                    candidate.IsOccupied) continue;
                float d2 = (candidate.transform.position - origin).sqrMagnitude;
                if (d2 >= nearestSquared || !candidate.IsSafeToExit) continue;
                Vector3 target = candidate.transform.position + Vector3.up * 1.1f;
                if (!HasClearVehicleAccess(eye, target, candidate.transform)) continue;
                nearest = candidate;
                nearestSquared = d2;
            }
            return nearest;
        }

        private bool HasClearVehicleAccess(Vector3 start, Vector3 end, Transform vehicle)
        {
            Vector3 delta = end - start;
            float distance = delta.magnitude;
            if (distance <= .01f) return true;

            Ray path = new Ray(start, delta / distance);
            int count = Physics.RaycastNonAlloc(path, vehicleAccessHits, distance,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            RaycastHit[] hits = vehicleAccessHits;
            // A full buffer can omit a solid wall behind the owned colliders.
            // Complete the query only on a saturated DRIVE or EXIT request.
            if (count >= vehicleAccessHits.Length)
            {
                hits = Physics.RaycastAll(path, distance,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                count = hits.Length;
            }
            for (int i = 0; i < count; i++)
            {
                Collider collider = hits[i].collider;
                if (collider == null || collider.transform.IsChildOf(transform) ||
                    collider.transform.IsChildOf(vehicle)) continue;
                return false;
            }
            return true;
        }

        private void SetFootHudVisible(bool show)
        {
            // A second hide must not overwrite the saved authored state.
            if (footHudHidden == !show) return;
            // Resolve the existing controls once, with no per-frame allocations.
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
                previousFootCanvasEnabled = new bool[footCanvases.Length];
                previousFootRaycasterEnabled = new bool[footRaycasters.Length];
            }
            for (int i = 0; i < footCanvases.Length; i++)
            {
                Canvas canvas = footCanvases[i];
                if (!show)
                {
                    previousFootCanvasEnabled[i] = canvas != null && canvas.enabled;
                    if (canvas != null) canvas.enabled = false;
                }
                else if (canvas != null)
                    canvas.enabled = previousFootCanvasEnabled[i];
            }
            for (int i = 0; i < footRaycasters.Length; i++)
            {
                GraphicRaycaster raycaster = footRaycasters[i];
                if (!show)
                {
                    previousFootRaycasterEnabled[i] = raycaster != null && raycaster.enabled;
                    if (raycaster != null) raycaster.enabled = false;
                }
                else if (raycaster != null)
                    raycaster.enabled = previousFootRaycasterEnabled[i];
            }
            footHudHidden = !show;
        }

        private bool TryFindExit(out Vector3 position)
        {
            position = default(Vector3);
            if (car == null) return false;
            // A late-created collider streamer may not exist at Configure.
            // Resolve only on an explicit exit request, never each frame.
            if (nearbyColliders == null)
                nearbyColliders = FindFirstObjectByType<AarisNearbyColliders>();
            // Refresh once on an exit request so a just-streamed road or
            // house wall is available to both the route and landing checks.
            if (nearbyColliders != null) nearbyColliders.RefreshNow();
            var t = car.transform;
            float seaLevel = -38.07552f;
            // Exit candidates are computed on demand, without allocating
            // a new array or touching any driving/render quality settings.
            // A rolled chassis's local right axis can point at the sky: using
            // it directly would place candidate ground rays metres above or
            // below the actual road and permanently trap an overturned driver.
            Vector3 flatForward = Vector3.ProjectOnPlane(t.forward, Vector3.up);
            if (flatForward.sqrMagnitude < .04f)
            {
                // If the nose points nearly vertical, derive a stable ground
                // heading from the other chassis axis, not a tiny vector.
                Vector3 flatRight = Vector3.ProjectOnPlane(t.right, Vector3.up);
                flatForward = flatRight.sqrMagnitude > .0001f
                    ? Vector3.Cross(flatRight, Vector3.up) : Vector3.forward;
            }
            flatForward.Normalize();
            Vector3 flatRightAxis = Vector3.Cross(Vector3.up, flatForward);
            for (int side = 0; side < 4; side++)
            {
                Vector3 option = t.position + (side == 0 ? flatRightAxis * 3.2f :
                    side == 1 ? -flatRightAxis * 3.2f :
                    side == 2 ? -flatForward * 3.5f : flatForward * 3.5f);
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
                // A clear landing spot can still be on the far side of a
                // wall. Check the waist-height route out of the car, then
                // try the remaining sides if this path is blocked.
                if (!HasClearVehicleAccess(t.position + Vector3.up * .35f,
                        support + Vector3.up * .85f, t)) continue;
                position = support + Vector3.up * .08f;
                return true;
            }
            return false;
        }

        private bool TryExit()
        {
            // Upright cars keep the existing safety gate. A stopped rollover
            // can attempt a rescue exit, but TryFindExit still rejects an
            // unsafe floor, occupied space or route through a wall.
            if (car == null ||
                (!car.IsSafeToExit && !car.CanEvacuateStoppedVehicle) ||
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
            if (characterVisual != null) characterVisual.SetPresentationPaused(false);
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
            float speed01 = Mathf.Clamp01(car.TotalSpeedMetresPerSecond /
                Mathf.Max(1f, car.TopSpeedMetresPerSecond));
            // A fixed chase rig makes a 90 km/h car read like a slow toy.
            // Open the framing gradually with real Rigidbody speed only;
            // collision handling below still owns the final camera position.
            // Long vans/pickups need more framing room, while a 3.4 m
            // buggy should not look distant and tiny. All sizes are cached
            // from the real fitted shell once at vehicle entry.
            float lengthFraming = Mathf.Clamp(
                (chaseBodyLength - 4.20f) * .26f, -.24f, .36f);
            float chaseDistance = Mathf.Lerp(6.25f, 7.40f, speed01) +
                lengthFraming;
            // Aim near the actual roof/upper cabin. The old 1.58-1.74 m
            // fixed target looked ABOVE the 1.15 m coupe, shrinking it.
            // The bounded offset remains world-up on slopes and rollovers.
            float heightFraming = Mathf.Clamp(
                (chaseAimHeight - 1.30f) * .25f, -.15f, .10f);
            float chaseHeight = Mathf.Lerp(2.75f, 3.05f, speed01) +
                heightFraming;
            Vector3 aim = vehicle.position + Vector3.up *
                (chaseAimHeight + .12f * speed01);
            // Small velocity-based horizon preview makes fast turns easier
            // to read. The existing single collision sweep and smoothing
            // still own the final camera position; never teleport the rig.
            Vector3 planarTravel = Vector3.ProjectOnPlane(
                car.DrivingVelocityWorld, Vector3.up);
            aim += Vector3.ClampMagnitude(planarTravel * .035f, .75f);
            Vector3 start = aim + Vector3.up * .85f;
            // Stable world-up chase: preserve yaw, never inherit the car's
            // uphill pitch or airborne nose-up attitude as camera altitude.
            // A nearly vertical chassis has no reliable projected forward;
            // keep the last valid horizontal heading instead of snapping it.
            Vector3 horizontalForward = Vector3.ProjectOnPlane(
                vehicle.forward, Vector3.up);
            if (horizontalForward.sqrMagnitude >= .04f)
                lastChaseHeading = horizontalForward.normalized;
            Vector3 requested = vehicle.position -
                lastChaseHeading * chaseDistance + Vector3.up * chaseHeight;
            // Smooth the intended orbit first, then collision-test the camera's
            // actual rendered position. Clamping the goal before SmoothDamp
            // allowed interpolation to cross walls on sharp vehicle turns.
            // Retain real elapsed time at 10-15 FPS instead of making the
            // chase camera move at half speed. Bound only exceptional hitches;
            // position, rotation and lens use one consistent frame budget.
            // Keep exactly one bounded physics sweep per frame on low-RAM phones.
            float cameraFrameDt = Mathf.Clamp(Time.deltaTime, 0f, .15f);
            Vector3 next = Vector3.SmoothDamp(
                followCamera.transform.position, requested, ref cameraVelocity,
                .12f, Mathf.Infinity, cameraFrameDt);
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
                    // A minimum camera distance can move the camera beyond a
                    // close collision. Never advance past the reported contact.
                    next = start + direction * Mathf.Max(0f, nearest - .24f);
                    // Stop smoothing velocity from repeatedly pushing through
                    // the same obstacle after this collision correction.
                    cameraVelocity = Vector3.zero;
                }
            }
            followCamera.transform.position = next;
            Vector3 look = aim - followCamera.transform.position;
            if (look.sqrMagnitude > .001f)
            {
                Quaternion targetRotation = Quaternion.LookRotation(look);
                float rotationAlpha = 1f - Mathf.Exp(-10f * cameraFrameDt);
                followCamera.transform.rotation = Quaternion.Slerp(
                    followCamera.transform.rotation, targetRotation, rotationAlpha);
            }
            float targetFov = Mathf.Lerp(62f, 70f,
                Mathf.SmoothStep(0f, 1f, speed01));
            float currentFov = followCamera.fieldOfView;
            float fovError = Mathf.Abs(targetFov - currentFov);
            if (fovError > 0.001f)
            {
                float fovDelta = cameraFrameDt;
                followCamera.fieldOfView = fovError <= 0.1f
                    ? targetFov
                    : Mathf.Lerp(currentFov, targetFov, 1f - Mathf.Exp(-5f * fovDelta));
            }
        }

        // VehicleSession is the authoritative held-input owner. UI buttons
        // reset themselves too, but Android may suspend the app while uGUI is
        // disabled/rebuilt or before PointerUp reaches the button. Clear the
        // actual motor command here so focus regain can never resume stale
        // throttle/steering without a fresh player press.
        private void ClearHeldDrivingInput()
        {
            touchMove = touchSteer = 0f;
            if (car != null) car.SetInput(0f, 0f);
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) ClearHeldDrivingInput();
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused) ClearHeldDrivingInput();
        }

        private void OnDisable() => ClearHeldDrivingInput();
    }
}
