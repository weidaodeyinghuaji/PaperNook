using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PaperTodo;

/// <summary>Small ink mark; checked state is authoritative, the stroke is presentation only.</summary>
internal sealed class PlannerCompletionMark : FrameworkElement
{
    public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(nameof(IsChecked),
        typeof(bool), typeof(PlannerCompletionMark), new FrameworkPropertyMetadata(false, Changed));
    public static readonly DependencyProperty MotionEnabledProperty = DependencyProperty.Register(nameof(MotionEnabled),
        typeof(bool), typeof(PlannerCompletionMark), new FrameworkPropertyMetadata(true, Changed));
    public static readonly DependencyProperty InkProperty = DependencyProperty.Register(nameof(Ink), typeof(Brush),
        typeof(PlannerCompletionMark), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RevealOnLoadProperty = DependencyProperty.Register(nameof(RevealOnLoad),
        typeof(bool), typeof(PlannerCompletionMark), new PropertyMetadata(false));
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double),
        typeof(PlannerCompletionMark), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsChecked { get => (bool)GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }
    public bool MotionEnabled { get => (bool)GetValue(MotionEnabledProperty); set => SetValue(MotionEnabledProperty, value); }
    public Brush Ink { get => (Brush)GetValue(InkProperty); set => SetValue(InkProperty, value); }
    public bool RevealOnLoad { get => (bool)GetValue(RevealOnLoadProperty); set => SetValue(RevealOnLoadProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    public PlannerCompletionMark()
    {
        Width = Height = 20; IsHitTestVisible = false;
        Loaded += (_, _) => { SystemParameters.StaticPropertyChanged += PreferencesChanged;
            if (RevealOnLoad && IsChecked) Progress = 0; ResetProgress(); };
        Unloaded += (_, _) => { SystemParameters.StaticPropertyChanged -= PreferencesChanged; BeginAnimation(ProgressProperty, null); };
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((PlannerCompletionMark)d).ResetProgress();
    private void ResetProgress()
    {
        var old = Progress; BeginAnimation(ProgressProperty, null); Progress = IsChecked ? 1 : 0;
        if (IsLoaded && MotionEnabled && SystemParameters.ClientAreaAnimation && IsChecked && old < 1)
            BeginAnimation(ProgressProperty, new DoubleAnimation(old, 1, TimeSpan.FromMilliseconds(150))
            { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
    private void PreferencesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or null) ResetProgress(); }
    protected override void OnRender(DrawingContext dc)
    {
        var center = new Point(10, 10); var pen = new Pen(Ink, 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.PushOpacity(IsChecked ? 1 : .65); dc.DrawEllipse(null, pen, center, 8.5, 8.5); dc.Pop();
        var p = Math.Clamp(Progress, 0, 1); if (p <= 0) return;
        var a = new Point(5.3, 10); var b = new Point(8.7, 13.1); var c = new Point(14.8, 6.8);
        Point Lerp(Point from, Point to, double t) => new(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
        dc.DrawLine(pen, a, Lerp(a, b, Math.Min(1, p / .36)));
        if (p > .36) dc.DrawLine(pen, b, Lerp(b, c, (p - .36) / .64));
    }
}
