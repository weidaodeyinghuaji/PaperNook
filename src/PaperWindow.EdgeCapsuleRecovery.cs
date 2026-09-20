using System.Diagnostics;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    // Presenter apply retry is intentionally bounded at 120 ms. Wait past that window before
    // restoring the last presenter-confirmed frame so transient native/WPF failures still use the
    // normal reconcile path and this remains a terminal safety net rather than a second scheduler.
    private const int EdgeCapsuleApplyFailureRecoveryDelayMilliseconds = 160;

    private DispatcherTimer? _edgeCapsuleApplyFailureRecoveryTimer;
    private bool _edgeCapsuleApplyFailureRecoveryRunning;

    private void ScheduleEdgeCapsuleApplyFailureRecovery()
    {
        if (_windowLifecycle != PaperWindowLifecycleState.Alive || IsClosed)
        {
            return;
        }

        if (_edgeCapsuleApplyFailureRecoveryTimer == null)
        {
            var dispatcher = _edgeCapsuleHost?.Dispatcher ?? Dispatcher;
            _edgeCapsuleApplyFailureRecoveryTimer =
                new DispatcherTimer(DispatcherPriority.Send, dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(
                        EdgeCapsuleApplyFailureRecoveryDelayMilliseconds)
                };
            _edgeCapsuleApplyFailureRecoveryTimer.Tick +=
                OnEdgeCapsuleApplyFailureRecoveryTimerTick;
        }

        RestartEdgeCapsuleApplyFailureRecoveryTimer();
    }

    private void RestartEdgeCapsuleApplyFailureRecoveryTimer()
    {
        var timer = _edgeCapsuleApplyFailureRecoveryTimer;
        if (timer == null)
        {
            return;
        }

        timer.Stop();
        timer.Interval = TimeSpan.FromMilliseconds(
            EdgeCapsuleApplyFailureRecoveryDelayMilliseconds);
        timer.Start();
    }

    private void OnEdgeCapsuleApplyFailureRecoveryTimerTick(
        object? sender,
        EventArgs e)
    {
        _edgeCapsuleApplyFailureRecoveryTimer?.Stop();
        if (_edgeCapsuleApplyFailureRecoveryRunning ||
            _windowLifecycle != PaperWindowLifecycleState.Alive ||
            IsClosed ||
            _edgeCapsuleHost is not { } host)
        {
            return;
        }

        var authority = CurrentEdgeCapsuleVisualAuthority;
        if (authority == EdgeCapsuleVisualAuthority.QueueTranslation)
        {
            // The compositor still covers this HWND. Let its bounded handoff finish before touching
            // the real host; the source must keep the same capacity for the entire translation.
            RestartEdgeCapsuleApplyFailureRecoveryTimer();
            return;
        }
        if (authority != EdgeCapsuleVisualAuthority.RealDocked)
        {
            // Floating/docking overlap currently owns visible pixels. Its normal terminal handoff
            // will either restore the docked host or schedule a fresh recovery on apply failure.
            return;
        }

        // Hidden/retracted/suppressed surfaces have another visual authority by design. The safety
        // net is only for a real docked capsule that should still be visible after apply retries.
        var confirmed = _edgeCapsule.AppliedPresentation;
        if (!confirmed.Visible ||
            confirmed.Surface is not (
                EdgeCapsuleSurfaceKind.DockedResting or
                EdgeCapsuleSurfaceKind.DockedHovered or
                EdgeCapsuleSurfaceKind.DockedActive or
                EdgeCapsuleSurfaceKind.DockedPreview) ||
            host.MatchesPresentation(confirmed))
        {
            return;
        }

        _edgeCapsuleApplyFailureRecoveryRunning = true;
        try
        {
            if (host.Apply(confirmed))
            {
                // Re-arm the presenter's bounded retry budget for the next real invalidation. The
                // restored frame is already the presenter's last committed frame, so no state is
                // rewritten and no additional reconcile is required here.
                _edgeCapsule.ForceApplyCurrentPresentation();
#if DEBUG
                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"host.recovery paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                    $"outcome=restored surface={confirmed.Surface}");
#endif
                return;
            }
        }
        finally
        {
            _edgeCapsuleApplyFailureRecoveryRunning = false;
        }

        if (CurrentEdgeCapsuleVisualAuthority !=
            EdgeCapsuleVisualAuthority.RealDocked)
        {
            // Host.Apply can synchronously dispatch native messages. If a new authority appeared
            // while it was failing, do not tear that owner down from the recovery callback.
            if (CurrentEdgeCapsuleVisualAuthority ==
                EdgeCapsuleVisualAuthority.QueueTranslation)
            {
                RestartEdgeCapsuleApplyFailureRecoveryTimer();
            }
            return;
        }

        // The real docked HWND could not even restore the last presenter-confirmed frame. Prefer a
        // visible expanded paper over leaving an opacity-zero capsule indefinitely.
        Trace.TraceWarning(
            "Edge capsule terminal apply recovery failed. Paper={0}; Surface={1}. " +
            "Restoring the expanded paper surface.",
            _paper.Id,
            confirmed.Surface);
        RestoreFromCapsuleAfterEligibilityLoss();
    }
}
