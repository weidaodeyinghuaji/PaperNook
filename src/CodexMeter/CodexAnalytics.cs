using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PaperTodo;

internal static class CodexAnalytics
{
    internal static (IReadOnlyList<CodexUsagePeriod> Periods, IReadOnlyList<CodexDailyUsage> Daily)
        Build(IReadOnlyList<CodexTimedUsage> rows, DateTimeOffset now)
    {
        var localNow = now.ToLocalTime();
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var periods = new[]
        {
            Period("today", rows, localNow.Date),
            Period("7d", rows, localNow.Date.AddDays(-6)),
            Period("30d", rows, localNow.Date.AddDays(-29)),
            Period("90d", rows, localNow.Date.AddDays(-89))
        };

        var daily = rows
            .Where(row => row.Timestamp.ToLocalTime() >= localNow.Date.AddDays(-29))
            .GroupBy(row => DateOnly.FromDateTime(row.Timestamp.ToLocalTime().DateTime))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var dailyRows = Enumerable.Range(0, 30)
            .Select(offset => today.AddDays(offset - 29))
            .Select(date =>
            {
                var matching = daily.GetValueOrDefault(date) ?? [];
                return new CodexDailyUsage
                {
                    Date = date,
                    Usage = Sum(matching),
                    CostUsd = CodexCostEstimator.Estimate(matching)
                };
            })
            .ToArray();
        return (periods, dailyRows);
    }

    private static CodexUsagePeriod Period(string key, IReadOnlyList<CodexTimedUsage> rows, DateTimeOffset from)
    {
        var matching = rows.Where(row => row.Timestamp.ToLocalTime() >= from).ToArray();
        return new CodexUsagePeriod
        {
            Key = key,
            Usage = Sum(matching),
            CostUsd = CodexCostEstimator.Estimate(matching)
        };
    }

    private static CodexTokenUsage Sum(IEnumerable<CodexTimedUsage> rows)
    {
        var total = new CodexTokenUsage();
        foreach (var row in rows) total += row.Usage;
        return total;
    }
}

internal static class CodexMeterExport
{
    internal static string Json(CodexMeterSnapshot snapshot) => JsonSerializer.Serialize(new
    {
        schema = 1,
        generatedAt = snapshot.GeneratedAt,
        account = snapshot.AccountLabel,
        activity = snapshot.ActivityKind,
        activeSessions = snapshot.ActiveSessions,
        quotas = new { fiveHour = snapshot.FiveHour, sevenDay = snapshot.SevenDay, source = snapshot.QuotaSource },
        usage = snapshot.Usage,
        estimatedCostUsd = snapshot.CostUsd,
        sessionCount = snapshot.SessionCount,
        periods = snapshot.Periods,
        daily = snapshot.DailyUsage,
        projects = snapshot.Projects.Select(project => new
        {
            project.Name,
            project.TotalTokens,
            project.SessionCount
        })
    }, CodexMeterJson.Options);

    internal static string Csv(CodexMeterSnapshot snapshot)
    {
        var builder = new StringBuilder("date,total_tokens,input_tokens,cached_input_tokens,output_tokens,estimated_cost_usd\r\n");
        foreach (var day in snapshot.DailyUsage)
        {
            builder.Append(day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                .Append(day.Usage.TotalTokens.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(day.Usage.InputTokens.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(day.Usage.CachedInputTokens.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(day.Usage.OutputTokens.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(day.CostUsd?.ToString("0.########", CultureInfo.InvariantCulture) ?? "").Append("\r\n");
        }
        return builder.ToString();
    }
}
