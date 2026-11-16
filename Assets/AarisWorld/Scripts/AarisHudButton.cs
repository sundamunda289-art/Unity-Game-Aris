using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Aaris.MapReview
{
    /// <summary>
    /// Touch lifecycle is tracked per pointer. Hold-fire is released by either
    /// pointer up, a disabled button, scene unload or application pause.
    /// </summary>
    public sealed class AarisHudButton : MonoBehaviour,
        IPointerDownHandler, IPointerUpHandler, ICancelHandler, IDragHandler,
        IInitializePotentialDragHandler
    {
        private AarisBattleHUD owner;
        private AarisBattleHUD.Action kind;
        private Image background;
        private RectTransform bounds;
        // Fire and Jump are hold actions: multiple fingers may overlap.
        // The action remains held until the LAST finger releases, unlike
        // the previous single-pointer implementation that stopped Fire early.
        private readonly System.Collections.Generic.HashSet<int> heldPointers =
            new System.Collections.Generic.HashSet<int>();
        // One Fire finger owns camera aim; other fingers only hold Fire.
        private int aimPointerId = int.MinValue;
        // Reused only while a button is held; no per-frame allocation.
        private readonly System.Collections.Generic.List<int> stalePointers =
            new System.Collections.Generic.List<int>(4);
        private static readonly Color Normal = Color.white;
        private static readonly Color Selected = new Color(1f, 0.85f, 0.62f, 1f);

        internal AarisBattleHUD.Action Kind => kind;

        internal void Configure(AarisBattleHUD controller, AarisBattleHUD.Action action, Image image)
        {
            owner = controller;
            kind = action;
            background = image;
            bounds = transform as RectTransform;
        }

        internal bool IsWithin(Vector2 point) =>
            bounds != null && RectTransformUtility.RectangleContainsScreenPoint(bounds, point, null);

        internal void SetSelected(bool selected)
        {
            Color tint = selected || heldPointers.Count > 0 ? Selected : Normal;
            if (background != null && background.color != tint) background.color = tint;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (owner == null || !AarisBattleHUD.IsVisible || eventData == null ||
                eventData.button != PointerEventData.InputButton.Left) return;
            bool isHoldAction = kind == AarisBattleHUD.Action.Fire ||
                kind == AarisBattleHUD.Action.Jump;
            if (!isHoldAction && heldPointers.Count > 0) return;
            if (!heldPointers.Add(eventData.pointerId)) return;
            if (heldPointers.Count == 1)
            {
                if (kind == AarisBattleHUD.Action.Fire) aimPointerId = eventData.pointerId;
                owner.Down(kind);
            }
        }

        // Fire is a continuous look input, not a draggable UI item. Unity's
        // default pixel drag threshold discards initial micro-aim motion.
        // Disable it for this pointer only; never change the global EventSystem.
        public void OnInitializePotentialDrag(PointerEventData eventData)
        {
            if (eventData != null && kind == AarisBattleHUD.Action.Fire)
                eventData.useDragThreshold = false;
        }

        // Fire can drag to aim while held. Jump is a vertical ACTION,
        // not a second look joystick: routing its drag into camera aim while
        // also dragging Fire made jump-shots rotate the camera twice and
        // produced unpredictable left/right swipes on 2-thumb Android play.
        // Keep independent held-pointer ownership and jumping unchanged.
        public void OnDrag(PointerEventData eventData)
        {
            if (owner == null || eventData == null || !AarisBattleHUD.IsVisible ||
                !heldPointers.Contains(eventData.pointerId)) return;
            if (kind == AarisBattleHUD.Action.Fire)
            {
                if (eventData.pointerId == aimPointerId)
                    owner.ForwardLookDelta(eventData.delta);
            }
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData != null) ReleasePointer(eventData.pointerId);
        }

        private void ReleasePointer(int pointerId)
        {
            if (!heldPointers.Remove(pointerId)) return;
            // Promote a remaining Fire finger without cancelling the shot.
            if (pointerId == aimPointerId)
            {
                aimPointerId = int.MinValue;
                foreach (int pointer in heldPointers)
                {
                    aimPointerId = pointer;
                    break;
                }
            }
            if (heldPointers.Count == 0) aimPointerId = int.MinValue;
            if (heldPointers.Count == 0 && owner != null) owner.Up(kind);
        }

        // PointerUp is usually delivered by the EventSystem. A lost touch
        // snapshot (without focus loss) can otherwise leave Fire/Jump held
        // forever. Reconcile after EventSystem.Update, never infer ownership
        // from a new touch's screen position.
        private void LateUpdate()
        {
            if (heldPointers.Count == 0) return;
            if (!AarisBattleHUD.IsVisible)
            {
                Release();
                return;
            }
            // This mapping is specific to the classic StandaloneInputModule.
            // Other input modules may assign device-specific pointer IDs.
            if (EventSystem.current == null ||
                !(EventSystem.current.currentInputModule is StandaloneInputModule)) return;

            stalePointers.Clear();
            foreach (int pointerId in heldPointers)
            {
                if (!IsPointerPhysicallyHeld(pointerId))
                    stalePointers.Add(pointerId);
            }
            for (int i = 0; i < stalePointers.Count; i++)
                ReleasePointer(stalePointers[i]);
            stalePointers.Clear();
        }

        internal static bool IsPointerPhysicallyHeld(int pointerId)
        {
            if (pointerId == PointerInputModule.kMouseLeftId)
                return Input.GetMouseButton(0);

            int count = Input.touchCount;
            for (int i = 0; i < count; i++)
            {
                Touch touch = Input.GetTouch(i);
                if (touch.fingerId == pointerId &&
                    touch.phase != TouchPhase.Ended &&
                    touch.phase != TouchPhase.Canceled)
                    return true;
            }
            return false;
        }

        public void OnCancel(BaseEventData eventData) => Release();

        internal void CancelHold() => Release();

        private void Release()
        {
            if (heldPointers.Count == 0)
            {
                aimPointerId = int.MinValue;
                return;
            }
            heldPointers.Clear();
            aimPointerId = int.MinValue;
            if (owner != null) owner.Up(kind);
        }

        // Android can suspend or lose focus while a Fire/Jump finger is down.
        // Unity is not guaranteed to dispatch PointerUp/Cancel on resume.
        // Clear the exact owned pointers and release the action so the next
        // physical touch can start a fresh hold instead of being ignored.
        private void OnApplicationPause(bool paused)
        {
            if (paused) Release();
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused) Release();
        }

        private void OnDisable() => Release();
    }
}
