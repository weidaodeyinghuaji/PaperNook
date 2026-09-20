using System.Diagnostics;

namespace PaperTodo;

public sealed partial class AppController
{
    // Boundary confirmation is transport-level wake state only. The existing corridor intent and
    // its single owner-local timer remain the only preview-exit timing authority.
    private string? _edgeCapsulePreviewBoundaryLeaveOwnerPaperId;
    private int _edgeCapsulePreviewBoundaryLeaveTransferGeneration;

    /// <summary>
    /// A verified native hit/move inside committed InteractiveBounds is fresh physical activity and
    /// may revoke a pending WM_MOUSELEAVE confirmation. Routed WPF enter/leave and reconcile samples
    /// never call this path.
    /// </summary>
    internal void NotifyEdgeCapsulePreviewHostPointerActivity()
    {
        var ownerPaperId = _edgeCapsulePreviewBoundaryLeaveOwnerPaperId;
        if (ownerPaperId == null)
        {
            return;
        }

        TraceEdgeCapsulePreview(
            $"boundary confirm cancelled owner={EdgeCapsulePreviewTraceId(ownerPaperId)} " +
            "reason=verified-host-activity");
        ClearEdgeCapsulePreviewHostBoundaryLeaveConfirmation();
        ResetEdgeCapsulePreviewCorridorExitIntent();
        ForgetEdgeCapsulePreviewPointerResolution();
    }

    /// <summary>
    /// WM_MOUSELEAVE can be delivered while the cursor still resolves inside the last committed
    /// InteractiveBounds. The following pixels may already be HTTRANSPARENT, so no later HWND move
    /// is guaranteed. Seed the existing corridor watcher only for that ambiguity and hand every
    /// actual owner/target/corridor/outside decision back to the canonical owner arbiter.
    /// </summary>
    internal void NotifyEdgeCapsulePreviewHostBoundaryLeave(
        PaperWindow inputWindow,
        DeviceScreenPoint? pointer)
    {
        ClearEdgeCapsulePreviewHostBoundaryLeaveConfirmation();
        NotifyEdgeCapsulePreviewPhysicalPointer(inputWindow, pointer);

        if (IsExiting ||
            !pointer.HasValue ||
            _edgeCapsulePreviewSession is not { } session ||
            !_windows.TryGetValue(session.OwnerPaperId, out var owner) ||
            !owner.CanEnterEdgeCapsulePreview ||
            owner.EdgeCapsulePreviewPointerCaptureActive ||
            !AllowsEdgeCapsuleQueueProxyOwnership(session.QueueKey))
        {
            return;
        }

        var resolution = ResolveEdgeCapsulePreviewPointer(
            session,
            pointer.Value);
        if (!resolution.OwnerContains)
        {
            // The canonical arbiter already started transfer/corridor/hard-close work.
            return;
        }

        var now = Stopwatch.GetTimestamp();
        _edgeCapsulePreviewBoundaryLeaveOwnerPaperId =
            session.OwnerPaperId;
        _edgeCapsulePreviewBoundaryLeaveTransferGeneration =
            _edgeCapsulePreviewTransferGeneration;
        _edgeCapsulePreviewCorridorExitIntent =
            new EdgeCapsulePreviewCorridorExitIntent(
                session.OwnerPaperId,
                now,
                null,
                false);
        ForgetEdgeCapsulePreviewPointerResolution();
        TraceEdgeCapsulePreview(
            $"boundary confirm armed owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
            $"pointer={pointer.Value.X},{pointer.Value.Y}");
        ScheduleEdgeCapsulePreviewCorridorIntentCheck(
            owner,
            now,
            retryAfterCursorReadFailure: true);
        EnsureEdgeCapsulePreviewBoundaryLeaveCorridorContinuation();
    }

    /// <summary>
    /// Passive WPF/reconcile samples are allowed to resolve a boundary confirmation, but while they
    /// still report the owner they must not cancel the already-armed native leave watcher. Verified
    /// native activity cancels earlier through NotifyEdgeCapsulePreviewHostPointerActivity.
    /// </summary>
    private bool PreserveEdgeCapsulePreviewHostBoundaryLeave(
        EdgeCapsulePreviewLayoutSession session,
        DeviceScreenPoint? pointer)
    {
        var expectedOwnerPaperId =
            _edgeCapsulePreviewBoundaryLeaveOwnerPaperId;
        if (expectedOwnerPaperId == null)
        {
            return false;
        }

        if (_edgeCapsulePreviewBoundaryLeaveTransferGeneration !=
                _edgeCapsulePreviewTransferGeneration ||
            !string.Equals(
                session.OwnerPaperId,
                expectedOwnerPaperId,
                StringComparison.Ordinal))
        {
            ClearEdgeCapsulePreviewHostBoundaryLeaveConfirmation();
            return false;
        }

        if (!pointer.HasValue)
        {
            ForgetEdgeCapsulePreviewPointerResolution();
            return true;
        }

        var resolution = ResolveEdgeCapsulePreviewPointer(
            session,
            pointer.Value);
        if (resolution.OwnerContains)
        {
            ForgetEdgeCapsulePreviewPointerResolution();
            return true;
        }

        var now = Stopwatch.GetTimestamp();
        ClearEdgeCapsulePreviewHostBoundaryLeaveConfirmation();
        _edgeCapsulePreviewCorridorExitIntent =
            new EdgeCapsulePreviewCorridorExitIntent(
                session.OwnerPaperId,
                now,
                null,
                false);
        ForgetEdgeCapsulePreviewPointerResolution();
        TraceEdgeCapsulePreview(
            $"boundary confirm resolved owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
            $"pointer={pointer.Value.X},{pointer.Value.Y} " +
            $"target={EdgeCapsulePreviewTraceId(resolution.Target?.EdgeCapsulePreviewPaperId)} " +
            $"transferContains={resolution.TransferRectangleContains} source=sample");
        return false;
    }

