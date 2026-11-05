using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using GpuMonitor.App.ViewModels;

namespace GpuMonitor.App.Controls;

/// <summary>One card: title, Display badge, load/VRAM/power meters, temperature and load graphs.</summary>
public sealed class CardPanel : Control
{
    private const double Pad = 12;
    private const double MeterHeight = 36;
    private const double MeterGap = 6;
    private const double GraphLabelHeight = 18;

    public static readonly StyledProperty<CardViewModel?> CardProperty =
        AvaloniaProperty.Register<CardPanel, CardViewModel?>(nameof(Card));

    public static readonly StyledProperty<int?> HeatLimitCProperty =
        AvaloniaProperty.Register<CardPanel, int?>(nameof(HeatLimitC));

    private CardViewModel? _subscribed;
    private bool _attached;

    static CardPanel()
    {
        AffectsRender<CardPanel>(CardProperty, HeatLimitCProperty, ThemeVariantScope.ActualThemeVariantProperty);
    }

    public CardViewModel? Card { get => GetValue(CardProperty); set => SetValue(CardProperty, value); }

    public int? HeatLimitC { get => GetValue(HeatLimitCProperty); set => SetValue(HeatLimitCProperty, value); }

    /// <summary>"" normally; <c>No reading since HH:mm:ss</c> (local time) when the card is stale.</summary>
    internal string StatusText
    {
        get
        {
            var card = Card;
            if (card is null || !card.IsStale)
            {
                return "";
            }

            return card.LastSeen is { } seen
                ? "No reading since " + seen.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                : "No reading";
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(360, 340);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CardProperty && _attached)
        {
            Subscribe(Card);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Subscribe(Card);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Subscribe(null);
    }

    private void Subscribe(CardViewModel? card)
    {
        if (ReferenceEquals(card, _subscribed))
        {
            return;
        }

        if (_subscribed is not null)
        {
            _subscribed.PropertyChanged -= OnCardChanged;
        }

        _subscribed = card;
        if (card is not null)
        {
            card.PropertyChanged += OnCardChanged;
        }
    }

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            InvalidateVisual();
        }
        else
        {
            Dispatcher.UIThread.Post(InvalidateVisual);
        }
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(Drawing.Brush(this, "Surface"), new Pen(Drawing.Brush(this, "Grid"), 1), bounds, 6, 6);

        var card = Card;
        if (card is null)
        {
            return;
        }

        var status = StatusText;
        using (context.PushOpacity(card.IsStale ? 0.4 : 1.0))
        {
            DrawBody(context, card, bounds);
        }

        if (status.Length > 0)
        {
            context.DrawText(Drawing.Text(status, 12, Drawing.Brush(this, "Warn")), new Point(Pad, Pad + 24));
        }
    }

    private void DrawBody(DrawingContext context, CardViewModel card, Rect bounds)
    {
        var inner = bounds.Deflate(Pad);
        var accent = Drawing.Brush(this, "Accent");
        var badgeRight = inner.Right;

        if (card.IsDisplay)
        {
            var badge = Drawing.Text("Display", 11, accent);
            var box = new Rect(inner.Right - badge.Width - 12, inner.Top, badge.Width + 12, badge.Height + 4);
            context.DrawRectangle(null, new Pen(accent, 1), box, 4, 4);
            context.DrawText(badge, new Point(box.Left + 6, box.Top + 2));
            badgeRight = box.Left - 8;
        }

        var title = Drawing.Text(card.Name, 15, Drawing.Brush(this, "Text"), FontWeight.SemiBold);
        title.MaxTextWidth = Math.Max(1, badgeRight - inner.Left);
        title.MaxLineCount = 1;
        title.Trimming = TextTrimming.CharacterEllipsis;
        context.DrawText(title, inner.TopLeft);

        var culture = CultureInfo.CurrentCulture;
        var y = inner.Top + 44;
        Rect NextMeter()
        {
            var r = new Rect(inner.Left, y, inner.Width, MeterHeight);
            y += MeterHeight + MeterGap;
            return r;
        }

        Meter.DrawMeter(context, this, NextMeter(), "Load",
            card.LoadPct is { } load ? load.ToString(culture) + " %" : "",
            card.LoadPct ?? 0, 100, card.LoadPct.HasValue);

        var hasVram = card.VramUsedMiB.HasValue && card.VramTotalMiB is > 0;
        Meter.DrawMeter(context, this, NextMeter(), "VRAM",
            hasVram ? string.Format(culture, "{0:N0} / {1:N0} MiB", card.VramUsedMiB, card.VramTotalMiB) : "",
            card.VramUsedMiB ?? 0, card.VramTotalMiB ?? 0, hasVram);

        var powerText = card.PowerW is not { } watts ? ""
            : card.PowerLimitW is { } cap ? string.Format(culture, "{0:0} / {1:0} W", watts, cap)
            : string.Format(culture, "{0:0} W", watts);
        Meter.DrawMeter(context, this, NextMeter(), "Power", powerText,
            card.PowerW ?? 0, card.PowerLimitW ?? 0, card.PowerW.HasValue);

        var graphHeight = Math.Max(24, (inner.Bottom - y - 2 * GraphLabelHeight - MeterGap) / 2);
        var tempLabel = card.TempC is { } t ? "Temperature  " + t.ToString(culture) + " °C" : "Temperature";
        y = DrawGraphBlock(context, tempLabel, card.TempHistory, HeatLimitC, inner, y, graphHeight) + MeterGap;
        DrawGraphBlock(context, "Load history", card.LoadHistory, null, inner, y, graphHeight);
    }

    private double DrawGraphBlock(
        DrawingContext context, string label, IReadOnlyList<double?> points, double? limit,
        Rect inner, double y, double height)
    {
        context.DrawText(Drawing.Text(label, 12, Drawing.Brush(this, "TextDim")), new Point(inner.Left, y));
        var rect = new Rect(inner.Left, y + GraphLabelHeight, inner.Width, height);
        LineGraph.DrawGraph(context, this, rect, points, 0, 100, limit);
        return rect.Bottom;
    }
}
