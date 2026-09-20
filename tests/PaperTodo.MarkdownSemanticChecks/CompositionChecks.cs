using System.Runtime.CompilerServices;
using PaperTodo;

internal static class CompositionChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        Check("Quoted fenced code composes", () =>
        {
            var snapshot = MarkdownSemanticSnapshot.Parse("> ```\n> code\n> ```");
            True(snapshot.GetLine(0).IsQuoted, "quoted fence opening keeps quote");
            True(snapshot.GetLine(0).IsFencedCodeOpening, "quoted fence opening boundary");
            True(snapshot.GetLine(1).IsQuoted && snapshot.GetLine(1).IsFencedCode, "quoted fence content composes");
            True(snapshot.GetLine(2).IsQuoted, "quoted fence closing keeps quote");
            True(snapshot.GetLine(2).IsFencedCodeClosing, "quoted fence closing boundary");
        });

        Check("Image-looking text inside fence stays code", () =>
        {
            var snapshot = MarkdownSemanticSnapshot.Parse("```\n![x](papertodo-image://123)\n```");
            True(snapshot.GetLine(1).IsCode, "image-looking fenced line stays code semantic");
            True(snapshot.GetLine(1).IsFencedCode, "image-looking fenced line stays fenced");
        });

        Check("List inside quote composes", () =>
        {
            var snapshot = MarkdownSemanticSnapshot.Parse("> - item");
            var line = snapshot.GetLine(0);
            True(line.IsQuoted, "quoted list keeps quote");
            True(
                (line.Traits & MarkdownSemanticLineTraits.UnorderedList) != 0,
                "quoted list keeps list semantic");
            Equal(
                1,
                snapshot.Spans.Count(span => span.Kind == MarkdownSemanticSpanKind.UnorderedListMarker),
                "quoted list marker count");
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

    private static void Equal<T>(T expected, T actual, string message)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
        {
            throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
        }
    }
}
