namespace PaperTodo;

public sealed partial class AppController
{
    private const double CodexMeterDefaultWidth = 360;
    private const double CodexMeterDefaultHeight = 430;

    internal void OpenOrCreateCodexMeterPaper()
    {
        var existing = State.Papers.FirstOrDefault(paper =>
            paper.Type == PaperTypes.Note &&
            string.Equals(paper.BodyProviderId, PaperBodyProviderIds.CodexMeter, StringComparison.Ordinal));
        if (existing != null)
        {
            existing.IsVisible = true;
            existing.IsCollapsed = false;
            EnablePluginRuntimeReconciliation();
            ShowPaper(existing, activate: true);
            RefreshTrayMenu();
            MarkDirty();
            return;
        }

        if (!PaperBodyPlugins.TryGet(PaperBodyProviderIds.CodexMeter, out var descriptor) ||
            !CanCreatePluginPaper(descriptor))
        {
            return;
        }

        var paper = CreatePaper(PaperTypes.Note, show: false);
        if (paper == null) return;
        paper.BodyProviderId = PaperBodyProviderIds.CodexMeter;
        paper.Title = PaperTitles.CleanCustomTitle(
            Strings.Get("CodexMeterPaperTitle"),
            State.MaxTitleLength);
        paper.Width = CodexMeterDefaultWidth;
        paper.Height = CodexMeterDefaultHeight;
        paper.IsVisible = true;
        paper.IsCollapsed = false;
        EnablePluginRuntimeReconciliation();
        ShowPaper(paper, activate: true);
        RefreshTrayMenu();
        MarkDirty();
    }
}
