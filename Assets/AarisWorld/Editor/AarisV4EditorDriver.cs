// Editor-only control bridge for Aaris V4. No sockets, scripts or arbitrary reflection.
#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using Aaris.MapReview;

namespace Aaris.MapReview.Editor
{
    [InitializeOnLoad]
    public static class AarisV4EditorDriver
    {
        [Serializable]
        private sealed class Request
        {
            public string id, op, target;
            public float x, y, z, yaw, pitch, seconds;
            public long expiresUnixMs;
        }

        [Serializable]
        private sealed class UiTarget
        {
            public string name, blockedBy;
            public float screenX, screenY;
            public bool visible, enabled, topmost;
        }

        [Serializable]
        private sealed class Response
        {
            public string id, op, result, detail, scene, playerPosition, weapon, stance;
            public bool playing, paused, compiling, updating, weaponEquipped, fireHeld, aiming, pickupAvailable;
            public int frame, magazine, reserve, consoleErrors;
            public float fpsEstimate;
            public UiTarget[] ui;
        }

        private static readonly string Root = Path.Combine(Directory.GetCurrentDirectory(), "ci-live-control");
        private static readonly string Inbox = Path.Combine(Root, "ai_v4_request.json");
        private static readonly string Outbox = Path.Combine(Root, "ai_v4_result.json");
        private static double nextPoll;
        private static double fireUntil = -1, jumpUntil = -1;
        private static AarisBattleActions heldFire;
        private static AarisThirdPersonPlayer heldJump;
        private static GameObject heldUiObject;
        private static PointerEventData heldUiPointer;
        private static double heldUiUntil = -1;
        private static readonly string[] UiNames = {
            "HUD Fire", "HUD Jump", "HUD Crouch", "HUD Prone", "HUD Sprint",
            "ADS", "PICK", "DROP", "RELOAD"
        };
        private static int errors;

        static AarisV4EditorDriver()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += OnPlayStateChanged;
            Application.logMessageReceived += OnLog;
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseInputs;
        }

        private static void OnLog(string text, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                errors++;
        }

