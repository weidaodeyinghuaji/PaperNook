using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class PlannerWindow
{
    private readonly TextBlock _subtitle = new();
    private readonly ScrollViewer _taskScroll = new();
    private readonly HashSet<string> _collapsedFolders = new();
    private FrameworkElement _addHost = null!;
    private FrameworkElement _searchHost = null!;
    private TextBlock? _dirtyLabel;
    private bool _wideWeek;
    private bool _compactRows;
    private sealed record CompletingTask(PlannerTask Task, string View, string? PaperId, string Search, long Until);
    private readonly Dictionary<(string PaperId, string TodoId), CompletingTask> _completing = new();
    private readonly DispatcherTimer _completionTimer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly Dictionary<(string View, string? PaperId), (double X, double Y)> _scrollPositions = new();
    private (string PaperId, string TodoId)? _addedTask;
    private Button? _saveButton;
    private int _renderEpoch;
    private (double X, double Y)? _navigateScroll;
    private bool MotionEnabled => _controller.State.EnableAnimations && SystemParameters.ClientAreaAnimation;

    private void OnMotionPreferenceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or null) RefreshMotionPreference();
        if (IsPinned && _pinLayoutReady && e.PropertyName is nameof(SystemParameters.WorkArea) or null)
        {
            WindowWorkAreaHelper.InvalidateMonitorGeometryCache();
            ApplyPinnedBounds(CapturePinnedLayout(), WindowWorkAreaHelper.WorkAreaFor(this));
        }
    }

    private void RefreshMotionPreference()
    {
        Resources["PlannerMotion"] = MotionEnabled;
        if (MotionEnabled) return;
        foreach (var element in TaskRows(_tasks).Cast<UIElement>().Append(_tasks).Append(_detailHost))
        {
            element.BeginAnimation(OpacityProperty, null);
            if (element.RenderTransform is TranslateTransform translate)
            { translate.BeginAnimation(TranslateTransform.XProperty, null); translate.BeginAnimation(TranslateTransform.YProperty, null); }
        }
        FinishCompletions();
    }

    private void ApplyPlannerTheme()
    {
        // Local, dynamic resources also recolor dirty editors without reconstructing their values.
        void Color(string key, string light, string dark) => Resources[key] =
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(Theme.IsDark ? dark : light));
        Color("PlannerCanvas", "#FFFEFA", "#1C2529");
        Color("PlannerSide", "#F5F4EF", "#222E33");
        Color("PlannerSurface", "#FFFEFA", "#273238");
        Color("PlannerInk", "#333C3B", "#E5EEEF");
        Color("PlannerMuted", "#767C76", "#A8BBBA");
        Color("PlannerAccent", "#4E7164", "#8EB8A5");
        Color("PlannerSelection", "#ECEFE7", "#31423D");
        Color("PlannerLine", "#E4E3DC", "#3E5054");
        Color("PlannerDanger", "#B94B40", "#FA9D90");
        SetResourceReference(BackgroundProperty, "PlannerCanvas");
        SetResourceReference(ForegroundProperty, "PlannerInk");
        _heading.SetResourceReference(TextBlock.ForegroundProperty, "PlannerInk");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "PlannerMuted");
        Resources["PlannerMotion"] = MotionEnabled;

        var button = new Style(typeof(Button));
        button.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("PlannerSide")));
        button.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("PlannerInk")));
        button.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("PlannerLine")));
        button.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        button.Setters.Add(new Setter(Control.MinHeightProperty, 34d));
        var frame = new FrameworkElementFactory(typeof(Border)); frame.Name = "frame";
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        frame.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        frame.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        frame.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetBinding(FrameworkElement.MarginProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        content.SetBinding(FrameworkElement.HorizontalAlignmentProperty, new System.Windows.Data.Binding("HorizontalContentAlignment") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        frame.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = frame };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, .8, "frame")); template.Triggers.Add(hover);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, .6, "frame")); template.Triggers.Add(pressed);
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "frame"));
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("PlannerAccent"), "frame"));
        template.Triggers.Add(focus);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .45, "frame")); template.Triggers.Add(disabled);
        button.Setters.Add(new Setter(Control.TemplateProperty, template)); Resources[typeof(Button)] = button;

        var editor = new Style(typeof(TextBox));
        editor.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("PlannerSurface")));
        editor.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("PlannerInk")));
        editor.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("PlannerLine")));
        editor.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        editor.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 8, 10, 8)));
        editor.Setters.Add(new Setter(Control.MinHeightProperty, 36d));
        var editFrame = new FrameworkElementFactory(typeof(Border)); editFrame.Name = "editor";
        editFrame.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        foreach (var property in new[] { Border.BackgroundProperty, Border.BorderBrushProperty, Border.BorderThicknessProperty, Border.PaddingProperty })
            editFrame.SetBinding(property, new System.Windows.Data.Binding(property.Name) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var scroll = new FrameworkElementFactory(typeof(ScrollViewer)); scroll.Name = "PART_ContentHost";
        editFrame.AppendChild(scroll);
        var editTemplate = new ControlTemplate(typeof(TextBox)) { VisualTree = editFrame };
        var editFocus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        editFocus.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("PlannerAccent"), "editor"));
        editTemplate.Triggers.Add(editFocus);
        editor.Setters.Add(new Setter(Control.TemplateProperty, editTemplate)); Resources[typeof(TextBox)] = editor;
        foreach (var type in new[] { typeof(ComboBox), typeof(DatePicker), typeof(Expander) })
        {
            var style = new Style(type);
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("PlannerInk")));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("PlannerSurface")));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("PlannerLine")));
            style.Setters.Add(new Setter(Control.PaddingProperty, type == typeof(DatePicker) ? new Thickness(0) : new Thickness(6)));
            style.Setters.Add(new Setter(Control.MinHeightProperty, 36d)); Resources[type] = style;
            if (type == typeof(DatePicker))
            {
                // Build the complete style before assigning it: a live resource lookup can seal it.
                var dateStyle = new Style(type, style); dateStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, 36d));
                Resources[type] = dateStyle;
            }
        }
        ConfigureSelectionControls();
        // Preserve native picker behavior, including typing, calendar keyboard navigation and popup focus.
        var dateText = new Style(typeof(DatePickerTextBox), (Style)Resources[typeof(TextBox)]);
        dateText.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("PlannerInk")));
        dateText.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("PlannerSurface")));
        dateText.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        dateText.Setters.Add(new Setter(Control.MinHeightProperty, 28d));
        dateText.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
        Resources[typeof(DatePickerTextBox)] = dateText;
        var check = new Style(typeof(CheckBox));
        check.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("PlannerInk")));
        var checkPanel = new FrameworkElementFactory(typeof(DockPanel));
        var circle = new FrameworkElementFactory(typeof(Border)); circle.Name = "circle";
        circle.SetValue(FrameworkElement.WidthProperty, 20d); circle.SetValue(FrameworkElement.HeightProperty, 20d);
        circle.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        circle.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
        circle.SetResourceReference(Border.BorderBrushProperty, "PlannerMuted");
        var mark = new FrameworkElementFactory(typeof(PlannerCompletionMark));
        mark.SetBinding(PlannerCompletionMark.IsCheckedProperty, new System.Windows.Data.Binding("IsChecked")
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent, TargetNullValue = false });
        mark.SetResourceReference(PlannerCompletionMark.InkProperty, "PlannerAccent");
        mark.SetBinding(PlannerCompletionMark.RevealOnLoadProperty, new System.Windows.Data.Binding("Tag")
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent, TargetNullValue = false });
        mark.SetResourceReference(PlannerCompletionMark.MotionEnabledProperty, "PlannerMotion");
        var checkFocusFrame = new FrameworkElementFactory(typeof(Border)); checkFocusFrame.Name = "circle";
        checkFocusFrame.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        checkFocusFrame.SetResourceReference(Border.BorderBrushProperty, "PlannerAccent");
        checkFocusFrame.SetValue(DockPanel.DockProperty, Dock.Left);
        checkFocusFrame.AppendChild(mark); checkPanel.AppendChild(checkFocusFrame);
        var checkContent = new FrameworkElementFactory(typeof(ContentPresenter));
        checkContent.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0)); checkPanel.AppendChild(checkContent);
        var checkTemplate = new ControlTemplate(typeof(CheckBox)) { VisualTree = checkPanel };
        var checkFocus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        checkFocus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1), "circle")); checkTemplate.Triggers.Add(checkFocus);
        check.Setters.Add(new Setter(Control.TemplateProperty, checkTemplate)); Resources[typeof(CheckBox)] = check;
    }

    private void ConfigureSelectionControls()
    {
        static System.Windows.Data.Binding Parent(string path, bool twoWay = false) => new(path)
        { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent,
            Mode = twoWay ? System.Windows.Data.BindingMode.TwoWay : System.Windows.Data.BindingMode.OneWay };
        // ComboBox retains its native keyboard/selection logic and named popup; only the chrome is replaced.
        var comboStyle = new Style(typeof(ComboBox), (Style)Resources[typeof(ComboBox)]);
        var root = new FrameworkElementFactory(typeof(Grid));
        var toggle = new FrameworkElementFactory(typeof(ToggleButton));
        toggle.SetValue(UIElement.FocusableProperty, false);
        toggle.SetBinding(ToggleButton.IsCheckedProperty, Parent("IsDropDownOpen", true));
        var toggleFrame = new FrameworkElementFactory(typeof(Border)); toggleFrame.Name = "comboFrame";
        toggleFrame.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        toggleFrame.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        toggleFrame.SetResourceReference(Border.BackgroundProperty, "PlannerSurface");
        toggleFrame.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush")
        { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(ComboBox), 1) });
        var arrow = new FrameworkElementFactory(typeof(TextBlock)); arrow.SetValue(TextBlock.TextProperty, "⌄");
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "PlannerMuted");
        arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
        toggleFrame.AppendChild(arrow);
        toggle.SetValue(Control.TemplateProperty, new ControlTemplate(typeof(ToggleButton)) { VisualTree = toggleFrame });
        root.AppendChild(toggle);
        var selection = new FrameworkElementFactory(typeof(ContentPresenter));
        selection.SetBinding(ContentPresenter.ContentProperty, Parent("SelectionBoxItem"));
        selection.SetBinding(ContentPresenter.ContentTemplateProperty, Parent("SelectionBoxItemTemplate"));
        selection.SetValue(UIElement.IsHitTestVisibleProperty, false);
        selection.SetValue(UIElement.ClipToBoundsProperty, true);
        selection.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 7, 28, 7));
        selection.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); root.AppendChild(selection);
        var popup = new FrameworkElementFactory(typeof(Popup)); popup.Name = "PART_Popup";
        popup.SetBinding(Popup.IsOpenProperty, Parent("IsDropDownOpen"));
        popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        var popupFrame = new FrameworkElementFactory(typeof(Border));
        popupFrame.SetResourceReference(Border.BackgroundProperty, "PlannerSurface");
        popupFrame.SetResourceReference(Border.BorderBrushProperty, "PlannerLine");
        popupFrame.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        popupFrame.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        popupFrame.SetBinding(FrameworkElement.MinWidthProperty, Parent("ActualWidth"));
        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.SetBinding(FrameworkElement.MaxHeightProperty, Parent("MaxDropDownHeight"));
        scroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        popupFrame.AppendChild(scroll); popup.AppendChild(popupFrame); root.AppendChild(popup);
        comboStyle.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ComboBox)) { VisualTree = root }));
        var focused = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focused.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("PlannerAccent")));
        comboStyle.Triggers.Add(focused);
        Resources[typeof(ComboBox)] = comboStyle;
        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("PlannerSurface")));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("PlannerInk")));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7)));
        Resources[typeof(ComboBoxItem)] = itemStyle;

        var expanderStyle = new Style(typeof(Expander), (Style)Resources[typeof(Expander)]);
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        var header = new FrameworkElementFactory(typeof(ToggleButton));
        header.SetBinding(ToggleButton.IsCheckedProperty, Parent("IsExpanded", true));
        header.SetBinding(ContentControl.ContentProperty, Parent("Header"));
        header.SetResourceReference(Control.ForegroundProperty, "PlannerMuted");
        var headerDock = new FrameworkElementFactory(typeof(DockPanel));
        headerDock.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 7, 0, 7));
        var chevron = new FrameworkElementFactory(typeof(TextBlock)); chevron.Name = "chevron";
        chevron.SetValue(TextBlock.TextProperty, "›"); chevron.SetValue(TextBlock.FontSizeProperty, 18d);
        chevron.SetValue(FrameworkElement.WidthProperty, 20d);
        var headerContent = new FrameworkElementFactory(typeof(ContentPresenter));
        headerDock.AppendChild(chevron); headerDock.AppendChild(headerContent);
        var headerTemplate = new ControlTemplate(typeof(ToggleButton)) { VisualTree = headerDock };
        var expanded = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        expanded.Setters.Add(new Setter(TextBlock.TextProperty, "⌄", "chevron")); headerTemplate.Triggers.Add(expanded);
        var headerFocus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        headerFocus.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold, "chevron")); headerTemplate.Triggers.Add(headerFocus);
        header.SetValue(Control.TemplateProperty, headerTemplate); stack.AppendChild(header);
        var body = new FrameworkElementFactory(typeof(ContentPresenter)); body.Name = "body";
        body.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed); stack.AppendChild(body);
        var expanderTemplate = new ControlTemplate(typeof(Expander)) { VisualTree = stack };
        var show = new Trigger { Property = Expander.IsExpandedProperty, Value = true };
        show.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "body")); expanderTemplate.Triggers.Add(show);
        expanderStyle.Setters.Add(new Setter(Control.TemplateProperty, expanderTemplate));
        Resources[typeof(Expander)] = expanderStyle;
    }

    private static void AccentButton(Button button)
    {
        button.SetResourceReference(Control.BackgroundProperty, "PlannerAccent");
        button.Foreground = Theme.IsDark ? new SolidColorBrush(Color.FromRgb(25, 46, 46)) : Brushes.White;
        // Foreground follows palette flips as well as dynamically constructed dirty details.
        button.SetResourceReference(Control.ForegroundProperty, "PlannerCanvas");
        button.FontWeight = FontWeights.SemiBold;
    }

    private static void AddMenu(ContextMenu menu, string title, Action action)
    {
        var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); menu.Items.Add(item);
    }

    private bool _navigating;
    private readonly Dictionary<(string View, string? PaperId), Button> _navigationButtons = new();

    private static void UpdateNavigationSelection(Button button, bool selected)
    {
        button.SetResourceReference(Control.BackgroundProperty, selected ? "PlannerSelection" : "PlannerSide");
        var marker = (Border)button.Content;
        marker.BorderThickness = new Thickness(selected ? 2 : 0, 0, 0, 0);
        var grid = (Grid)marker.Child;
        ((System.Windows.Shapes.Path)grid.Children[0]).SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
            selected ? "PlannerAccent" : "PlannerMuted");
        ((TextBlock)grid.Children[1]).FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private Button NavigationButton(string title, int count, bool selected, string kind, Action action)
    {
        var button = ActionButton("", action); button.Padding = new Thickness(8, 9, 8, 9);
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.SetResourceReference(Control.BackgroundProperty, selected ? "PlannerSelection" : "PlannerSide");
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(25) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var icon = new System.Windows.Shapes.Path { Width = 15, Height = 15, Stretch = Stretch.Uniform,
            StrokeThickness = 1.2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
            Data = Geometry.Parse(kind switch
            {
                "inbox" => "M1,5 L4,1 12,1 15,5 15,13 1,13 Z M1,7 L5,7 6,9 10,9 11,7 15,7",
                "today" or "tomorrow" or "week" => "M2,3 L14,3 14,14 2,14 Z M2,6 L14,6 M5,1 L5,4 M11,1 L11,4 M5,9 L7,9 M9,9 L11,9 M5,12 L7,12",
                "agenda" => "M8,1 A7,7 0 1 1 7.99,1 M8,4 L8,8 11,10",
                "completed" => "M2,8 L6,12 14,3",
                "list" => "M2,4 L14,4 M2,8 L14,8 M2,12 L14,12",
                _ => "M2,2 L14,2 14,14 2,14 Z M5,6 L11,6 M5,10 L11,10"
            }) };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, selected ? "PlannerAccent" : "PlannerMuted");
        grid.Children.Add(icon);
        var name = Label(title); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.Margin = new Thickness(0); name.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        Grid.SetColumn(name, 1); grid.Children.Add(name);
        var number = Label(count.ToString(), true); number.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(number, 2); grid.Children.Add(number);
        var marker = new Border { BorderThickness = new Thickness(selected ? 2 : 0, 0, 0, 0), Padding = new Thickness(5, 0, 0, 0), Child = grid };
        marker.SetResourceReference(Border.BorderBrushProperty, "PlannerAccent"); button.Content = marker;
        button.ToolTip = title; System.Windows.Automation.AutomationProperties.SetName(button, title + " " + count);
        return button;
    }

    private UIElement EmptyState()
    {
        var panel = new StackPanel { Margin = new Thickness(16, 36, 16, 24) };
        panel.Children.Add(Label(S("Empty"), true));
        if (View != "completed")
            panel.Children.Add(ActionButton("+ " + S("Add"), () => { _addHost.Visibility = Visibility.Visible; _quickAdd.Focus(); }));
        return panel;
    }

    private void UpdateResponsiveLayout()
    {
        if (IsPinned) return;
        _detailHost.Visibility = _selected == null ? Visibility.Collapsed : Visibility.Visible;
        var width = _layout.ActualWidth > 0 ? _layout.ActualWidth : Width - 32;
        _layout.ColumnDefinitions[0].Width = new GridLength(width < 900 ? 164 : 200);
        var overlay = width < 940;
        _layout.ColumnDefinitions[2].Width = new GridLength(_selected == null || overlay ? 0 : 310);
        Grid.SetColumn(_detailHost, overlay ? 1 : 2);
        Grid.SetColumnSpan(_detailHost, overlay ? 2 : 1);
        _detailHost.HorizontalAlignment = overlay ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        _detailHost.Width = overlay && _selected != null ? Math.Min(310, Math.Max(240, width - 200)) : double.NaN;
        Panel.SetZIndex(_detailHost, 2);
        var wide = width >= 940;
        if (wide != _wideWeek) { _wideWeek = wide; if (View == "week") QueueRefresh(); }
        if (View == "week") UpdateWeekWidths();
    }

    private void AddWeekColumns(PlannerTask[] tasks)
    {
        var grid = new Grid();
        var virtualColumns = tasks.Length >= 80;
        var overdue = tasks.Count(t => TaskPlanningRules.IsOverdue(t.Planning, _today));
        var columnWidth = Math.Max(170, (_taskScroll.ViewportWidth > 0 ? _taskScroll.ViewportWidth : 850) / 7);
        for (var i = 0; i < 7; i++)
        {
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(columnWidth) });
            var date = _today.AddDays(i);
            var panel = new StackPanel { Margin = new Thickness(8) };
            var title = Label(date.ToString("M/d ddd", System.Globalization.CultureInfo.CurrentCulture));
            title.FontWeight = FontWeights.SemiBold;
            if (i == 0) title.SetResourceReference(TextBlock.ForegroundProperty, "PlannerAccent");
            panel.Children.Add(title);
            var dayTasks = tasks.Where(t => !TaskPlanningRules.IsOverdue(t.Planning, _today) && TaskPlanningRules.OnDate(t.Planning, date)).ToArray();
            if (virtualColumns)
            {
                var list = BuildVirtualTaskList(dayTasks.Select(t => new PlannerListEntry(this, t, null)),
                    chrome: 55, reservedTasks: overdue);
                list.Tag = date.ToString("yyyy-MM-dd"); panel.Children.Add(list);
            }
            else foreach (var task in dayTasks) panel.Children.Add(TaskRow(task));
            var column = new Border { Child = panel, BorderThickness = new Thickness(0, i == 0 ? 2 : 0, 0, 0) };
            column.SetResourceReference(Border.BorderBrushProperty, i == 0 ? "PlannerAccent" : "PlannerLine");
            Grid.SetColumn(column, i); grid.Children.Add(column);
        }
        _tasks.Children.Add(grid);
    }

    private void UpdateWeekWidths()
    {
        var grid = _tasks.Children.OfType<Grid>().FirstOrDefault(g => g.ColumnDefinitions.Count == 7);
        if (grid == null) return;
        var width = Math.Max(170, _taskScroll.ViewportWidth / 7);
        foreach (var column in grid.ColumnDefinitions) column.Width = new GridLength(width);
    }

    private void MarkDirty()
    {
        _detailsDirty = true;
        if (_saveButton != null) _saveButton.IsEnabled = true;
        if (_dirtyLabel != null) _dirtyLabel.Text = S("Unsaved");
    }

    private void AnimateEntrance(UIElement element, double offset = 0)
    {
        if (!_controller.State.EnableAnimations || !SystemParameters.ClientAreaAnimation) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.65, 1, TimeSpan.FromMilliseconds(180))
            { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        if (offset != 0)
        {
            var translate = new TranslateTransform(); element.RenderTransform = translate;
            translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offset, 0, TimeSpan.FromMilliseconds(200))
                { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private void SetPinnedToolsVisible(UIElement element, bool visible)
    {
        var from = element.Opacity;
        element.BeginAnimation(OpacityProperty, null); element.Opacity = visible ? 1 : 0;
        if (MotionEnabled) element.BeginAnimation(OpacityProperty,
            new DoubleAnimation(from, element.Opacity, TimeSpan.FromMilliseconds(120))
            { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void CompleteTask(PlannerTask task, CheckBox check, Border row, TextBlock title)
    {
        if (_completing.ContainsKey(task.Key)) return;
        var done = check.IsChecked == true;
        // The command commits immediately; animation is presentation only, never a deferred write.
        try
        {
            _controller.PaperCommands.UpdateTodo(new UpdateTodoRequest { PaperId = task.Paper.Id,
                TodoId = task.Item.Id, Done = done }, PaperOperationContext.User());
            if (done && View != "completed" && MotionEnabled)
            {
                check.IsEnabled = false;
                _completing[task.Key] = new(task, View, PaperId, _search.Text, Environment.TickCount64 + 330);
                DrawCompletion(title, true); FadeCompletedRow(row, 330); _completionTimer.Start();
            }
            else { _completing.Remove(task.Key); QueueRefresh(); }
        }
        catch (Exception ex) when (ex is PaperCommandException or ArgumentException)
        {
            _completing.Remove(task.Key); check.IsChecked = task.Item.Done; _status.Text = ex.Message; QueueRefresh();
        }
    }

    private void FinishCompletions()
    {
        var finished = _completing.Where(p => !MotionEnabled || !p.Value.Task.Item.Done || p.Value.Until <= Environment.TickCount64).Select(p => p.Key).ToArray();
        foreach (var key in finished)
            _completing.Remove(key);
        if (_completing.Count == 0) _completionTimer.Stop();
        if (finished.Length != 0) QueueRefresh();
    }

    private IEnumerable<Border> TaskRows(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border { Tag: ValueTuple<string, string> } row) yield return row;
            else foreach (var nested in TaskRows(child)) yield return nested;
        }
    }

    private Dictionary<(string Paper, string Todo, int Occurrence), double> CaptureRowPositions()
    {
        var positions = new Dictionary<(string, string, int), double>();
        var occurrences = new Dictionary<(string, string), int>();
        foreach (var row in TaskRows(_tasks))
        {
            var key = ((string, string))row.Tag; occurrences.TryGetValue(key, out var index); occurrences[key] = index + 1;
            positions[(key.Item1, key.Item2, index)] = row.TransformToAncestor(_tasks).Transform(new Point()).Y;
        }
        return positions;
    }

    private void FinishRowLayout(Dictionary<(string Paper, string Todo, int Occurrence), double> before, (double X, double Y) scroll, bool animateRows = true)
    {
        var epoch = ++_renderEpoch;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_closed || epoch != _renderEpoch) return;
            if (_virtualTaskList != null) InnerScroll(_virtualTaskList)?.ScrollToVerticalOffset(scroll.Y);
            else _taskScroll.ScrollToVerticalOffset(scroll.Y);
            _taskScroll.ScrollToHorizontalOffset(scroll.X);
            foreach (var list in WeekVirtualLists(_tasks))
                if (list.Tag is string day) InnerScroll(list)?.ScrollToVerticalOffset(
                    _weekColumnScroll.GetValueOrDefault((View, PaperId, day)));
            UpdateWeekWidths();
            if (!animateRows || _virtualTaskList != null || WeekVirtualLists(_tasks).Any() || !MotionEnabled || !IsLoaded)
            { _addedTask = null; return; }
            var occurrences = new Dictionary<(string, string), int>();
            foreach (var row in TaskRows(_tasks))
            {
                var key = ((string, string))row.Tag; occurrences.TryGetValue(key, out var index); occurrences[key] = index + 1;
                if (_addedTask == key) { AnimateEntrance(row); continue; }
                var y = row.TransformToAncestor(_tasks).Transform(new Point()).Y;
                if (!before.TryGetValue((key.Item1, key.Item2, index), out var previous) || Math.Abs(previous - y) < 1) continue;
                if (y < scroll.Y - 100 || y > scroll.Y + _taskScroll.ViewportHeight + 100) continue;
                var transform = new TranslateTransform(); row.RenderTransform = transform;
                transform.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(Math.Clamp(previous - y, -48, 48), 0, TimeSpan.FromMilliseconds(160))
                    { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            _addedTask = null;
        }));
    }

    private static void FadeCompletedRow(Border row, double remaining)
    {
        row.BeginAnimation(OpacityProperty, new DoubleAnimation(1, .3, TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(120, remaining))))
        { BeginTime = TimeSpan.FromMilliseconds(Math.Max(0, remaining - 120)), FillBehavior = FillBehavior.Stop });
    }

    private void DrawCompletion(TextBlock title, bool animate)
    {
        var ink = (Brush)Resources["PlannerInk"];
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        var color = (ink as SolidColorBrush)?.Color ?? Colors.Gray;
        brush.GradientStops.Add(new GradientStop(color, 0));
        var edge = new GradientStop(color, animate && MotionEnabled ? 0 : 1);
        var clear = new GradientStop(Colors.Transparent, edge.Offset);
        brush.GradientStops.Add(edge); brush.GradientStops.Add(clear); brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        title.TextDecorations = new TextDecorationCollection { new TextDecoration(TextDecorationLocation.Strikethrough,
            new Pen(brush, 1), 0, TextDecorationUnit.FontRecommended, TextDecorationUnit.Pixel) };
        title.Opacity = .6;
        if (animate && MotionEnabled)
            foreach (var stop in new[] { edge, clear }) stop.BeginAnimation(GradientStop.OffsetProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { BeginTime = TimeSpan.FromMilliseconds(60) });
    }
}
