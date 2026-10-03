using System.IO;
using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed class CodexMeterPaperPlugin : IPaperBodyPlugin, IPaperPluginRuntimeProvider
{
    public IPaperBodySession Create(PaperBodyContext context) => new CodexMeterPaperSession(context);

    public IPaperPluginRuntime CreatePluginRuntime(PaperPluginRuntimeContext context) =>
        new CodexMeterPluginRuntime(context);
}

internal sealed class CodexMeterPluginRuntime : IPaperPluginRuntime
{
    private readonly PaperPluginRuntimeContext _context;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly CodexAccountScope _accountScope;
    private readonly CodexSessionScanner _scanner;
    private readonly CodexActivityMonitor _activityMonitor;
    private readonly CodexRateLimitReader _rateLimits;
    private readonly CodexExchangeRateReader _exchangeRates;
    private readonly CodexSessionRecycleBin _recycleBin;
    private readonly IDisposable _paperSubscription;
    private readonly IDisposable _settingsSubscription;
    private readonly Task _loop;
    private CodexMeterSnapshot? _snapshot;
    private readonly object _activityLock = new();
    private CodexActivityObservation? _latestActivity;
    private IReadOnlyList<CodexAccountRange> _activityRanges = [];
    private IReadOnlyDictionary<string, CodexSessionSummary> _latestSessions =
        new Dictionary<string, CodexSessionSummary>(StringComparer.OrdinalIgnoreCase);
    private string? _operationMessage;
    private int _refreshSeconds = 60;
    private int _quotaFailures;
    private bool _showCost = true;
    private bool _allAccounts;
    private string _currencyCode = "USD";
    private int _disposed;

