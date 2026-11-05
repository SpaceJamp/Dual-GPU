using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Drawing = GpuMonitor.App.Controls.Drawing;

namespace GpuMonitor.App.Tray;

/// <summary>Draws the tray icon: the hotter card's temperature on a rounded tile in theme colors.</summary>
public static class TrayIconRenderer
{
    public const int Size = 32;

    /// <param name="tempC">Temperature of the hotter card, or null when no card has one.</param>
    /// <param name="warning">NVML failed or a card stopped reporting: draws "!" instead of the number.</param>
    /// <param name="heatLimit">At or above this temperature the number is drawn in the Danger color.</param>
    public static WindowIcon Render(int? tempC, bool warning, int heatLimit)
    {
        using var bitmap = Draw(tempC, warning, heatLimit);
        var png = new MemoryStream();
        bitmap.Save(png, PngBitmapEncoderOptions.Default);
        png.Position = 0;
        return new WindowIcon(png);
    }

    /// <summary>The text on the icon: "!" when warning, "--" without a temperature, else the number.</summary>
    public static string Text(int? tempC, bool warning) =>
        warning ? "!" : tempC is { } t ? t.ToString(CultureInfo.InvariantCulture) : "--";

    internal static IBrush Brush(string key) => Drawing.Brush(key);

    internal static RenderTargetBitmap Draw(int? tempC, bool warning, int heatLimit)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.DrawRectangle(Brush("Surface"), null, new RoundedRect(new Rect(0, 0, Size, Size), 6));

            var key = warning || tempC >= heatLimit ? "Danger" : tempC is null ? "TextDim" : "Text";
            var text = Text(tempC, warning);
            var formatted = Drawing.Text(text, 20, Brush(key), FontWeight.Bold);
            if (formatted.Width > Size - 2)
                formatted = Drawing.Text(text, 20 * (Size - 2) / formatted.Width, Brush(key), FontWeight.Bold);
            // Grayscale antialiasing: subpixel color fringes look wrong on a small icon.
            using var _ = ctx.PushTextOptions(new TextOptions { TextRenderingMode = TextRenderingMode.Antialias });
            ctx.DrawText(formatted, new Point((Size - formatted.Width) / 2, (Size - formatted.Height) / 2));
        }

        return bitmap;
    }
}
