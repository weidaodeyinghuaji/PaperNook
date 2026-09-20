using System.Runtime.CompilerServices;
using PaperTodo;

internal static class HtmlEscapeChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        Check("HTML strong semantic", () =>
        {
            const string source = "before <strong>bold</strong> after";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var strong = SingleSpan(snapshot, MarkdownSemanticSpanKind.HtmlStrong);
            Equal("bold", source.Substring(strong.Start, strong.Length), "HTML strong content");
            Equal(2, snapshot.Spans.Count(span => span.Kind == MarkdownSemanticSpanKind.HtmlMarker), "HTML marker count");
            Equal(1, snapshot.Spans.Count(span => span.Kind == MarkdownSemanticSpanKind.HtmlContainer), "HTML container count");
        });

        Check("Nested HTML semantics compose", () =>
        {
            const string source = "<i><b>x</b></i>";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var emphasis = SingleSpan(snapshot, MarkdownSemanticSpanKind.HtmlEmphasis);
            var strong = SingleSpan(snapshot, MarkdownSemanticSpanKind.HtmlStrong);
            True(emphasis.Start <= strong.Start && emphasis.End >= strong.End, "outer italic covers nested bold");
            Equal("x", source.Substring(strong.Start, strong.Length), "nested bold content");
        });

        Check("Crossed HTML stays plain", () =>
        {
            var snapshot = MarkdownSemanticSnapshot.Parse("<b><i>x</b></i>");
            Equal(0, snapshot.Spans.Count(span => span.Kind == MarkdownSemanticSpanKind.HtmlContainer), "crossed HTML container count");
            Equal(0, snapshot.Spans.Count(span => span.Kind is MarkdownSemanticSpanKind.HtmlStrong or MarkdownSemanticSpanKind.HtmlEmphasis), "crossed HTML style count");
        });

        Check("Unsupported HTML stays plain", () =>
        {
            var snapshot = MarkdownSemanticSnapshot.Parse("<span>x</span>");
            Equal(0, snapshot.Spans.Count(span => span.Kind == MarkdownSemanticSpanKind.HtmlContainer), "unsupported HTML container count");
            Equal(0, snapshot.Spans.Count(span => span.Kind == MarkdownSemanticSpanKind.HtmlMarker), "unsupported HTML marker count");
        });

        Check("Attributes stay restricted on formatting HTML", () =>
        {
            var snapshot = MarkdownSemanticSnapshot.Parse("<b class=\"x\">bold</b>");
            Equal(0, snapshot.Spans.Count(span => span.Kind == MarkdownSemanticSpanKind.HtmlStrong), "formatting tag attributes stay unsupported");
        });

        Check("HTML anchor semantic", () =>
        {
            const string source = "<a href=\"https://example.com/a\">label</a>";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            Equal(1, snapshot.Links.Count, "HTML anchor link count");
            var link = snapshot.Links[0];
            Equal("label", source.Substring(link.LabelStart, link.LabelLength), "HTML anchor label");
            Equal("https://example.com/a", source.Substring(link.DestinationStart, link.DestinationLength), "HTML href source");
            Equal("https://example.com/a", link.Url.TrimEnd('/'), "HTML normalized URL");
        });

        Check("Unsafe HTML anchor stays plain", () =>
        {
            var snapshot = MarkdownSemanticSnapshot.Parse("<a href=\"javascript:alert(1)\">x</a>");
            Equal(0, snapshot.Links.Count, "unsafe HTML anchor link count");
            Equal(0, snapshot.Spans.Count(span => span.Kind == MarkdownSemanticSpanKind.HtmlContainer), "unsafe HTML anchor container count");
        });

        Check("Bare URL stays inert inside HTML code", () =>
        {
            var snapshot = MarkdownSemanticSnapshot.Parse("<code>https://example.com</code>");
            Equal(0, snapshot.Links.Count, "HTML code bare-link count");
        });

        Check("Escape marker semantic", () =>
        {
            const string source = "\\*literal*";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var marker = SingleSpan(snapshot, MarkdownSemanticSpanKind.EscapeMarker);
            Equal(0, marker.Start, "escape marker start");
            Equal(1, marker.Length, "escape marker length");
        });

        Check("Escape markers stay out of inline code", () =>
        {
            var snapshot = MarkdownSemanticSnapshot.Parse("`\\*` ");
            Equal(0, snapshot.Spans.Count(span => span.Kind == MarkdownSemanticSpanKind.EscapeMarker), "inline-code escape marker count");
        });

        Check("Escape markers stay out of supported HTML", () =>
        {
            var snapshot = MarkdownSemanticSnapshot.Parse("<b>\\*</b>");
            Equal(0, snapshot.Spans.Count(span => span.Kind == MarkdownSemanticSpanKind.EscapeMarker), "HTML escape marker count");
        });
    }

    private static MarkdownSemanticSpan SingleSpan(
        MarkdownSemanticSnapshot snapshot,
        MarkdownSemanticSpanKind kind)
    {
        var matches = snapshot.Spans.Where(span => span.Kind == kind).ToArray();
        Equal(1, matches.Length, $"{kind} span count");
        return matches[0];
    }

    private static void Check(string name, Action check)
    {
        try
        {
            check();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"FAIL {name}: {ex.Message}", ex);
        }
    }

    private static void True(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
        {
            throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
        }
    }
}
