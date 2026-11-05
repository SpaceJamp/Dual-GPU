using System.Globalization;
using System.Text;
using GpuMonitor.Core.Model;

namespace GpuMonitor.Core.Poll;

public sealed class Recorder(string dir)
{
    private const string Header =
        "time,bus_id,name,load_pct,vram_used_mib,vram_total_mib,temp_c,power_w,power_limit_w,graphics_mhz,memory_mhz,sm_mhz,fan_pct,pstate,throttle,display_active";

    private readonly object _gate = new();
    private StreamWriter? _writer;

    public bool IsRecording { get { lock (_gate) return _writer is not null; } }
    public string? CurrentFile { get; private set; }

    public void Start(DateTimeOffset now)
    {
        lock (_gate)
        {
            StopLocked();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture) + ".csv");
            _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            _writer.WriteLine(Header);
            _writer.Flush();
            CurrentFile = path;
        }
    }

    public void Write(PollResult r)
    {
        lock (_gate)
        {
            if (_writer is null) return;
            var names = new Dictionary<string, string>();
            foreach (var c in r.Cards) names[c.BusId] = c.Name;
            foreach (var x in r.Readings)
            {
                var cells = new[]
                {
                    x.At.ToString("o", CultureInfo.InvariantCulture),
                    x.BusId,
                    names.TryGetValue(x.BusId, out var n) ? n : "",
                    Cell(x.LoadPct), Cell(x.VramUsedMiB), Cell(x.VramTotalMiB), Cell(x.TempC),
                    Cell(x.PowerW), Cell(x.PowerLimitW), Cell(x.GraphicsMHz), Cell(x.MemoryMHz),
                    Cell(x.SmMHz), Cell(x.FanPct), Cell(x.PState),
                    x.Throttle.Ok ? ((ulong)x.Throttle.Value!.Value).ToString(CultureInfo.InvariantCulture) : "",
                    x.DisplayActive.Ok ? (x.DisplayActive.Value!.Value ? "true" : "false") : "",
                };
                _writer.WriteLine(string.Join(',', cells.Select(Quote)));
            }
            _writer.Flush();
        }
    }

    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    private void StopLocked()
    {
        _writer?.Dispose();
        _writer = null;
        CurrentFile = null;
    }

    private static string Cell<T>(Field<T> f) where T : struct, IFormattable =>
        f.Ok ? f.Value!.Value.ToString(null, CultureInfo.InvariantCulture) : "";

    private static string Quote(string s) =>
        s.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
