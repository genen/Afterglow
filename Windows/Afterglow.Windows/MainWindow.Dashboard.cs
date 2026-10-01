using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Afterglow.Core;

namespace Afterglow.Windows;

internal sealed partial class MainWindow
{
    private readonly List<(VfdMeter Meter, Func<Sample, double?> Value)> meters = [];
    private readonly List<(VfdMeter Meter, Func<Sample, double?> Value)> compactMeters = [];
    private HistoryGraph? compactGraph;
    private RibbonView? ribbon;

    private UIElement Dashboard()
    {
        var stack = new StackPanel { Margin = new Thickness(16) };
        var top = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        top.ColumnDefinitions.Add(new() { Width = new GridLength(210) });
        top.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star), MinWidth = 270 });
        top.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star), MinWidth = 320, MaxWidth = 400 });

        var gauges = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Height = 210 };
        gauges.Children.Add(Gauge("CPU", Theme.Cpu, s => s.Cpu / 100, s => Format.Percent(s.Cpu)));
        gauges.Children.Add(Gauge("Power", Theme.Energy, s => s.PackageWatts / 750, s => s.PackageWatts is null ? "N/A" : Format.Watts(s.PackageWatts)));
        gauges.Children.Add(Gauge("Temp", Theme.Thermal, s => s.Temperature / 100, s => s.Temperature is null ? "N/A" : Format.Celsius(s.Temperature)));
        gauges.Children.Add(Gauge("GPU", Theme.Gpu, s => s.Gpu / 100, s => s.Gpu is null ? "N/A" : Format.Percent(s.Gpu)));
        var gaugeCard = Card(gauges); gaugeCard.VerticalAlignment = VerticalAlignment.Top; gaugeCard.Margin = new Thickness(0, 0, 12, 0); top.Children.Add(gaugeCard);

        var cpu = new StackPanel(); cpu.Children.Add(CardHeading("CPU Overview", "CPU", Theme.Cpu, s => Format.Percent(s.Cpu)));
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 7) };
        legend.Children.Add(Text("⌁  Utilization", 10, Theme.Cpu)); legend.Children.Add(new TextBlock { Text = "⚙  Kernel", FontSize = 10, Foreground = Theme.Kernel, Margin = new Thickness(18, 3, 0, 3) }); cpu.Children.Add(legend);
        legend.Children.Add(new TextBlock { Text = "°  Temperature", FontSize = 10, Foreground = Theme.Thermal, Margin = new Thickness(18, 3, 0, 3) });
        var cpuGraph = NewGraph(s => s.Cpu, 100, Theme.Cpu, 168); cpuGraph.Tag = "percent"; cpuGraph.SecondaryValue = s => s.Kernel; cpuGraph.SecondaryStroke = Theme.Kernel; cpuGraph.RightValue = s => s.Temperature; cpuGraph.RightCeiling = 110; cpu.Children.Add(cpuGraph);
        var cpuFoot = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var speed = Reading(s => s.Frequency is { } mhz ? $"{mhz:0} MHz" : "Clock unavailable", 9, Theme.Muted); speed.HorizontalAlignment = HorizontalAlignment.Right; DockPanel.SetDock(speed, Dock.Right); cpuFoot.Children.Add(speed);
        cpuFoot.Children.Add(Text($"{Environment.ProcessorCount} logical processors", 9, Theme.Muted)); cpu.Children.Add(cpuFoot);
        var cpuCard = Card(cpu); cpuCard.VerticalAlignment = VerticalAlignment.Top; cpuCard.Margin = new Thickness(0, 0, 12, 0); Grid.SetColumn(cpuCard, 1); top.Children.Add(cpuCard);

        var topProcesses = new StackPanel(); topProcesses.Children.Add(CardHeading("Top CPU processes (12)", "Processes", Theme.Cpu, _ => $"{Current?.Processes.Length ?? 0} total", 14));
        table = SummaryGrid(); topProcesses.Children.Add(table);
        var processCard = Card(topProcesses); processCard.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(processCard, 2); top.Children.Add(processCard); stack.Children.Add(top);

        var memory = new StackPanel(); memory.Children.Add(CardHeading("Memory Utilization", "Memory", Theme.Memory, s => $"{Format.Bytes(s.Memory)} / {Format.Bytes(s.Total)}"));
        var memoryBody = new Grid { Margin = new Thickness(0, 4, 0, 0) }; memoryBody.ColumnDefinitions.Add(new() { Width = new GridLength(40) }); memoryBody.ColumnDefinitions.Add(new());
        var memGauge = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        memGauge.Children.Add(Meter(s => s.Total > 0 ? s.Memory / s.Total : null, Theme.Memory, true, 118, 18));
        memGauge.Children.Add(Reading(s => s.Total > 0 ? Format.Percent(s.Memory / s.Total * 100) : "N/A", 9, Theme.Memory)); memoryBody.Children.Add(memGauge);
        var memoryGraph = NewGraph(s => s.Memory, Current?.Sample.Total ?? 1, Theme.Memory, 118); memoryGraph.Tag = "memory"; memoryGraph.Fill = true; Grid.SetColumn(memoryGraph, 1); memoryBody.Children.Add(memoryGraph); memory.Children.Add(memoryBody);
        var memoryFoot = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
        memoryFoot.Children.Add(Reading(s => $"Pressure {Sensors.PressureName(s.Pressure)}", 10, Theme.Muted));
        memoryFoot.Children.Add(Reading(s => $"Available {Format.Bytes(s.Available)}", 10, Theme.Muted));
        var memoryNote = Reading(s => MemoryDetail(s), 10, Theme.Muted); memoryNote.TextAlignment = TextAlignment.Right; memoryFoot.Children.Add(memoryNote); memory.Children.Add(memoryFoot);
        var memCard = Card(memory); memCard.Margin = new Thickness(0, 0, 0, 12); stack.Children.Add(memCard);

        var resources = new Grid(); for (var i = 0; i < 3; i++) resources.ColumnDefinitions.Add(new()); for (var i = 0; i < 2; i++) resources.RowDefinitions.Add(new() { Height = GridLength.Auto });
        UIElement[] tiles = [
            Resource("Disks", "Disks", Theme.Cpu, s => Format.Rate(s.DiskRead + s.DiskWrite), s => $"R: {Format.Rate(s.DiskRead)}   W: {Format.Rate(s.DiskWrite)}   Busy {Format.Percent(s.DiskBusy)}", s => s.DiskBusy / 100 ?? (s.DiskRead + s.DiskWrite) / 500_000_000, "Busy is the busiest disk. The fallback scale is 500 MB/s."),
            Resource("Network", "Network", Theme.Network, s => Format.Rate(s.Receive + s.Send), s => $"R: {Format.Rate(s.Receive)}   S: {Format.Rate(s.Send)}", s => (s.Receive + s.Send) / 12_500_000, "Meter scale: 12.5 MB/s combined throughput"),
            Resource("Energy", "Energy", Theme.Energy, s => s.PackageWatts is { } package ? Format.Watts(package) : s.BatteryWatts is null ? "Unavailable" : Format.Watts(s.BatteryWatts), s => EnergyCaption(s), s => s.PackageWatts is { } package ? package / 750 : s.BatteryWatts is { } watts ? Math.Abs(watts) / 100 : null),
            Resource("GPU 0", "Window", Theme.Gpu, s => s.Gpu is null ? "Unavailable" : Format.Percent(s.Gpu), s => s.Gpu is null ? "GPU utilization unavailable" : $"{s.GpuName ?? "GPU"} · dedicated {Format.Bytes(s.GpuMemory)}", s => s.Gpu / 100),
            Resource("NPU 0", "CPU", Theme.Npu, _ => "Unavailable", _ => "No generic NPU counter", _ => null),
            Resource("Thermals", "Thermals", Theme.Thermal, s => s.Temperature is null ? "Unavailable" : Format.Celsius(s.Temperature), s => s.Temperature is null ? SensorAccess.Unavailable : Sensors.ThermalCaption(s.Temperature, s.TempMin, s.TempNames), s => s.Temperature / 100)
        ];
        for (var i = 0; i < tiles.Length; i++) { Grid.SetColumn(tiles[i], i % 3); Grid.SetRow(tiles[i], i / 3); ((FrameworkElement)tiles[i]).Margin = new Thickness(0, 0, i % 3 == 2 ? 0 : 12, i < 3 ? 12 : 0); resources.Children.Add(tiles[i]); }
        stack.Children.Add(resources);
        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private static string EnergyCaption(Sample sample)
    {
        var total = sample.PackageWatts is { } watts ? $"Total {Format.Watts(watts)}" : "Measured power unavailable";
        var parts = string.IsNullOrWhiteSpace(sample.PowerDetail) ? null : sample.PowerDetail;
        var battery = sample.BatteryWatts is { } rate ? $"Battery {Format.Watts(rate)} {(rate > 0 ? "discharging" : rate < 0 ? "charging" : "idle")}" : "Battery rate unavailable";
        return string.Join(" · ", new[] { total, parts, battery, sample.PowerSource }.Where(item => !string.IsNullOrWhiteSpace(item)));
    }

    private UIElement Gauge(string title, Brush color, Func<Sample, double?> value, Func<Sample, string> caption)
    {
        var panel = new DockPanel { Margin = new Thickness(4, 0, 4, 0) };
        var label = Text(title, 10, color); label.FontWeight = FontWeights.SemiBold; label.TextAlignment = TextAlignment.Center; label.Margin = new Thickness(0, 0, 0, 9); DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label);
        var number = Reading(caption, 9, color); number.TextAlignment = TextAlignment.Center; number.Margin = new Thickness(0, 9, 0, 0); DockPanel.SetDock(number, Dock.Bottom); panel.Children.Add(number);
        var meter = Meter(value, color, true, double.NaN);
        meter.ToolTip = title switch
        {
            "CPU" => "CPU utilization · 0–100%",
            "Power" => "Sum of CPU package, memory, GPU, motherboard, and PSU sensors. Not wall-outlet power. Scaled to 750 W",
            "Temp" => "CPU thermal zone when one is published; otherwise the hottest zone",
            "GPU" => "Busiest GPU engine",
            _ => title
        };
        panel.Children.Add(meter); return panel;
    }
    private TextBlock Reading(Func<Sample, string> value, double size, Brush color)
    {
        var text = Text(Current is { } f ? value(f.Sample) : "—", size, color); text.FontFamily = new FontFamily("Consolas"); text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis; readings.Add((text, value)); return text;
    }
    private VfdMeter Meter(Func<Sample, double?> value, Brush color, bool vertical, double height, int cells = 22)
    {
        var meter = new VfdMeter { Tone = color, Vertical = vertical, Height = height, Cells = cells }; meter.Fraction = Current is { } f ? value(f.Sample) : null; meters.Add((meter, value)); return meter;
    }
    private UIElement CardHeading(string title, string symbol, Brush color, Func<Sample, string>? value = null, double size = 16)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        if (value is not null)
        {
            var number = Reading(value, size, color); number.FontWeight = FontWeights.SemiBold; number.Margin = new Thickness(8, 0, 0, 0); number.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(number, Dock.Right); row.Children.Add(number);
        }
        var icon = new Symbol(symbol, color) { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
        var name = Text(title, size); name.FontWeight = FontWeights.Medium; name.Margin = new Thickness(0); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; row.Children.Add(name); return row;
    }
    private Border Resource(string title, string icon, Brush color, Func<Sample, string> value, Func<Sample, string> detail, Func<Sample, double?> fraction, string? tooltip = null)
    {
        var stack = new StackPanel(); stack.Children.Add(CardHeading(title, icon, color, value, 15));
        var caption = Reading(detail, 10, Theme.Muted); caption.FontFamily = new FontFamily("Segoe UI"); caption.Margin = new Thickness(0, 0, 0, 8); stack.Children.Add(caption);
        var meter = Meter(fraction, color, false, 14); meter.ToolTip = tooltip ?? "Unavailable readings leave meter segments unlit"; stack.Children.Add(meter); return Card(stack);
    }
    private HistoryGraph NewGraph(Func<Sample, double?> value, double ceiling, Brush stroke, double height)
    {
        var graph = new HistoryGraph { Value = value, Ceiling = ceiling, Stroke = stroke, Height = height }; graphs.Add(graph); return graph;
    }
    private Border GraphCard(string title, Func<Sample, double?> value, double ceiling, Brush stroke, string? tag = null)
    {
        var stack = new StackPanel(); stack.Children.Add(CardHeading(title, tag == "memory" ? "Memory" : "Performance", stroke, s => tag switch
        {
            "memory" => Format.Bytes(value(s)),
            "temp" => Format.Celsius(value(s)),
            "watts" => Format.Watts(value(s)),
            "percent" => Format.Percent(value(s)),
            "rate" => Format.Rate(value(s)),
            _ => ceiling == 100 ? Format.Percent(value(s)) : Format.Rate(value(s))
        }));
        var graph = NewGraph(value, ceiling, stroke, 168); graph.Tag = tag; stack.Children.Add(graph); var card = Card(stack); card.Margin = new Thickness(0, 0, 0, 12); return card;
    }
    private static string MemoryDetail(Sample sample)
    {
        var parts = new List<string>();
        if (sample.Cached is not null) parts.Add($"Cached {Format.Bytes(sample.Cached)}");
        if (sample.Committed is not null || sample.CommitLimit is not null) parts.Add($"Commit {Format.Bytes(sample.Committed)} / {Format.Bytes(sample.CommitLimit)}");
        if (sample.PageFile is not null) parts.Add($"Page {Format.Percent(sample.PageFile)}");
        return parts.Count == 0 ? "Cache and commit unavailable" : string.Join(" · ", parts);
    }
    private static DataGrid SummaryGrid()
    {
        var grid = GridTable(); grid.Height = 244; grid.RowHeight = 18; grid.FontSize = 10; grid.FontFamily = new FontFamily("Consolas"); grid.ColumnHeaderHeight = 22;
        grid.Background = Theme.Panel; grid.RowBackground = Theme.Panel; grid.AlternatingRowBackground = Theme.RowAlt;
        var header = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), (Style)Application.Current.FindResource(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)));
        header.Setters.Add(new Setter(Control.BackgroundProperty, Theme.Panel)); header.Setters.Add(new Setter(Control.FontSizeProperty, 9d)); header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(2, 2, 2, 2))); header.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0))); grid.ColumnHeaderStyle = header;
        ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Disabled); ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Disabled);
        Column(grid, "PID", "Pid", 45); Column(grid, "Name", "Name", 1, true); Column(grid, "CPU", "CpuText", 60, sort: "Cpu");
        var cpuText = new Style(typeof(TextBlock)); cpuText.Setters.Add(new Setter(TextBlock.ForegroundProperty, Theme.Cpu)); ((DataGridTextColumn)grid.Columns[^1]).ElementStyle = cpuText;
        Column(grid, "GPU", "GpuText", 52, sort: "Gpu");
        Column(grid, "Memory", "MemoryText", 72, sort: "Memory"); return grid;
    }
}
