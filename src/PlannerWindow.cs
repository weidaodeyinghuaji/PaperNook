using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo;

/// <summary>Query surfaces only: all tasks remain owned by their existing todo papers.</summary>
internal sealed partial class PlannerWindow : Window
{
    private readonly AppController _controller;
    private readonly Grid _layout = new();
    private readonly StackPanel _sidebar = new();
    private readonly StackPanel _tasks = new();
    private readonly Border _detailHost = new();
    private readonly TextBox _search = new();
    private readonly TextBox _quickAdd = new();
    private readonly TextBlock _heading = new();
    private readonly TextBlock _status = new();
    private readonly DispatcherTimer _midnightTimer;
    private DateOnly _today = DateOnly.FromDateTime(DateTime.Now);
    private bool _refreshQueued;
    private bool _sourceRefreshPending;
    private bool _filterOnlyRefresh;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private bool _closed;
    private PlannerTask? _selected;
    private string _selectedVersion = "";
    private DatePicker? _planDate;
    private DatePicker? _dueDate;
    private ComboBox? _priority;
    private ComboBox? _list;
    private TextBox? _duration;
    private TextBox? _taskTitle;
    private TextBlock? _quickAddHint;
    private Button? _showAddedButton;
    private PlannerTask? _lastAddedTask;
    private TextBox? _tags;
    private TextBox? _start;
    private TextBox? _end;
    private DatePicker? _startDate;
    private DatePicker? _endDate;
    private CheckBox? _locked;
    private bool _detailsDirty;

    public string View { get; private set; }
    public string? PaperId { get; private set; }
    public bool IsPinned { get; }

