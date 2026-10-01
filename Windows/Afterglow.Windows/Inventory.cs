using System.ComponentModel;
using System.Reflection;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Afterglow.Core;

namespace Afterglow.Windows;

internal static class Inventory
{
    internal static string ProductVersion
    {
        get
        {
            var info = typeof(Inventory).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+');
                return plus > 0 ? info[..plus] : info;
            }
            var version = typeof(Inventory).Assembly.GetName().Version;
            return version is null ? "1.3 stable" : version.Major + "." + version.Minor + " beta";
        }
    }

    public static InfoRow[] Collect(string page, IReadOnlyDictionary<int, string>? processes = null) => page switch
    {
        "System Info" => SystemInfo(),
        "Startup apps" => Startup(),
        "Installed Apps" => Apps(),
        "Disk Space" => Volumes(),
        "Connections" => Connections(processes),
        "Services" => Services(),
        "Power & Freq" => Power(),
        _ => []
    };

    private static InfoRow[] SystemInfo()
    {
        using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        var facts = MachineFacts.Current;
        return [new("Afterglow", "Stable release", ProductVersion),
            new("Computer", "Host name", Environment.MachineName), new("Operating system", "Windows", RuntimeInformation.OSDescription),
            new("Processor", "Registry hardware description", cpu?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "Unavailable"),
            new("Manufacturer", "System BIOS", bios?.GetValue("SystemManufacturer")?.ToString() ?? "Unavailable"),
            new("Model", "System BIOS", bios?.GetValue("SystemProductName")?.ToString() ?? "Unavailable"),
            new("Logical processors", "Available to this process", Environment.ProcessorCount.ToString()),
            new("Physical cores", "Processor topology", facts.PhysicalCores > 0 ? facts.PhysicalCores.ToString() : "Unavailable"),
            new("Processor packages", "Processor topology", facts.Packages > 0 ? facts.Packages.ToString() : "Unavailable"),
            new("L1 cache", "Largest cache at this level", facts.L1),
            new("L2 cache", "Largest cache at this level", facts.L2),
            new("L3 cache", "Largest cache at this level", facts.L3),
            new("Architecture", "Operating system / process", $"{RuntimeInformation.OSArchitecture} / {RuntimeInformation.ProcessArchitecture}"),
            new("Virtualization", "Processor feature", facts.Virtualization),
            new("Virtual machine", "CPUID hypervisor bit", facts.VirtualMachine),
            new("Uptime", "Since system boot", TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"d\.hh\:mm\:ss")),
            new("CPU coverage", "PDH Processor Information, with a GetSystemTimes fallback", "Group-aware when the per-core counters are available. The fallback aggregate is disabled above 64 logical processors.")];
    }
    private static IEnumerable<(RegistryHive Hive, RegistryView View)> Hives()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 }) yield return (hive, view);
    }
    private static InfoRow[] Startup()
    {
        var rows = new List<InfoRow>();
        foreach (var (hive, view) in Hives())
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            foreach (var suffix in new[] { "Run", "RunOnce" })
            {
                using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\" + suffix);
                if (key is null) continue;
                foreach (var name in key.GetValueNames())
                {
                    var command = key.GetValue(name)?.ToString() ?? "";
                    rows.Add(new(name, command, $"{hive} · {view} · {suffix}", CommandPath(command)));
                }
            }
        }
        foreach (var folder in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
        {
            var path = Environment.GetFolderPath(folder);
            if (Directory.Exists(path)) rows.AddRange(Directory.EnumerateFiles(path).Select(f => new InfoRow(Path.GetFileName(f), f, folder.ToString(), f)));
        }
        ScheduledTasks(rows);
        return rows.Distinct().OrderBy(r => r.Name).ToArray();
    }
    private static string CommandPath(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 1) return command[1..end];
        }
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)].Trim('"') : "";
    }
    private static void ScheduledTasks(List<InfoRow> rows)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return;
            dynamic scheduler = Activator.CreateInstance(type)!;
            scheduler.Connect();
            Walk(scheduler.GetFolder("\\"), 0);
            void Walk(dynamic folder, int depth)
            {
                if (depth > 4 || rows.Count >= 2000) return;
                try
                {
                    dynamic tasks = folder.GetTasks(1);
                    var count = (int)tasks.Count;
                    for (var i = 1; i <= count && rows.Count < 2000; i++)
                    {
                        dynamic task = tasks.Item(i);
                        string name = task.Name;
                        string path = task.Path;
                        bool enabled = task.Enabled;
                        rows.Add(new(name, path, enabled ? "Scheduled task · enabled" : "Scheduled task · disabled"));
                    }
                }
                catch (COMException) { }
                try
                {
                    dynamic folders = folder.GetFolders(0);
                    var count = (int)folders.Count;
                    for (var i = 1; i <= count && rows.Count < 2000 && depth < 4; i++) Walk(folders.Item(i), depth + 1);
                }
                catch (COMException) { }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException or System.Reflection.TargetInvocationException) { }
    }
    private static InfoRow[] Apps()
    {
        var rows = new List<InfoRow>();
        foreach (var (hive, view) in Hives())
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (key is null) continue;
            foreach (var name in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(name);
                if (app?.GetValue("DisplayName") is not string title) continue;
                var location = app.GetValue("InstallLocation") as string ?? "";
                rows.Add(new(title, app.GetValue("Publisher")?.ToString() ?? "", app.GetValue("DisplayVersion")?.ToString() ?? "Unavailable", location));
            }
        }
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (packages is not null)
                foreach (var full in packages.GetSubKeyNames())
                {
                    var family = full.Split('_', 2)[0];
                    if (family.Length > 0) rows.Add(new(family, full, "Store package"));
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return rows.Distinct().OrderBy(r => r.Name).ToArray();
    }
    private static InfoRow[] Volumes() => DriveInfo.GetDrives().Select(d =>
    {
        try { return d.IsReady ? new InfoRow(d.Name + " " + d.VolumeLabel, $"{d.DriveType} · {d.DriveFormat}", $"{Format.Bytes(d.AvailableFreeSpace)} available / {Format.Bytes(d.TotalSize)} total") : new(d.Name, d.DriveType.ToString(), "Not ready"); }
        catch (IOException) { return new InfoRow(d.Name, "Volume", "Unavailable"); }
    }).ToArray();
    private static InfoRow[] Connections(IReadOnlyDictionary<int, string>? processes)
    {
        var rows = new List<InfoRow>();
        var tcp4 = ReadTable(true, 2, 5);
        var tcp6 = ReadTable(true, 23, 5);
        var udp4 = ReadTable(false, 2, 1);
        var udp6 = ReadTable(false, 23, 1);
        if (tcp4 is null && tcp6 is null && udp4 is null && udp6 is null)
            return [new("Connections unavailable", "IP Helper did not return a table.", "Refresh to retry")];
        if (tcp4 is not null) ParseTcp(tcp4, false, 24, processes, rows);
        if (tcp6 is not null) ParseTcp(tcp6, true, 56, processes, rows);
        if (udp4 is not null) ParseUdp(udp4, false, 12, processes, rows);
        if (udp6 is not null) ParseUdp(udp6, true, 28, processes, rows);
        return rows.ToArray();
    }
    private static byte[]? ReadTable(bool tcp, uint family, int kind)
    {
        var capacity = 4096;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (capacity is <= 0 or > 32 * 1024 * 1024) return null;
            var buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                var size = capacity;
                var result = tcp
                    ? Native.GetExtendedTcpTable(buffer, ref size, true, family, kind, 0)
                    : Native.GetExtendedUdpTable(buffer, ref size, true, family, kind, 0);
                if (result == 0)
                {
                    var take = Math.Min(capacity, Math.Max(size, 0));
                    var bytes = new byte[take];
                    if (take > 0) Marshal.Copy(buffer, bytes, 0, take);
                    return bytes;
                }
                if (result != 122) return null;
                capacity = size;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }
    private static void ParseTcp(byte[] bytes, bool v6, int rowSize, IReadOnlyDictionary<int, string>? names, List<InfoRow> rows)
    {
        if (bytes.Length < 4) return;
        var count = BitConverter.ToInt32(bytes, 0);
        var offset = 4;
        for (var i = 0; i < count && offset + rowSize <= bytes.Length; i++, offset += rowSize)
        {
            string local, remote; uint state; int pid;
            if (!v6)
            {
                state = BitConverter.ToUInt32(bytes, offset);
                local = Endpoint(bytes, offset + 4, 4, offset + 8);
                remote = Endpoint(bytes, offset + 12, 4, offset + 16);
                pid = BitConverter.ToInt32(bytes, offset + 20);
            }
            else
            {
                local = Endpoint(bytes, offset, 16, offset + 20, BitConverter.ToUInt32(bytes, offset + 16));
                remote = Endpoint(bytes, offset + 24, 16, offset + 44, BitConverter.ToUInt32(bytes, offset + 40));
                state = BitConverter.ToUInt32(bytes, offset + 48);
                pid = BitConverter.ToInt32(bytes, offset + 52);
            }
            rows.Add(new(Owner(names, pid), $"{local} → {remote}", TcpState(state)));
        }
    }
    private static void ParseUdp(byte[] bytes, bool v6, int rowSize, IReadOnlyDictionary<int, string>? names, List<InfoRow> rows)
    {
        if (bytes.Length < 4) return;
        var count = BitConverter.ToInt32(bytes, 0);
        var offset = 4;
        for (var i = 0; i < count && offset + rowSize <= bytes.Length; i++, offset += rowSize)
        {
            var local = v6
                ? Endpoint(bytes, offset, 16, offset + 20, BitConverter.ToUInt32(bytes, offset + 16))
                : Endpoint(bytes, offset, 4, offset + 4);
            var pid = BitConverter.ToInt32(bytes, offset + (v6 ? 24 : 8));
            rows.Add(new(Owner(names, pid), local, "UDP"));
        }
    }
    private static string Endpoint(byte[] bytes, int address, int length, int portOffset, uint scope = 0)
    {
        var raw = new byte[length];
        Buffer.BlockCopy(bytes, address, raw, 0, length);
        var ip = new IPAddress(raw);
        if (length == 16 && scope != 0) ip.ScopeId = scope;
        var port = Sensors.NetworkPort(BitConverter.ToUInt32(bytes, portOffset));
        return length == 16 ? $"[{ip}]:{port}" : $"{ip}:{port}";
    }
    private static string Owner(IReadOnlyDictionary<int, string>? names, int pid) =>
        names is not null && names.TryGetValue(pid, out var name) && !string.IsNullOrWhiteSpace(name) ? $"{name} · {pid}" : $"pid {pid}";
    private static string TcpState(uint state) => state switch
    {
        1 => "Closed", 2 => "Listen", 3 => "SynSent", 4 => "SynReceived", 5 => "Established",
        6 => "FinWait1", 7 => "FinWait2", 8 => "CloseWait", 9 => "Closing", 10 => "LastAck",
        11 => "TimeWait", 12 => "Delete", _ => "State " + state.ToString(CultureInfo.InvariantCulture)
    };
    private static InfoRow[] Power()
    {
        var ok = Native.GetSystemPowerStatus(out var p);
        using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return [new("Power source", "GetSystemPowerStatus", !ok ? "Unavailable" : p.AcLine switch { 1 => "AC power", 0 => "Battery", _ => "Unknown" }),
            new("Battery", "Reported charge", ok && p.BatteryPercent <= 100 && (p.BatteryFlag & 128) == 0 ? $"{p.BatteryPercent}%" : "Unavailable"),
            new("Energy saver", "System power policy", !ok ? "Unavailable" : p.SystemStatus == 1 ? "Enabled" : "Disabled"),
            new("CPU nominal frequency", "Registry value; the live clock is shown above when PDH reports it", cpu?.GetValue("~MHz") is int mhz ? $"{mhz} MHz" : "Unavailable"),
            new("Total power", "CPU package, memory, GPU, motherboard, storage, and PSU output", "Shown live. Core and pin sensors are not added on top of a package total."),
            new("Battery rate", "SYSTEM_BATTERY_STATE.Rate when a battery is present", "Shown live on this page. Positive means discharging. This is not package power."),
            new("Temperature", "CPU package sensor, then ACPI thermal zones", "Shown live when a sensor can be read."),
            new("GPU utilization", "PDH GPU Engine", "Shown live when the driver publishes engine counters."),
            new("NPU utilization", "No generic NPU counter", "Unavailable")];
    }
    private static InfoRow[] Services()
    {
        var manager = Native.OpenSCManager(null, null, 4);
        if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var rows = new List<InfoRow>(); uint resume = 0; const int size = 256 * 1024;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                bool complete;
                do
                {
                    complete = Native.EnumServicesStatusEx(manager, 0, 0x30, 3, buffer, size, out _, out var count, ref resume, null);
                    var error = Marshal.GetLastWin32Error();
                    if (!complete && error != 234) throw new Win32Exception(error);
                    for (var i = 0; i < count; i++)
                    {
                        var item = Marshal.PtrToStructure<Native.ServiceEntry>(buffer + i * Marshal.SizeOf<Native.ServiceEntry>());
                        rows.Add(new(Marshal.PtrToStringUni(item.DisplayName) ?? "", $"{Marshal.PtrToStringUni(item.Name)} · PID {item.Status.Pid}",
                            item.Status.State switch { 1 => "Stopped", 2 => "Starting", 3 => "Stopping", 4 => "Running", 7 => "Paused", _ => "Pending" }));
                    }
                } while (!complete && resume != 0);
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return rows.OrderBy(r => r.Name).ToArray();
        }
        finally { Native.CloseServiceHandle(manager); }
    }
}
