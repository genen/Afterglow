using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace Afterglow.Windows;

internal sealed record MachineFacts(int PhysicalCores, int Packages, string L1, string L2, string L3, string Virtualization, string VirtualMachine)
{
    public static MachineFacts Current { get; } = Load();

    private static MachineFacts Load()
    {
        var cores = 0;
        var packages = 0;
        uint l1 = 0, l2 = 0, l3 = 0;
        try
        {
            uint length = 0;
            Native.GetLogicalProcessorInformationEx(0xFFFF, 0, ref length);
            if (length is > 0 and < 1_000_000)
            {
                var buffer = Marshal.AllocHGlobal((int)length);
                try
                {
                    if (Native.GetLogicalProcessorInformationEx(0xFFFF, buffer, ref length))
                    {
                        var offset = 0;
                        while (offset + 8 <= length)
                        {
                            var kind = Marshal.ReadInt32(buffer, offset);
                            var size = Marshal.ReadInt32(buffer, offset + 4);
                            if (size < 8 || offset + size > length) break;
                            if (kind == 0) cores++;
                            else if (kind == 3) packages++;
                            else if (kind == 2 && size >= 16)
                            {
                                var level = Marshal.ReadByte(buffer, offset + 8);
                                var bytes = (uint)Marshal.ReadInt32(buffer, offset + 12);
                                if (level == 1) l1 = Math.Max(l1, bytes);
                                else if (level == 2) l2 = Math.Max(l2, bytes);
                                else if (level == 3) l3 = Math.Max(l3, bytes);
                            }
                            offset += size;
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { /* Older than the supported Windows baseline. */ }
        var guest = "Unavailable";
        if (X86Base.IsSupported)
        {
            var leaf = X86Base.CpuId(1, 0);
            guest = (leaf.Ecx & (1u << 31)) != 0 ? "Yes" : "No";
        }
        var virtualization = Native.IsProcessorFeaturePresent(21) ? "Firmware virtualization enabled" : "Unavailable";
        return new(cores, packages, Cache(l1), Cache(l2), Cache(l3), virtualization, guest);
    }

    private static string Cache(uint bytes) => bytes == 0 ? "Unavailable" : Afterglow.Core.Format.Bytes(bytes);
}
