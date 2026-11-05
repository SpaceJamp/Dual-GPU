using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GpuMonitor.Core.Diagnostics;

public static class ReportRender
{
    private static readonly JsonSerializerOptions PlainOptions = Options(null);

    private static JsonSerializerOptions Options(Redactor? r)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        if (r is not null)
        {
            o.Converters.Add(new RedactingStringConverter(r));
            o.Converters.Add(new RedactingDictionaryFactory(r));
        }
        o.Converters.Add(new FactConverterFactory());
        o.Converters.Add(new TimeSpanConverter());
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    public static int ExitCode(IReadOnlyList<Check> checks) => checks.Any(c => c.Level == CheckLevel.Fail) ? 1 : 0;

    public static string Json(Facts f, IReadOnlyList<Check> checks, bool redact = true, Redactor? redactor = null)
    {
        // Redaction happens per string value and dictionary key, never on the serialized text,
        // so JSON escapes such as \\n and \\t cannot hide a name from the word-boundary rules.
        var r = redact ? redactor ?? Redactor.ForCurrentUser() : null;
        return JsonSerializer.Serialize(new { checks, facts = f }, Options(r));
    }

    public static string Text(Facts f, IReadOnlyList<Check> checks, bool redact = true, Redactor? redactor = null)
    {
        var sb = new StringBuilder();
        sb.Append("GPU Monitor diagnostic report\n");
        sb.Append("Collected ").Append(f.CollectedAt.ToString("o", CultureInfo.InvariantCulture)).Append("\n\n");

        sb.Append("Checks\n");
        foreach (var c in checks)
        {
            sb.Append('[').Append(Label(c.Level)).Append("] ").Append(c.Id).Append(": ").Append(c.Message).Append('\n');
            if (!string.IsNullOrEmpty(c.Hint)) sb.Append("       ").Append(c.Hint).Append('\n');
        }

        Section(sb, "App", s =>
        {
            Line(s, "version", f.App.Version);
            Line(s, "commit", f.App.Commit);
            Line(s, "runtime", f.App.Runtime);
        });
        Section(sb, "Windows", s =>
        {
            Line(s, "version", Show(f.Windows.Version));
            Line(s, "uptime", Show(f.Windows.Uptime));
            Line(s, "hw_sch_mode", Show(f.Windows.HwSchMode));
            Line(s, "game_mode", Show(f.Windows.GameMode));
        });
        Section(sb, "Driver", s =>
        {
            Line(s, "init_code", f.Driver.InitCode.ToString());
            Line(s, "nvml_path", f.Driver.NvmlPath ?? "none");
            Line(s, "load_error", f.Driver.LoadError ?? "none");
            Line(s, "driver_version", Show(f.Driver.DriverVersion));
            Line(s, "nvml_version", Show(f.Driver.NvmlVersion));
            Line(s, "cuda_version", Show(f.Driver.CudaVersion));
        });
        foreach (var (c, i) in f.Cards.Select((c, i) => (c, i)))
        {
            Section(sb, $"Card {i} ({c.BusId})", s =>
            {
                Line(s, "name", Show(c.Name));
                Line(s, "uuid", Show(c.Uuid));
                Line(s, "vbios", Show(c.Vbios));
                Line(s, "pcie_gen_cur", Show(c.PcieGenCur));
                Line(s, "pcie_gen_max", Show(c.PcieGenMax));
                Line(s, "pcie_width_cur", Show(c.PcieWidthCur));
                Line(s, "pcie_width_max", Show(c.PcieWidthMax));
                Line(s, "display_active", Show(c.DisplayActive));
                Line(s, "temp_c", Show(c.TempC));
                Line(s, "temp_slowdown_c", Show(c.TempSlowdownC));
                Line(s, "temp_shutdown_c", Show(c.TempShutdownC));
                Line(s, "power_w", Show(c.PowerW));
                Line(s, "power_limit_w", Show(c.PowerLimitW));
                Line(s, "power_default_w", Show(c.PowerDefaultW));
                Line(s, "graphics_mhz", Show(c.GraphicsMHz));
                Line(s, "graphics_max_mhz", Show(c.GraphicsMaxMHz));
                Line(s, "memory_mhz", Show(c.MemoryMHz));
                Line(s, "memory_max_mhz", Show(c.MemoryMaxMHz));
                Line(s, "sm_mhz", Show(c.SmMHz));
                Line(s, "sm_max_mhz", Show(c.SmMaxMHz));
                Line(s, "fan_pct", Show(c.FanPct));
                Line(s, "p_state", Show(c.PState));
                Line(s, "throttle", Show(c.Throttle));
                Line(s, "vram_used_mib", Show(c.VramUsedMiB));
                Line(s, "vram_total_mib", Show(c.VramTotalMiB));
                if (!c.Processes.Ok) Line(s, "processes", c.Processes.Error ?? "");
                else
                {
                    Line(s, "processes", c.Processes.Value!.Count == 0 ? "none" : "");
                    foreach (var p in c.Processes.Value!)
                        s.Append("  ").Append(p.Pid).Append(' ').Append(p.Name).Append(' ')
                            .Append(p.UsedMiB is { } m ? m.ToString(CultureInfo.InvariantCulture) + " MiB" : "unknown").Append('\n');
                }
            });
        }
        Section(sb, "Windows adapters", s =>
        {
            if (!f.Wmi.Ok) { s.Append(f.Wmi.Error).Append('\n'); return; }
            if (f.Wmi.Value!.Count == 0) s.Append("none\n");
            foreach (var a in f.Wmi.Value!)
                s.Append(a.Name).Append(" | driver ").Append(a.DriverVersion).Append(" | ").Append(a.PnpDeviceId)
                    .Append(" | ").Append(a.Status).Append('\n');
        });
        Section(sb, "nvidia-smi", s =>
        {
            if (!f.NvidiaSmi.Ok) { s.Append(f.NvidiaSmi.Error).Append('\n'); return; }
            if (string.IsNullOrEmpty(f.NvidiaSmi.Value)) { s.Append("none\n"); return; }
            foreach (var line in f.NvidiaSmi.Value.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
                s.Append("  ").Append(line).Append('\n');
        });
        Section(sb, "GPU preferences", s =>
        {
            if (!f.GpuPreferences.Ok) { s.Append(f.GpuPreferences.Error).Append('\n'); return; }
            if (f.GpuPreferences.Value!.Count == 0) s.Append("none\n");
            foreach (var kv in f.GpuPreferences.Value!) Line(s, kv.Key, kv.Value);
        });
        Section(sb, "Event log", s =>
        {
            if (!f.EventLog.Ok) { s.Append(f.EventLog.Error).Append('\n'); return; }
            if (f.EventLog.Value!.Count == 0) s.Append("none\n");
            foreach (var e in f.EventLog.Value!)
                s.Append(e.At.ToString("o", CultureInfo.InvariantCulture)).Append(' ').Append(e.Provider)
                    .Append(" id=").Append(e.Id).Append(' ').Append(e.Level).Append(": ").Append(e.Message).Append('\n');
        });
        Section(sb, "State", s =>
        {
            var st = f.State;
            Line(s, "settings_status", st.SettingsStatus.ToString());
            Line(s, "settings", JsonSerializer.Serialize(st.Settings, PlainOptions));
            Line(s, "startup_value", st.StartupValue ?? "none");
            Line(s, "log_writable", st.LogWritable ? "true" : "false");
            s.Append("alerts:").Append(st.Alerts.Count == 0 ? " none" : "").Append('\n');
            foreach (var a in st.Alerts)
                s.Append("  ").Append(a.At.ToString("o", CultureInfo.InvariantCulture)).Append(' ').Append(a.Kind)
                    .Append(' ').Append(a.BusId).Append(' ').Append(a.CardName).Append(' ').Append(a.Message).Append('\n');
            s.Append("timings:").Append(st.Timings.Count == 0 ? " none" : "").Append('\n');
            foreach (var kv in st.Timings)
                s.Append("  ").Append(kv.Key).Append(": ").Append(string.Create(CultureInfo.InvariantCulture,
                    $"count={kv.Value.Count} min={kv.Value.MinMs:0.##} avg={kv.Value.AvgMs:0.##} max={kv.Value.MaxMs:0.##} p99={kv.Value.P99Ms:0.##} ms")).Append('\n');
            s.Append("log tail:").Append(st.LogTail.Count == 0 ? " none" : "").Append('\n');
            foreach (var l in st.LogTail) s.Append("  ").Append(l).Append('\n');
        });

        return Finish(sb.ToString(), redact, redactor);
    }

    private static string Finish(string s, bool redact, Redactor? redactor) =>
        redact ? (redactor ?? Redactor.ForCurrentUser()).Apply(s) : s;

    private static string Label(CheckLevel l) => l switch
    {
        CheckLevel.Pass => "PASS", CheckLevel.Info => "INFO", CheckLevel.Warn => "WARN", _ => "FAIL",
    };

    private static void Section(StringBuilder sb, string title, Action<StringBuilder> body)
    {
        sb.Append('\n').Append(title).Append('\n');
        body(sb);
    }

    private static void Line(StringBuilder sb, string key, string value) =>
        sb.Append(key).Append(':').Append(value.Length == 0 ? "" : " " + value).Append('\n');

    private static string Show<T>(Fact<T> f)
    {
        if (!f.Ok) return f.Error ?? "error";
        return f.Value switch
        {
            null => "none",
            bool b => b ? "true" : "false",
            TimeSpan t => TimeSpanConverter.Format(t),
            IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
            var v => v.ToString() ?? "",
        };
    }

    private sealed class TimeSpanConverter : JsonConverter<TimeSpan>
    {
        public static string Format(TimeSpan t) => t.ToString("c", CultureInfo.InvariantCulture);
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            TimeSpan.Parse(reader.GetString()!, CultureInfo.InvariantCulture);
        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Format(value));
    }

