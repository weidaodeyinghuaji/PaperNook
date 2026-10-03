using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo;

internal sealed partial class PlannerWindow
{
    private sealed record SidebarList(PaperData Paper, string Title, string Folder);
    private SidebarList[]? _sidebarLists;
    private PlannerTaskQuery? _taskQuery;
    private bool _invalidateRows;
    private sealed record RowStamp(PaperData Source, PaperItem Item, string Text, bool Done, TaskPlanningData Planning,
        string ListId, string ListTitle, bool Compact, bool Overdue);
    private readonly Dictionary<Border, RowStamp> _rowStamps = new();
    private readonly Dictionary<(string PaperId, string TodoId), Queue<(Border Row, RowStamp Stamp)>> _availableRows = new();

    private RowStamp RowStampFor(PlannerTask task)
    {
        var list = _taskQuery!.ResolveList(task);
        return new(task.Paper, task.Item, task.Item.Text, task.Item.Done, task.Planning, list.Id, list.Title,
            _compactRows, TaskPlanningRules.IsOverdue(task.Planning, _today));
    }

    private void PrepareReusableRows()
    {
        _availableRows.Clear();
        if (!_invalidateRows)
            foreach (var row in TaskRows(_tasks))
            {
                // Virtualized rows belong to recycling content presenters, not to the flat panel.
                if (VisualTreeHelper.GetParent(row) is not Panel) continue;
                if (!_rowStamps.TryGetValue(row, out var stamp)) continue;
                var key = ((string, string))row.Tag;
                if (!_availableRows.TryGetValue(key, out var rows)) _availableRows[key] = rows = new();
                rows.Enqueue((row, stamp));
            }
        _rowStamps.Clear(); _invalidateRows = false;
    }

    private void UpdateRowSelection(Border row, PlannerTask task)
    {
        var selected = _selected?.Key == task.Key;
        row.BorderThickness = new Thickness(selected ? 2 : 0, 0, 0, 0);
        row.SetResourceReference(Border.BackgroundProperty, selected ? "PlannerSelection" : "PlannerCanvas");
    }

    private void RefreshSidebar(AppState state, PlannerTaskQuery query, bool refreshCounts)
    {
        if (IsPinned) return;
        var lists = state.Papers.Where(p => p.Type == PaperTypes.Todo && !p.PlannerInbox)
            .Select(p => new SidebarList(p, string.IsNullOrWhiteSpace(p.Title) ? S("Untitled") : p.Title,
                p.PlannerFolder ?? S("Ungrouped"))).ToArray();
        var rebuild = _sidebarLists == null || !_sidebarLists.SequenceEqual(lists);
        if (rebuild)
        {
            _sidebarLists = lists;
            _sidebar.Children.Clear(); _navigationButtons.Clear();
            foreach (var (view, key) in new[] { ("inbox", "Inbox"), ("today", "Today"), ("tomorrow", "Tomorrow"),
                ("week", "Week"), ("all", "All"), ("agenda", "Agenda"), ("completed", "Completed") })
            {
                var button = NavigationButton(S(key), 0, View == view, view, () => Navigate(view));
                _navigationButtons[(view, null)] = button; _sidebar.Children.Add(button);
            }
            var heading = new DockPanel { Margin = new Thickness(8, 18, 0, 6) };
            var create = ActionButton("+", () => EditList(null)); create.ToolTip = S("NewList");
            DockPanel.SetDock(create, Dock.Right); heading.Children.Add(create); heading.Children.Add(Label(S("Lists"), true));
            _sidebar.Children.Add(heading);
            foreach (var folder in lists.GroupBy(l => l.Folder))
            {
                var items = new StackPanel();
                var group = new Expander { Header = folder.Key, Content = items,
                    IsExpanded = !_collapsedFolders.Contains(folder.Key), Margin = new Thickness(4, 4, 0, 6) };
                group.SetResourceReference(Control.ForegroundProperty, "PlannerMuted");
                group.Collapsed += (_, _) => _collapsedFolders.Add(folder.Key);
                group.Expanded += (_, _) => _collapsedFolders.Remove(folder.Key);
                _sidebar.Children.Add(group);
                foreach (var list in folder)
                {
                    var paper = list.Paper;
                    var button = NavigationButton(list.Title, 0, View == "list" && PaperId == paper.Id,
                        "list", () => Navigate("list", paper.Id));
                    _navigationButtons[("list", paper.Id)] = button;
                    var menu = new ContextMenu();
                    var edit = new MenuItem { Header = S("EditList") }; edit.Click += (_, _) => EditList(paper);
                    var show = new MenuItem { Header = S("ShowPaper") }; show.Click += (_, _) => _controller.ShowPaper(paper);
                    menu.Items.Add(edit); menu.Items.Add(show); button.ContextMenu = menu; items.Children.Add(button);
                }
            }
        }
        if (!rebuild && !refreshCounts) return;
        foreach (var entry in _navigationButtons)
        {
            var count = query.Query(entry.Key.View, entry.Key.PaperId, _today).Count();
            var grid = (Grid)((Border)entry.Value.Content).Child;
            var number = (TextBlock)grid.Children[2];
            var text = count.ToString(System.Globalization.CultureInfo.CurrentCulture);
            if (number.Text != text) number.Text = text;
            System.Windows.Automation.AutomationProperties.SetName(entry.Value, entry.Value.ToolTip + " " + text);
        }
    }
}
