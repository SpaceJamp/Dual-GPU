using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GpuMonitor.App.Controls;

/// <summary>History line graph. Null points break the line; an optional dashed limit line.</summary>
public sealed class LineGraph : Control
{
    public static readonly StyledProperty<IReadOnlyList<double?>> PointsProperty =
        AvaloniaProperty.Register<LineGraph, IReadOnlyList<double?>>(nameof(Points), Array.Empty<double?>());

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<LineGraph, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<LineGraph, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<double?> LimitProperty =
        AvaloniaProperty.Register<LineGraph, double?>(nameof(Limit));

    static LineGraph()
    {
        AffectsRender<LineGraph>(
            PointsProperty, MinimumProperty, MaximumProperty, LimitProperty, ThemeVariantScope.ActualThemeVariantProperty);
    }

    public IReadOnlyList<double?> Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }

    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public double? Limit { get => GetValue(LimitProperty); set => SetValue(LimitProperty, value); }

    /// <summary>Segments drawn at the last render (runs of 2+ points and isolated points).</summary>
    internal int LastSegmentCount { get; private set; }

    protected override Size MeasureOverride(Size availableSize) => new(200, 80);

    public override void Render(DrawingContext context)
    {
        LastSegmentCount = DrawGraph(context, this, new Rect(Bounds.Size), Points, Minimum, Maximum, Limit);
    }

    /// <summary>Draws a graph into <paramref name="rect"/> and returns the number of segments drawn.</summary>
    internal static int DrawGraph(
        DrawingContext context, Control owner, Rect rect, IReadOnlyList<double?>? points,
        double minimum, double maximum, double? limit)
    {
        var grid = new Pen(Drawing.Brush(owner, "Grid"), 1);
        context.DrawRectangle(null, grid, rect);
        var mid = rect.Top + rect.Height / 2;
        context.DrawLine(grid, new Point(rect.Left, mid), new Point(rect.Right, mid));

        var range = maximum > minimum ? maximum - minimum : 1;
        double Y(double v) => rect.Bottom - (Math.Clamp(v, minimum, Math.Max(minimum, maximum)) - minimum) / range * rect.Height;

        if (limit is { } l && double.IsFinite(l))
        {
            var dashed = new Pen(Drawing.Brush(owner, "Danger"), 1, DashStyle.Dash);
            var y = Y(l);
            context.DrawLine(dashed, new Point(rect.Left, y), new Point(rect.Right, y));
        }

        if (points is null || points.Count == 0)
        {
            return 0;
        }

        var accent = Drawing.Brush(owner, "Accent");
        var line = new Pen(accent, 1.5, lineJoin: PenLineJoin.Round);
        var step = points.Count > 1 ? rect.Width / (points.Count - 1) : 0;
        var run = new List<Point>();
        var segments = 0;

        void Flush()
        {
            if (run.Count == 1)
            {
                context.DrawEllipse(accent, null, run[0], 1.5, 1.5);
                segments++;
            }
            else if (run.Count > 1)
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(run[0], false);
                    for (var i = 1; i < run.Count; i++)
                    {
                        g.LineTo(run[i]);
                    }

                    g.EndFigure(false);
                }

                context.DrawGeometry(null, line, geometry);
                segments++;
            }

            run.Clear();
        }

        for (var i = 0; i < points.Count; i++)
        {
            if (points[i] is { } v && double.IsFinite(v))
            {
                run.Add(new Point(rect.Left + i * step, Y(v)));
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return segments;
    }
}
