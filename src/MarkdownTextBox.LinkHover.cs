using System;
using System.Windows;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    public bool TryGetOpenableLinkFromTextViewPoint(Point point, out string url) =>
        TryGetOpenableLinkFromTextViewPointCore(point, out url);

    public bool TryGetOpenableLinkFromTextViewPointFast(Point point, out string url) =>
        TryGetOpenableLinkFromTextViewPointCore(point, out url);

    private bool TryGetOpenableLinkFromTextViewPointCore(
        Point point,
        out string url)
    {
        url = "";
        if (Document == null ||
            !TryGetCurrentSemanticSnapshot(out var snapshot))
        {
            return false;
        }

        try
        {
            EnsureVisualLines();
            var textView = TextArea.TextView;
            if (!textView.VisualLinesValid)
            {
                return false;
            }

            var editorPoint = textView.TranslatePoint(point, this);
            if (!TryGetCharacterIndexFromPoint(editorPoint, out var characterIndex))
            {
                return false;
            }

            var offset = Math.Clamp(characterIndex, 0, Document.TextLength);
            if (!TryResolveLinkAtVisualOffset(snapshot, offset, out var link))
            {
                return false;
            }

            var highlightLinks = !string.Equals(
                MarkdownRenderMode,
                MarkdownRenderModes.Off,
                StringComparison.Ordinal);
            if (!highlightLinks && !IsLiteralBareHttpLink(link))
            {
                return false;
            }

            if (!IsLinkSegmentHit(point, link.Start, link.Length) ||
                !TryNormalizeMarkdownUrl(link.Url, out var normalizedUrl))
            {
                return false;
            }

            url = normalizedUrl;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveLinkAtVisualOffset(
        MarkdownSemanticSnapshot snapshot,
        int offset,
        out MarkdownSemanticLink link)
    {
        if (snapshot.TryGetLinkAtOffset(offset, out link))
        {
            return true;
        }

        return offset > 0 && snapshot.TryGetLinkAtOffset(offset - 1, out link);
    }

    private bool IsLiteralBareHttpLink(MarkdownSemanticLink link)
    {
        if (!link.IsAuto || link.Length <= 0 || Document == null ||
            link.Start < 0 || link.End > Document.TextLength)
        {
            return false;
        }

        var source = Document.GetText(link.Start, link.Length);
        return source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsLinkSegmentHit(Point point, int startOffset, int length)
    {
        if (length <= 0)
        {
            return false;
        }

        var textView = TextArea.TextView;
        var segment = new TextSegment
        {
            StartOffset = startOffset,
            Length = length
        };

        foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(
                     textView,
                     segment,
                     true))
        {
            var hitRect = new Rect(
                rect.X - 2,
                rect.Y - 2,
                rect.Width + 4,
                rect.Height + 4);
            if (hitRect.Contains(point))
            {
                return true;
            }
        }

        return false;
    }
}
