namespace PaperTodo;

internal enum PluginShortcutPaperAction
{
    Show,
    Hide,
    Toggle,
    Expand,
    Collapse,
    Activate
}

internal static class PluginShortcutActions
{
    public const string Default = "paper.toggle";

    public static string Normalize(string? value)
    {
        var normalized = value?.Trim() ?? "";
        if (TryParsePaperAction(normalized, out var paperAction))
        {
            return paperAction switch
            {
                PluginShortcutPaperAction.Show => "paper.show",
                PluginShortcutPaperAction.Hide => "paper.hide",
                PluginShortcutPaperAction.Toggle => "paper.toggle",
                PluginShortcutPaperAction.Expand => "paper.expand",
                PluginShortcutPaperAction.Collapse => "paper.collapse",
                PluginShortcutPaperAction.Activate => "paper.activate",
                _ => ""
            };
        }

        return IsCustomActionId(normalized) ? normalized : "";
    }

    public static bool TryParsePaperAction(
        string? value,
        out PluginShortcutPaperAction action)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "show":
            case "paper.show":
                action = PluginShortcutPaperAction.Show;
                return true;
            case "hide":
            case "paper.hide":
                action = PluginShortcutPaperAction.Hide;
                return true;
            case "toggle":
            case "paper.toggle":
                action = PluginShortcutPaperAction.Toggle;
                return true;
            case "expand":
            case "paper.expand":
                action = PluginShortcutPaperAction.Expand;
                return true;
            case "collapse":
            case "paper.collapse":
                action = PluginShortcutPaperAction.Collapse;
                return true;
            case "activate":
            case "paper.activate":
                action = PluginShortcutPaperAction.Activate;
                return true;
            default:
                action = default;
                return false;
        }
    }

    public static bool IsCustomAction(string value) =>
        !TryParsePaperAction(value, out _) && IsCustomActionId(value);

    private static bool IsCustomActionId(string value) =>
        value.Length is >= 1 and <= 80 &&
        !value.StartsWith("paper.", StringComparison.OrdinalIgnoreCase) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-');
}
