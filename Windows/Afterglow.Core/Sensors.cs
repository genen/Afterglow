using System.Globalization;
using System.Text.RegularExpressions;

namespace Afterglow.Core;

public static class Sensors
{
    public const double DiskRateCap = 16d * 1024 * 1024 * 1024;

    public static double Energy(double? cpu, double? memory, double? total) =>
        Math.Max(0, cpu ?? 0) * 0.85 + ((memory ?? 0) / Math.Max(total ?? 0, 1) * 100) * 0.15;

    // Available physical memory is the Windows signal that lines up with the Mac thresholds.
    // Memory pressure here is Normal / Warning / Critical. Urgent (2) is accepted from Mac recordings.
    public static int Pressure(double? available, double? total)
    {
        if (total is not > 0 || available is not { } free || !double.IsFinite(free) || free < 0) return 0;
        var percent = free / total.Value * 100;
        if (percent < 15) return 4;
        if (percent < 40) return 1;
        return 0;
    }

    public static string PressureName(int level) => level switch
    {
        4 => "Critical",
        2 => "Urgent",
        1 => "Warning",
        _ => "Normal"
    };

    public static double? CapRate(double value) =>
        double.IsFinite(value) && value >= 0 && value <= DiskRateCap ? value : null;

    public static int NetworkPort(uint value) => (int)(((value & 0xFF) << 8) | ((value & 0xFF00) >> 8));

    public readonly record struct CoreReading(string Name, double Total, double? Privileged);

