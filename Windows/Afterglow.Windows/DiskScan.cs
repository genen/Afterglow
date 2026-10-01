using System.Globalization;
using System.Runtime.InteropServices;
using Afterglow.Core;

namespace Afterglow.Windows;

internal static class DiskScan
{
    public static (InfoRow[] Rows, string Status) Measure(string root, CancellationToken cancel, IProgress<string>? progress)
    {
        if (!Directory.Exists(root)) return ([], "Folder is unavailable.");
        string[] children;
        try { children = Directory.GetFileSystemEntries(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ([], "Could not read the folder: " + ex.Message); }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var denied = 0;
        var count = 0;
        var values = new List<(string Name, string Path, long Size)>();
        foreach (var child in children)
        {
            if (cancel.IsCancellationRequested) break;
            long total = 0;
            var skipped = false;
            try
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0) skipped = true;
                else if ((attributes & FileAttributes.Directory) == 0) total = SizeOf(child, seen, ref denied);
                else total = FolderSize(child, seen, ref denied, ref count, cancel, progress);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { denied++; }
            values.Add((Path.GetFileName(child), child, skipped ? -1 : total));
        }
        var rows = values.OrderByDescending(item => item.Size).Select(item => new InfoRow(item.Name, item.Path, item.Size < 0 ? "Reparse point" : Format.Bytes(item.Size), item.Path)).ToArray();
        var stopped = cancel.IsCancellationRequested || count >= 1_000_000;
        return (rows, $"{(stopped ? "Partial scan" : "Scan complete")} · {count:N0} entries · {denied:N0} unreadable · size on disk; reparse points are skipped and hard links are counted once.");
    }

    private static long FolderSize(string root, HashSet<string> seen, ref int denied, ref int count, CancellationToken cancel, IProgress<string>? progress)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && !cancel.IsCancellationRequested && count < 1_000_000)
        {
            var dir = pending.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { denied++; continue; }
            foreach (var entry in entries)
            {
                if (cancel.IsCancellationRequested || count >= 1_000_000) break;
                count++;
                if (count % 5000 == 0) progress?.Report($"Scanning… {count:N0} entries");
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                    else total += SizeOf(entry, seen, ref denied);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { denied++; }
            }
        }
        return total;
    }

    private static long SizeOf(string path, HashSet<string> seen, ref int denied)
    {
        var compressed = Compressed(path);
        try
        {
            using var handle = Native.CreateFile(path, 0x80, 7, 0, 3, 0x02200000, 0);
            if (!handle.IsInvalid && Native.GetFileInformationByHandle(handle, out var info))
            {
                var key = string.Create(CultureInfo.InvariantCulture, $"{info.VolumeSerial:X}-{info.IndexHigh:X}-{info.IndexLow:X}");
                if (!seen.Add(key)) return 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { denied++; }
        if (compressed is { } size) return size;
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { denied++; return 0; }
    }

    private static long? Compressed(string path)
    {
        var low = Native.GetCompressedFileSize(path, out var high);
        if (low == uint.MaxValue && Marshal.GetLastWin32Error() != 0) return null;
        return ((long)high << 32) | low;
    }
}
