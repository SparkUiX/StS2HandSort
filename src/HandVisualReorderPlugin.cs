using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace Hand_Sort.src;

internal static class HandVisualReorderConfig
{
    public static double HoverLockSeconds = 0.5;
    public static double GlobalHoverLockSeconds = 0.3;
    public static float GapSizeUnits = 2f;

    // 所有卡牌判定点统一加上的 X 偏移，默认 0
    public static float CardPositionXOffset = 0f;

    // true:
    //   从右往左时，插入某张牌后方需要越过“下一张牌的位置”
    // false:
    //   从右往左时，使用“卡牌位置 - 固定偏移量”作为判定点
    public static bool UseNextCardThresholdWhenMovingLeft = false;

    // 当 UseNextCardThresholdWhenMovingLeft = false 时使用
    public static float MovingLeftThresholdOffset = 0f;

    // 鼠标方向判定死区，避免轻微抖动频繁切方向
    public static float DirectionDeadzone = 0.5f;
}

internal static class HandVisualReorderRuntime
{
    private sealed class DragState
    {
        public NHandCardHolder DraggedHolder;
        public int OriginalIndex;
        public int PreviewInsertIndex;
        public bool ShowGap;
        public float PreviousMouseX;
        public int LastDirection; // 1 = right, -1 = left, 0 = none
    }

    private sealed class HoverLockState
    {
        public NHandCardHolder Holder;
        public ulong Token;
        public bool LockAllHolders;
    }

    private static readonly Dictionary<NPlayerHand, DragState> DragStates = new();
    private static readonly Dictionary<NPlayerHand, HoverLockState> HoverLocks = new();

    private static ulong _hoverLockTokenCounter;

    private static readonly AccessTools.FieldRef<NMouseCardPlay, float> DragStartYRef =
        AccessTools.FieldRefAccess<NMouseCardPlay, float>("_dragStartYPosition");

    private static readonly AccessTools.FieldRef<NMouseCardPlay, bool> IsLeftMouseDownRef =
        AccessTools.FieldRefAccess<NMouseCardPlay, bool>("_isLeftMouseDown");

    private static readonly AccessTools.FieldRef<NMouseCardPlay, bool> SkipStartDragRef =
        AccessTools.FieldRefAccess<NMouseCardPlay, bool>("_skipStartCardDrag");

    private static readonly AccessTools.FieldRef<NPlayerHand, int> DraggedHolderIndexRef =
        AccessTools.FieldRefAccess<NPlayerHand, int>("_draggedHolderIndex");

    private static readonly AccessTools.FieldRef<NPlayerHand, Dictionary<NHandCardHolder, int>> HoldersAwaitingQueueRef =
        AccessTools.FieldRefAccess<NPlayerHand, Dictionary<NHandCardHolder, int>>("_holdersAwaitingQueue");

    private static readonly MethodInfo RefreshClickableFocusMethod =
        AccessTools.Method(typeof(NClickableControl), "RefreshFocus");

    private static readonly MethodInfo ForceHolderUnfocusMethod =
        AccessTools.Method(typeof(NPlayerHand), "OnHolderUnfocused");

    public static void BeginDrag(NMouseCardPlay cardPlay)
    {
        if (!TryFindHand(cardPlay?.Holder, out NPlayerHand hand))
        {
            return;
        }

        ClearHoverLock(hand, refreshFocus: false);

        NHandCardHolder draggedHolder = cardPlay.Holder;
        int originalIndex = DraggedHolderIndexRef(hand);
        if (draggedHolder == null || originalIndex < 0)
        {
            return;
        }

        float startMouseX = cardPlay.GetViewport()?.GetMousePosition().X ?? draggedHolder.GlobalPosition.X;

        DragStates[hand] = new DragState
        {
            DraggedHolder = draggedHolder,
            OriginalIndex = originalIndex,
            PreviewInsertIndex = originalIndex,
            ShowGap = true,
            PreviousMouseX = startMouseX,
            LastDirection = 0
        };
    }

    public static void ClearDrag(NCardPlay cardPlay)
    {
        if (!TryFindHand(cardPlay?.Holder, out NPlayerHand hand))
        {
            return;
        }

        if (!DragStates.TryGetValue(hand, out DragState dragState))
        {
            return;
        }

        if (!ReferenceEquals(dragState.DraggedHolder, cardPlay.Holder))
        {
            return;
        }

        DragStates.Remove(hand);
    }

