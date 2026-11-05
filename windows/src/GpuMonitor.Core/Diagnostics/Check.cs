namespace GpuMonitor.Core.Diagnostics;

public enum CheckLevel { Pass, Info, Warn, Fail }

public sealed record Check(CheckLevel Level, string Id, string Message, string Hint);
