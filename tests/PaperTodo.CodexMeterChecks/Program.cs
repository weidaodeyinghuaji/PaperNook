using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PaperTodo;
using PaperTodo.Plugin;

var checks = 0;
if (args.Contains("--render-latency", StringComparer.Ordinal))
    await RenderLatencyChecks.RunAsync(live: false);
if (args.Contains("--live-render", StringComparer.Ordinal))
    await RenderLatencyChecks.RunAsync(live: true);
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

var activeCapsule = CodexMeterPluginRuntime.BuildCapsule(new CodexMeterSnapshot
{
    ActivityKind = "thinking",
    ActivityText = "Thinking",
    FiveHour = new CodexRateLimitWindow { RemainingPercent = 19 }
});
var inkComponent = activeCapsule.Components.Single(component =>
    component.Kind == PaperCapsuleComponentKind.ProgressRing);
Check(inkComponent.Width == 24 && inkComponent.Text == "thinking" &&
    Math.Abs(inkComponent.Value - 0.19) < 0.0001 &&
    inkComponent.Tone == PaperCapsuleTone.Warning,
    "Available quota must use a larger ring whose inner mark carries activity without altering the quota value.");
Check(activeCapsule.Components.All(component => component.Kind != PaperCapsuleComponentKind.StatusDot),
    "The active quota ring must replace the redundant status dot.");
Check(activeCapsule.PreferredWidth == 100 &&
    activeCapsule.Components.Single(component => component.Kind == PaperCapsuleComponentKind.Text).Text.EndsWith('%'),
    "The capsule must reserve enough width to keep the quota percentage visible beside its larger ring.");
var fullQuotaCapsule = CodexMeterPluginRuntime.BuildCapsule(new CodexMeterSnapshot
{
    FiveHour = new CodexRateLimitWindow { RemainingPercent = 100 }
});
Check(fullQuotaCapsule.PreferredWidth == 100 && fullQuotaCapsule.PlainText == "5h 100%",
    "The compact capsule must retain the complete three-digit quota label.");
var unavailableCapsule = CodexMeterPluginRuntime.BuildCapsule(new CodexMeterSnapshot
{
    ActivityKind = "tool",
    ActivityText = "Using tools"
});
Check(unavailableCapsule.Components.Any(component =>
        component.Kind == PaperCapsuleComponentKind.StatusDot && component.Text == "tool") &&
    unavailableCapsule.Components.All(component => component.Kind != PaperCapsuleComponentKind.ProgressRing) &&
    unavailableCapsule.PreferredWidth == 120,
    "Unavailable quota must not draw a misleading progress ring.");
var activityNow = DateTimeOffset.UtcNow;
var longThinking = new CodexSessionSummary
{
    UpdatedAt = activityNow - TimeSpan.FromMinutes(2),
    LastEventKind = "thinking"
};
Check(CodexSessionScanner.BuildResult([longThinking], activityNow).ActivityKind == "thinking",
    "A long reasoning turn must not become idle after only 30 seconds of silence.");
Check(CodexSessionScanner.BuildResult([longThinking], activityNow + TimeSpan.FromMinutes(4)).ActivityKind == "unknown",
    "Stale activity must be unverified, not falsely reported as confirmed idle.");
Check(CodexSessionScanner.BuildResult([longThinking with { LastEventKind = "idle" }], activityNow).ActivityKind == "idle",
    "A fresh terminal event must stay idle after the slower scan refresh.");
Check(CodexSessionScanner.BuildResult([longThinking with { LastEventKind = "waiting" }], activityNow).ActivityKind == "waiting",
    "Confirmed blocking input must retain its distinct waiting state.");

