using System.Text.Json;

namespace Afterglow.Core;

public static class TraceFile
{
    public const int MaxBytes = 128 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 32 };

    public static async Task Save(string path, IEnumerable<Frame> frames, string platform = "windows")
    {
        var trace = new Trace(2, "Afterglow", platform, frames.ToArray());
        Validate(trace);
        var data = JsonSerializer.SerializeToUtf8Bytes(trace, Options);
        if (data.Length > MaxBytes) throw new InvalidDataException("Recording exceeds 128 MiB.");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temp, data); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static async Task<Trace> Load(string path)
    {
        // Read through one handle, with a hard bound even if a file changes during reading.
        await using var file = File.OpenRead(path);
        if (file.Length > MaxBytes) throw new InvalidDataException("Recording exceeds 128 MiB.");
        using var buffer = new MemoryStream();
        var chunk = new byte[65536];
        int count;
        while ((count = await file.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + count > MaxBytes) throw new InvalidDataException("Recording exceeds 128 MiB.");
            buffer.Write(chunk, 0, count);
        }
        return Decode(buffer.ToArray());
    }

    public static Trace Decode(byte[] data)
    {
        if (data.Length > MaxBytes) throw new InvalidDataException("Recording too large.");
        using var doc = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 });
        var root = doc.RootElement;
        if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var v)) throw new InvalidDataException("Missing trace version.");
        var trace = v switch
        {
            1 => ImportMac(root),
            2 => JsonSerializer.Deserialize<Trace>(data, Options) ?? throw new InvalidDataException("Empty trace."),
            _ => throw new InvalidDataException("Unsupported trace version.")
        };
        Validate(trace); return trace;
    }

    private static void Validate(Trace trace)
    {
        if (trace.App != "Afterglow" || trace.Platform is not ("windows" or "macos") || trace.Frames is null ||
            trace.Frames.Length is < 1 or > RecordingBuffer.MaxFrames) throw new InvalidDataException("Invalid recording header or sample count.");
        long count = 0;
        DateTimeOffset? previousTime = null;
        foreach (var f in trace.Frames)
        {
            if (f is null || f.Sample is null || f.Processes is null || f.Sample.Interfaces is null || f.Sample.TopCpu is null || f.Sample.PowerSource is null)
                throw new InvalidDataException("Incomplete frame.");
            count += f.Processes.Length;
            if (count > RecordingBuffer.MaxProcesses) throw new InvalidDataException("Too many process snapshots.");
            var s = f.Sample;
            if (previousTime > s.Time) throw new InvalidDataException("Recording timestamps are not ordered.");
            previousTime = s.Time;
            double?[] values = [s.Cpu, s.Kernel, s.Memory, s.Total, s.Available, s.Receive, s.Send, s.DiskRead, s.DiskWrite, s.BatteryPercent,
                s.Cached, s.Committed, s.CommitLimit, s.GpuMemory, s.Frequency];
            if (values.Any(x => x is { } n && (!double.IsFinite(n) || n < 0)) || s.Cpu > 100 || s.Kernel > 100 || s.BatteryPercent > 100)
                throw new InvalidDataException("Invalid sample counter.");
            if (s.Pressure is not (0 or 1 or 2 or 4)) throw new InvalidDataException("Invalid memory pressure.");
            foreach (var percent in new double?[] { s.Gpu, s.Gpu3D, s.GpuCopy, s.GpuCompute, s.GpuDecode, s.GpuEncode, s.DiskBusy, s.PageFile })
                if (percent is { } n && (!double.IsFinite(n) || n is < 0 or > 100)) throw new InvalidDataException("Invalid percent counter.");
            if (s.BatteryWatts is { } watts && (!double.IsFinite(watts) || Math.Abs(watts) > 500)) throw new InvalidDataException("Invalid battery rate.");
            if (s.PackageWatts is { } package && (!double.IsFinite(package) || package is <= 0 or > 2000)) throw new InvalidDataException("Invalid package power.");
            if (s.Frequency is > 20_000) throw new InvalidDataException("Invalid processor frequency.");
            if (new double?[] { s.Temperature, s.TempMin }.Any(temp => temp is { } n && (!double.IsFinite(n) || n is < -20 or > 125)) ||
                (s.Temps ?? []).Any(n => !double.IsFinite(n) || n is < -20 or > 125))
                throw new InvalidDataException("Invalid temperature.");
            if (s.Temps is not null && (s.TempNames is null || s.TempNames.Length != s.Temps.Length || s.TempNames.Any(name => name is null)))
                throw new InvalidDataException("Invalid temperature names.");
            if (s.Cores is { } cores && cores.Any(n => !double.IsFinite(n) || n is < 0 or > 100)) throw new InvalidDataException("Invalid logical processor counter.");
            foreach (var p in f.Processes)
                if (p is null || p.Name is null || p.User is null || p.Path is null || p.Status is null || p.Pid < 0 || p.Parent < 0 || p.Threads < 0 || p.CpuTime < 0 ||
                    new[] { p.Cpu, p.Memory, p.ReadRate, p.WriteRate }.Any(x => x is { } n && (!double.IsFinite(n) || n < 0)) ||
                    p.Gpu is { } gpu && (!double.IsFinite(gpu) || gpu is < 0 or > 100))
                    throw new InvalidDataException("Invalid process snapshot.");
            foreach (var nic in s.Interfaces.Concat(s.Disks ?? []))
                if (nic is null || nic.Id is null || nic.Name is null ||
                    new[] { nic.Receive, nic.Send, nic.Read, nic.Write, nic.Busy }.Any(x => x is { } n && (!double.IsFinite(n) || n < 0)) || nic.Busy > 100)
                    throw new InvalidDataException("Invalid device counter.");
        }
    }

    private static Trace ImportMac(JsonElement root)
    {
        if (root.GetProperty("app").GetString() is not ("Afterglow" or "Pulse" or "TMOGClone")) throw new InvalidDataException("Unknown recording application.");
        var input = root.GetProperty("frames");
        if (input.GetArrayLength() > RecordingBuffer.MaxFrames) throw new InvalidDataException("Too many frames.");
        var frames = new List<Frame>();
        foreach (var f in input.EnumerateArray())
        {
            var s = f.GetProperty("sample");
            // Foundation JSONEncoder encodes Date as seconds since 2001-01-01.
            var time = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(s.GetProperty("time").GetDouble());
            var rows = f.GetProperty("processes").EnumerateArray().Select(p => new ProcessRow(
                p.GetProperty("pid").GetInt32(), 0, Str(p, "name"), Str(p, "user"), -1, Num(p, "cpu"), Num(p, "memory"),
                Num(p, "readRate"), Num(p, "writeRate"), Str(p, "path"), Str(p, "status"), Int(p, "ppid"), Int(p, "threads"), 0, Int(p, "priority"))).ToArray();
            frames.Add(new(new(time, Num(s, "cpu"), Num(s, "kernel"), Num(s, "memory"), Num(s, "total"), Num(s, "available"),
                Num(s, "receive"), Num(s, "send"), Num(s, "diskRead"), Num(s, "diskWrite"), Str(s, "powerSource"), null, Str(s, "topCPU"),
                Devices(s, "nics", "inRate", "outRate", false), Nums(s, "cores"), Devices(s, "disks", "readRate", "writeRate", true), Num(s, "gpu"),
                null, null, null, null, null, null, null, Num(s, "cached"), null, null, null, Int(s, "pressure"), Num(s, "diskBusy"),
                Num(s, "temperature"), Num(s, "tempMin")), rows));
        }
        return new(2, "Afterglow", "macos", frames.ToArray());
    }
    private static int Int(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    private static double[]? Nums(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var values = v.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Number).Select(item => item.GetDouble()).ToArray();
        return values.Length == 0 ? null : values;
    }
    private static DeviceSample[] Devices(JsonElement e, string key, string left, string right, bool disk)
    {
        if (!e.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array) return [];
        return v.EnumerateArray().Select(item =>
        {
            var name = Str(item, "name");
            return disk
                ? new DeviceSample(name, name, null, null, Num(item, left), Num(item, right), Num(item, "busy"))
                : new DeviceSample(name, name, Num(item, left), Num(item, right));
        }).ToArray();
    }
    private static double? Num(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    private static string Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
}