    public static void LockReturnedHolder(NHandCardHolder holder)
    {
        if (!TryFindHand(holder, out NPlayerHand hand))
        {
            return;
        }

        ClearHoverLock(hand, refreshFocus: false);

        ulong token = ++_hoverLockTokenCounter;
        HoverLocks[hand] = new HoverLockState
        {
            Holder = holder,
            Token = token,
            LockAllHolders = true
        };

        ForceAllHoldersUnfocus(hand);
        UnlockHoverLaterAsync(hand, holder, token);
    }

    public static void OnOtherHolderFocused(NPlayerHand hand, NHandCardHolder focusedHolder)
    {
        if (!HoverLocks.TryGetValue(hand, out HoverLockState hoverLock))
        {
            return;
        }

        if (ReferenceEquals(hoverLock.Holder, focusedHolder))
        {
            return;
        }

        ClearHoverLock(hand, refreshFocus: true);
    }

    public static bool IsHoverLocked(NClickableControl clickable)
    {
        if (!TryFindHolder(clickable, out NHandCardHolder holder))
        {
            return false;
        }

        if (!TryFindHand(holder, out NPlayerHand hand))
        {
            return false;
        }

        if (!HoverLocks.TryGetValue(hand, out HoverLockState hoverLock))
        {
            return false;
        }

        if (hoverLock.LockAllHolders)
        {
            return true;
        }

        if (!GodotObject.IsInstanceValid(hoverLock.Holder))
        {
            HoverLocks.Remove(hand);
            return false;
        }

        return ReferenceEquals(hoverLock.Holder, holder);
    }

    public static bool TryHandleMouseInput(NMouseCardPlay cardPlay, InputEvent inputEvent)
    {
        if (!TryFindHand(cardPlay?.Holder, out NPlayerHand hand))
        {
            return false;
        }

        if (!DragStates.TryGetValue(hand, out DragState dragState))
        {
            return false;
        }

        if (!ReferenceEquals(dragState.DraggedHolder, cardPlay.Holder))
        {
            return false;
        }

        if (inputEvent is InputEventMouseMotion)
        {
            UpdatePreviewFromCursor(hand, cardPlay, dragState);
            return false;
        }

        if (inputEvent is not InputEventMouseButton mouseButton)
        {
            return false;
        }

        bool inHandArea = IsCursorInHandArea(cardPlay);
        bool wasLeftMouseDown = IsLeftMouseDownRef(cardPlay);
        float mouseX = GetMouseX(cardPlay, dragState);
        bool movingRight = GetCurrentDirection(mouseX, dragState);

        if (mouseButton.ButtonIndex == MouseButton.Right && mouseButton.IsPressed() && inHandArea)
        {
            dragState.ShowGap = true;
            dragState.PreviewInsertIndex = CalculateInsertIndex(hand.ActiveHolders, mouseX, movingRight);
            CommitPreviewIndex(hand, dragState);
            dragState.PreviousMouseX = mouseX;
            cardPlay.GetViewport()?.SetInputAsHandled();
            cardPlay.CancelPlayCard();
            return true;
        }

        if (mouseButton.ButtonIndex == MouseButton.Left && mouseButton.IsPressed() && !wasLeftMouseDown && inHandArea)
        {
            dragState.ShowGap = true;
            dragState.PreviewInsertIndex = CalculateInsertIndex(hand.ActiveHolders, mouseX, movingRight);
            CommitPreviewIndex(hand, dragState);
            dragState.PreviousMouseX = mouseX;
            cardPlay.GetViewport()?.SetInputAsHandled();
            cardPlay.CancelPlayCard();
            return true;
        }

        if (mouseButton.ButtonIndex == MouseButton.Left && mouseButton.IsReleased() && wasLeftMouseDown && inHandArea)
        {
            int targetIndex = CalculateInsertIndex(hand.ActiveHolders, mouseX, movingRight);
            dragState.ShowGap = true;
            dragState.PreviewInsertIndex = targetIndex;
            dragState.PreviousMouseX = mouseX;

            if (targetIndex != dragState.OriginalIndex)
            {
                CommitPreviewIndex(hand, dragState);
                cardPlay.GetViewport()?.SetInputAsHandled();
                cardPlay.CancelPlayCard();
                return true;
            }
        }

        return false;
    }

