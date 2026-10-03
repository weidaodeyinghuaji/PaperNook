using System.Runtime.CompilerServices;
using PaperTodo.Plugin;

namespace PaperTodo;

// Measures the observable rollout timestamp to the first visible WPF drawing pass.
// This is not a GPU presentation fence or a measurement of unpublished Codex reasoning.
internal static class CodexActivityLatency
{
    private sealed record Stamp(DateTimeOffset At);
    private static readonly ConditionalWeakTable<PaperCapsuleComponent, Stamp> Stamps = new();
    private static readonly object Gate = new();
    private static readonly Queue<(DateTimeOffset At, string Kind, double Milliseconds)> Samples = new();

    internal static void Bind(PaperCapsuleComponent component, DateTimeOffset? at)
    {
        if (at.HasValue) Stamps.AddOrUpdate(component, new Stamp(at.Value));
    }

    internal static DateTimeOffset? Timestamp(PaperCapsuleComponent component) =>
        Stamps.TryGetValue(component, out var stamp) ? stamp.At : null;

    internal static void Copy(PaperCapsuleComponent source, PaperCapsuleComponent destination) =>
        Bind(destination, Timestamp(source));

    internal static void Drawn(DateTimeOffset? at, string kind)
    {
        if (!at.HasValue) return;
        var milliseconds = (DateTimeOffset.UtcNow - at.Value).TotalMilliseconds;
        // Negative clocks and stale re-opened surfaces are not latency samples.
        if (milliseconds < 0 || milliseconds > 60_000) return;
        lock (Gate)
        {
            if (Samples.Any(sample => sample.At == at && sample.Kind == kind)) return;
            Samples.Enqueue((at.Value, kind, milliseconds));
            while (Samples.Count > 128) Samples.Dequeue();
        }
    }

    internal static object Summary()
    {
        lock (Gate)
        {
            return new
            {
                boundary = "rollout-event-timestamp-to-visible-WPF-draw; not internal reasoning or GPU presentation",
                samples = Samples.Count,
                targetMilliseconds = 500,
                budgetMilliseconds = 1500,
                maxMilliseconds = Samples.Count == 0 ? (double?)null : Math.Round(Samples.Max(s => s.Milliseconds)),
                overBudget = Samples.Count(s => s.Milliseconds > 1500)
            };
        }
    }
}