    internal CodexMeterPluginRuntime(PaperPluginRuntimeContext context)
    {
        _context = context;
        var storage = AppStoragePaths.Current ?? AppStoragePaths.Initialize();
        var cacheRoot = Path.Combine(storage.CacheDirectory, "CodexMeter");
        _accountScope = new CodexAccountScope(
            () => context.State.Json,
            context.State.Save);
        _scanner = new CodexSessionScanner(cacheRoot);
        _activityMonitor = new CodexActivityMonitor(OnActivityObserved);
        _rateLimits = new CodexRateLimitReader(cacheRoot);
        _exchangeRates = new CodexExchangeRateReader(cacheRoot);
        _recycleBin = new CodexSessionRecycleBin(storage.DataDirectory);
        ApplySettings(context.Settings.Json);
        _paperSubscription = context.Papers.Subscribe(OnPaperEvent);
        _settingsSubscription = context.Settings.Subscribe(ApplySettings);
        _loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        await RefreshAsync(force: false, _cancellation.Token).ConfigureAwait(false);
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(
                    CodexRefreshPolicy.NextDelay(_refreshSeconds, Volatile.Read(ref _quotaFailures)),
                    _cancellation.Token).ConfigureAwait(false);
                await RefreshAsync(force: false, _cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void OnPaperEvent(PaperPluginRuntimeEvent item)
    {
        if (item.Kind == PaperPluginRuntimeEventKind.PaperAdded)
        {
            if (_snapshot != null) Publish(item.PaperId, _snapshot);
            return;
        }
        if (item.Kind != PaperPluginRuntimeEventKind.Message || !item.Message.HasValue) return;
        var message = item.Message.Value;
        var type = message.ValueKind == JsonValueKind.Object &&
                   message.TryGetProperty("type", out var typeValue) &&
                   typeValue.ValueKind == JsonValueKind.String
            ? typeValue.GetString()
            : null;
        if (type == "snapshot.request")
        {
            var force = message.TryGetProperty("force", out var forceValue) && forceValue.ValueKind == JsonValueKind.True;
            if (_snapshot != null && !force) Publish(item.PaperId, _snapshot);
            _ = Task.Run(() => RefreshAsync(force, _cancellation.Token));
        }
        else if (type == "network.request")
        {
            _ = Task.Run(() => RefreshAsync(force: false, _cancellation.Token, checkNetwork: true));
        }
        else if (type is "session.resume" or "session.recycle" or "session.restore")
        {
            var id = message.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String
                ? idValue.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(id))
                _ = Task.Run(() => RunSessionActionAsync(type, id, _cancellation.Token));
        }
    }

    private async Task RefreshAsync(
        bool force,
        CancellationToken cancellationToken,
        bool checkNetwork = false)
    {
        if (!await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            var currentAccount = _accountScope.Current();
            var currentScan = await _scanner.ScanAsync(currentAccount, force, cancellationToken).ConfigureAwait(false);
            var account = _allAccounts ? _accountScope.AllAccounts() : currentAccount;
            var scan = _allAccounts
                ? await _scanner.ScanAsync(account, force, cancellationToken).ConfigureAwait(false)
                : currentScan;
            _activityMonitor.TrackCurrentRollout(scan.Sessions.FirstOrDefault()?.FilePath);
            _latestSessions = scan.Sessions
                .Where(session => !string.IsNullOrWhiteSpace(session.SessionId))
                .GroupBy(session => session.SessionId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var limits = await _rateLimits.ReadAsync(currentAccount, currentScan.LatestRateLimits, cancellationToken).ConfigureAwait(false);
            if (CodexRefreshPolicy.IsLive(limits))
                Interlocked.Exchange(ref _quotaFailures, 0);
            else
                Interlocked.Exchange(ref _quotaFailures, Math.Min(4, Volatile.Read(ref _quotaFailures) + 1));
            var periodStart = limits.SevenDay?.ResetAt is { } reset
                ? reset - TimeSpan.FromMinutes(limits.SevenDay.WindowMinutes ?? CodexRateLimitReader.SevenDayMinutes)
                : DateTimeOffset.UtcNow - TimeSpan.FromDays(7);
            var periodRows = scan.TimedUsage.Where(row => row.Timestamp >= periodStart).ToArray();
            var analytics = CodexAnalytics.Build(scan.TimedUsage, DateTimeOffset.UtcNow);
            var currentPeriodRows = currentScan.TimedUsage.Where(row => row.Timestamp >= periodStart).ToArray();
            var currentPeriodUsage = SumUsage(currentPeriodRows);
            var currentPeriodCost = _showCost ? CodexCostEstimator.Estimate(currentPeriodRows) : null;
            var currency = _showCost && string.Equals(_currencyCode, "CNY", StringComparison.Ordinal)
                ? await _exchangeRates.ReadCnyAsync(cancellationToken).ConfigureAwait(false) ?? new CodexCurrencyRate()
                : new CodexCurrencyRate();
            _accountScope.ObserveQuotaCycle(currentAccount.Key, limits, currentPeriodUsage, currentPeriodCost);
            var network = checkNetwork
                ? await CodexNetworkDiagnostics.CheckAsync(cancellationToken).ConfigureAwait(false)
                : _snapshot?.Network;
            var snapshot = new CodexMeterSnapshot
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                AccountLabel = account.Label,
                AccountKey = account.Key,
                ActivityKind = scan.ActivityKind,
                ActivityText = ActivityText(scan.ActivityKind),
                ActiveSessions = scan.ActiveSessions,
                Usage = scan.Totals,
                CostUsd = _showCost ? CodexCostEstimator.Estimate(periodRows) : null,
                Currency = currency,
                FiveHour = limits.FiveHour,
                SevenDay = limits.SevenDay,
                QuotaSource = limits.Source,
                QuotaObservedAt = limits.ObservedAt,
                QuotaDiagnostics = limits.Diagnostics,
                Message = string.Join(Environment.NewLine,
                    new[] { _operationMessage, limits.Message }.Where(value => !string.IsNullOrWhiteSpace(value))),
                SessionCount = scan.SessionCount,
                Projects = scan.Projects.Take(20).ToArray(),
                Periods = analytics.Periods,
                DailyUsage = analytics.Daily,
                QuotaCycles = _accountScope.GetQuotaCycles(account.Key),
                RecentSessions = scan.Sessions
                    .Where(session => CodexSessionLauncher.IsValidSessionId(session.SessionId))
                    .Take(12)
                    .Select(ToSessionItem)
                    .ToArray(),
                RecycledSessions = _recycleBin.List()
                    .Take(8)
                    .Select(item => new CodexTrashItem
                    {
                        Id = item.Id,
                        SessionId = item.SessionId,
                        DeletedAt = item.DeletedAt
                    })
                    .ToArray(),
                Components = CodexComponentInventory.Scan(),
                Network = network
            };
            _operationMessage = null;
            lock (_activityLock)
            {
                _activityRanges = account.Ranges;
                var newestScanEvent = scan.Sessions
                    .Select(session => session.UpdatedAt ?? new DateTimeOffset(session.LastWriteUtcTicks, TimeSpan.Zero))
                    .DefaultIfEmpty(DateTimeOffset.MinValue)
                    .Max();
                if (_latestActivity is { } observed && observed.Timestamp >= newestScanEvent &&
                    IsFreshActivity(observed.Timestamp) &&
                    InAccountRange(observed.Timestamp, _activityRanges))
                {
                    snapshot = snapshot with
                    {
                        ActivityEventAt = observed.Timestamp,
                        ActivityKind = observed.Kind,
                        ActivityText = ActivityText(observed.Kind)
                    };
                }
                _snapshot = snapshot;
            }
            foreach (var paper in _context.Papers.List())
            {
                Publish(paper.PaperId, snapshot);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            var fallback = (_snapshot ?? new CodexMeterSnapshot
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                ActivityText = Strings.Get("CodexMeterUnavailable")
            }) with
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                Message = ex.GetBaseException().Message
            };
            if (_latestActivity == null || !IsFreshActivity(_latestActivity.Timestamp))
                fallback = fallback with { ActivityEventAt = null, ActivityKind = "unknown", ActivityText = ActivityText("unknown") };
            _snapshot = fallback;
            foreach (var paper in _context.Papers.List()) Publish(paper.PaperId, fallback);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void Publish(string paperId, CodexMeterSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposed) != 0 || !ReferenceEquals(_snapshot, snapshot)) return;
        try
        {
            _context.Papers.SetTitle(paperId, Strings.Get("CodexMeterPaperTitle"));
            _context.Papers.SetHeaderText(paperId,
                $"Codex · {snapshot.ActivityText}");
            _context.Papers.SetCapsulePresentation(paperId, BuildCapsule(snapshot));
            var message = JsonSerializer.SerializeToElement(
                new CodexMeterRuntimeMessage { Snapshot = snapshot },
                CodexMeterJson.Options);
            _context.Papers.PostToBody(paperId, message);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void OnActivityObserved(CodexActivityObservation observation)
    {
        CodexMeterSnapshot? updated;
        lock (_activityLock)
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                _latestActivity != null && observation.Timestamp <= _latestActivity.Timestamp ||
                !IsFreshActivity(observation.Timestamp) ||
                !InAccountRange(observation.Timestamp, _activityRanges)) return;
            _latestActivity = observation;
            if (_snapshot?.ActivityKind == observation.Kind) return;
            // First-turn activity must not wait for the initial quota RPC to complete.
            updated = (_snapshot ?? new CodexMeterSnapshot { GeneratedAt = DateTimeOffset.UtcNow }) with
            {
                ActivityEventAt = observation.Timestamp,
                ActivityKind = observation.Kind,
                ActivityText = ActivityText(observation.Kind)
            };
            _snapshot = updated;
        }
        foreach (var paper in _context.Papers.List()) Publish(paper.PaperId, updated);
    }

