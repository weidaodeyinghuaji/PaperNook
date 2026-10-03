using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaperTodo;

internal sealed class CodexAccountScope
{
    private const int Schema = 1;
    private readonly Func<string> _readState;
    private readonly Action<string> _saveState;
    private readonly string _authPath;
    private StateDocument _state;
    private string _lastLabel = "";

    private sealed class StateDocument
    {
        public int Schema { get; set; } = CodexAccountScope.Schema;
        public string? ActiveKey { get; set; }
        public Dictionary<string, AccountState> Accounts { get; set; } = new(StringComparer.Ordinal);
        public List<CodexQuotaCycleSummary> QuotaCycles { get; set; } = [];
    }

    private sealed class AccountState
    {
        public DateTimeOffset FirstSeenAt { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
        public List<CodexAccountRange> Ranges { get; set; } = [];
    }

    internal CodexAccountScope(Func<string> readState, Action<string> saveState, string? authPath = null)
    {
        _readState = readState;
        _saveState = saveState;
        _authPath = authPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "auth.json");
        _state = ReadState(readState());
    }

    internal CodexAccountProfile Current(DateTimeOffset? now = null)
    {
        var observedNow = now ?? DateTimeOffset.UtcNow;
        var auth = ReadAuth(_authPath);
        var key = auth.AccountId == null
            ? _state.ActiveKey ?? "local"
            : HashAccountId(auth.AccountId);
        var observedAt = observedNow;
        try
        {
            var write = File.GetLastWriteTimeUtc(_authPath);
            if (write != DateTime.MinValue)
            {
                var fileTime = new DateTimeOffset(write, TimeSpan.Zero);
                if (fileTime < observedAt) observedAt = fileTime;
            }
        }
        catch { }

        var changed = false;
        if (!_state.Accounts.TryGetValue(key, out var account))
        {
            account = new AccountState
            {
                FirstSeenAt = observedAt,
                LastSeenAt = observedNow,
                Ranges = [new CodexAccountRange { From = observedAt }]
            };
            _state.Accounts[key] = account;
            changed = true;
        }
        if (account.Ranges.Count == 0)
        {
            account.Ranges.Add(new CodexAccountRange { From = account.FirstSeenAt });
            changed = true;
        }

        if (_state.ActiveKey != null &&
            !string.Equals(_state.ActiveKey, key, StringComparison.Ordinal) &&
            _state.Accounts.TryGetValue(_state.ActiveKey, out var previous))
        {
            var open = previous.Ranges.LastOrDefault(range => range.To == null);
            if (open != null)
            {
                open.To = observedAt > open.From ? observedAt : open.From;
                changed = true;
            }
            if (!account.Ranges.Any(range => range.To == null))
            {
                var lastEnd = account.Ranges.LastOrDefault()?.To;
                account.Ranges.Add(new CodexAccountRange
                {
                    From = lastEnd.HasValue && lastEnd.Value > observedAt ? lastEnd.Value : observedAt
                });
                changed = true;
            }
        }
        else if (!account.Ranges.Any(range => range.To == null))
        {
            account.Ranges.Add(new CodexAccountRange { From = observedAt });
            changed = true;
        }

        if (!string.Equals(_state.ActiveKey, key, StringComparison.Ordinal))
        {
            _state.ActiveKey = key;
            changed = true;
        }
        account.LastSeenAt = observedNow;
        if (changed) Save();

        var label = auth.Label;
        if (string.IsNullOrWhiteSpace(label))
        {
            label = string.Equals(key, "local", StringComparison.Ordinal)
                ? Strings.Get("CodexMeterLocalAccount")
                : !string.IsNullOrWhiteSpace(_lastLabel)
                    ? _lastLabel
                    : Strings.Format("CodexMeterAccountFormat", key[..Math.Min(6, key.Length)].ToUpperInvariant());
        }
        _lastLabel = label;
        return new CodexAccountProfile
        {
            Key = key,
            Label = label,
            Ranges = account.Ranges.Select(range => new CodexAccountRange
            {
                From = range.From,
                To = range.To
            }).ToArray()
        };
    }

