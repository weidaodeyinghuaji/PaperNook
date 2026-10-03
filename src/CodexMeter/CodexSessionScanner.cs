using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaperTodo;

internal sealed partial class CodexSessionScanner
{
    private const int CacheSchema = 1;
    internal static readonly TimeSpan ActivityStaleAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TimedUsageRetention = TimeSpan.FromDays(100);
    private readonly string _sessionsDirectory;
    private readonly string _cacheRoot;
    private readonly IDurableAtomicFileWriter _writer;

    internal static string DefaultSessionsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

    private sealed class CacheDocument
    {
        public int Schema { get; set; } = CacheSchema;
        public string ScopeFingerprint { get; set; } = "";
        public List<CodexSessionSummary> Sessions { get; set; } = [];
    }

    internal CodexSessionScanner(
        string cacheRoot,
        string? sessionsDirectory = null,
        IDurableAtomicFileWriter? writer = null)
    {
        _sessionsDirectory = sessionsDirectory ?? DefaultSessionsDirectory;
        _cacheRoot = cacheRoot;
        _writer = writer ?? DurableAtomicFileWriter.Shared;
    }

    internal async Task<CodexScanResult> ScanAsync(
        CodexAccountProfile account,
        bool force,
        CancellationToken cancellationToken)
    {
        var fingerprint = ScopeFingerprint(account.Ranges);
        var cachePath = Path.Combine(_cacheRoot, "accounts", account.Key, "workspace-cache.json");
        var cache = ReadCache(cachePath, fingerprint);
        if (cache.Sessions.Count == 0)
        {
            cache = TryImportLegacyCache(account.Key, fingerprint) ?? cache;
        }
        var byPath = cache.Sessions.ToDictionary(
            item => PathKey(item.FilePath),
            StringComparer.OrdinalIgnoreCase);
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in EnumerateSessionFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info;
            try { info = new FileInfo(path); }
            catch { continue; }
            var key = PathKey(path);
            live.Add(key);
            byPath.TryGetValue(key, out var existing);
            if (!force &&
                existing != null &&
                existing.Size == info.Length &&
                existing.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
            {
                continue;
            }

            try
            {
                byPath[key] = !force && existing != null &&
                              existing.Size > 0 && info.Length > existing.Size
                    ? await ParseIncrementalAsync(path, info, existing, account.Ranges, cancellationToken)
                        .ConfigureAwait(false)
                    : await ParseAsync(path, info, account.Ranges, cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // A concurrently written or unreadable rollout must not discard its last trusted
                // cache entry. A later refresh will retry it.
            }
        }

        foreach (var key in byPath.Keys.Where(key => !live.Contains(key)).ToArray())
        {
            byPath.Remove(key);
        }

        var saved = new CacheDocument
        {
            ScopeFingerprint = fingerprint,
            Sessions = byPath.Values.OrderBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase).ToList()
        };
        WriteCache(cachePath, saved);
        return BuildResult(saved.Sessions, DateTimeOffset.UtcNow);
    }

