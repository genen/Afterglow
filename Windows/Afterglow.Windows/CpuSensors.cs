using LibreHardwareMonitor.Hardware;
using Afterglow.Core;

namespace Afterglow.Windows;

internal sealed class CpuSensors : IDisposable
{
    private Computer? computer;

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
        var opened = new Computer { IsCpuEnabled = true, IsMotherboardEnabled = true };
        opened.Open();
        return opened;
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
