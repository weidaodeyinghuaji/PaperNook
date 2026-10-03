using System.Globalization;
using System.Text;

namespace PaperTodo;

internal static class CodexDiagnosticSummary
{
    internal static string Build(CodexMeterSnapshot snapshot)
    {
        var diagnostics = snapshot.QuotaDiagnostics;
        var builder = new StringBuilder();
        builder.AppendLine("PaperNook Codex Meter diagnostics");
        builder.Append("Generated: ").AppendLine(snapshot.GeneratedAt.ToString("O", CultureInfo.InvariantCulture));
        builder.Append("Account: ").AppendLine(snapshot.AccountLabel);
        builder.Append("Quota source: ").AppendLine(snapshot.QuotaSource);
        builder.Append("Quota observed: ").AppendLine(snapshot.QuotaObservedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "unavailable");
        builder.Append("CLI version: ").AppendLine(string.IsNullOrWhiteSpace(diagnostics?.CliVersion) ? "unknown" : diagnostics.CliVersion);
        builder.Append("Stage: ").AppendLine(string.IsNullOrWhiteSpace(diagnostics?.Stage) ? "unknown" : diagnostics.Stage);
        builder.Append("Request shape: ").AppendLine(diagnostics?.RequestShape ?? "unknown");
        builder.Append("Initialize ms: ").AppendLine(diagnostics?.InitializeMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? "unavailable");
        builder.Append("Read ms: ").AppendLine(diagnostics?.ReadMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? "unavailable");
        builder.Append("Price catalog: ").Append(CodexCostEstimator.PriceCatalogDate).Append(" · ").AppendLine(CodexCostEstimator.PriceSourceUrl);
        builder.Append("Display currency: ").Append(snapshot.Currency.CurrencyCode)
            .Append(" · rate source ").AppendLine(snapshot.Currency.Source);
        builder.Append("Sessions: ").AppendLine(snapshot.SessionCount.ToString(CultureInfo.InvariantCulture));
        builder.Append("Tokens: ").AppendLine(snapshot.Usage.TotalTokens.ToString(CultureInfo.InvariantCulture));
        builder.Append("Activity drawing latency: ").AppendLine(
            System.Text.Json.JsonSerializer.Serialize(CodexActivityLatency.Summary()));
        return builder.ToString().TrimEnd();
    }
}
