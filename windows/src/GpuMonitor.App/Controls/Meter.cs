using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GpuMonitor.App.Controls;

/// <summary>Horizontal bar with a label on the left and a value text on the right, drawn above the bar.</summary>
public sealed class Meter : Control
{
    internal const string NoValueText = "n/a";

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<Meter, double>(nameof(Value));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<Meter, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<Meter, string>(nameof(Label), "");

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<Meter, string>(nameof(Text), "");

    public static readonly StyledProperty<bool> HasValueProperty =
        AvaloniaProperty.Register<Meter, bool>(nameof(HasValue));

    static Meter()
    {
        AffectsRender<Meter>(
            ValueProperty, MaximumProperty, LabelProperty, TextProperty, HasValueProperty, ThemeVariantScope.ActualThemeVariantProperty);
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public bool HasValue { get => GetValue(HasValueProperty); set => SetValue(HasValueProperty, value); }

    /// <summary>Fill brush picked at the last render; transparent when there was no value.</summary>
    internal IBrush CurrentFill { get; private set; } = Brushes.Transparent;

    /// <summary>The value text as drawn: <see cref="Text"/>, or <c>n/a</c> without a value.</summary>
    internal string ShownText => HasValue ? Text : NoValueText;

    protected override Size MeasureOverride(Size availableSize) => new(200, 36);

    public override void Render(DrawingContext context)
    {
        CurrentFill = DrawMeter(context, this, new Rect(Bounds.Size), Label, Text, Value, Maximum, HasValue);
    }

    /// <summary>Theme key of the fill brush for a value at <paramref name="ratio"/> of its maximum.</summary>
    internal static string FillKey(double ratio) => ratio < 0.80 ? "Accent" : ratio < 0.95 ? "Warn" : "Danger";

    /// <summary>Draws a meter into <paramref name="rect"/> and returns the fill brush used.</summary>
    internal static IBrush DrawMeter(
        DrawingContext context, Control owner, Rect rect, string label, string text,
        double value, double maximum, bool hasValue)
    {
        var dim = Drawing.Brush(owner, "TextDim");
        var labelText = Drawing.Text(label, 12, dim);
        var valueText = Drawing.Text(hasValue ? text : NoValueText, 12, hasValue ? Drawing.Brush(owner, "Text") : dim);
        context.DrawText(labelText, rect.TopLeft);
        context.DrawText(valueText, new Point(rect.Right - valueText.Width, rect.Top));

        var barHeight = Math.Clamp(rect.Height - 20, 4, 12);
        var track = new Rect(rect.Left, rect.Bottom - barHeight, rect.Width, barHeight);
        context.DrawRectangle(Drawing.Brush(owner, "Grid"), null, track, 3, 3);

        if (!hasValue)
        {
            return Brushes.Transparent;
        }

        var ratio = maximum > 0 && double.IsFinite(value) ? value / maximum : 0;
        var fill = Drawing.Brush(owner, FillKey(ratio));
        var width = track.Width * Math.Clamp(ratio, 0, 1);
        if (width > 0)
        {
            context.DrawRectangle(fill, null, track.WithWidth(width), 3, 3);
        }

        return fill;
    }
}
