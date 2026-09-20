namespace PaperTodo;

internal static class EdgeCapsuleNativeTransactionPolicy
{
    public static bool RequiresCrossQueueGroup(
        IEnumerable<string> queueKeys) =>
        queueKeys
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Count() > 1;

    public static bool ParticipatesInBatchOutcome(
        long transactionGroupId,
        bool applyAttempted,
        bool retryWasPending,
        bool deferred) =>
        transactionGroupId > 0 ||
        applyAttempted ||
        retryWasPending ||
        deferred;

    public static bool CanRelease(
        long transactionGroupId,
        bool transitionActive,
        bool retryPending,
        bool applyActive,
        bool hasPresentationWork) =>
        transactionGroupId > 0 &&
        !transitionActive &&
        !retryPending &&
        !applyActive &&
        !hasPresentationWork;

    // EndDeferWindowPos may synchronously dispatch WPF resize/render messages while a controller-
    // owned visual transaction still owns the Presenter's native apply state. A shared render
    // callback must not open a second native apply over that state; it simply waits for the outer
    // transaction to complete and consumes the next composition frame.
    public static bool ShouldDeferSharedFrameForNativeApply(
        bool nativeBatchApplyActive) =>
        nativeBatchApplyActive;
}
