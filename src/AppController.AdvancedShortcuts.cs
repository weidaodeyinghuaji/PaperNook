using System.Runtime.InteropServices;

namespace PaperTodo;

public sealed partial class AppController
{
    private readonly HashSet<string> _advancedTransparentPaperIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _advancedTransparentCapsuleIds = new(StringComparer.Ordinal);
    private bool _advancedAllPapersLocked;
    private bool _advancedMasterCapsulesTransparent;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    internal double AdvancedShortcutOpacity =>
        ExperimentalOpacityLevels.Normalize(
            State.ExperimentalShortcutOpacityLevel,
            0.35);

    internal bool IsAdvancedPaperTransparent(PaperData paper) =>
        _advancedTransparentPaperIds.Contains(paper.Id);

    internal bool IsAdvancedCapsuleTransparent(PaperData paper) =>
        _advancedTransparentCapsuleIds.Contains(paper.Id);

    internal bool AreAdvancedMasterCapsulesTransparent =>
        _advancedMasterCapsulesTransparent;

    private void ToggleAdvancedAllPapersLocked() =>
        SetAdvancedAllPapersLocked(!_advancedAllPapersLocked);

    private void SetAdvancedAllPapersLocked(bool locked)
    {
        if (_advancedAllPapersLocked == locked)
        {
            RefreshAdvancedShortcutSurfaces();
            return;
        }

        _advancedAllPapersLocked = locked;
        foreach (var window in _windows.Values.ToList())
        {
            window.SetAdvancedInteractionLocked(locked);
        }

        RefreshTrayMenu();
    }

    internal void UnlockAllPapersFromLockIcon()
    {
        if (_advancedAllPapersLocked &&
            State.ExperimentalAllowLockIconUnlock)
        {
            SetAdvancedAllPapersLocked(false);
        }
    }

    private void ToggleAdvancedAllPapersTransparent()
    {
        var paperIds = State.Papers
            .Select(paper => paper.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (paperIds.Length == 0)
        {
            return;
        }

        var allTransparent = paperIds.All(_advancedTransparentPaperIds.Contains);
        if (allTransparent)
        {
            _advancedTransparentPaperIds.ExceptWith(paperIds);
        }
        else
        {
            _advancedTransparentPaperIds.UnionWith(paperIds);
        }

        RefreshAdvancedShortcutSurfaces();
    }

    private void ToggleAdvancedAllCapsulesTransparent()
    {
        var paperIds = State.Papers
            .Where(CanPaperDisplayAsCapsule)
            .Select(paper => paper.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (paperIds.Length == 0 && !_advancedMasterCapsulesTransparent)
        {
            return;
        }

        var allTransparent =
            _advancedMasterCapsulesTransparent &&
            paperIds.All(_advancedTransparentCapsuleIds.Contains);
        if (allTransparent)
        {
            _advancedTransparentCapsuleIds.ExceptWith(paperIds);
            _advancedMasterCapsulesTransparent = false;
        }
        else
        {
            _advancedTransparentCapsuleIds.UnionWith(paperIds);
            _advancedMasterCapsulesTransparent = true;
        }

        RefreshAdvancedShortcutSurfaces();
    }

    private void ToggleAdvancedCurrentPaperTransparent()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return;
        }

        var target = _windows.Values.FirstOrDefault(window =>
            !window.IsClosed &&
            window.HasExpandedPaperSurface &&
            window.OwnsNativeWindow(foregroundWindow));
        if (target == null)
        {
            return;
        }

        if (!_advancedTransparentPaperIds.Add(target.PaperId))
        {
            _advancedTransparentPaperIds.Remove(target.PaperId);
        }
        target.UpdateExperimentalOpacitySettings();
    }

    private void RefreshAdvancedShortcutSurfaces(bool animate = true)
    {
        foreach (var window in _windows.Values.ToList())
        {
            window.SetAdvancedInteractionLocked(_advancedAllPapersLocked);
            window.UpdateExperimentalOpacitySettings(animate);
        }
        foreach (var master in _masterCapsules.Values.ToList())
        {
            master.UpdateExperimentalOpacity();
        }
    }

    private void ClearAdvancedShortcutRuntimeState()
    {
        _advancedTransparentPaperIds.Clear();
        _advancedTransparentCapsuleIds.Clear();
        _advancedMasterCapsulesTransparent = false;
        SetAdvancedAllPapersLocked(false);
        RefreshAdvancedShortcutSurfaces(animate: false);
    }
}
