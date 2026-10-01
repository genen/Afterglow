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

    public (string Name, double Celsius)[] Read()
    {
        try
        {
            computer ??= Open();
            var rows = ReadOnce(computer);
            if (rows.Count == 0) rows = ReadOnce(computer);
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
        var opened = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true };
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

    private static List<(string Name, double Celsius)> ReadOnce(Computer computer)
    {
        var rows = new List<(string Name, double Celsius)>();
        foreach (var hardware in computer.Hardware)
        {
            if (hardware.HardwareType is not (HardwareType.Cpu or HardwareType.Motherboard)) continue;
            Visit(hardware, rows);
        }
        return rows;
    }

    private static void Visit(IHardware hardware, List<(string Name, double Celsius)> rows)
    {
        hardware.Update();
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature || sensor.Value is not float value) continue;
            if (float.IsNaN(value) || float.IsInfinity(value) || value is < 10 or > 125) continue;
            var name = string.IsNullOrWhiteSpace(sensor.Name) ? "CPU" : sensor.Name;
            if (name.Contains("distance", StringComparison.OrdinalIgnoreCase) || name.Contains("tjmax", StringComparison.OrdinalIgnoreCase)) continue;
            if (hardware.HardwareType == HardwareType.Motherboard && !Sensors.IsCpuZone(name)) continue;
            rows.Add((hardware.HardwareType == HardwareType.Motherboard ? hardware.Name + " " + name : name, value));
        }
        foreach (var sub in hardware.SubHardware) Visit(sub, rows);
    }
}
