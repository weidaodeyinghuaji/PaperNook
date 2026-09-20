using System.Runtime.CompilerServices;
using PaperTodo;

internal static class InlineBoundaryChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        Check("Triple emphasis composes", () =>
        {
            const string source = "***both***";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var emphasis = snapshot.Spans.Single(span => span.Kind == MarkdownSemanticSpanKind.Emphasis);
            var strong = snapshot.Spans.Single(span => span.Kind == MarkdownSemanticSpanKind.Strong);
            True(emphasis.Start <= strong.Start && emphasis.End >= strong.End ||
                 strong.Start <= emphasis.Start && strong.End >= emphasis.End,
                "triple emphasis spans must nest");
            True(SharedContent(source, emphasis, strong).Contains("both", StringComparison.Ordinal),
                "nested spans must share content");
        });

        Check("Triple underscore emphasis composes", () =>
        {
            const string source = "___both___";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            Equal(1, snapshot.Spans.Count(span => span.Kind == MarkdownSemanticSpanKind.Emphasis), "underscore emphasis count");
            Equal(1, snapshot.Spans.Count(span => span.Kind == MarkdownSemanticSpanKind.Strong), "underscore strong count");
        });

        Check("Intraword underscore stays literal", () =>
        {
            var snapshot = MarkdownSemanticSnapshot.Parse("foo_bar_baz");
            Equal(0, snapshot.Spans.Count(span => span.Kind is MarkdownSemanticSpanKind.Emphasis or MarkdownSemanticSpanKind.Strong),
                "CommonMark intraword underscore must not become emphasis");
        });

        Check("Asterisk emphasis remains valid inside word boundary", () =>
        {
            var snapshot = MarkdownSemanticSnapshot.Parse("foo*bar*baz");
            Equal(1, snapshot.Spans.Count(span => span.Kind == MarkdownSemanticSpanKind.Emphasis),
                "asterisk emphasis count");
        });

        Check("Nested emphasis inside link label", () =>
        {
            const string source = "[***both***](https://example.com)";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var link = snapshot.Links.Single();
            var styled = snapshot.Spans
                .Where(span => span.Kind is MarkdownSemanticSpanKind.Emphasis or MarkdownSemanticSpanKind.Strong)
                .ToArray();
            Equal(2, styled.Length, "nested link emphasis span count");
            True(styled.All(span => span.Start >= link.LabelStart && span.End <= link.LabelEnd),
                "all emphasis spans must stay inside link label");
        });

        Check("Inline code suppresses emphasis", () =>
        {
            var snapshot = MarkdownSemanticSnapshot.Parse("`**not bold**`");
            Equal(1, snapshot.Spans.Count(span => span.Kind == MarkdownSemanticSpanKind.InlineCode), "inline code count");
            Equal(0, snapshot.Spans.Count(span => span.Kind is MarkdownSemanticSpanKind.Emphasis or MarkdownSemanticSpanKind.Strong),
                "emphasis must not leak into inline code");
        });
    }

    private static string SharedContent(
        string source,
        MarkdownSemanticSpan left,
        MarkdownSemanticSpan right)
    {
        var start = Math.Max(left.Start, right.Start);
        var end = Math.Min(left.End, right.End);
        return end > start ? source[start..end] : string.Empty;
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
