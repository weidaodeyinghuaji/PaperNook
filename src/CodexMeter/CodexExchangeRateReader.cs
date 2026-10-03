using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;

namespace PaperTodo;

internal sealed class CodexExchangeRateReader
{
    internal const string SourceUrl = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly string _cachePath;

    internal CodexExchangeRateReader(string cacheRoot)
    {
        _cachePath = Path.Combine(cacheRoot, "exchange-rate.json");
    }

    internal async Task<CodexCurrencyRate?> ReadCnyAsync(CancellationToken cancellationToken)
    {
        var cached = ReadCache();
        if (cached != null && DateTimeOffset.UtcNow - cached.CheckedAt < TimeSpan.FromHours(24)) return cached;
        try
        {
            using var response = await Client.GetAsync(SourceUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var rate = Parse(xml) with { CheckedAt = DateTimeOffset.UtcNow, Source = "ecb" };
            SaveCache(rate);
            return rate;
        }
        catch when (cached != null && DateTimeOffset.UtcNow - cached.CheckedAt < TimeSpan.FromDays(7))
        {
            return cached with { Source = "ecb-cache" };
        }
        catch
        {
            return null;
        }
    }

    internal static CodexCurrencyRate Parse(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.None);
        var rateNodes = document.Descendants().Where(element => element.Name.LocalName == "Cube");
        var dateNode = rateNodes.FirstOrDefault(element => element.Attribute("time") != null);
        var usd = ParseRate(rateNodes, "USD");
        var cny = ParseRate(rateNodes, "CNY");
        if (usd <= 0 || cny <= 0) throw new FormatException("ECB exchange-rate response did not contain valid USD and CNY rates.");
        return new CodexCurrencyRate
        {
            CurrencyCode = "CNY",
            UsdToCurrency = cny / usd,
            RateDate = DateOnly.TryParseExact(
                dateNode?.Attribute("time")?.Value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date) ? date : null,
            CheckedAt = DateTimeOffset.UtcNow,
            Source = "ecb"
        };
    }

    private static double ParseRate(IEnumerable<XElement> nodes, string currency)
    {
        var value = nodes.FirstOrDefault(element =>
            string.Equals(element.Attribute("currency")?.Value, currency, StringComparison.OrdinalIgnoreCase))
            ?.Attribute("rate")?.Value;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ? rate : 0;
    }

    private CodexCurrencyRate? ReadCache()
    {
        try
        {
            return File.Exists(_cachePath)
                ? JsonSerializer.Deserialize<CodexCurrencyRate>(File.ReadAllText(_cachePath), CodexMeterJson.Options)
                : null;
        }
        catch { return null; }
    }

    private void SaveCache(CodexCurrencyRate rate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        var temporary = _cachePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(rate, CodexMeterJson.Options));
        File.Move(temporary, _cachePath, overwrite: true);
    }
}
