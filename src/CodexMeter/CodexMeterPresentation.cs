using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed class CodexMeterPaperSession : IPaperBodySession, IPaperMiniViewProvider
{
    private readonly PaperBodyContext _context;
    private readonly CodexMeterDashboardView _dashboard;
    private CodexMeterDashboardView? _mini;
    private CodexMeterSnapshot? _snapshot;
    private bool _disposed;

    internal CodexMeterPaperSession(PaperBodyContext context)
    {
        _context = context;
        _dashboard = new CodexMeterDashboardView(
            context.Theme,
            RequestRefresh,
            RequestNetworkCheck,
            compact: false,
            resumeSession: id => PostSessionAction("session.resume", id),
            recycleSession: id => PostSessionAction("session.recycle", id),
            restoreSession: id => PostSessionAction("session.restore", id));
        View = _dashboard;
        context.TopBar.SetActionHandler(OnTopBarAction);
        context.TopBar.SetPaperActions(
        [
            new PaperTopBarAction
            {
                Id = "refresh",
                Icon = PaperTopBarIcon.SvgPath(
                    "M 17,6 A 7,7 0 1 0 18.5,13 M 17,6 L 17,2 M 17,6 L 13,6",
                    PaperTopBarSvgRenderMode.Stroke,
                    1.6),
                ToolTip = Strings.Get("CodexMeterRefresh")
            },
            new PaperTopBarAction
            {
                Id = "export",
                Icon = PaperTopBarIcon.SvgPath(
                    "M 12,3 L 12,15 M 7,10 L 12,15 L 17,10 M 5,19 L 19,19",
                    PaperTopBarSvgRenderMode.Stroke,
                    1.6),
                ToolTip = Strings.Get("CodexMeterExport")
            }
        ]);
        context.Paper.SetTitle(Strings.Get("CodexMeterPaperTitle"));
        context.Paper.SetHeaderText("Codex");
        RequestSnapshot(force: false);
    }

    public FrameworkElement View { get; }

    public PaperMiniViewSize PreferredMiniViewSize => new(360, 240);

    public FrameworkElement? CreateMiniView(PaperMiniViewContext context)
    {
        _mini = new CodexMeterDashboardView(context.Theme, RequestRefresh, RequestNetworkCheck, compact: true);
        if (_snapshot != null) _mini.Update(_snapshot);
        return _mini;
    }

    public bool OnRuntimeMessage(JsonElement message)
    {
        try
        {
            var envelope = message.Deserialize<CodexMeterRuntimeMessage>(CodexMeterJson.Options);
            if (envelope?.Snapshot == null) return false;
            _snapshot = envelope.Snapshot;
            _dashboard.Update(_snapshot);
            _mini?.Update(_snapshot);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void OnVisibilityChanged(bool visible)
    {
        if (visible) RequestSnapshot(force: false);
    }

    public void OnActivated() => RequestSnapshot(force: false);

    public void OnThemeChanged(PaperBodyTheme theme)
    {
        _dashboard.ApplyTheme(theme);
        _mini?.ApplyTheme(theme);
    }

    public void OnTypographyChanged(PaperBodyTheme theme) => OnThemeChanged(theme);

    public void OnSettingsChanged(string settingsJson) => RequestSnapshot(force: true);

    private void OnTopBarAction(PaperTopBarActionInvocation invocation)
    {
        if (invocation.ActionId == "refresh") RequestRefresh();
        if (invocation.ActionId == "export") ExportSnapshot();
    }

    private void ExportSnapshot()
    {
        if (_snapshot == null) return;
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = Strings.Get("CodexMeterExport"),
                FileName = $"papernook-codex-{DateTime.Now:yyyyMMdd-HHmm}",
                Filter = $"{Strings.Get("CodexMeterExportJson")} (*.json)|*.json|{Strings.Get("CodexMeterExportCsv")} (*.csv)|*.csv",
                DefaultExt = ".json",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog() != true) return;
            var csv = string.Equals(Path.GetExtension(dialog.FileName), ".csv", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(
                dialog.FileName,
                csv ? CodexMeterExport.Csv(_snapshot) : CodexMeterExport.Json(_snapshot),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: csv));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Strings.Format("CodexMeterExportFailedFormat", ex.GetBaseException().Message),
                Strings.Get("CodexMeterExport"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void RequestRefresh()
    {
        _dashboard.SetRefreshing();
        _mini?.SetRefreshing();
        RequestSnapshot(force: true);
    }

    private void RequestNetworkCheck()
    {
        if (_disposed || !_context.Runtime.IsAvailable) return;
        _dashboard.SetNetworkChecking();
        _context.Runtime.Post(JsonSerializer.SerializeToElement(
            new { type = "network.request" }, CodexMeterJson.Options));
    }

    private void RequestSnapshot(bool force)
    {
        if (_disposed || !_context.Runtime.IsAvailable) return;
        _context.Runtime.Post(JsonSerializer.SerializeToElement(
            new { type = "snapshot.request", force },
            CodexMeterJson.Options));
    }

    private void PostSessionAction(string type, string id)
    {
        if (_disposed || !_context.Runtime.IsAvailable) return;
        _context.Runtime.Post(JsonSerializer.SerializeToElement(
            new { type, id }, CodexMeterJson.Options));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context.TopBar.Clear();
        _mini = null;
    }
}

internal sealed class CodexMeterDashboardView : Grid
{
    private readonly bool _compact;
    private readonly Action _refresh;
    private readonly Action _checkNetwork;
    private readonly Action<string>? _resumeSession;
    private readonly Action<string>? _recycleSession;
    private readonly Action<string>? _restoreSession;
    private readonly TextBlock _account;
    private readonly Border _statusPill;
    private readonly TextBlock _status;
    private readonly TextBlock _fiveValue;
    private readonly TextBlock _fiveReset;
    private readonly ProgressBar _fiveProgress;
    private readonly CodexInkRing _fiveRing;
    private readonly TextBlock _sevenValue;
    private readonly TextBlock _sevenReset;
    private readonly ProgressBar _sevenProgress;
    private readonly TextBlock _tokens;
    private readonly TextBlock _cost;
    private readonly TextBlock _sessions;
    private readonly StackPanel _projects;
    private readonly Grid _periods;
    private readonly StackPanel _trend;
    private readonly TextBlock _tokenBreakdown;
    private readonly StackPanel _cycles;
    private readonly StackPanel _sessionsList;
    private readonly StackPanel _recycledList;
    private readonly List<ProgressBar> _dynamicProgress = [];
    private readonly List<Button> _sectionButtons = [];
    private readonly List<Button> _dynamicButtons = [];
    private readonly TextBlock _components;
    private readonly StackPanel _network;
    private readonly Button _networkButton;
    private readonly Button _copyDiagnosticsButton;
    private readonly TextBlock _footer;
    private readonly Button _refreshButton;
    private FrameworkElement? _quotaSection;
    private FrameworkElement? _metricsSection;
    private FrameworkElement? _analysisSection;
    private FrameworkElement? _projectsSection;
    private FrameworkElement? _diagnosticsSection;
    private string _activeSection = "overview";
    private PaperBodyTheme _theme;
    private CodexMeterSnapshot? _latestSnapshot;
    private string _motionKind = "";
    private bool _motionEnabled;

    internal CodexMeterDashboardView(
        PaperBodyTheme theme,
        Action refresh,
        Action checkNetwork,
        bool compact,
        Action<string>? resumeSession = null,
        Action<string>? recycleSession = null,
        Action<string>? restoreSession = null)
    {
        _theme = theme;
        _refresh = refresh;
        _checkNetwork = checkNetwork;
        _resumeSession = resumeSession;
        _recycleSession = recycleSession;
        _restoreSession = restoreSession;
        _compact = compact;
        ClipToBounds = true;
        Background = Brushes.Transparent;

        var root = new Grid { Margin = compact ? new Thickness(12, 10, 12, 10) : new Thickness(16, 12, 16, 14) };
        for (var index = 0; index < (compact ? 5 : 8); index++)
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (compact) root.RowDefinitions[3].Height = new GridLength(1, GridUnitType.Star);
        Children.Add(compact ? root : new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = root
        });

        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var identity = new StackPanel();
        identity.Children.Add(new TextBlock
        {
            Text = Strings.Get("CodexMeterHeading"),
            FontWeight = FontWeights.SemiBold,
            FontSize = compact ? 15 : 17
        });
        _account = new TextBlock { FontSize = 10.5, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        identity.Children.Add(_account);
        heading.Children.Add(identity);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _status = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _statusPill = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(6, 0, 0, 0),
            Child = _status
        };
        IsVisibleChanged += (_, _) => UpdateStatusMotion();
        Loaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged += OnSystemMotionChanged;
            UpdateStatusMotion();
        };
        Unloaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= OnSystemMotionChanged;
            _statusPill.BeginAnimation(UIElement.OpacityProperty, null);
            _motionEnabled = false;
        };
        actions.Children.Add(_statusPill);
        _refreshButton = new Button
        {
            Content = "↻",
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            FontSize = 15
        };
        ToolTipService.SetToolTip(_refreshButton, Strings.Get("CodexMeterRefresh"));
        AutomationProperties.SetName(_refreshButton, Strings.Get("CodexMeterRefresh"));
        _refreshButton.Click += (_, _) => _refresh();
        actions.Children.Add(_refreshButton);
        Grid.SetColumn(actions, 1);
        heading.Children.Add(actions);
        root.Children.Add(heading);

        if (!compact)
        {
            var tabs = new UniformGrid
            {
                Columns = 4,
                Margin = new Thickness(0, 12, 0, 2)
            };
            tabs.Children.Add(CreateSectionButton("overview", Strings.Get("CodexMeterTabOverview")));
            tabs.Children.Add(CreateSectionButton("analysis", Strings.Get("CodexMeterTabAnalysis")));
            tabs.Children.Add(CreateSectionButton("projects", Strings.Get("CodexMeterTabProjects")));
            tabs.Children.Add(CreateSectionButton("diagnostics", Strings.Get("CodexMeterTabDiagnostics")));
            Grid.SetRow(tabs, 3);
            root.Children.Add(tabs);
        }

        var quota = new Grid { Margin = new Thickness(0, compact ? 10 : 14, 0, 0) };
        quota.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        quota.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(compact ? 8 : 10) });
        quota.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        (_fiveValue, _fiveReset, _fiveProgress, _fiveRing) = AddQuotaCard(quota, 0, Strings.Get("CodexMeterFiveHour"), true);
        (_sevenValue, _sevenReset, _sevenProgress, _) = AddQuotaCard(quota, 2, Strings.Get("CodexMeterSevenDay"), false);
        Grid.SetRow(quota, 1);
        root.Children.Add(quota);
        _quotaSection = quota;

        var metrics = new Grid { Margin = new Thickness(0, compact ? 9 : 12, 0, 0) };
        for (var index = 0; index < 3; index++) metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _tokens = AddMetric(metrics, 0, Strings.Get("CodexMeterTokens"));
        _cost = AddMetric(metrics, 1, Strings.Get("CodexMeterCost"));
        _sessions = AddMetric(metrics, 2, Strings.Get("CodexMeterSessions"));
        Grid.SetRow(metrics, 2);
        root.Children.Add(metrics);
        _metricsSection = metrics;

        _periods = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        _trend = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        _tokenBreakdown = new TextBlock { FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(1, 10, 0, 0) };
        _cycles = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (!compact)
        {
            var analysis = new StackPanel();
            analysis.Children.Add(new TextBlock
            {
                Text = Strings.Get("CodexMeterAnalysis"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(1, 0, 0, 0)
            });
            analysis.Children.Add(_periods);
            analysis.Children.Add(new TextBlock
            {
                Text = Strings.Get("CodexMeterDailyTrend"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(1, 12, 0, 0)
            });
            analysis.Children.Add(_trend);
            analysis.Children.Add(new TextBlock
            {
                Text = Strings.Get("CodexMeterTokenBreakdown"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(1, 12, 0, 0)
            });
            analysis.Children.Add(_tokenBreakdown);
            analysis.Children.Add(new TextBlock
            {
                Text = Strings.Get("CodexMeterCycleHistory"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(1, 12, 0, 0)
            });
            analysis.Children.Add(_cycles);
            Grid.SetRow(analysis, 4);
            root.Children.Add(analysis);
            _analysisSection = analysis;
        }

        _projects = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (!compact)
        {
            _projects.Children.Add(new TextBlock
            {
                Text = Strings.Get("CodexMeterTopProjects"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(1, 0, 0, 6)
            });
        }
        _sessionsList = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
        _recycledList = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
        if (compact)
        {
            Grid.SetRow(_projects, 3);
            root.Children.Add(_projects);
        }
        else
        {
            var projectsSection = new StackPanel();
            projectsSection.Children.Add(_projects);
            projectsSection.Children.Add(new TextBlock
            {
                Text = Strings.Get("CodexMeterRecentSessions"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(1, 12, 0, 0)
            });
            projectsSection.Children.Add(_sessionsList);
            projectsSection.Children.Add(new TextBlock
            {
                Text = Strings.Get("CodexMeterRecycledSessions"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(1, 12, 0, 0)
            });
            projectsSection.Children.Add(_recycledList);
            Grid.SetRow(projectsSection, 5);
            root.Children.Add(projectsSection);
            _projectsSection = projectsSection;
        }

        _components = new TextBlock { FontSize = 10.5, TextWrapping = TextWrapping.Wrap };
        _network = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
        _networkButton = new Button
        {
            Content = Strings.Get("CodexMeterCheckNetwork"),
            Padding = new Thickness(9, 4, 9, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0),
            Cursor = Cursors.Hand
        };
        AutomationProperties.SetName(_networkButton, Strings.Get("CodexMeterCheckNetwork"));
        _networkButton.Click += (_, _) => _checkNetwork();
        _copyDiagnosticsButton = new Button
        {
            Content = Strings.Get("CodexMeterCopyDiagnostics"),
            Padding = new Thickness(9, 4, 9, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(7, 6, 0, 0),
            Cursor = Cursors.Hand
        };
        AutomationProperties.SetName(_copyDiagnosticsButton, Strings.Get("CodexMeterCopyDiagnostics"));
        _copyDiagnosticsButton.Click += (_, _) => CopyDiagnostics();
        if (!compact)
        {
            var diagnostics = new StackPanel { Margin = new Thickness(0, 13, 0, 0) };
            diagnostics.Children.Add(new TextBlock
            {
                Text = Strings.Get("CodexMeterComponents"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            });
            diagnostics.Children.Add(_components);
            diagnostics.Children.Add(new TextBlock
            {
                Text = Strings.Get("CodexMeterNetwork"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 0)
            });
            diagnostics.Children.Add(_network);
            var diagnosticActions = new StackPanel { Orientation = Orientation.Horizontal };
            diagnosticActions.Children.Add(_networkButton);
            diagnosticActions.Children.Add(_copyDiagnosticsButton);
            diagnostics.Children.Add(diagnosticActions);
            Grid.SetRow(diagnostics, 6);
            root.Children.Add(diagnostics);
            _diagnosticsSection = diagnostics;
        }

        _footer = new TextBlock
        {
            FontSize = 9.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(1, compact ? 5 : 11, 0, 0)
        };
        Grid.SetRow(_footer, compact ? 4 : 7);
        root.Children.Add(_footer);
        if (!compact) SetSection("overview");
        ApplyTheme(theme);
        ShowLoading();
    }

    internal void Update(CodexMeterSnapshot snapshot)
    {
        _refreshButton.IsEnabled = true;
        _account.Text = snapshot.AccountLabel;
        _status.Text = snapshot.ActivityKind != "idle" && snapshot.ActiveSessions > 1
            ? Strings.Format("CodexMeterActivityCountFormat", snapshot.ActivityText, snapshot.ActiveSessions)
            : snapshot.ActivityText;
        UpdateQuota(_fiveValue, _fiveReset, _fiveProgress, snapshot.FiveHour, _latestSnapshot?.FiveHour);
        UpdateQuota(_sevenValue, _sevenReset, _sevenProgress, snapshot.SevenDay, _latestSnapshot?.SevenDay);
        System.Windows.Documents.Typography.SetNumeralAlignment(_fiveValue, FontNumeralAlignment.Tabular);
        System.Windows.Documents.Typography.SetNumeralAlignment(_sevenValue, FontNumeralAlignment.Tabular);
        var remaining = snapshot.FiveHour?.RemainingPercent;
        _fiveRing.Visibility = remaining.HasValue ? Visibility.Visible : Visibility.Collapsed;
        _fiveRing.ActivityKind = snapshot.ActivityKind;
        _fiveRing.ActivityEventAt = snapshot.ActivityEventAt;
        if (remaining.HasValue)
        {
            _fiveRing.BeginAnimation(CodexInkRing.ValueProperty, null);
            _fiveRing.Value = Math.Clamp(remaining.Value / 100, 0, 1);
            if (_latestSnapshot?.FiveHour?.RemainingPercent is { } previous &&
                Math.Abs(previous - remaining.Value) > 0.01 && CanAnimateMotion())
            {
                _fiveRing.AnimateFrom(Math.Clamp(previous / 100, 0, 1));
                if (previous > 20 && remaining.Value <= 20) _fiveRing.CueLowQuota();
            }
        }
        _tokens.Text = FormatTokens(snapshot.Usage.TotalTokens);
        _cost.Text = FormatCost(snapshot.CostUsd, snapshot.Currency);
        ToolTipService.SetToolTip(_cost, Strings.Format(
            "CodexMeterPriceCatalogFormat",
            CodexCostEstimator.PriceCatalogDate));
        _sessions.Text = snapshot.SessionCount.ToString(CultureInfo.CurrentCulture);
        RebuildAnalytics(snapshot.Periods, snapshot.DailyUsage, snapshot.Currency);
        RebuildProjects(snapshot.Projects);
        RebuildCycles(snapshot.QuotaCycles, snapshot.Currency);
        RebuildSessions(snapshot.RecentSessions, snapshot.RecycledSessions);
        _tokenBreakdown.Text = Strings.Format(
            "CodexMeterTokenBreakdownFormat",
            FormatTokens(snapshot.Usage.InputTokens),
            FormatTokens(snapshot.Usage.CachedInputTokens + snapshot.Usage.CacheCreationInputTokens),
            FormatTokens(snapshot.Usage.OutputTokens),
            FormatTokens(snapshot.Usage.ReasoningOutputTokens));
        UpdateDiagnostics(snapshot.Components, snapshot.Network);
        var sourceState = CodexQuotaSourceAnalyzer.Analyze(
            snapshot.QuotaSource,
            snapshot.QuotaObservedAt,
            DateTimeOffset.UtcNow);
        var sourceLabel = sourceState.Kind switch
        {
            "mixed" => Strings.Get("CodexMeterQuotaMixed"),
            "live" => Strings.Get("CodexMeterQuotaLive"),
            "cache" => Strings.Get("CodexMeterQuotaCache"),
            "session" => Strings.Get("CodexMeterQuotaSession"),
            _ => Strings.Get("CodexMeterQuotaUnavailable")
        };
        var source = sourceState.AgeMinutes is > 0
            ? Strings.Format("CodexMeterQuotaAgeFormat", sourceLabel, sourceState.AgeMinutes.Value)
            : sourceLabel;
        var diagnostics = snapshot.QuotaDiagnostics is
            { InitializeMilliseconds: { } initialize, ReadMilliseconds: { } read }
            ? Environment.NewLine + Strings.Format(
                "CodexMeterQuotaDiagnosticsFormat", initialize, read)
            : "";
        _footer.Text = $"{source} · {snapshot.GeneratedAt.ToLocalTime():HH:mm:ss}" +
            diagnostics +
            (string.IsNullOrWhiteSpace(snapshot.Message) ? "" : $"{Environment.NewLine}{snapshot.Message}");
        _latestSnapshot = snapshot;
        ApplyTheme(_theme);
    }

    internal void SetRefreshing()
    {
        _refreshButton.IsEnabled = false;
        _footer.Text = Strings.Get("CodexMeterRefreshing");
    }

    internal void ApplyTheme(PaperBodyTheme theme)
    {
        _theme = theme;
        var text = Brush(theme.TextColor, Colors.Black);
        var weak = Brush(theme.WeakTextColor, Colors.Gray);
        var accent = Brush(theme.AccentColor, Colors.DodgerBlue);
        var border = Brush(theme.BorderColor, Color.FromArgb(70, 80, 80, 80));
        var tint = new SolidColorBrush(Color.FromArgb(theme.IsDark ? (byte)34 : (byte)22,
            ((SolidColorBrush)accent).Color.R, ((SolidColorBrush)accent).Color.G, ((SolidColorBrush)accent).Color.B));
        FontFamily font;
        try { font = new FontFamily(theme.FontFamily); }
        catch { font = SystemFonts.MessageFontFamily; }
        ApplyTextStyle(this, text, weak, font, theme.FontScale);
        _statusPill.Background = tint;
        _status.Foreground = accent;
        _refreshButton.Foreground = text;
        _refreshButton.Background = Brushes.Transparent;
        _refreshButton.BorderBrush = border;
        _networkButton.Foreground = text;
        _networkButton.Background = Brushes.Transparent;
        _networkButton.BorderBrush = border;
        _copyDiagnosticsButton.Foreground = text;
        _copyDiagnosticsButton.Background = Brushes.Transparent;
        _copyDiagnosticsButton.BorderBrush = border;
        _fiveProgress.Foreground = accent;
        _fiveRing.ForegroundBrush = _latestSnapshot?.FiveHour?.RemainingPercent switch
        {
            <= 10 => new SolidColorBrush(Color.FromRgb(190, 91, 81)),
            <= 20 => new SolidColorBrush(Color.FromRgb(212, 138, 62)),
            _ => accent
        };
        _fiveRing.TrackBrush = border;
        _fiveProgress.Foreground = _fiveRing.ForegroundBrush;
        _fiveRing.InvalidateVisual();
        _fiveRing.RefreshMotion();
        _sevenProgress.Foreground = accent;
        _fiveProgress.Background = border;
        _sevenProgress.Background = border;
        foreach (var progress in _dynamicProgress)
        {
            progress.Foreground = accent;
            progress.Background = border;
        }
        foreach (var button in _dynamicButtons)
        {
            button.Foreground = text;
            button.Background = Brushes.Transparent;
            button.BorderBrush = border;
        }
        foreach (var button in _sectionButtons)
        {
            var selected = string.Equals(button.Tag as string, _activeSection, StringComparison.Ordinal);
            button.Foreground = selected ? accent : weak;
            button.Background = selected ? tint : Brushes.Transparent;
            button.BorderBrush = selected ? accent : border;
        }
        UpdateStatusMotion();
    }

    private bool CanAnimateMotion() =>
        _theme.AnimationsEnabled && SystemParameters.ClientAreaAnimation && IsVisible;

    private void OnSystemMotionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or null)
            UpdateStatusMotion();
    }

    private void UpdateStatusMotion()
    {
        var kind = _latestSnapshot?.ActivityKind ?? "idle";
        // The ink mark owns activity motion; keep the accompanying words steady and readable.
        var enabled = false;
        if (_motionKind == kind && _motionEnabled == enabled) return;
        _motionKind = kind;
        _motionEnabled = enabled;
        _statusPill.BeginAnimation(UIElement.OpacityProperty, null);
    }

    private Button CreateSectionButton(string key, string label)
    {
        var button = new Button
        {
            Content = label,
            Tag = key,
            Padding = new Thickness(5, 5, 5, 5),
            Margin = new Thickness(2, 0, 2, 0),
            BorderThickness = new Thickness(0, 0, 0, 2),
            Cursor = Cursors.Hand,
            FontSize = 10.5
        };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => SetSection(key);
        _sectionButtons.Add(button);
        return button;
    }

    private void CopyDiagnostics()
    {
        if (_latestSnapshot == null) return;
        try
        {
            Clipboard.SetText(CodexDiagnosticSummary.Build(_latestSnapshot));
            _footer.Text = Strings.Get("CodexMeterDiagnosticsCopied");
        }
        catch (Exception ex)
        {
            _footer.Text = Strings.Format("CodexMeterDiagnosticsCopyFailedFormat", ex.GetBaseException().Message);
        }
    }

    private void SetSection(string key)
    {
        if (_compact) return;
        _activeSection = key;
        var overview = string.Equals(key, "overview", StringComparison.Ordinal);
        if (_quotaSection != null) _quotaSection.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
        if (_metricsSection != null) _metricsSection.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
        if (_analysisSection != null) _analysisSection.Visibility = key == "analysis" ? Visibility.Visible : Visibility.Collapsed;
        if (_projectsSection != null) _projectsSection.Visibility = key == "projects" ? Visibility.Visible : Visibility.Collapsed;
        if (_diagnosticsSection != null) _diagnosticsSection.Visibility = key == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        ApplyTheme(_theme);
    }

    private void ShowLoading()
    {
        _account.Text = Strings.Get("CodexMeterWaitingForData");
        _status.Text = Strings.Get("CodexMeterLoading");
        _fiveValue.Text = _sevenValue.Text = "—";
        _fiveReset.Text = _sevenReset.Text = Strings.Get("CodexMeterWaitingForData");
        _tokens.Text = _cost.Text = _sessions.Text = "—";
        _footer.Text = Strings.Get("CodexMeterLoadingDetail");
        _components.Text = "—";
        _network.Children.Add(new TextBlock
        {
            Text = Strings.Get("CodexMeterNetworkNotChecked"),
            FontSize = 9.5,
            TextWrapping = TextWrapping.Wrap
        });
    }

    internal void SetNetworkChecking()
    {
        if (_compact) return;
        _networkButton.IsEnabled = false;
        _network.Children.Clear();
        _network.Children.Add(new TextBlock { Text = Strings.Get("CodexMeterLoading"), FontSize = 9.5 });
    }

    private (TextBlock Value, TextBlock Reset, ProgressBar Progress, CodexInkRing Ring) AddQuotaCard(
        Grid parent, int column, string title, bool primary)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontSize = 10.5 });
        var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Height = 37 };
        var ring = new CodexInkRing
        {
            Width = 32,
            Height = 32,
            Visibility = Visibility.Collapsed,
            MotionAllowed = () => _theme.AnimationsEnabled,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (primary) valueRow.Children.Add(ring);
        var value = new TextBlock
        {
            FontSize = _compact ? 20 : 24,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(primary ? 8 : 0, 2, 0, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        valueRow.Children.Add(value);
        panel.Children.Add(valueRow);
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, BorderThickness = new Thickness(0), IsHitTestVisible = false };
        AutomationProperties.SetName(progress, title);
        panel.Children.Add(progress);
        var reset = new TextBlock { FontSize = 9.5, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        panel.Children.Add(reset);
        var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 8), Child = panel };
        Grid.SetColumn(card, column);
        parent.Children.Add(card);
        return (value, reset, progress, ring);
    }

    private TextBlock AddMetric(Grid parent, int column, string label)
    {
        var panel = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 7, 0, 0, 0) };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 9.5 });
        var value = new TextBlock { FontSize = _compact ? 13 : 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) };
        panel.Children.Add(value);
        Grid.SetColumn(panel, column);
        parent.Children.Add(panel);
        return value;
    }

    private void RebuildProjects(IReadOnlyList<CodexProjectUsage> projects)
    {
        if (_compact) return;
        while (_projects.Children.Count > 1) _projects.Children.RemoveAt(1);
        foreach (var project in projects.Take(4))
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = project.Name, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = project.Path };
            var value = new TextBlock { Text = FormatTokens(project.TotalTokens), FontSize = 10.5, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(value, 1);
            row.Children.Add(name);
            row.Children.Add(value);
            _projects.Children.Add(row);
        }
        if (projects.Count == 0)
        {
            _projects.Children.Add(new TextBlock { Text = Strings.Get("CodexMeterNoSessions"), FontSize = 10.5 });
        }
    }

    private void RebuildAnalytics(
        IReadOnlyList<CodexUsagePeriod> periods,
        IReadOnlyList<CodexDailyUsage> daily,
        CodexCurrencyRate currency)
    {
        if (_compact) return;
        _periods.Children.Clear();
        _periods.ColumnDefinitions.Clear();
        _trend.Children.Clear();
        _dynamicProgress.Clear();

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["today"] = Strings.Get("CodexMeterToday"),
            ["7d"] = Strings.Get("CodexMeterSevenDay"),
            ["30d"] = Strings.Get("CodexMeterThirtyDays"),
            ["90d"] = Strings.Get("CodexMeterNinetyDays")
        };
        for (var index = 0; index < periods.Count; index++)
        {
            _periods.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var period = periods[index];
            var panel = new StackPanel { Margin = new Thickness(index == 0 ? 0 : 5, 0, 0, 0) };
            panel.Children.Add(new TextBlock
            {
                Text = labels.GetValueOrDefault(period.Key, period.Key),
                FontSize = 9.5
            });
            panel.Children.Add(new TextBlock
            {
                Text = FormatTokens(period.Usage.TotalTokens),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 2, 0, 0)
            });
            panel.Children.Add(new TextBlock
            {
                Text = FormatCost(period.CostUsd, currency),
                FontSize = 9.5
            });
            Grid.SetColumn(panel, index);
            _periods.Children.Add(panel);
        }

        var recent = daily.TakeLast(7).ToArray();
        var maximum = Math.Max(1, recent.Max(day => day.Usage.TotalTokens));
        foreach (var day in recent)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            row.Children.Add(new TextBlock
            {
                Text = day.Date.ToString("M-d", CultureInfo.CurrentCulture),
                FontSize = 9.5,
                VerticalAlignment = VerticalAlignment.Center
            });
            var progress = new ProgressBar
            {
                Minimum = 0,
                Maximum = maximum,
                Value = day.Usage.TotalTokens,
                Height = 5,
                BorderThickness = new Thickness(0),
                IsHitTestVisible = false,
                Margin = new Thickness(5, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _dynamicProgress.Add(progress);
            Grid.SetColumn(progress, 1);
            row.Children.Add(progress);
            var value = new TextBlock
            {
                Text = FormatTokens(day.Usage.TotalTokens),
                FontSize = 9.5,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(value, 2);
            row.Children.Add(value);
            _trend.Children.Add(row);
        }
    }

    private void RebuildCycles(IReadOnlyList<CodexQuotaCycleSummary> cycles, CodexCurrencyRate currency)
    {
        if (_compact) return;
        _cycles.Children.Clear();
        foreach (var cycle in cycles.Take(4))
        {
            _cycles.Children.Add(new TextBlock
            {
                Text = Strings.Format(
                    "CodexMeterCycleRowFormat",
                    cycle.ResetAt.ToLocalTime().ToString("M-d", CultureInfo.CurrentCulture),
                    cycle.RemainingPercent ?? 0,
                    $"{FormatTokens(cycle.Usage.TotalTokens)} · {FormatCost(cycle.CostUsd, currency)}"),
                FontSize = 9.5,
                Margin = new Thickness(0, 1, 0, 1),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        if (cycles.Count == 0)
            _cycles.Children.Add(new TextBlock { Text = Strings.Get("CodexMeterWaitingForData"), FontSize = 9.5 });
    }

    private void RebuildSessions(
        IReadOnlyList<CodexSessionItem> sessions,
        IReadOnlyList<CodexTrashItem> recycled)
    {
        if (_compact) return;
        _sessionsList.Children.Clear();
        _recycledList.Children.Clear();
        _dynamicButtons.Clear();
        foreach (var session in sessions.Take(6))
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var details = new StackPanel();
            details.Children.Add(new TextBlock
            {
                Text = session.ProjectName,
                FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            details.Children.Add(new TextBlock
            {
                Text = $"{session.Model} · {FormatTokens(session.TotalTokens)} · {session.UpdatedAt?.ToLocalTime():M-d HH:mm}",
                FontSize = 9,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            row.Children.Add(details);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
            actions.Children.Add(SessionActionButton(Strings.Get("CodexMeterResumeSession"), () => _resumeSession?.Invoke(session.SessionId)));
            actions.Children.Add(SessionActionButton(Strings.Get("CodexMeterRecycleSession"), () =>
            {
                if (MessageBox.Show(
                        Strings.Format("CodexMeterRecycleConfirmFormat", session.SessionId[..Math.Min(8, session.SessionId.Length)]),
                        Strings.Get("CodexMeterRecentSessions"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    _recycleSession?.Invoke(session.SessionId);
            }));
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);
            _sessionsList.Children.Add(row);
        }
        if (sessions.Count == 0)
            _sessionsList.Children.Add(new TextBlock { Text = Strings.Get("CodexMeterNoSessions"), FontSize = 9.5 });

        foreach (var item in recycled.Take(5))
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = $"{item.SessionId[..Math.Min(8, item.SessionId.Length)]} · {item.DeletedAt.ToLocalTime():M-d HH:mm}",
                FontSize = 9.5,
                VerticalAlignment = VerticalAlignment.Center
            });
            var restore = SessionActionButton(Strings.Get("CodexMeterRestoreSession"), () => _restoreSession?.Invoke(item.Id));
            Grid.SetColumn(restore, 1);
            row.Children.Add(restore);
            _recycledList.Children.Add(row);
        }
        if (recycled.Count == 0)
            _recycledList.Children.Add(new TextBlock { Text = "—", FontSize = 9.5 });
        ApplyTheme(_theme);
    }

    private Button SessionActionButton(string label, Action action)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(3, 0, 0, 0),
            BorderThickness = new Thickness(1),
            FontSize = 9,
            Cursor = Cursors.Hand
        };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        _dynamicButtons.Add(button);
        return button;
    }

    private void UpdateDiagnostics(CodexComponentSummary components, CodexNetworkSnapshot? network)
    {
        if (_compact) return;
        _components.Text = Strings.Format(
            "CodexMeterComponentCountsFormat",
            components.SkillCount,
            components.PluginCount,
            components.McpServerCount);
        ToolTipService.SetToolTip(_components, string.Join(Environment.NewLine,
            components.Skills.Concat(components.Plugins).Concat(components.McpServers).Take(24)));
        _networkButton.IsEnabled = true;
        _network.Children.Clear();
        if (network == null)
        {
            _network.Children.Add(new TextBlock
            {
                Text = Strings.Get("CodexMeterNetworkNotChecked"),
                FontSize = 9.5,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }
        foreach (var probe in network.Probes)
        {
            _network.Children.Add(new TextBlock
            {
                Text = probe.Reachable
                    ? Strings.Format("CodexMeterNetworkReachableFormat", probe.Name, probe.LatencyMilliseconds ?? 0)
                    : Strings.Format("CodexMeterNetworkFailedFormat", probe.Name),
                ToolTip = probe.Error,
                FontSize = 9.5,
                Margin = new Thickness(0, 1, 0, 1)
            });
        }
    }

    private void UpdateQuota(TextBlock value, TextBlock reset, ProgressBar progress,
        CodexRateLimitWindow? window, CodexRateLimitWindow? previous)
    {
        progress.BeginAnimation(RangeBase.ValueProperty, null);
        if (window?.RemainingPercent is not { } remaining)
        {
            value.Text = "—";
            reset.Text = Strings.Get("CodexMeterQuotaUnavailable");
            progress.Value = 0;
            return;
        }
        remaining = Math.Clamp(remaining, 0, 100);
        value.Text = $"{remaining:0}%";
        progress.Value = remaining;
        if (previous?.RemainingPercent is { } oldRemaining &&
            Math.Abs(oldRemaining - remaining) > 0.01 && CanAnimateMotion())
        {
            progress.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(
                Math.Clamp(oldRemaining, 0, 100), remaining, TimeSpan.FromMilliseconds(520))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
            if (oldRemaining > 20 && remaining <= 20)
            {
                value.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.45, 1,
                    TimeSpan.FromMilliseconds(600)) { FillBehavior = FillBehavior.Stop });
            }
        }
        reset.Text = window.ResetAt.HasValue
            ? Strings.Format("CodexMeterResetsFormat", window.ResetAt.Value.ToLocalTime().ToString("M-d HH:mm", CultureInfo.CurrentCulture))
            : Strings.Get("CodexMeterResetUnknown");
    }

    private static string FormatTokens(long value) => Math.Abs(value) switch
    {
        >= 1_000_000 => $"{value / 1_000_000d:0.0}M",
        >= 1_000 => $"{value / 1_000d:0.0}K",
        _ => value.ToString(CultureInfo.CurrentCulture)
    };

    private static string FormatCost(double? usd, CodexCurrencyRate currency)
    {
        if (!usd.HasValue) return "—";
        return string.Equals(currency.CurrencyCode, "CNY", StringComparison.OrdinalIgnoreCase)
            ? $"¥{usd.Value * currency.UsdToCurrency:0.00}"
            : $"${usd.Value:0.00}";
    }

    private static Brush Brush(string value, Color fallback)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)!); }
        catch { return new SolidColorBrush(fallback); }
    }

    private static void ApplyTextStyle(DependencyObject root, Brush text, Brush weak, FontFamily font, double scale)
    {
        foreach (var item in LogicalTreeHelper.GetChildren(root))
        {
            if (item is not DependencyObject child) continue;
            if (child is TextBlock block)
            {
                block.Foreground = block.FontSize <= 11 ? weak : text;
                block.FontFamily = font;
                block.FontSize = Math.Max(8, block.FontSize * Math.Clamp(scale, 0.75, 2) / Math.Clamp(block.Tag as double? ?? scale, 0.75, 2));
                block.Tag = scale;
            }
            ApplyTextStyle(child, text, weak, font, scale);
        }
    }
}
