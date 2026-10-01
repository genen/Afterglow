using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Afterglow.Windows;

internal static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, char[] buffer, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWow64Process2(SafeProcessHandle process, out ushort processMachine, out ushort nativeMachine);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsProcessCritical(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);
    [DllImport("ntdll.dll")] internal static extern int NtSuspendProcess(SafeProcessHandle process);
    [DllImport("ntdll.dll")] internal static extern int NtResumeProcess(SafeProcessHandle process);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32First(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32Next(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsProcessorFeaturePresent(uint feature);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLogicalProcessorInformationEx(int relationship, nint buffer, ref uint length);
    [DllImport("psapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetPerformanceInfo(ref PerformanceInfo info, uint size);
    [DllImport("powrprof.dll")]
    internal static extern uint CallNtPowerInformation(int level, nint input, int inputLength, out BatteryState output, int outputLength);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] internal static extern uint PdhOpenQuery(string? source, nuint data, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] internal static extern uint PdhAddEnglishCounter(nint query, string path, nuint data, out nint counter);
    [DllImport("pdh.dll")] internal static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")] internal static extern uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out PdhValue value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint size, ref uint count, nint buffer);
    [DllImport("pdh.dll")] internal static extern uint PdhCloseQuery(nint query);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumServicesStatusEx(nint manager, int level, uint type, uint state, nint services, uint size,
        out uint needed, out uint returned, ref uint resume, string? group);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseServiceHandle(nint handle);
    [DllImport("iphlpapi.dll", SetLastError = true)]
    internal static extern uint GetExtendedTcpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll", SetLastError = true)]
    internal static extern uint GetExtendedUdpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int tableClass, uint reserved);
    [DllImport("dxgi.dll")] internal static extern int CreateDXGIFactory1(ref Guid id, out nint factory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetCompressedFileSize(string path, out uint high);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileIdentity info);

    internal static (string Name, double Value)[] CounterArray(nint counter)
    {
        if (counter == 0) return [];
        uint size = 0, count = 0;
        PdhGetFormattedCounterArray(counter, 0x200, ref size, ref count, 0);
        if (size == 0 || size > 8_000_000) return [];
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, 0x200, ref size, ref count, buffer) != 0 || count > 100_000) return [];
            var stride = Marshal.SizeOf<PdhItem>();
            if (stride < 24 || (long)count * stride > size) return [];
            var list = new List<(string, double)>((int)count);
            for (uint i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PdhItem>(buffer + (nint)(i * stride));
                if (item.Status > 1 || !double.IsFinite(item.Value) || item.Value < 0) continue;
                var name = Marshal.PtrToStringUni(item.Name);
                if (!string.IsNullOrWhiteSpace(name)) list.Add((name, item.Value));
            }
            return list.ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static double? CounterScalar(nint counter)
    {
        if (counter == 0 || PdhGetFormattedCounterValue(counter, 0x200, out _, out var value) != 0) return null;
        return value.Status <= 1 && double.IsFinite(value.Value) && value.Value >= 0 ? value.Value : null;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtended;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct PowerStatus
    {
        public byte AcLine, BatteryFlag, BatteryPercent, SystemStatus;
        public uint BatteryLife, BatteryFullLife;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }
    [StructLayout(LayoutKind.Explicit)] internal struct PdhValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }
    [StructLayout(LayoutKind.Explicit, Size = 24)] internal struct PdhItem
    {
        [FieldOffset(0)] public nint Name;
        [FieldOffset(8)] public uint Status;
        [FieldOffset(16)] public double Value;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nint Heap;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct PerformanceInfo
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct BatteryState
    {
        public byte Ac, Present, Charging, Discharging, Spare1, Spare2, Spare3, Spare4;
        public uint MaxCapacity, RemainingCapacity;
        public int Rate;
        public uint EstimatedTime, DefaultAlert1, DefaultAlert2;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] internal struct FileIdentity
    {
        public uint Attributes;
        public long Created, Accessed, Written;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Size = 312)] internal struct DxgiDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideo, DedicatedSystem, SharedSystem;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct ServiceStatus
    {
        public uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint, Pid, Flags;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct ServiceEntry
    {
        public nint Name, DisplayName;
        public ServiceStatus Status;
    }
}
