using System.Text.Json.Serialization;

namespace PaperTodo;

internal sealed record CodexTokenUsage
{
    public long InputTokens { get; init; }
    public long CachedInputTokens { get; init; }
    public long CacheCreationInputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long ReasoningOutputTokens { get; init; }
    public long TotalTokens { get; init; }

    public static CodexTokenUsage operator +(CodexTokenUsage left, CodexTokenUsage right) =>
        new()
        {
            InputTokens = left.InputTokens + right.InputTokens,
            CachedInputTokens = left.CachedInputTokens + right.CachedInputTokens,
            CacheCreationInputTokens = left.CacheCreationInputTokens + right.CacheCreationInputTokens,
            OutputTokens = left.OutputTokens + right.OutputTokens,
            ReasoningOutputTokens = left.ReasoningOutputTokens + right.ReasoningOutputTokens,
            TotalTokens = left.TotalTokens + right.TotalTokens
        };
}

internal sealed record CodexRateLimitWindow
{
    public double? UsedPercent { get; init; }
    public double? RemainingPercent { get; init; }
    public int? WindowMinutes { get; init; }
    public DateTimeOffset? ResetAt { get; init; }
}

internal sealed record CodexRateLimits
{
    public CodexRateLimitWindow? FiveHour { get; init; }
    public CodexRateLimitWindow? SevenDay { get; init; }
    public string? PlanType { get; init; }
    public DateTimeOffset CheckedAt { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public string Source { get; init; } = "unavailable";
    public string? Message { get; init; }
    public CodexQuotaDiagnostics? Diagnostics { get; init; }
}

internal sealed record CodexQuotaDiagnostics
{
    public string CliVersion { get; init; } = "";
    public string Stage { get; init; } = "";
    public long? InitializeMilliseconds { get; init; }
    public long? ReadMilliseconds { get; init; }
    public string RequestShape { get; init; } = "unit";
}

internal sealed record CodexProjectUsage
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public long TotalTokens { get; init; }
    public int SessionCount { get; init; }
}

internal sealed record CodexUsagePeriod
{
    public string Key { get; init; } = "";
    public CodexTokenUsage Usage { get; init; } = new();
    public double? CostUsd { get; init; }
}

internal sealed record CodexDailyUsage
{
    public DateOnly Date { get; init; }
    public CodexTokenUsage Usage { get; init; } = new();
    public double? CostUsd { get; init; }
}

internal sealed record CodexCurrencyRate
{
    public string CurrencyCode { get; init; } = "USD";
    public double UsdToCurrency { get; init; } = 1;
    public DateOnly? RateDate { get; init; }
    public DateTimeOffset CheckedAt { get; init; }
    public string Source { get; init; } = "fixed-usd";
}

internal sealed record CodexQuotaCycleSummary
{
    public string AccountKey { get; init; } = "local";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset ResetAt { get; init; }
    public DateTimeOffset LastObservedAt { get; init; }
    public double? RemainingPercent { get; init; }
    public CodexTokenUsage Usage { get; init; } = new();
    public double? CostUsd { get; init; }
}

internal sealed record CodexComponentSummary
{
    public int SkillCount { get; init; }
    public int PluginCount { get; init; }
    public int McpServerCount { get; init; }
    public IReadOnlyList<string> Skills { get; init; } = [];
    public IReadOnlyList<string> Plugins { get; init; } = [];
    public IReadOnlyList<string> McpServers { get; init; } = [];
}

internal sealed record CodexNetworkProbe
{
    public string Name { get; init; } = "";
    public bool Reachable { get; init; }
    public int? StatusCode { get; init; }
    public long? LatencyMilliseconds { get; init; }
    public string? Error { get; init; }
}

internal sealed record CodexNetworkSnapshot
{
    public DateTimeOffset CheckedAt { get; init; }
    public IReadOnlyList<CodexNetworkProbe> Probes { get; init; } = [];
}