    private sealed class FactConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Fact<>);

        public override JsonConverter CreateConverter(Type t, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(FactConverter<>).MakeGenericType(t.GetGenericArguments()[0]))!;
    }

    private sealed class FactConverter<T> : JsonConverter<Fact<T>>
    {
        public override Fact<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, Fact<T> value, JsonSerializerOptions options)
        {
            if (!value.Ok) JsonSerializer.Serialize(writer, value.Error, typeof(string), options);
            else JsonSerializer.Serialize(writer, value.Value, typeof(T), options);
        }
    }

    private sealed class RedactingStringConverter(Redactor r) : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(r.Apply(value));
    }

    private sealed class RedactingDictionaryFactory(Redactor r) : JsonConverterFactory
    {
        public override bool CanConvert(Type t) => t.IsGenericType
            && t.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) && t.GetGenericArguments()[0] == typeof(string);

        public override JsonConverter CreateConverter(Type t, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(DictConverter<>).MakeGenericType(t.GetGenericArguments()[1]), r)!;
    }

    private sealed class DictConverter<TValue>(Redactor r) : JsonConverter<IReadOnlyDictionary<string, TValue>>
    {
        public override IReadOnlyDictionary<string, TValue> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, TValue> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in value)
            {
                var name = r.Apply(kv.Key);
                for (var n = 2; !seen.Add(name); n++) name = $"{r.Apply(kv.Key)} ({n})";
                writer.WritePropertyName(name);
                JsonSerializer.Serialize(writer, kv.Value, typeof(TValue), options);
            }
            writer.WriteEndObject();
        }
    }
}
