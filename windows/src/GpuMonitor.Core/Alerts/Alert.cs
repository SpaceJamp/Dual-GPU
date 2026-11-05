using System;
using System.Globalization;

namespace GpuMonitor.Core.Alerts;

public enum AlertKind { Hot, HotCleared, LowVram, LowVramCleared, CardMissing, CardBack }

public sealed record Alert(AlertKind Kind, string BusId, string CardName, double? Value, DateTimeOffset At)
{
    /// <summary>Limit that was in force when the alert was raised. Used only for message text.</summary>
    public int Limit { get; init; }

    public bool Notify => Kind is AlertKind.Hot or AlertKind.LowVram or AlertKind.CardMissing or AlertKind.CardBack;

    public string Title => Kind switch
    {
        AlertKind.Hot => $"{CardName} is hot",
        AlertKind.HotCleared => $"{CardName} cooled down",
        AlertKind.LowVram => $"{CardName} is low on VRAM",
        AlertKind.LowVramCleared => $"{CardName} VRAM recovered",
        AlertKind.CardMissing => $"{CardName} stopped reporting",
        _ => $"{CardName} is reporting again",
    };

    public string Message
    {
        get
        {
            var v = Value ?? 0;
            var inv = CultureInfo.InvariantCulture;
            return Kind switch
            {
                AlertKind.Hot => string.Create(inv, $"{v:0}°C, limit {Limit}°C"),
                AlertKind.HotCleared => string.Create(inv, $"{v:0}°C, below {Limit - AlertRules.Margin}°C"),
                AlertKind.LowVram => string.Create(inv, $"{v:0}% used, limit {Limit}%"),
                AlertKind.LowVramCleared => string.Create(inv, $"{v:0}% used"),
                AlertKind.CardMissing => "No reading for 5 seconds",
                _ => "Readings resumed",
            };
        }
    }
}
