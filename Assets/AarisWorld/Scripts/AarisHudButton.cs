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
        IPointerDownHandler, IPointerUpHandler, ICancelHandler, IDragHandler
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
            if (owner == null || !AarisBattleHUD.IsVisible || eventData == null) return;
            bool isHoldAction = kind == AarisBattleHUD.Action.Fire ||
                kind == AarisBattleHUD.Action.Jump;
            if (!isHoldAction && heldPointers.Count > 0) return;
            if (!heldPointers.Add(eventData.pointerId)) return;
            if (heldPointers.Count == 1) owner.Down(kind);
        }

        // The UI owns this finger while Fire or Jump is held.
        public void OnDrag(PointerEventData eventData)
        {
            if (owner == null || eventData == null || !AarisBattleHUD.IsVisible ||
                !heldPointers.Contains(eventData.pointerId)) return;
            if (kind == AarisBattleHUD.Action.Fire || kind == AarisBattleHUD.Action.Jump)
                owner.ForwardLookDelta(eventData.delta);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData == null || !heldPointers.Remove(eventData.pointerId)) return;
            if (heldPointers.Count == 0 && owner != null) owner.Up(kind);
        }

        public void OnCancel(BaseEventData eventData) => Release();

        internal void CancelHold() => Release();

        private void Release()
        {
            if (heldPointers.Count == 0) return;
            heldPointers.Clear();
            if (owner != null) owner.Up(kind);
        }

        private void OnDisable() => Release();
    }
}
