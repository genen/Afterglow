using System.ComponentModel;
using System.Management;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Afterglow.Core;

namespace Afterglow.Windows;

internal sealed class WindowsCollector : IDisposable
{
    private readonly PdhSource counters = new();
    private readonly CpuSensors cpuSensors = new();
    private readonly Dictionary<ulong, (string Name, double Dedicated)> adapters = [];
    private (ulong Idle, ulong Kernel, ulong User)? cpu;
    private long stamp, nextAdapters, nextThermal;
    private int thermalRunning;
    private (string Name, double Celsius)[] pdhZones = [];
    private (string Name, double Celsius)[] fallbackZones = [];
    private Dictionary<string, (ulong Cpu, ulong? Read, ulong? Write, string Owner)> previous = [];
    private Dictionary<string, (ulong Receive, ulong Send)> oldNics = [];
    private static bool archAvailable = true;

    public Frame Collect()
    {
        ApplyThermal();
        RefreshAdapters();
        var now = Stopwatch.GetTimestamp();
        var seconds = stamp == 0 ? 0 : Stopwatch.GetElapsedTime(stamp, now).TotalSeconds;
        stamp = now;
        counters.Collect();
        var cores = Sensors.Cores(counters.Array("cpu"), counters.Array("kernel"));
        double? usedCpu = null, kernelCpu = null;
        if (cores.Length > 0)
        {
            usedCpu = cores.Average(core => core.Total);
            var privileged = cores.Select(core => core.Privileged).ToArray();
            if (privileged.All(value => value is not null)) kernelCpu = privileged.Average(value => value!.Value);
        }
        else if (Native.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            if (cpu is { } old && Environment.ProcessorCount <= 64)
                (usedCpu, kernelCpu) = CounterMath.Cpu(idle, kernel, user, old.Idle, old.Kernel, old.User);
            cpu = (idle, kernel, user);
        }
        else cpu = null;

        var memory = new Native.MemoryStatus { Length = (uint)Marshal.SizeOf<Native.MemoryStatus>() };
        var memoryOk = Native.GlobalMemoryStatusEx(ref memory);
        double? cached = null, committed = null, commitLimit = null;
        var performance = new Native.PerformanceInfo { Size = (uint)Marshal.SizeOf<Native.PerformanceInfo>() };
        if (Native.GetPerformanceInfo(ref performance, performance.Size) && performance.PageSize > 0)
        {
            cached = (double)performance.SystemCache * (double)performance.PageSize;
            committed = (double)performance.CommitTotal * (double)performance.PageSize;
            commitLimit = (double)performance.CommitLimit * (double)performance.PageSize;
        }
        var reads = counters.Array("diskRead");
        var writes = counters.Array("diskWrite");
        var idles = counters.Array("diskIdle");
        var disks = Sensors.DisksFrom(reads, writes, idles);
        var diskRead = disks.Read;
        var diskWrite = disks.Write;
        var diskBusy = disks.Busy;
        if (disks.Disks.Length == 0)
        {
            diskRead = Aggregate(reads);
            diskWrite = Aggregate(writes);
            if (Total(idles) is { } idle && idle <= 100) diskBusy = 100 - idle;
        }
        var gpu = Sensors.Aggregate(counters.Array("gpu"), adapters);
        var frequency = counters.Scalar("freq");
        if (frequency is null or <= 0 or > 20_000) frequency = null;
        var page = counters.Scalar("page");
        if (page is null or > 100) page = null;
        var powerOk = Native.GetSystemPowerStatus(out var power);
        var source = !powerOk ? "Unavailable" : power.AcLine switch { 0 => "Battery", 1 => "AC power", _ => "Unknown" };
        var batteryPercent = powerOk && power.BatteryPercent <= 100 && (power.BatteryFlag & 128) == 0 ? (double?)power.BatteryPercent : null;
        var thermal = CurrentThermal();
        double? hotspot = thermal.Temperature;
        double? tempMin = thermal.TempMin;
        var rows = Processes(seconds, gpu);
        var nics = Interfaces(seconds);
        var top = rows.Where(row => row.Cpu is not null).MaxBy(row => row.Cpu);
        var topMemory = rows.Where(row => row.Memory is not null).MaxBy(row => row.Memory);
        var available = memoryOk ? (double?)memory.AvailablePhysical : null;
        var total = memoryOk ? (double?)memory.TotalPhysical : null;
        var sample = new Sample(DateTimeOffset.Now, usedCpu, kernelCpu,
            memoryOk ? memory.TotalPhysical - memory.AvailablePhysical : null, total, available,
            Sum(nics.Select(nic => nic.Receive)), Sum(nics.Select(nic => nic.Send)),
            diskRead, diskWrite, source, batteryPercent, top is null ? "Unavailable" : $"{top.Name} · {Format.Percent(top.Cpu)}", nics,
            cores.Length == 0 ? null : cores.Select(core => core.Total).ToArray(),
            disks.Disks.Length == 0 ? null : disks.Disks.Select(disk => new DeviceSample(disk.Name, disk.Name, null, null, disk.Read, disk.Write, disk.Busy)).ToArray(),
            gpu.Utilization, gpu.Name, gpu.ThreeD, gpu.Copy, gpu.Compute, gpu.Decode, gpu.Encode, gpu.Dedicated,
            cached, committed, commitLimit, page, Sensors.Pressure(available, total), diskBusy, hotspot, tempMin,
            thermal.Temps, thermal.Names, BatteryWatts(), frequency, topMemory is null ? null : $"{topMemory.Name} · {Format.Bytes(topMemory.Memory)}", cpuSensors.PackageWatts, cpuSensors.PowerDetail);
        return new(sample, rows);
    }

