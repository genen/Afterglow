using System.Globalization;
using System.Text.Json.Serialization;

namespace Afterglow.Core;

public sealed record ProcessRow(int Pid, long Started, string Name, string User, int Session,
    double? Cpu, double? Memory, double? ReadRate, double? WriteRate, string Path, string Status,
    int Parent = 0, int Threads = 0, long CpuTime = 0, int Priority = 0, string? Arch = null, bool Windowed = false, double? Gpu = null)
{
    [JsonIgnore] public string Identity => $"{Pid}:{Started}";
    [JsonIgnore] public string CpuText => Format.Percent(Cpu);
    [JsonIgnore] public string MemoryText => Format.Bytes(Memory);
    [JsonIgnore] public string ReadText => Format.Rate(ReadRate);
    [JsonIgnore] public string WriteText => Format.Rate(WriteRate);
    [JsonIgnore] public string GpuText => Gpu is null ? "—" : Format.Percent(Gpu);
    [JsonIgnore] public string ArchText => string.IsNullOrEmpty(Arch) ? "Unavailable" : Arch;
}

public sealed record DeviceSample(string Id, string Name, double? Receive, double? Send, double? Read = null, double? Write = null, double? Busy = null);

public sealed record Sample(DateTimeOffset Time, double? Cpu, double? Kernel, double? Memory,
    double? Total, double? Available, double? Receive, double? Send, double? DiskRead,
    double? DiskWrite, string PowerSource, double? BatteryPercent, string TopCpu,
    DeviceSample[] Interfaces, double[]? Cores = null, DeviceSample[]? Disks = null, double? Gpu = null,
    string? GpuName = null, double? Gpu3D = null, double? GpuCopy = null, double? GpuCompute = null,
    double? GpuDecode = null, double? GpuEncode = null, double? GpuMemory = null, double? Cached = null,
    double? Committed = null, double? CommitLimit = null, double? PageFile = null, int Pressure = 0,
    double? DiskBusy = null, double? Temperature = null, double? TempMin = null, double[]? Temps = null,
    string[]? TempNames = null, double? BatteryWatts = null, double? Frequency = null, string? TopMemory = null, double? PackageWatts = null);

public sealed record Frame(Sample Sample, ProcessRow[] Processes);
public sealed record InfoRow(string Name, string Detail, string Value, string Path = "");
public sealed record Trace(int Version, string App, string Platform, Frame[] Frames);

public static class CounterMath
{
    public static double? Rate(ulong current, ulong previous, double seconds) =>
        seconds > 0 && double.IsFinite(seconds) && current >= previous ? (current - previous) / seconds : null;

    // Windows kernel time includes idle time.
    public static (double? Cpu, double? Kernel) Cpu(ulong idle, ulong kernel, ulong user,
        ulong oldIdle, ulong oldKernel, ulong oldUser)
    {
        if (idle < oldIdle || kernel < oldKernel || user < oldUser) return (null, null);
        var i = (double)(idle - oldIdle); var k = (double)(kernel - oldKernel); var u = (double)(user - oldUser);
        var total = k + u;
        if (total <= 0 || i > k) return (null, null);
        return (Math.Clamp((total - i) / total * 100, 0, 100), Math.Clamp((k - i) / total * 100, 0, 100));
    }

    public static bool CanTerminate(ProcessRow row, long actualStart, int selfPid, bool replay) =>
        !replay && row.Pid > 4 && row.Pid != selfPid && row.Started > 0 && row.Started == actualStart;
}

public static class Format
{
    public static string Percent(double? n) => n is { } v && double.IsFinite(v) ? $"{v:0.0}%" : "Unavailable";
    public static string Watts(double? n)
    {
        if (n is not { } v || !double.IsFinite(v)) return "Unavailable";
        return Math.Abs(v) < 10 ? $"{v:0.0} W" : $"{v:0} W";
    }
    public static string Celsius(double? n) => n is { } v && double.IsFinite(v) ? $"{v:0.0} °C" : "Unavailable";
    public static string Bytes(double? n)
    {
        if (n is not { } v || !double.IsFinite(v) || v < 0) return "Unavailable";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }
    public static string Rate(double? n) => n is null ? "Unavailable" : Bytes(n) + "/s";
    public static string ZeroRate(double? n)
    {
        if (n is not { } v || !double.IsFinite(v)) return "Unavailable";
        return Math.Abs(v) < 512 ? "Zero KB/s" : Rate(n);
    }
    public static string Clock(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "Unavailable";
        var span = TimeSpan.FromSeconds(seconds);
        return $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}";
    }
    public static string CpuTime(long hundredNanoseconds)
    {
        if (hundredNanoseconds < 0) return "Unavailable";
        var seconds = hundredNanoseconds / 10_000_000;
        return $"{seconds / 60}:{seconds % 60:00}";
    }
    public static string CsvField(string value)
    {
        if (value.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' || value.StartsWith('\t') || value.StartsWith('\r')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    public static string Csv(IEnumerable<ProcessRow> rows) =>
        "PID,Name,User,CPU percent,Resident bytes,Status,Path,Read bytes/s,Write bytes/s,GPU percent,Parent\r\n" +
        string.Join("\r\n", rows.Select(r => string.Join(',', new[]
        {
            r.Pid.ToString(CultureInfo.InvariantCulture), r.Name, r.User,
            r.Cpu?.ToString(CultureInfo.InvariantCulture) ?? "", r.Memory?.ToString(CultureInfo.InvariantCulture) ?? "", r.Status, r.Path,
            r.ReadRate?.ToString(CultureInfo.InvariantCulture) ?? "", r.WriteRate?.ToString(CultureInfo.InvariantCulture) ?? "",
            r.Gpu?.ToString(CultureInfo.InvariantCulture) ?? "", r.Parent.ToString(CultureInfo.InvariantCulture)
        }.Select(CsvField))));
}

public sealed class RecordingBuffer
{
    public const int MaxFrames = 3600;
    public const int MaxProcesses = 250_000;
    private readonly List<Frame> frames = [];
    private int processes;
    public IReadOnlyList<Frame> Frames => frames;
    public bool Add(Frame frame)
    {
        if (frames.Count >= MaxFrames || processes + frame.Processes.Length > MaxProcesses) return false;
        frames.Add(frame); processes += frame.Processes.Length; return true;
    }
    public void Clear() { frames.Clear(); processes = 0; }
}
