using System.Diagnostics;
using System.Net.Http;
using System.Security.Principal;
using Microsoft.Win32;
using LibreHardwareMonitor.Hardware;
using Afterglow.Core;

namespace Afterglow.Windows;

internal static class SensorAccess
{
    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception) { return false; }
    }

    public static string Unavailable => IsAdministrator()
        ? "Temperature sensors unavailable"
        : "CPU temperature needs administrator approval. Start Afterglow again and choose Yes.";
}

internal sealed class CpuSensors : IDisposable
{
    private const string PawnIoSetup = "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/raw/refs/heads/master/LibreHardwareMonitor.Windows.Forms/Resources/PawnIO_setup.exe";
    private Computer? computer;
    private int pawnAttempt;
    private PackagePower power = new(null, null);
    private GpuReading gpu = new(null, null);
    private bool sawGpu;

    public double? PackageWatts => Volatile.Read(ref power).Watts;
    public string? PowerDetail => Volatile.Read(ref power).Detail;
    public string? GpuTemperature => Volatile.Read(ref gpu).Text;
    public double? GpuCelsius => Volatile.Read(ref gpu).Celsius;

    public (string Name, double Celsius)[] Read()
    {
        try
        {
            computer ??= Open();
            var rows = ReadOnce(computer);
            if (rows.Count == 0 || PackageWatts is null || (sawGpu && GpuTemperature is null)) rows = ReadOnce(computer);
            return rows.ToArray();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Close();
            return [];
        }
    }

    public void Dispose() => Close();

