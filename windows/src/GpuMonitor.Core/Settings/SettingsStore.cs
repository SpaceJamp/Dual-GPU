using System.Globalization;
using System.Text.Json;
using GpuMonitor.Core.Logging;

namespace GpuMonitor.Core.Settings;

public enum SettingsStatus { Loaded, Missing, Invalid }

public sealed record LoadResult(AppSettings Settings, SettingsStatus Status, string? Error);

public sealed class SettingsStore(string path, AppLog log)
{
    private const string Source = "settings";

    public LoadResult Load()
    {
        if (!File.Exists(path)) return new LoadResult(new AppSettings(), SettingsStatus.Missing, null);

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn(Source, $"Could not read the settings file, leaving it untouched: {ex.Message}", ex);
            return new LoadResult(new AppSettings(), SettingsStatus.Invalid, ex.Message);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            log.Error(Source, $"Settings file is not valid JSON: {ex.Message}", ex);
            MoveAside();
            return new LoadResult(new AppSettings(), SettingsStatus.Invalid, ex.Message);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                const string msg = "Settings file does not contain a JSON object.";
                log.Error(Source, msg);
                MoveAside();
                return new LoadResult(new AppSettings(), SettingsStatus.Invalid, msg);
            }

            var parsed = Parse(doc.RootElement);
            var clamped = parsed.Clamp();
            Report(nameof(AppSettings.RefreshSeconds), parsed.RefreshSeconds, clamped.RefreshSeconds);
            Report(nameof(AppSettings.HeatLimitC), parsed.HeatLimitC, clamped.HeatLimitC);
            Report(nameof(AppSettings.VramLimitPct), parsed.VramLimitPct, clamped.VramLimitPct);
            if (parsed.ExpectedDisplayCard != clamped.ExpectedDisplayCard)
                log.Warn(Source, $"ExpectedDisplayCard is blank; using \"{clamped.ExpectedDisplayCard}\".");
            return new LoadResult(clamped, SettingsStatus.Loaded, null);
        }
    }

    public void Save(AppSettings s)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber(nameof(AppSettings.RefreshSeconds), s.RefreshSeconds);
            w.WriteNumber(nameof(AppSettings.HeatLimitC), s.HeatLimitC);
            w.WriteNumber(nameof(AppSettings.VramLimitPct), s.VramLimitPct);
            w.WriteString(nameof(AppSettings.LogLevel), s.LogLevel.ToString());
            w.WriteBoolean(nameof(AppSettings.LogJson), s.LogJson);
            w.WriteBoolean(nameof(AppSettings.StartWithWindows), s.StartWithWindows);
            w.WriteString(nameof(AppSettings.ExpectedDisplayCard), s.ExpectedDisplayCard);
            w.WriteEndObject();
        }
        File.Move(tmp, path, overwrite: true);
    }

    private AppSettings Parse(JsonElement root)
    {
        var d = new AppSettings();
        return d with
        {
            RefreshSeconds = Read(root, nameof(AppSettings.RefreshSeconds), JsonValueKind.Number,
                e => e.TryGetDouble(out var v) ? v : null, d.RefreshSeconds),
            HeatLimitC = Read(root, nameof(AppSettings.HeatLimitC), JsonValueKind.Number, ReadInt, d.HeatLimitC),
            VramLimitPct = Read(root, nameof(AppSettings.VramLimitPct), JsonValueKind.Number, ReadInt, d.VramLimitPct),
            LogLevel = Read(root, nameof(AppSettings.LogLevel), JsonValueKind.String,
                e => Enum.TryParse<LogLevel>(e.GetString(), false, out var v) && Enum.IsDefined(v) ? v : null, d.LogLevel),
            LogJson = ReadBool(root, nameof(AppSettings.LogJson), d.LogJson),
            StartWithWindows = ReadBool(root, nameof(AppSettings.StartWithWindows), d.StartWithWindows),
            ExpectedDisplayCard = Read(root, nameof(AppSettings.ExpectedDisplayCard), JsonValueKind.String,
                e => e.GetString(), d.ExpectedDisplayCard) ?? d.ExpectedDisplayCard,
        };
    }

    private static int? ReadInt(JsonElement e) => e.TryGetInt32(out var v) ? v : null;

    private bool ReadBool(JsonElement root, string name, bool fallback)
    {
        if (!root.TryGetProperty(name, out var e)) return fallback;
        if (e.ValueKind is JsonValueKind.True) return true;
        if (e.ValueKind is JsonValueKind.False) return false;
        log.Warn(Source, $"{name} must be true or false; using default {fallback}.");
        return fallback;
    }

    private T Read<T>(JsonElement root, string name, JsonValueKind kind, Func<JsonElement, T?> convert, T fallback)
        where T : struct
    {
        if (!root.TryGetProperty(name, out var e)) return fallback;
        if (e.ValueKind == kind && convert(e) is { } v) return v;
        log.Warn(Source, $"{name} has an invalid value; using default {Format(fallback)}.");
        return fallback;
    }

    private string? Read(JsonElement root, string name, JsonValueKind kind, Func<JsonElement, string?> convert, string fallback)
    {
        if (!root.TryGetProperty(name, out var e)) return fallback;
        if (e.ValueKind == kind) return convert(e);
        log.Warn(Source, $"{name} has an invalid value; using default \"{fallback}\".");
        return fallback;
    }

    private void Report<T>(string name, T before, T after) where T : IEquatable<T>
    {
        if (!before.Equals(after))
            log.Warn(Source, $"{name} {Format(before)} is out of range; using {Format(after)}.");
    }

    private static string Format<T>(T v) => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";

    private void MoveAside()
    {
        try { File.Move(path, path + ".bad", overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn(Source, $"Could not rename the bad settings file: {ex.Message}", ex);
        }
    }
}
