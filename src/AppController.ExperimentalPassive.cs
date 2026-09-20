namespace PaperTodo;

public sealed partial class AppController
{
    private PaperWindow? _lastActivatedPaperWindow;
    private PaperWindow? _experimentalCurrentPassiveWindow;
    private bool _experimentalAllSurfacesPassive;

    internal bool IsExperimentalAllSurfacesPassive =>
        _experimentalAllSurfacesPassive;

    internal void NotifyPaperWindowActivated(PaperWindow window)
    {
        if (window.CanEnterCurrentExperimentalPassive &&
            !window.IsExperimentalPassive)
        {
            _lastActivatedPaperWindow = window;
        }
    }

    internal void NotifyPaperWindowClosed(PaperWindow window)
    {
        if (ReferenceEquals(_lastActivatedPaperWindow, window))
        {
            _lastActivatedPaperWindow = null;
        }
        if (ReferenceEquals(_experimentalCurrentPassiveWindow, window))
        {
            _experimentalCurrentPassiveWindow = null;
        }
    }

    private void ExecuteExperimentalShortcut(GlobalShortcutDefinition definition)
    {
        if (!State.GlobalHotkeyEnabled.GetValueOrDefault(definition.Id))
        {
            return;
        }

        switch (definition.ExperimentalKind)
        {
            case ExperimentalShortcutKind.CurrentPaperPassive:
                ToggleCurrentPaperExperimentalPassive();
                break;
            case ExperimentalShortcutKind.AllSurfacesPassive:
                if (!HasDeepCapsuleReorderDragInProgress())
                {
                    SetAllSurfacesExperimentalPassive(
                        !_experimentalAllSurfacesPassive);
                }
                break;
            case ExperimentalShortcutKind.LockAllPapers:
                ToggleAdvancedAllPapersLocked();
                break;
            case ExperimentalShortcutKind.AllPapersTransparent:
                ToggleAdvancedAllPapersTransparent();
                break;
            case ExperimentalShortcutKind.AllCapsulesTransparent:
                ToggleAdvancedAllCapsulesTransparent();
                break;
            case ExperimentalShortcutKind.CurrentPaperTransparent:
                ToggleAdvancedCurrentPaperTransparent();
                break;
        }
    }

    private void ToggleCurrentPaperExperimentalPassive()
    {
        if (_experimentalCurrentPassiveWindow is { } passiveWindow)
        {
            passiveWindow.SetExperimentalPassiveReason(
                ExperimentalPassiveReason.CurrentPaper,
                enabled: false);
            _experimentalCurrentPassiveWindow = null;
            RefreshTrayMenu();
            return;
        }

        var target = _windows.Values.FirstOrDefault(window =>
                window.IsActive &&
                !window.IsExperimentalPassive &&
                window.CanEnterCurrentExperimentalPassive) ??
            (_lastActivatedPaperWindow is { } lastWindow &&
             _windows.Values.Contains(lastWindow) &&
             !lastWindow.IsExperimentalPassive &&
             lastWindow.CanEnterCurrentExperimentalPassive
                ? lastWindow
                : null);
        if (target == null)
        {
            return;
        }

        target.SetExperimentalPassiveReason(
            ExperimentalPassiveReason.CurrentPaper,
            enabled: true);
        _experimentalCurrentPassiveWindow = target;
        RefreshTrayMenu();
    }

    private void HandleExperimentalShortcutFeatureChanged(
        GlobalShortcutDefinition definition,
        bool enabled)
    {
        if (enabled)
        {
            return;
        }

        if (definition.ExperimentalKind == ExperimentalShortcutKind.CurrentPaperPassive)
        {
            RestoreCurrentPaperExperimentalPassive();
        }
        else if (definition.ExperimentalKind == ExperimentalShortcutKind.AllSurfacesPassive)
        {
            SetAllSurfacesExperimentalPassive(enabled: false);
        }
    }

    private void RestoreCurrentPaperExperimentalPassive()
    {
        if (_experimentalCurrentPassiveWindow is not { } window)
        {
            return;
        }

        window.SetExperimentalPassiveReason(
            ExperimentalPassiveReason.CurrentPaper,
            enabled: false);
        _experimentalCurrentPassiveWindow = null;
        RefreshTrayMenu();
    }

    private void RestoreExperimentalPassiveForWindow(PaperWindow window)
    {
        window.SetExperimentalPassiveReason(
            ExperimentalPassiveReason.CurrentPaper,
            enabled: false);
        if (ReferenceEquals(_experimentalCurrentPassiveWindow, window))
        {
            _experimentalCurrentPassiveWindow = null;
        }
    }

    private bool HasExperimentalPassiveSurfaces =>
        _experimentalCurrentPassiveWindow != null ||
        _experimentalAllSurfacesPassive;

    private void RestoreAllExperimentalPassiveSurfaces()
    {
        RestoreCurrentPaperExperimentalPassive();
        SetAllSurfacesExperimentalPassive(enabled: false);
    }

    private void SetAllSurfacesExperimentalPassive(bool enabled)
    {
        if (_experimentalAllSurfacesPassive == enabled)
        {
            return;
        }

        _experimentalAllSurfacesPassive = enabled;
        foreach (var window in _windows.Values.ToList())
        {
            window.SetExperimentalAllSurfacesPassive(enabled);
        }
        foreach (var master in _masterCapsules.Values.ToList())
        {
            master.SetExperimentalPassive(enabled);
        }

        RefreshTrayMenu();
    }
}
