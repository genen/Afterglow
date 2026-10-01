using LibreHardwareMonitor.Hardware;

namespace Afterglow.Windows;

internal sealed class CpuSensors : IDisposable
{
    private Computer? computer;

    public (string Name, double Celsius)[] Read()
    {
        try
        {
            computer ??= Open();
            var rows = new List<(string Name, double Celsius)>();
            foreach (var hardware in computer.Hardware)
            {
                if (hardware.HardwareType != HardwareType.Cpu) continue;
                Visit(hardware, rows);
            }
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
        var opened = new Computer { IsCpuEnabled = true };
        opened.Open();
        return opened;
    }

    private void Close()
    {
        try { computer?.Close(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        computer = null;
    }

    private static void Visit(IHardware hardware, List<(string Name, double Celsius)> rows)
    {
        hardware.Update();
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature || sensor.Value is not float value) continue;
            if (float.IsNaN(value) || float.IsInfinity(value) || value is < -20 or > 125) continue;
            var name = string.IsNullOrWhiteSpace(sensor.Name) ? "CPU" : sensor.Name;
            if (name.Contains("distance", StringComparison.OrdinalIgnoreCase) || name.Contains("tjmax", StringComparison.OrdinalIgnoreCase)) continue;
            rows.Add((name, value));
        }
        foreach (var sub in hardware.SubHardware) Visit(sub, rows);
    }
}