var activityDirectory = Path.Combine(Path.GetTempPath(), "PaperNook-activity-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(activityDirectory);
try
{
    var activitySignals = Channel.CreateUnbounded<CodexActivityObservation>();
    using var monitor = new CodexActivityMonitor(item => activitySignals.Writer.TryWrite(item), activityDirectory);
    var rollout = Path.Combine(activityDirectory, "rollout-test.jsonl");
    var maxObservationDelay = TimeSpan.Zero;
    foreach (var (recordType, eventType, expected) in new[]
    {
        ("event_msg", "turn_started", "thinking"),
        ("response_item", "custom_tool_call", "tool"),
        ("response_item", "function_call", "waiting"),
        ("event_msg", "exec_command_begin", "tool"),
        ("response_item", "message", "answering"),
        ("event_msg", "turn_complete", "idle")
    })
    {
        var line = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
            type = recordType,
            payload = new { type = eventType, role = eventType == "message" ? "assistant" : null,
                name = eventType == "function_call" ? "functions.request_user_input" : null }
        });
        var stopwatch = Stopwatch.StartNew();
        await File.AppendAllTextAsync(rollout, line + "\n");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var observed = await activitySignals.Reader.ReadAsync(timeout.Token);
        stopwatch.Stop();
        if (stopwatch.Elapsed > maxObservationDelay) maxObservationDelay = stopwatch.Elapsed;
        Check(observed.Kind == expected,
            $"The activity watcher must distinguish {expected} from other Codex states.");
    }
    Check(maxObservationDelay < TimeSpan.FromSeconds(1.5),
        $"Codex rollout activity should be observed within 1.5 s; measured {maxObservationDelay.TotalMilliseconds:0} ms.");
    await File.AppendAllTextAsync(rollout, JsonSerializer.Serialize(new
    {
        timestamp = DateTimeOffset.UtcNow.ToString("O"),
        type = "response_item",
        payload = new { type = "message", role = "user" }
    }) + "\n");
    var afterUserMessage = await CodexSessionScanner.ReadLatestActivityAsync(rollout, CancellationToken.None);
    Check(afterUserMessage?.Kind == "idle",
        "A user or developer message must not be misreported as Codex answering.");
    var fallbackSignals = Channel.CreateUnbounded<CodexActivityObservation>();
    using (var fallbackMonitor = new CodexActivityMonitor(
        item => fallbackSignals.Writer.TryWrite(item), activityDirectory, enableWatcher: false))
    {
        fallbackMonitor.TrackCurrentRollout(rollout);
        var fallbackClock = Stopwatch.StartNew();
        await File.AppendAllTextAsync(rollout, JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
            type = "event_msg",
            payload = new { type = "turn_started" }
        }) + "\n");
        using var fallbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var fallbackObservation = await fallbackSignals.Reader.ReadAsync(fallbackTimeout.Token);
        fallbackClock.Stop();
        Check(fallbackObservation.Kind == "thinking" && fallbackClock.Elapsed < TimeSpan.FromSeconds(1.5),
            "The tracked-rollout poll must detect thinking within 1.5 s if a file notification is missed.");
        Console.WriteLine($"Codex activity fallback poll: {fallbackClock.Elapsed.TotalMilliseconds:0} ms.");
    }
    Console.WriteLine($"Codex activity watcher: worst observed delay {maxObservationDelay.TotalMilliseconds:0} ms.");
}
finally
{
    Directory.Delete(activityDirectory, recursive: true);
}

var normalized = CodexSessionScanner.NormalizeUsage(new CodexTokenUsage
{
    InputTokens = 100,
    CachedInputTokens = 20,
    OutputTokens = 30,
    ReasoningOutputTokens = 10,
    TotalTokens = 160
});
Check(normalized.InputTokens == 80, "Cached input must be removed from non-cached input.");
Check(normalized.TotalTokens == 130, "Normalized total must not double-count cached input or reasoning tokens.");

var delta = CodexSessionScanner.PickDelta(
    null,
    new CodexTokenUsage { InputTokens = 170, CachedInputTokens = 30, OutputTokens = 50, TotalTokens = 270 },
    new CodexTokenUsage { InputTokens = 100, CachedInputTokens = 20, OutputTokens = 30, TotalTokens = 160 });
Check(delta?.TotalTokens == 90, "Cumulative token counters must be converted to one-turn deltas.");

using (var rateDocument = JsonDocument.Parse("""
{
  "rateLimitsByLimitId": {
    "codex": {
      "planType": "plus",
      "primary": { "usedPercent": 70, "windowDurationMins": 10080, "resetsAt": 1780000000 },
      "secondary": { "usedPercent": 25, "windowDurationMins": 300, "resetsAt": 1780000100 }
    }
  }
}
"""))
{
    var mapped = CodexRateLimitReader.MapRateLimits(rateDocument.RootElement);
    Check(mapped.FiveHour?.UsedPercent == 25, "Five-hour quota must be classified by duration, not primary order.");
    Check(mapped.SevenDay?.UsedPercent == 70, "Seven-day quota must be classified by duration, not secondary order.");
    Check(mapped.PlanType == "plus", "Plan type must be preserved from the selected Codex bucket.");
}

