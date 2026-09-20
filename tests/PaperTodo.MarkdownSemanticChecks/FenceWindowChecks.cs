using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace PaperTodo;

internal static class FenceWindowChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        CheckDeletingClosingFenceReachesEnd();
        CheckNewTildeFenceExpands();
        CheckFourBacktickChangePropagates();
        CheckRemovingLineBreakDestroysFence();
        CheckInsertingLineBreakCreatesFence();
        CheckNewFenceConvergesInsideExistingFence();
        CheckInlineBackticksStayLocal();
        ProfileFenceStateExpansion();
    }

    private static void CheckDeletingClosingFenceReachesEnd()
    {
        var builder = new StringBuilder();
        AppendPlain(builder, "prefix", 260);
        builder.Append("```text\n");
        AppendPlain(builder, "inside", 620);
        var closing = builder.Length;
        builder.Append("```\n");
        AppendPlain(builder, "tail", 420);

        var oldSource = builder.ToString();
        var newSource = oldSource.Remove(closing, 3);
        AssertExpandedEditMatchesFull(
            oldSource,
            newSource,
            minimumWindow: newSource.Length - closing,
            "deleted closing fence");
    }

    private static void CheckNewTildeFenceExpands()
    {
        var builder = new StringBuilder();
        AppendPlain(builder, "prefix", 240);
        var insertAt = builder.Length;
        builder.Append("anchor\n");
        AppendPlain(builder, "body", 520);
        builder.Append("~~~\n");
        AppendPlain(builder, "tail", 260);

        var oldSource = builder.ToString();
        var newSource = oldSource.Insert(insertAt, "~~~text\n");
        AssertExpandedEditMatchesFull(
            oldSource,
            newSource,
            minimumWindow: newSource.Length / 2,
            "new tilde fence");
    }

    private static void CheckFourBacktickChangePropagates()
    {
        var builder = new StringBuilder();
        AppendPlain(builder, "prefix", 220);
        var opening = builder.Length;
        builder.Append("````text\n");
        AppendPlain(builder, "inside-a", 180);
        builder.Append("```\n");
        AppendPlain(builder, "inside-b", 180);
        builder.Append("````\n");
        AppendPlain(builder, "tail", 260);

        var oldSource = builder.ToString();
        var newSource = oldSource.Remove(opening, 1);
        AssertExpandedEditMatchesFull(
            oldSource,
            newSource,
            minimumWindow: newSource.Length / 2,
            "four-backtick opener shortened");
    }

    private static void CheckRemovingLineBreakDestroysFence()
    {
        var builder = new StringBuilder();
        AppendPlain(builder, "prefix", 220);
        builder.Append("lead");
        var lineBreak = builder.Length;
        builder.Append("\n```text\n");
        AppendPlain(builder, "body", 420);
        builder.Append("```\n");
        AppendPlain(builder, "tail", 320);

        var oldSource = builder.ToString();
        var newSource = oldSource.Remove(lineBreak, 1);
        AssertExpandedEditMatchesFull(
            oldSource,
            newSource,
            minimumWindow: newSource.Length - lineBreak,
            "removed line break destroys fence opener");
    }

    private static void CheckInsertingLineBreakCreatesFence()
    {
        var builder = new StringBuilder();
        AppendPlain(builder, "prefix", 220);
        builder.Append("lead");
        var insertAt = builder.Length;
        builder.Append("```text\n");
        AppendPlain(builder, "body", 420);
        builder.Append("```\n");
        AppendPlain(builder, "tail", 320);

        var oldSource = builder.ToString();
        var newSource = oldSource.Insert(insertAt, "\n");
        AssertExpandedEditMatchesFull(
            oldSource,
            newSource,
            minimumWindow: newSource.Length - insertAt,
            "inserted line break creates fence opener");
    }

    private static void CheckNewFenceConvergesInsideExistingFence()
    {
        var builder = new StringBuilder();
        AppendPlain(builder, "prefix", 220);
        var insertAt = builder.Length;
        builder.Append("anchor\n");
        AppendPlain(builder, "between", 220);
        builder.Append("```csharp\n");
        AppendPlain(builder, "existing-fence", 180);
        var existingClosing = builder.Length;
        builder.Append("```\n");
        AppendPlain(builder, "tail", 220);

        var oldSource = builder.ToString();
        var newSource = oldSource.Insert(insertAt, "```text\n");
        AssertExpandedEditMatchesFull(
            oldSource,
            newSource,
            minimumWindow: existingClosing - insertAt,
            "new fence convergence inside existing fence");
    }

    private static void CheckInlineBackticksStayLocal()
    {
        var builder = new StringBuilder();
        AppendPlain(builder, "plain", 700);
        builder.Append("paragraph `` inline marker remains ordinary text\n\n");
        AppendPlain(builder, "after", 700);

        var oldSource = builder.ToString();
        var marker = oldSource.IndexOf("`` inline", StringComparison.Ordinal);
        var newSource = oldSource.Insert(marker, "`");
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        var windowLength = MarkdownSemanticSnapshot.GetIncrementalWindowLengthForTests(
            oldSource,
            oldSnapshot,
            newSource);

        if (windowLength <= 0 || windowLength > 2_000)
        {
            throw new InvalidOperationException(
                $"FAIL inline backticks: invalid local window {windowLength}");
        }
        if (!MarkdownSemanticSnapshot.TryParseIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var incremental))
        {
            throw new InvalidOperationException("FAIL inline backticks: unexpected fallback");
        }
        AssertEquivalent(MarkdownSemanticSnapshot.Parse(newSource), incremental, "inline backticks");
        Console.WriteLine($"PASS inline triple backticks stay local window={windowLength}");
    }

    private static void ProfileFenceStateExpansion()
    {
        var builder = new StringBuilder(100_000);
        AppendPlain(builder, "prefix", 420);
        builder.Append("```text\n");
        AppendPlain(builder, "inside", 500);
        var closing = builder.Length;
        builder.Append("```\n");
        AppendPlain(builder, "tail", 420);
        while (builder.Length < 98_000)
        {
            builder.Append("padding row for fence propagation profile\n\n");
        }

        var oldSource = builder.ToString();
        var newSource = oldSource.Remove(closing, 3);
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);

        _ = MarkdownSemanticSnapshot.GetIncrementalWindowLengthForTests(
            oldSource,
            oldSnapshot,
            newSource);
        _ = MarkdownSemanticSnapshot.GetIncrementalWindowLengthForTests(
            oldSource,
            oldSnapshot,
            newSource);

        const int scanIterations = 31;
        var scanMs = new double[scanIterations];
        var scanAlloc = new long[scanIterations];
        var windowLength = 0;
        for (var index = 0; index < scanIterations; index++)
        {
            var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            windowLength = MarkdownSemanticSnapshot.GetIncrementalWindowLengthForTests(
                oldSource,
                oldSnapshot,
                newSource);
            scanMs[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            scanAlloc[index] = Math.Max(
                0,
                GC.GetAllocatedBytesForCurrentThread() - allocationBefore);
        }
        Array.Sort(scanMs);
        Array.Sort(scanAlloc);

        const int incrementalIterations = 9;
        var incrementalMs = new double[incrementalIterations];
        var incrementalAlloc = new long[incrementalIterations];
        var last = MarkdownSemanticSnapshot.Empty;
        for (var index = 0; index < incrementalIterations; index++)
        {
            var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            if (!MarkdownSemanticSnapshot.TryParseIncremental(
                    oldSource,
                    oldSnapshot,
                    newSource,
                    out last))
            {
                throw new InvalidOperationException("FAIL fence profile: unexpected fallback");
            }
            incrementalMs[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            incrementalAlloc[index] = Math.Max(
                0,
                GC.GetAllocatedBytesForCurrentThread() - allocationBefore);
        }
        GC.KeepAlive(last);
        Array.Sort(incrementalMs);
        Array.Sort(incrementalAlloc);

        const int fullIterations = 7;
        var fullMs = new double[fullIterations];
        for (var index = 0; index < fullIterations; index++)
        {
            var started = Stopwatch.GetTimestamp();
            GC.KeepAlive(MarkdownSemanticSnapshot.Parse(newSource));
            fullMs[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        Array.Sort(fullMs);

        Console.WriteLine(
            $"PROFILE FenceWindowScan98k window={windowLength} " +
            $"p50={Median(scanMs):F3}ms p95={P95(scanMs):F3}ms " +
            $"alloc-p50={Median(scanAlloc) / 1024d:F1}KiB");
        Console.WriteLine(
            $"PROFILE FenceIncremental98k p50={Median(incrementalMs):F3}ms " +
            $"p95={P95(incrementalMs):F3}ms " +
            $"alloc-p50={Median(incrementalAlloc) / 1024d:F1}KiB");
        Console.WriteLine(
            $"PROFILE FenceFullParse98k p50={Median(fullMs):F3}ms p95={P95(fullMs):F3}ms");
    }

    private static void AssertExpandedEditMatchesFull(
        string oldSource,
        string newSource,
        int minimumWindow,
        string name)
    {
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        var windowLength = MarkdownSemanticSnapshot.GetIncrementalWindowLengthForTests(
            oldSource,
            oldSnapshot,
            newSource);
        if (windowLength < minimumWindow)
        {
            throw new InvalidOperationException(
                $"FAIL {name}: window {windowLength} < expected {minimumWindow}");
        }
        if (!MarkdownSemanticSnapshot.TryParseIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var incremental))
        {
            throw new InvalidOperationException($"FAIL {name}: unexpected fallback");
        }

        AssertEquivalent(MarkdownSemanticSnapshot.Parse(newSource), incremental, name);
        Console.WriteLine($"PASS {name} expands window={windowLength}/{newSource.Length}");
    }

    private static void AssertEquivalent(
        MarkdownSemanticSnapshot expected,
        MarkdownSemanticSnapshot actual,
        string name)
    {
        if (expected.LineCount != actual.LineCount ||
            !expected.Spans.SequenceEqual(actual.Spans) ||
            !expected.Links.SequenceEqual(actual.Links))
        {
            throw new InvalidOperationException($"FAIL {name}: snapshot mismatch");
        }
        for (var line = 0; line < expected.LineCount; line++)
        {
            if (!expected.GetLine(line).Equals(actual.GetLine(line)))
            {
                throw new InvalidOperationException($"FAIL {name}: line mismatch at {line}");
            }
        }
    }

    private static void AppendPlain(StringBuilder builder, string label, int count)
    {
        for (var index = 0; index < count; index++)
        {
            builder.Append(label).Append(" row ").Append(index)
                .Append(" ordinary words for local Markdown editing\n\n");
        }
    }

    private static double Median(double[] values) => values[values.Length / 2];
    private static long Median(long[] values) => values[values.Length / 2];
    private static double P95(double[] values) =>
        values[(int)Math.Ceiling(values.Length * 0.95) - 1];
}
