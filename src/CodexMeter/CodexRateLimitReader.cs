using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PaperTodo;

internal sealed class CodexRateLimitReader
{
    internal const int FiveHourMinutes = 300;
    internal const int SevenDayMinutes = 10080;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan RpcCacheMaximumAge = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SessionFallbackMaximumAge = TimeSpan.FromMinutes(15);
    private readonly string _cacheRoot;
    private readonly IDurableAtomicFileWriter _writer;
    private readonly Func<CancellationToken, Task<(CodexRateLimits Limits, string? Message)>> _fetch;

    private sealed class CacheDocument
    {
        public int Schema { get; set; } = 1;
        public DateTimeOffset ObservedAt { get; set; }
        public CodexRateLimits Limits { get; set; } = new();
    }

    internal CodexRateLimitReader(
        string cacheRoot,
        IDurableAtomicFileWriter? writer = null,
        Func<CancellationToken, Task<(CodexRateLimits Limits, string? Message)>>? fetch = null)
    {
        _cacheRoot = cacheRoot;
        _writer = writer ?? DurableAtomicFileWriter.Shared;
        _fetch = fetch ?? FetchFromAppServerAsync;
    }

    internal async Task<CodexRateLimits> ReadAsync(
        CodexAccountProfile account,
        CodexRateLimitObservation? sessionFallback,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var cachePath = Path.Combine(_cacheRoot, "accounts", account.Key, "rate-limits-cache.json");
        var cached = ReadCache(cachePath);
        var (live, message) = await _fetch(cancellationToken).ConfigureAwait(false);

        var fiveHour = IsWindow(live.FiveHour, FiveHourMinutes) ? live.FiveHour : null;
        var sevenDay = IsWindow(live.SevenDay, SevenDayMinutes) ? live.SevenDay : null;
        var hasLive = fiveHour != null || sevenDay != null;
        var source = new List<string>();
        if (fiveHour != null || sevenDay != null) source.Add("codex-rpc");

        if (cached != null && now - cached.ObservedAt <= RpcCacheMaximumAge)
        {
            if (fiveHour == null && IsUsable(cached.Limits.FiveHour, cached.ObservedAt, now, RpcCacheMaximumAge))
            {
                fiveHour = cached.Limits.FiveHour;
                source.Add("cache");
            }
            if (sevenDay == null && IsUsable(cached.Limits.SevenDay, cached.ObservedAt, now, RpcCacheMaximumAge))
            {
                sevenDay = cached.Limits.SevenDay;
                if (!source.Contains("cache", StringComparer.Ordinal)) source.Add("cache");
            }
        }

        if (sessionFallback != null)
        {
            if (fiveHour == null && IsUsable(sessionFallback.Limits.FiveHour, sessionFallback.ObservedAt, now, SessionFallbackMaximumAge))
            {
                fiveHour = sessionFallback.Limits.FiveHour;
                source.Add("session");
            }
            if (sevenDay == null && IsUsable(sessionFallback.Limits.SevenDay, sessionFallback.ObservedAt, now, SessionFallbackMaximumAge))
            {
                sevenDay = sessionFallback.Limits.SevenDay;
                if (!source.Contains("session", StringComparer.Ordinal)) source.Add("session");
            }
        }

        if (hasLive)
        {
            WriteCache(cachePath, new CacheDocument
            {
                ObservedAt = now,
                Limits = live with
                {
                    FiveHour = IsWindow(live.FiveHour, FiveHourMinutes)
                        ? live.FiveHour
                        : cached?.Limits.FiveHour,
                    SevenDay = IsWindow(live.SevenDay, SevenDayMinutes)
                        ? live.SevenDay
                        : cached?.Limits.SevenDay,
                    CheckedAt = now,
                    ObservedAt = now,
                    Source = "codex-rpc"
                }
            });
        }

        var observations = new List<DateTimeOffset>();
        if (source.Contains("codex-rpc", StringComparer.Ordinal)) observations.Add(now);
        if (source.Contains("cache", StringComparer.Ordinal) && cached != null) observations.Add(cached.ObservedAt);
        if (source.Contains("session", StringComparer.Ordinal) && sessionFallback != null) observations.Add(sessionFallback.ObservedAt);

        return new CodexRateLimits
        {
            FiveHour = fiveHour,
            SevenDay = sevenDay,
            PlanType = live.PlanType ?? cached?.Limits.PlanType ?? sessionFallback?.Limits.PlanType,
            CheckedAt = now,
            ObservedAt = observations.Count == 0 ? null : observations.Min(),
            Source = source.Count == 0 ? "unavailable" : string.Join('+', source.Distinct(StringComparer.Ordinal)),
            Message = message,
            Diagnostics = live.Diagnostics
        };
    }