    public static bool TryApplyPreviewLayout(NPlayerHand hand)
    {
        if (!DragStates.TryGetValue(hand, out DragState dragState))
        {
            return false;
        }

        if (!dragState.ShowGap)
        {
            return false;
        }

        IReadOnlyList<NHandCardHolder> holders = hand.ActiveHolders;
        int count = holders.Count;
        if (count <= 0)
        {
            return false;
        }

        int totalSlots = count + 1;
        int insertIndex = Mathf.Clamp(dragState.PreviewInsertIndex, 0, count);
        Vector2 scale = HandPosHelper.GetScale(totalSlots);

        float extraGapWidth = GetExtraGapWidth(totalSlots, insertIndex);

        for (int i = 0; i < count; i++)
        {
            NHandCardHolder holder = holders[i];
            int visualSlot = i >= insertIndex ? i + 1 : i;

            Vector2 position = HandPosHelper.GetPosition(totalSlots, visualSlot);
            position.X += GetGapOffsetX(extraGapWidth, insertIndex, count, visualSlot);

            holder.SetTargetPosition(position);
            holder.SetTargetScale(scale);
            holder.SetTargetAngle(HandPosHelper.GetAngle(totalSlots, visualSlot));
            holder.Hitbox.MouseFilter = Control.MouseFilterEnum.Ignore;

            NodePath leftPath = i > 0 ? holders[i - 1].GetPath() : holders[count - 1].GetPath();
            NodePath rightPath = i < count - 1 ? holders[i + 1].GetPath() : holders[0].GetPath();

            holder.FocusNeighborLeft = leftPath;
            holder.FocusNeighborRight = rightPath;
            holder.FocusNeighborBottom = holder.GetPath();
            holder.SetIndexLabel(visualSlot + 1);
        }

        if (GodotObject.IsInstanceValid(dragState.DraggedHolder))
        {
            dragState.DraggedHolder.SetIndexLabel(insertIndex + 1);
        }

        return true;
    }

    private static void UpdatePreviewFromCursor(NPlayerHand hand, NMouseCardPlay cardPlay, DragState dragState)
    {
        bool showGap = IsCursorInHandArea(cardPlay);
        int previewInsertIndex = dragState.OriginalIndex;
        float mouseX = GetMouseX(cardPlay, dragState);

        if (showGap)
        {
            bool movingRight = GetCurrentDirection(mouseX, dragState);
            previewInsertIndex = CalculateInsertIndex(hand.ActiveHolders, mouseX, movingRight);
        }

        dragState.PreviousMouseX = mouseX;

        if (dragState.ShowGap == showGap && dragState.PreviewInsertIndex == previewInsertIndex)
        {
            return;
        }

        dragState.ShowGap = showGap;
        dragState.PreviewInsertIndex = previewInsertIndex;
        hand.ForceRefreshCardIndices();
    }

    private static void CommitPreviewIndex(NPlayerHand hand, DragState dragState)
    {
        Dictionary<NHandCardHolder, int> holdersAwaitingQueue = HoldersAwaitingQueueRef(hand);
        if (!holdersAwaitingQueue.ContainsKey(dragState.DraggedHolder))
        {
            return;
        }

        holdersAwaitingQueue[dragState.DraggedHolder] = dragState.PreviewInsertIndex;
    }

    private static float GetMouseX(NMouseCardPlay cardPlay, DragState dragState)
    {
        return cardPlay.GetViewport()?.GetMousePosition().X ?? dragState.DraggedHolder.GlobalPosition.X;
    }

    private static bool GetCurrentDirection(float mouseX, DragState dragState)
    {
        float deltaX = mouseX - dragState.PreviousMouseX;

        if (Mathf.Abs(deltaX) <= HandVisualReorderConfig.DirectionDeadzone)
        {
            if (dragState.LastDirection == 0)
            {
                return true;
            }

            return dragState.LastDirection > 0;
        }

        bool movingRight = deltaX > 0f;
        dragState.LastDirection = movingRight ? 1 : -1;
        return movingRight;
    }

    private static int CalculateInsertIndex(IReadOnlyList<NHandCardHolder> holders, float mouseX, bool movingRight)
    {
        if (holders.Count == 0)
        {
            return 0;
        }

        if (movingRight)
        {
            int insertIndex = 0;

            for (int i = 0; i < holders.Count; i++)
            {
                float thresholdX = GetCardThresholdX(holders[i]);
                if (mouseX >= thresholdX)
                {
                    insertIndex = i + 1;
                }
                else
                {
                    break;
                }
            }

            return insertIndex;
        }

        if (HandVisualReorderConfig.UseNextCardThresholdWhenMovingLeft)
        {
            if (mouseX < GetCardThresholdX(holders[0]))
            {
                return 0;
            }

            for (int i = 0; i < holders.Count - 1; i++)
            {
                float thresholdX = GetCardThresholdX(holders[i + 1]);
                if (mouseX < thresholdX)
                {
                    return i;
                }
            }

            return holders.Count;
        }
        else
        {
            int insertIndex = 0;

            for (int i = 0; i < holders.Count; i++)
            {
                float thresholdX = GetCardThresholdX(holders[i]) - HandVisualReorderConfig.MovingLeftThresholdOffset;
                if (mouseX >= thresholdX)
                {
                    insertIndex = i + 1;
                }
                else
                {
                    break;
                }
            }

            return insertIndex;
        }
    }