internal sealed record CodexMeterSnapshot
{
    public DateTimeOffset? ActivityEventAt { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    public string AccountLabel { get; init; } = "";
    public string AccountKey { get; init; } = "local";
    public string ActivityKind { get; init; } = "idle";
    public string ActivityText { get; init; } = "";
    public int ActiveSessions { get; init; }
    public CodexTokenUsage Usage { get; init; } = new();
    public double? CostUsd { get; init; }
    public CodexCurrencyRate Currency { get; init; } = new();
    public CodexRateLimitWindow? FiveHour { get; init; }
    public CodexRateLimitWindow? SevenDay { get; init; }
    public string QuotaSource { get; init; } = "unavailable";
    public DateTimeOffset? QuotaObservedAt { get; init; }
    public CodexQuotaDiagnostics? QuotaDiagnostics { get; init; }
    public string? Message { get; init; }
    public int SessionCount { get; init; }
    public IReadOnlyList<CodexProjectUsage> Projects { get; init; } = [];
    public IReadOnlyList<CodexUsagePeriod> Periods { get; init; } = [];
    public IReadOnlyList<CodexDailyUsage> DailyUsage { get; init; } = [];
    public IReadOnlyList<CodexQuotaCycleSummary> QuotaCycles { get; init; } = [];
    public IReadOnlyList<CodexSessionItem> RecentSessions { get; init; } = [];
    public IReadOnlyList<CodexTrashItem> RecycledSessions { get; init; } = [];
    public CodexComponentSummary Components { get; init; } = new();
    public CodexNetworkSnapshot? Network { get; init; }
}

internal sealed record CodexTimedUsage
{
    public DateTimeOffset Timestamp { get; init; }
    public string Model { get; init; } = "unknown";
    public CodexTokenUsage Usage { get; init; } = new();
}

internal sealed record CodexSessionSummary
{
    public string FilePath { get; init; } = "";
    public long Size { get; init; }
    public long LastWriteUtcTicks { get; init; }
    public string SessionId { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string Model { get; init; } = "unknown";
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string LastEventKind { get; init; } = "idle";
    public CodexTokenUsage Totals { get; init; } = new();
    public CodexTokenUsage? LastRawTotals { get; init; }
    public IReadOnlyList<CodexTimedUsage> TimedUsage { get; init; } = [];
    public CodexRateLimitObservation? LatestRateLimits { get; init; }
}

internal sealed record CodexSessionItem
{
    public string SessionId { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string Model { get; init; } = "unknown";
    public DateTimeOffset? UpdatedAt { get; init; }
    public long TotalTokens { get; init; }
}

internal sealed record CodexTrashItem
{
    public string Id { get; init; } = "";
    public string SessionId { get; init; } = "";
    public DateTimeOffset DeletedAt { get; init; }
}

internal sealed record CodexRateLimitObservation
{
    public DateTimeOffset ObservedAt { get; init; }
    public CodexRateLimits Limits { get; init; } = new();
}

internal sealed record CodexAccountRange
{
    public DateTimeOffset From { get; init; }
    public DateTimeOffset? To { get; set; }
}

internal sealed record CodexAccountProfile
{
    public string Key { get; init; } = "local";
    public string Label { get; init; } = "";
    public IReadOnlyList<CodexAccountRange> Ranges { get; init; } = [];
}

internal sealed record CodexScanResult
{
    public CodexTokenUsage Totals { get; init; } = new();
    public IReadOnlyList<CodexTimedUsage> TimedUsage { get; init; } = [];
    public IReadOnlyList<CodexProjectUsage> Projects { get; init; } = [];
    public int SessionCount { get; init; }
    public int ActiveSessions { get; init; }
    public string ActivityKind { get; init; } = "idle";
    public CodexRateLimitObservation? LatestRateLimits { get; init; }
    public IReadOnlyList<CodexSessionSummary> Sessions { get; init; } = [];
}

internal sealed record CodexMeterRuntimeMessage
{
    public string Type { get; init; } = "snapshot";
    public CodexMeterSnapshot? Snapshot { get; init; }
}
