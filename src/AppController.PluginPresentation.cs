namespace PaperTodo;

public sealed partial class AppController
{
    internal bool TryShowPluginHostPaper(
        string paperId,
        string providerId,
        bool activate)
    {
        if (!TryGetPluginHostPaper(paperId, providerId, out var paper))
        {
            return false;
        }

        ShowPaper(paper, activate);
        return true;
    }

    internal bool TryHidePluginHostPaper(string paperId, string providerId)
    {
        if (!TryGetPluginHostPaper(paperId, providerId, out var paper))
        {
            return false;
        }

        HidePaper(paper);
        return true;
    }

    internal bool TryTogglePluginHostPaperVisibility(
        string paperId,
        string providerId,
        bool activate)
    {
        if (!TryGetPluginHostPaper(paperId, providerId, out var paper))
        {
            return false;
        }

        if (paper.IsVisible)
        {
            HidePaper(paper);
        }
        else
        {
            ShowPaper(paper, activate);
        }
        return true;
    }

    internal bool TryExpandPluginHostPaper(
        string paperId,
        string providerId,
        bool activate)
    {
        if (!TryGetPluginHostPaper(paperId, providerId, out var paper))
        {
            return false;
        }

        SetPaperCollapsedRuntime(
            paper,
            collapsed: false,
            animate: State.EnableAnimations,
            saveGeometry: true);
        ShowPaper(paper, activate);
        return true;
    }

    internal bool TryCollapsePluginHostPaper(string paperId, string providerId)
    {
        if (!TryGetPluginHostPaper(paperId, providerId, out var paper) ||
            !CanPaperDisplayAsCapsule(paper))
        {
            return false;
        }

        SetPaperCollapsedRuntime(
            paper,
            collapsed: true,
            animate: State.EnableAnimations,
            saveGeometry: true);
        if (paper.IsVisible && State.UseCapsuleMode && State.UseDeepCapsuleMode)
        {
            ArrangeDeepCapsules(animate: State.EnableAnimations);
        }
        RefreshTrayMenu();
        MarkDirty();
        return true;
    }

    internal bool TryTogglePluginHostPaperCollapsed(
        string paperId,
        string providerId,
        bool activate)
    {
        if (!TryGetPluginHostPaper(paperId, providerId, out var paper))
        {
            return false;
        }

        return paper.IsCollapsed
            ? TryExpandPluginHostPaper(paperId, providerId, activate)
            : TryCollapsePluginHostPaper(paperId, providerId);
    }

    internal bool TryActivatePluginHostPaper(string paperId, string providerId)
    {
        if (!TryGetPluginHostPaper(paperId, providerId, out var paper))
        {
            return false;
        }

        if (!paper.IsVisible)
        {
            ShowPaper(paper, activate: true);
        }
        else
        {
            BringPaperToFront(paper);
        }
        return true;
    }

    private bool TryGetPluginHostPaper(
        string paperId,
        string providerId,
        out PaperData paper)
    {
        paper = null!;
        if (IsExiting ||
            string.IsNullOrWhiteSpace(paperId) ||
            string.IsNullOrWhiteSpace(providerId))
        {
            return false;
        }

        var candidate = State.Papers.FirstOrDefault(item =>
            string.Equals(item.Id, paperId, StringComparison.Ordinal));
        if (candidate == null ||
            candidate.Type != PaperTypes.Note ||
            !string.Equals(
                candidate.BodyProviderId,
                providerId,
                StringComparison.Ordinal))
        {
            return false;
        }

        paper = candidate;
        return true;
    }
}
