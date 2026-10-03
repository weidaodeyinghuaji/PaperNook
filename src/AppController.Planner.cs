using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private readonly HashSet<PlannerWindow> _plannerWindows = new();
    private PlannerWindow? _plannerWindow;
    private bool _plannerDraftPending;
    internal PlannerScheduleDraft? LastPlannerSchedule { get; set; }

    public void OpenPlanner()
    {
        if (IsExiting) return;
        if (_plannerWindow == null)
        {
            _plannerWindow = new PlannerWindow(this);
            RegisterPlanner(_plannerWindow);
            _plannerWindow.Closed += (_, _) => _plannerWindow = null;
        }
        _plannerWindow.Show();
        if (_plannerWindow.WindowState == WindowState.Minimized) _plannerWindow.WindowState = WindowState.Normal;
        _plannerWindow.Activate();
    }

    internal void PinPlannerView(string view, string? paperId)
    {
        var existing = _plannerWindows.FirstOrDefault(w => w.IsPinned && w.View == view && w.PaperId == paperId);
        if (existing != null) { existing.Activate(); return; }
        var config = State.PlannerPinnedViews.FirstOrDefault(p => p.View == view && p.PaperId == paperId)
            ?? new PlannerPinnedView(view, paperId);
        if (!State.PlannerPinnedViews.Any(p => p.View == view && p.PaperId == paperId))
        {
            if (State.PlannerPinnedViews.Count >= 20)
                throw new PaperCommandException("pin_limit", Strings.Get("PlannerPinLimit"));
            State.PlannerPinnedViews.Add(config);
            if (!TryCommitExternalMutation())
            {
                State.PlannerPinnedViews.Remove(config);
                throw new PaperCommandException("save_failed", Strings.Get("SaveFailureTitle"));
            }
        }
        var window = new PlannerWindow(this, view, paperId, pinned: true);
        window.RestorePinnedLayout(config.Layout);
        RegisterPlanner(window);
        window.Closed += (_, _) =>
        {
            if (IsExiting) return;
            var current = State.PlannerPinnedViews.FirstOrDefault(p => p.View == view && p.PaperId == paperId);
            if (current == null) return;
            State.PlannerPinnedViews.Remove(current);
            if (!TryCommitExternalMutation()) State.PlannerPinnedViews.Add(current);
        };
        window.Show();
    }

    internal void RememberPlannerPinnedLayout(string view, string? paperId, PlannerPinnedLayout layout)
    {
        if (!IsRunning) return;
        var index = State.PlannerPinnedViews.FindIndex(p => p.View == view && p.PaperId == paperId);
        if (index < 0 || State.PlannerPinnedViews[index].Layout == layout) return;
        State.PlannerPinnedViews[index] = State.PlannerPinnedViews[index] with { Layout = layout };
        // Use the existing debounced/versioned save path, including final exit flush.
        MarkDirty();
    }

    private void RestorePlannerPinnedViews(StartupCommandKind visibility)
    {
        if (IsExiting || visibility is StartupCommandKind.Hide or StartupCommandKind.Toggle) return;
        foreach (var config in State.PlannerPinnedViews.ToArray())
        {
            if (config.View == "list" && !State.Papers.Any(p => p.Type == PaperTypes.Todo && p.Id == config.PaperId)) continue;
            PinPlannerView(config.View, config.PaperId);
        }
    }

    internal void OpenPlannerTask(PlannerTask task)
    {
        OpenPlanner();
        _plannerWindow?.SelectTask(task);
    }

    private void RegisterPlanner(PlannerWindow window)
    {
        _plannerWindows.Add(window);
        window.Closed += (_, _) => _plannerWindows.Remove(window);
    }

    internal void NotifyPlannerChanged()
    {
        foreach (var window in _plannerWindows?.ToArray() ?? []) window.QueueRefresh();
    }

    private void RefreshPlannerAppearance()
    {
        foreach (var window in _plannerWindows.ToArray()) window.RefreshAppearance();
    }

    internal void ShowPlannerScheduleDraft(PlannerScheduleDraft draft)
    {
        OpenPlanner();
        _plannerWindow?.ShowScheduleDraft(draft);
    }

    internal void QueuePlannerScheduleDraft(PlannerScheduleDraft draft)
    {
        if (_plannerDraftPending) throw new PaperCommandException("draft_pending", "A schedule proposal is already awaiting local confirmation.");
        _plannerDraftPending = true;
        _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try { if (!IsExiting) ShowPlannerScheduleDraft(draft); }
            finally { _plannerDraftPending = false; }
        }));
    }
}
