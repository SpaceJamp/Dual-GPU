using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace GpuMonitor.App.Controls;

/// <summary>Theme brush lookup and text helpers shared by the drawn controls.</summary>
internal static class Drawing
{
    private static readonly Dictionary<string, IBrush> Fallbacks = new()
    {
        ["Background"] = new ImmutableSolidColorBrush(Color.Parse("#F7F7F8")),
        ["Surface"] = Brushes.White,
        ["Text"] = new ImmutableSolidColorBrush(Color.Parse("#1B1B1F")),
        ["TextDim"] = new ImmutableSolidColorBrush(Color.Parse("#5C5F66")),
        ["Accent"] = new ImmutableSolidColorBrush(Color.Parse("#1F6FEB")),
        ["Warn"] = new ImmutableSolidColorBrush(Color.Parse("#C27803")),
        ["Danger"] = new ImmutableSolidColorBrush(Color.Parse("#C62828")),
        ["Grid"] = new ImmutableSolidColorBrush(Color.Parse("#DADCE0")),
    };

    /// <summary>Theme brush for <paramref name="key"/>, or a fixed brush when the resource is missing.</summary>
    public static IBrush Brush(Control owner, string key)
    {
        if (owner.TryFindResource(key, owner.ActualThemeVariant, out var value) && value is IBrush brush)
        {
            return brush;
        }

        return Fallbacks.TryGetValue(key, out var fallback) ? fallback : Brushes.Gray;
    }

    public static FormattedText Text(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight),
            size,
            brush);
}
