using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static int _assertions;
    [STAThread]
    private static int Main(string[] args)
    {
        var temp = Path.Combine(Path.GetTempPath(), "PaperNook-PlannerChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (args.Contains("--navigation-benchmark")) { BenchmarkNavigation(); return 0; }
            if (args.Contains("--daily-use-soak")) { CheckDailyUseSoak(temp); return 0; }
            if (args.Contains("--long-soak")) { CheckLongSoak(temp, args); return 0; }
            CheckQueriesAndIdentity();
            CheckDrafts();
            CheckPersistence(temp);
            CheckTransactions(temp);
            CheckMcp(temp);
            CheckWindow(args);
            CheckFeedback(temp);
            CheckNavigation();
            CheckSelectiveSchedule(temp, args);
            CheckPinnedLayout(temp);
            CheckVirtualLists(temp, args);
            CheckActivityMarks();
            CheckDailyUse(temp);
            CheckReminderCatchUp(temp);
            Console.WriteLine($"PASS planner: {_assertions} assertions; queries, identity, persistence, transaction rollback, draft conflicts, undo and WPF layout.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            // This exact task-created isolated directory contains only our fixture state.
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    private static AppState Fixture()
    {
        var today = new DateOnly(2026, 10, 2);
        return new() { Papers = [new() { Id = "inbox", Title = "收集箱", PlannerInbox = true,
            Items = [new() { Id = "a", Text = "研究报告", Planning = new() { PlannedDate = today, Priority = 3 } },
                new() { Id = "b", Text = "过期任务", Planning = new() { DueDate = today.AddDays(-1) } },
                new() { Id = "c", Text = "已经完成", Done = true }, new() { Id = "placeholder" }] },
            new() { Id = "work", Title = "工作项目", PlannerFolder = "工作", Items = [] }] };
    }

    private static void BenchmarkNavigation()
    {
        foreach (var count in new[] { 50, 500, 1500 })
        {
            var state = Fixture(); state.Papers[0].Items.Clear();
            for (var i = 0; i < count; i++) state.Papers[0].Items.Add(new() {
                Id = "bench-" + i, Text = "任务 " + i,
                Planning = new() { PlannedDate = DateOnly.FromDateTime(DateTime.Now).AddDays(i % 4),
                    Priority = i % 4, ListId = i % 3 == 0 ? "work" : null } });
            var controller = Controller(state);
            var window = new PlannerWindow(controller, "inbox") { Width = 1080, Height = 700,
                ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            DrainDispatcher();
            var navigate = typeof(PlannerWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!;
            void Switch(string view)
            {
                navigate.Invoke(window, [view, view == "list" ? "work" : null]);
                window.UpdateLayout(); DrainDispatcher();
            }
            Switch("today"); Switch("inbox");
            var samples = new List<double>();
            var allocation = GC.GetAllocatedBytesForCurrentThread();
            var views = new[] { "today", "tomorrow", "inbox", "list", "all", "week" };
            for (var i = 0; i < 12; i++)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew(); Switch(views[i % views.Length]);
                samples.Add(clock.Elapsed.TotalMilliseconds);
            }
            samples.Sort();
            Console.WriteLine($"Navigation {count}: median={samples[6]:F1}ms max={samples[^1]:F1}ms allocated={(GC.GetAllocatedBytesForCurrentThread() - allocation) / 12 / 1024}KiB/switch (real WPF window/layout+dispatcher, six views; not compositor latency)");
            window.Close(); StopTimers(controller);
        }
    }

    private static void CheckNavigation()
    {
        var state = Fixture();
        state.Papers[0].Items[0].Planning = new() { PlannedDate = DateOnly.FromDateTime(DateTime.Now) };
        var controller = Controller(state);
        var window = new PlannerWindow(controller, "inbox");
        DrainDispatcher();
        var sidebar = (StackPanel)Field(window, "_sidebar");
        var firstButton = sidebar.Children[0];
        var tasks = (StackPanel)Field(window, "_tasks");
        var row = Descendants<Border>(tasks).Single(b => Equals(b.Tag, ("inbox", "a")));
        var navigate = typeof(PlannerWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        navigate.Invoke(window, ["today", null]);
        Assert(ReferenceEquals(firstButton, sidebar.Children[0]), "navigation preserves sidebar controls");
        Assert(ReferenceEquals(row, Descendants<Border>(tasks).Single(b => Equals(b.Tag, ("inbox", "a")))),
            "unchanged source row reused across navigation");
        Assert(!tasks.HasAnimatedProperties, "navigation does not animate full task panel");
        navigate.Invoke(window, ["tomorrow", null]); navigate.Invoke(window, ["inbox", null]);
        Assert(window.View == "inbox", "rapid navigation keeps latest selection");
        state.Papers[0].Items[0].Text = "updated source"; window.QueueRefresh();
        navigate.Invoke(window, ["today", null]);
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1080, 700)); root.Arrange(new Rect(0, 0, 1080, 700)); root.UpdateLayout();
        Assert(Descendants<TextBlock>(tasks).Any(t => t.Text == "updated source"), "pending source update is not hidden by row reuse");
        Assert(!ReferenceEquals(row, Descendants<Border>(tasks).Single(b => Equals(b.Tag, ("inbox", "a")))),
            "pending source updates invalidate reused rows");
        DrainDispatcher();
        var unchanged = Descendants<Border>(tasks).Single(b => Equals(b.Tag, ("inbox", "b")));
        var sidebarButton = sidebar.Children[0];
        state.Papers[0].Items[0].Text = "updated again"; window.QueueRefresh(); DrainDispatcher();
        Assert(ReferenceEquals(unchanged, Descendants<Border>(tasks).Single(b => Equals(b.Tag, ("inbox", "b")))),
            "source refresh preserves unaffected task controls");
        Assert(ReferenceEquals(sidebarButton, sidebar.Children[0]), "source refresh updates counts without recreating sidebar");
        var search = (TextBox)Field(window, "_search"); search.Text = "updated again";
        window.QueueRefresh(sourceChanged: false); DrainDispatcher();
        Assert(ReferenceEquals(sidebarButton, sidebar.Children[0]), "search preserves sidebar identity");
        Assert(!Descendants<Border>(tasks).Any(b => Equals(b.Tag, ("inbox", "b"))), "search filters current source text");
        window.Close(); StopTimers(controller);
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void CheckQueriesAndIdentity()
    {
        var state = Fixture(); var today = new DateOnly(2026, 10, 2);
        Assert(TaskPlanningRules.All(state).Count() == 3, "blank placeholders excluded");
        Assert(TaskPlanningRules.Query(state, "today", null, today).Count() == 2, "today includes overdue");
        Assert(TaskPlanningRules.Query(state, "completed", null, today).Count() == 1, "completed filter");
        Assert(TaskPlanningRules.Query(state, "tomorrow", null, today).Count() == 0, "tomorrow excludes today");
        var task = state.Papers[0].Items[0]; var version = TaskPlanningRules.Version(task);
        task.Planning = task.Planning! with { ListId = "work", Tags = "写作" };
        Assert(TaskPlanningRules.Query(state, "inbox", null, today).Count() == 1, "classified task leaves inbox query");
        Assert(ReferenceEquals(TaskPlanningRules.Query(state, "list", "work", today).Single().Item, task), "same identity, no copied tasks");
        Assert(TaskPlanningRules.Query(state, "all", null, today, "写作").Count() == 1, "tag search");
        Assert(TaskPlanningRules.Query(state, "all", null, today, "工作项目").Count() == 1, "classified list search");
        Assert(TaskPlanningRules.Version(task) != version, "metadata advances token");
        Assert(TodoRules.Clone(task).Planning == task.Planning, "undo clone preserves immutable planning");
        state.Papers.RemoveAt(1);
        Assert(TaskPlanningRules.ResolveList(state, new(state.Papers[0], task)) == state.Papers[0], "deleted classification safely falls back");
        var week = new PaperItem { Text = "seven", Planning = new() { PlannedDate = today.AddDays(6) } };
        state.Papers[0].Items.Add(week);
        Assert(TaskPlanningRules.Query(state, "week", null, today).Any(t => t.Item == week), "seventh day included");
        week.Planning = week.Planning! with { PlannedDate = today.AddDays(7) };
        Assert(!TaskPlanningRules.Query(state, "week", null, today).Any(t => t.Item == week), "eighth day excluded");
        task.Done = true;
        Assert(!TaskPlanningRules.Query(state, "today", null, today).Any(t => t.Item == task), "completion shared across views");
        Assert(TodoRules.HasMeaningfulContent(new() { Planning = new() { DueDate = today } }), "metadata is not a placeholder");
    }

    private static DateTimeOffset At(int hour) => new(2026, 10, 2, hour, 0, 0, TimeSpan.FromHours(8));
    private static ScheduleProposal Block(PaperData paper, PaperItem task, int start, int end) =>
        new(paper.Id, task.Id, TaskPlanningRules.Version(task), At(start), At(end));

    private static void CheckDrafts()
    {
        var state = Fixture(); var paper = state.Papers[0]; var a = paper.Items[0]; var b = paper.Items[1];
        b.Planning = new() { ScheduledStart = At(10), ScheduledEnd = At(11), Locked = true };
        var proposal = Block(paper, a, 9, 10);
        var draft = TaskPlanningRules.CreateDraft(state, [proposal]);
        Assert(draft.Changes.Count == 1 && a.Planning!.ScheduledStart == null, "preview does not mutate");
        Reject(() => TaskPlanningRules.CreateDraft(state, [Block(paper, a, 10, 12)]), "overlap with fixed event");
        Reject(() => TaskPlanningRules.CreateDraft(state, [Block(paper, b, 12, 13)]), "fixed event cannot move");
        Reject(() => TaskPlanningRules.CreateDraft(state, [proposal, proposal]), "duplicates rejected");
        Reject(() => TaskPlanningRules.CreateDraft(state, [Block(paper, a, 9, 9)]), "zero interval rejected");
        a.Text = "changed";
        Reject(() => TaskPlanningRules.CreateDraft(state, [proposal]), "stale text rejected");
        a.Done = true;
        Reject(() => TaskPlanningRules.CreateDraft(state, [Block(paper, a, 9, 10)]), "completed task rejected");
        a.Done = false; a.Planning = new() { DueDate = new DateOnly(2026, 10, 1) };
        Reject(() => TaskPlanningRules.CreateDraft(state, [Block(paper, a, 9, 10)]), "deadline violated");
        a.Planning = new(); b.Planning = new();
        Reject(() => TaskPlanningRules.CreateDraft(state, [Block(paper, a, 9, 11), Block(paper, b, 10, 12)]), "draft overlaps itself");
        Assert(TaskPlanningRules.CreateDraft(state, [Block(paper, a, 9, 10), Block(paper, b, 10, 11)]).Changes.Count == 2,
            "adjacent blocks allowed");
        Reject(() => TaskPlanningRules.Validate(new() { ScheduledStart = At(9) }), "unpaired schedule rejected");
        Reject(() => TaskPlanningRules.Validate(new() { Priority = 4 }), "invalid priority");
        Reject(() => TaskPlanningRules.Validate(new() { DurationMinutes = 0 }), "invalid duration");
    }

    private static void CheckPersistence(string temp)
    {
        var store = new StateStore(Path.Combine(temp, "roundtrip"), DurableAtomicFileWriter.Shared);
        var state = Fixture(); var item = state.Papers[0].Items[0];
        state.PlannerPinnedViews.Add(new("today", null));
        item.Planning = item.Planning! with { ListId = "work", DueDate = new(2026, 10, 5),
            ScheduledStart = At(9), ScheduledEnd = At(10), Tags = "学习", Locked = true };
        store.SaveJsonSync(store.SerializeState(state), 1);
        var loaded = store.Load();
        Assert(loaded.Papers[0].Items[0].Planning == item.Planning, "actual StateStore planning roundtrip");
        Assert(loaded.Papers[1].PlannerFolder == "工作" && loaded.Papers[0].PlannerInbox, "list metadata roundtrip");
        Assert(loaded.PlannerPinnedViews.SequenceEqual(state.PlannerPinnedViews), "pinned queries persisted");
        var legacy = new AppState { Papers = [new() { Items = [new() { Text = "legacy" }] }] };
        store.SaveJsonSync(store.SerializeState(legacy), 2);
        Assert(store.Load().Papers[0].Items[0].Planning == null, "legacy data remains optional");
        var malformed = new TaskPlanningData { Priority = 999, DurationMinutes = -1, Tags = null!, ScheduledStart = At(9) };
        var normalized = TaskPlanningRules.Normalize(malformed);
        TaskPlanningRules.Validate(normalized);
        Assert(normalized.ScheduledStart == null && normalized.Priority == 3 && normalized.DurationMinutes == 5,
            "invalid stored metadata normalized safely");
        legacy.Papers[0].Items[0].Planning = malformed with { Tags = "" };
        File.WriteAllText(store.FilePath, JsonSerializer.Serialize(legacy,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert(store.Load().Papers[0].Items[0].Planning == normalized, "invalid metadata normalized on load, not only save");
    }

    private static AppController Controller(AppState state, StateStore? store = null)
    {
        var controller = (AppController)RuntimeHelpers.GetUninitializedObject(typeof(AppController));
        foreach (var field in typeof(AppController).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() is { } generic &&
                (generic == typeof(Dictionary<,>) || generic == typeof(HashSet<>) || generic == typeof(List<>)))
                field.SetValue(controller, Activator.CreateInstance(field.FieldType));
            if (field.FieldType == typeof(DispatcherTimer))
                field.SetValue(controller, new DispatcherTimer { Interval = TimeSpan.FromDays(1) });
        }
        Set(controller, "<State>k__BackingField", state);
        Set(controller, "_ignoreSaveFailures", true);
        if (store != null) Set(controller, "_store", store);
        return controller;
    }

    private static void CheckTransactions(string temp)
    {
        var state = Fixture(); var a = state.Papers[0].Items[0];
        var b = new PaperItem { Text = "other paper" }; state.Papers[1].Items.Add(b);
        var controller = Controller(state, new StateStore(Path.Combine(temp, "transactions"), DurableAtomicFileWriter.Shared));
        var commands = new PaperCommandService(controller);
        var original = a.Planning;
        Reject(() => commands.SetTaskPlanning("inbox", a.Id, TaskPlanningRules.Version(a), new() {
            DueDate = new(2026, 10, 1), ScheduledStart = At(9), ScheduledEnd = At(10) }, PaperOperationContext.User()),
            "manual scheduling enforces the same deadline as AI");
        Assert(a.Planning == original, "deadline rejection leaves task unchanged");
        var scheduled = new TaskPlanningData { ScheduledStart = At(9), ScheduledEnd = At(10) };
        a.Planning = scheduled;
        Reject(() => commands.SetTaskPlanning("inbox", a.Id, TaskPlanningRules.Version(a),
            scheduled with { DueDate = new(2026, 10, 1) }, PaperOperationContext.User()),
            "deadline edits cannot invalidate an existing schedule");
        a.Planning = original;
        var draft = commands.PreviewPlannerSchedule([Block(state.Papers[0], a, 9, 10), Block(state.Papers[1], b, 10, 11)]);
        commands.ApplyPlannerSchedule(draft);
        Assert(a.Planning!.ScheduledStart == At(9) && b.Planning!.ScheduledStart == At(10), "multi-paper apply");
        commands.UndoPlannerSchedule();
        Assert(a.Planning!.ScheduledStart == null && b.Planning == null, "multi-paper undo restores null metadata");
        commands.ApplyPlannerSchedule(commands.PreviewPlannerSchedule([Block(state.Papers[0], a, 9, 10)]));
        a.Planning = a.Planning! with { Tags = "edited after apply" };
        Reject(() => commands.UndoPlannerSchedule(), "undo refuses to overwrite newer edit");
        var beforeA = a.Planning; var beforeB = b.Planning;
        Set(controller, "_store", new StateStore(Path.Combine(temp, "failed"), new FailingWriter()));
        Reject(() => commands.ApplyPlannerSchedule(commands.PreviewPlannerSchedule(
            [Block(state.Papers[0], a, 12, 13), Block(state.Papers[1], b, 13, 14)])), "disk failure rejects apply");
        Assert(a.Planning == beforeA && b.Planning == beforeB, "all papers rolled back on disk failure");
        Reject(() => commands.SetTaskPlanning(state.Papers[0].Id, a.Id, "stale", new(), PaperOperationContext.User()), "detail stale check");
        StopTimers(controller);
    }

    private static void CheckSelectiveSchedule(string temp, string[] args)
    {
        var state = Fixture(); var a = state.Papers[0].Items[0]; var b = state.Papers[0].Items[1];
        b.Planning = new();
        var controller = Controller(state, new StateStore(Path.Combine(temp, "selective"), DurableAtomicFileWriter.Shared));
        var commands = controller.PaperCommands;
        var draft = commands.PreviewPlannerSchedule([Block(state.Papers[0], a, 9, 10), Block(state.Papers[0], b, 10, 11)]);
        Reject(() => commands.ApplyPlannerSchedule(draft, []), "empty schedule selection rejected");
        Reject(() => commands.ApplyPlannerSchedule(draft, [("inbox", "missing")]), "foreign selection rejected");
        commands.ApplyPlannerSchedule(draft, [("inbox", "a")]);
        Assert(a.Planning!.ScheduledStart == At(9) && b.Planning!.ScheduledStart == null, "only selected proposals applied");
        commands.UndoPlannerSchedule();
        Assert(a.Planning!.ScheduledStart == null, "partial application remains undoable");
        a.Planning = new() { ScheduledStart = At(9), ScheduledEnd = At(10) };
        b.Planning = new() { ScheduledStart = At(10), ScheduledEnd = At(11) };
        var swap = commands.PreviewPlannerSchedule([Block(state.Papers[0], a, 10, 11), Block(state.Papers[0], b, 9, 10)]);
        Reject(() => commands.ApplyPlannerSchedule(swap, [("inbox", "a")]), "partial swap rechecks conflict against unselected tasks");
        a.Planning = new(); b.Planning = new();
        var preview = commands.PreviewPlannerSchedule([Block(state.Papers[0], a, 9, 10), Block(state.Papers[0], b, 10, 11)]);
        var window = new PlannerWindow(controller);
        var many = Enumerable.Range(0, 100).Select(i => preview.Changes[0] with {
            Task = new(state.Papers[0], new() { Id = "preview-" + i,
                Text = i == 0 ? string.Concat(Enumerable.Repeat("较长的任务标题需要完整显示", 8)) : "建议任务 " + i }) }).ToArray();
        var longRoot = window.BuildSchedulePreview(new(many), () => { });
        longRoot.Resources.MergedDictionaries.Add(window.Resources);
        longRoot.Measure(new Size(520, 360)); longRoot.Arrange(new Rect(0, 0, 520, 360)); longRoot.UpdateLayout();
        var longApply = Descendants<Button>(longRoot).Single(button => Equals(button.Content, Strings.Get("PlannerApplySchedule")));
        Assert(longApply.TransformToAncestor(longRoot).Transform(new Point(0, longApply.ActualHeight)).Y <= longRoot.ActualHeight,
            "100-proposal preview keeps footer visible without scrolling to the end");
        var wrappedTitle = Descendants<TextBlock>(longRoot).Single(t => t.Text == many[0].Task.Item.Text);
        Assert(wrappedTitle.ActualWidth < 480 && wrappedTitle.ActualHeight > 30, "long proposal title wraps within preview width");
        if (args.Length > 0) Snapshot(longRoot, 520, 360, Path.ChangeExtension(args[0], ".schedule.png"));
        var closed = false;
        var root = window.BuildSchedulePreview(preview, () => closed = true);
        root.Measure(new Size(520, 360)); root.Arrange(new Rect(0, 0, 520, 360)); root.UpdateLayout();
        var apply = Descendants<Button>(root).Single(button => Equals(button.Content, Strings.Get("PlannerApplySchedule")));
        Assert(apply.TransformToAncestor(root).Transform(new Point(0, apply.ActualHeight)).Y <= root.ActualHeight,
            "schedule apply footer remains visible in constrained layout");
        var checks = Descendants<CheckBox>(root).ToArray();
        foreach (var check in checks) check.IsChecked = false;
        Assert(!apply.IsEnabled, "no selected proposals disables application");
        checks[0].IsChecked = true;
        Set(controller, "_store", new StateStore(Path.Combine(temp, "selective-failure"), new FailingWriter()));
        apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(!closed && a.Planning!.ScheduledStart == null, "preview save failure preserves dialog and source");
        Set(controller, "_store", new StateStore(Path.Combine(temp, "selective-success"), DurableAtomicFileWriter.Shared));
        apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(closed && a.Planning!.ScheduledStart == At(9) && b.Planning!.ScheduledStart == null,
            "preview applies only the checked proposal after successful commit");
        window.Close(); StopTimers(controller);
    }

    private static void CheckPinnedLayout(string temp)
    {
        var state = Fixture(); state.PlannerPinnedViews.Add(new("today", null));
        var store = new StateStore(Path.Combine(temp, "pinned-layout"), DurableAtomicFileWriter.Shared);
        var controller = Controller(state, store);
        var saved = new PlannerPinnedLayout(-1900, 25, 460, 620, "missing-monitor", "compact");
        controller.RememberPlannerPinnedLayout("today", null, saved);
        controller.SaveNow(sync: true);
        Assert(store.Load().PlannerPinnedViews.Single().Layout == saved, "pinned geometry and density survive StateStore roundtrip");
        Assert(PlannerPinnedLayout.Normalize(new(0, 0, double.NaN, 500, null)) == null, "invalid geometry rejected");
        Assert(PlannerPinnedLayout.Normalize(saved with { Density = "invalid" })!.Density == "auto", "invalid density falls back safely");
        var area = new Rect(0, 0, 1280, 720);
        var restored = PlannerPinnedPlacement.Constrain(saved, area);
        Assert(area.Contains(restored) && restored.Width == 460 && restored.Height == 620,
            "disconnected monitor is rescued into available work area");
        Assert(new Rect(-1920, 0, 1920, 1080).Contains(PlannerPinnedPlacement.Constrain(saved, new(-1920, 0, 1920, 1080))),
            "negative-coordinate monitor remains supported");
        var tiny = new Rect(0, 0, 300, 250);
        Assert(tiny.Contains(PlannerPinnedPlacement.Constrain(saved, tiny)), "small work area does not push pinned window off-screen");
        state.PlannerPinnedViews.Add(new("today", null) { Layout = saved with { Left = 100 } });
        store.SaveJsonSync(store.SerializeState(state), 100);
        Assert(store.Load().PlannerPinnedViews.Count == 1, "pinned identity deduplicates independently of geometry");
        var window = new PlannerWindow(controller, "today", pinned: true) { ShowActivated = false, ShowInTaskbar = false };
        window.RestorePinnedLayout(saved); window.Show(); DrainDispatcher();
        var actual = new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
        Assert(WindowWorkAreaHelper.WorkAreaFor(window).Contains(actual), "real pinned window restored into connected work area");
        Assert(window.Width == 460 && (string)Field(window, "_pinDensity") == "compact", "real window restores width and density");
        window.Close();
        StopTimers(controller);
    }

    private static void CheckVirtualLists(string temp, string[] args)
    {
        var state = Fixture(); state.EnableAnimations = false; state.Papers[0].Items.Clear();
        var today = DateOnly.FromDateTime(DateTime.Now);
        for (var i = 0; i < 600; i++) state.Papers[0].Items.Add(new() {
            Id = "large-" + i, Text = "大清单任务 " + i, Order = i,
            Planning = new() { PlannedDate = i == 0 ? today.AddDays(1) : today } });
        var controller = Controller(state, new StateStore(Path.Combine(temp, "virtual-list"), DurableAtomicFileWriter.Shared));
        var window = new PlannerWindow(controller, "all") { Width = 1080, Height = 700, ShowActivated = false, ShowInTaskbar = false };
        window.Show(); DrainDispatcher();
        var tasks = (StackPanel)Field(window, "_tasks");
        Border[] Rows() => Descendants<Border>(tasks).Where(b => b.Tag is ValueTuple<string, string>).ToArray();
        Assert(Rows().Length is > 0 and < 80, "large list realizes only visible task controls");
        var list = (ListBox)Field(window, "_virtualTaskList");
        var scroll = Descendants<ScrollViewer>(list).First();
        scroll.ScrollToEnd(); DrainDispatcher();
        Assert(Rows().Any(b => Equals(b.Tag, ("inbox", "large-599"))), "scrolling realizes tasks at the end of a large list");
        var cache = (System.Collections.IDictionary)Field(window, "_rowStamps");
        Assert(cache.Count < 100, "recycled task rows do not accumulate retained controls");
        var navigate = typeof(PlannerWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        navigate.Invoke(window, ["tomorrow", null]); DrainDispatcher();
        Assert(Rows().Length == 1 && Rows().Single().Tag.Equals(("inbox", "large-0")), "large-to-small transition preserves source identity");
        navigate.Invoke(window, ["all", null]); DrainDispatcher();
        list = (ListBox)Field(window, "_virtualTaskList"); scroll = Descendants<ScrollViewer>(list).First();
        Assert(scroll.VerticalOffset > 0, "virtualized view restores its own scroll position");
        var row = Rows().First(); var key = ((string, string))row.Tag;
        var check = Descendants<CheckBox>(row).Single(); check.IsChecked = true;
        check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); DrainDispatcher();
        Assert(state.Papers[0].Items.Single(i => i.Id == key.Item2).Done, "completion from a recycled row changes the correct source task");
        Assert(!Rows().Any(b => Equals(b.Tag, key)), "completed task leaves the active virtualized query");
        navigate.Invoke(window, ["week", null]); DrainDispatcher();
        var columns = Descendants<Grid>(tasks).Single(g => g.ColumnDefinitions.Count == 7);
        Assert(Descendants<ListBox>(columns).Count() == 7 && Rows().Length < 100, "large seven-day view virtualizes each day column");
        for (var i = 1; i <= 39; i++) state.Papers[0].Items[i].Planning = new() { DueDate = today.AddDays(-1) };
        window.QueueRefresh(); DrainDispatcher();
        columns = Descendants<Grid>(tasks).Single(g => g.ColumnDefinitions.Count == 7);
        var dayLists = Descendants<ListBox>(columns).ToArray();
        Assert(dayLists.All(l => l.ActualHeight > 0) && Rows().Length < 100,
            "overdue section cannot consume the height of virtualized day columns");
        if (args.Length > 0) Snapshot((FrameworkElement)window.Content, 1080, 700, Path.ChangeExtension(args[0], ".virtual.png"));
        window.Close(); StopTimers(controller);
    }

    private static void CheckWindow(string[] args)
    {
        var controller = Controller(Fixture());
        var window = new PlannerWindow(controller, "all");
        window.SelectTask(TaskPlanningRules.All(controller.State).First());
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1080, 700)); root.Arrange(new Rect(0, 0, 1080, 700)); root.UpdateLayout();
        Assert(root.ActualWidth == 1048 && root.ActualHeight == 668, "real WPF wide layout");
        root.Measure(new Size(700, 650)); root.Arrange(new Rect(0, 0, 700, 650)); root.UpdateLayout();
        Assert(root.ActualWidth == 668, "narrow WPF layout");
        var detail = (Border)Field(window, "_detailHost");
        Assert(Grid.GetColumn(detail) == 1 && detail.ActualWidth <= 310, "narrow details overlay does not squeeze list");
        var save = Descendants<Button>(detail).Single(b => Equals(b.Content, Strings.Get("PlannerSave")));
        var footer = save.TransformToAncestor(root).Transform(new Point(0, save.ActualHeight));
        Assert(footer.Y <= root.ActualHeight, "save footer stays visible in narrow window");
        var searchHost = (FrameworkElement)Field(window, "_searchHost");
        Assert(searchHost.Visibility == Visibility.Collapsed, "search is on demand, not permanent form chrome");
        var tags = (TextBox)Field(window, "_tags"); tags.Text = "unsaved local edit";
        var currentProperty = typeof(AppController).GetProperty("Current")!;
        var previous = currentProperty.GetValue(null);
        currentProperty.SetValue(null, controller);
        try
        {
            controller.State.Theme = "dark"; Theme.Invalidate(); window.RefreshAppearance();
            root.UpdateLayout();
            Assert(ReferenceEquals(tags, Field(window, "_tags")) && tags.Text == "unsaved local edit",
                "theme refresh keeps dirty editor identity and value");
            Assert(((SolidColorBrush)tags.Foreground).Color == Color.FromRgb(229, 238, 239), "dirty editor dark text updates dynamically");
            Assert(((TextBlock)Field(window, "_dirtyLabel")).Text == Strings.Get("PlannerUnsaved"), "unsaved status remains visible");
            if (args.Length > 0) Snapshot(root, 700, 650, Path.ChangeExtension(args[0], ".dark.png"));
            controller.State.Theme = "light"; Theme.Invalidate(); window.RefreshAppearance();
            Assert(tags.Text == "unsaved local edit", "light theme preserves dirty value too");
        }
        finally { currentProperty.SetValue(null, previous); Theme.Invalidate(); }
        tags.Text = "";
        typeof(PlannerWindow).GetField("_detailsDirty", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
        ((TextBlock)Field(window, "_dirtyLabel")).Text = Strings.Get("PlannerSaved");
        if (args.Length > 0)
        {
            root.Measure(new Size(1080, 700)); root.Arrange(new Rect(0, 0, 1080, 700)); root.UpdateLayout();
            Snapshot(root, 1080, 700, args[0]);
        }
        window.Close();
        var pin = new PlannerWindow(controller, "today", pinned: true);
        var pinRoot = (FrameworkElement)pin.Content;
        pinRoot.Measure(new Size(360, 480)); pinRoot.Arrange(new Rect(0, 0, 360, 480)); pinRoot.UpdateLayout();
        Assert(pinRoot.ActualWidth == 328 && pin.Topmost, "compact pinned surface");
        Assert(((FrameworkElement)Field(pin, "_addHost")).Visibility == Visibility.Collapsed,
            "pin starts as note without permanent input form");
        if (args.Length > 0) Snapshot(pinRoot, 360, 480, Path.ChangeExtension(args[0], ".pin.png"));
        pin.Close(); StopTimers(controller);
        var week = new PlannerWindow(controller, "week");
        var weekRoot = (FrameworkElement)week.Content;
        weekRoot.Measure(new Size(1100, 700)); weekRoot.Arrange(new Rect(0, 0, 1100, 700)); weekRoot.UpdateLayout();
        typeof(PlannerWindow).GetMethod("RefreshTasks", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(week, null);
        weekRoot.UpdateLayout();
        var weekScroll = (ScrollViewer)Field(week, "_taskScroll");
        Assert(weekScroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto, "wide week allows horizontal columns");
        Assert(Descendants<Grid>((DependencyObject)weekScroll.Content).Any(g => g.ColumnDefinitions.Count == 7), "real seven-day grid rendered");
        if (args.Length > 0) Snapshot(weekRoot, 1100, 700, Path.ChangeExtension(args[0], ".week.png"));
        weekRoot.Measure(new Size(740, 650)); weekRoot.Arrange(new Rect(0, 0, 740, 650)); weekRoot.UpdateLayout();
        typeof(PlannerWindow).GetMethod("RefreshTasks", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(week, null);
        Assert(weekScroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled, "narrow week uses vertical day groups");
        weekRoot.Measure(new Size(1800, 700)); weekRoot.Arrange(new Rect(0, 0, 1800, 700)); weekRoot.UpdateLayout();
        typeof(PlannerWindow).GetMethod("RefreshTasks", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(week, null);
        weekRoot.UpdateLayout();
        var columns = Descendants<Grid>((DependencyObject)weekScroll.Content).Single(g => g.ColumnDefinitions.Count == 7);
        Assert(columns.ColumnDefinitions.Sum(c => c.ActualWidth) <= weekScroll.ViewportWidth + 1,
            "large window shows all seven days without mandatory scrolling");
        week.Close();
        Assert(StartupCommand.Parse(["--planner"]).Kind == StartupCommandKind.Planner, "planner startup command");
    }

    private static void CheckFeedback(string temp)
    {
        var controller = Controller(Fixture(), new StateStore(Path.Combine(temp, "feedback"), DurableAtomicFileWriter.Shared));
        var window = new PlannerWindow(controller, "all");
        void Refresh()
        {
            typeof(PlannerWindow).GetMethod("RefreshTasks", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            var root = (FrameworkElement)window.Content; root.Measure(new Size(1080, 700));
            root.Arrange(new Rect(0, 0, 1080, 700)); root.UpdateLayout();
        }
        CheckBox Toggle(string id)
        {
            var row = Descendants<Border>((DependencyObject)window.Content).Single(b => b.Tag is ValueTuple<string, string> key && key.Item2 == id);
            var check = Descendants<CheckBox>(row).Single(); check.IsChecked = true;
            check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); return check;
        }
        Refresh(); Toggle("a"); Refresh(); Toggle("b");
        Assert(controller.State.Papers[0].Items.Take(2).All(t => t.Done), "rapid completion commits both tasks immediately");
        var thirdId = controller.PaperCommands.AddPlannerTask("inbox", "新增任务不会等待完成动画", null); Refresh();
        Assert(Descendants<Border>((DependencyObject)window.Content).Any(b => b.Tag is ValueTuple<string, string> key && key.Item2 == thirdId),
            "new tasks render while other rows are completing");
        controller.State.EnableAnimations = false; window.RefreshAppearance(); Refresh();
        Assert(((System.Collections.IDictionary)Field(window, "_completing")).Count == 0, "disabling motion settles presentation immediately");
        Assert(!Descendants<Border>((DependencyObject)window.Content).Any(b => b.Tag is ValueTuple<string, string> key && key.Item2 is "a" or "b"),
            "completed rows leave active list after presentation settles");
        Set(controller, "_store", new StateStore(Path.Combine(temp, "feedback-fail"), new FailingWriter()));
        var failed = Toggle(thirdId);
        Assert(!controller.State.Papers[0].Items.Single(t => t.Id == thirdId).Done && failed.IsChecked == false,
            "failed completion restores both task and checkbox");
        window.Close(); StopTimers(controller);
    }

    private static void CheckActivityMarks()
    {
        var images = new HashSet<string>();
        foreach (var kind in new[] { "idle", "thinking", "tool", "answering", "waiting", "unknown" })
        {
            var ring = new CodexInkRing { ActivityKind = kind, Value = .72, ShowQuotaRing = true,
                ForegroundBrush = Brushes.Black, TrackBrush = Brushes.LightGray, MotionAllowed = () => false };
            ring.Measure(new Size(24, 24)); ring.Arrange(new Rect(0, 0, 24, 24));
            var bitmap = new RenderTargetBitmap(24, 24, 96, 96, PixelFormats.Pbgra32); bitmap.Render(ring);
            var pixels = new byte[24 * 24 * 4]; bitmap.CopyPixels(pixels, 24 * 4, 0);
            images.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels)));
            Assert(ring.Value == .72 && ring.ActualWidth == 24, "state does not alter quota or ring bounds: " + kind);
        }
        Assert(images.Count == 6, "six activity marks remain distinguishable with motion disabled");
        var mark = new PlannerCompletionMark { MotionEnabled = false, IsChecked = true };
        Assert(mark.Progress == 1, "reduced-motion completion shows final ink mark immediately");
        mark.IsChecked = false; Assert(mark.Progress == 0, "uncheck removes the mark immediately");
    }

    private static object Field(object target, string name) => target.GetType().GetField(name,
        BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Snapshot(FrameworkElement root, int width, int height, string path)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }

    private static void CheckMcp(string temp)
    {
        var state = Fixture();
        var controller = Controller(state, new StateStore(Path.Combine(temp, "mcp"), DurableAtomicFileWriter.Shared));
        var adapter = new McpCommandService(controller, new PaperCommandService(controller));
        JsonElement Request(string method, object parameters) => JsonSerializer.SerializeToElement(new
            { method, @params = parameters });
        Reject(() => adapter.Execute(Request("list_tasks", new { })), "disabled MCP remains disabled");
        state.McpEnabled = true;
        var result = JsonSerializer.SerializeToElement(adapter.Execute(Request("list_tasks", new { })));
        Assert(result.GetProperty("tasks").GetArrayLength() == 3, "MCP planning read excludes placeholders");
        Assert(result.GetProperty("tasks")[0].GetProperty("version").GetString()!.Length == 64, "MCP exposes concurrency token");
        Reject(() => adapter.Execute(Request("propose_schedule", new { blocks = Array.Empty<object>() })), "proposal respects write policy");
        state.McpAllowFullWrites = true;
        try { adapter.Execute(Request("propose_schedule", new { blocks = Array.Empty<object>() })); throw new InvalidOperationException("expected dedicated permission"); }
        catch (McpApiException ex) { Assert(ex.Code == "schedule_proposals_disabled", "full writes cannot implicitly enable schedule proposals"); }
        state.McpAllowFullWrites = false; state.McpAllowScheduleProposals = true;
        try { adapter.Execute(Request("propose_schedule", new { blocks = Array.Empty<object>() })); throw new InvalidOperationException("expected invalid blocks"); }
        catch (McpApiException ex) { Assert(ex.Code == "invalid_params", "schedule proposals do not require full writes"); }
        try { adapter.Execute(Request("update_todo", new { paper_id = "inbox", todo_id = "a", text = "unauthorized" })); throw new InvalidOperationException("expected edit permission"); }
        catch (McpApiException ex) { Assert(ex.Code == "full_writes_disabled", "proposal permission does not grant task editing"); }
        Assert(state.Papers[0].Items[0].Text == "研究报告", "narrow permission leaves existing task text unchanged");
        Reject(() => adapter.Execute(Request("propose_schedule", new { blocks = Array.Empty<object>() })), "empty proposal rejected");
        Reject(() => adapter.Execute(Request("propose_schedule", new { blocks = new[] { new
        {
            paper_id = "inbox", todo_id = "a", expected_version = TaskPlanningRules.Version(state.Papers[0].Items[0]),
            start = "2026-10-02T09:00:00", end = "2026-10-02T10:00:00+08:00"
        } } })), "timezone-free proposal rejected");
        Assert(state.Papers[0].Items[0].Planning!.ScheduledStart == null, "rejected MCP proposal never mutates");
        var accepted = JsonSerializer.SerializeToElement(adapter.Execute(Request("propose_schedule", new { blocks = new[] { new {
            paper_id = "inbox", todo_id = "a", expected_version = TaskPlanningRules.Version(state.Papers[0].Items[0]),
            start = At(9).ToString("O"), end = At(10).ToString("O") } } })));
        Assert(accepted.GetProperty("status").GetString() == "pending_local_confirmation" &&
            state.Papers[0].Items[0].Planning!.ScheduledStart == null,
            "narrow permission accepts a valid proposal without modifying source data");
        // The isolated test controller is exiting: do not open its queued modal preview during later tests.
        var lifecycle = typeof(AppController).GetField("_lifecycleState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        lifecycle.SetValue(controller, Enum.Parse(lifecycle.FieldType, "Exiting"));
        StopTimers(controller);
    }

    private static void Set(object target, string name, object value) =>
        typeof(AppController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void CheckDailyUse(string temp)
    {
        var state = Fixture();
        var store = new StateStore(Path.Combine(temp, "daily-use"), DurableAtomicFileWriter.Shared);
        var controller = Controller(state, store);
        var window = new PlannerWindow(controller, "all");
        ((HashSet<PlannerWindow>)Field(controller, "_plannerWindows")).Add(window);
        var saveWarnings = 0;
        bool PrepareExit(Func<MessageBoxResult>? choose = null) =>
            controller.TryPrepareNormalExit(choose, () => saveWarnings++);
        var task = TaskPlanningRules.All(state).First();
        window.SelectTask(task);
        ((TextBox)Field(window, "_taskTitle")).Text = "修改后的任务";
        ((TextBox)Field(window, "_tags")).Text = "重要";
        Assert(!PrepareExit(() => MessageBoxResult.Cancel), "normal exit cancels on unsaved details");
        Assert(controller.IsRunning && task.Item.Text == "研究报告", "cancel preserves lifecycle and does not commit fields");
        Assert(window.TryLeaveDetails(() => MessageBoxResult.Yes), "save-and-continue saves valid details");
        Assert(task.Item.Text == "修改后的任务" && task.Planning.Tags == "重要", "title and planning commit together");
        Assert(store.Load().Papers[0].Items[0].Text == task.Item.Text, "detail rename is durable");
        ((TextBox)Field(window, "_duration")).Text = "invalid";
        Assert(!window.TryLeaveDetails(() => MessageBoxResult.Yes), "invalid input prevents leaving");
        Assert(((TextBox)Field(window, "_duration")).Text == "invalid", "invalid input remains available for correction");
        ((TextBox)Field(window, "_duration")).Text = "45";
        Set(controller, "_store", new StateStore(Path.Combine(temp, "daily-use-failed"), new FailingWriter()));
        ((TextBox)Field(window, "_taskTitle")).Text = "不能写入的名称";
        Assert(!window.TryLeaveDetails(() => MessageBoxResult.Yes), "save failure keeps editor open");
        Assert(state.Papers[0].Items[0].Text == "修改后的任务" && state.Papers[0].Items[0].Planning!.DurationMinutes == 30,
            "save failure rolls back title and metadata together");
        Assert(((TextBox)Field(window, "_taskTitle")).Text == "不能写入的名称", "failed rename remains in editor");
        Assert(window.TryLeaveDetails(() => MessageBoxResult.No), "discard explicitly permits leaving");
        Assert(!PrepareExit(() => MessageBoxResult.No) && controller.IsRunning,
            "failed final save leaves runtime running");
        Assert(saveWarnings == 1, "ignoring autosave warnings does not hide failed exit warning");
        Assert((bool)Field(controller, "_hasPendingDirty") && ((DispatcherTimer)Field(controller, "_forceSaveTimer")).IsEnabled,
            "failed final save retains dirty state and retry timer");
        Set(controller, "_store", store);
        Assert(window.TryLeaveDetails(() => MessageBoxResult.Yes), "save retries successfully after storage recovery");
        Assert(state.Papers[0].Items[0].Text == "不能写入的名称", "retry resolves current source identity");
        state.Papers[0].Items[0].Text = "原便签中较新的名称";
        ((TextBox)Field(window, "_taskTitle")).Text = "过时的表单";
        Assert(!window.TryLeaveDetails(() => MessageBoxResult.Yes), "stale edit cannot overwrite original paper");
        Assert(state.Papers[0].Items[0].Text == "原便签中较新的名称", "newer source title is preserved");
        typeof(PlannerWindow).GetField("_detailsDirty", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
        window.Close();
        ((HashSet<PlannerWindow>)Field(controller, "_plannerWindows")).Clear();
        Assert(PrepareExit() && controller.IsRunning, "successful preparation saves before lifecycle transition");
        var primary = Path.Combine(temp, "daily-use", "data.json");
        var beforeLockedSave = File.ReadAllText(primary);
        state.Papers[0].Items[0].Text = "文件占用后仍保留的修改";
        using (var heldFile = new FileStream(primary, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert(!PrepareExit() && controller.IsRunning,
                "real file sharing failure cancels exit and keeps runtime alive");
        Assert(File.ReadAllText(primary) == beforeLockedSave, "sharing failure leaves last durable generation intact");
        Assert(PrepareExit() && store.Load().Papers[0].Items[0].Text == "文件占用后仍保留的修改",
            "release of real file lock permits durable retry");
        var durableBeforeFaults = File.ReadAllText(primary);
        foreach (var code in new[] { unchecked((int)0x80070070), unchecked((int)0x80070005) })
        {
            Set(controller, "_store", new StateStore(Path.Combine(temp, "daily-use"), new StorageFaultWriter(code)));
            Assert(!PrepareExit() && controller.IsRunning, "disk-full/access-denied error cancels normal exit");
            Assert(File.ReadAllText(primary) == durableBeforeFaults, "storage error preserves durable generation");
        }
        Set(controller, "_store", store);
        var attributes = File.GetAttributes(primary);
        try
        {
            File.SetAttributes(primary, attributes | FileAttributes.ReadOnly);
            Assert(!PrepareExit() && controller.IsRunning, "actual read-only primary cancels normal exit");
            Assert(File.ReadAllText(primary) == durableBeforeFaults, "read-only failure preserves existing data");
        }
        finally { File.SetAttributes(primary, attributes); }
        Assert(PrepareExit(), "clearing fixture read-only attribute permits retry");

        var agenda = new PlannerWindow(controller, "agenda");
        ((TextBox)Field(agenda, "_search")).Text = "无法匹配";
        ((TextBox)Field(agenda, "_quickAdd")).Text = "日程页面快速添加";
        typeof(PlannerWindow).GetMethod("AddTask", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(agenda, null);
        var added = TaskPlanningRules.All(state).Single(t => t.Item.Text == "日程页面快速添加");
        Assert(added.Planning.ScheduledStart == null, "agenda quick-add does not invent a schedule");
        Assert(((Button)Field(agenda, "_showAddedButton")).Visibility == Visibility.Visible, "hidden added task has explicit reveal action");
        agenda.ShowAddedTask(); DrainDispatcher();
        Assert(agenda.View == "list" && agenda.PaperId == added.Paper.Id &&
            ((TextBox)Field(agenda, "_search")).Text == "", "reveal opens actual destination and clears hiding search");
        Assert(((PlannerTask)Field(agenda, "_selected")).Key == added.Key, "reveal selects exact original task");
        ((TextBox)Field(agenda, "_quickAdd")).Text = "未提交输入";
        Assert(!agenda.TryPrepareExit(() => MessageBoxResult.Cancel), "unsubmitted quick-add blocks closing on cancel");
        Assert(agenda.TryPrepareExit(() => MessageBoxResult.Yes) && TaskPlanningRules.All(state).Any(t => t.Item.Text == "未提交输入"),
            "quick-add can be saved at exit");
        agenda.Close();
        var completed = new PlannerWindow(controller, "completed");
        Assert(((FrameworkElement)Field(completed, "_addHost")).Visibility == Visibility.Collapsed,
            "completed view does not offer misleading add input");
        completed.Close(); StopTimers(controller);
    }

    private static void CheckReminderCatchUp(string temp)
    {
        var now = DateTimeOffset.Now;
        var state = Fixture(); state.ExperimentalTodoReminders = true;
        state.ExperimentalTodoReminderSoundEnabled = false;
        var items = state.Papers[0].Items;
        items.Clear();
        items.Add(new() { Id = "due-a", Text = "休眠时到期", ReminderAt = now.AddHours(-1) });
        items.Add(new() { Id = "due-b", Text = "另一条到期", ReminderAt = now.AddMinutes(-1) });
        items.Add(new() { Id = "future", Text = "尚未到期", ReminderAt = now.AddHours(1) });
        items.Add(new() { Id = "done", Text = "已完成", Done = true, ReminderAt = now.AddHours(-1) });
        var store = new StateStore(Path.Combine(temp, "reminders"), DurableAtomicFileWriter.Shared);
        var controller = Controller(state, store);
        var shown = 0;
        controller.ProcessDueTodoReminders(now, (_, _) => false, _ => false);
        Assert(items.All(t => !t.ReminderTriggered), "unavailable delivery does not consume overdue reminders");
        Assert(((DispatcherTimer)Field(controller, "_todoReminderTimer")).Interval == TimeSpan.FromSeconds(30),
            "unavailable reminder delivery is retried without busy looping");
        controller.ProcessDueTodoReminders(now, (_, _) => { shown++; return true; }, _ => false);
        Assert(items[0].ReminderTriggered && !items[1].ReminderTriggered,
            "target-only delivery consumes only the shown item, not the entire overdue batch");
        controller.ProcessDueTodoReminders(now, (_, _) => { shown++; return true; }, _ => false);
        controller.ProcessDueTodoReminders(now, (_, _) => { shown++; return true; }, _ => true);
        Assert(shown == 2 && items[1].ReminderTriggered, "repeat catch-up does not redeliver consumed reminders");
        Assert(!items[2].ReminderTriggered && !items[3].ReminderTriggered, "future and completed reminders stay excluded");
        Assert(controller.TryPrepareNormalExit(), "delivered reminder state saves successfully");
        var restarted = Controller(store.Load(), store);
        restarted.ProcessDueTodoReminders(now, (_, _) => { shown++; return true; }, _ => true);
        Assert(shown == 2, "restart from durable state does not repeat delivered reminders");
        items[0].ReminderTriggered = false; items[1].ReminderTriggered = false;
        controller.ProcessDueTodoReminders(now, (_, _) => throw new InvalidOperationException("target failure"), _ => true);
        Assert(items[0].ReminderTriggered && items[1].ReminderTriggered, "tray batch succeeds even when target window fails");
        items[0].ReminderTriggered = false; state.ExperimentalTodoReminders = false;
        controller.ProcessDueTodoReminders(now, (_, _) => { shown++; return true; }, _ => true);
        Assert(!items[0].ReminderTriggered && shown == 2, "disabled reminder feature cannot deliver or mutate state");
        controller.SaveNow(sync: true);
        StopTimers(controller); StopTimers(restarted);
    }

    private static void CheckDailyUseSoak(string temp)
    {
        var state = Fixture(); state.Papers[0].Items.Clear();
        for (var i = 0; i < 1500; i++) state.Papers[0].Items.Add(new() {
            Text = "长期操作任务 " + i, Id = "soak-" + i, Planning = new() {
                PlannedDate = DateOnly.FromDateTime(DateTime.Now).AddDays(i % 7), Priority = i % 4,
                ListId = i % 3 == 0 ? "work" : null } });
        var controller = Controller(state, new StateStore(Path.Combine(temp, "soak"), DurableAtomicFileWriter.Shared));
        var window = new PlannerWindow(controller, "inbox") { ShowActivated = false, ShowInTaskbar = false };
        window.Show(); DrainDispatcher();
        var navigate = typeof(PlannerWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var search = (TextBox)Field(window, "_search");
        var views = new[] { "today", "tomorrow", "inbox", "list", "all", "week" };
        var peakRows = 0; var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 600; i++)
        {
            search.Text = i % 5 == 0 ? "任务 1" : "";
            navigate.Invoke(window, [views[i % views.Length], views[i % views.Length] == "list" ? "work" : null]);
            window.UpdateLayout(); DrainDispatcher();
            var rows = ((System.Collections.IDictionary)Field(window, "_rowStamps")).Count;
            peakRows = Math.Max(peakRows, rows);
            Assert(rows < 500, "recycled UI cache stays bounded across repeated view/search changes");
            if (i % 60 == 0)
            {
                var task = state.Papers[0].Items[i];
                controller.PaperCommands.SetTaskDetails("inbox", task.Id, TaskPlanningRules.Version(task),
                    "长期操作已保存 " + i, task.Planning!, PaperOperationContext.User());
                window.QueueRefresh(); DrainDispatcher();
            }
        }
        search.Clear(); ((DispatcherTimer)Field(window, "_searchTimer")).Stop();
        Assert(controller.TryPrepareNormalExit(), "repeated operations end with a successful durable save");
        window.Close(); DrainDispatcher();
        Assert(!((DispatcherTimer)Field(window, "_midnightTimer")).IsEnabled &&
            !((DispatcherTimer)Field(window, "_searchTimer")).IsEnabled &&
            !((DispatcherTimer)Field(window, "_completionTimer")).IsEnabled, "closed planner stops all owned timers");
        StopTimers(controller);
        Console.WriteLine($"PASS daily-use stress: 1500 tasks, 600 view/search switches, 10 atomic edits; peak live row cache={peakRows}; elapsed={clock.Elapsed.TotalSeconds:F1}s. This is not an eight-hour soak or input-to-photon measurement.");
    }

    private static void StopTimers(AppController controller)
    {
        foreach (var field in typeof(AppController).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            if (field.GetValue(controller) is DispatcherTimer timer) timer.Stop();
    }
    private sealed class FailingWriter : IDurableAtomicFileWriter
    {
        public void Write(string targetPath, byte[] bytes, Func<string, bool>? validateTemp = null)
            => throw new IOException("Injected disk failure");
    }
    private sealed class StorageFaultWriter(int code) : IDurableAtomicFileWriter
    {
        public void Write(string targetPath, byte[] bytes, Func<string, bool>? validateTemp = null)
            => throw new IOException("Injected storage error", code);
    }
    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message); _assertions++;
    }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or PaperCommandException or McpApiException) { _assertions++; return; }
        throw new InvalidOperationException("Expected rejection: " + message);
    }
}