    private static float GetCardThresholdX(NHandCardHolder holder)
    {
        return holder.GlobalPosition.X + HandVisualReorderConfig.CardPositionXOffset;
    }

    private static float GetExtraGapWidth(int totalSlots, int insertIndex)
    {
        float gapUnits = Mathf.Max(1f, HandVisualReorderConfig.GapSizeUnits);
        if (gapUnits <= 1f)
        {
            return 0f;
        }

        float slotWidth = GetBaseSlotWidth(totalSlots, insertIndex);
        return slotWidth * (gapUnits - 1f);
    }

    private static float GetBaseSlotWidth(int totalSlots, int insertIndex)
    {
        if (totalSlots <= 1)
        {
            return 160f;
        }

        if (insertIndex <= 0)
        {
            return Mathf.Abs(HandPosHelper.GetPosition(totalSlots, 1).X - HandPosHelper.GetPosition(totalSlots, 0).X);
        }

        if (insertIndex >= totalSlots - 1)
        {
            return Mathf.Abs(HandPosHelper.GetPosition(totalSlots, totalSlots - 1).X - HandPosHelper.GetPosition(totalSlots, totalSlots - 2).X);
        }

        float leftWidth = Mathf.Abs(HandPosHelper.GetPosition(totalSlots, insertIndex).X - HandPosHelper.GetPosition(totalSlots, insertIndex - 1).X);
        float rightWidth = Mathf.Abs(HandPosHelper.GetPosition(totalSlots, insertIndex + 1).X - HandPosHelper.GetPosition(totalSlots, insertIndex).X);
        return (leftWidth + rightWidth) * 0.5f;
    }

    private static float GetGapOffsetX(float extraGapWidth, int insertIndex, int holderCount, int visualSlot)
    {
        if (extraGapWidth <= 0f)
        {
            return 0f;
        }

        if (insertIndex <= 0)
        {
            return extraGapWidth;
        }

        if (insertIndex >= holderCount)
        {
            return -extraGapWidth;
        }

        return visualSlot < insertIndex ? -extraGapWidth * 0.5f : extraGapWidth * 0.5f;
    }

    private static bool IsCursorInHandArea(NMouseCardPlay cardPlay)
    {
        Viewport viewport = cardPlay.GetViewport();
        if (viewport == null)
        {
            return false;
        }

        float baseThreshold = viewport.GetVisibleRect().Size.Y * 0.75f;
        float dragStartY = DragStartYRef(cardPlay);

        float playZoneThreshold;
        if (SkipStartDragRef(cardPlay))
        {
            playZoneThreshold = baseThreshold + 100f;
        }
        else if (dragStartY > baseThreshold)
        {
            playZoneThreshold = Mathf.Max(baseThreshold, dragStartY - 100f);
        }
        else
        {
            playZoneThreshold = Mathf.Min(baseThreshold, dragStartY - 50f);
        }

        return viewport.GetMousePosition().Y >= playZoneThreshold;
    }

    private static void ForceHolderUnfocus(NPlayerHand hand, NHandCardHolder holder)
    {
        if (!GodotObject.IsInstanceValid(hand) || !GodotObject.IsInstanceValid(holder))
        {
            return;
        }

        ForceHolderUnfocusMethod?.Invoke(hand, new object[] { holder });
    }
    private static void ForceAllHoldersUnfocus(NPlayerHand hand)
    {
        if (!GodotObject.IsInstanceValid(hand))
        {
            return;
        }

        foreach (NHandCardHolder activeHolder in hand.ActiveHolders)
        {
            if (!GodotObject.IsInstanceValid(activeHolder))
            {
                continue;
            }

            ForceHolderUnfocus(hand, activeHolder);
        }
    }

    private static void ClearHoverLock(NPlayerHand hand, bool refreshFocus)
    {
        if (!HoverLocks.TryGetValue(hand, out HoverLockState hoverLock))
        {
            return;
        }

        HoverLocks.Remove(hand);

        if (refreshFocus && GodotObject.IsInstanceValid(hoverLock.Holder))
        {
            RefreshClickableFocus(hoverLock.Holder.Hitbox);
        }
    }

