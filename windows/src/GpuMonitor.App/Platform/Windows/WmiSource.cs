using System.Management;
using System.Runtime.Versioning;
using GpuMonitor.Core.Diagnostics;

namespace GpuMonitor.App.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WmiSource : IWmiSource
{
    private const string Query = "SELECT Name, DriverVersion, PNPDeviceID, Status FROM Win32_VideoController";

    public IReadOnlyList<WmiAdapter> Adapters()
    {
        var list = new List<WmiAdapter>();
        using var searcher = new ManagementObjectSearcher(Query);
        using var results = searcher.Get();
        foreach (var item in results)
        {
            using (item)
            {
                list.Add(new WmiAdapter(
                    Text(item, "Name"),
                    Text(item, "DriverVersion"),
                    Text(item, "PNPDeviceID"),
                    Text(item, "Status")));
            }
        }
        return list;
    }

    private static string Text(ManagementBaseObject item, string property) =>
        item[property]?.ToString() ?? "";
}
