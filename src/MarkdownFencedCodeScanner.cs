namespace PaperTodo;

internal enum MarkdownFenceLineKind
{
    None,
    Opening,
    Closing
}

// State immediately before a Markdown line is processed. Keeping the opening marker and its
// length is essential: inside a four-backtick block, a three-backtick line (or any tilde fence)
// is content, not a closing fence.
internal readonly record struct MarkdownFencedCodeState(char Marker, int OpeningLength)
{
    public bool IsInside => Marker is '`' or '~' && OpeningLength >= 3;
}

// Lightweight fenced-code recognizer used only where a cheap fence state/boundary scan is useful:
// bounded Edge Mini navigation and large-Note incremental window discovery. It never publishes
// Note-body semantics; Markdig remains the body semantic authority.
internal static class MarkdownFencedCodeScanner
{
    internal static MarkdownFenceLineKind ClassifyLine(
        string text,
        MarkdownFencedCodeState stateBefore,
        out MarkdownFencedCodeState stateAfter) =>
        ClassifyLine((text ?? string.Empty).AsSpan(), stateBefore, out stateAfter);

    internal static MarkdownFenceLineKind ClassifyLine(
        ReadOnlySpan<char> text,
        MarkdownFencedCodeState stateBefore,
        out MarkdownFencedCodeState stateAfter)
    {
        if (stateBefore.IsInside)
        {
            if (IsClosingFence(text, stateBefore))
            {
                stateAfter = default;
                return MarkdownFenceLineKind.Closing;
            }

            stateAfter = stateBefore;
            return MarkdownFenceLineKind.None;
        }

        if (TryParseOpeningFence(text, out var opening))
        {
            stateAfter = opening;
            return MarkdownFenceLineKind.Opening;
        }

        stateAfter = default;
        return MarkdownFenceLineKind.None;
    }

    private static bool TryParseOpeningFence(
        ReadOnlySpan<char> text,
        out MarkdownFencedCodeState opening)
    {
        opening = default;
        var start = FenceStart(text);
        if (start < 0 || start >= text.Length)
        {
            return false;
        }

        var marker = text[start];
        if (marker is not ('`' or '~'))
        {
            return false;
        }

        var length = CountMarkerRun(text, start, marker);
        if (length < 3)
        {
            return false;
        }

        // A backtick info string may not itself contain a backtick. Tilde info strings have no
        // equivalent restriction. This prevents malformed lines from changing scanner state.
        if (marker == '`' && text[(start + length)..].IndexOf('`') >= 0)
        {
            return false;
        }

        opening = new MarkdownFencedCodeState(marker, length);
        return true;
    }

    private static bool IsClosingFence(
        ReadOnlySpan<char> text,
        MarkdownFencedCodeState opening)
    {
        var start = FenceStart(text);
        if (start < 0 || start >= text.Length || text[start] != opening.Marker)
        {
            return false;
        }

        var length = CountMarkerRun(text, start, opening.Marker);
        if (length < opening.OpeningLength)
        {
            return false;
        }

        // Closing fences may only be followed by spaces or tabs.
        for (var i = start + length; i < text.Length; i++)
        {
            if (text[i] is not (' ' or '\t'))
            {
                return false;
            }
        }

        return true;
    }

    private static int FenceStart(ReadOnlySpan<char> text)
    {
        var start = 0;
        while (start < text.Length && start < 3 && text[start] == ' ')
        {
            start++;
        }

        // Four leading spaces (and leading tabs) form indented content, not a fence opener.
        return start < text.Length && text[start] != ' ' && text[start] != '\t'
            ? start
            : -1;
    }

    private static int CountMarkerRun(ReadOnlySpan<char> text, int start, char marker)
    {
        var length = 0;
        while (start + length < text.Length && text[start + length] == marker)
        {
            length++;
        }

        return length;
    }
}