        private static void OnPlayStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
                ReleaseInputs();
        }

        private static void ReleaseInputs()
        {
            if (heldFire != null) heldFire.SetFireHeld(false);
            if (heldJump != null) heldJump.SetJumpHeld(false);
            heldFire = null;
            heldJump = null;
            fireUntil = jumpUntil = -1;
            ReleaseUiPointer();
        }

        private static void ReleaseUiPointer()
        {
            if (heldUiObject != null && heldUiPointer != null)
            {
                try { ExecuteEvents.Execute(heldUiObject, heldUiPointer, ExecuteEvents.pointerUpHandler); }
                catch (Exception ex) { Debug.LogWarning("[Aaris V4] release UI failed: " + ex.GetType().Name); }
            }
            heldUiObject = null;
            heldUiPointer = null;
            heldUiUntil = -1;
        }

        private static void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            if (heldUiUntil > 0 && now >= heldUiUntil) ReleaseUiPointer();
            if (fireUntil > 0 && now >= fireUntil)
            {
                if (heldFire != null) heldFire.SetFireHeld(false);
                heldFire = null;
                fireUntil = -1;
            }
            if (jumpUntil > 0 && now >= jumpUntil)
            {
                if (heldJump != null) heldJump.SetJumpHeld(false);
                heldJump = null;
                jumpUntil = -1;
            }
            if (now < nextPoll) return;
            nextPoll = now + 0.12;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || !File.Exists(Inbox))
                return;
            Request req = null;
            try
            {
                var payload = File.ReadAllText(Inbox);
                File.Delete(Inbox); // Consume exactly one local request.
                if (payload.Length > 4096) throw new ArgumentException("request too long");
                req = JsonUtility.FromJson<Request>(payload);
                if (req == null || String.IsNullOrEmpty(req.id) || req.id.Length > 64 ||
                    !System.Text.RegularExpressions.Regex.IsMatch(req.id, @"^[A-Za-z0-9_-]+$"))
                    throw new ArgumentException("invalid request id");
                long nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                // An Editor recovering from a long renderer stall must never
                // execute previously timed-out Fire, Jump, UI or Play actions.
                if (req.expiresUnixMs <= nowUnixMs ||
                    req.expiresUnixMs > nowUnixMs + 120000L)
                    throw new TimeoutException("Command expired or missing a valid deadline");
                Response result = Build(req, "ok", "");
                Execute(req);
                result = Build(req, "ok", "Action accepted by Editor driver");
                Publish(result);
            }
            catch (Exception ex)
            {
                Publish(Build(req, "error", ex.GetType().Name + ": " + ex.Message));
            }
        }

        private static Response Build(Request req, string status, string detail)
        {
            var response = new Response {
                id = req != null ? req.id : "",
                op = req != null ? req.op : "",
                result = status, detail = detail,
                scene = SceneManager.GetActiveScene().path,
                playing = EditorApplication.isPlaying,
                paused = EditorApplication.isPaused,
                compiling = EditorApplication.isCompiling,
                updating = EditorApplication.isUpdating,
                frame = Time.frameCount,
                consoleErrors = errors,
                fpsEstimate = Time.smoothDeltaTime > 0.0001f ? 1f / Time.smoothDeltaTime : 0f,
                magazine = -1, reserve = -1,
            };
            if (EditorApplication.isPlaying)
            {
                var player = UnityEngine.Object.FindFirstObjectByType<AarisThirdPersonPlayer>();
                if (player != null)
                {
                    response.playerPosition = player.transform.position.ToString("F2");
                    var actions = player.GetComponent<AarisBattleActions>();
                    var inventory = player.GetComponent<AarisWeaponInventory>();
                    if (actions != null)
                    {
                        response.stance = actions.Stance.ToString();
                        response.fireHeld = actions.FireHeld;
                    }
                    if (inventory != null)
                    {
                        response.weaponEquipped = inventory.HasGun;
                        response.aiming = inventory.Aiming;
                        response.pickupAvailable = inventory.PickupAvailable;
                        var gun = inventory.Current;
                        if (gun != null)
                        {
                            response.weapon = gun.name;
                            response.magazine = gun.Magazine;
                            response.reserve = gun.Reserve;
                        }
                    }
                }
            }
            if (req != null && req.op == "uimap")
                response.ui = BuildUiMap();
            return response;
        }

        private static UiTarget[] BuildUiMap()
        {
            var result = new List<UiTarget>();
            foreach (var name in UiNames)
            {
                var ui = GameObject.Find(name);
                var entry = new UiTarget { name = name, visible = false };
                if (ui != null)
                {
                    var rect = ui.GetComponent<RectTransform>();
                    var select = ui.GetComponent<Selectable>();
                    entry.visible = IsRenderedUi(ui);
                    entry.enabled = entry.visible && (select == null || select.IsActive() && select.IsInteractable());
                    if (rect != null)
                    {
                        Vector3 center = rect.TransformPoint(rect.rect.center);
                        Vector2 point = RectTransformUtility.WorldToScreenPoint(null, center);
                        entry.screenX = point.x;
                        entry.screenY = point.y;
                        entry.blockedBy = entry.visible ? BlockedBy(ui, point) : "CanvasOrRaycasterDisabled";
                        entry.topmost = entry.enabled && String.IsNullOrEmpty(entry.blockedBy);
                    }
                }
                result.Add(entry);
            }
            return result.ToArray();
        }

        // An active GameObject is not necessarily an on-screen button.
        // Plane Drop intentionally disables the actual Canvas and Raycaster.
        private static bool IsRenderedUi(GameObject ui)
        {
            if (ui == null || !ui.activeInHierarchy) return false;
            var canvas = ui.GetComponentInParent<Canvas>();
            var raycaster = ui.GetComponentInParent<GraphicRaycaster>();
            if (canvas == null || !canvas.isActiveAndEnabled ||
                raycaster == null || !raycaster.isActiveAndEnabled) return false;
            var graphic = ui.GetComponent<Graphic>();
            if (graphic == null || !graphic.isActiveAndEnabled || !graphic.raycastTarget)
                return false;
            foreach (var group in ui.GetComponentsInParent<CanvasGroup>())
                if (!group.interactable || !group.blocksRaycasts || group.alpha <= 0.001f)
                    return false;
            return true;
        }

        private static string BlockedBy(GameObject ui, Vector2 point)
        {
            if (EventSystem.current == null) return "EventSystemMissing";
            var pointer = new PointerEventData(EventSystem.current) { position = point };
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(pointer, hits);
            if (hits.Count == 0) return "NoRaycastTarget";
            var top = hits[0].gameObject;
            return top == ui || top.transform.IsChildOf(ui.transform) ? "" : top.name;
        }

        private static void TapNamedUi(Request req)
        {
            if (!EditorApplication.isPlaying || EditorApplication.isPaused)
                throw new InvalidOperationException("Named UI input requires running Play Mode");
            if (Array.IndexOf(UiNames, req.target) < 0)
                throw new ArgumentException("UI name not allowlisted");
            if (heldUiObject != null)
                throw new InvalidOperationException("another named UI hold is active");
            var ui = GameObject.Find(req.target);
            if (ui == null || !IsRenderedUi(ui))
                throw new InvalidOperationException("UI item hidden or Canvas/Raycaster disabled");
            var select = ui.GetComponent<Selectable>();
            if (select != null && !select.interactable)
                throw new InvalidOperationException("UI item disabled");
            var rect = ui.GetComponent<RectTransform>();
            if (rect == null) throw new InvalidOperationException("UI RectTransform missing");
            var point = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center));
            string blocker = BlockedBy(ui, point);
            if (!String.IsNullOrEmpty(blocker)) throw new InvalidOperationException("blocked_by:" + blocker);
            var pointer = new PointerEventData(EventSystem.current) {
                pointerId = -9001, position = point, button = PointerEventData.InputButton.Left,
                pointerPress = ui, rawPointerPress = ui, eligibleForClick = true,
            };
            if (req.op == "hold_ui") ValidateNumber(req.seconds, 0.05f, 3f);
            ExecuteEvents.Execute(ui, pointer, ExecuteEvents.pointerDownHandler);
            if (req.op == "tap_ui")
            {
                try
                {
                    ExecuteEvents.Execute(ui, pointer, ExecuteEvents.pointerUpHandler);
                    ExecuteEvents.Execute(ui, pointer, ExecuteEvents.pointerClickHandler);
                }
                finally
                {
                    // A faulting UI callback must not leave Fire/Jump held.
                    ExecuteEvents.Execute(ui, pointer, ExecuteEvents.pointerUpHandler);
                }
            }
            else
            {
                heldUiObject = ui;
                heldUiPointer = pointer;
                heldUiUntil = EditorApplication.timeSinceStartup + req.seconds;
            }
        }

        private static void Publish(Response response)
        {
            Directory.CreateDirectory(Root);
            var tmp = Outbox + ".tmp";
            File.WriteAllText(tmp, JsonUtility.ToJson(response, true));
            if (File.Exists(Outbox)) File.Delete(Outbox);
            File.Move(tmp, Outbox);
        }

        private static void Execute(Request req)
        {
            if (req.op == "status" || req.op == "uimap") return;
            if (req.op == "tap_ui" || req.op == "hold_ui") { TapNamedUi(req); return; }
            if (req.op == "play") { EditorApplication.isPlaying = true; return; }
            if (req.op == "stop") { ReleaseInputs(); EditorApplication.isPlaying = false; return; }
            if (req.op == "pause" || req.op == "resume")
            {
                if (!EditorApplication.isPlaying)
                    throw new InvalidOperationException("Pause/resume requires Play Mode");
                EditorApplication.isPaused = req.op == "pause";
                return;
            }
            if (req.op == "step")
            {
                if (!EditorApplication.isPlaying || !EditorApplication.isPaused)
                    throw new InvalidOperationException("Step needs paused Play Mode");
                EditorApplication.Step();
                return;
            }
            if (req.op == "camera" || req.op == "focus")
            {
                var view = SceneView.lastActiveSceneView;
                if (view == null) view = EditorWindow.GetWindow<SceneView>("Scene");
                Vector3 location;
                Quaternion facing;
                float distance;
                if (req.op == "camera")
                {
                    ValidateNumber(req.x, -2000, 2000);
                    ValidateNumber(req.y, -500, 4000);
                    ValidateNumber(req.z, -2000, 2000);
                    ValidateNumber(req.yaw, -360, 360);
                    ValidateNumber(req.pitch, -85, 85);
                    location = new Vector3(req.x, req.y, req.z);
                    facing = Quaternion.Euler(req.pitch, req.yaw, 0f);
                    distance = 10f;
                    location += facing * Vector3.forward * distance;
                }
                else
                {
                    if (String.IsNullOrEmpty(req.target) || req.target.Length > 180)
                        throw new ArgumentException("target required");
                    var obj = GameObject.Find(req.target);
                    if (obj == null) throw new InvalidOperationException("target not active or not found");
                    location = obj.transform.position;
                    var bounds = obj.GetComponentsInChildren<Renderer>(true);
                    float radius = 4f;
                    Bounds combined = new Bounds(location, Vector3.zero);
                    bool foundRenderer = false;
                    foreach (var renderer in bounds)
                    {
                        if (renderer == null) continue;
                        if (!foundRenderer) { combined = renderer.bounds; foundRenderer = true; }
                        else combined.Encapsulate(renderer.bounds);
                    }
                    if (foundRenderer)
                    {
                        location = combined.center;
                        radius = Mathf.Max(4f, combined.extents.magnitude);
                    }
                    facing = Quaternion.Euler(18f, 35f, 0f);
                    distance = Mathf.Clamp(radius * 2.6f, 3f, 150f);
                }
                view.Show();
                view.in2DMode = false;
                view.orthographic = false;
                view.LookAtDirect(location, facing, distance);
                view.Repaint();
                return;
            }
            if (req.op == "release")
            {
                ReleaseInputs();
                return;
            }
            if (req.op != "action") throw new ArgumentException("action not allowlisted");
            if (!EditorApplication.isPlaying || EditorApplication.isPaused)
                throw new InvalidOperationException("Gameplay action requires running Play Mode");
            var player = UnityEngine.Object.FindFirstObjectByType<AarisThirdPersonPlayer>();
            if (player == null) throw new InvalidOperationException("player not found");
            var combat = player.GetComponent<AarisBattleActions>();
            var inventory = player.GetComponent<AarisWeaponInventory>();
            switch (req.target)
            {
                case "Fire":
                    if (combat == null) throw new InvalidOperationException("combat unavailable");
                    ValidateNumber(req.seconds, 0.05f, 3f);
                    combat.SetFireHeld(true);
                    heldFire = combat;
                    fireUntil = EditorApplication.timeSinceStartup + req.seconds;
                    break;
                case "Jump":
                    ValidateNumber(req.seconds, 0.05f, 3f);
                    player.SetJumpHeld(true);
                    heldJump = player;
                    jumpUntil = EditorApplication.timeSinceStartup + req.seconds;
                    break;
                case "Crouch":
                    if (combat == null) throw new InvalidOperationException("combat unavailable");
                    combat.ToggleCrouch();
                    break;
                case "Prone":
                    if (combat == null) throw new InvalidOperationException("combat unavailable");
                    combat.ToggleProne();
                    break;
                case "Sprint": player.ToggleAutoSprint(); break;
                case "Pickup":
                    if (inventory == null) throw new InvalidOperationException("inventory unavailable");
                    inventory.RequestPickup(); break;
                case "Scope":
                    if (inventory == null) throw new InvalidOperationException("inventory unavailable");
                    inventory.ToggleAim(); break;
                case "Reload":
                    if (inventory == null) throw new InvalidOperationException("inventory unavailable");
                    inventory.Reload(); break;
                case "Drop":
                    if (inventory == null) throw new InvalidOperationException("inventory unavailable");
                    inventory.DropCurrent(); break;
                default:
                    throw new ArgumentException("unknown allowlisted gameplay action");
            }
        }

        private static void ValidateNumber(float value, float min, float max)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < min || value > max)
                throw new ArgumentOutOfRangeException("number out of range");
        }
    }
}
#endif
