using System.Runtime.CompilerServices;
using PaperTodo;

internal static class QueryChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        Check("Link query resolves source body", () =>
        {
            const string source = "before [label](https://example.com) after";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var bodyOffset = source.IndexOf("label", StringComparison.Ordinal) + 2;
            True(snapshot.TryGetLinkAtOffset(bodyOffset, out var link), "link query inside label");
            Equal("label", source.Substring(link.LabelStart, link.LabelLength), "queried label");
        });

        Check("Link query keeps source end exclusive", () =>
        {
            const string source = "https://example.com";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var link = snapshot.Links.Single();
            False(snapshot.TryGetLinkAtOffset(link.End, out _), "semantic link end must remain exclusive");
        });

        Check("Adjacent link boundary resolves second link", () =>
        {
            const string source = "[a](https://a.example)[b](https://b.example)";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            Equal(2, snapshot.Links.Count, "adjacent link count");
            var second = snapshot.Links[1];
            True(snapshot.TryGetLinkAtOffset(second.Start, out var resolved), "second link start resolves");
            Equal(second, resolved, "second link identity");
        });

        Check("Line index returns only overlapping semantics", () =>
        {
            const string source = "**bold**\nplain\n`code` https://example.com";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            True(
                snapshot.SpansForLine(0).ToArray().Any(span => span.Kind == MarkdownSemanticSpanKind.Strong),
                "line zero strong span");
            False(
                snapshot.SpansForLine(1).ToArray().Any(span => span.Kind == MarkdownSemanticSpanKind.Strong),
                "plain line excludes strong span");
            True(
                snapshot.SpansForLine(2).ToArray().Any(span => span.Kind == MarkdownSemanticSpanKind.InlineCode),
                "line two inline code span");
            Equal(1, snapshot.LinksForLine(2).Length, "line two link count");
            Equal(0, snapshot.LinksForLine(1).Length, "plain line link count");
        });

        Check("Link query misses plain text", () =>
        {
            const string source = "before https://example.com after";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            False(snapshot.TryGetLinkAtOffset(1, out _), "plain-text offset must not resolve link");
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

    private static void True(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool value, string message) => True(!value, message);

    private static void Equal<T>(T expected, T actual, string message)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
        {
            throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
        }
    }
}