    public PlannerWindow(AppController controller, string view = "today", string? paperId = null, bool pinned = false)
    {
        _controller = controller;
        View = view;
        PaperId = paperId;
        IsPinned = pinned;
        Title = Strings.Get("PlannerTitle");
        Width = pinned ? 410 : 1120;
        Height = pinned ? 540 : 760;
        MinWidth = pinned ? 340 : 720;
        MinHeight = 400;
        Topmost = pinned;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = AppTypography.UiFontFamily;
        FontSize = AppTypography.Scale(13);
        Language = AppTypography.Language;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Typography.SetNumeralAlignment(this, FontNumeralAlignment.Tabular);
        AppTypography.ApplyTextRendering(this);
        BuildShell();
        RefreshAppearance();
        RefreshTasks();
        _layout.SizeChanged += (_, _) => UpdateResponsiveLayout();
        _midnightTimer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) =>
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (_today != today) { _today = today; QueueRefresh(); }
        }, Dispatcher);
        _completionTimer.Tick += (_, _) => FinishCompletions();
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); QueueRefresh(sourceChanged: false); };
        Loaded += (_, _) => SystemParameters.StaticPropertyChanged += OnMotionPreferenceChanged;
        Loaded += (_, _) => InitializePinnedLayout();
        LocationChanged += (_, _) => RememberPinnedLayout();
        SizeChanged += (_, _) => RememberPinnedLayout();
        DpiChanged += (_, _) =>
        {
            if (!IsPinned) return;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (_closed || !_pinLayoutReady) return;
                WindowWorkAreaHelper.InvalidateMonitorGeometryCache();
                ApplyPinnedBounds(CapturePinnedLayout(), WindowWorkAreaHelper.WorkAreaFor(this));
            }));
        };
        Unloaded += (_, _) => SystemParameters.StaticPropertyChanged -= OnMotionPreferenceChanged;
        Closed += (_, _) => { _closed = true; _midnightTimer.Stop(); _completionTimer.Stop(); _searchTimer.Stop(); };
        Closing += (_, e) => { if (_controller.IsRunning && !TryPrepareExit()) e.Cancel = true; };
    }

    private static string S(string key) => Strings.Get("Planner" + key);
    private static TextBlock Label(string text, bool weak = false)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 5) };
        label.SetResourceReference(TextBlock.ForegroundProperty, weak ? "PlannerMuted" : "PlannerInk");
        return label;
    }
    private static Button ActionButton(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(2), HorizontalContentAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => action();
        return button;
    }

    private void BuildShell()
    {
        var shell = new DockPanel { Margin = new Thickness(16) };
        shell.SetResourceReference(Panel.BackgroundProperty, "PlannerCanvas");
        Content = shell;
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(header, Dock.Top); shell.Children.Add(header);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
        if (!IsPinned)
        {
            actions.Children.Add(ActionButton("+ " + S("Add"), () => _quickAdd.Focus()));
            var more = ActionButton("···", () => { }); more.ToolTip = S("More");
            var menu = new ContextMenu();
            AddMenu(menu, S("Search"), () => { _searchHost.Visibility = Visibility.Visible; _search.Focus(); });
            AddMenu(menu, S("NewList"), () => EditList(null));
            AddMenu(menu, S("Pin"), () => Run(() => _controller.PinPlannerView(View, PaperId)));
            AddMenu(menu, S("UndoSchedule"), () => Run(() => _controller.PaperCommands.UndoPlannerSchedule()));
            AddMenu(menu, S("AiHelp"), () => MessageBox.Show(this, S("AiHelpDetail"), S("Title"),
                MessageBoxButton.OK, MessageBoxImage.Information));
            more.Click += (_, _) => { menu.PlacementTarget = more; menu.IsOpen = true; };
            actions.Children.Add(more);
        }
        else
        {
            var spacing = ActionButton("···", () => { }); spacing.ToolTip = S("More");
            var spacingMenu = new ContextMenu();
            AddMenu(spacingMenu, S("DensityAuto"), () => SetPinnedDensity("auto"));
            AddMenu(spacingMenu, S("DensityComfortable"), () => SetPinnedDensity("comfortable"));
            AddMenu(spacingMenu, S("DensityCompact"), () => SetPinnedDensity("compact"));
            spacing.Click += (_, _) => { spacingMenu.PlacementTarget = spacing; spacingMenu.IsOpen = true; };
            actions.Children.Add(spacing);
            var addToggle = ActionButton("+", () => { _addHost.Visibility = Visibility.Visible; _quickAdd.Focus(); });
            addToggle.ToolTip = S("Add"); actions.Children.Add(addToggle);
            actions.Children.Add(ActionButton(S("Open"), _controller.OpenPlanner));
            actions.Opacity = 0;
            shell.MouseEnter += (_, _) => SetPinnedToolsVisible(actions, true);
            shell.MouseLeave += (_, _) => { if (!shell.IsKeyboardFocusWithin) SetPinnedToolsVisible(actions, false); };
            shell.IsKeyboardFocusWithinChanged += (_, _) => SetPinnedToolsVisible(actions, shell.IsKeyboardFocusWithin || shell.IsMouseOver);
            _quickAdd.LostKeyboardFocus += (_, _) =>
            { if (string.IsNullOrWhiteSpace(_quickAdd.Text)) _addHost.Visibility = Visibility.Collapsed; };
        }
        _heading.FontSize = AppTypography.Scale(IsPinned ? 20 : 23);
        _heading.FontWeight = FontWeights.SemiBold;
        _heading.VerticalAlignment = VerticalAlignment.Center;
        var headingStack = new StackPanel(); headingStack.Children.Add(_heading);
        _subtitle.FontSize = AppTypography.Scale(11);
        _subtitle.SetResourceReference(TextBlock.ForegroundProperty, "PlannerMuted");
        headingStack.Children.Add(_subtitle); header.Children.Add(headingStack);

        DockPanel.SetDock(_status, Dock.Bottom);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 12, 0, 0);
        var feedback = new StackPanel(); DockPanel.SetDock(feedback, Dock.Bottom);
        feedback.Children.Add(_status);
        _showAddedButton = ActionButton(S("ShowAdded"), ShowAddedTask);
        _showAddedButton.Visibility = Visibility.Collapsed;
        feedback.Children.Add(_showAddedButton); shell.Children.Add(feedback);
        shell.Children.Add(_layout);
        _layout.ColumnDefinitions.Add(new() { Width = new GridLength(IsPinned ? 0 : 210) });
        _layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _layout.ColumnDefinitions.Add(new() { Width = new GridLength(0) });
        var sideScroll = new ScrollViewer { Content = _sidebar, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var sideSurface = new Border { Child = sideScroll, CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 12, 8, 12) };
        sideSurface.SetResourceReference(Border.BackgroundProperty, "PlannerSide");
        Grid.SetColumn(sideSurface, 0); _layout.Children.Add(sideSurface);
        if (IsPinned) sideSurface.Visibility = Visibility.Collapsed;

        var center = new DockPanel { Margin = new Thickness(IsPinned ? 0 : 18, 0, 0, 0) };
        Grid.SetColumn(center, 1); _layout.Children.Add(center);
        var filters = new StackPanel(); DockPanel.SetDock(filters, Dock.Top); center.Children.Add(filters);
        _search.ToolTip = S("Search");
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _search.Margin = new Thickness(0, 0, 0, 4);
        _search.Height = 36;
        System.Windows.Automation.AutomationProperties.SetName(_search, S("Search"));
        var searchHost = new DockPanel();
        var searchLabel = Label(S("Search"), true); searchLabel.Margin = new Thickness(0, 8, 12, 0);
        DockPanel.SetDock(searchLabel, Dock.Left); searchHost.Children.Add(searchLabel); searchHost.Children.Add(_search);
        _searchHost = searchHost; searchHost.Visibility = Visibility.Collapsed;
        if (!IsPinned) filters.Children.Add(searchHost);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && !IsPinned)
            { _searchHost.Visibility = Visibility.Visible; _search.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape && _searchHost.Visibility == Visibility.Visible)
            { _search.Clear(); _searchHost.Visibility = Visibility.Collapsed; _quickAdd.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape && _selected != null)
            { CloseDetails(); e.Handled = true; }
        };
        var add = new DockPanel { Margin = new Thickness(0, 12, 0, 14) };
        var addButton = ActionButton("↵", AddTask); addButton.ToolTip = S("Add"); DockPanel.SetDock(addButton, Dock.Right);
        add.Children.Add(addButton);
        _quickAdd.ToolTip = S("QuickAdd");
        _quickAdd.Height = 36; _quickAdd.VerticalAlignment = VerticalAlignment.Center;
        _quickAdd.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddTask(); e.Handled = true; } };
        System.Windows.Automation.AutomationProperties.SetName(_quickAdd, S("QuickAdd"));
        var entry = new Grid(); entry.Children.Add(_quickAdd);
        var hint = Label(S("QuickAdd"), true); hint.Margin = new Thickness(11, 9, 11, 0);
        _quickAddHint = hint;
        hint.IsHitTestVisible = false; hint.TextWrapping = TextWrapping.NoWrap; hint.TextTrimming = TextTrimming.CharacterEllipsis;
        entry.Children.Add(hint);
        _quickAdd.TextChanged += (_, _) => hint.Visibility = string.IsNullOrEmpty(_quickAdd.Text) ? Visibility.Visible : Visibility.Collapsed;
        add.Children.Add(entry); filters.Children.Add(add); _addHost = add;
        UpdateQuickAddContext();
        _taskScroll.Content = _tasks; _taskScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _taskScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        center.Children.Add(_taskScroll);
        _detailHost.Margin = new Thickness(18, 0, 0, 0);
        Grid.SetColumn(_detailHost, 2); _layout.Children.Add(_detailHost);
    }

    public void RefreshAppearance()
    {
        ApplyPlannerTheme();
        RefreshMotionPreference();
        _invalidateRows = true; _sidebarLists = null;
        QueueRefresh(sourceChanged: false);
        if (_selected != null && !_detailsDirty) BuildDetails(_selected);
    }

    public void QueueRefresh(bool sourceChanged = true)
    {
        if (_closed) return;
        _sourceRefreshPending |= sourceChanged;
        if (_refreshQueued) return;
        _refreshQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _refreshQueued = false;
            _filterOnlyRefresh = !_sourceRefreshPending; _sourceRefreshPending = false;
            try { if (!_closed) RefreshTasks(); }
            finally { _filterOnlyRefresh = false; }
        }));
    }

    private void Navigate(string view, string? paperId = null)
    {
        if (View == view && PaperId == paperId) return;
        if (!CanLeaveDetails()) return;
        _searchTimer.Stop();
        _scrollPositions[(View, PaperId)] = CurrentTaskScroll(); CaptureWeekColumnScroll();
        View = view; PaperId = paperId; _selected = null; _detailsDirty = false;
        UpdateQuickAddContext();
        _heading.Text = ViewTitle();
        foreach (var entry in _navigationButtons)
            UpdateNavigationSelection(entry.Value, entry.Key == (View, PaperId));
        _navigateScroll = _scrollPositions.GetValueOrDefault((view, paperId));
        _detailHost.Child = null; _layout.ColumnDefinitions[2].Width = new GridLength(0);
        UpdateResponsiveLayout();
        _navigating = true;
        try { RefreshTasks(); }
        finally { _navigating = false; }
    }

    private string ViewTitle() => View == "list"
        ? _controller.State.Papers.FirstOrDefault(p => p.Id == PaperId)?.Title ?? S("All")
        : S(View switch { "inbox" => "Inbox", "today" => "Today", "tomorrow" => "Tomorrow",
            "week" => "Week", "completed" => "Completed", "agenda" => "Agenda", _ => "All" });

    private void RefreshTasks()
    {
        var animateRows = !_navigating && !_filterOnlyRefresh && _virtualTaskList == null;
        var before = animateRows && MotionEnabled && IsLoaded ? CaptureRowPositions()
            : new Dictionary<(string Paper, string Todo, int Occurrence), double>();
        var scroll = _navigateScroll ?? CurrentTaskScroll(); _navigateScroll = null;
        if (!_navigating) CaptureWeekColumnScroll();
        var state = _controller.State;
        _heading.Text = ViewTitle();
        _taskQuery = new PlannerTaskQuery(state);
        RefreshSidebar(state, _taskQuery, !_filterOnlyRefresh && !_navigating || _sourceRefreshPending);
        foreach (var entry in _navigationButtons)
            UpdateNavigationSelection(entry.Value, entry.Key == (View, PaperId));
        var liveKeys = _completing.Count > 0 ? _taskQuery.Tasks.Select(t => t.Key).ToHashSet() : null;
        var tasks = _taskQuery.Query(View, PaperId, _today, _search.Text)
            .Concat(_completing.Values.Where(c => c.Task.Item.Done && c.View == View && c.PaperId == PaperId && c.Search == _search.Text &&
                liveKeys!.Contains(c.Task.Key)).Select(c => c.Task)).DistinctBy(t => t.Key).ToArray();
        _subtitle.Text = _today.ToString("M/d ddd", CultureInfo.CurrentCulture) + "   " +
            string.Format(CultureInfo.CurrentCulture, S("TaskCount"), tasks.Length);
        PrepareReusableRows();
        _tasks.Children.Clear();
        _virtualTaskList = null; _virtualEntries.Clear();
        _largeWeek = tasks.Length >= 80 && View == "week" && _wideWeek && !IsPinned;
        _virtualizeRows = tasks.Length >= 80 && !_largeWeek;
        _compactRows = IsPinned && (_pinDensity == "compact" || _pinDensity == "auto" && tasks.Length >= 8);
        _taskScroll.HorizontalScrollBarVisibility = _wideWeek && View == "week" ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        if (tasks.Length == 0) _tasks.Children.Add(EmptyState());
        if (View == "week")
        {
            AddGroup(S("Overdue"), tasks.Where(t => TaskPlanningRules.IsOverdue(t.Planning, _today)));
            if (_wideWeek && !IsPinned) AddWeekColumns(tasks);
            else for (var day = 0; day < 7; day++)
            {
                var date = _today.AddDays(day);
                AddGroup(date.ToString("M/d ddd", CultureInfo.CurrentCulture), tasks.Where(t =>
                    !TaskPlanningRules.IsOverdue(t.Planning, _today) && TaskPlanningRules.OnDate(t.Planning, date)));
            }
        }
        else if (View == "agenda")
        {
            foreach (var group in tasks.OrderBy(t => t.Planning.ScheduledStart)
                .GroupBy(t => DateOnly.FromDateTime(t.Planning.ScheduledStart!.Value.LocalDateTime)))
                AddGroup(group.Key.ToString("M/d ddd", CultureInfo.CurrentCulture), group);
        }
        else
        {
            AddGroup(S("Overdue"), tasks.Where(t => TaskPlanningRules.IsOverdue(t.Planning, _today)));
            foreach (var priority in new[] { 3, 2, 1, 0 })
                AddGroup(S("Priority" + priority), tasks.Where(t =>
                    !TaskPlanningRules.IsOverdue(t.Planning, _today) && t.Planning.Priority == priority));
        }
        if (_virtualizeRows)
        {
            _virtualTaskList = BuildVirtualTaskList(_virtualEntries);
            _tasks.Children.Add(_virtualTaskList);
            animateRows = false;
        }
        // Never replace unsaved detail edits when a desktop paper or another view changes.
        if (_selected != null && !_detailsDirty)
        {
            var current = _taskQuery.Tasks.FirstOrDefault(t => t.Key == _selected.Key);
            if (current == null)
            {
                _selected = null; _detailHost.Child = null; _layout.ColumnDefinitions[2].Width = new GridLength(0);
                UpdateResponsiveLayout();
            }
            else if (current.Version != _selectedVersion) BuildDetails(current);
        }
        _availableRows.Clear();
        FinishRowLayout(before, scroll, animateRows);
    }

    private void AddGroup(string title, IEnumerable<PlannerTask> source)
    {
        var tasks = source.ToArray(); if (tasks.Length == 0) return;
        var heading = title + " · " + tasks.Length;
        if (_virtualizeRows)
        {
            _virtualEntries.Add(new(this, null, heading));
            _virtualEntries.AddRange(tasks.Select(t => new PlannerListEntry(this, t, null))); return;
        }
        _tasks.Children.Add(GroupHeading(heading));
        if (_largeWeek)
        {
            var list = BuildVirtualTaskList(tasks.Select(t => new PlannerListEntry(this, t, null)), .3, 40, tasks.Length * 75);
            list.Tag = "overdue"; _tasks.Children.Add(list); return;
        }
        foreach (var task in tasks) _tasks.Children.Add(TaskRow(task));
    }

    private UIElement TaskRow(PlannerTask task)
    {
        var stamp = RowStampFor(task);
        if (!_completing.ContainsKey(task.Key) && _availableRows.TryGetValue(task.Key, out var rows) && rows.Count > 0)
        {
            var candidate = rows.Dequeue();
            if (candidate.Stamp == stamp)
            {
                var reused = candidate.Row;
                if (VisualTreeHelper.GetParent(reused) is Panel parent) parent.Children.Remove(reused);
                reused.BeginAnimation(OpacityProperty, null); reused.RenderTransform = Transform.Identity;
                UpdateRowSelection(reused, task); _rowStamps[reused] = stamp;
                return reused;
            }
        }
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var check = new CheckBox { IsChecked = task.Item.Done, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, _compactRows ? 10 : 14, 12, _compactRows ? 8 : 12), ToolTip = S("Complete"),
            IsEnabled = !_completing.ContainsKey(task.Key) };
        check.Tag = _completing.ContainsKey(task.Key);
        System.Windows.Automation.AutomationProperties.SetName(check, S("Complete") + " " + task.Item.Text);
        grid.Children.Add(check);
        var text = new StackPanel();
        var title = Label(task.Item.Text); title.FontSize = AppTypography.Scale(14);
        title.Margin = new Thickness(0, 2, 0, 4);
        if (task.Item.Done) DrawCompletion(title, _completing.ContainsKey(task.Key));
        text.Children.Add(title);
        var p = task.Planning;
        var list = _taskQuery!.ResolveList(task);
        var info = new List<string> { string.IsNullOrWhiteSpace(list.Title) ? S("Untitled") : list.Title };
        if (p.ScheduledStart.HasValue) info.Add(p.ScheduledStart.Value.LocalDateTime.ToString("M/d HH:mm") +
            "–" + p.ScheduledEnd!.Value.LocalDateTime.ToString("HH:mm") + (p.Locked ? " · " + S("Locked") : ""));
        else if (p.DueDate.HasValue) info.Add(S("DueDate") + " " + p.DueDate.Value.ToString("M/d"));
        else if (p.PlannedDate.HasValue) info.Add(p.PlannedDate.Value.ToString("M/d"));
        var metadata = Label(string.Join(" · ", info), true); metadata.FontSize = AppTypography.Scale(11);
        metadata.Margin = new Thickness(0);
        if (TaskPlanningRules.IsOverdue(p, _today)) metadata.SetResourceReference(TextBlock.ForegroundProperty, "PlannerDanger");
        text.Children.Add(metadata);
        var button = ActionButton("", () => SelectTask(task));
        button.Content = text; button.BorderThickness = new Thickness(0); button.Background = Brushes.Transparent;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch; Grid.SetColumn(button, 1); grid.Children.Add(button);
        var row = new Border { Child = grid, CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(_selected?.Key == task.Key ? 2 : 0, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, _compactRows ? 2 : 7), Tag = task.Key };
        row.SetResourceReference(Border.BorderBrushProperty, "PlannerAccent");
        row.SetResourceReference(Border.BackgroundProperty, _selected?.Key == task.Key ? "PlannerSelection" : "PlannerCanvas");
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "PlannerSelection");
        row.MouseLeave += (_, _) => row.SetResourceReference(Border.BackgroundProperty, _selected?.Key == task.Key ? "PlannerSelection" : "PlannerCanvas");
        check.Click += (_, _) => CompleteTask(task, check, row, title);
        if (_completing.TryGetValue(task.Key, out var completing)) FadeCompletedRow(row, completing.Until - Environment.TickCount64);
        _rowStamps[row] = stamp;
        return row;
    }

    internal void SelectTask(PlannerTask task)
    {
        if (!CanLeaveDetails()) return;
        if (IsPinned) { _controller.OpenPlannerTask(task); return; }
        var wasOpen = _selected != null;
        BuildDetails(task);
        RefreshTasks();
        if (!wasOpen) AnimateEntrance(_detailHost, 16);
    }

    private bool CanLeaveDetails() => TryLeaveDetails();

    internal bool TryLeaveDetails(Func<MessageBoxResult>? choose = null)
    {
        if (!_detailsDirty) return true;
        var choice = choose?.Invoke() ?? ShowUnsavedChoice(S("UnsavedChoice"));
        if (choice == MessageBoxResult.No) return true;
        if (choice != MessageBoxResult.Yes) return false;
        SaveDetails();
        return !_detailsDirty;
    }

    internal bool TryPrepareExit(Func<MessageBoxResult>? choose = null)
    {
        if (!TryLeaveDetails(choose)) return false;
        if (string.IsNullOrWhiteSpace(_quickAdd.Text)) return true;
        var choice = choose?.Invoke() ?? ShowUnsavedChoice(S("UnsavedQuickAdd"));
        if (choice == MessageBoxResult.No) return true;
        if (choice != MessageBoxResult.Yes) return false;
        AddTask();
        return string.IsNullOrWhiteSpace(_quickAdd.Text);
    }

    private MessageBoxResult ShowUnsavedChoice(string message)
    {
        var choice = MessageBoxResult.Cancel;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(Label(message));
        var dialog = new Window { Owner = IsVisible ? this : null, Title = S("Title"),
            Content = panel, Width = 380, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Background = Theme.PaperBrush, Foreground = Theme.TextBrush, ResizeMode = ResizeMode.NoResize };
        dialog.Resources.MergedDictionaries.Add(Resources);
        foreach (var (key, result) in new[] { ("Save", MessageBoxResult.Yes),
            ("DiscardChanges", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel) })
        {
            var button = ActionButton(S(key), () => { choice = result; dialog.Close(); });
            button.IsCancel = result == MessageBoxResult.Cancel;
            panel.Children.Add(button);
        }
        dialog.ShowDialog(); return choice;
    }

    private void CloseDetails()
    {
        if (!CanLeaveDetails()) return;
        _selected = null; _detailsDirty = false; _detailHost.Child = null;
        UpdateResponsiveLayout(); RefreshTasks();
    }

    private void BuildDetails(PlannerTask task)
    {
        _selected = task; _selectedVersion = task.Version;
        var p = task.Planning;
        var panel = new StackPanel();
        var close = ActionButton("×", CloseDetails);
        close.ToolTip = S("CloseDetails"); close.HorizontalAlignment = HorizontalAlignment.Right;
        _taskTitle = new TextBox { Text = task.Item.Text, MaxLength = PaperWindow.TodoTextMaxLength,
            TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 36 };
        System.Windows.Automation.AutomationProperties.SetName(_taskTitle, S("TaskName"));
        panel.Children.Add(Label(S("TaskName"), true)); panel.Children.Add(_taskTitle);
        _list = new ComboBox { MinHeight = 32 };
        foreach (var paper in _controller.State.Papers.Where(paper => paper.Type == PaperTypes.Todo))
            _list.Items.Add(new ComboBoxItem { Content = string.IsNullOrWhiteSpace(paper.Title) ? S("Untitled") : paper.Title,
                Tag = paper.Id, IsSelected = paper.Id == TaskPlanningRules.ResolveList(_controller.State, task).Id });
        panel.Children.Add(Label(S("ListName"), true)); panel.Children.Add(_list);
        _planDate = new DatePicker { SelectedDate = p.PlannedDate?.ToDateTime(TimeOnly.MinValue) };
        _dueDate = new DatePicker { SelectedDate = p.DueDate?.ToDateTime(TimeOnly.MinValue) };
        _priority = new ComboBox { ItemsSource = Enumerable.Range(0, 4).Select(i => S("Priority" + i)).ToArray(),
            SelectedIndex = p.Priority, MinHeight = 32 };
        _duration = new TextBox { Text = p.DurationMinutes.ToString(CultureInfo.InvariantCulture) };
        _tags = new TextBox { Text = p.Tags, MaxLength = 256 };
        _start = new TextBox { Text = p.ScheduledStart?.LocalDateTime.ToString("HH:mm") ?? "", Width = 74, MaxLength = 5 };
        _end = new TextBox { Text = p.ScheduledEnd?.LocalDateTime.ToString("HH:mm") ?? "", Width = 74, MaxLength = 5 };
        _startDate = new DatePicker { SelectedDate = p.ScheduledStart?.LocalDateTime.Date };
        _endDate = new DatePicker { SelectedDate = p.ScheduledEnd?.LocalDateTime.Date };
        var startEditor = TimeEditor(_startDate, _start);
        var endEditor = TimeEditor(_endDate, _end);
        _locked = new CheckBox { Content = S("Locked"), IsChecked = p.Locked, Margin = new Thickness(0, 10, 0, 10) };
        foreach (var (key, control) in new (string, UIElement)[] { ("PlannedDate", _planDate), ("DueDate", _dueDate),
            ("Priority", _priority) })
        { panel.Children.Add(Label(S(key), true)); panel.Children.Add(control); }
        var extra = new StackPanel();
        extra.Children.Add(Label(S("Duration"), true)); extra.Children.Add(_duration);
        extra.Children.Add(Label(S("Tags"), true)); extra.Children.Add(_tags);
        panel.Children.Add(new Expander { Header = S("MoreDetails"), Content = extra, Margin = new Thickness(0, 16, 0, 8) });
        var time = new StackPanel();
        time.Children.Add(Label(S("Start"), true)); time.Children.Add(startEditor);
        time.Children.Add(Label(S("End"), true)); time.Children.Add(endEditor);
        time.Children.Add(Label(S("TimeHint"), true)); time.Children.Add(_locked);
        panel.Children.Add(new Expander { Header = S("ArrangeTime"), Content = time,
            IsExpanded = p.ScheduledStart.HasValue, Margin = new Thickness(0, 8, 0, 12) });
        panel.Children.Add(ActionButton(S("ShowPaper"), () => _controller.ShowPaper(task.Paper)));
        panel.Children.Add(ActionButton(S("Reminder"), () => Run(() =>
        {
            if (TodoReminderDialog.TryShow(this, task.Item.ReminderAt ?? DateTimeOffset.Now.AddMinutes(30),
                animate: false, out var reminder))
                _controller.PaperCommands.SetTodoReminder(new SetTodoReminderRequest
                { PaperId = task.Paper.Id, TodoId = task.Item.Id, ReminderAt = reminder }, PaperOperationContext.User());
        })));
        foreach (var box in new[] { _taskTitle, _duration, _tags, _start, _end }) box.TextChanged += (_, _) => MarkDirty();
        _planDate.SelectedDateChanged += (_, _) => MarkDirty();
        _dueDate.SelectedDateChanged += (_, _) => MarkDirty();
        _startDate.SelectedDateChanged += (_, _) => MarkDirty();
        _endDate.SelectedDateChanged += (_, _) => MarkDirty();
        _priority.SelectionChanged += (_, _) => MarkDirty();
        _list.SelectionChanged += (_, _) => MarkDirty();
        _locked.Click += (_, _) => MarkDirty();
        _detailsDirty = false;
        var detail = new DockPanel(); var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        _dirtyLabel = Label(S("Saved"), true); footer.Children.Add(_dirtyLabel);
        var save = ActionButton(S("Save"), SaveDetails); AccentButton(save); _saveButton = save; save.IsEnabled = false;
        save.HorizontalContentAlignment = HorizontalAlignment.Center; footer.Children.Add(save);
        DockPanel.SetDock(footer, Dock.Bottom); detail.Children.Add(footer);
        DockPanel.SetDock(close, Dock.Top); detail.Children.Add(close);
        detail.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        _detailHost.Padding = new Thickness(18); _detailHost.CornerRadius = new CornerRadius(4);
        _detailHost.BorderThickness = new Thickness(1);
        _detailHost.SetResourceReference(Border.BackgroundProperty, "PlannerSurface");
        _detailHost.SetResourceReference(Border.BorderBrushProperty, "PlannerLine");
        _detailHost.Child = detail; UpdateResponsiveLayout();
    }

    private static DockPanel TimeEditor(DatePicker date, TextBox time)
    {
        var panel = new DockPanel();
        DockPanel.SetDock(time, Dock.Right); time.Margin = new Thickness(6, 0, 0, 0);
        panel.Children.Add(time); panel.Children.Add(date); return panel;
    }

    private static DateTimeOffset? ReadTime(DateTime? date, string text)
    {
        if (!date.HasValue && string.IsNullOrWhiteSpace(text)) return null;
        if (!date.HasValue || !TimeOnly.TryParseExact(text.Trim(), "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var time)) throw new ArgumentException(S("TimeHint"));
        var local = DateTime.SpecifyKind(date.Value.Date + time.ToTimeSpan(), DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local))
            throw new ArgumentException(S("AmbiguousTime"));
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private void SaveDetails() => Run(() =>
    {
        if (_selected == null) return;
        if (!int.TryParse(_duration!.Text, out var duration)) throw new ArgumentException(S("DurationError"));
        var planning = new TaskPlanningData
        {
            PlannedDate = _planDate!.SelectedDate is { } planned ? DateOnly.FromDateTime(planned) : null,
            DueDate = _dueDate!.SelectedDate is { } due ? DateOnly.FromDateTime(due) : null,
            Priority = _priority!.SelectedIndex, DurationMinutes = duration, Tags = _tags!.Text.Trim(),
            ListId = (_list!.SelectedItem as ComboBoxItem)?.Tag as string,
            ScheduledStart = ReadTime(_startDate!.SelectedDate, _start!.Text),
            ScheduledEnd = ReadTime(_endDate!.SelectedDate, _end!.Text), Locked = _locked!.IsChecked == true
        };
        var key = _selected.Key;
        _controller.PaperCommands.SetTaskDetails(_selected.Paper.Id, _selected.Item.Id,
            _selectedVersion, _taskTitle!.Text, planning, PaperOperationContext.User());
        var current = TaskPlanningRules.All(_controller.State).First(t => t.Key == key);
        _detailsDirty = false; BuildDetails(current); _status.Text = S("Saved");
    }, detailsError: true);

    private void AddTask() => Run(() =>
    {
        if (string.IsNullOrWhiteSpace(_quickAdd.Text)) return;
        DateOnly? date = View == "today" ? _today : View == "tomorrow" ? _today.AddDays(1) : null;
        var id = _controller.PaperCommands.AddPlannerTask(View == "list" ? PaperId : null, _quickAdd.Text.Trim(), date);
        _lastAddedTask = TaskPlanningRules.All(_controller.State).First(t => t.Item.Id == id);
        _addedTask = _lastAddedTask.Key;
        _showAddedButton!.Visibility = Visibility.Visible;
        _quickAdd.Clear(); _quickAdd.Focus(); _status.Text = S("Added");
    });

    private void UpdateQuickAddContext()
    {
        if (_addHost == null) return;
        _addHost.Visibility = IsPinned || View == "completed" ? Visibility.Collapsed : Visibility.Visible;
        if (_quickAddHint != null) _quickAddHint.Text = View == "list" ? S("QuickAdd") : S("QuickAddInbox");
    }

    internal void ShowAddedTask()
    {
        if (_lastAddedTask == null) return;
        var current = TaskPlanningRules.All(_controller.State).FirstOrDefault(t => t.Key == _lastAddedTask.Key);
        if (current == null) { _status.Text = S("Stale"); return; }
        if (!CanLeaveDetails()) return;
        _detailsDirty = false;
        _search.Clear(); _searchHost.Visibility = Visibility.Collapsed;
        _searchTimer.Stop(); Navigate("list", TaskPlanningRules.ResolveList(_controller.State, current).Id);
        _addedTask = current.Key; SelectTask(current);
    }

    private void EditList(PaperData? paper)
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        var title = new TextBox { Text = paper?.Title ?? "", MaxLength = _controller.State.MaxTitleLength };
        var folder = new TextBox { Text = paper?.PlannerFolder ?? "", MaxLength = 80 };
        panel.Children.Add(Label(S("ListName"))); panel.Children.Add(title);
        panel.Children.Add(Label(S("Folder"))); panel.Children.Add(folder);
        var dialog = new Window { Owner = this, Title = S("EditList"), Content = panel, Width = 360,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Theme.PaperBrush, Foreground = Theme.TextBrush, FontFamily = FontFamily,
            ResizeMode = ResizeMode.NoResize };
        dialog.Resources.MergedDictionaries.Add(Resources);
        dialog.SetResourceReference(BackgroundProperty, "PlannerCanvas");
        dialog.SetResourceReference(ForegroundProperty, "PlannerInk");
        panel.Children.Add(ActionButton(S("Save"), () =>
        {
            try
            {
                if (paper == null) _controller.PaperCommands.CreatePlannerList(title.Text.Trim(), folder.Text.Trim());
                else _controller.PaperCommands.SetPlannerList(paper.Id, title.Text.Trim(), folder.Text.Trim());
                dialog.Close(); QueueRefresh();
            }
            catch (Exception ex) when (ex is PaperCommandException or ArgumentException)
            { MessageBox.Show(dialog, ex.Message, S("Title"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        }));
        dialog.ShowDialog();
    }

    internal void ShowScheduleDraft(PlannerScheduleDraft draft)
    {
        var dialog = new Window { Owner = this, Title = S("Preview"), Width = 580, Height = 600,
            MinWidth = 420, MinHeight = 350, MaxHeight = Math.Max(350, WindowWorkAreaHelper.WorkAreaFor(this).Height - 40),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Theme.PaperBrush, Foreground = Theme.TextBrush, FontFamily = FontFamily, FontSize = FontSize };
        dialog.Resources.MergedDictionaries.Add(Resources);
        dialog.SetResourceReference(BackgroundProperty, "PlannerCanvas");
        dialog.SetResourceReference(ForegroundProperty, "PlannerInk");
        dialog.Content = BuildSchedulePreview(draft, dialog.Close);
        dialog.ShowDialog();
    }

    internal FrameworkElement BuildSchedulePreview(PlannerScheduleDraft draft, Action close)
    {
        var root = new DockPanel { Margin = new Thickness(20) };
        root.SetResourceReference(Panel.BackgroundProperty, "PlannerCanvas");
        var selections = new List<(CheckBox Check, ScheduleChange Change)>();
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var count = Label("", true); footer.Children.Add(count);
        var error = Label("", true); error.SetResourceReference(TextBlock.ForegroundProperty, "PlannerDanger");
        error.Visibility = Visibility.Collapsed; footer.Children.Add(error);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var apply = ActionButton(S("ApplySchedule"), () =>
        {
            try
            {
                var keys = selections.Where(s => s.Check.IsChecked == true).Select(s => s.Change.Task.Key).ToArray();
                _controller.PaperCommands.ApplyPlannerSchedule(draft, keys);
                close(); _status.Text = S("Saved");
            }
            catch (PaperCommandException ex) { error.Text = ex.Message; error.Visibility = Visibility.Visible; }
        });
        AccentButton(apply);
        var cancel = ActionButton(S("Cancel"), close); cancel.IsCancel = true;
        actions.Children.Add(cancel); actions.Children.Add(apply); footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        void UpdateSelection()
        {
            var selected = selections.Count(s => s.Check.IsChecked == true);
            count.Text = string.Format(CultureInfo.CurrentCulture, S("SelectedCount"), selected, selections.Count);
            apply.IsEnabled = selected > 0;
        }
        var header = new StackPanel(); header.Children.Add(Label(S("PreviewNotice"), true));
        var toggles = new WrapPanel();
        toggles.Children.Add(ActionButton(S("SelectAll"), () => { foreach (var s in selections) s.Check.IsChecked = true; }));
        toggles.Children.Add(ActionButton(S("SelectNone"), () => { foreach (var s in selections) s.Check.IsChecked = false; }));
        header.Children.Add(toggles); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var panel = new StackPanel();
        foreach (var change in draft.Changes)
        {
            var row = new StackPanel { Margin = new Thickness(4, 8, 4, 8) };
            var taskTitle = Label(change.Task.Item.Text); taskTitle.Margin = new Thickness(0);
            var check = new CheckBox { Content = taskTitle, IsChecked = true, Tag = false };
            System.Windows.Automation.AutomationProperties.SetName(check, change.Task.Item.Text);
            selections.Add((check, change)); check.Checked += (_, _) => UpdateSelection();
            check.Unchecked += (_, _) => UpdateSelection(); row.Children.Add(check);
            row.Children.Add(Label((change.Before?.ScheduledStart?.ToString("g") ?? S("Unscheduled")) +
                " → " + change.After.ScheduledStart!.Value.ToString("g") + " – " +
                change.After.ScheduledEnd!.Value.ToString("t"), true));
            if (change.After.DueDate is { } due) row.Children.Add(Label(S("DueDate") + " " + due.ToString("M/d"), true));
            panel.Children.Add(row);
        }
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        UpdateSelection(); return root;
    }

    private void Run(Action action, bool detailsError = false)
    {
        try { action(); QueueRefresh(); }
        catch (Exception ex) when (ex is PaperCommandException or ArgumentException)
        { if (detailsError && _dirtyLabel != null) _dirtyLabel.Text = ex.Message;
            else _status.Text = ex.Message; QueueRefresh(); }
    }
}