    // Activity must not wait for the full usage/quota refresh. Read only the tail of the
    // changed rollout; incomplete lines are ignored and retried on the next write.
    internal static async Task<CodexActivityObservation?> ReadLatestActivityAsync(
        string path, CancellationToken cancellationToken)
    {
        const int maxTailBytes = 256 * 1024;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var start = Math.Max(0, stream.Length - maxTailBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: start == 0,
            bufferSize: 16 * 1024, leaveOpen: false);
        if (start > 0) _ = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        CodexActivityObservation? latest = null;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!TryTimestamp(root, out var timestamp)) continue;
                var kind = ClassifyActivity(String(root, "type") ?? "", Object(root, "payload"), "");
                if (kind.Length > 0 && (latest == null || timestamp >= latest.Timestamp))
                    latest = new CodexActivityObservation(path, kind, timestamp);
            }
            catch (JsonException) { }
        }
        return latest;
    }

    internal static CodexScanResult BuildResult(
        IReadOnlyList<CodexSessionSummary> sessions,
        DateTimeOffset now)
    {
        var totals = new CodexTokenUsage();
        var timed = new List<CodexTimedUsage>();
        var projects = new Dictionary<string, (string Path, CodexTokenUsage Usage, int Sessions)>(
            StringComparer.OrdinalIgnoreCase);
        var active = 0;
        var newestActivity = DateTimeOffset.MinValue;
        var activityKind = "idle";
        CodexRateLimitObservation? latestLimits = null;

        foreach (var session in sessions)
        {
            totals += session.Totals;
            timed.AddRange(session.TimedUsage);
            var path = NormalizePath(session.WorkingDirectory);
            var key = string.IsNullOrWhiteSpace(path) ? "__unknown__" : path;
            projects.TryGetValue(key, out var project);
            projects[key] = (path, (project.Usage ?? new CodexTokenUsage()) + session.Totals, project.Sessions + 1);

            var updated = session.UpdatedAt ?? new DateTimeOffset(session.LastWriteUtcTicks, TimeSpan.Zero);
            if (now - updated <= TimeSpan.FromMinutes(2)) active++;
            if (updated > newestActivity)
            {
                newestActivity = updated;
                activityKind = now - updated <= ActivityStaleAfter
                    ? NormalizeActivityKind(session.LastEventKind)
                    : session.LastEventKind == "idle" ? "idle" : "unknown";
            }
            if (session.LatestRateLimits != null &&
                (latestLimits == null || session.LatestRateLimits.ObservedAt > latestLimits.ObservedAt))
            {
                latestLimits = session.LatestRateLimits;
            }
        }

        return new CodexScanResult
        {
            Totals = totals,
            TimedUsage = timed.OrderBy(item => item.Timestamp).ToArray(),
            Projects = projects.Values
                .Select(item => new CodexProjectUsage
                {
                    Name = string.IsNullOrWhiteSpace(item.Path)
                        ? Strings.Get("CodexMeterUnknownProject")
                        : Path.GetFileName(item.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name
                            ? name
                            : item.Path,
                    Path = string.IsNullOrWhiteSpace(item.Path) ? Strings.Get("CodexMeterUnknownProject") : item.Path,
                    TotalTokens = item.Usage.TotalTokens,
                    SessionCount = item.Sessions
                })
                .OrderByDescending(item => item.TotalTokens)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            SessionCount = sessions.Count(item => item.Totals.TotalTokens > 0),
            ActiveSessions = active,
            ActivityKind = activityKind,
            LatestRateLimits = latestLimits,
            Sessions = sessions
                .OrderByDescending(item => item.UpdatedAt ?? new DateTimeOffset(item.LastWriteUtcTicks, TimeSpan.Zero))
                .ToArray()
        };
    }

    internal static async Task<CodexSessionSummary> ParseAsync(
        string path,
        FileInfo info,
        IReadOnlyList<CodexAccountRange> accountRanges,
        CancellationToken cancellationToken)
    {
        var state = new ParseState { SessionId = RolloutId(path) };
        await ReadIntoStateAsync(path, offset: 0, state, accountRanges, cancellationToken)
            .ConfigureAwait(false);
        return state.ToSummary(path, info);
    }

    private static async Task<CodexSessionSummary> ParseIncrementalAsync(
        string path,
        FileInfo info,
        CodexSessionSummary existing,
        IReadOnlyList<CodexAccountRange> accountRanges,
        CancellationToken cancellationToken)
    {
        var state = ParseState.From(existing);
        await ReadIntoStateAsync(path, existing.Size, state, accountRanges, cancellationToken)
            .ConfigureAwait(false);
        return state.ToSummary(path, info);
    }

    private sealed class ParseState
    {
        internal string SessionId { get; set; } = "";
        internal string WorkingDirectory { get; set; } = "";
        internal string Model { get; set; } = "unknown";
        internal string Provider { get; set; } = "unknown";
        internal string LastEventKind { get; set; } = "idle";
        internal DateTimeOffset? StartedAt { get; set; }
        internal DateTimeOffset? UpdatedAt { get; set; }
        internal CodexTokenUsage? PreviousRawTotals { get; set; }
        internal CodexTokenUsage Totals { get; set; } = new();
        internal List<CodexTimedUsage> Timed { get; set; } = [];
        internal CodexRateLimitObservation? LatestLimits { get; set; }

        internal static ParseState From(CodexSessionSummary summary) => new()
        {
            SessionId = summary.SessionId,
            WorkingDirectory = summary.WorkingDirectory,
            Model = summary.Model,
            LastEventKind = summary.LastEventKind,
            StartedAt = summary.StartedAt,
            UpdatedAt = summary.UpdatedAt,
            PreviousRawTotals = summary.LastRawTotals,
            Totals = summary.Totals,
            Timed = summary.TimedUsage.ToList(),
            LatestLimits = summary.LatestRateLimits
        };

        internal CodexSessionSummary ToSummary(string path, FileInfo info) => new()
        {
            FilePath = Path.GetFullPath(path),
            Size = info.Length,
            LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
            SessionId = SessionId,
            WorkingDirectory = NormalizePath(WorkingDirectory),
            Model = Model != "unknown" ? Model : Provider,
            StartedAt = StartedAt,
            UpdatedAt = UpdatedAt,
            LastEventKind = LastEventKind,
            Totals = Totals,
            LastRawTotals = PreviousRawTotals,
            TimedUsage = Timed,
            LatestRateLimits = LatestLimits
        };
    }

    private static async Task ReadIntoStateAsync(
        string path,
        long offset,
        ParseState state,
        IReadOnlyList<CodexAccountRange> accountRanges,
        CancellationToken cancellationToken)
    {
        var retentionCutoff = DateTimeOffset.UtcNow - TimedUsageRetention;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var discardFirstLine = false;
        if (offset > 0 && offset <= stream.Length)
        {
            stream.Seek(offset - 1, SeekOrigin.Begin);
            discardFirstLine = stream.ReadByte() is not ('\n' or '\r');
            stream.Seek(offset, SeekOrigin.Begin);
        }
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: offset == 0,
            bufferSize: 64 * 1024, leaveOpen: false);
        if (discardFirstLine) _ = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch { continue; }
            using (document)
            {
                var root = document.RootElement;
                if (!TryTimestamp(root, out var timestamp)) continue;
                if (!state.StartedAt.HasValue || timestamp < state.StartedAt) state.StartedAt = timestamp;
                if (!state.UpdatedAt.HasValue || timestamp > state.UpdatedAt) state.UpdatedAt = timestamp;

                var type = String(root, "type") ?? "";
                var payload = Object(root, "payload");
                if (type == "session_meta" && payload.HasValue)
                {
                    state.SessionId = String(payload.Value, "id") ?? state.SessionId;
                    state.WorkingDirectory = String(payload.Value, "cwd") ?? state.WorkingDirectory;
                    state.Provider = String(payload.Value, "model_provider") ?? state.Provider;
                }
                else if (type == "turn_context" && payload.HasValue)
                {
                    state.WorkingDirectory = String(payload.Value, "cwd") ?? state.WorkingDirectory;
                    state.Model = String(payload.Value, "model") ?? state.Model;
                }

                state.LastEventKind = ClassifyActivity(type, payload, state.LastEventKind);
                var token = ExtractTokenCount(type, payload);
                if (token.Info == null) continue;

                var totalRaw = Usage(token.Info.Value, "total_token_usage");
                var inRange = accountRanges.Count == 0 || accountRanges.Any(range =>
                    timestamp >= range.From && (!range.To.HasValue || timestamp < range.To.Value));
                if (!inRange)
                {
                    if (totalRaw != null) state.PreviousRawTotals = totalRaw;
                    continue;
                }

                var lastRaw = Usage(token.Info.Value, "last_token_usage");
                var delta = PickDelta(lastRaw, totalRaw, state.PreviousRawTotals);
                if (totalRaw != null) state.PreviousRawTotals = totalRaw;
                if (delta is { TotalTokens: > 0 })
                {
                    state.Totals += delta;
                    if (timestamp >= retentionCutoff)
                    {
                        state.Timed.Add(new CodexTimedUsage
                        {
                            Timestamp = timestamp,
                            Model = state.Model != "unknown" ? state.Model : state.Provider,
                            Usage = delta
                        });
                    }
                }

                if (token.RateLimits.HasValue)
                {
                    var mapped = CodexRateLimitReader.MapRateLimits(token.RateLimits.Value);
                    if (mapped.FiveHour != null || mapped.SevenDay != null)
                    {
                        state.LatestLimits = new CodexRateLimitObservation
                        {
                            ObservedAt = timestamp,
                            Limits = mapped with { ObservedAt = timestamp, Source = "session" }
                        };
                    }
                }
            }
        }
        state.Timed = state.Timed
            .Where(item => item.Timestamp >= retentionCutoff)
            .OrderBy(item => item.Timestamp)
            .ToList();
    }

    internal static CodexTokenUsage NormalizeUsage(CodexTokenUsage raw)
    {
        var cached = Math.Max(0, raw.CachedInputTokens);
        var input = Math.Max(0, raw.InputTokens - cached);
        var cacheWrite = Math.Max(0, raw.CacheCreationInputTokens);
        var output = Math.Max(0, raw.OutputTokens);
        return new CodexTokenUsage
        {
            InputTokens = input,
            CachedInputTokens = cached,
            CacheCreationInputTokens = cacheWrite,
            OutputTokens = output,
            ReasoningOutputTokens = Math.Max(0, raw.ReasoningOutputTokens),
            TotalTokens = input + cached + cacheWrite + output
        };
    }

    internal static CodexTokenUsage? PickDelta(
        CodexTokenUsage? lastUsage,
        CodexTokenUsage? totalUsage,
        CodexTokenUsage? previousTotals)
    {
        if (totalUsage != null && previousTotals != null)
        {
            if (totalUsage.TotalTokens < previousTotals.TotalTokens)
            {
                return NormalizeUsage(lastUsage ?? totalUsage);
            }
            return NormalizeUsage(new CodexTokenUsage
            {
                InputTokens = Math.Max(0, totalUsage.InputTokens - previousTotals.InputTokens),
                CachedInputTokens = Math.Max(0, totalUsage.CachedInputTokens - previousTotals.CachedInputTokens),
                CacheCreationInputTokens = Math.Max(0, totalUsage.CacheCreationInputTokens - previousTotals.CacheCreationInputTokens),
                OutputTokens = Math.Max(0, totalUsage.OutputTokens - previousTotals.OutputTokens),
                ReasoningOutputTokens = Math.Max(0, totalUsage.ReasoningOutputTokens - previousTotals.ReasoningOutputTokens),
                TotalTokens = Math.Max(0, totalUsage.TotalTokens - previousTotals.TotalTokens)
            });
        }
        if (lastUsage != null) return NormalizeUsage(lastUsage);
        return totalUsage == null ? null : NormalizeUsage(totalUsage);
    }

    private IEnumerable<string> EnumerateSessionFiles()
    {
        if (!Directory.Exists(_sessionsDirectory)) return [];
        try
        {
            return Directory.EnumerateFiles(_sessionsDirectory, "*.jsonl", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static CacheDocument? TryImportLegacyCache(string accountKey, string fingerprint)
    {
        var legacyRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex-meter");
        var candidates = new[]
        {
            Path.Combine(legacyRoot, "accounts", accountKey, "workspace-cache.json"),
            Path.Combine(legacyRoot, "workspace-cache.json")
        };
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (!root.TryGetProperty("schema", out var schema) || schema.GetInt32() != 7 ||
                    !root.TryGetProperty("sessions", out var sessions) || sessions.ValueKind != JsonValueKind.Array)
                    continue;

                var imported = new List<CodexSessionSummary>();
                foreach (var row in sessions.EnumerateArray())
                {
                    var filePath = String(row, "filePath") ?? "";
                    if (filePath.Length == 0 || !File.Exists(filePath)) continue;
                    var file = new FileInfo(filePath);
                    var timed = new List<CodexTimedUsage>();
                    if (row.TryGetProperty("timedModelTotals", out var timedRows) && timedRows.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var timedRow in timedRows.EnumerateArray())
                        {
                            if (!DateTimeOffset.TryParse(String(timedRow, "timestamp"), out var timestamp)) continue;
                            timed.Add(new CodexTimedUsage
                            {
                                Timestamp = timestamp,
                                Model = String(timedRow, "model") ?? String(row, "model") ?? "unknown",
                                Usage = NormalizeLegacyUsage(timedRow)
                            });
                        }
                    }

                    CodexRateLimitObservation? limits = null;
                    if (TryObject(row, "rateLimits") is { } rateEvent &&
                        TryObject(rateEvent, "value") is { } rateValue &&
                        DateTimeOffset.TryParse(String(rateEvent, "observedAt"), out var observedAt))
                    {
                        var mapped = CodexRateLimitReader.MapRateLimits(rateValue);
                        limits = new CodexRateLimitObservation
                        {
                            ObservedAt = observedAt,
                            Limits = mapped with { ObservedAt = observedAt, Source = "session" }
                        };
                    }

                    imported.Add(new CodexSessionSummary
                    {
                        FilePath = Path.GetFullPath(filePath),
                        Size = file.Length,
                        LastWriteUtcTicks = file.LastWriteTimeUtc.Ticks,
                        SessionId = String(row, "threadId") ?? String(row, "sessionId") ?? RolloutId(filePath),
                        WorkingDirectory = NormalizePath(String(row, "cwd")),
                        Model = String(row, "model") ?? "unknown",
                        StartedAt = ParseTimestamp(String(row, "startedAt")),
                        UpdatedAt = ParseTimestamp(String(row, "updatedAt")) ?? new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                        Totals = TryObject(row, "totals") is { } totalObject ? NormalizeLegacyUsage(totalObject) : new CodexTokenUsage(),
                        TimedUsage = timed,
                        LatestRateLimits = limits
                    });
                }
                if (imported.Count > 0)
                {
                    return new CacheDocument
                    {
                        ScopeFingerprint = fingerprint,
                        Sessions = imported
                    };
                }
            }
            catch
            {
                // The original standalone cache is never changed; native scanning remains the fallback.
            }
        }
        return null;
    }

    private static CodexTokenUsage NormalizeLegacyUsage(JsonElement value) => new()
    {
        InputTokens = Long(value, "input_tokens"),
        CachedInputTokens = Long(value, "cached_input_tokens"),
        CacheCreationInputTokens = Long(value, "cache_creation_input_tokens"),
        OutputTokens = Long(value, "output_tokens"),
        ReasoningOutputTokens = Long(value, "reasoning_output_tokens"),
        TotalTokens = Long(value, "total_tokens")
    };

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private CacheDocument ReadCache(string path, string fingerprint)
    {
        try
        {
            var document = JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(path), CodexMeterJson.Options);
            return document is { Schema: CacheSchema } &&
                   string.Equals(document.ScopeFingerprint, fingerprint, StringComparison.Ordinal)
                ? document
                : new CacheDocument { ScopeFingerprint = fingerprint };
        }
        catch
        {
            return new CacheDocument { ScopeFingerprint = fingerprint };
        }
    }

    private void WriteCache(string path, CacheDocument document)
    {
        try
        {
            _writer.Write(path, JsonSerializer.SerializeToUtf8Bytes(document, CodexMeterJson.Options),
                temp =>
                {
                    try
                    {
                        var value = JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(temp), CodexMeterJson.Options);
                        return value is { Schema: CacheSchema };
                    }
                    catch { return false; }
                });
        }
        catch
        {
            // Cache writes are best-effort; the next refresh can rebuild from rollout files.
        }
    }

    private static (JsonElement? Info, JsonElement? RateLimits) ExtractTokenCount(
        string type,
        JsonElement? payload)
    {
        if (type != "event_msg" || !payload.HasValue) return (null, null);
        var candidate = payload.Value;
        if (String(candidate, "type") != "token_count")
        {
            var message = Object(candidate, "msg");
            if (!message.HasValue || String(message.Value, "type") != "token_count") return (null, null);
            candidate = message.Value;
        }
        return (Object(candidate, "info"), Object(candidate, "rate_limits") ?? Object(candidate, "rateLimits"));
    }

    private static CodexTokenUsage? Usage(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        return new CodexTokenUsage
        {
            InputTokens = Long(usage, "input_tokens"),
            CachedInputTokens = Long(usage, "cached_input_tokens"),
            CacheCreationInputTokens = Long(usage, "cache_creation_input_tokens"),
            OutputTokens = Long(usage, "output_tokens"),
            ReasoningOutputTokens = Long(usage, "reasoning_output_tokens"),
            TotalTokens = Long(usage, "total_tokens")
        };
    }

    private static long Long(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt64(out var number)
            ? Math.Max(0, number)
            : 0;

    private static string ClassifyActivity(string type, JsonElement? payload, string fallback)
    {
        var payloadType = payload.HasValue ? String(payload.Value, "type") ?? "" : "";
        if (type == "response_item")
        {
            return payloadType switch
            {
                "function_call" when payload.HasValue && String(payload.Value, "name") is { } toolName &&
                    (toolName == "request_user_input" || toolName.EndsWith(".request_user_input", StringComparison.Ordinal)) => "waiting",
                "function_call" or "function_call_output" or
                    "custom_tool_call" or "custom_tool_call_output" => "tool",
                "reasoning" => "thinking",
                "message" when payload.HasValue && String(payload.Value, "role") == "assistant" => "answering",
                _ => fallback
            };
        }
        if (type == "event_msg")
        {
            return payloadType switch
            {
                "task_started" or "turn_started" => "thinking",
                "exec_command_begin" or "exec_command_end" => "tool",
                "task_complete" or "turn_complete" or "turn_aborted" => "idle",
                _ => fallback
            };
        }
        return fallback;
    }

    private static string NormalizeActivityKind(string kind) => kind is "thinking" or "tool" or "answering" or "waiting" or "idle" or "unknown" ? kind : "active";

    private static bool TryTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return String(root, "timestamp") is { } text && DateTimeOffset.TryParse(text, out timestamp);
    }

    private static JsonElement? Object(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Object
            ? property
            : null;

    private static JsonElement? TryObject(JsonElement value, string name) => Object(value, name);

    private static string? String(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path); }
        catch { return path.Trim(); }
    }

    private static string PathKey(string path) => NormalizePath(path);

    private static string ScopeFingerprint(IReadOnlyList<CodexAccountRange> ranges)
    {
        var raw = string.Join("|", ranges.Select(range => $"{range.From:O}>{range.To:O}"));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..24];
    }

    private static string RolloutId(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var match = RolloutIdRegex().Match(name);
        return match.Success ? match.Value : name;
    }

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RolloutIdRegex();
}