    internal CodexAccountProfile AllAccounts(DateTimeOffset? now = null)
    {
        Current(now);
        return new CodexAccountProfile
        {
            Key = "all",
            Label = Strings.Get("CodexMeterAllAccounts"),
            Ranges = _state.Accounts.Values
                .SelectMany(account => account.Ranges)
                .Select(range => new CodexAccountRange { From = range.From, To = range.To })
                .OrderBy(range => range.From)
                .ToArray()
        };
    }

    internal void ObserveQuotaCycle(
        string accountKey,
        CodexRateLimits limits,
        CodexTokenUsage usage,
        double? costUsd)
    {
        var window = limits.SevenDay;
        if (window?.ResetAt is not { } resetAt ||
            window.WindowMinutes is not { } minutes ||
            window.RemainingPercent is not { } remaining ||
            !(CodexRefreshPolicy.IsLive(limits) || limits.Source.Split('+').Contains("session", StringComparer.Ordinal)))
        {
            return;
        }

        var observedAt = limits.ObservedAt ?? limits.CheckedAt;
        if (observedAt == default) observedAt = DateTimeOffset.UtcNow;
        var index = _state.QuotaCycles.FindIndex(cycle =>
            string.Equals(cycle.AccountKey, accountKey, StringComparison.Ordinal) &&
            cycle.ResetAt == resetAt);
        var cycle = new CodexQuotaCycleSummary
        {
            AccountKey = accountKey,
            StartedAt = resetAt - TimeSpan.FromMinutes(minutes),
            ResetAt = resetAt,
            LastObservedAt = observedAt,
            RemainingPercent = Math.Clamp(remaining, 0, 100),
            Usage = usage,
            CostUsd = costUsd
        };
        if (index >= 0) _state.QuotaCycles[index] = cycle;
        else _state.QuotaCycles.Add(cycle);

        _state.QuotaCycles = _state.QuotaCycles
            .GroupBy(item => item.AccountKey, StringComparer.Ordinal)
            .SelectMany(group => group.OrderByDescending(item => item.ResetAt).Take(12))
            .OrderBy(item => item.AccountKey, StringComparer.Ordinal)
            .ThenByDescending(item => item.ResetAt)
            .ToList();
        Save();
    }

    internal IReadOnlyList<CodexQuotaCycleSummary> GetQuotaCycles(string accountKey) =>
        _state.QuotaCycles
            .Where(cycle => string.Equals(accountKey, "all", StringComparison.Ordinal) ||
                            string.Equals(cycle.AccountKey, accountKey, StringComparison.Ordinal))
            .OrderByDescending(cycle => cycle.ResetAt)
            .Take(12)
            .ToArray();

    internal static string HashAccountId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }

    internal static string MaskEmail(string? value)
    {
        var email = value?.Trim() ?? "";
        var at = email.IndexOf('@');
        if (at <= 0) return "";
        var visible = email[..Math.Min(2, at)];
        return $"{visible}***{email[at..]}";
    }

    private void Save()
    {
        _state.Schema = Schema;
        _saveState(JsonSerializer.Serialize(_state, CodexMeterJson.Options));
    }

    private static StateDocument ReadState(string json)
    {
        try
        {
            var state = JsonSerializer.Deserialize<StateDocument>(json, CodexMeterJson.Options);
            return state is { Schema: Schema } ? state : new StateDocument();
        }
        catch
        {
            return new StateDocument();
        }
    }

    private static (string? AccountId, string Label) ReadAuth(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var tokens = root.TryGetProperty("tokens", out var tokenObject) ? tokenObject : default;
            var accountId = String(tokens, "account_id") ?? String(root, "account_id");
            var idToken = String(tokens, "id_token");
            var claims = DecodeJwtPayload(idToken);
            var email = claims.HasValue ? String(claims.Value, "email") : null;
            var name = claims.HasValue ? String(claims.Value, "name") : null;
            return (accountId, MaskEmail(email) is { Length: > 0 } masked ? masked : name?.Trim() ?? "");
        }
        catch
        {
            return (null, "");
        }
    }

    private static JsonElement? DecodeJwtPayload(string? token)
    {
        try
        {
            var parts = token?.Split('.');
            if (parts is not { Length: >= 2 }) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string? String(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

internal static class CodexMeterJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
