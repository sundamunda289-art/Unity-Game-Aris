#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Native, temporary Play Mode QA for authored loot, real Unity FBX grips
    /// and interactive door leaves. No scene/asset saves, builds or downloads.
    /// -batchmode -nographics -executeMethod
    /// Aaris.MapReview.Editor.AarisGunDoorLiveAudit.Run (without -quit).
    /// Report: /tmp/aaris-gun-door-live-audit/report.txt
    /// </summary>
    [InitializeOnLoad]
    public static class AarisGunDoorLiveAudit
    {
        private const string Scene = "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";
        private const string Key = "AarisGunDoorLiveAudit20261114";
        private static readonly string Folder = Path.Combine(Path.GetTempPath(),
            "aaris-gun-door-live-audit");
        private static readonly BindingFlags Private =
            BindingFlags.Instance | BindingFlags.NonPublic;

        [Serializable] private sealed class LootDocument { public LootPoint[] candidates; }
        [Serializable] private sealed class LootPoint { public string kind; public float x, z; }

        static AarisGunDoorLiveAudit() { EditorApplication.update += Tick; }

        // Fast separate graphical modes avoid rendering the entire four-tile
        // terrain for every gun view on CPU-only Unity/Xvfb test machines.
        public static void RunGroundPhoto()
        {
            Run();
            SessionState.SetBool(Key + "GroundOnly", true);
        }

        public static void RunGunPhotos()
        {
            Run();
            SessionState.SetBool(Key + "GunOnly", true);
        }

        [MenuItem("Aaris/CI/Native gun-door collision and grip Play Mode audit")]
        public static void Run()
        {
            Directory.CreateDirectory(Folder);
            SessionState.SetInt(Key, 0);
            SessionState.SetBool(Key + "GroundOnly", false);
            SessionState.SetBool(Key + "GunOnly", false);
            SessionState.SetString(Key + "Report", "UNITY=" + Application.unityVersion + "\n");
            SessionState.SetInt(Key + "LootSites", 0);
            SessionState.SetFloat(Key + "Deadline",
                (float)EditorApplication.timeSinceStartup + 210f);
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode ||
                    EditorApplication.isCompiling)
                    throw new InvalidOperationException("EDITOR_BUSY");
                var scene = EditorSceneManager.OpenScene(Scene, OpenSceneMode.Single);
                if (scene.path != Scene) throw new InvalidOperationException("SCENE_NOT_OPEN");
                SessionState.SetInt(Key, 1);
                EditorApplication.isPlaying = true;
            }
            catch (Exception ex) { Abort("PREFLIGHT_" + ex.GetType().Name + "_" + ex.Message); }
        }

        private static void Note(string line) =>
            SessionState.SetString(Key + "Report",
                SessionState.GetString(Key + "Report", "") + line + "\n");

        private static void Abort(string reason)
        {
            Note("FAIL=" + reason);
            Finish(1);
        }

        private static void Finish(int exitCode)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, "report.txt"),
                SessionState.GetString(Key + "Report", "") +
                (exitCode == 0 ? "RESULT=PASS\n" : "RESULT=FAIL\n"));
            SessionState.SetInt(Key + "ExitCode", exitCode);
            SessionState.SetInt(Key, 9);
            if (EditorApplication.isPlaying)
                EditorApplication.isPlaying = false;
            else if (Application.isBatchMode)
                EditorApplication.Exit(exitCode);
        }

        private static LootPoint SelectSite(int siteIndex)
        {
            var asset = Resources.Load<TextAsset>("AarisWeapons/loot_candidates");
            if (asset == null) return null;
            var data = JsonUtility.FromJson<LootDocument>(asset.text);
            if (data == null || data.candidates == null) return null;
            foreach (LootPoint point in data.candidates)
            {
                if (point == null || point.kind != "open") continue;
                if ((siteIndex == 0 && !(point.x < -600f && point.z < -600f)) ||
                    (siteIndex == 1 && !(Mathf.Abs(point.x) < 250f &&
                                         Mathf.Abs(point.z) < 250f)) ||
                    (siteIndex == 2 && !(point.x > 600f && point.z > 600f)))
                    continue;
                foreach (Terrain terrain in Terrain.activeTerrains)
                {
                    if (terrain == null || terrain.terrainData == null) continue;
                    Vector3 p = terrain.transform.position;
                    Vector3 size = terrain.terrainData.size;
                    if (point.x < p.x || point.x > p.x + size.x ||
                        point.z < p.z || point.z > p.z + size.z) continue;
                    if ((terrain.transform.position.y + terrain.SampleHeight(new Vector3(point.x, 0f, point.z))) >
                        -37.0f) return point;
                }
            }
            return null;
        }

        private static void MovePlayer(AarisThirdPersonPlayer player, Vector3 pos)
        {
            CharacterController controller = player.GetComponent<CharacterController>();
            if (controller != null) controller.enabled = false;
            player.transform.position = pos;
            if (controller != null) controller.enabled = true;
            Physics.SyncTransforms(); // Explicit QA teleport, never an Update hotpath.
            var streaming = Object.FindFirstObjectByType<AarisNearbyColliders>();
            if (streaming != null) streaming.RefreshNow();
        }

        private static bool StartSite(int index, AarisThirdPersonPlayer player)
        {
            LootPoint point = SelectSite(index);
            if (point == null) { Note("SITE_" + index + "=NO_TERRAIN_CANDIDATE"); return false; }
            foreach (Terrain tile in Terrain.activeTerrains)
            {
                if (tile == null || tile.terrainData == null) continue;
                var p = tile.transform.position;
                var d = tile.terrainData.size;
                if (point.x < p.x || point.x > p.x + d.x ||
                    point.z < p.z || point.z > p.z + d.z) continue;
                float y = tile.transform.position.y + tile.SampleHeight(new Vector3(point.x, 0f, point.z));
                MovePlayer(player, new Vector3(point.x, y + 0.12f, point.z));
                Note("SITE_" + index + "_WORLD=" + player.transform.position.ToString("F2"));
                SessionState.SetFloat(Key + "SiteStart",
                    (float)EditorApplication.timeSinceStartup);
                return true;
            }
            return false;
        }

        // Editor lives in Assembly-CSharp-Editor and cannot access runtime's
        // internal mesh helper; measure the identical source-mesh corners here.
        private static bool MeasureMeshes(Transform root, Renderer[] renderers,
            out Bounds bounds)
        {
            bounds = default(Bounds);
            bool found = false;
            if (root == null || renderers == null) return false;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                Bounds source;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    if (skinned.sharedMesh == null) continue;
                    source = skinned.localBounds;
                }
                else
                {
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    source = filter.sharedMesh.bounds;
                }
                Vector3 c = source.center, e = source.extents;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 local = c + new Vector3(
                        (corner & 1) == 0 ? -e.x : e.x,
                        (corner & 2) == 0 ? -e.y : e.y,
                        (corner & 4) == 0 ? -e.z : e.z);
                    Vector3 point = root.InverseTransformPoint(
                        renderer.transform.TransformPoint(local));
                    if (!found) { bounds = new Bounds(point, Vector3.zero); found = true; }
                    else bounds.Encapsulate(point);
                }
            }
            return found && bounds.size.sqrMagnitude > 0.000001f;
        }


        // Real URP camera readback from an actual Play Mode scene. Images are
        // written under /tmp only and never ship inside the Android player.
        private static void CaptureAuditPhoto(
            string label, Vector3 focus, Vector3 lookFrom, int subjectLayer)
        {
            Camera camera = Camera.main;
            if (camera == null) { Note("PHOTO_" + label + "=NO_CAMERA"); return; }
            const int width = 960, height = 540;
            RenderTexture output = RenderTexture.GetTemporary(width, height, 24);
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            Vector3 previousPosition = camera.transform.position;
            Quaternion previousRotation = camera.transform.rotation;
            int previousMask = camera.cullingMask;
            CameraClearFlags previousClear = camera.clearFlags;
            Color previousBackground = camera.backgroundColor;
            float previousFov = camera.fieldOfView;
            Texture2D pixels = null;
            try
            {
                Vector3 orbit = lookFrom.sqrMagnitude > 0.01f
                    ? lookFrom.normalized : Vector3.forward;
                Vector3 viewpoint = focus + orbit * 2.25f + Vector3.up * 0.32f;
                camera.transform.SetPositionAndRotation(
                    viewpoint, Quaternion.LookRotation(focus - viewpoint, Vector3.up));
                bool showTerrain = label.StartsWith("GroundLoot_", StringComparison.Ordinal);
                if (showTerrain)
                    camera.cullingMask |= 1 << subjectLayer;
                else
                {
                    // Real player/held-gun meshes are on the saved Player
                    // layer (8), explicitly included in URP RendererData's
                    // Opaque/Transparent masks. Layer 30 is excluded by URP;
                    // moving gun meshes there produced all-background JPEGs.
                    // Keep the authored layers untouched and isolate only the
                    // actual player's layer, excluding default-layer terrain.
                    camera.cullingMask = 1 << subjectLayer;
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = new Color(.12f, .16f, .19f, 1f);
                }
                camera.fieldOfView = 54f;
                camera.targetTexture = output;
                camera.Render();
                RenderTexture.active = output;
                pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
                pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                pixels.Apply(false);
                byte[] jpg = pixels.EncodeToJPG(86);
                if (jpg.Length < 4000)
                    throw new InvalidOperationException("EMPTY_NATIVE_CAMERA_FRAME");
                string filename = Path.Combine(Folder, label + ".jpg");
                File.WriteAllBytes(filename, jpg);
                Note("PHOTO_" + label + "_bytes=" + jpg.Length);
            }
            finally
            {
                camera.transform.SetPositionAndRotation(
                    previousPosition, previousRotation);
                camera.cullingMask = previousMask;
                camera.clearFlags = previousClear;
                camera.backgroundColor = previousBackground;
                camera.fieldOfView = previousFov;
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (pixels != null) Object.DestroyImmediate(pixels);
                RenderTexture.ReleaseTemporary(output);
            }
        }

        private static void CheckLootSite(int index, AarisThirdPersonPlayer player)
        {
            int active = 0, models = 0, nearby = 0, oversize = 0;
            float nearest = float.PositiveInfinity, longestNearby = 0f;
            AarisWeaponPickup nearestPickup = null;
            foreach (var pickup in Object.FindObjectsByType<AarisWeaponPickup>(
                FindObjectsSortMode.None))
            {
                if (pickup == null || !pickup.gameObject.activeInHierarchy) continue;
                active++;
                bool visibleMesh = false, hasBounds = false;
                Bounds gunBody = default(Bounds);
                foreach (var renderer in pickup.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(renderer is MeshRenderer || renderer is SkinnedMeshRenderer))
                        continue; // Exclude the 1.65m nonphysical beacon.
                    visibleMesh |= renderer.enabled;
                    // Immediate Renderer.bounds can be stale after an FBX is moved
                    // or rescaled. Inspect current mesh geometry in pickup space.
                    // The same exact-mesh helper is used by live pickup placement.
                    // Collect renderers once below, not a scene-wide query.
                }
                var meshRenderers = pickup.GetComponentsInChildren<Renderer>(true);
                hasBounds = MeasureMeshes(
                    pickup.transform, meshRenderers, out gunBody);
                if (visibleMesh && hasBounds) models++;
                float dist = Vector3.Distance(player.transform.position,
                    pickup.transform.position);
                if (dist < nearest) { nearest = dist; nearestPickup = pickup; }
                if (dist < 50f && visibleMesh)
                {
                    nearby++;
                    float longest = Mathf.Max(gunBody.size.x,
                        gunBody.size.y, gunBody.size.z);
                    longestNearby = Mathf.Max(longestNearby, longest);
                    if (longest < .12f || longest > 1.7f) oversize++;
                }
            }
            if (nearby > 0 && oversize == 0)
                SessionState.SetInt(Key + "LootSites",
                    SessionState.GetInt(Key + "LootSites", 0) + 1);
            if (nearby > 0 && nearestPickup != null)
            {
                // Same real authored gun, camera and angle in both frames.
                // First temporarily reproduce the previous 4cm root lift
                // (current outdoor 28cm lift minus 24cm), then restore
                // production placement for a genuine comparative URP render.
                Vector3 raisedPosition = nearestPickup.transform.position;
                Vector3 fixedFocus = raisedPosition + Vector3.up * 0.14f;
                try
                {
                    nearestPickup.transform.position =
                        raisedPosition - Vector3.up * 0.24f;
                    CaptureAuditPhoto("GroundLoot_Before_Site_" + index,
                        fixedFocus, player.transform.forward,
                        nearestPickup.gameObject.layer);
                }
                finally
                {
                    nearestPickup.transform.position = raisedPosition;
                }
                CaptureAuditPhoto("GroundLoot_After_Site_" + index,
                    fixedFocus, player.transform.forward,
                    nearestPickup.gameObject.layer);
            }
            Note("SITE_" + index + "_PICKUPS active=" + active + " mesh=" +
                models + " within50m=" + nearby + " invalidSize=" + oversize +
                " longestNearby_m=" + longestNearby.ToString("F3") +
                " nearest=" +
                (float.IsInfinity(nearest) ? -1f : nearest).ToString("F2"));
        }

        private static bool TestHeldMeshes(AarisThirdPersonPlayer player)
        {
            var inventory = player.GetComponent<AarisWeaponInventory>();
            var visual = player.GetComponent<AarisCharacterVisual>();
            if (inventory == null || visual == null || !visual.HasWorkingHumanoid)
            {
                Note("HELD_GUN=NO_WORKING_HUMANOID_OR_INVENTORY");
                return false;
            }
            Animator animator = visual.GetComponentInChildren<Animator>(true);
            Transform hand = animator != null && animator.isHuman
                ? animator.GetBoneTransform(HumanBodyBones.RightHand) : null;
            if (hand == null) { Note("HELD_GUN=NO_RIGHT_HAND"); return false; }
            bool success = true;
            foreach (string gunName in new[] { "SMG", "AssaultRifle", "BattleRifle",
                         "Sniper", "Shotgun", "DMR" })
            {
                AarisWeaponSpec spec = AarisWeaponCatalog.Get(gunName);
                bool equipped = spec != null && inventory.TryPickup(
                    new AarisWeaponState(spec, (uint)(33001 + gunName.Length)), out _);
                if (!equipped) { Note("HELD_" + gunName + "=EQUIP_FAILED"); success = false; continue; }
                var gun = inventory.Current;
                var renderers = gun.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0) { Note("HELD_" + gunName + "=NO_RENDERERS"); success = false; continue; }
                if (!MeasureMeshes(gun.transform, renderers,
                    out Bounds bounds))
                {
                    Note("HELD_" + gunName + "=INVALID_SOURCE_MESH_BOUNDS");
                    success = false;
                    continue;
                }
                Vector3 measuredWorldCenter = gun.transform.TransformPoint(bounds.center);
                Vector3 worldSize = Vector3.Scale(bounds.size, gun.transform.lossyScale);
                float distance = Vector3.Distance(hand.position, measuredWorldCenter);
                float size = Mathf.Max(Mathf.Abs(worldSize.x), Mathf.Abs(worldSize.y),
                    Mathf.Abs(worldSize.z));
                Note("HELD_" + gunName + " hand_center_m=" + distance.ToString("F3") +
                    " body_longest_m=" + size.ToString("F3"));
                if (distance > 0.90f || size < 0.15f || size > 1.7f) success = false;
                Vector3 focus = measuredWorldCenter + Vector3.up * 0.03f;
                CaptureAuditPhoto("Held_" + gunName + "_Front",
                    focus, player.transform.forward, player.gameObject.layer);
                CaptureAuditPhoto("Held_" + gunName + "_Side",
                    focus, Quaternion.AngleAxis(78f, Vector3.up) *
                    player.transform.forward, player.gameObject.layer);
                bool fired = gun.TryFire(Camera.main, player.gameObject);
                Note("FIRING_" + gunName + "=" + fired);
                if (!fired) success = false;
                else
                {
                    CaptureAuditPhoto("Firing_" + gunName,
                        focus, player.transform.forward, player.gameObject.layer);
                    bool reload = gun.BeginReload();
                    Note("RELOAD_" + gunName + "=" + reload);
                    if (!reload) success = false;
                    CaptureAuditPhoto("ReloadState_" + gunName,
                        focus, player.transform.forward, player.gameObject.layer);
                }
            }
            return success;
        }

        // Unlike the single-frame geometry audit, visual proof must run
        // after true PlayerLoop animation/IK and grip positioning have updated.
        // Each equip receives several rendered frames before its photographs.
        private static System.Collections.IEnumerator CaptureAnimatedWeaponPhotos(
            AarisThirdPersonPlayer player)
        {
            var inventory = player.GetComponent<AarisWeaponInventory>();
            var visual = player.GetComponent<AarisCharacterVisual>();
            if (inventory == null || visual == null || !visual.HasWorkingHumanoid)
            {
                Note("PHOTO_GUN=NO_WORKING_HUMANOID");
                Finish(1);
                yield break;
            }
            Animator animator = visual.GetComponentInChildren<Animator>(true);
            Transform hand = animator != null && animator.isHuman
                ? animator.GetBoneTransform(HumanBodyBones.RightHand) : null;
            if (hand == null)
            {
                Note("PHOTO_GUN=NO_RIGHT_HAND");
                Finish(1);
                yield break;
            }

            bool pass = true;
            foreach (string gunName in new[] { "SMG", "AssaultRifle",
                         "BattleRifle", "Sniper", "Shotgun", "DMR" })
            {
                AarisWeaponSpec weapon = AarisWeaponCatalog.Get(gunName);
                if (weapon == null || !inventory.TryPickup(
                    new AarisWeaponState(weapon, (uint)(44001 + gunName.Length)),
                    out _))
                {
                    Note("PHOTO_EQUIP_" + gunName + "=FAIL");
                    pass = false;
                    continue;
                }

                // Real game Update/Animator/LateUpdate, not a synthetic
                // manual LateUpdate invocation in the same equip frame.
                for (int frame = 0; frame < 6; frame++)
                    yield return null;

                AarisEquippedGun gun = inventory.Current;
                Renderer[] renderers = gun != null ?
                    gun.GetComponentsInChildren<Renderer>(true) : null;
                if (renderers == null ||
                    !MeasureMeshes(gun.transform, renderers, out Bounds bounds))
                {
                    Note("PHOTO_" + gunName + "=NO_RENDERABLE_GUN");
                    pass = false;
                    continue;
                }
                int enabledCount = 0;
                foreach (Renderer r in renderers)
                    if (r != null && r.enabled && r.gameObject.activeInHierarchy)
                        enabledCount++;
                if (enabledCount == 0)
                {
                    Note("PHOTO_" + gunName + "=ALL_RENDERERS_DISABLED");
                    pass = false;
                    continue;
                }
                Vector3 center = gun.transform.TransformPoint(bounds.center);
                Vector3 dimensions = Vector3.Scale(
                    bounds.size, gun.transform.lossyScale);
                float longest = Mathf.Max(Mathf.Abs(dimensions.x),
                    Mathf.Abs(dimensions.y), Mathf.Abs(dimensions.z));
                float handGap = Vector3.Distance(hand.position, center);
                Note("PHOTO_GUN_" + gunName + " renderers=" + enabledCount +
                    " hand_center_m=" + handGap.ToString("F3") +
                    " longest_m=" + longest.ToString("F3"));
                if (handGap > .9f || longest < .15f || longest > 1.7f)
                    pass = false;

                Vector3 focus = center + Vector3.up * .03f;
                Vector3 front = Quaternion.AngleAxis(25f, Vector3.up) *
                    player.transform.forward;
                CaptureAuditPhoto("Held_" + gunName + "_Front",
                    focus, front, player.gameObject.layer);
                CaptureAuditPhoto("Held_" + gunName + "_Side",
                    focus, Quaternion.AngleAxis(78f, Vector3.up) *
                    player.transform.forward, player.gameObject.layer);

                bool fired = gun.TryFire(Camera.main, player.gameObject);
                Note("PHOTO_FIRING_" + gunName + "=" + fired);
                if (!fired) { pass = false; continue; }
                CaptureAuditPhoto("Firing_" + gunName,
                    focus, front, player.gameObject.layer);
                bool reload = gun.BeginReload();
                Note("PHOTO_RELOAD_" + gunName + "=" + reload);
                if (!reload) pass = false;
                for (int frame = 0; frame < 5; frame++)
                    yield return null;
                CaptureAuditPhoto("ReloadState_" + gunName,
                    focus, front, player.gameObject.layer);
            }
            Finish(pass ? 0 : 1);
        }

        private static void Tick()
        {
            int stage = SessionState.GetInt(Key, 0);
            if (stage == 0) return;
            if (stage == 9)
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                SessionState.SetInt(Key, 0);
                if (Application.isBatchMode)
                    EditorApplication.Exit(SessionState.GetInt(Key + "ExitCode", 1));
                return;
            }
            if (EditorApplication.timeSinceStartup >
                SessionState.GetFloat(Key + "Deadline", 0))
            { Abort("PLAYMODE_TIMEOUT"); return; }
            if (!EditorApplication.isPlaying) return;
            try
            {
                var player = Object.FindFirstObjectByType<AarisThirdPersonPlayer>();
                var world = Object.FindFirstObjectByType<AarisWeaponLootWorld>();
                if (player == null || world == null || Terrain.activeTerrains.Length != 4)
                    return;
                if (stage == 1)
                {
                    // Test physical floor visibility independent of the
                    // intentionally automatic first-time starter pickup.
                    // Otherwise a correctly spawned gun disappears from
                    // the scene immediately and yields a misleading zero.
                    typeof(AarisWeaponLootWorld).GetField(
                        "allowInitialAutoPickup", Private).SetValue(world, false);
                    var drop = player.GetComponent<AarisOfflineDrop>();
                    if (drop != null && !drop.HasLanded)
                    {
                        var land = typeof(AarisOfflineDrop).GetMethod("Land", Private);
                        if (land == null) throw new MissingMethodException("AarisOfflineDrop.Land");
                        land.Invoke(drop, null);
                    }
                    if (SessionState.GetBool(Key + "GroundOnly", false))
                    {
                        if (!StartSite(1, player)) { Abort("GROUND_SITE_NOT_FOUND"); return; }
                        SessionState.SetInt(Key, 7);
                        return;
                    }
                    if (SessionState.GetBool(Key + "GunOnly", false))
                    {
                        if (!StartSite(1, player)) { Abort("GUN_SITE_NOT_FOUND"); return; }
                        SessionState.SetInt(Key, 8);
                        return;
                    }
                    if (!StartSite(0, player)) { Abort("SITE0_NOT_FOUND"); return; }
                    SessionState.SetInt(Key, 2);
                    return;
                }
                if (stage == 7 || stage == 8)
                {
                    float elapsed = (float)EditorApplication.timeSinceStartup -
                        SessionState.GetFloat(Key + "SiteStart", 0);
                    if (elapsed < (stage == 7 ? 8.5f : 3.0f)) return;
                    if (stage == 7)
                    {
                        CheckLootSite(1, player);
                        Finish(SessionState.GetInt(Key + "LootSites", 0) > 0 ? 0 : 1);
                    }
                    else
                    {
                        SessionState.SetInt(Key, 12);
                        player.StartCoroutine(CaptureAnimatedWeaponPhotos(player));
                    }
                    return;
                }
                if (stage == 12) return; // Coroutine owns completion.
                int index = stage - 2;
                if (stage >= 2 && stage <= 4)
                {
                    if (EditorApplication.timeSinceStartup -
                        SessionState.GetFloat(Key + "SiteStart", 0) < 7.5f) return;
                    CheckLootSite(index, player);
                    if (stage < 4 && !StartSite(index + 1, player))
                    { Abort("NEXT_SITE_NOT_FOUND"); return; }
                    SessionState.SetInt(Key, stage + 1);
                    return;
                }
                if (stage == 5)
                {
                    bool hands = TestHeldMeshes(player);
                    var doors = Object.FindObjectsByType<AarisBreakablePanel>(
                        FindObjectsSortMode.None);
                    int realDoors = 0;
                    AarisBreakablePanel door = null;
                    foreach (var panel in doors)
                        if (panel != null && panel.enabled && !panel.IsBroken)
                        { realDoors++; if (door == null) door = panel; }
                    Note("RUNTIME_REAL_DOORS=" + realDoors);
                    SessionState.SetBool(Key + "Hands", hands);
                    if (door == null)
                    {
                        Note("DOOR=NONE_IN_RUNTIME_WORLD");
                        Finish(1);
                        return;
                    }
                    MovePlayer(player, door.transform.position +
                        door.transform.right * 0.7f - door.transform.forward * 0.8f +
                        Vector3.up * 0.06f);
                    SessionState.SetFloat(Key + "SiteStart",
                        (float)EditorApplication.timeSinceStartup);
                    SessionState.SetInt(Key, 6);
                    return;
                }
                if (stage == 6)
                {
                    if (EditorApplication.timeSinceStartup -
                        SessionState.GetFloat(Key + "SiteStart", 0) < 1.1f) return;
                    int doorsOpen = 0;
                    foreach (var panel in Object.FindObjectsByType<AarisBreakablePanel>(
                        FindObjectsSortMode.None))
                        if (panel != null && panel.IsOpen) doorsOpen++;
                    Note("RUNTIME_DOORS_OPEN=" + doorsOpen);
                    bool passed = SessionState.GetInt(Key + "LootSites", 0) >= 2 &&
                        SessionState.GetBool(Key + "Hands", false) && doorsOpen > 0;
                    Finish(passed ? 0 : 1);
                }
            }
            catch (Exception ex) { Abort(ex.GetType().Name + "_" + ex.Message); }
        }
    }
}
#endif