Check(!CodexRateLimitReader.IsUsable(
        new CodexRateLimitWindow { WindowMinutes = 300, ResetAt = DateTimeOffset.UtcNow.AddHours(1) },
        DateTimeOffset.UtcNow.AddHours(-1),
        DateTimeOffset.UtcNow,
        TimeSpan.FromMinutes(30)),
    "Stale quota cache must not be presented as fresh.");
Check(CodexRefreshPolicy.NextDelay(300, 1) == TimeSpan.FromSeconds(30) &&
      CodexRefreshPolicy.NextDelay(300, 3) == TimeSpan.FromSeconds(120) &&
      CodexRefreshPolicy.NextDelay(60, 4) == TimeSpan.FromSeconds(60),
    "Quota retry backoff must be bounded by the configured refresh interval.");
Check(CodexRefreshPolicy.IsLive(new CodexRateLimits { Source = "codex-rpc+cache" }) &&
      !CodexRefreshPolicy.IsLive(new CodexRateLimits { Source = "session" }),
    "Only a response containing codex-rpc may reset live-read backoff.");
var appServerStart = CodexRateLimitReader.CreateStartInfo("codex.exe");
Check(appServerStart.StandardInputEncoding?.GetPreamble().Length == 0,
    "Codex app-server stdin must use UTF-8 without a BOM.");
var sourceState = CodexQuotaSourceAnalyzer.Analyze(
    "session",
    DateTimeOffset.Parse("2026-09-21T10:00:00Z"),
    DateTimeOffset.Parse("2026-09-21T10:07:59Z"));
Check(sourceState.Kind == "session" && sourceState.AgeMinutes == 7,
    "Quota source presentation must preserve fallback kind and age.");
var mixedSourceState = CodexQuotaSourceAnalyzer.Analyze(
    "codex-rpc+cache",
    DateTimeOffset.Parse("2026-09-21T10:00:00Z"),
    DateTimeOffset.Parse("2026-09-21T10:07:59Z"));
Check(mixedSourceState.Kind == "mixed" && mixedSourceState.AgeMinutes == 7,
    "Mixed quota sources must expose the oldest observation age.");