    private void EnsureEdgeCapsulePreviewBoundaryLeaveCorridorContinuation()
    {
        var timer = _edgeCapsulePreviewCorridorIntentTimer;
        if (timer == null)
        {
            return;
        }

        // The normal corridor handler was attached when the timer was created. Re-attach this
        // continuation last so it can restore the same watcher only if the normal owner arbiter saw
        // a still-inside sample or a transient cursor-read failure.
        timer.Tick -= OnEdgeCapsulePreviewBoundaryLeaveCorridorTimerTick;
        timer.Tick += OnEdgeCapsulePreviewBoundaryLeaveCorridorTimerTick;
    }

    private void OnEdgeCapsulePreviewBoundaryLeaveCorridorTimerTick(
        object? sender,
        EventArgs e)
    {
        var expectedOwnerPaperId =
            _edgeCapsulePreviewBoundaryLeaveOwnerPaperId;
        if (expectedOwnerPaperId == null)
        {
            return;
        }

        if (IsExiting ||
            _edgeCapsulePreviewBoundaryLeaveTransferGeneration !=
                _edgeCapsulePreviewTransferGeneration ||
            _edgeCapsulePreviewSession is not { } session ||
            !string.Equals(
                session.OwnerPaperId,
                expectedOwnerPaperId,
                StringComparison.Ordinal) ||
            !_windows.TryGetValue(session.OwnerPaperId, out var owner) ||
            !AllowsEdgeCapsuleQueueProxyOwnership(session.QueueKey))
        {
            ClearEdgeCapsulePreviewHostBoundaryLeaveConfirmation();
            return;
        }

        // The normal corridor handler has already converted capture into its established paused
        // state. Boundary confirmation no longer owns anything once capture is active.
        if (!owner.CanEnterEdgeCapsulePreview ||
            owner.EdgeCapsulePreviewPointerCaptureActive)
        {
            ClearEdgeCapsulePreviewHostBoundaryLeaveConfirmation();
            return;
        }

        if (!WindowNative.TryGetCursorScreenPosition(out var pointer))
        {
            // The normal corridor handler may have started its no-target clock from this failed
            // sample. A boundary confirmation has no positive exit evidence yet, so revoke any
            // close it queued, restart the shared watcher, and wait for a readable physical point.
            CancelQueuedEdgeCapsulePreviewClose();
            var now = Stopwatch.GetTimestamp();
            _edgeCapsulePreviewCorridorExitIntent =
                new EdgeCapsulePreviewCorridorExitIntent(
                    session.OwnerPaperId,
                    now,
                    null,
                    false);
            ForgetEdgeCapsulePreviewPointerResolution();
            ScheduleEdgeCapsulePreviewCorridorIntentCheck(
                owner,
                now,
                retryAfterCursorReadFailure: true);
            EnsureEdgeCapsulePreviewBoundaryLeaveCorridorContinuation();
            return;
        }

        var resolution = ResolveEdgeCapsulePreviewPointer(
            session,
            pointer);
        if (resolution.OwnerContains)
        {
            // The normal handler just treated this as an ordinary owner sample and therefore reset
            // the corridor intent. Restore the same single watcher without starting a second timer.
            CancelQueuedEdgeCapsulePreviewClose();
            var now = Stopwatch.GetTimestamp();
            _edgeCapsulePreviewCorridorExitIntent =
                new EdgeCapsulePreviewCorridorExitIntent(
                    session.OwnerPaperId,
                    now,
                    null,
                    false);
            ForgetEdgeCapsulePreviewPointerResolution();
            ScheduleEdgeCapsulePreviewCorridorIntentCheck(
                owner,
                now,
                retryAfterCursorReadFailure: true);
            EnsureEdgeCapsulePreviewBoundaryLeaveCorridorContinuation();
            return;
        }

        // The normal handler already routed this readable outside-owner sample through the canonical
        // owner arbiter. Only retire the transport-level confirmation marker; preserve any corridor
        // or transfer intent that arbiter just established.
        TraceEdgeCapsulePreview(
            $"boundary confirm resolved owner={EdgeCapsulePreviewTraceId(session.OwnerPaperId)} " +
            $"pointer={pointer.X},{pointer.Y} " +
            $"target={EdgeCapsulePreviewTraceId(resolution.Target?.EdgeCapsulePreviewPaperId)} " +
            $"transferContains={resolution.TransferRectangleContains} source=timer");
        ClearEdgeCapsulePreviewHostBoundaryLeaveConfirmation();
    }

    private void ClearEdgeCapsulePreviewHostBoundaryLeaveConfirmation()
    {
        _edgeCapsulePreviewBoundaryLeaveOwnerPaperId = null;
    }
}