    public static CoreReading[] Cores(IEnumerable<(string Name, double Value)> total, IEnumerable<(string Name, double Value)> privileged)
    {
        var kernel = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in privileged)
            if (LogicalIndex(item.Name) is not null && item.Value is >= 0 and <= 100) kernel[item.Name] = item.Value;
        return total
            .Select(item => (item, Index: LogicalIndex(item.Name)))
            .Where(item => item.Index is not null && item.item.Value is >= 0 and <= 100)
            .OrderBy(item => item.Index!.Value.Group).ThenBy(item => item.Index!.Value.Processor)
            .Select(item => new CoreReading(item.item.Name, item.item.Value, kernel.TryGetValue(item.item.Name, out var value) ? value : null))
            .ToArray();
    }

    public static (int Group, int Processor)? LogicalIndex(string name)
    {
        if (name.Contains("_Total", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = name.Split(',');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var group)) return null;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var processor)) return null;
        return (group, processor);
    }

    public readonly record struct DiskReading(string Name, double? Read, double? Write, double? Busy);

    public static (double? Read, double? Write, double? Busy, DiskReading[] Disks) DisksFrom(
        IEnumerable<(string Name, double Value)> reads,
        IEnumerable<(string Name, double Value)> writes,
        IEnumerable<(string Name, double Value)> idles)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var readMap = Map(reads, names);
        var writeMap = Map(writes, names);
        var idleMap = Map(idles, names);
        var disks = new List<DiskReading>();
        double? readSum = null, writeSum = null, busyMax = null;
        foreach (var name in names)
        {
            if (name.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;
            double? read = readMap.TryGetValue(name, out var r) ? CapRate(r) : null;
            double? write = writeMap.TryGetValue(name, out var w) ? CapRate(w) : null;
            double? busy = null;
            if (idleMap.TryGetValue(name, out var idle) && idle is >= 0 and <= 100) busy = 100 - idle;
            if (read is { } rv) readSum = (readSum ?? 0) + rv;
            if (write is { } wv) writeSum = (writeSum ?? 0) + wv;
            if (busy is { } bv) busyMax = busyMax is { } old ? Math.Max(old, bv) : bv;
            disks.Add(new DiskReading(name, read, write, busy));
        }
        return (readSum, writeSum, busyMax, disks.ToArray());
    }

    // Task Manager reads Network Adapter bytes per second. 100 Gbps is the sanity cap.
    public static DeviceSample[] NetworkFrom(
        IEnumerable<(string Name, double Value)> received,
        IEnumerable<(string Name, double Value)> sent)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var inMap = Map(received, names);
        var outMap = Map(sent, names);
        var rows = new List<DeviceSample>();
        foreach (var name in names)
        {
            if (name.Equals("_Total", StringComparison.OrdinalIgnoreCase) || name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)) continue;
            double? receive = inMap.TryGetValue(name, out var input) && input <= 12_500_000_000d ? input : null;
            double? send = outMap.TryGetValue(name, out var output) && output <= 12_500_000_000d ? output : null;
            if (receive is null && send is null) continue;
            rows.Add(new DeviceSample(name, name, receive, send));
        }
        return rows.ToArray();
    }

    private static Dictionary<string, double> Map(IEnumerable<(string Name, double Value)> rows, SortedSet<string> names)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in rows)
        {
            if (string.IsNullOrWhiteSpace(name) || !double.IsFinite(value) || value < 0) continue;
            names.Add(name);
            map[name] = value;
        }
        return map;
    }

    public readonly record struct EngineSample(int Pid, ulong Adapter, int Physical, string Engine);

    private static readonly Regex EnginePattern = new(
        @"^pid_(\d+)_luid_0x([0-9A-Fa-f]+)_0x([0-9A-Fa-f]+)_phys_(\d+)_eng_\d+_engtype_(.+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryParseEngine(string instance, out EngineSample sample)
    {
        sample = default;
        var match = EnginePattern.Match(instance);
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) return false;
        if (!ulong.TryParse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var high)) return false;
        if (!ulong.TryParse(match.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var low)) return false;
        if (!int.TryParse(match.Groups[4].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var physical)) return false;
        sample = new EngineSample(pid, (high << 32) | low, physical, match.Groups[5].Value);
        return true;
    }

    public sealed record GpuReading(double? Utilization, ulong Adapter, string? Name, double? Dedicated,
        double? ThreeD, double? Copy, double? Compute, double? Decode, double? Encode, Dictionary<int, double> ByProcess);

    public static GpuReading Aggregate(IEnumerable<(string Instance, double Value)> rows, IReadOnlyDictionary<ulong, (string Name, double Dedicated)>? adapters = null)
    {
        var engines = new Dictionary<(ulong Adapter, int Physical, string Engine), double>();
        var processes = new List<(int Pid, ulong Adapter, int Physical, string Engine, double Value)>();
        foreach (var (instance, value) in rows)
        {
            if (!double.IsFinite(value) || value < 0 || !TryParseEngine(instance, out var parsed)) continue;
            var key = (parsed.Adapter, parsed.Physical, parsed.Engine);
            engines[key] = engines.GetValueOrDefault(key) + value;
            processes.Add((parsed.Pid, parsed.Adapter, parsed.Physical, parsed.Engine, value));
        }
        if (engines.Count == 0) return new(null, 0, null, null, null, null, null, null, null, []);
        foreach (var key in engines.Keys.ToArray()) engines[key] = Math.Clamp(engines[key], 0, 100);
        var best = engines.GroupBy(item => (item.Key.Adapter, item.Key.Physical))
            .Select(group => (group.Key, Max: group.Max(item => item.Value)))
            .OrderByDescending(item => item.Max).First().Key;
        double? Pick(string engine)
        {
            var found = engines.Where(item => item.Key.Adapter == best.Adapter && item.Key.Physical == best.Physical &&
                item.Key.Engine.Equals(engine, StringComparison.OrdinalIgnoreCase)).Select(item => item.Value).ToArray();
            return found.Length == 0 ? null : found.Max();
        }
        var byProcess = new Dictionary<int, double>();
        var perEngine = new Dictionary<(int Pid, string Engine), double>();
        foreach (var row in processes)
        {
            if (row.Adapter != best.Adapter || row.Physical != best.Physical) continue;
            var key = (row.Pid, row.Engine);
            perEngine[key] = perEngine.GetValueOrDefault(key) + row.Value;
        }
        foreach (var group in perEngine.GroupBy(item => item.Key.Pid))
            byProcess[group.Key] = Math.Clamp(group.Max(item => Math.Clamp(item.Value, 0, 100)), 0, 100);
        string? name = null;
        double? dedicated = null;
        if (adapters is not null && adapters.TryGetValue(best.Adapter, out var adapter))
        {
            name = adapter.Name;
            dedicated = adapter.Dedicated;
        }
        var utilization = engines.Where(item => item.Key.Adapter == best.Adapter && item.Key.Physical == best.Physical).Max(item => item.Value);
        return new(utilization, best.Adapter, name, dedicated, Pick("3D"), Pick("Copy"), Pick("Compute"), Pick("VideoDecode"), Pick("VideoEncode"), byProcess);
    }

    public static ProcessRow[] TerminationOrder(ProcessRow root, IReadOnlyList<ProcessRow> all)
    {
        var children = all.GroupBy(row => row.Parent).ToDictionary(group => group.Key, group => group.ToArray());
        var ordered = new List<ProcessRow>();
        var seen = new HashSet<int>();
        void Walk(ProcessRow row, int depth)
        {
            if (depth > 64 || !seen.Add(row.Pid)) return;
            if (children.TryGetValue(row.Pid, out var kids))
                foreach (var child in kids)
                    if (IsChildOf(child, row, strict: true)) Walk(child, depth + 1);
            ordered.Add(row);
        }
        Walk(root, 0);
        return ordered.ToArray();
    }

    // Windows reuses PIDs, so a parent PID alone does not prove ancestry: a child cannot start before its parent.
    // strict requires both start times (termination). Display code passes false so recordings without start times keep their tree.
    public static bool IsChildOf(ProcessRow child, ProcessRow parent, bool strict)
    {
        if (child.Parent != parent.Pid || child.Pid == parent.Pid) return false;
        if (child.Started > 0 && parent.Started > 0) return child.Started >= parent.Started;
        return !strict;
    }

    public readonly record struct ThermalReading(double? Temperature, double? TempMin, double[] Temps, string[] Names);

    // ACPI CurrentTemperature and many PDH providers are tenths of a Kelvin (3085 -> 35.35 °C).
    // The documented PDH "Temperature" counter is Kelvin (308.5 -> 35.35 °C). A few firmwares publish
    // deci-Celsius, and a plain Celsius value is accepted when it cannot be Kelvin.
    public static double? ThermalCelsius(double raw)
    {
        if (!double.IsFinite(raw) || raw <= 0) return null;
        // A reading at 0 °C is the ACPI/PDH "no sensor" placeholder (2732 tenths of a Kelvin, or 273.15 K).
        // A running PC does not report a real CPU temperature there, so it must not win over a live sensor.
        double? celsius = raw switch
        {
            >= 2531.5 and <= 3981.5 => raw / 10.0 - 273.15,
            >= 253.15 and <= 398.15 => raw - 273.15,
            <= 125 => raw,
            _ => raw <= 1250 ? raw / 10.0 : null
        };
        return celsius is >= 10 and <= 125 ? celsius : null;
    }

    public static bool IsPackageZone(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var text = name.ToLowerInvariant();
        return text.Contains("package") || text.Contains("tctl") || text.Contains("tdie");
    }

    public static bool IsCpuZone(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var text = name.ToLowerInvariant();
        if (text.Contains("cpu") || text.Contains("package") || text.Contains("tctl") || text.Contains("tdie") ||
            text.Contains("ccd") || text.Contains("processor")) return true;
        var index = 0;
        while ((index = text.IndexOf("core", index, StringComparison.Ordinal)) >= 0)
        {
            var before = index == 0 ? ' ' : text[index - 1];
            var after = index + 4 < text.Length ? text[index + 4] : ' ';
            if (!char.IsLetter(before) && !char.IsLetter(after)) return true;
            index += 4;
        }
        return false;
    }

    public static ThermalReading Thermals(IEnumerable<(string Name, double Value)> temperature, IEnumerable<(string Name, double Value)> precise)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        Take(temperature, map);
        Take(precise, map);
        return FromMap(map);
    }

    public static ThermalReading FromCelsius(IEnumerable<(string Name, double Celsius)> rows)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, celsius) in rows)
        {
            if (string.IsNullOrWhiteSpace(name) || !double.IsFinite(celsius) || celsius is < 10 or > 125) continue;
            map[name] = celsius;
        }
        return FromMap(map);
    }

    public static string ThermalCaption(double? temperature, double? tempMin, string[]? names)
    {
        if (temperature is null) return "Temperature sensors unavailable";
        var cpu = (names ?? []).Any(IsCpuZone);
        return $"{(cpu ? "CPU" : "Hottest zone")} {Format.Celsius(temperature)} · min {Format.Celsius(tempMin)}";
    }

    private static void Take(IEnumerable<(string Name, double Value)> rows, Dictionary<string, double> map)
    {
        foreach (var (name, value) in rows)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;
            if (ThermalCelsius(value) is not { } celsius) continue;
            map[name] = celsius;
        }
    }

    private static ThermalReading FromMap(Dictionary<string, double> map)
    {
        if (map.Count == 0) return new(null, null, [], []);
        var ordered = map.Where(pair => pair.Value is >= 10 and <= 125).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        if (ordered.Length == 0) return new(null, null, [], []);
        var package = ordered.Where(pair => IsPackageZone(pair.Key)).ToArray();
        var chosen = package.Length > 0 ? package : ordered.Where(pair => IsCpuZone(pair.Key)).ToArray();
        if (chosen.Length == 0) chosen = ordered;
        return new(chosen.Max(pair => pair.Value), chosen.Min(pair => pair.Value),
            ordered.Select(pair => pair.Value).ToArray(), ordered.Select(pair => pair.Key).ToArray());
    }
}
