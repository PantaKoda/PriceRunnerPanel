using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PriceRunnerPanel.Controls;

/// <summary>
/// A price history as a step line (prices hold until they change) with a soft area beneath and a dot on the
/// latest price. The lowest point is marked so a dip is visible at a glance.
/// </summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> LowBrushProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(LowBrush));

    static Sparkline()
    {
        AffectsRender<Sparkline>(ValuesProperty, StrokeProperty, LowBrushProperty);
    }

    public Sparkline()
    {
        IsHitTestVisible = false;
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? LowBrush
    {
        get => GetValue(LowBrushProperty);
        set => SetValue(LowBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var values = Values;
        var stroke = Stroke ?? Brushes.SteelBlue;
        var (w, h) = (Bounds.Width, Bounds.Height);
        if (w <= 8 || h <= 6)
        {
            return;
        }

        const double pad = 7;
        var baseline = new Pen(new SolidColorBrush(Colors.SlateGray, 0.25), 1, new DashStyle([2, 3], 0));
        context.DrawLine(baseline, new Point(0, h - 0.5), new Point(w, h - 0.5));
        if (values is null || values.Count == 0)
        {
            return;
        }

        // A single observation (or a flat price) is a level line across the middle.
        IReadOnlyList<double> series = values.Count == 1 ? [values[0], values[0]] : values;
        var min = series.Min();
        var max = series.Max();
        var range = max - min;
        double X(int i) => pad + ((w - (2 * pad)) * i / (series.Count - 1));
        double Y(double v) => range < 1e-9 ? h / 2 : pad + ((h - (2 * pad)) * (1 - ((v - min) / range)));

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            l.BeginFigure(new Point(X(0), Y(series[0])), false);
            a.BeginFigure(new Point(X(0), h), true);
            a.LineTo(new Point(X(0), Y(series[0])));
            for (var i = 1; i < series.Count; i++)
            {
                var step = new Point(X(i), Y(series[i - 1]));
                var next = new Point(X(i), Y(series[i]));
                l.LineTo(step);
                l.LineTo(next);
                a.LineTo(step);
                a.LineTo(next);
            }

            a.LineTo(new Point(X(series.Count - 1), h));
            l.EndFigure(false);
            a.EndFigure(true);
        }

        var color = stroke is ISolidColorBrush solid ? solid.Color : Colors.SteelBlue;
        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(70, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
            },
        };
        context.DrawGeometry(fill, null, area);
        context.DrawGeometry(null, new Pen(stroke, 1.6, lineJoin: PenLineJoin.Round), line);

        if (range > 1e-9 && LowBrush is { } lowBrush)
        {
            var lowIndex = values.Count == 1 ? 0 : Enumerable.Range(0, series.Count).First(i => series[i] <= min);
            context.DrawEllipse(lowBrush, null, new Point(X(lowIndex), Y(min)), 2.6, 2.6);
        }

        var last = new Point(X(series.Count - 1), Y(series[^1]));
        context.DrawEllipse(new SolidColorBrush(color, 0.22), null, last, 6, 6);
        context.DrawEllipse(stroke, null, last, 3, 3);
    }
}

/// <summary>
/// Where the current price sits between the lowest and highest price seen: a thin gradient track (green = cheap,
/// red = expensive) with a marker. A quick "is this a good moment to buy" reading.
/// </summary>
public sealed class DealMeter : Control
{
    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<DealMeter, double>(nameof(Position), double.NaN);

    public static readonly StyledProperty<IBrush?> MarkerBrushProperty =
        AvaloniaProperty.Register<DealMeter, IBrush?>(nameof(MarkerBrush));

    static DealMeter()
    {
        AffectsRender<DealMeter>(PositionProperty, MarkerBrushProperty);
    }

    public DealMeter()
    {
        IsHitTestVisible = false;
    }

    /// <summary>0 = at the lowest price seen, 1 = at the highest; NaN = not enough history yet.</summary>
    public double Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public IBrush? MarkerBrush
    {
        get => GetValue(MarkerBrushProperty);
        set => SetValue(MarkerBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var (w, h) = (Bounds.Width, Bounds.Height);
        var y = h / 2;
        var track = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#0EA86C"), 0),
                new GradientStop(Color.Parse("#E0B020"), 0.55),
                new GradientStop(Color.Parse("#E0405E"), 1),
            },
        };
        var known = !double.IsNaN(Position);
        context.DrawRectangle(track, null, new Rect(0, y - 2, w, 4), 2, 2);
        if (!known)
        {
            context.DrawRectangle(new SolidColorBrush(Colors.White, 0.65), null, new Rect(0, y - 2, w, 4), 2, 2);
            return;
        }

        var x = Math.Clamp(Position, 0, 1) * (w - 10) + 5;
        context.DrawEllipse(Brushes.White, new Pen(MarkerBrush ?? Brushes.SteelBlue, 2), new Point(x, y), 5, 5);
    }
}
