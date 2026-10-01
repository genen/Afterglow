using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Afterglow.Core;

namespace Afterglow.Windows;

internal sealed class HistoryGraph : FrameworkElement
{
    public IReadOnlyList<Sample> Samples { get; set; } = [];
    public Func<Sample, double?> Value { get; set; } = s => s.Cpu;
    public Func<Sample, double?>? SecondaryValue { get; set; }
    public Brush SecondaryStroke { get; set; } = Theme.Kernel;
    public double Ceiling { get; set; } = 100;
    public Func<Sample, double?>? RightValue { get; set; }
    public double RightCeiling { get; set; } = 110;
    public bool AutoScale { get; set; }
    public Brush Stroke { get; set; } = Theme.Cpu;
    public bool Fill { get; set; }
    private double? hover;
    private double WindowSeconds => Samples.Count < 2 ? 60 : Math.Max(60, (Samples[^1].Time - Samples[0].Time).TotalSeconds);
    public HistoryGraph()
    {
        Height = 155; ClipToBounds = true;
        MouseMove += Hover;
        MouseLeave += (_, _) => { hover = null; ToolTip = null; InvalidateVisual(); };
    }
    private void Hover(object sender, MouseEventArgs e)
    {
        if (Samples.Count == 0 || ActualWidth == 0) return;
        hover = Math.Clamp(e.GetPosition(this).X, 0, ActualWidth);
        var time = Samples[^1].Time.AddSeconds((hover.Value / ActualWidth - 1) * WindowSeconds);
        var s = Samples.MinBy(s => Math.Abs((s.Time - time).TotalSeconds))!;
        var value = FormatValue(Value(s), Tag as string, Ceiling);
        var right = RightValue is null ? "" : "\n" + FormatValue(RightValue(s), "temp", RightCeiling);
        ToolTip = $"{s.Time:T}\n{value}{right}\nCPU {Format.Percent(s.Cpu)} · kernel {Format.Percent(s.Kernel)}\nTop: {s.TopCpu}";
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = ActualWidth; var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), 4, 4));
        dc.DrawRectangle(Theme.Graph, null, new Rect(0, 0, w, h));
        var grid = new Pen(Theme.Alpha(Stroke, .12), .6);
        for (var i = 0; i <= 6; i++) dc.DrawLine(grid, new Point(0, h * i / 6), new Point(w, h * i / 6));
        for (var i = 0; i <= 12; i++) dc.DrawLine(grid, new Point(w * i / 12, 0), new Point(w * i / 12, h));
        if (Samples.Count > 0)
        {
            var steps = Samples.Zip(Samples.Skip(1), (a, b) => (b.Time - a.Time).TotalSeconds).Where(s => s > 0).Order().ToArray();
            var gap = steps.Length == 0 ? 2.5 : steps[steps.Length / 2] * 2.5;
            var scale = ScaleFor(Value, Ceiling);
            if (SecondaryValue is not null && (AutoScale || Ceiling <= 0)) scale = Math.Max(scale, ScaleFor(SecondaryValue, 0));
            DrawSeries(dc, Value, Stroke, Fill, gap, scale);
            if (SecondaryValue is not null) DrawSeries(dc, SecondaryValue, SecondaryStroke, false, gap, scale);
            if (RightValue is not null) DrawSeries(dc, RightValue, Theme.Thermal, false, gap, RightCeiling > 0 ? RightCeiling : ScaleFor(RightValue, 0));
        }
        if (hover is { } x) dc.DrawLine(new Pen(Theme.Alpha(Theme.Foreground, .45), 1), new Point(x, 0), new Point(x, h));
        dc.DrawRoundedRectangle(null, new Pen(Theme.Alpha(Theme.Foreground, .08), 1), new Rect(.5, .5, Math.Max(0, w - 1), Math.Max(0, h - 1)), 4, 4);
        dc.Pop();
    }
    private static string FormatValue(double? value, string? tag, double ceiling) => tag switch
    {
        "memory" => Format.Bytes(value),
        "temp" => Format.Celsius(value),
        "percent" => Format.Percent(value),
        "rate" => Format.Rate(value),
        _ => ceiling == 100 ? Format.Percent(value) : Format.Rate(value)
    };

    private double ScaleFor(Func<Sample, double?> value, double ceiling)
    {
        if (ceiling > 0 && !AutoScale) return ceiling;
        var max = 1d;
        foreach (var sample in Samples)
            if (value(sample) is { } n && double.IsFinite(n)) max = Math.Max(max, Math.Abs(n));
        return max <= 0 ? 1 : max * 1.1;
    }

    private void DrawSeries(DrawingContext dc, Func<Sample, double?> value, Brush tone, bool fill, double gap, double scale)
    {
        var points = new List<Point>(); DateTimeOffset? previous = null;
        void Flush()
        {
            if (points.Count == 0) return;
            if (points.Count == 1) { dc.DrawEllipse(tone, null, points[0], 1.5, 1.5); points.Clear(); return; }
            var line = new StreamGeometry();
            using (var ctx = line.Open()) { ctx.BeginFigure(points[0], false, false); ctx.PolyLineTo(points.Skip(1).ToArray(), true, false); } line.Freeze();
            if (fill)
            {
                var area = new StreamGeometry();
                using (var ctx = area.Open())
                {
                    ctx.BeginFigure(new Point(points[0].X, ActualHeight), true, true); ctx.PolyLineTo(points, true, false); ctx.LineTo(new Point(points[^1].X, ActualHeight), true, false);
                }
                var c = ((SolidColorBrush)tone).Color;
                var gradient = new LinearGradientBrush(Color.FromArgb(80, c.R, c.G, c.B), Color.FromArgb(4, c.R, c.G, c.B), 90);
                dc.DrawGeometry(gradient, null, area);
            }
            if (Theme.Bloom)
            {
                dc.DrawGeometry(null, new Pen(Theme.Alpha(tone, .07), 8), line);
                dc.DrawGeometry(null, new Pen(Theme.Alpha(tone, .16), 4), line);
            }
            dc.DrawGeometry(null, new Pen(tone, 1.4), line); points.Clear();
        }
        foreach (var sample in Samples)
        {
            if (value(sample) is not { } v || !double.IsFinite(v)) { Flush(); previous = null; continue; }
            if (previous is { } last && (sample.Time - last).TotalSeconds > gap) Flush();
            var x = ActualWidth * (1 - (Samples[^1].Time - sample.Time).TotalSeconds / WindowSeconds);
            var y = ActualHeight * (1 - Math.Clamp(v / Math.Max(scale, 1e-9), 0, 1));
            points.Add(new Point(x, y)); previous = sample.Time;
        }
        Flush();
    }
}
