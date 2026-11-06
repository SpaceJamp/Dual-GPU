using System;

namespace GpuMonitor.Core.Model;

[Flags]
public enum ThrottleReasons : ulong
{
    None = 0,
    GpuIdle = 0x1,
    ApplicationsClocksSetting = 0x2,
    SwPowerCap = 0x4,
    HwSlowdown = 0x8,
    SyncBoost = 0x10,
    SwThermalSlowdown = 0x20,
    HwThermalSlowdown = 0x40,
    HwPowerBrakeSlowdown = 0x80,
    DisplayClockSetting = 0x100,
    Reliability = 0x400,
}

public static class ThrottleReasonsExtensions
{
    public static bool IsThermal(this ThrottleReasons r) =>
        (r & (ThrottleReasons.SwThermalSlowdown | ThrottleReasons.HwThermalSlowdown | ThrottleReasons.HwSlowdown)) != 0;
}
