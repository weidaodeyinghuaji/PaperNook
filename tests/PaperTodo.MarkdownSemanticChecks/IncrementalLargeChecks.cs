using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace PaperTodo;

internal static class IncrementalLargeChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        CheckLargeLocalSmoke();
        ProfileDenseMarkdownIncrementalEdit();
    }

    private static void CheckLargeLocalSmoke()
    {
        var builder = new StringBuilder(70_000);
        var index = 0;
        while (builder.Length < 60_000)
        {
            builder.Append("Paragraph ").Append(index)
                .Append(" ordinary words for large best-effort local editing.\n\n");
            if ((index % 31) == 0)
            {
                builder.Append("```text\nfenced row\nsecond fenced row\n```\n\n");
            }
            index++;
        }

        var source = builder.ToString();
        var snapshot = MarkdownSemanticSnapshot.Parse(source);
        for (var step = 0; step < 24; step++)
        {
            var marker = $"Paragraph {50 + (step * 7)} ordinary";
            var offset = source.IndexOf(marker, StringComparison.Ordinal);
            if (offset < 0)
            {
                throw new InvalidOperationException($"FAIL large local smoke: marker '{marker}' missing");
            }
            offset += marker.Length;
            var next = source.Insert(offset, "Z");
            var windowLength = MarkdownSemanticSnapshot.GetIncrementalWindowLengthForTests(
                source,
                snapshot,
                next);
            if (windowLength < 0 || windowLength > 2_000)
            {
                throw new InvalidOperationException(
                    $"FAIL large local smoke step {step}: ordinary window too large ({windowLength})");
            }
            if (!MarkdownSemanticSnapshot.TryParseIncremental(
                    source,
                    snapshot,
                    next,
                    out var incremental))
            {
                throw new InvalidOperationException(
                    $"FAIL large local smoke step {step}: ordinary edit unexpectedly fell back");
            }
            ValidateRanges(incremental, next.Length, $"large local smoke step {step}");
            source = next;
            snapshot = incremental;
        }

        Console.WriteLine("PASS large best-effort local edit smoke");
    }

    private static void ProfileDenseMarkdownIncrementalEdit()
    {
        var oldSource = PerformanceProfileChecks.BuildLargeStressSource();
        var probe = oldSource.IndexOf("- [ ] item ", oldSource.Length / 2, StringComparison.Ordinal);
        if (probe < 0)
        {
            probe = oldSource.LastIndexOf("- [ ] item ", StringComparison.Ordinal);
        }
        var editAt = probe + "- [ ] item ".Length;
        var newSource = oldSource.Insert(editAt, "Z");
        var oldSnapshot = MarkdownSemanticSnapshot.Parse(oldSource);
        var windowLength = MarkdownSemanticSnapshot.GetIncrementalWindowLengthForTests(
            oldSource,
            oldSnapshot,
            newSource);

        if (!MarkdownSemanticSnapshot.TryParseIncremental(
                oldSource,
                oldSnapshot,
                newSource,
                out var warm))
        {
            throw new InvalidOperationException("FAIL dense incremental profile: fallback");
        }
        ValidateRanges(warm, newSource.Length, "dense profile warmup");

        const int iterations = 21;
        var samples = new double[iterations];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var stopwatch = Stopwatch.StartNew();
            if (!MarkdownSemanticSnapshot.TryParseIncremental(
                    oldSource,
                    oldSnapshot,
                    newSource,
                    out var result))
            {
                throw new InvalidOperationException("FAIL dense incremental profile: fallback during timing");
            }
            stopwatch.Stop();
            GC.KeepAlive(result);
            samples[iteration] = stopwatch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        var p50 = samples[samples.Length / 2];
        var p95 = samples[(int)Math.Ceiling(samples.Length * 0.95) - 1];
        Console.WriteLine(
            $"PROFILE IncrementalDense98k window={windowLength} p50={p50:F3}ms p95={p95:F3}ms");
    }

    private static void ValidateRanges(
        MarkdownSemanticSnapshot snapshot,
        int sourceLength,
        string name)
    {
        foreach (var span in snapshot.Spans)
        {
            if (span.Start < 0 || span.End < span.Start || span.End > sourceLength)
            {
                throw new InvalidOperationException(
                    $"FAIL {name}: invalid span {span.Start}..{span.End} / {sourceLength}");
            }
        }
        foreach (var link in snapshot.Links)
        {
            if (link.Start < 0 || link.End < link.Start || link.End > sourceLength)
            {
                throw new InvalidOperationException(
                    $"FAIL {name}: invalid link {link.Start}..{link.End} / {sourceLength}");
            }
        }
    }
}