    internal static CodexRateLimits MapRateLimits(JsonElement response)
    {
        var root = response;
        if (TryObject(root, "result", out var result)) root = result;

        JsonElement source;
        if (TryObject(root, "rateLimitsByLimitId", out var byId) &&
            TryObject(byId, "codex", out var codex))
        {
            source = codex;
        }
        else if (TryObject(root, "rateLimits", out var nested))
        {
            source = nested;
        }
        else
        {
            source = root;
        }

        var candidates = new List<CodexRateLimitWindow>();
        if (TryObject(source, "primary", out var primary) && NormalizeWindow(primary) is { } primaryWindow)
            candidates.Add(primaryWindow);
        if (TryObject(source, "secondary", out var secondary) && NormalizeWindow(secondary) is { } secondaryWindow)
            candidates.Add(secondaryWindow);

        return new CodexRateLimits
        {
            FiveHour = candidates.FirstOrDefault(item => item.WindowMinutes is >= FiveHourMinutes - 1 and <= FiveHourMinutes + 1),
            SevenDay = candidates.FirstOrDefault(item => item.WindowMinutes is >= SevenDayMinutes - 1 and <= SevenDayMinutes + 1),
            PlanType = String(source, "planType") ?? String(source, "plan_type") ?? String(root, "planType") ?? String(root, "plan_type"),
            CheckedAt = DateTimeOffset.UtcNow,
            Source = "codex-rpc"
        };
    }

    internal static bool IsUsable(
        CodexRateLimitWindow? window,
        DateTimeOffset observedAt,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        if (window == null || !window.WindowMinutes.HasValue) return false;
        if (observedAt > now + TimeSpan.FromMinutes(1) || now - observedAt > maximumAge) return false;
        return !window.ResetAt.HasValue || window.ResetAt.Value > now;
    }

    private static CodexRateLimitWindow? NormalizeWindow(JsonElement value)
    {
        var used = Number(value, "usedPercent") ?? Number(value, "used_percent");
        var minutes = Number(value, "windowDurationMins") ?? Number(value, "windowMinutes") ?? Number(value, "window_minutes");
        var reset = Timestamp(value, "resetsAt") ?? Timestamp(value, "resets_at") ?? Timestamp(value, "resetAt");
        if (!used.HasValue && !minutes.HasValue && !reset.HasValue) return null;
        var clamped = used.HasValue ? Math.Clamp(used.Value, 0, 100) : (double?)null;
        return new CodexRateLimitWindow
        {
            UsedPercent = clamped,
            RemainingPercent = clamped.HasValue ? 100 - clamped.Value : null,
            WindowMinutes = minutes.HasValue ? (int)Math.Round(minutes.Value) : null,
            ResetAt = reset
        };
    }

