using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class AppController
{
    // Stability requires both real observations and elapsed time. This avoids treating the first
    // frame after a render stall as stable, without making lifetime depend on refresh rate.
    private const int ExperimentalFollowMinimumStableSamples = 3;
    private static readonly TimeSpan ExperimentalFollowStableDuration =
        TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ExperimentalFollowMovingIdleDuration =
        TimeSpan.FromSeconds(2);
    private ExternalWindowTracker? _externalWindowTracker;
    private readonly HashSet<IntPtr> _externalMoveSizeWindows = new();
    private bool _experimentalFollowRendering;
    private long _experimentalFollowLastChangeTimestamp;
    private int _experimentalFollowStableSamples;

    private bool NeedsExternalWindowTracker =>
        _windows.Values.Any(window =>
            window.HasExperimentalExternalWindowAttachment);

    internal void NotifyExperimentalWindowAttachmentChanged()
    {
        if (!IsExiting)
        {
            RefreshExperimentalWindowRuntime();
        }
    }

    private void RefreshExperimentalWindowRuntime()
    {
        if (IsExiting)
        {
            DisposeExperimentalWindowRuntime();
            return;
        }

        if (NeedsExternalWindowTracker)
        {
            if (_externalWindowTracker == null)
            {
                var tracker = new ExternalWindowTracker(
                    Application.Current.Dispatcher);
                tracker.Changed += OnExternalWindowChanged;
                _externalWindowTracker = tracker;
            }
            return;
        }

        DisposeExternalWindowTracker();
    }

    private void OnExternalWindowChanged(ExternalWindowEvent windowEvent)
    {
        if (IsExiting || !NeedsExternalWindowTracker)
        {
            return;
        }

        if ((windowEvent.Kind &
             ExternalWindowEventKind.MoveSizeStarted) != 0)
        {
            _externalMoveSizeWindows.Add(windowEvent.Handle);
        }
        if ((windowEvent.Kind &
             (ExternalWindowEventKind.MoveSizeEnded |
              ExternalWindowEventKind.Destroyed)) != 0)
        {
            _externalMoveSizeWindows.Remove(windowEvent.Handle);
        }

        var followsEventWindow = false;
        foreach (var window in _windows.Values.ToList())
        {
            followsEventWindow |=
                window.TracksExperimentalExternalWindow(
                    windowEvent.Handle);
            window.HandleExternalWindowEvent(windowEvent);
        }

        if (followsEventWindow &&
            (windowEvent.Kind &
             (ExternalWindowEventKind.Location |
              ExternalWindowEventKind.MoveSizeStarted |
              ExternalWindowEventKind.MoveSizeEnded |
              ExternalWindowEventKind.MinimizeEnded |
              ExternalWindowEventKind.Uncloaked)) != 0)
        {
            BeginExperimentalFollowFrames();
        }
    }

    private void BeginExperimentalFollowFrames()
    {
        _experimentalFollowLastChangeTimestamp = Stopwatch.GetTimestamp();
        _experimentalFollowStableSamples = 0;
        if (_experimentalFollowRendering ||
            IsExiting ||
            !NeedsExternalWindowTracker)
        {
            return;
        }

        CompositionTarget.Rendering +=
            OnExperimentalFollowRendering;
        _experimentalFollowRendering = true;
    }

    internal void RequestExperimentalWindowFrames()
    {
        if (NeedsExternalWindowTracker)
        {
            BeginExperimentalFollowFrames();
        }
    }

    private void OnExperimentalFollowRendering(
        object? sender,
        EventArgs e)
    {
        if (IsExiting || !NeedsExternalWindowTracker)
        {
            StopExperimentalFollowFrames();
            return;
        }

        var hasAttachment = false;
        var targetMoving = false;
        var changed = false;
        foreach (var window in _windows.Values.ToList())
        {
            if (!window.RefreshExperimentalAttachmentFrame(
                    out var targetHandle,
                    out var windowChanged))
            {
                continue;
            }

            hasAttachment = true;
            changed |= windowChanged;
            targetMoving |=
                _externalMoveSizeWindows.Contains(targetHandle);
        }

        if (!hasAttachment)
        {
            StopExperimentalFollowFrames();
            return;
        }

        if (changed)
        {
            _experimentalFollowLastChangeTimestamp =
                Stopwatch.GetTimestamp();
            _experimentalFollowStableSamples = 0;
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (_experimentalFollowLastChangeTimestamp == 0)
        {
            _experimentalFollowLastChangeTimestamp = now;
            return;
        }

        if (_experimentalFollowStableSamples < int.MaxValue)
        {
            _experimentalFollowStableSamples++;
        }
        var idleDuration = targetMoving
            ? ExperimentalFollowMovingIdleDuration
            : ExperimentalFollowStableDuration;
        var idleElapsed = TimeSpan.FromSeconds(
            Math.Max(
                0,
                now - _experimentalFollowLastChangeTimestamp) /
            (double)Stopwatch.Frequency);
        if (_experimentalFollowStableSamples >=
                ExperimentalFollowMinimumStableSamples &&
            idleElapsed >= idleDuration)
        {
            // A missed MOVESIZEEND must not leave a render-rate Win32 sampler alive forever.
            // A later location event starts it again if the target resumes moving.
            if (targetMoving)
            {
                _externalMoveSizeWindows.Clear();
            }
            StopExperimentalFollowFrames();
        }
    }

    private void StopExperimentalFollowFrames()
    {
        if (_experimentalFollowRendering)
        {
            CompositionTarget.Rendering -=
                OnExperimentalFollowRendering;
            _experimentalFollowRendering = false;
        }
        _experimentalFollowLastChangeTimestamp = 0;
        _experimentalFollowStableSamples = 0;
    }

    private void ToggleExperimentalCapsuleMagnetism()
    {
        State.ExperimentalCapsuleMagnetism =
            !State.ExperimentalCapsuleMagnetism;
        if (!State.ExperimentalCapsuleMagnetism)
        {
            foreach (var window in _windows.Values.ToList())
            {
                window.DisableExperimentalCapsuleMagnet();
            }
        }

        SaveNow();
        RefreshExperimentalWindowRuntime();
        RefreshSettingsRegions("labs.window");
    }

    private void ToggleExperimentalCapsuleMagnetScreenEdges()
    {
        State.ExperimentalCapsuleMagnetScreenEdges =
            !State.ExperimentalCapsuleMagnetScreenEdges;
        foreach (var window in _windows.Values.ToList())
        {
            window.DisableExperimentalCapsuleMagnet();
        }
        SaveNow();
        RefreshSettingsRegions("labs.window");
    }

    private void ToggleExperimentalCapsuleMagnetWindowEdges()
    {
        State.ExperimentalCapsuleMagnetWindowEdges =
            !State.ExperimentalCapsuleMagnetWindowEdges;
        foreach (var window in _windows.Values.ToList())
        {
            window.DisableExperimentalCapsuleMagnet();
        }
        SaveNow();
        RefreshExperimentalWindowRuntime();
        RefreshSettingsRegions("labs.window");
    }

    private void SetExperimentalCapsuleMagnetDistance(int distance)
    {
        var normalized =
            ExperimentalWindowAttachmentOptions.NormalizeSnapDistance(
                distance);
        if (State.ExperimentalCapsuleMagnetDistance == normalized)
        {
            return;
        }

        State.ExperimentalCapsuleMagnetDistance = normalized;
        SaveNow();
        RefreshSettingsRegions("labs.window");
    }

    private void ToggleExperimentalWindowTethering()
    {
        State.ExperimentalWindowTethering =
            !State.ExperimentalWindowTethering;
        if (!State.ExperimentalWindowTethering)
        {
            foreach (var window in _windows.Values.ToList())
            {
                window.DisableExperimentalWindowTether();
            }
        }

        SaveNow();
        RefreshExperimentalWindowRuntime();
        RefreshExperimentalAttachmentMenus();
        RefreshSettingsRegions("labs.window");
    }

    private void SetExperimentalWindowTetherPreferredEdge(string edge)
    {
        var normalized = ExperimentalWindowTetherOptions.NormalizeEdge(edge);
        if (State.ExperimentalWindowTetherPreferredEdge == normalized)
        {
            return;
        }

        State.ExperimentalWindowTetherPreferredEdge = normalized;
        foreach (var window in _windows.Values.ToList())
        {
            window.RefreshExperimentalWindowTetherOptions();
        }
        SaveNow();
        RefreshSettingsRegions("labs.window");
    }

    private void SetExperimentalWindowTetherGap(int gap)
    {
        var normalized = ExperimentalWindowTetherOptions.NormalizeGap(gap);
        if (State.ExperimentalWindowTetherGap == normalized)
        {
            return;
        }

        State.ExperimentalWindowTetherGap = normalized;
        foreach (var window in _windows.Values.ToList())
        {
            window.RefreshExperimentalWindowTetherOptions();
        }
        SaveNow();
        RefreshSettingsRegions("labs.window");
    }

    private void ToggleExperimentalTetherVisibilityLink()
    {
        State.ExperimentalTetherVisibilityLink =
            !State.ExperimentalTetherVisibilityLink;
        if (!State.ExperimentalTetherVisibilityLink)
        {
            foreach (var window in _windows.Values.ToList())
            {
                window.DisableExperimentalTetherVisibilityLink();
            }
        }
        else
        {
            foreach (var window in _windows.Values.ToList())
            {
                window.RefreshExperimentalTetherVisibilityOptions();
            }
        }

        SaveNow();
        RefreshSettingsRegions("labs.window");
    }

    private void SetExperimentalTetherMinimizedBehavior(string behavior)
    {
        var normalized =
            ExperimentalTetherVisibilityModes.Normalize(behavior);
        if (State.ExperimentalTetherMinimizedBehavior == normalized)
        {
            return;
        }

        State.ExperimentalTetherMinimizedBehavior = normalized;
        foreach (var window in _windows.Values.ToList())
        {
            window.RefreshExperimentalTetherVisibilityOptions();
        }
        SaveNow();
        RefreshSettingsRegions("labs.window");
    }

    private void RefreshExperimentalAttachmentMenus()
    {
        foreach (var window in _windows.Values.ToList())
        {
            window.RefreshExperimentalAttachmentMenu();
        }
    }

    private void RefreshExperimentalAttachmentsAfterDisplayMetrics()
    {
        foreach (var window in _windows.Values.ToList())
        {
            window.RefreshExperimentalAttachmentForDisplayMetrics();
        }
    }

    private void DisposeExternalWindowTracker()
    {
        StopExperimentalFollowFrames();
        _externalMoveSizeWindows.Clear();
        if (_externalWindowTracker == null)
        {
            return;
        }

        _externalWindowTracker.Changed -= OnExternalWindowChanged;
        _externalWindowTracker.Dispose();
        _externalWindowTracker = null;
    }

    private void DisposeExperimentalWindowRuntime()
    {
        DisposeExternalWindowTracker();
        foreach (var window in _windows.Values.ToList())
        {
            window.DisposeExperimentalWindowAttachment();
        }
    }
}