    private Computer Open()
    {
        EnsurePawnIo();
        // Same walk as OpenHardwareMonitor: open the CPU, Update(), then read Temperature sensors.
        var opened = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = true,
            IsPsuEnabled = true,
            IsPowerMonitorEnabled = true,
            IsControllerEnabled = true
        };
        opened.Open();
        return opened;
    }

    private void EnsurePawnIo()
    {
        if (PawnIoInstalled() || !SensorAccess.IsAdministrator() || Interlocked.Exchange(ref pawnAttempt, 1) == 1) return;
        try
        {
            var dest = Path.Combine(Path.GetTempPath(), "Afterglow-PawnIO-setup.exe");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var bytes = http.GetByteArrayAsync(PawnIoSetup).GetAwaiter().GetResult();
            if (bytes.Length < 10_000) return;
            File.WriteAllBytes(dest, bytes);
            using var process = Process.Start(new ProcessStartInfo(dest, "-install -silent")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(120_000);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private static bool PawnIoInstalled()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            if (Directory.Exists(Path.Combine(Environment.GetFolderPath(folder), "PawnIO"))) return true;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return key is not null;
        }
        catch (Exception) { return false; }
    }

    private void Close()
    {
        try { computer?.Close(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        computer = null;
    }

    private List<(string Name, double Celsius)> ReadOnce(Computer computer)
    {
        var rows = new List<(string Name, double Celsius)>();
        var parts = new List<(string Label, double Watts)>();
        var gpuTemps = new List<double>();
        sawGpu = false;
        foreach (var hardware in computer.Hardware) Visit(hardware, rows, parts, gpuTemps);
        var supply = parts.Where(part => part.Label == "PSU").ToArray();
        var components = parts.Where(part => part.Label != "PSU").ToArray();
        double? total = supply.Length > 0 ? supply.Sum(part => part.Watts) : components.Length > 0 ? components.Sum(part => part.Watts) : null;
        var detailParts = supply.Length > 0 ? supply.Concat(components) : components;
        var detail = detailParts.Any() ? string.Join(" · ", detailParts.Select(part => $"{part.Label} {Format.Watts(part.Watts)}")) : null;
        Volatile.Write(ref power, new PackagePower(total, detail));
        Volatile.Write(ref gpu, new GpuReading(gpuTemps.Count == 0 ? null : gpuTemps.Max(), gpuTemps.Count == 0 ? null : string.Join(" · ", gpuTemps.Select((celsius, index) => $"{(gpuTemps.Count == 1 ? "GPU" : "GPU " + (index + 1))} {Format.Celsius(celsius)}"))));
        return rows;
    }

    private void Visit(IHardware hardware, List<(string Name, double Celsius)> rows, List<(string Label, double Watts)> parts, List<double> gpuTemps)
    {
        hardware.Update();
        if (hardware.HardwareType != HardwareType.Battery) CollectPower(hardware, parts);
        if (hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
        {
            sawGpu = true;
            if (GpuCoreCelsius(hardware) is { } celsius) gpuTemps.Add(celsius);
        }
        foreach (var sensor in hardware.Sensors)
        {
            if (hardware.HardwareType is not (HardwareType.Cpu or HardwareType.Motherboard)) break;
            if (sensor.SensorType != SensorType.Temperature || sensor.Value is not float value || float.IsNaN(value) || float.IsInfinity(value) || value is < 10 or > 125) continue;
            var name = string.IsNullOrWhiteSpace(sensor.Name) ? "CPU" : sensor.Name;
            if (name.Contains("distance", StringComparison.OrdinalIgnoreCase) || name.Contains("tjmax", StringComparison.OrdinalIgnoreCase)) continue;
            if (hardware.HardwareType == HardwareType.Motherboard && !Sensors.IsCpuZone(name)) continue;
            rows.Add((hardware.HardwareType == HardwareType.Motherboard ? hardware.Name + " " + name : name, value));
        }
        foreach (var sub in hardware.SubHardware) Visit(sub, rows, parts, gpuTemps);
    }

    private static double? GpuCoreCelsius(IHardware hardware)
    {
        var temps = hardware.Sensors
            .Where(sensor => sensor.SensorType == SensorType.Temperature && sensor.Value is float value && !float.IsNaN(value) && !float.IsInfinity(value) && value is >= 10f and <= 125f)
            .Select(sensor => (Name: sensor.Name ?? "", Celsius: (double)sensor.Value!.Value))
            .ToArray();
        if (temps.Length == 0) return null;
        var core = temps.Where(sensor => sensor.Name.Contains("core", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (core.Length == 0) core = temps.Where(sensor => !sensor.Name.Contains("memory", StringComparison.OrdinalIgnoreCase) && !sensor.Name.Contains("hot", StringComparison.OrdinalIgnoreCase) && !sensor.Name.Contains("junction", StringComparison.OrdinalIgnoreCase)).ToArray();
        return (core.Length > 0 ? core : temps).Max(sensor => sensor.Celsius);
    }

    private static void CollectPower(IHardware hardware, List<(string Label, double Watts)> parts)
    {
        var sensors = hardware.Sensors
            .Where(sensor => sensor.SensorType == SensorType.Power && sensor.Value is float value && !float.IsNaN(value) && !float.IsInfinity(value) && value is >= 0.05f and <= 2000f)
            .Select(sensor => (Name: sensor.Name ?? "", Watts: (double)sensor.Value!.Value))
            .Where(sensor => !SkipPower(sensor.Name))
            .ToArray();
        if (sensors.Length == 0) return;
        var supply = sensors.Where(sensor => IsSupplyTotal(sensor.Name)).ToArray();
        if (supply.Length > 0)
        {
            parts.Add(("PSU", supply.Max(sensor => sensor.Watts)));
            sensors = sensors.Where(sensor => !IsSupplyTotal(sensor.Name)).ToArray();
            if (sensors.Length == 0) return;
        }
        switch (hardware.HardwareType)
        {
            case HardwareType.Cpu:
                AddCpu(sensors, parts);
                break;
            case HardwareType.GpuNvidia:
            case HardwareType.GpuAmd:
            case HardwareType.GpuIntel:
                if (Best(sensors, IsGpuTotal) is { } gpu) parts.Add((NextLabel(parts, "GPU"), gpu));
                break;
            case HardwareType.Psu:
                break;
            default:
                if (parts.Any(part => part.Label == "CPU")) sensors = sensors.Where(sensor => !IsCpuName(sensor.Name)).ToArray();
                if (parts.Any(part => part.Label == "Memory")) sensors = sensors.Where(sensor => !IsMemoryPower(sensor.Name)).ToArray();
                if (parts.Any(part => part.Label.StartsWith("GPU", StringComparison.Ordinal))) sensors = sensors.Where(sensor => !IsGpuName(sensor.Name)).ToArray();
                foreach (var sensor in sensors)
                    parts.Add((Trim(sensor.Name), sensor.Watts));
                break;
        }
    }

    private static void AddCpu((string Name, double Watts)[] sensors, List<(string Label, double Watts)> parts)
    {
        var package = sensors.Where(sensor => sensor.Name.Contains("package", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (package.Length == 0) package = sensors.Where(sensor => sensor.Name.Contains("ppt", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (package.Length == 0) package = sensors.Where(sensor => sensor.Name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase) || sensor.Name.Equals("Package", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (package.Length > 0) parts.Add(("CPU", package.Sum(sensor => sensor.Watts)));
        else
        {
            var rest = sensors.Where(sensor => !IsMemoryPower(sensor.Name)).Sum(sensor => sensor.Watts);
            if (rest >= 0.05) parts.Add(("CPU", rest));
        }
        var memory = sensors.Where(sensor => IsMemoryPower(sensor.Name)).Sum(sensor => sensor.Watts);
        if (memory >= 0.05) parts.Add(("Memory", memory));
    }

    private static double? Best((string Name, double Watts)[] sensors, Func<string, bool> match)
    {
        var found = sensors.Where(sensor => match(sensor.Name)).ToArray();
        return found.Length == 0 ? null : found.Max(sensor => sensor.Watts);
    }

    private static bool IsGpuTotal(string name)
    {
        var text = name.ToLowerInvariant();
        if (text.Contains("package") || text.Contains("board") || text.Contains("ppt")) return true;
        return text is "gpu power" or "gpu total";
    }

    private static bool IsMemoryPower(string name)
    {
        var text = name.ToLowerInvariant();
        return text.Contains("dram") || text is "memory" || text.Contains("cpu memory");
    }

    private static bool IsSupplyTotal(string name)
    {
        var text = name.ToLowerInvariant();
        return text is "psu out" or "total output" or "total watts" or "total power" || text.Contains("total output");
    }

    private static bool SkipPower(string name)
    {
        var text = name.ToLowerInvariant();
        return text.Contains("limit") || text.Contains("pin") || text.Contains("efficiency") || text.Contains("vrm") || text.Contains("input") || text.Contains("auxiliary") || text.Contains("vcore");
    }

    private static bool IsCpuName(string name) => name.Contains("cpu", StringComparison.OrdinalIgnoreCase) || name.Contains("package", StringComparison.OrdinalIgnoreCase);
    private static bool IsGpuName(string name) => name.Contains("gpu", StringComparison.OrdinalIgnoreCase);
    private static string NextLabel(List<(string Label, double Watts)> parts, string label) => parts.Any(part => part.Label == label) ? label + " " + (parts.Count(part => part.Label.StartsWith(label, StringComparison.Ordinal)) + 1) : label;
    private static string Trim(string name) => name.Length <= 24 ? name : name[..24];
}

internal sealed class GpuReading(double? celsius, string? text)
{
    public double? Celsius { get; } = celsius;
    public string? Text { get; } = text;
}

internal sealed class PackagePower(double? watts, string? detail)
{
    public double? Watts { get; } = watts;
    public string? Detail { get; } = detail;
}
