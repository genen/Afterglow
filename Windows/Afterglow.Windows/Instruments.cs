using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Afterglow.Core;

namespace Afterglow.Windows;

internal sealed class VfdMeter : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(VfdMeter), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public bool Vertical { get; init; }
    public int Cells { get; init; } = 22;
    public Brush Tone { get; init; } = Theme.Cpu;
    private double? fraction;
    public double? Fraction
    {
        set
        {
            if (fraction == value) return;
            fraction = value;
            var next = value is { } n && double.IsFinite(n) ? Math.Clamp(n, 0, 1) : 0;
            if (value is null || !IsLoaded) { BeginAnimation(LevelProperty, null); Level = next; InvalidateVisual(); return; }
            BeginAnimation(LevelProperty, new DoubleAnimation(Level, next, TimeSpan.FromSeconds(.28)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var gap = Vertical ? 2.2 : 3;
        var width = Vertical ? ActualWidth : Math.Max(1, (ActualWidth - (Cells - 1) * gap) / Cells);
        var height = Vertical ? Math.Max(1, (ActualHeight - (Cells - 1) * gap) / Cells) : ActualHeight;
        for (var i = 0; i < Cells; i++)
        {
            var on = fraction is not null && i / (double)Cells < Level;
            var rect = new Rect(Vertical ? 0 : i * (width + gap), Vertical ? (Cells - 1 - i) * (height + gap) : 0, width, height);
            if (on && Theme.Bloom)
            {
                dc.DrawRoundedRectangle(null, new Pen(Theme.Alpha(Tone, .08), 8), rect, 2, 2);
                dc.DrawRoundedRectangle(null, new Pen(Theme.Alpha(Tone, .18), 4), rect, 2, 2);
            }
            dc.DrawRoundedRectangle(Theme.Alpha(Tone, on ? .95 : .10), new Pen(Theme.Alpha(Tone, on ? .95 : .16), .6), rect, 1.4, 1.4);
        }
    }
}

// Small vector symbols keep icon weight consistent without depending on SF Symbols or emoji fonts.
internal sealed class Symbol : FrameworkElement
{
    private readonly Geometry geometry;
    public Brush Tone { get; set; }
    public Symbol(string name, Brush? tone = null)
    {
        Width = 16; Height = 16; Tone = tone ?? Theme.Muted;
        geometry = Geometry.Parse(name switch
        {
            "Summary" or "Benchmarks" => "M 3,13 A 7,7 0 1 1 13,13 M 4,5 L 5,6 M 8,2 L 8,4 M 12,5 L 11,6 M 8,10 L 11,5 M 6,13 L 10,13",
            "Performance" => "M 1,8 L 4,8 6,3 8,13 10,6 12,8 15,8",
            "Processes" or "Flight Recorder" => "M 2,3 L 14,3 14,13 2,13 Z M 5,5 L 5,11 M 7,5 L 12,5 M 7,8 L 12,8 M 7,11 L 12,11",
            "System Info" => "M 8,1 A 7,7 0 1 1 7.99,1 M 8,7 L 8,12 M 8,4 L 8,5",
            "Startup apps" or "Power & Freq" => "M 8,1 L 8,8 M 4,3 A 6,6 0 1 0 12,3",
            "Users" => "M 6,2 A 2.5,2.5 0 1 1 5.99,2 M 1,14 L 1,12 Q 1,9 6,9 Q 11,9 11,12 L 11,14 Z M 12,3 Q 16,5 12,7 M 12,10 Q 16,10 15,14",
            "Services" => "M 8,2 L 10,2 10,4 12,5 14,5 15,7 13,8 13,10 14,12 12,14 10,13 8,14 7,15 5,14 5,12 3,11 1,11 1,8 3,7 3,5 5,3 7,4 Z M 9,6 A 3,3 0 1 0 9,12 A 3,3 0 1 0 9,6",
            "Connections" or "Network" => "M 3,2 A 2,2 0 1 1 2.99,2 M 13,2 A 2,2 0 1 1 12.99,2 M 8,11 A 2,2 0 1 1 7.99,11 M 5,4 L 11,4 M 4,6 L 7,11 M 12,6 L 9,11",
            "Installed Apps" => "M 2,2 L 6,2 6,6 2,6 Z M 10,2 L 14,2 14,6 10,6 Z M 2,10 L 6,10 6,14 2,14 Z M 10,10 L 14,10 14,14 10,14 Z",
            "Disk Space" or "Disks" => "M 3,3 L 13,3 15,10 1,10 Z M 1,10 L 1,14 15,14 15,10 M 11,12 L 13,12",
            "CPU" => "M 4,4 L 12,4 12,12 4,12 Z M 6,6 L 10,6 10,10 6,10 Z M 6,1 L 6,4 M 10,1 L 10,4 M 6,12 L 6,15 M 10,12 L 10,15 M 1,6 L 4,6 M 1,10 L 4,10 M 12,6 L 15,6 M 12,10 L 15,10",
            "Memory" => "M 2,4 L 14,4 14,11 2,11 Z M 4,12 L 4,14 M 7,12 L 7,14 M 10,12 L 10,14 M 13,12 L 13,14",
            "Energy" => "M 9,1 L 3,9 8,9 7,15 13,6 8,6 Z",
            "Thermals" => "M 6,9 L 6,3 A 2,2 0 0 1 10,3 L 10,9 A 4,4 0 1 1 6,9 M 8,5 L 8,12",
            "Settings" => "M 1,4 L 15,4 M 1,8 L 15,8 M 1,12 L 15,12 M 5,2 L 5,6 M 11,6 L 11,10 M 6,10 L 6,14",
            "Colors" => "M 8,1 C 1,1 0,6 2,11 C 4,16 9,16 8,12 C 7,9 15,12 15,6 C 15,3 12,1 8,1 Z M 5,4 L 5,5 M 9,3 L 9,4 M 12,5 L 12,6 M 4,8 L 4,9",
            "Record" => "M 8,2 A 6,6 0 1 1 7.99,2 M 8,5 A 3,3 0 1 1 7.99,5",
            "Pause" => "M 5,3 L 5,13 M 11,3 L 11,13",
            "Play" => "M 4,2 L 13,8 4,14 Z",
            "Waveform" => "M 0,9 L 3,9 4.5,3 6,14 8,1 10,15 12,9 16,9",
            _ => "M 2,3 L 14,3 14,12 2,12 Z M 6,15 L 10,15 M 8,12 L 8,15"
        }); geometry.Freeze();
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.PushTransform(new ScaleTransform(ActualWidth / 16, ActualHeight / 16));
        var pen = new Pen(Tone, 1.15) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, pen, geometry); dc.Pop();
    }
}

internal sealed class CpuUsageBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double?), typeof(CpuUsageBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public double? Value { get => (double?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public CpuUsageBar() { Height = 17; Margin = new Thickness(4, 3, 6, 3); }
    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(0, 0, ActualWidth, ActualHeight); dc.DrawRoundedRectangle(Theme.Brush("#303032"), null, rect, 2, 2);
        if (Value is { } v && double.IsFinite(v)) dc.DrawRoundedRectangle(Theme.Alpha(Theme.Cpu, .75), null, new Rect(0, 0, ActualWidth * Math.Clamp(v / 100, 0, 1), ActualHeight), 2, 2);
        var text = new FormattedText(Format.Percent(Value), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Consolas"), 10, Theme.Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(5, (ActualHeight - text.Height) / 2));
    }
}

internal sealed class BlockMap : FrameworkElement
{
    public InfoRow[] Rows { get; set; } = [];
    public BlockMap() { Height = 72; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var ranked = Rows.Where(row => row.Value != "Reparse point").Take(48).ToArray();
        dc.DrawRoundedRectangle(Theme.Graph, null, new Rect(0, 0, ActualWidth, ActualHeight), 4, 4);
        if (ranked.Length == 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        double weights = ranked.Length * (ranked.Length + 1) / 2d;
        Brush[] tones = [Theme.Cpu, Theme.Memory, Theme.Network, Theme.Energy, Theme.Thermal, Theme.Gpu];
        var x = 0d;
        for (var i = 0; i < ranked.Length; i++)
        {
            var width = ActualWidth * (ranked.Length - i) / weights;
            dc.DrawRectangle(Theme.Alpha(tones[i % tones.Length], .88), null, new Rect(x, 0, Math.Max(0, width - 1), ActualHeight));
            x += width;
        }
    }
}

internal sealed class RibbonView : FrameworkElement
{
    private readonly (Func<Sample, double> Amplitude, Brush Tone, double Shift)[] bands =
    [
        (sample => Math.Clamp((sample.Cpu ?? 0) / 100, 0, 1), Theme.Cpu, 0),
        (sample => Math.Clamp(((sample.DiskRead ?? 0) + (sample.DiskWrite ?? 0)) / 80_000_000d, 0, 1), Theme.Thermal, 1.3),
        (sample => Math.Clamp((sample.Gpu ?? 0) / 100, 0, 1), Theme.Gpu, 2.4),
        (sample => sample.Total is > 0 ? Math.Clamp((sample.Memory ?? 0) / sample.Total.Value, 0, 1) : 0, Theme.Memory, 3.6)
    ];
    private bool running;
    private double phase;
    private TimeSpan last;
    public IReadOnlyList<Sample> Samples { get; set; } = [];
    public void Start()
    {
        if (running) return;
        running = true;
        CompositionTarget.Rendering += OnRendering;
    }
    public void Stop()
    {
        if (!running) return;
        running = false;
        CompositionTarget.Rendering -= OnRendering;
    }
    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs args)
        {
            if (last != TimeSpan.Zero) phase += (args.RenderingTime - last).TotalSeconds * 1.4;
            last = args.RenderingTime;
        }
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth; var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
        const int count = 280;
        for (var band = 0; band < bands.Length; band++)
        {
            var (amplitude, tone, shift) = bands[band];
            var baseline = height * (0.22 + band * 0.18);
            var points = new Point[count];
            for (var i = 0; i < count; i++)
            {
                var sample = Samples.Count == 0 ? null : Samples[(int)(i / (double)(count - 1) * (Samples.Count - 1))];
                var amount = sample is null ? 0 : amplitude(sample);
                var y = baseline + Math.Sin(i * 0.045 + phase + shift) * amount * height * 0.16;
                points[i] = new Point(width * i / (count - 1), y);
            }
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open()) { ctx.BeginFigure(points[0], false, false); ctx.PolyLineTo(points.Skip(1).ToArray(), true, true); }
            geometry.Freeze();
            if (Theme.Bloom) dc.DrawGeometry(null, new Pen(Theme.Alpha(tone, .35), 9) { LineJoin = PenLineJoin.Round }, geometry);
            dc.DrawGeometry(null, new Pen(tone, 2.2) { LineJoin = PenLineJoin.Round }, geometry);
        }
    }
}

internal static class IconCache
{
    private static readonly Dictionary<string, ImageSource?> cache = new(StringComparer.OrdinalIgnoreCase);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfo
    {
        public nint Icon;
        public int Index;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Display;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string Type;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attributes, ref FileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);

    public static ImageSource? Image(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (cache.TryGetValue(path, out var found)) return found;
        if (cache.Count >= 500) cache.Clear();
        ImageSource? image = null;
        try
        {
            var info = new FileInfo();
            var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<FileInfo>(), 0x100 | 0x1);
            if (result != 0 && info.Icon != 0)
            {
                try
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    image = source;
                }
                finally { DestroyIcon(info.Icon); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException) { }
        cache[path] = image;
        return image;
    }
}

internal sealed class PathIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => IconCache.Image(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

internal sealed class DepthMarginConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => new Thickness(value is int depth ? depth * 16 : 0, 0, 6, 0);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