    private static bool InAccountRange(DateTimeOffset timestamp, IReadOnlyList<CodexAccountRange> ranges) =>
        ranges.Count == 0 || ranges.Any(range =>
            timestamp >= range.From && (!range.To.HasValue || timestamp < range.To.Value));

    private static bool IsFreshActivity(DateTimeOffset timestamp)
    {
        var age = DateTimeOffset.UtcNow - timestamp;
        return age >= TimeSpan.FromSeconds(-5) && age <= CodexSessionScanner.ActivityStaleAfter;
    }

    internal static PaperCapsulePresentation BuildCapsule(CodexMeterSnapshot snapshot)
    {
        var quota = snapshot.FiveHour;
        var remaining = quota?.RemainingPercent;
        var text = remaining.HasValue
            ? $"5h {remaining.Value:0}%"
            : snapshot.ActivityText;
        var tone = snapshot.ActivityKind switch
        {
            "tool" => PaperCapsuleTone.Accent,
            "thinking" or "answering" or "active" => PaperCapsuleTone.Warning,
            _ => PaperCapsuleTone.Muted
        };
        var components = new List<PaperCapsuleComponent>();
        if (remaining.HasValue)
        {
            components.Add(new PaperCapsuleComponent
            {
                Kind = PaperCapsuleComponentKind.ProgressRing,
                Value = Math.Clamp(remaining.Value / 100d, 0, 1),
                Text = snapshot.ActivityKind,
                Tone = remaining.Value <= 10 ? PaperCapsuleTone.Danger
                    : remaining.Value <= 20 ? PaperCapsuleTone.Warning
                    : PaperCapsuleTone.Accent,
                Width = 24
            });
        }
        else
        {
            components.Add(new PaperCapsuleComponent
            {
                Kind = PaperCapsuleComponentKind.StatusDot,
                Tone = tone,
                Text = snapshot.ActivityKind,
                Width = 18
            });
        }
        components.Add(new PaperCapsuleComponent
        {
            Kind = PaperCapsuleComponentKind.Text,
            Text = text,
            Fill = true
        });
        foreach (var component in components) CodexActivityLatency.Bind(component, snapshot.ActivityEventAt);
        return new PaperCapsulePresentation
        {
            // Keep a small reserve for the full percentage while the docked host
            // retargets its width, without leaving a long empty tail after the text.
            PreferredWidth = remaining.HasValue ? 100 : 120,
            PlainText = text,
            ToolTip = Strings.Format("CodexMeterCapsuleTooltipFormat", snapshot.ActivityText, text),
            Components = components.ToArray()
        };
    }