var temp = Path.Combine(Path.GetTempPath(), $"papernook-codex-meter-{Guid.NewGuid():N}");
Directory.CreateDirectory(temp);
try
{
    var rollout = Path.Combine(temp, "rollout-test.jsonl");
    var events = new object[]
    {
        new { timestamp = "2026-09-01T10:00:00.000Z", type = "session_meta", payload = new { id = "session-1", cwd = temp, model_provider = "openai" } },
        new { timestamp = "2026-09-01T10:00:01.000Z", type = "turn_context", payload = new { cwd = temp, model = "gpt-5.5" } },
        TokenCount("2026-09-01T10:00:02.000Z", 100, 20, 30, 10, 160),
        new { timestamp = "2026-09-02T10:00:01.000Z", type = "turn_context", payload = new { cwd = temp, model = "gpt-5.6-terra" } },
        TokenCount("2026-09-02T10:00:02.000Z", 170, 30, 50, 20, 270)
    };
    await File.WriteAllLinesAsync(rollout, events.Select(item => JsonSerializer.Serialize(item)));
    var parsed = await CodexSessionScanner.ParseAsync(
        rollout,
        new FileInfo(rollout),
        [new CodexAccountRange { From = DateTimeOffset.Parse("2026-09-01T00:00:00Z") }],
        CancellationToken.None);
    Check(parsed.SessionId == "session-1", "Rollout session metadata must be preserved.");
    Check(parsed.Totals.TotalTokens == 220, "Rollout token deltas must match the Node reference behavior.");
    Check(parsed.TimedUsage.Count == 2, "Timed rows must keep the per-model quota-cycle cost basis.");
    Check(parsed.TimedUsage[1].Model == "gpt-5.6-terra", "Model switches must apply to following token rows.");

    var scanner = new CodexSessionScanner(Path.Combine(temp, "cache"), temp);
    var profile = new CodexAccountProfile
    {
        Key = "test-account",
        Label = "Test",
        Ranges = [new CodexAccountRange { From = DateTimeOffset.Parse("2026-09-01T00:00:00Z") }]
    };
    var firstScan = await scanner.ScanAsync(profile, force: false, CancellationToken.None);
    await File.AppendAllTextAsync(rollout, JsonSerializer.Serialize(
        TokenCount("2026-09-03T10:00:02.000Z", 200, 40, 70, 25, 335)) + Environment.NewLine);
    var incrementalScan = await scanner.ScanAsync(profile, force: false, CancellationToken.None);
    Check(firstScan.Totals.TotalTokens == 220, "Initial native scan must match direct parsing.");
    Check(incrementalScan.Totals.TotalTokens == 270, "Growing rollout files must scan only appended token deltas.");
    Check(incrementalScan.Sessions.Count == 1 && incrementalScan.Sessions[0].SessionId == "session-1",
        "Scan results must expose sanitized session metadata for the native session page.");

    var stateJson = "{}";
    var auth = Path.Combine(temp, "auth.json");
    var jwtPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"email\":\"demo@example.com\"}"))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    await File.WriteAllTextAsync(auth, JsonSerializer.Serialize(new
    {
        tokens = new { account_id = "secret-account-id", id_token = $"x.{jwtPayload}.x" }
    }));
    var scope = new CodexAccountScope(() => stateJson, value => stateJson = value, auth);
    var account = scope.Current(DateTimeOffset.Parse("2026-08-20T12:00:00Z"));
    Check(account.Key != "secret-account-id" && account.Key.Length == 24, "Account ids must be hashed before persistence.");
    Check(account.Label == "de***@example.com", "Only a masked email may reach presentation state.");
    Check(!stateJson.Contains("secret-account-id", StringComparison.Ordinal), "Persistent account state must not contain the raw id.");

    await File.WriteAllTextAsync(auth, JsonSerializer.Serialize(new
    {
        tokens = new { account_id = "second-secret-account", id_token = $"x.{jwtPayload}.x" }
    }));
    var secondAccount = scope.Current(DateTimeOffset.Parse("2026-08-21T12:00:00Z"));
    var allAccounts = scope.AllAccounts(DateTimeOffset.Parse("2026-08-21T12:01:00Z"));
    Check(secondAccount.Key != account.Key && allAccounts.Key == "all" && allAccounts.Ranges.Count == 2,
        "All-account scope must merge persisted account ranges without exposing raw account ids.");

    var cycleLimits = new CodexRateLimits
    {
        SevenDay = new CodexRateLimitWindow
        {
            RemainingPercent = 63,
            WindowMinutes = CodexRateLimitReader.SevenDayMinutes,
            ResetAt = DateTimeOffset.Parse("2026-09-28T12:00:00Z")
        },
        ObservedAt = DateTimeOffset.Parse("2026-09-22T09:00:00Z"),
        Source = "codex-rpc"
    };
    scope.ObserveQuotaCycle(secondAccount.Key, cycleLimits,
        new CodexTokenUsage { TotalTokens = 1234 }, 0.25);
    var cycles = scope.GetQuotaCycles(secondAccount.Key);
    Check(cycles.Count == 1 && cycles[0].RemainingPercent == 63 && cycles[0].Usage.TotalTokens == 1234,
        "Trusted seven-day observations must persist one account-scoped quota-cycle summary.");

    var estimated = CodexCostEstimator.Estimate(new CodexTokenUsage
    {
        InputTokens = 1_000_000,
        CachedInputTokens = 1_000_000,
        CacheCreationInputTokens = 1_000_000,
        OutputTokens = 1_000_000,
        TotalTokens = 4_000_000
    }, "gpt-5.6-terra");
    Check(estimated == 16.7, "Cost estimator must apply the model-specific public price table and cache-write multiplier.");
    var parsedRate = CodexExchangeRateReader.Parse("""
        <gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01" xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref">
          <Cube><Cube time="2026-09-21"><Cube currency="USD" rate="1.1490"/><Cube currency="CNY" rate="8.1000"/></Cube></Cube>
        </gesmes:Envelope>
        """);
    Check(parsedRate.CurrencyCode == "CNY" && Math.Abs(parsedRate.UsdToCurrency - (8.1 / 1.149)) < 0.000001 &&
          parsedRate.RateDate == new DateOnly(2026, 9, 21),
        "ECB rates must be converted from EUR quotations into a USD-to-CNY display rate.");
    Check(CodexSessionLauncher.IsValidSessionId("11111111-1111-1111-1111-111111111111") &&
          !CodexSessionLauncher.IsValidSessionId("--dangerous"),
        "Session resume must accept only canonical GUID identifiers.");

    var analytics = CodexAnalytics.Build(parsed.TimedUsage, DateTimeOffset.Parse("2026-09-03T12:00:00Z"));
    Check(analytics.Periods.Single(period => period.Key == "7d").Usage.TotalTokens == 220,
        "Seven-day analytics must aggregate retained timed usage.");
    Check(analytics.Daily.Count == 30 && analytics.Daily[^2].Usage.TotalTokens == 90,
        "Daily analytics must emit a stable 30-day series including empty days.");
    var exportSnapshot = new CodexMeterSnapshot
    {
        GeneratedAt = DateTimeOffset.Parse("2026-09-03T12:00:00Z"),
        AccountLabel = "de***@example.com",
        AccountKey = "private-key",
        DailyUsage = analytics.Daily,
        Periods = analytics.Periods,
        Projects = [new CodexProjectUsage { Name = "demo", Path = "C:\\secret", TotalTokens = 220, SessionCount = 1 }]
    };
    var jsonExport = CodexMeterExport.Json(exportSnapshot);
    Check(!jsonExport.Contains("private-key", StringComparison.Ordinal) &&
          !jsonExport.Contains("C:\\secret", StringComparison.Ordinal),
        "Exports must omit account hashes and local project paths.");
    Check(CodexMeterExport.Csv(exportSnapshot).StartsWith("date,total_tokens", StringComparison.Ordinal),
        "CSV export must use the documented daily schema.");
    var diagnosticText = CodexDiagnosticSummary.Build(exportSnapshot);
    Check(!diagnosticText.Contains("private-key", StringComparison.Ordinal) &&
          !diagnosticText.Contains("C:\\secret", StringComparison.Ordinal) &&
          diagnosticText.Contains(CodexCostEstimator.PriceCatalogDate, StringComparison.Ordinal),
        "Copyable diagnostics must stay sanitized and identify the price catalog date.");

    var sessionsRoot = Path.Combine(temp, "sessions-root");
    var sessionPath = Path.Combine(sessionsRoot, "2026", "09", "rollout-session-1.jsonl");
    Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
    await File.WriteAllTextAsync(sessionPath, "{\"type\":\"test\"}\n");
    var recycle = new CodexSessionRecycleBin(Path.Combine(temp, "durable"), sessionsRoot);
    var recycled = await recycle.MoveToTrashAsync(new CodexSessionSummary
    {
        FilePath = sessionPath,
        SessionId = "11111111-1111-1111-1111-111111111111",
        WorkingDirectory = temp,
        Size = new FileInfo(sessionPath).Length
    }, CancellationToken.None);
    Check(!File.Exists(sessionPath) && File.Exists(recycled.ManifestPath),
        "Soft delete must move the rollout into the durable PaperNook recycle area.");
    var restored = await recycle.RestoreAsync(recycled.Id, CancellationToken.None);
    Check(restored == CodexSessionRestoreResult.Restored && File.Exists(sessionPath),
        "Recycle restore must return the verified rollout to its original location.");
    var recycledAgain = await recycle.MoveToTrashAsync(new CodexSessionSummary
    {
        FilePath = sessionPath,
        SessionId = "11111111-1111-1111-1111-111111111111",
        WorkingDirectory = temp,
        Size = new FileInfo(sessionPath).Length
    }, CancellationToken.None);
    Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
    await File.WriteAllTextAsync(sessionPath, "conflict");
    Check(await recycle.RestoreAsync(recycledAgain.Id, CancellationToken.None) == CodexSessionRestoreResult.Conflict,
        "Recycle restore must never overwrite a newly-created session file.");

    var inventoryRoot = Path.Combine(temp, "codex-home");
    Directory.CreateDirectory(Path.Combine(inventoryRoot, "skills", "demo-skill"));
    Directory.CreateDirectory(Path.Combine(inventoryRoot, "plugins", "demo-plugin"));
    await File.WriteAllTextAsync(Path.Combine(inventoryRoot, "skills", "demo-skill", "SKILL.md"), "demo");
    await File.WriteAllTextAsync(Path.Combine(inventoryRoot, "plugins", "demo-plugin", "plugin.json"), "{}");
    await File.WriteAllTextAsync(Path.Combine(inventoryRoot, "config.toml"), "[mcp_servers.demo-mcp]\ncommand = 'demo'");
    var inventory = CodexComponentInventory.Scan(inventoryRoot);
    Check(inventory.SkillCount == 1 && inventory.PluginCount == 1 && inventory.McpServerCount == 1,
        "Component inventory must discover local skills, plugins and MCP server declarations.");
}
finally
{
    Directory.Delete(temp, recursive: true);
}

