using System;
using System.Text;

namespace GpuMonitor.Core.Nvml;

/// <summary>nvmlReturn_t from nvml.h.</summary>
public enum NvmlReturn
{
    Success = 0,
    Uninitialized = 1,
    InvalidArgument = 2,
    NotSupported = 3,
    NoPermission = 4,
    AlreadyInitialized = 5,
    NotFound = 6,
    InsufficientSize = 7,
    InsufficientPower = 8,
    DriverNotLoaded = 9,
    Timeout = 10,
    IrqIssue = 11,
    LibraryNotFound = 12,
    FunctionNotFound = 13,
    CorruptedInforom = 14,
    GpuIsLost = 15,
    ResetRequired = 16,
    OperatingSystem = 17,
    LibRmVersionMismatch = 18,
    InUse = 19,
    Memory = 20,
    NoData = 21,
    VgpuEccNotSupported = 22,
    InsufficientResources = 23,
    FreqNotSupported = 24,
    ArgumentVersionMismatch = 25,
    Deprecated = 26,
    NotReady = 27,
    GpuNotFound = 28,
    InvalidState = 29,
    ResetTypeNotSupported = 30,
    Unknown = 999,
}

public static class NvmlReturnExtensions
{
    public static string NvmlReturnName(this NvmlReturn r)
    {
        if (r == NvmlReturn.Success) return "NVML_SUCCESS";
        if (!Enum.IsDefined(r)) return $"NVML_ERROR_{(int)r}";
        var name = r.ToString();
        var sb = new StringBuilder("NVML_ERROR_");
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                sb.Append('_');
            sb.Append(char.ToUpperInvariant(name[i]));
        }
        return sb.ToString();
    }
}
