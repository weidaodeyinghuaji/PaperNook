using System.Text.RegularExpressions;

namespace PaperTodo;

internal static partial class CodexCostEstimator
{
    internal const string PriceCatalogDate = "2026-07-30";
    internal const string PriceSourceUrl = "https://openai.com/index/advancing-the-price-performance-frontier-with-gpt-5-6/";

    private sealed record Price(Regex Pattern, double Input, double Cached, double CacheWrite, double Output);

    private static readonly Price[] Prices =
    [
        new(Gpt56Terra(), 2, 0.2, 2.5, 12),
        new(Gpt56Luna(), 0.2, 0.02, 0.25, 1.2),
        new(Gpt56Sol(), 5, 0.5, 6.25, 30),
        new(Gpt55(), 5, 0.5, 5, 30),
        new(Gpt54(), 2.5, 0.25, 2.5, 15),
        new(Gpt53Codex(), 1.75, 0.175, 1.75, 14),
        new(Gpt52Codex(), 1.75, 0.175, 1.75, 14),
        new(Gpt5Codex(), 1.25, 0.125, 1.25, 10)
    ];

    internal static double? Estimate(CodexTokenUsage usage, string? model)
    {
        var price = Prices.FirstOrDefault(item => item.Pattern.IsMatch(model?.Trim() ?? ""));
        if (price == null) return null;
        const double million = 1_000_000d;
        return Math.Round(
            usage.InputTokens * price.Input / million +
            usage.CachedInputTokens * price.Cached / million +
            usage.CacheCreationInputTokens * price.CacheWrite / million +
            usage.OutputTokens * price.Output / million,
            8,
            MidpointRounding.AwayFromZero);
    }

    internal static double? Estimate(IEnumerable<CodexTimedUsage> rows)
    {
        var total = 0d;
        var pricedAny = false;
        foreach (var row in rows)
        {
            var value = Estimate(row.Usage, row.Model);
            if (!value.HasValue) continue;
            total += value.Value;
            pricedAny = true;
        }
        return pricedAny ? Math.Round(total, 8, MidpointRounding.AwayFromZero) : null;
    }

    [GeneratedRegex("^gpt-5\\.6-terra(?:-|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Gpt56Terra();
    [GeneratedRegex("^gpt-5\\.6-luna(?:-|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Gpt56Luna();
    [GeneratedRegex("^gpt-5\\.6(?:-sol)?(?:-|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Gpt56Sol();
    [GeneratedRegex("^gpt-5\\.5(?:-|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Gpt55();
    [GeneratedRegex("^gpt-5\\.4(?:-|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Gpt54();
    [GeneratedRegex("^gpt-5\\.3-codex(?:-|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Gpt53Codex();
    [GeneratedRegex("^gpt-5\\.2-codex(?:-|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Gpt52Codex();
    [GeneratedRegex("^gpt-5(?:-codex)?(?:-|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Gpt5Codex();
}
