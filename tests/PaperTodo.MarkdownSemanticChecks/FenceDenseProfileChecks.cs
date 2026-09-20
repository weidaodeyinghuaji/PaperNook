using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PaperTodo;

internal static class FenceDenseProfileChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        var oldSource = PerformanceProfileChecks.BuildLargeStressSource();
        var insertAt = oldSource.IndexOf(
            "# Heading ",
            oldSource.Length / 3,
            StringComparison.Ordinal);
        if (insertAt < 0)
        {
            throw new InvalidOperationException("FAIL dense fence profile: heading probe missing");
        }

        var newSource = oldSource.Insert(insertAt, "```text\n");
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        var full = MarkdownSemanticSnapshot.Parse(newSource);
        var windowLength = MarkdownSemanticSnapshot.GetIncrementalWindowLengthForTests(
            oldSource,
            oldSnapshot,
            newSource);
        if (windowLength <= 0)
        {
            throw new InvalidOperationException("FAIL dense fence profile: no incremental window");
        }
        if (!MarkdownSemanticSnapshot.TryParseIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var warm))
        {
            throw new InvalidOperationException("FAIL dense fence profile: unexpected fallback");
        }
        AssertEquivalent(full, warm);

        const int incrementalIterations = 9;
        var incrementalMs = new double[incrementalIterations];
        var incrementalAlloc = new long[incrementalIterations];
        MarkdownSemanticSnapshot last = warm;
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
                throw new InvalidOperationException("FAIL dense fence profile timing: fallback");
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
        var fullAlloc = new long[fullIterations];
        for (var index = 0; index < fullIterations; index++)
        {
            var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            GC.KeepAlive(MarkdownSemanticSnapshot.Parse(newSource));
            fullMs[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            fullAlloc[index] = Math.Max(
                0,
                GC.GetAllocatedBytesForCurrentThread() - allocationBefore);
        }
        Array.Sort(fullMs);
        Array.Sort(fullAlloc);

        Console.WriteLine(
            $"PROFILE FenceDense98k window={windowLength}/{newSource.Length} " +
            $"incremental-p50={Median(incrementalMs):F3}ms " +
            $"incremental-p95={P95(incrementalMs):F3}ms " +
            $"incremental-alloc={Median(incrementalAlloc) / 1024d:F1}KiB");
        Console.WriteLine(
            $"PROFILE FenceDenseFull98k p50={Median(fullMs):F3}ms " +
            $"p95={P95(fullMs):F3}ms alloc={Median(fullAlloc) / 1024d:F1}KiB");
    }

    private static void AssertEquivalent(
        MarkdownSemanticSnapshot expected,
        MarkdownSemanticSnapshot actual)
    {
        if (expected.LineCount != actual.LineCount ||
            !expected.Spans.SequenceEqual(actual.Spans) ||
            !expected.Links.SequenceEqual(actual.Links))
        {
            throw new InvalidOperationException("FAIL dense fence profile: snapshot mismatch");
        }
        for (var line = 0; line < expected.LineCount; line++)
        {
            if (!expected.GetLine(line).Equals(actual.GetLine(line)))
            {
                throw new InvalidOperationException(
                    $"FAIL dense fence profile: line mismatch at {line}");
            }
        }
    }

    private static double Median(double[] values) => values[values.Length / 2];
    private static long Median(long[] values) => values[values.Length / 2];
    private static double P95(double[] values) =>
        values[(int)Math.Ceiling(values.Length * 0.95) - 1];
}
