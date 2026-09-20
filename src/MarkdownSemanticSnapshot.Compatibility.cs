namespace PaperTodo;

internal sealed partial class MarkdownSemanticSnapshot
{
    private static void ApplyLegacyCompatibilityBoundaries(
        string source,
        List<MarkdownSemanticSpan> spans,
        List<MarkdownSemanticLink> links)
    {
        var multilineHtml = spans
            .Where(span =>
                span.Kind == MarkdownSemanticSpanKind.HtmlContainer &&
                ContainsLineBreak(source, span.Start, span.Length))
            .ToArray();
        if (multilineHtml.Length == 0)
        {
            return;
        }

        foreach (var container in multilineHtml)
        {
            spans.RemoveAll(span =>
                IsHtmlSemanticKind(span.Kind) &&
                span.Start >= container.Start &&
                span.End <= container.End);

            links.RemoveAll(link =>
                link.Start >= container.Start &&
                link.End <= container.End &&
                IsHtmlOpeningAt(source, link.Start));
        }
    }

    private static bool ContainsLineBreak(
        string source,
        int start,
        int length)
    {
        if (length <= 0 || start < 0 || start >= source.Length)
        {
            return false;
        }

        var end = Math.Min(source.Length, start + length);
        for (var index = start; index < end; index++)
        {
            if (source[index] is '\r' or '\n')
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsHtmlSemanticKind(MarkdownSemanticSpanKind kind) =>
        kind is MarkdownSemanticSpanKind.HtmlContainer or
            MarkdownSemanticSpanKind.HtmlMarker or
            MarkdownSemanticSpanKind.HtmlStrong or
            MarkdownSemanticSpanKind.HtmlEmphasis or
            MarkdownSemanticSpanKind.HtmlStrikethrough or
            MarkdownSemanticSpanKind.HtmlUnderline or
            MarkdownSemanticSpanKind.HtmlCode;

    private static bool IsHtmlOpeningAt(string source, int start)
    {
        return start >= 0 &&
            start + 2 < source.Length &&
            source[start] == '<' &&
            (source[start + 1] is 'a' or 'A') &&
            (char.IsWhiteSpace(source[start + 2]) || source[start + 2] == '>');
    }
}