    private async Task<(CodexRateLimits Limits, string? Message)> FetchFromAppServerAsync(CancellationToken cancellationToken)
    {
        var executable = FindCodexBinary();
        if (executable == null)
        {
            return (new CodexRateLimits(), Strings.Get("CodexMeterCodexNotFound"));
        }

        Process? process = null;
        var stage = "start";
        long? initializeMilliseconds = null;
        long? readMilliseconds = null;
        var requestShape = "unit";
        var cliVersion = "";
        CodexRateLimits Failure(string currentStage) => new()
        {
            Diagnostics = new CodexQuotaDiagnostics
            {
                CliVersion = cliVersion,
                Stage = currentStage,
                InitializeMilliseconds = initializeMilliseconds,
                ReadMilliseconds = readMilliseconds,
                RequestShape = requestShape
            }
        };
        try
        {
            try
            {
                cliVersion = FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? "";
            }
            catch { }
            var start = CreateStartInfo(executable);
            process = Process.Start(start);
            if (process == null) throw new InvalidOperationException("Could not start codex app-server.");
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var nextId = 1;

            stage = "initialize";
            var timer = Stopwatch.StartNew();
            await SendAsync(process, new
            {
                id = nextId,
                method = "initialize",
                @params = new
                {
                    clientInfo = new { name = "papernook", title = "PaperNook Codex Meter", version = PaperTodoVersion.Current.ToString() }
                }
            }, cancellationToken).ConfigureAwait(false);
            var initialized = await ReadResponseAsync(process, nextId++, TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);
            initializeMilliseconds = timer.ElapsedMilliseconds;
            if (HasError(initialized, out var initializeError))
                return (Failure("initialize"), Strings.Format("CodexMeterRpcInitializeFailedFormat", initializeError));

            await SendAsync(process, new { method = "initialized", @params = new { } }, cancellationToken).ConfigureAwait(false);
            stage = "rate-limits";
            timer.Restart();
            await SendAsync(process, new { id = nextId, method = "account/rateLimits/read" }, cancellationToken).ConfigureAwait(false);
            var response = await ReadResponseAsync(process, nextId++, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);

            if (ErrorCode(response) is -32600 or -32602)
            {
                requestShape = "object";
                await SendAsync(process, new
                {
                    id = nextId,
                    method = "account/rateLimits/read",
                    @params = new { supportsLunaReserve = true, excludeResetCreditDetails = false }
                }, cancellationToken).ConfigureAwait(false);
                response = await ReadResponseAsync(process, nextId, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            }
            readMilliseconds = timer.ElapsedMilliseconds;
            if (HasError(response, out var readError))
                return (Failure("rate-limits"), Strings.Format("CodexMeterRpcReadFailedFormat", readError));
            return (MapRateLimits(response) with
            {
                Diagnostics = new CodexQuotaDiagnostics
                {
                    CliVersion = cliVersion,
                    Stage = "complete",
                    InitializeMilliseconds = initializeMilliseconds,
                    ReadMilliseconds = readMilliseconds,
                    RequestShape = requestShape
                }
            }, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (Failure(stage), Strings.Get("CodexMeterRpcTimeout"));
        }
        catch (Exception ex)
        {
            return (Failure(stage), Strings.Format("CodexMeterRpcFailedFormat", ex.GetBaseException().Message));
        }
        finally
        {
            if (process != null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
            }
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executable)
    {
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom
        };
        if (Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(executable).Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            info.ArgumentList.Add("/d");
            info.ArgumentList.Add("/s");
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add($"\"{executable}\" app-server");
        }
        else
        {
            info.FileName = executable;
            info.ArgumentList.Add("app-server");
        }
        return info;
    }

    internal static string? FindCodexBinary()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "codex",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            var paths = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return paths.FirstOrDefault(path => path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                ?? paths.FirstOrDefault(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                ?? paths.FirstOrDefault(File.Exists);
        }
        catch
        {
            return null;
        }
    }

    private static async Task SendAsync(Process process, object payload, CancellationToken cancellationToken)
    {
        // app-server uses one compact JSON object per stdout/stdin line.
        var line = JsonSerializer.Serialize(payload);
        await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReadResponseAsync(
        Process process,
        int id,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false);
            if (line == null) throw new EndOfStreamException("codex app-server closed stdout.");
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var responseId) && responseId.TryGetInt32(out var value) && value == id)
                    return root.Clone();
            }
            catch (JsonException) { }
        }
    }

    private static bool HasError(JsonElement response, out string message)
    {
        message = "";
        if (!TryObject(response, "error", out var error)) return false;
        message = String(error, "message") ?? "unknown error";
        return true;
    }

    private static int? ErrorCode(JsonElement response) =>
        TryObject(response, "error", out var error) && error.TryGetProperty("code", out var code) && code.TryGetInt32(out var value)
            ? value
            : null;

    private static bool IsWindow(CodexRateLimitWindow? value, int minutes) =>
        value?.WindowMinutes is { } actual && Math.Abs(actual - minutes) <= 1;

    private CacheDocument? ReadCache(string path)
    {
        try
        {
            var value = JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(path), CodexMeterJson.Options);
            return value is { Schema: 1 } ? value : null;
        }
        catch { return null; }
    }

    private void WriteCache(string path, CacheDocument value)
    {
        try
        {
            _writer.Write(path, JsonSerializer.SerializeToUtf8Bytes(value, CodexMeterJson.Options));
        }
        catch
        {
            // Live quota remains usable when a cache write fails.
        }
    }

    private static bool TryObject(JsonElement value, string name, out JsonElement result)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out result) && result.ValueKind == JsonValueKind.Object)
            return true;
        result = default;
        return false;
    }

    private static string? String(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static double? Number(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)) return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number)) return number;
        return property.ValueKind == JsonValueKind.String && double.TryParse(property.GetString(), out number) ? number : null;
    }

    private static DateTimeOffset? Timestamp(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)) return null;
        if (property.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(property.GetString(), out var parsed)) return parsed;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var seconds))
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch { return null; }
        }
        return null;
    }
}
