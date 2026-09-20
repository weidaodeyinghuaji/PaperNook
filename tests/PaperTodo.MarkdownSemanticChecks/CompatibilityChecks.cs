using System.Runtime.CompilerServices;
using PaperTodo;

internal static class CompatibilityChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        Check("Multiline formatting HTML stays source", () =>
        {
            var snapshot = MarkdownSemanticSnapshot.Parse("<b>first\nsecond</b>");
            Equal(0, snapshot.Spans.Count(span => span.Kind == MarkdownSemanticSpanKind.HtmlContainer),
                "multiline HTML container count");
            Equal(0, snapshot.Spans.Count(span => span.Kind == MarkdownSemanticSpanKind.HtmlStrong),
                "multiline HTML style count");
        });

        Check("Multiline HTML anchor stays source", () =>
        {
            var snapshot = MarkdownSemanticSnapshot.Parse("<a href=\"https://example.com\">first\nsecond</a>");
            Equal(0, snapshot.Links.Count, "multiline HTML anchor link count");
            Equal(0, snapshot.Spans.Count(span => span.Kind == MarkdownSemanticSpanKind.HtmlContainer),
                "multiline anchor container count");
        });

        Check("Single-line HTML remains supported", () =>
        {
            var snapshot = MarkdownSemanticSnapshot.Parse("<b>bold</b>");
            Equal(1, snapshot.Spans.Count(span => span.Kind == MarkdownSemanticSpanKind.HtmlContainer),
                "single-line HTML container count");
            Equal(1, snapshot.Spans.Count(span => span.Kind == MarkdownSemanticSpanKind.HtmlStrong),
                "single-line HTML style count");
        });
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

    private static void Equal<T>(T expected, T actual, string message)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
        {
            throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
        }
    }
}
