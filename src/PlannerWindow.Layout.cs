using System.Windows;

namespace PaperTodo;

internal static class PlannerPinnedPlacement
{
    internal static Rect Constrain(PlannerPinnedLayout layout, Rect workArea)
    {
        var width = Math.Min(Math.Max(340, layout.Width), workArea.Width);
        var height = Math.Min(Math.Max(400, layout.Height), workArea.Height);
        return new Rect(Math.Clamp(layout.Left, workArea.Left, workArea.Right - width),
            Math.Clamp(layout.Top, workArea.Top, workArea.Bottom - height), width, height);
    }
}

internal sealed partial class PlannerWindow
{
    private string _pinDensity = "auto";
    private PlannerPinnedLayout? _restoredLayout;
    private bool _pinLayoutReady;

    internal void RestorePinnedLayout(PlannerPinnedLayout? layout)
    {
        if (!IsPinned || _pinLayoutReady) return;
        _restoredLayout = PlannerPinnedLayout.Normalize(layout);
        if (_restoredLayout == null) return;
        _pinDensity = _restoredLayout.Density;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = _restoredLayout.Left; Top = _restoredLayout.Top;
        Width = _restoredLayout.Width; Height = _restoredLayout.Height;
        QueueRefresh();
    }

    private void InitializePinnedLayout()
    {
        if (!IsPinned || _pinLayoutReady) return;
        var saved = _restoredLayout;
        var area = saved?.MonitorDeviceName is { } device
            ? WindowWorkAreaHelper.WorkAreaForDevice(device) ?? WindowWorkAreaHelper.WorkAreaFor(this)
            : WindowWorkAreaHelper.WorkAreaFor(this);
        ApplyPinnedBounds(saved ?? CapturePinnedLayout(), area);
        _restoredLayout = null;
        _pinLayoutReady = true;
        RememberPinnedLayout();
    }

    private PlannerPinnedLayout CapturePinnedLayout()
    {
        WindowWorkAreaHelper.TryGetMonitorDeviceName(this, out var device);
        return new(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height,
            string.IsNullOrEmpty(device) ? null : device, _pinDensity);
    }

    private void ApplyPinnedBounds(PlannerPinnedLayout layout, Rect area)
    {
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0) return;
        var bounds = PlannerPinnedPlacement.Constrain(layout, area);
        MinWidth = Math.Min(340, area.Width); MinHeight = Math.Min(400, area.Height);
        Width = bounds.Width; Height = bounds.Height; Left = bounds.Left; Top = bounds.Top;
    }

    private void RememberPinnedLayout()
    {
        if (!IsPinned || !_pinLayoutReady || WindowState != WindowState.Normal) return;
        var layout = PlannerPinnedLayout.Normalize(CapturePinnedLayout());
        if (layout != null) _controller.RememberPlannerPinnedLayout(View, PaperId, layout);
    }

    private void SetPinnedDensity(string density)
    {
        _pinDensity = density;
        QueueRefresh(); RememberPinnedLayout();
    }
}