    private static async void UnlockHoverLaterAsync(NPlayerHand hand, NHandCardHolder holder, ulong token)
    {
        if (!GodotObject.IsInstanceValid(holder))
        {
            return;
        }

        SceneTree tree = holder.GetTree();
        if (tree == null)
        {
            return;
        }

        if (HandVisualReorderConfig.GlobalHoverLockSeconds > 0)
        {
            await holder.ToSignal(tree.CreateTimer(HandVisualReorderConfig.GlobalHoverLockSeconds), SceneTreeTimer.SignalName.Timeout);

            if (!GodotObject.IsInstanceValid(hand))
            {
                return;
            }

            if (!HoverLocks.TryGetValue(hand, out HoverLockState globalHoverLock))
            {
                return;
            }

            if (globalHoverLock.Token != token)
            {
                return;
            }

            HoverLocks[hand] = new HoverLockState
            {
                Holder = holder,
                Token = token,
                LockAllHolders = false
            };

            if (GodotObject.IsInstanceValid(holder))
            {
                ForceHolderUnfocus(hand, holder);
            }
        }

        double remainingLockSeconds = HandVisualReorderConfig.HoverLockSeconds - HandVisualReorderConfig.GlobalHoverLockSeconds;
        if (remainingLockSeconds > 0)
        {
            await holder.ToSignal(tree.CreateTimer(remainingLockSeconds), SceneTreeTimer.SignalName.Timeout);

            if (!GodotObject.IsInstanceValid(hand))
            {
                return;
            }

            if (!HoverLocks.TryGetValue(hand, out HoverLockState finalHoverLock))
            {
                return;
            }

            if (finalHoverLock.Token != token)
            {
                return;
            }
        }

        ClearHoverLock(hand, refreshFocus: true);
    }

    private static void RefreshClickableFocus(NClickableControl clickable)
    {
        if (clickable == null || !GodotObject.IsInstanceValid(clickable))
        {
            return;
        }

        RefreshClickableFocusMethod?.Invoke(clickable, null);
    }

    private static bool TryFindHand(Node node, out NPlayerHand hand)
    {
        hand = null;
        Node current = node;

        while (current != null)
        {
            if (current is NPlayerHand foundHand)
            {
                hand = foundHand;
                return GodotObject.IsInstanceValid(hand);
            }

            current = current.GetParent();
        }

        return false;
    }

    private static bool TryFindHolder(Node node, out NHandCardHolder holder)
    {
        holder = null;
        Node current = node;

        while (current != null)
        {
            if (current is NHandCardHolder foundHolder)
            {
                holder = foundHolder;
                return GodotObject.IsInstanceValid(holder);
            }

            current = current.GetParent();
        }

        return false;
    }
}

[HarmonyPatch(typeof(NMouseCardPlay), nameof(NMouseCardPlay.Start))]
internal static class NMouseCardPlayStartPatch
{
    [HarmonyPostfix]
    private static void Postfix(NMouseCardPlay __instance)
    {
        HandVisualReorderRuntime.BeginDrag(__instance);
    }
}

[HarmonyPatch(typeof(NMouseCardPlay), nameof(NMouseCardPlay._Input))]
internal static class NMouseCardPlayInputPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(NMouseCardPlay __instance, InputEvent inputEvent)
    {
        return !HandVisualReorderRuntime.TryHandleMouseInput(__instance, inputEvent);
    }
}

[HarmonyPatch(typeof(NCardPlay), "Cleanup")]
internal static class NCardPlayCleanupPatch
{
    [HarmonyPrefix]
    private static void Prefix(NCardPlay __instance)
    {
        HandVisualReorderRuntime.ClearDrag(__instance);
    }
}

[HarmonyPatch(typeof(NPlayerHand), "RefreshLayout")]
internal static class NPlayerHandRefreshLayoutPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NPlayerHand __instance)
    {
        return !HandVisualReorderRuntime.TryApplyPreviewLayout(__instance);
    }
}

[HarmonyPatch(typeof(NPlayerHand), "ReturnHolderToHand")]
internal static class NPlayerHandReturnHolderToHandPatch
{
    [HarmonyPostfix]
    private static void Postfix(NHandCardHolder holder)
    {
        HandVisualReorderRuntime.LockReturnedHolder(holder);
    }
}

[HarmonyPatch(typeof(NPlayerHand), "OnHolderFocused")]
internal static class NPlayerHandOnHolderFocusedPatch
{
    [HarmonyPostfix]
    private static void Postfix(NPlayerHand __instance, NHandCardHolder holder)
    {
        HandVisualReorderRuntime.OnOtherHolderFocused(__instance, holder);
    }
}

[HarmonyPatch(typeof(NClickableControl), "RefreshFocus")]
internal static class NClickableControlRefreshFocusPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NClickableControl __instance)
    {
        return !HandVisualReorderRuntime.IsHoverLocked(__instance);
    }
}