    private void ApplySettings(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("refreshSeconds", out var refresh) && refresh.TryGetDouble(out var seconds))
                _refreshSeconds = (int)Math.Clamp(Math.Round(seconds), 30, 1800);
            if (root.TryGetProperty("showCost", out var cost) && cost.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _showCost = cost.GetBoolean();
            if (root.TryGetProperty("accountScope", out var accountScope) && accountScope.ValueKind == JsonValueKind.String)
                _allAccounts = string.Equals(accountScope.GetString(), "all", StringComparison.Ordinal);
            if (root.TryGetProperty("currency", out var currency) && currency.ValueKind == JsonValueKind.String)
                _currencyCode = string.Equals(currency.GetString(), "CNY", StringComparison.OrdinalIgnoreCase) ? "CNY" : "USD";
        }
        catch { }
    }

    private async Task RunSessionActionAsync(string type, string id, CancellationToken cancellationToken)
    {
        if (!await _actionGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            if (type == "session.restore")
            {
                var result = await _recycleBin.RestoreAsync(id, cancellationToken).ConfigureAwait(false);
                _operationMessage = result switch
                {
                    CodexSessionRestoreResult.Restored => Strings.Get("CodexMeterSessionRestored"),
                    CodexSessionRestoreResult.Conflict => Strings.Get("CodexMeterSessionRestoreConflict"),
                    _ => Strings.Format("CodexMeterSessionActionFailedFormat", result.ToString())
                };
            }
            else if (_latestSessions.TryGetValue(id, out var session))
            {
                if (type == "session.resume") CodexSessionLauncher.Launch(session);
                else
                {
                    await _recycleBin.MoveToTrashAsync(session, cancellationToken).ConfigureAwait(false);
                    _operationMessage = Strings.Get("CodexMeterSessionRecycled");
                }
            }
            else
            {
                _operationMessage = Strings.Format("CodexMeterSessionActionFailedFormat", "session not found");
            }
        }
        catch (Exception ex)
        {
            _operationMessage = Strings.Format("CodexMeterSessionActionFailedFormat", ex.GetBaseException().Message);
        }
        finally
        {
            _actionGate.Release();
        }
        await RefreshAsync(force: true, cancellationToken).ConfigureAwait(false);
    }

    private static CodexSessionItem ToSessionItem(CodexSessionSummary session)
    {
        var path = session.WorkingDirectory?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? "";
        var name = string.IsNullOrWhiteSpace(path) ? Strings.Get("CodexMeterUnknownProject") : Path.GetFileName(path);
        return new CodexSessionItem
        {
            SessionId = session.SessionId,
            ProjectName = string.IsNullOrWhiteSpace(name) ? path : name,
            Model = session.Model,
            UpdatedAt = session.UpdatedAt,
            TotalTokens = session.Totals.TotalTokens
        };
    }

    private static CodexTokenUsage SumUsage(IEnumerable<CodexTimedUsage> rows)
    {
        var total = new CodexTokenUsage();
        foreach (var row in rows) total += row.Usage;
        return total;
    }

    private static string ActivityText(string kind) => kind switch
    {
        "thinking" => Strings.Get("CodexMeterActivityThinking"),
        "tool" => Strings.Get("CodexMeterActivityTool"),
        "answering" => Strings.Get("CodexMeterActivityAnswering"),
        "active" => Strings.Get("CodexMeterActivityActive"),
        "waiting" => Strings.Get("CodexMeterActivityWaiting"),
        "unknown" or "error" => Strings.Get("CodexMeterActivityUnknown"),
        _ => Strings.Get("CodexMeterActivityIdle")
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _settingsSubscription.Dispose();
        _paperSubscription.Dispose();
        _activityMonitor.Dispose();
        _cancellation.Cancel();
        _context.GlobalTopBar.Clear();
    }
}
