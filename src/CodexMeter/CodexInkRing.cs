using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PaperTodo;

// The outer stroke is quota, never a spinner. Only the small mark inside describes activity.
internal sealed class CodexInkRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(CodexInkRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty MotionPhaseProperty = DependencyProperty.Register(
        nameof(MotionPhase), typeof(double), typeof(CodexInkRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty WorkPhaseProperty = DependencyProperty.Register(
        "WorkPhase", typeof(double), typeof(CodexInkRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private string _activityKind = "idle";
    private bool _motionRunning;
    private string _runningKind = "";
    private DateTimeOffset? _activityEventAt;
    private DateTimeOffset? _recordedEventAt;
    internal event Action<string, DateTimeOffset?>? FrameDrawn;
    public DateTimeOffset? ActivityEventAt
    {
        get => _activityEventAt;
        set { if (_activityEventAt == value) return; _activityEventAt = value; InvalidateVisual(); }
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string ActivityKind
    {
        get => _activityKind;
        set
        {
            if (_activityKind == value) return;
            _activityKind = value;
            RefreshMotion();
            InvalidateVisual();
        }
    }

    public Brush ForegroundBrush { get; set; } = Brushes.SteelBlue;
    public Brush TrackBrush { get; set; } = Brushes.LightGray;
    public bool ShowQuotaRing { get; set; } = true;
    public Func<bool>? MotionAllowed { get; set; }

    private double MotionPhase
    {
        get => (double)GetValue(MotionPhaseProperty);
        set => SetValue(MotionPhaseProperty, value);
    }

    public CodexInkRing()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged += OnSystemPreferenceChanged;
            RefreshMotion();
        };
        Unloaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= OnSystemPreferenceChanged;
            StopMotion();
        };
        IsVisibleChanged += (_, _) => RefreshMotion();
    }

    public void AnimateFrom(double from)
    {
        BeginAnimation(ValueProperty, new DoubleAnimation(from, Value,
            TimeSpan.FromMilliseconds(620))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }

    public void CueLowQuota()
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.5,
            TimeSpan.FromMilliseconds(240))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2),
            FillBehavior = FillBehavior.Stop
        });
    }

    public void RefreshMotion()
    {
        var shouldRun = IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation &&
            (MotionAllowed?.Invoke() ?? false) &&
            ActivityKind is ("thinking" or "tool" or "answering" or "active");
        if (shouldRun == _motionRunning && (!shouldRun || _runningKind == ActivityKind)) return;
        if (!shouldRun)
        {
            StopMotion();
            return;
        }

        _motionRunning = true;
        _runningKind = ActivityKind;
        BeginAnimation(WorkPhaseProperty, null);
        if (ActivityKind == "tool") BeginAnimation(WorkPhaseProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever });
        var seconds = ActivityKind switch
        {
            "tool" => .38,
            "answering" => 1.65,
            "thinking" => 2.0,
            _ => 2.4
        };
        BeginAnimation(MotionPhaseProperty, new DoubleAnimation(0, 1,
            TimeSpan.FromSeconds(seconds))
        {
            RepeatBehavior = ActivityKind == "tool" ? new RepeatBehavior(1) : RepeatBehavior.Forever
        });
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 12) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        if (ShowQuotaRing)
        {
            var thickness = size >= 30 ? 3.4 : 3.1;
            var radius = size / 2 - thickness / 2 - 0.5;
            drawingContext.DrawEllipse(null, new Pen(TrackBrush, thickness), center, radius, radius);
            var value = Math.Clamp(Value, 0, 1);
            if (value > 0)
            {
                var pen = new Pen(ForegroundBrush, thickness)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };
                if (value >= 0.999)
                {
                    drawingContext.DrawEllipse(null, pen, center, radius, radius);
                }
                else
                {
                    Point At(double degrees)
                    {
                        var radians = degrees * Math.PI / 180;
                        return new Point(center.X + Math.Cos(radians) * radius,
                            center.Y + Math.Sin(radians) * radius);
                    }
                    var geometry = new StreamGeometry();
                    using (var context = geometry.Open())
                    {
                        context.BeginFigure(At(-90), false, false);
                        context.ArcTo(At(-90 + value * 360), new Size(radius, radius), 0,
                            value > 0.5, SweepDirection.Clockwise, true, false);
                    }
                    geometry.Freeze();
                    drawingContext.DrawGeometry(null, pen, geometry);
                }
            }
        }

        DrawActivity(drawingContext, center, size);
    }

    private void DrawActivity(DrawingContext drawingContext, Point center, double size)
    {
        var scale = size / (ShowQuotaRing ? 24 : 17);
        var phase = _motionRunning ? MotionPhase : 0;
        var ink = ForegroundBrush;
        switch (ActivityKind)
        {
            case "thinking":
                // Three ink drops gather into one thought, then open again. The outer
                // quota stroke never moves, so motion cannot be mistaken for usage.
                var gather = _motionRunning
                    ? (1 - Math.Cos(2 * Math.PI * phase)) / 2
                    : 0.2;
                var spacing = (3.25 - 2.1 * gather) * scale;
                for (var index = -1; index <= 1; index++)
                {
                    var lift = _motionRunning ? Math.Sin(2 * Math.PI * phase + index * .8) * 1.15 : index == 0 ? -.65 : .3;
                    drawingContext.PushOpacity(index == 0 ? 1 : 0.6 + 0.4 * gather);
                    drawingContext.DrawEllipse(ink, null,
                        new Point(center.X + index * spacing,
                            center.Y + lift * scale),
                        (index == 0 ? 1.55 + 0.25 * gather : 1.15) * scale,
                        (index == 0 ? 1.55 + 0.25 * gather : 1.15) * scale);
                    drawingContext.Pop();
                }
                break;
            case "tool":
                // A tiny stamp lands, leaves a mark, and rests. It reads differently
                // from both the gathering thought and the written answer at 24 DIP.
                var strike = _motionRunning ? Math.Min(1, phase / 0.33) : 1;
                var stampY = (-2.2 + 2.2 * strike) * scale;
                var stampPen = new Pen(ink, 1.45 * scale)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                };
                drawingContext.DrawRoundedRectangle(null, stampPen,
                    new Rect(center.X - 2.45 * scale, center.Y - 2.85 * scale + stampY,
                        4.9 * scale, 4.4 * scale), 0.9 * scale, 0.9 * scale);
                drawingContext.DrawLine(stampPen,
                    new Point(center.X - 3.5 * scale, center.Y + 3.6 * scale),
                    new Point(center.X + 3.5 * scale, center.Y + 3.6 * scale));
                if (_motionRunning && phase >= .95)
                {
                    var work = (double)GetValue(WorkPhaseProperty);
                    drawingContext.DrawEllipse(ink, null,
                        new Point(center.X + (-2.6 + 5.2 * work) * scale, center.Y + 3.6 * scale),
                        .8 * scale, .8 * scale);
                }
                if (_motionRunning && phase is > 0.3 and < 0.55)
                {
                    drawingContext.PushOpacity(1 - (phase - 0.3) / 0.25);
                    drawingContext.DrawEllipse(ink, null,
                        new Point(center.X - 4.4 * scale, center.Y + 1.8 * scale),
                        0.65 * scale, 0.65 * scale);
                    drawingContext.DrawEllipse(ink, null,
                        new Point(center.X + 4.4 * scale, center.Y + 1.8 * scale),
                        0.65 * scale, 0.65 * scale);
                    drawingContext.Pop();
                }
                break;
            case "answering":
                var firstStroke = _motionRunning ? Math.Clamp(phase / 0.35, 0, 1) : 1;
                var secondStroke = _motionRunning ? Math.Clamp((phase - 0.28) / 0.36, 0, 1) : 1;
                var linePen = new Pen(ink, 1.45 * scale)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };
                drawingContext.DrawLine(linePen,
                    new Point(center.X - 3.3 * scale, center.Y - 2 * scale),
                    new Point(center.X + (-3.3 + 6.6 * firstStroke) * scale, center.Y - 2 * scale));
                drawingContext.DrawLine(linePen,
                    new Point(center.X - 3.3 * scale, center.Y + 2 * scale),
                    new Point(center.X + (-3.3 + 5.2 * secondStroke) * scale, center.Y + 2 * scale));
                if (_motionRunning && phase < 0.65)
                {
                    var nibX = phase < 0.35 ? -3.3 + 6.6 * firstStroke : -3.3 + 5.2 * secondStroke;
                    var nibY = phase < 0.35 ? -2 : 2;
                    drawingContext.DrawEllipse(ink, null,
                        new Point(center.X + nibX * scale, center.Y + nibY * scale),
                        1.15 * scale, 1.15 * scale);
                }
                break;
            case "active":
                var breath = _motionRunning ? 0.85 + 0.25 * Math.Sin(2 * Math.PI * phase) : 1;
                drawingContext.DrawEllipse(ink, null, center, 2.3 * scale * breath, 2.3 * scale * breath);
                break;
            case "waiting":
                var pausePen = new Pen(ink, 1.7 * scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                for (var x = -1; x <= 1; x += 2) drawingContext.DrawLine(pausePen,
                    new Point(center.X + x * 1.9 * scale, center.Y - 2.8 * scale),
                    new Point(center.X + x * 1.9 * scale, center.Y + 2.8 * scale));
                break;
            case "unknown":
            case "error":
                var neutralPen = new Pen(ink, 1.25 * scale) { DashStyle = new DashStyle([1.5, 2], 0) };
                drawingContext.DrawEllipse(null, neutralPen, center, 3.2 * scale, 3.2 * scale);
                drawingContext.DrawEllipse(ink, null, center, .7 * scale, .7 * scale);
                break;
            default:
                drawingContext.DrawEllipse(ink, null, center, 1.7 * scale, 1.7 * scale);
                break;
        }
        if (IsLoaded && IsVisible)
        {
            if (_recordedEventAt != ActivityEventAt)
            {
                CodexActivityLatency.Drawn(ActivityEventAt, ActivityKind);
                _recordedEventAt = ActivityEventAt;
            }
            FrameDrawn?.Invoke(ActivityKind, ActivityEventAt);
        }
    }

    private void StopMotion()
    {
        BeginAnimation(MotionPhaseProperty, null);
        BeginAnimation(WorkPhaseProperty, null);
        _motionRunning = false;
        _runningKind = "";
        MotionPhase = 0;
        InvalidateVisual();
    }

    private void OnSystemPreferenceChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or null)
            RefreshMotion();
    }
}
