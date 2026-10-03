namespace PaperTodo;

internal static class CodexRefreshPolicy
{
    internal static TimeSpan NextDelay(int configuredSeconds, int consecutiveFailures)
    {
        var configured = Math.Clamp(configuredSeconds, 30, 1800);
        if (consecutiveFailures <= 0) return TimeSpan.FromSeconds(configured);
        var backoff = consecutiveFailures switch
        {
            1 => 30,
            2 => 60,
            3 => 120,
            _ => 300
        };
        return TimeSpan.FromSeconds(Math.Min(configured, backoff));
    }

    internal static bool IsLive(CodexRateLimits limits) =>
        limits.Source.Split('+', StringSplitOptions.RemoveEmptyEntries)
            .Contains("codex-rpc", StringComparer.Ordinal);
}

internal sealed record CodexQuotaSourceState(string Kind, int? AgeMinutes);

internal static class CodexQuotaSourceAnalyzer
{
    internal static CodexQuotaSourceState Analyze(
        string source,
        DateTimeOffset? observedAt,
        DateTimeOffset now)
    {
        var parts = source.Split('+', StringSplitOptions.RemoveEmptyEntries);
        var kind = parts.Length > 1
            ? "mixed"
            : parts.Contains("codex-rpc", StringComparer.Ordinal)
            ? "live"
            : parts.Contains("cache", StringComparer.Ordinal)
                ? "cache"
                : parts.Contains("session", StringComparer.Ordinal)
                    ? "session"
                    : "unavailable";
        var age = observedAt.HasValue
            ? Math.Max(0, (int)Math.Floor((now - observedAt.Value).TotalMinutes))
            : (int?)null;
        return new CodexQuotaSourceState(kind, age);
    }
}
