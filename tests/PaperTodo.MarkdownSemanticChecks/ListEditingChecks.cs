using System.Runtime.CompilerServices;
using PaperTodo;

internal static class ListEditingChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        Check("Unordered continuation preserves source prefix", () =>
        {
            const string source = "  - item";
            var plan = Build(source);
            Equal("  - ", plan.Continuation, "unordered continuation");
            Equal(2, plan.MarkerStart, "unordered marker start");
            Equal(4, plan.ContentStart, "unordered content start");
        });

        Check("Ordered continuation increments number", () =>
        {
            const string source = "12)  item";
            var plan = Build(source);
            Equal("13)  ", plan.Continuation, "ordered continuation");
            Equal(0, plan.MarkerStart, "ordered marker start");
            Equal(5, plan.ContentStart, "ordered content start");
        });

        Check("Task continuation resets checkbox", () =>
        {
            const string source = "- [x] done";
            var plan = Build(source);
            Equal("- [ ] ", plan.Continuation, "task continuation");
            Equal(2, plan.ContentStart, "task base content start");
            Equal(6, plan.EmptyContentStart, "task content start");
        });

        Check("Quote list continuation preserves quote prefix", () =>
        {
            const string source = "> - item";
            var plan = Build(source);
            Equal("> - ", plan.Continuation, "quote list continuation");
            Equal(2, plan.MarkerStart, "quote marker start");
        });

        Check("Continuation line without marker is ignored", () =>
        {
            const string source = "- first\n  continuation";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var secondStart = source.IndexOf("  continuation", StringComparison.Ordinal);
            False(
                MarkdownListEditing.TryBuildContinuationPlan(
                    "  continuation",
                    secondStart,
                    snapshot,
                    out _),
                "continuation line must not synthesize a marker");
        });

        Check("Fenced pseudo-list is ignored", () =>
        {
            const string source = "```\n- not a list\n```";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var lineStart = source.IndexOf("- not", StringComparison.Ordinal);
            False(
                MarkdownListEditing.TryBuildContinuationPlan(
                    "- not a list",
                    lineStart,
                    snapshot,
                    out _),
                "code line must not produce list continuation");
        });

        Check("Empty task removal boundary excludes quote prefix", () =>
        {
            const string source = "> - [ ]";
            var plan = Build(source);
            Equal(2, plan.MarkerStart, "empty task marker start");
            Equal(source.Length, plan.EmptyContentStart, "empty task removal end");
        });
    }

    private static MarkdownListContinuationPlan Build(string source)
    {
        var snapshot = MarkdownSemanticSnapshot.Parse(source);
        if (!MarkdownListEditing.TryBuildContinuationPlan(source, 0, snapshot, out var plan))
        {
            throw new InvalidOperationException("expected list continuation plan");
        }
        return plan;
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
