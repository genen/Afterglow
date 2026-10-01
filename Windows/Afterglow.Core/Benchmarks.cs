using System.Diagnostics;
using System.Security.Cryptography;

namespace Afterglow.Core;

public static class Benchmarks
{
    public static string Run(string kind)
    {
        if (kind == "CPU")
        {
            var timer = Stopwatch.StartNew(); ulong value = 0x123456789abcdef;
            const int rounds = 20_000_000;
            for (var i = 0; i < rounds; i++) { value ^= value << 13; value ^= value >> 7; value ^= value << 17; }
            return $"CPU · {rounds / timer.Elapsed.TotalSeconds / 1e6:0.0} million iterations/s\nSingle-thread xorshift workload\nChecksum: {value}";
        }
        var data = new byte[64 * 1024 * 1024]; Array.Fill<byte>(data, 0xA5);
        if (kind == "Memory")
        {
            var timer = Stopwatch.StartNew(); byte[] hash = [];
            for (var i = 0; i < 16; i++) hash = SHA256.HashData(data);
            return $"Memory + SHA-256 · {1 / timer.Elapsed.TotalSeconds:0.00} GiB/s\n1 GiB hashed\nSHA-256: {Convert.ToHexString(hash)}";
        }
        if (kind != "Disk") throw new ArgumentException("Unknown workload.", nameof(kind));
        var path = Path.Combine(Path.GetTempPath(), "afterglow-benchmark-" + Guid.NewGuid().ToString("N"));
        try
        {
            var timer = Stopwatch.StartNew();
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(data); file.Flush(true); }
            var write = timer.Elapsed.TotalSeconds; timer.Restart();
            var read = File.ReadAllBytes(path); var readTime = timer.Elapsed.TotalSeconds;
            if (!data.AsSpan().SequenceEqual(read)) throw new IOException("Read verification failed.");
            return $"Disk · 64 MiB temporary file\nWrite + flush: {Format.Rate(data.Length / write)}\nCached read: {Format.Rate(read.Length / readTime)}\nRead verified: yes";
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