var requireLiveQuota = args.Contains("--live-required", StringComparer.Ordinal);
if (args.Contains("--live-activity", StringComparer.Ordinal))
{
    var started = DateTimeOffset.UtcNow;
    var observations = new System.Collections.Concurrent.ConcurrentQueue<(string Kind, double Delay)>();
    using var monitor = new CodexActivityMonitor(observation =>
    {
        if (observation.Timestamp < started) return;
        var delay = (DateTimeOffset.UtcNow - observation.Timestamp).TotalMilliseconds;
        if (delay >= 0) observations.Enqueue((observation.Kind, delay));
    });
    await Task.Delay(TimeSpan.FromSeconds(45));
    var values = observations.ToArray();
    Console.WriteLine(values.Length == 0
        ? "INCONCLUSIVE live activity: no newly-timestamped local events in the 45-second observation window."
        : $"Live activity: {values.Length} observations; event-timestamp to callback min={values.Min(v => v.Delay):F0}ms max={values.Max(v => v.Delay):F0}ms; kinds={string.Join(",", values.Select(v => v.Kind).Distinct())}. Not internal-thought-start or display latency.");
}
if (args.Contains("--live", StringComparer.Ordinal) || requireLiveQuota)
{
    var liveCache = Path.Combine(Path.GetTempPath(), $"papernook-codex-live-{Guid.NewGuid():N}");
    try
    {
        var liveState = "{}";
        var liveAccount = new CodexAccountScope(() => liveState, value => liveState = value).Current();
        var liveScan = await new CodexSessionScanner(liveCache).ScanAsync(liveAccount, force: false, CancellationToken.None);
        var liveLimits = await new CodexRateLimitReader(liveCache).ReadAsync(liveAccount, liveScan.LatestRateLimits, CancellationToken.None);
        Check(liveScan.Totals.TotalTokens >= 0 && liveScan.SessionCount >= 0, "Live rollout scan must produce a valid non-negative snapshot.");
        Check(liveLimits.Source.Length > 0, "Live quota read must report its data source even when unavailable.");
        if (requireLiveQuota)
        {
            Check(CodexRefreshPolicy.IsLive(liveLimits) && liveLimits.FiveHour != null && liveLimits.SevenDay != null,
                $"Live quota probe must return both Codex windows. Source={liveLimits.Source}; Message={liveLimits.Message}");
        }
    }
    finally
    {
        if (Directory.Exists(liveCache)) Directory.Delete(liveCache, recursive: true);
    }
}

if (args.Contains("--network", StringComparer.Ordinal))
{
    var network = await CodexNetworkDiagnostics.CheckAsync(CancellationToken.None);
    Check(network.Probes.Count == 2 && network.Probes.All(probe => probe.Name.Length > 0),
        "Native network diagnostics must return both official service probes.");
}

Console.WriteLine($"Codex Meter: {checks} checks passed.");

static object TokenCount(string timestamp, long input, long cached, long output, long reasoning, long total) => new
{
    timestamp,
    type = "event_msg",
    payload = new
    {
        type = "token_count",
        info = new
        {
            total_token_usage = new
            {
                input_tokens = input,
                cached_input_tokens = cached,
                cache_creation_input_tokens = 0,
                output_tokens = output,
                reasoning_output_tokens = reasoning,
                total_tokens = total
            }
        }
    }
};
