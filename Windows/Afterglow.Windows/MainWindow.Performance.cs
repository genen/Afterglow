using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Afterglow.Core;

namespace Afterglow.Windows;

internal sealed partial class MainWindow
{
    private string perfSection = "CPU";
    private string diskPick = "All";
    private string nicPick = "All";
    private ComboBox? diskList, nicList;
    private bool suppressPick;
    private CancellationTokenSource? scanCancel;
    private int scanGeneration;
    private bool scanning;
    private InfoRow[] scanRows = [];
    private string scanStatus = "Choose a folder to measure size on disk.";
    private TextBlock? scanLabel;
    private DataGrid? scanTable;
    private BlockMap? scanMap;

    private UIElement PerformancePage()
    {
        var root = new Grid { Margin = new Thickness(16, 0, 16, 16) };
        root.ColumnDefinitions.Add(new() { Width = new GridLength(210) });
        root.ColumnDefinitions.Add(new());
        var rail = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        foreach (var section in new[] { "CPU", "Memory", "GPU 0", "NPU 0", "Disks", "Network", "Energy", "Thermals" })
        {
            var button = Button(section, (_, _) => { perfSection = section; ShowPage("Performance"); });
            if (section == perfSection) button.BorderBrush = Theme.Accent;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            rail.Children.Add(button);
            rail.Children.Add(Meter(SectionFraction(section), SectionTone(section), false, 12, 18));
        }
        root.Children.Add(new ScrollViewer { Content = rail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var detail = new ScrollViewer { Content = PerformanceDetail(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(detail, 1);
        root.Children.Add(detail);
        return root;
    }

    private static Brush SectionTone(string section) => section switch
    {
        "Memory" => Theme.Memory,
        "GPU 0" => Theme.Gpu,
        "NPU 0" => Theme.Npu,
        "Network" => Theme.Network,
        "Energy" => Theme.Energy,
        "Thermals" => Theme.Thermal,
        _ => Theme.Cpu
    };

    private static Func<Sample, double?> SectionFraction(string section) => section switch
    {
        "CPU" => s => s.Cpu / 100,
        "Memory" => s => s.Total > 0 ? s.Memory / s.Total : null,
        "GPU 0" => s => s.Gpu / 100,
        "NPU 0" => _ => null,
        "Disks" => s => s.DiskBusy / 100,
        "Network" => s => (s.Receive + s.Send) / 12_500_000,
        "Energy" => s => s.BatteryWatts is { } watts ? Math.Abs(watts) / 100 : null,
        "Thermals" => s => s.Temperature / 100,
        _ => _ => null
    };

    private UIElement PerformanceDetail()
    {
        var stack = new StackPanel();
        var sample = Current?.Sample;
        switch (perfSection)
        {
            case "Memory":
                stack.Children.Add(GraphCard("Physical memory", s => s.Memory, sample?.Total ?? 1, Theme.Memory, "memory"));
                graphs[^1].Fill = true;
                stack.Children.Add(DetailReading(s => $"Used {Format.Bytes(s.Memory)} / {Format.Bytes(s.Total)}\nAvailable {Format.Bytes(s.Available)} · cached {Format.Bytes(s.Cached)}\nCommit {Format.Bytes(s.Committed)} / {Format.Bytes(s.CommitLimit)} · page file {Format.Percent(s.PageFile)}\nPressure {Sensors.PressureName(s.Pressure)}"));
                break;
            case "GPU 0":
                stack.Children.Add(GraphCard("GPU utilization", s => s.Gpu, 100, Theme.Gpu, "percent"));
                stack.Children.Add(DetailReading(s => $"{s.GpuName ?? "GPU"} · dedicated {Format.Bytes(s.GpuMemory)}\n3D {Format.Percent(s.Gpu3D)} · Copy {Format.Percent(s.GpuCopy)} · Compute {Format.Percent(s.GpuCompute)}\nDecode {Format.Percent(s.GpuDecode)} · Encode {Format.Percent(s.GpuEncode)}"));
                break;
            case "NPU 0":
                stack.Children.Add(Text("NPU utilization is unavailable.", 18, Theme.Npu));
                stack.Children.Add(Text("Windows has no generic NPU counter that this build can read without inventing a value. The meter stays unlit.", 13, Theme.Muted));
                stack.Children.Add(Meter(_ => null, Theme.Npu, false, 16));
                break;
            case "Disks":
                stack.Children.Add(Picker("Disk", diskPick, DiskNames(sample), value => diskPick = value));
                stack.Children.Add(RateGraph("Disk read", s => PickedDisk(s)?.Read ?? (diskPick == "All" ? s.DiskRead : null), Theme.Cpu, s => PickedDisk(s)?.Write ?? (diskPick == "All" ? s.DiskWrite : null), Theme.Thermal));
                stack.Children.Add(DetailReading(s => $"Busy {Format.Percent(diskPick == "All" ? s.DiskBusy : PickedDisk(s)?.Busy)}\nR {Format.Rate(diskPick == "All" ? s.DiskRead : PickedDisk(s)?.Read)}   W {Format.Rate(diskPick == "All" ? s.DiskWrite : PickedDisk(s)?.Write)}"));
                break;
            case "Network":
                stack.Children.Add(Picker("Adapter", nicPick, NicNames(sample), value => nicPick = value));
                stack.Children.Add(RateGraph("Network receive", s => PickedNic(s)?.Receive ?? (nicPick == "All" ? s.Receive : null), Theme.Network, s => PickedNic(s)?.Send ?? (nicPick == "All" ? s.Send : null), Theme.Energy));
                stack.Children.Add(DetailReading(s => $"↓ {Format.Rate(nicPick == "All" ? s.Receive : PickedNic(s)?.Receive)}   ↑ {Format.Rate(nicPick == "All" ? s.Send : PickedNic(s)?.Send)}"));
                break;
            case "Energy":
                var battery = GraphCard("Battery rate", s => s.BatteryWatts, 0, Theme.Energy, "watts");
                graphs[^1].AutoScale = true;
                stack.Children.Add(battery);
                stack.Children.Add(DetailReading(s => s.BatteryWatts is { } watts
                    ? $"{Format.Watts(watts)} · {(watts > 0 ? "discharging" : watts < 0 ? "charging" : "idle")} · {s.PowerSource}\nPackage watts are unavailable. Battery rate is not package power."
                    : $"Battery rate unavailable · {s.PowerSource}\nPackage watts are unavailable."));
                break;
            case "Thermals":
                stack.Children.Add(GraphCard("Temperature", s => s.Temperature, 110, Theme.Thermal, "temp"));
                stack.Children.Add(DetailReading(ThermalText));
                break;
            default:
                var cores = sample?.Cores;
                stack.Children.Add(GraphCard("CPU utilization", s => s.Cpu, 100, Theme.Cpu, "percent"));
                graphs[^1].SecondaryValue = s => s.Kernel; graphs[^1].SecondaryStroke = Theme.Kernel;
                graphs[^1].RightValue = s => s.Temperature; graphs[^1].RightCeiling = 110;
                stack.Children.Add(DetailReading(s => $"{Format.Percent(s.Cpu)} · kernel {Format.Percent(s.Kernel)} · {ClockText(s)}\n{Environment.ProcessorCount} logical processors"));
                if (cores is not { Length: > 0 }) stack.Children.Add(Text("Per-core counters are unavailable.", 13, Theme.Muted));
                else
                {
                    var wrap = new WrapPanel();
                    for (var i = 0; i < cores.Length; i++)
                    {
                        var index = i;
                        var card = GraphCard("Core " + index, s => s.Cores is { } values && index < values.Length ? values[index] : null, 100, Theme.Cpu, "percent");
                        card.Width = 220; card.Margin = new Thickness(0, 0, 12, 12); wrap.Children.Add(card);
                    }
                    stack.Children.Add(wrap);
                }
                break;
        }
        return stack;
    }

    private static string ClockText(Sample sample) => sample.Frequency is { } mhz ? $"{mhz:0} MHz" : "Clock unavailable";
    private static string ThermalText(Sample sample)
    {
        var temps = sample.Temps ?? [];
        var names = sample.TempNames ?? [];
        if (temps.Length == 0 || names.Length != temps.Length) return sample.Temperature is null ? SensorAccess.Unavailable : $"Hotspot {Format.Celsius(sample.Temperature)} · min {Format.Celsius(sample.TempMin)}";
        return string.Join("\n", temps.Select((temp, index) => $"{names[index]}: {Format.Celsius(temp)}"));
    }
    private static string[] DiskNames(Sample? sample) => ["All", .. (sample?.Disks ?? []).Select(disk => disk.Name).Distinct()];
    private static string[] NicNames(Sample? sample) => ["All", .. (sample?.Interfaces ?? []).Select(nic => nic.Name).Distinct()];
    private DeviceSample? PickedDisk(Sample sample) => diskPick == "All" ? null : (sample.Disks ?? []).FirstOrDefault(disk => disk.Name == diskPick);
    private DeviceSample? PickedNic(Sample sample) => nicPick == "All" ? null : sample.Interfaces.FirstOrDefault(nic => nic.Name == nicPick);

    private UIElement Picker(string label, string selected, string[] names, Action<string> choose)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        row.Children.Add(Text(label, 13, Theme.Muted));
        suppressPick = true;
        var box = new ComboBox { ItemsSource = names, SelectedItem = names.Contains(selected) ? selected : "All", Width = 280, Foreground = Brushes.Black, Margin = new Thickness(8, 0, 0, 0) };
        suppressPick = false;
        if (label == "Disk") diskList = box; else nicList = box;
        box.SelectionChanged += (_, _) =>
        {
            if (suppressPick || box.SelectedItem is not string value) return;
            choose(value);
            foreach (var graph in graphs) graph.InvalidateVisual();
            Render();
        };
        row.Children.Add(box);
        return row;
    }

    private Border RateGraph(string title, Func<Sample, double?> value, Brush stroke, Func<Sample, double?>? secondary, Brush secondaryStroke)
    {
        var card = GraphCard(title, value, 0, stroke, "rate");
        graphs[^1].AutoScale = true;
        if (secondary is not null) { graphs[^1].SecondaryValue = secondary; graphs[^1].SecondaryStroke = secondaryStroke; }
        return card;
    }

    private TextBlock DetailReading(Func<Sample, string> value)
    {
        var text = Reading(value, 13, Theme.Muted);
        text.FontFamily = new FontFamily("Segoe UI");
        text.Margin = new Thickness(0, 4, 0, 12);
        return text;
    }

    private UIElement PowerPage()
    {
        var panel = new DockPanel { Margin = new Thickness(22, 0, 22, 20) };
        var live = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        live.Children.Add(Text("Package power", 16));
        live.Children.Add(Text("Unavailable. There is no generic package-watt counter on Windows, and battery rate is not package power.", 13, Theme.Muted));
        live.Children.Add(Text("Battery", 16));
        live.Children.Add(Reading(s => s.BatteryWatts is { } watts
            ? $"{Format.Watts(watts)} · {(watts > 0 ? "discharging" : watts < 0 ? "charging" : "idle")} · {s.PowerSource}{(s.BatteryPercent is { } percent ? $" · {percent:0}%" : "")}"
            : $"Battery rate unavailable · {s.PowerSource}", 18, Theme.Energy));
        live.Children.Add(Text("Frequency", 16));
        live.Children.Add(Reading(s => $"{ClockText(s)} · {NominalClock()}", 18, Theme.Cpu));
        live.Children.Add(Meter(s => s.Frequency is { } mhz ? mhz / 5000 : null, Theme.Cpu, false, 14));
        DockPanel.SetDock(live, Dock.Top); panel.Children.Add(live);
        search = SearchBox();
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        tools.Children.Add(search);
        tools.Children.Add(Button("Refresh", async (_, _) => await RefreshInventory()));
        DockPanel.SetDock(tools, Dock.Top); panel.Children.Add(tools);
        table = GridTable(); Column(table, "Name", "Name", 240); Column(table, "Details", "Detail", 1, true); Column(table, "Value / state", "Value", 360);
        panel.Children.Add(table);
        return panel;
    }

    private static string NominalClock()
    {
        try
        {
            using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return cpu?.GetValue("~MHz") is int mhz ? $"{mhz} MHz nominal" : "Nominal frequency unavailable";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return "Nominal frequency unavailable"; }
    }

    private UIElement DiskPage()
    {
        var panel = new DockPanel { Margin = new Thickness(22, 0, 22, 20) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        search = SearchBox(); tools.Children.Add(search);
        tools.Children.Add(Button("Refresh", async (_, _) => await RefreshInventory()));
        tools.Children.Add(Button("Scan folder…", (_, _) => ChooseFolder()));
        tools.Children.Add(Button("Cancel scan", (_, _) => scanCancel?.Cancel()));
        DockPanel.SetDock(tools, Dock.Top); panel.Children.Add(tools);
        scanLabel = Text(scanStatus, 12, Theme.Muted); scanLabel.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(scanLabel, Dock.Top); panel.Children.Add(scanLabel);
        scanMap = new BlockMap { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(scanMap, Dock.Top); panel.Children.Add(scanMap);
        scanTable = GridTable(); scanTable.MaxHeight = 280; scanTable.Margin = new Thickness(0, 0, 0, 16);
        Column(scanTable, "Name", "Name", 220); Column(scanTable, "Path", "Path", 1, true); Column(scanTable, "Size on disk", "Value", 160);
        scanTable.MouseDoubleClick += (_, _) => { if (scanTable.SelectedItem is InfoRow row) RevealPath(row.Path); };
        DockPanel.SetDock(scanTable, Dock.Top); panel.Children.Add(scanTable);
        var note = Text("Volumes below are capacity and free space. A folder scan measures immediate children, uses allocated size, skips reparse points, and counts each hard link once.", 12, Theme.Muted);
        note.Margin = new Thickness(0, 0, 0, 12); DockPanel.SetDock(note, Dock.Top); panel.Children.Add(note);
        table = GridTable(); Column(table, "Name", "Name", 240); Column(table, "Details", "Detail", 1, true); Column(table, "Value / state", "Value", 300);
        panel.Children.Add(table);
        PaintScan();
        return panel;
    }

    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to measure" };
        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName)) StartScan(dialog.FolderName);
    }

    private void StartScan(string path)
    {
        scanCancel?.Cancel();
        scanCancel = new CancellationTokenSource();
        var generation = ++scanGeneration;
        var token = scanCancel.Token;
        scanning = true;
        scanStatus = "Scanning…";
        PaintScan();
        var progress = new Progress<string>(text => { if (generation == scanGeneration) { scanStatus = text; if (scanLabel is not null) scanLabel.Text = text; } });
        Task.Run(() => DiskScan.Measure(path, token, progress)).ContinueWith(task => Dispatcher.Invoke(() =>
        {
            if (generation != scanGeneration) return;
            scanning = false;
            if (task.IsFaulted) { scanRows = []; scanStatus = task.Exception?.GetBaseException().Message ?? "Scan failed."; }
            else { scanRows = task.Result.Rows; scanStatus = task.Result.Status; }
            PaintScan();
        }));
    }

    private void PaintScan()
    {
        if (scanLabel is not null) scanLabel.Text = scanning ? scanStatus : scanStatus;
        if (scanTable is null || scanMap is null) return;
        var query = search?.Text ?? "";
        var rows = scanRows.Where(row => $"{row.Name} {row.Detail} {row.Value} {row.Path}".Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        scanTable.ItemsSource = rows;
        scanMap.Rows = rows;
        scanMap.InvalidateVisual();
    }

    private void EnsurePicks(Sample sample)
    {
        if (diskPick != "All" && (sample.Disks ?? []).All(disk => disk.Name != diskPick)) diskPick = "All";
        if (nicPick != "All" && sample.Interfaces.All(nic => nic.Name != nicPick)) nicPick = "All";
        SyncPick(diskList, diskPick);
        SyncPick(nicList, nicPick);
    }
    private void SyncPick(ComboBox? box, string pick)
    {
        if (box is null || Equals(box.SelectedItem, pick) || !box.Items.Contains(pick)) return;
        suppressPick = true;
        box.SelectedItem = pick;
        suppressPick = false;
    }
}