    private ProcessRow[] Processes(double seconds, Sensors.GpuReading gpu)
    {
        var snapshot = ProcessSnapshot.Take();
        var gpuKnown = gpu.Utilization is not null;
        var next = new Dictionary<string, (ulong Cpu, ulong? Read, ulong? Write, string Owner)>();
        var rows = new List<ProcessRow>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var pid = process.Id;
                    var name = process.ProcessName;
                    using var handle = Native.OpenProcess(0x1000, false, pid);
                    long start = 0;
                    double? pcpu = null, readRate = null, writeRate = null, memory = null;
                    var owner = "Unavailable";
                    var path = "";
                    var session = -1;
                    var status = "Limited access";
                    long cpuTime = 0;
                    var arch = "";
                    if (!handle.IsInvalid && Native.GetProcessTimes(handle, out start, out _, out var kernel, out var user))
                    {
                        var identity = $"{pid}:{start}";
                        var ticks = kernel + user;
                        cpuTime = ticks > long.MaxValue ? long.MaxValue : (long)ticks;
                        var seen = previous.TryGetValue(identity, out var prior);
                        var ioOk = Native.GetProcessIoCounters(handle, out var io);
                        if (seen)
                        {
                            pcpu = CounterMath.Rate(ticks, prior.Cpu, seconds) / 100_000;
                            if (ioOk && prior.Read is { } read) readRate = CounterMath.Rate(io.ReadBytes, read, seconds);
                            if (ioOk && prior.Write is { } write) writeRate = CounterMath.Rate(io.WriteBytes, write, seconds);
                            owner = prior.Owner;
                        }
                        else if (Native.OpenProcessToken(handle, 8, out var token))
                        {
                            using (token)
                            using (var account = new WindowsIdentity(token.DangerousGetHandle())) owner = account.Name;
                        }
                        next[identity] = (ticks, ioOk ? io.ReadBytes : null, ioOk ? io.WriteBytes : null, owner);
                        status = "Running";
                        path = ImagePath(handle);
                        arch = Architecture(handle);
                    }
                    try { memory = process.WorkingSet64; } catch (Win32Exception) { }
                    try { session = process.SessionId; } catch (Win32Exception) { }
                    var windowed = false;
                    try { windowed = process.MainWindowHandle != 0; } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
                    snapshot.TryGetValue(pid, out var info);
                    var processGpu = gpuKnown ? gpu.ByProcess.GetValueOrDefault(pid) : (double?)null;
                    rows.Add(new(pid, start, name, owner, session, pcpu, memory, readRate, writeRate, path, status, info.Parent, info.Threads, cpuTime, info.Priority, arch, windowed, processGpu));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException)
                { /* A process can exit or deny access between enumeration and inspection. */ }
            }
        }
        previous = next;
        return rows.OrderByDescending(row => row.Cpu ?? -1).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ImagePath(SafeProcessHandle handle)
    {
        var buffer = new char[1024];
        var size = buffer.Length;
        return Native.QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0 ? new string(buffer, 0, size) : "";
    }

    private static string Architecture(SafeProcessHandle handle)
    {
        if (!archAvailable) return "";
        try
        {
            if (!Native.IsWow64Process2(handle, out var machine, out var native)) return "";
            return Machine(machine == 0 ? native : machine);
        }
        catch (EntryPointNotFoundException) { archAvailable = false; return ""; }
    }

    private static string Machine(ushort machine) => machine switch { 0x014c => "x86", 0x8664 => "x64", 0xAA64 => "ARM64", _ => "" };

    private DeviceSample[] Interfaces(double seconds)
    {
        var rows = new List<DeviceSample>();
        var next = new Dictionary<string, (ulong Receive, ulong Send)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(item => item.NetworkInterfaceType != NetworkInterfaceType.Loopback && item.OperationalStatus == OperationalStatus.Up))
            {
                try
                {
                    var stats = nic.GetIPStatistics();
                    var receive = (ulong)Math.Max(0, stats.BytesReceived);
                    var send = (ulong)Math.Max(0, stats.BytesSent);
                    var valid = oldNics.TryGetValue(nic.Id, out var old);
                    rows.Add(new(nic.Id, nic.Name, valid ? CounterMath.Rate(receive, old.Receive, seconds) : null, valid ? CounterMath.Rate(send, old.Send, seconds) : null));
                    next[nic.Id] = (receive, send);
                }
                catch (NetworkInformationException) { rows.Add(new(nic.Id, nic.Name, null, null)); }
            }
        }
        catch (NetworkInformationException) { }
        oldNics = next;
        return rows.ToArray();
    }

    private static double? Sum(IEnumerable<double?> numbers)
    {
        var values = numbers.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return values.Length == 0 ? null : values.Sum();
    }

    private static double? Total(IEnumerable<(string Name, double Value)> rows) =>
        rows.Where(row => row.Name.Equals("_Total", StringComparison.OrdinalIgnoreCase)).Select(row => (double?)row.Value).FirstOrDefault();

    private static double? Aggregate(IEnumerable<(string Name, double Value)> rows)
    {
        const double cap = 64d * 1024 * 1024 * 1024;
        return Total(rows) is { } value && value <= cap ? value : null;
    }

    private static double? BatteryWatts()
    {
        if (Marshal.SizeOf<Native.BatteryState>() != 32) return null;
        if (Native.CallNtPowerInformation(5, 0, 0, out var state, 32) != 0 || state.Present == 0) return null;
        var watts = state.Rate / 1000.0;
        return double.IsFinite(watts) && Math.Abs(watts) <= 500 ? watts : null;
    }

    private void RefreshAdapters()
    {
        var now = Stopwatch.GetTimestamp();
        if (adapters.Count > 0 && now < nextAdapters) return;
        nextAdapters = now + Stopwatch.Frequency * 30;
        try
        {
            var found = GpuAdapters.Current();
            adapters.Clear();
            foreach (var pair in found) adapters[pair.Key] = pair.Value;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException) { }
    }

    private void ApplyThermal()
    {
        if (counters.Ready && (counters.Has("thermal") || counters.Has("thermalHi")))
        {
            var reading = Sensors.Thermals(counters.Array("thermal"), counters.Array("thermalHi"));
            if (reading.Temps.Length > 0)
            {
                Volatile.Write(ref pdhZones, Pairs(reading));
                if (!reading.Names.Any(Sensors.IsCpuZone)) ScheduleThermal();
                return;
            }
        }
        Volatile.Write(ref pdhZones, []);
        ScheduleThermal();
    }

    private Sensors.ThermalReading CurrentThermal() =>
        Sensors.FromCelsius(Volatile.Read(ref fallbackZones).Concat(Volatile.Read(ref pdhZones)));

    private static (string Name, double Celsius)[] Pairs(Sensors.ThermalReading reading)
    {
        var rows = new (string Name, double Celsius)[reading.Temps.Length];
        for (var i = 0; i < rows.Length; i++) rows[i] = (reading.Names[i], reading.Temps[i]);
        return rows;
    }

    private void ScheduleThermal()
    {
        var now = Stopwatch.GetTimestamp();
        if (now < nextThermal || Interlocked.Exchange(ref thermalRunning, 1) == 1) return;
        nextThermal = now + Stopwatch.Frequency * 2;
        var thread = new Thread(ReadThermal) { IsBackground = true, Name = "Afterglow thermal" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void ReadThermal()
    {
        try
        {
            var rows = new List<(string Name, double Celsius)>();
            var havePdh = Volatile.Read(ref pdhZones).Length > 0;
            if (!havePdh) ReadAcpi(rows);
            rows.AddRange(cpuSensors.Read());
            if (havePdh) rows.RemoveAll(row => !Sensors.IsCpuZone(row.Name) && !Sensors.IsPackageZone(row.Name));
            if (rows.Count == 0) return;
            Volatile.Write(ref fallbackZones, rows.ToArray());
        }
        catch (Exception ex) when (IsOptional(ex))
        { /* Thermal sensors are optional. Keep the last good reading. */ }
        finally { Interlocked.Exchange(ref thermalRunning, 0); }
    }

    private static void ReadAcpi(List<(string Name, double Celsius)> rows)
    {
        QueryWmi(@"root\wmi", "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature", item =>
        {
            if (item["CurrentTemperature"] is null || Sensors.ThermalCelsius(Convert.ToDouble(item["CurrentTemperature"])) is not { } celsius) return;
            var name = item["InstanceName"]?.ToString();
            rows.Add((string.IsNullOrWhiteSpace(name) ? "Thermal zone" : name, celsius));
        });
        QueryWmi(@"root\cimv2", "SELECT Name, Temperature, HighPrecisionTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation", item =>
        {
            double? precise = item["HighPrecisionTemperature"] is null ? null : Convert.ToDouble(item["HighPrecisionTemperature"]);
            double? coarse = item["Temperature"] is null ? null : Convert.ToDouble(item["Temperature"]);
            var celsius = precise is { } hi ? Sensors.ThermalCelsius(hi) : null;
            celsius ??= coarse is { } lo ? Sensors.ThermalCelsius(lo) : null;
            if (celsius is not { } value) return;
            var name = item["Name"]?.ToString();
            rows.Add((string.IsNullOrWhiteSpace(name) ? "Thermal zone" : name, value));
        });
    }

    private static void QueryWmi(string scope, string query, Action<ManagementObject> visit)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                    try { visit(item); }
                    catch (Exception ex) when (IsOptional(ex)) { }
            }
        }
        catch (Exception ex) when (IsOptional(ex) || ex is ManagementException || ex is UnauthorizedAccessException)
        { }
    }

    private static bool IsOptional(Exception ex) => ex is COMException or InvalidCastException or InvalidOperationException
        or System.Reflection.TargetInvocationException or FormatException or OverflowException or NullReferenceException or InvalidComObjectException;

    public void Dispose()
    {
        counters.Dispose();
        cpuSensors.Dispose();
    }

    public static void Terminate(ProcessRow row, bool replay)
    {
        using var handle = Native.OpenProcess(0x1001, false, row.Pid);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!Native.GetProcessTimes(handle, out var start, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!CounterMath.CanTerminate(row, start, Environment.ProcessId, replay)) throw new InvalidOperationException("Process identity changed, is protected, or belongs to a recording.");
        if (!Native.IsProcessCritical(handle, out var critical) || critical) throw new InvalidOperationException("Cannot terminate a critical or unverified process.");
        if (!Native.TerminateProcess(handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static void TerminateTree(ProcessRow root, IReadOnlyList<ProcessRow> all, bool replay)
    {
        if (!CounterMath.CanTerminate(root, root.Started, Environment.ProcessId, replay))
            throw new InvalidOperationException("Process identity changed, is protected, or belongs to a recording.");
        Verify(root);
        var errors = new List<string>();
        foreach (var row in Sensors.TerminationOrder(root, all))
        {
            if (row.Pid == root.Pid && row.Started == root.Started) continue;
            try { Terminate(row, false); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { errors.Add($"{row.Name} ({row.Pid}): {ex.Message}"); }
        }
        try { Terminate(root, false); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { errors.Add($"{root.Name} ({root.Pid}): {ex.Message}"); }
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
    }

    public static void Suspend(ProcessRow row, bool resume, bool replay)
    {
        using var handle = Native.OpenProcess(0x1800, false, row.Pid);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!Native.GetProcessTimes(handle, out var start, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!CounterMath.CanTerminate(row, start, Environment.ProcessId, replay)) throw new InvalidOperationException("Process identity changed, is protected, or belongs to a recording.");
        if (!Native.IsProcessCritical(handle, out var critical) || critical) throw new InvalidOperationException("Cannot change a critical or unverified process.");
        var status = resume ? Native.NtResumeProcess(handle) : Native.NtSuspendProcess(handle);
        if (status < 0) throw new InvalidOperationException((resume ? "Resume" : "Suspend") + " failed (0x" + status.ToString("X8") + ").");
    }

    private static void Verify(ProcessRow row)
    {
        using var handle = Native.OpenProcess(0x1000, false, row.Pid);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!Native.GetProcessTimes(handle, out var start, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!CounterMath.CanTerminate(row, start, Environment.ProcessId, false)) throw new InvalidOperationException("Process identity changed, is protected, or belongs to a recording.");
        if (!Native.IsProcessCritical(handle, out var critical) || critical) throw new InvalidOperationException("Cannot terminate a critical or unverified process.");
    }
}
