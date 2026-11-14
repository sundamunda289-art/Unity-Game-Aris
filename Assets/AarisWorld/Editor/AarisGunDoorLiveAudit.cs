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

        [MenuItem("Aaris/CI/Native gun-door collision and grip Play Mode audit")]
        public static void Run()
        {
            Directory.CreateDirectory(Folder);
            SessionState.SetInt(Key, 0);
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

        private static void CheckLootSite(int index, AarisThirdPersonPlayer player)
        {
            int active = 0, models = 0, nearby = 0, oversize = 0;
            float nearest = float.PositiveInfinity, longestNearby = 0f;
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
                nearest = Mathf.Min(nearest, dist);
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
                         "Sniper", "Shotgun" })
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
            }
            return success;
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
                    if (!StartSite(0, player)) { Abort("SITE0_NOT_FOUND"); return; }
                    SessionState.SetInt(Key, 2);
                    return;
                }
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
