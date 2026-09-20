namespace PaperTodo;

internal enum EdgeCapsulePreviewExitPolicyDecision
{
    ImmediateClose,
    PredictiveWait,
    FixedWait
}

internal enum EdgeCapsulePreviewCloseReason
{
    OutsideTransferRectangle,
    NoTargetIntent
}

/// <summary>
/// Orders the two edge-preview boundaries. The queue's empty transfer rectangle is a temporary
/// buffer; its outer edge is absolute and cannot be vetoed by hover-intent prediction.
/// </summary>
internal static class EdgeCapsulePreviewExitPolicy
{
    public const double FixedWaitMilliseconds = 1000;

    public static EdgeCapsulePreviewExitPolicyDecision Resolve(
        bool transferRectangleContains,
        bool predictiveIntentEnabled)
    {
        if (!transferRectangleContains)
        {
            return EdgeCapsulePreviewExitPolicyDecision.ImmediateClose;
        }

        return predictiveIntentEnabled
            ? EdgeCapsulePreviewExitPolicyDecision.PredictiveWait
            : EdgeCapsulePreviewExitPolicyDecision.FixedWait;
    }

    public static EdgeCapsulePreviewCloseReason StrongerCloseReason(
        EdgeCapsulePreviewCloseReason current,
        EdgeCapsulePreviewCloseReason incoming) =>
        current == EdgeCapsulePreviewCloseReason.OutsideTransferRectangle ||
        incoming == EdgeCapsulePreviewCloseReason.OutsideTransferRectangle
            ? EdgeCapsulePreviewCloseReason.OutsideTransferRectangle
            : EdgeCapsulePreviewCloseReason.NoTargetIntent;

    public static bool EmptyRegionCanCancelQueuedClose(
        EdgeCapsulePreviewCloseReason reason,
        bool predictiveIntentEnabled,
        bool hasTargetIntent) =>
        reason == EdgeCapsulePreviewCloseReason.NoTargetIntent &&
        predictiveIntentEnabled &&
        hasTargetIntent;
}
