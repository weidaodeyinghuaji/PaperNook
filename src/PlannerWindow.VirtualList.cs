using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PaperTodo;

internal sealed record PlannerListEntry(PlannerWindow Owner, PlannerTask? Task, string? Heading);

// Recycling containers create controls only for visible entries, not for every source task.
internal sealed class PlannerEntryPresenter : ContentControl
{
    public PlannerEntryPresenter()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        DataContextChanged += (_, _) => RefreshEntry();
        Loaded += (_, _) => { if (Content == null) RefreshEntry(); };
        Unloaded += (_, _) => ReleaseEntry();
    }
    private PlannerWindow? _owner;
    private void ReleaseEntry()
    {
        if (Content is Border row) _owner?.ForgetVirtualRow(row);
        Content = null; _owner = null;
    }
    private void RefreshEntry()
    {
        ReleaseEntry();
        if (DataContext is not PlannerListEntry entry) return;
        _owner = entry.Owner; Content = entry.Owner.RenderVirtualEntry(entry);
    }
}

internal sealed class PlannerViewportHeight(double fraction, double chrome, double maximum, int reservedTasks) : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var height = value is double number ? number : 0;
        var reserved = reservedTasks > 0 ? Math.Min(height * .3, reservedTasks * 75 + 40) : 0;
        return Math.Min(maximum, Math.Max(0, height * fraction - chrome - reserved));
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

internal sealed partial class PlannerWindow
{
    private bool _virtualizeRows;
    private bool _largeWeek;
    private readonly List<PlannerListEntry> _virtualEntries = new();
    private ListBox? _virtualTaskList;
    private readonly Dictionary<(string View, string? PaperId, string Day), double> _weekColumnScroll = new();
    internal UIElement RenderVirtualEntry(PlannerListEntry entry) => entry.Task != null
        ? TaskRow(entry.Task) : GroupHeading(entry.Heading!);
    internal void ForgetVirtualRow(Border row) => _rowStamps.Remove(row);

    private TextBlock GroupHeading(string title)
    {
        var label = Label(title, true); label.Margin = new Thickness(8, 18, 0, 9);
        label.FontWeight = FontWeights.SemiBold; label.FontSize = AppTypography.Scale(11); return label;
    }

    private ListBox BuildVirtualTaskList(IEnumerable<PlannerListEntry> entries, double fraction = 1, double chrome = 0,
        double maximum = double.PositiveInfinity, int reservedTasks = 0)
    {
        var list = new ListBox { ItemsSource = entries.ToArray(), BorderThickness = new Thickness(0), Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        list.SetResourceReference(Control.BackgroundProperty, "PlannerCanvas");
        list.SetBinding(HeightProperty, new Binding(nameof(ScrollViewer.ViewportHeight)) { Source = _taskScroll,
            Converter = new PlannerViewportHeight(fraction, chrome, maximum, reservedTasks) });
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        list.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(PlannerEntryPresenter)) };
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(FocusableProperty, false));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        presenter.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ContentControl.ContentTemplateProperty));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = presenter }));
        list.ItemContainerStyle = style;
        return list;
    }

    private static ScrollViewer? InnerScroll(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroll) return scroll;
            if (InnerScroll(child) is { } found) return found;
        }
        return null;
    }
    private (double X, double Y) CurrentTaskScroll() => (_taskScroll.HorizontalOffset,
        _virtualTaskList != null ? InnerScroll(_virtualTaskList)?.VerticalOffset ?? 0 : _taskScroll.VerticalOffset);

    private void CaptureWeekColumnScroll()
    {
        foreach (var list in WeekVirtualLists(_tasks))
            if (list.Tag is string day && InnerScroll(list) is { } scroll)
                _weekColumnScroll[(View, PaperId, day)] = scroll.VerticalOffset;
    }
    private static IEnumerable<ListBox> WeekVirtualLists(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ListBox list && list.Tag is string) yield return list;
            else foreach (var nested in WeekVirtualLists(child)) yield return nested;
        }
    }
}
