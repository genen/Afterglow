using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Afterglow.Core;

namespace Afterglow.Windows;

internal static class SmokeTest
{
    // Writes a report because a WinExe normally has no attached console.
    public static int Run()
    {
        var checks = new List<string>();
        var failures = new List<string>();
        void Check(string name, bool ok) { (ok ? checks : failures).Add(name); }
        try
        {
            Check("MEMORYSTATUSEX layout", Marshal.SizeOf<Native.MemoryStatus>() == 64);
            Check("SYSTEM_POWER_STATUS layout", Marshal.SizeOf<Native.PowerStatus>() == 12);
            Check("IO_COUNTERS layout", Marshal.SizeOf<Native.IoCounters>() == 48);
            Check("PDH_FMT_COUNTERVALUE layout", Marshal.SizeOf<Native.PdhValue>() == 16);
            Check("SYSTEM_BATTERY_STATE layout", Marshal.SizeOf<Native.BatteryState>() == 32);
            var performance = new Native.PerformanceInfo { Size = (uint)Marshal.SizeOf<Native.PerformanceInfo>() };
            Check("GetPerformanceInfo", Native.GetPerformanceInfo(ref performance, performance.Size) && performance.PageSize > 0);
            using var collector = new WindowsCollector();
            var first = collector.Collect(); Thread.Sleep(600); var second = collector.Collect();
            Check("CPU warm-up is missing", first.Sample.Cpu is null);
            Check("CPU sample", Environment.ProcessorCount > 64 || second.Sample.Cpu is >= 0 and <= 100);
            Check("Physical memory", second.Sample.Total > 0 && second.Sample.Memory >= 0 && second.Sample.Memory <= second.Sample.Total);
            Check("Core range", second.Sample.Cores is not { } cores || cores.All(core => core is >= 0 and <= 100));
            Check("GPU range", second.Sample.Gpu is null || second.Sample.Gpu is >= 0 and <= 100);
            Check("Self process identity", second.Processes.Any(p => p.Pid == Environment.ProcessId && p.Started > 0));
            Check("Process parent", second.Processes.Any(p => p.Parent > 0));
            foreach (var page in new[] { "System Info", "Startup apps", "Users", "Services", "Connections", "Power & Freq", "Installed Apps", "Disk Space" })
            {
                if (page == "Users") continue;
                Check(page + " provider", Inventory.Collect(page) is not null);
            }
            // Only terminate an explicitly created disposable child, never a pre-existing process.
            using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--test-child")
                { UseShellExecute = false, CreateNoWindow = true })!;
            try
            {
                var row = collector.Collect().Processes.Single(p => p.Pid == child.Id);
                bool rejected = false;
                try { WindowsCollector.Terminate(row with { Started = row.Started + 1 }, false); } catch (InvalidOperationException) { rejected = true; }
                Check("Stale identity refused", rejected && !child.HasExited);
                rejected = false;
                try { WindowsCollector.Terminate(row, true); } catch (InvalidOperationException) { rejected = true; }
                Check("Replay action refused", rejected && !child.HasExited);
                WindowsCollector.Terminate(row, false);
                Check("Disposable child termination", child.WaitForExit(5000));
            }
            finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); } }
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".afterglowtrace");
            try
            {
                TraceFile.Save(path, [first, second]).GetAwaiter().GetResult();
                Check("Native trace round-trip", TraceFile.Load(path).GetAwaiter().GetResult().Frames.Length == 2);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        var report = Path.Combine(Environment.CurrentDirectory, "afterglow-smoke-test.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new { passed = checks, failed = failures }, new JsonSerializerOptions { WriteIndented = true }));
        return failures.Count == 0 ? 0 : 1;
    }
}
