using System.Runtime.InteropServices;

namespace Afterglow.Windows;

internal sealed class PdhSource : IDisposable
{
    private readonly Dictionary<string, nint> counters = [];
    private nint query;
    private bool primed;
    public bool Ready { get; private set; }

    public PdhSource()
    {
        if (Native.PdhOpenQuery(null, 0, out query) != 0) { query = 0; return; }
        Add("cpu", @"\Processor Information(*)\% Processor Time");
        Add("kernel", @"\Processor Information(*)\% Privileged Time");
        Add("freq", @"\Processor Information(_Total)\Processor Frequency");
        Add("diskRead", @"\PhysicalDisk(*)\Disk Read Bytes/sec");
        Add("diskWrite", @"\PhysicalDisk(*)\Disk Write Bytes/sec");
        Add("diskIdle", @"\PhysicalDisk(*)\% Idle Time");
        Add("gpu", @"\GPU Engine(*)\Utilization Percentage");
        Add("page", @"\Paging File(_Total)\% Usage");
        Add("thermal", @"\Thermal Zone Information(*)\Temperature");
        Add("thermalHi", @"\Thermal Zone Information(*)\High Precision Temperature");
    }

    public bool Has(string key) => counters.ContainsKey(key);
    public (string Name, double Value)[] Array(string key) => Ready && counters.TryGetValue(key, out var counter) ? Native.CounterArray(counter) : [];
    public double? Scalar(string key) => Ready && counters.TryGetValue(key, out var counter) ? Native.CounterScalar(counter) : null;

    public void Collect()
    {
        Ready = false;
        if (query == 0 || Native.PdhCollectQueryData(query) != 0) { primed = false; return; }
        if (!primed) { primed = true; return; }
        Ready = true;
    }

    public void Dispose()
    {
        if (query != 0) Native.PdhCloseQuery(query);
        query = 0;
        counters.Clear();
    }

    private void Add(string key, string path)
    {
        if (query != 0 && Native.PdhAddEnglishCounter(query, path, 0, out var counter) == 0 && counter != 0) counters[key] = counter;
    }
}

internal static class ProcessSnapshot
{
    public readonly record struct Info(int Parent, int Threads, int Priority);

    public static Dictionary<int, Info> Take()
    {
        var map = new Dictionary<int, Info>();
        var snapshot = Native.CreateToolhelp32Snapshot(2, 0);
        if (snapshot == 0 || snapshot == -1) return map;
        try
        {
            var entry = new Native.ProcessEntry { Size = (uint)Marshal.SizeOf<Native.ProcessEntry>() };
            if (!Native.Process32First(snapshot, ref entry)) return map;
            do
            {
                map[(int)entry.ProcessId] = new Info(entry.ParentProcessId > int.MaxValue ? 0 : (int)entry.ParentProcessId,
                    entry.Threads > int.MaxValue ? 0 : (int)entry.Threads, entry.Priority);
                entry.Size = (uint)Marshal.SizeOf<Native.ProcessEntry>();
            }
            while (Native.Process32Next(snapshot, ref entry));
        }
        finally { Native.CloseHandle(snapshot); }
        return map;
    }
}

internal static class GpuAdapters
{
    private static readonly Guid FactoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");

    public static Dictionary<ulong, (string Name, double Dedicated)> Current()
    {
        var map = new Dictionary<ulong, (string, double)>();
        nint factory = 0;
        try
        {
            var factoryId = FactoryId;
            if (Native.CreateDXGIFactory1(ref factoryId, out factory) < 0 || factory == 0) return map;
            for (uint index = 0; index < 32; index++)
            {
                if (!Invoke(factory, 12, index, out var adapter) || adapter == 0) break;
                try
                {
                    if (!Describe(adapter, out var description)) continue;
                    if ((description.Flags & 2) != 0) continue;
                    var name = description.Description?.Trim().TrimEnd('\0');
                    if (string.IsNullOrWhiteSpace(name)) name = "GPU";
                    var id = description.LuidLow | ((ulong)(uint)description.LuidHigh << 32);
                    map[id] = (name, description.DedicatedVideo);
                }
                finally { Release(adapter); }
            }
        }
        catch (DllNotFoundException) { return map; }
        finally { Release(factory); }
        return map;
    }

    private static bool Invoke(nint factory, int slot, uint index, out nint adapter)
    {
        adapter = 0;
        var function = Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), slot * IntPtr.Size);
        var enumerate = Marshal.GetDelegateForFunctionPointer<EnumAdapters>(function);
        return enumerate(factory, index, out adapter) >= 0;
    }

    private static bool Describe(nint adapter, out Native.DxgiDescription description)
    {
        var function = Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter), 10 * IntPtr.Size);
        var describe = Marshal.GetDelegateForFunctionPointer<GetDescription>(function);
        return describe(adapter, out description) >= 0;
    }

    private static void Release(nint pointer)
    {
        if (pointer == 0) return;
        var function = Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer), 2 * IntPtr.Size);
        Marshal.GetDelegateForFunctionPointer<ReleasePointer>(function)(pointer);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapters(nint self, uint index, out nint adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDescription(nint self, out Native.DxgiDescription description);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleasePointer(nint self);
}
