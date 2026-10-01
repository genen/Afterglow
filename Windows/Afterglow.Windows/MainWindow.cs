using System.ComponentModel;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Afterglow.Core;
using Frame = Afterglow.Core.Frame;

namespace Afterglow.Windows;

internal sealed partial class MainWindow : Window
{
    private static readonly string[] Destinations = ["Summary", "Performance", "Processes", "System Info", "Startup apps", "Users", "Services", "Connections", "Power & Freq", "Flight Recorder", "Benchmarks", "Installed Apps", "Disk Space"];
    private readonly WindowsCollector collector = new();
    private readonly DispatcherTimer timer = new();
    private readonly RecordingBuffer recorded = new();
    private readonly List<Sample> history = [];
    private readonly ContentControl body = new();
    private readonly TextBlock heading = Text("Summary", 27), footer = Text("Starting native providers…", 11), notice = Text("", 12);
    private readonly Button pause = Button("Pause", null), record = Button("Record", null);
    private readonly ListBox navigation = new();
    private readonly List<(TextBlock Label, Func<Sample, string> Value)> readings = [];
    private readonly List<HistoryGraph> graphs = [];
    private Settings settings = Settings.Load();
    private Frame? frame;
    private Trace? replay;
    private int replayIndex, epoch, pageEpoch;
    private bool busy, paused, recording, replayPlaying, closing, loadingTrace;
    private DataGrid? table;
    private TextBox? search;
    private Slider? scrub;
    private TextBlock? recorderLabel;
    private Button? terminate;
    private string destination = "Summary";
    private InfoRow[] inventory = [];
    private Window? compact, saver;
    private TextBlock? compactText;
    private readonly List<(string Id, string Text)> alerts = [];
    private readonly Dictionary<string, DateTimeOffset> alertCooldown = [];
    private readonly Dictionary<string, DateTimeOffset> cpuHotSince = [];
    private DateTimeOffset lastInventoryRefresh;
    private string actionMessage = "";
    private int savedFrameCount;
    private int recordingGeneration;
    private long replayStamp;
    private double replayElapsed;
    private bool benchmarkRunning;
    private string benchmarkResult = "Ready";

    private async Task Tick(bool force = false)
    {
        if (closing || loadingTrace) return;
        if (replay is not null)
        {
            if (replayPlaying)
            {
                var now = Stopwatch.GetTimestamp();
                replayElapsed += Stopwatch.GetElapsedTime(replayStamp, now).TotalSeconds; replayStamp = now;
                while (replayIndex < replay.Frames.Length - 1)
                {
                    var gap = Math.Max(0, (replay.Frames[replayIndex + 1].Sample.Time - replay.Frames[replayIndex].Sample.Time).TotalSeconds);
                    if (replayElapsed < gap) break;
                    replayElapsed -= gap; replayIndex++;
                }
                if (replayIndex == replay.Frames.Length - 1) replayPlaying = false;
            }
            alerts.Clear(); PaintAlerts(); Render(); return;
        }
        if (busy || paused && !force) return;
        busy = true; var generation = epoch;
        try
        {
            var next = await Task.Run(collector.Collect);
            if (closing || epoch != generation || replay is not null) return;
            frame = next; history.Add(next.Sample); if (history.Count > 180) history.RemoveAt(0);
            if (recording && !recorded.Add(next)) { recording = false; actionMessage = "Recording limit reached. Save the captured frames."; }
            EvaluateAlerts(next.Sample, next.Processes);
            Render();
            var refresh = destination == "Connections" ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(5);
            if (destination is "Connections" or "Power & Freq" && DateTimeOffset.Now - lastInventoryRefresh > refresh)
                await RefreshInventory();
        }
        catch (Exception ex) { actionMessage = "Sample failed: " + ex.Message; notice.Text = actionMessage; notice.Visibility = Visibility.Visible; }
        finally { busy = false; if (closing) collector.Dispose(); }
    }
    private Frame? Current => replay?.Frames[replayIndex] ?? frame;
    private IReadOnlyList<Sample> History => replay is null ? history : replay.Frames.Take(replayIndex + 1).TakeLast(180).Select(f => f.Sample).ToArray();
    private void Render()
    {
        var current = Current; if (current is null) return; var s = current.Sample;
        foreach (var (label, value) in readings) label.Text = value(s);
        EnsurePicks(s);
        foreach (var (meter, value, overlay) in meters) { meter.Fraction = value(s); if (overlay is not null) meter.OverlayFraction = overlay(s); }
        foreach (var (meter, value) in compactMeters) meter.Fraction = value(s);
        foreach (var graph in graphs) { graph.Samples = History; if (graph.Tag as string == "memory") graph.Ceiling = Math.Max(s.Total ?? 1, 1); graph.InvalidateVisual(); }
        if (compactGraph is not null) { compactGraph.Samples = History; compactGraph.InvalidateVisual(); }
        if (ribbon is not null) ribbon.Samples = History;
        if (destination is "Processes" or "Summary" or "Users") UpdateTable();
        var pauseLabel = replay is null ? paused ? "Resume" : "Pause" : replayPlaying ? "Pause replay" : "Play replay";
        pause.Content = new Symbol((replay is null ? paused : !replayPlaying) ? "Play" : "Pause"); pause.ToolTip = pauseLabel;
        System.Windows.Automation.AutomationProperties.SetName(pause, pauseLabel);
        record.Content = new Symbol("Record", recording ? Theme.Npu : Theme.Muted); record.ToolTip = recording ? "Stop recording" : "Flight Recorder · start recording"; record.IsEnabled = replay is null && !loadingTrace;
        System.Windows.Automation.AutomationProperties.SetName(record, recording ? "Stop recording" : "Start recording");
        if (terminate is not null) terminate.IsEnabled = replay is null && SelectedProcess() is { Started: > 0 };
        var selfCpu = replay is null ? current.Processes.FirstOrDefault(p => p.Pid == Environment.ProcessId)?.Cpu : null;
        footer.Text = $"{(replay is not null ? "Replay · " + replay.Platform : paused ? "Paused" : s.Cpu is null || s.Memory is null ? "Providers warming / partial" : "Native providers healthy")}   ·   {current.Processes.Length} processes   ·   {settings.Interval:0.##} s" + (selfCpu is null ? "" : $"   ·   self {Format.Percent(selfCpu)}") + (recording ? $"   ● REC {recorded.Frames.Count}" : "");
        clockLabel.Text = s.Time.ToString("T"); statusLight.Fill = s.Cpu is null || s.Memory is null ? Theme.Thermal : Theme.Cpu;
        notice.Text = actionMessage;
        notice.Visibility = string.IsNullOrEmpty(notice.Text) ? Visibility.Collapsed : Visibility.Visible;
        var tip = $"CPU {Format.Percent(s.Cpu)}";
        if (s.Gpu is not null) tip += $"   GPU {Format.Percent(s.Gpu)}";
        if (s.Temperature is not null) tip += $"   {Format.Celsius(s.Temperature)}";
        tray.Update(tip);
        if (scrub is not null) { scrub.Maximum = Math.Max(0, (replay?.Frames.Length ?? 1) - 1); scrub.Value = replayIndex; }
        if (recorderLabel is not null) recorderLabel.Text = replay is null ? $"{recorded.Frames.Count} frames captured · {(recording ? "Recording" : "Stopped")}" : $"Replay {replayIndex + 1} / {replay.Frames.Length} · {replay.Platform} · {s.Time:G}";
        if (compactText is not null) compactText.Text = $"{(replay is null ? paused ? "PAUSED" : "LIVE" : "REPLAY")}   ·   {Sensors.PressureName(s.Pressure)}   ·   {s.TopCpu}";
        if (destination == "Disk Space") PaintScan();
    }

    private void ShowPage(string page)
    {
        destination = page; heading.Text = page; pageEpoch++; readings.Clear(); graphs.Clear(); meters.Clear(); table = null; search = null; terminate = null; scrub = null; recorderLabel = null; inspector = null; scanLabel = null; scanTable = null; scanMap = null;
        switch (page)
        {
            case "Summary": body.Content = Dashboard(); break;
            case "Performance": body.Content = PerformancePage(); break;
            case "Processes": body.Content = ProcessPage(); break;
            case "Power & Freq":
                inventory = [new("Loading…", "Collecting Windows inventory", "")];
                body.Content = PowerPage();
                _ = RefreshInventory(); break;
            case "Disk Space":
                inventory = [new("Loading…", "Collecting Windows inventory", "")];
                body.Content = DiskPage();
                _ = RefreshInventory(); break;
            case "Users": body.Content = TablePage("Process owners; CPU uses one logical core = 100%. Working sets may count shared memory more than once."); break;
            case "Flight Recorder": body.Content = RecorderPage(); break;
            case "Benchmarks": body.Content = BenchmarkPage(); break;
            default:
                inventory = [new("Loading…", "Collecting Windows inventory", "")];
                body.Content = TablePage(InventoryDescription(page));
                _ = RefreshInventory(); break;
        }
        Render();
    }
    private static string InventoryDescription(string page) => page switch
    {
        "Startup apps" => "Run and RunOnce entries, Startup folders, and scheduled tasks. Reveal opens a path when one was recorded.",
        "Installed Apps" => "Desktop uninstall entries, including install location when Windows recorded one, plus Store package family names.",
        "Connections" => "TCP and UDP endpoints with the owning process · refreshes every 2 s while live.",
        "Services" => "Read-only Windows Service Control Manager inventory.",
        _ => "Windows system inventory · Refresh to collect again."
    };

    private UIElement TablePage(string detail)
    {
        var panel = new DockPanel { Margin = new Thickness(22, 0, 22, 20) };
        var note = Text(detail, 12, Theme.Muted); note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 0, 0, 12); DockPanel.SetDock(note, Dock.Top); panel.Children.Add(note);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        search = SearchBox(); tools.Children.Add(search);
        if (destination != "Users") tools.Children.Add(Button("Refresh", async (_, _) => await RefreshInventory()));
        if (destination is "Installed Apps" or "Startup apps") tools.Children.Add(Button("Reveal", (_, _) => RevealPath((table?.SelectedItem as InfoRow)?.Path)));
        DockPanel.SetDock(tools, Dock.Top); panel.Children.Add(tools);
        table = GridTable(); Column(table, "Name", "Name", 240); Column(table, "Details", "Detail", 1, true); Column(table, "Value / state", "Value", 300);
        table.MouseDoubleClick += (_, _) => { if (table.SelectedItem is InfoRow row && destination is "Installed Apps" or "Startup apps") RevealPath(row.Path); };
        panel.Children.Add(table); return panel;
    }
    private async Task RefreshInventory()
    {
        var page = destination; var version = pageEpoch; lastInventoryRefresh = DateTimeOffset.Now;
        if (replay is not null) { inventory = [new("Inventory unavailable in replay", "Recordings contain telemetry and process snapshots only.", "Return to Live")]; UpdateTable(); return; }
        try
        {
            IReadOnlyDictionary<int, string>? names = page == "Connections"
                ? (Current?.Processes ?? []).GroupBy(row => row.Pid).ToDictionary(group => group.Key, group => group.First().Name)
                : null;
            var rows = await Task.Run(() => Inventory.Collect(page, names));
            if (closing || version != pageEpoch || replay is not null) return;
            inventory = rows.Length == 0 ? [new("No entries reported", "This provider returned no visible entries.", "—")] : rows; UpdateTable();
        }
        catch (Exception ex) { if (version == pageEpoch) { inventory = [new("Inventory unavailable", ex.Message, "Refresh to retry")]; UpdateTable(); } }
    }
    private void UpdateTable()
    {
        if (table is null) return;
        var query = search?.Text ?? "";
        bool Match(string text) => text.Contains(query, StringComparison.OrdinalIgnoreCase);
        var oldSelection = (table.SelectedItem as ProcessRow)?.Identity;
        var sorts = table.Items.SortDescriptions.ToArray();
        if (destination == "Processes")
        {
            var lines = BuildProcessLines();
            var identity = (table.SelectedItem as ProcessLine)?.Identity ?? oldSelection;
            fillingTable = true;
            table.ItemsSource = lines;
            if (identity is not null) table.SelectedItem = lines.FirstOrDefault(line => line.Identity == identity);
            fillingTable = false;
            RefreshProcessSelection();
            if (destination == "Disk Space") PaintScan();
            return;
        }
        else if (destination == "Summary")
        {
            var rows = (Current?.Processes ?? []).Where(p => settings.ShowSelf || p.Pid != Environment.ProcessId).Where(p => Match($"{p.Name} {p.Pid} {p.User} {p.Path}"));
            table.ItemsSource = rows.OrderByDescending(p => p.Cpu ?? -1).Take(12).ToArray();
        }
        else if (destination == "Users")
            table.ItemsSource = (Current?.Processes ?? []).Where(p => settings.ShowSelf || p.Pid != Environment.ProcessId).GroupBy(p => p.User).Select(g => new InfoRow(g.Key, $"{g.Count()} processes · {Format.Bytes(g.Any(p => p.Memory is not null) ? g.Sum(p => p.Memory ?? 0) : null)} working set", Format.Percent(g.Any(p => p.Cpu is not null) ? g.Sum(p => p.Cpu ?? 0) : null))).Where(r => Match(r.Name)).ToArray();
        else table.ItemsSource = inventory.Where(r => Match($"{r.Name} {r.Detail} {r.Value}")).ToArray();
        if (destination == "Disk Space") PaintScan();
        table.Items.SortDescriptions.Clear();
        foreach (var sort in sorts) table.Items.SortDescriptions.Add(sort);
        if (oldSelection is not null) table.SelectedItem = table.Items.OfType<ProcessRow>().FirstOrDefault(p => p.Identity == oldSelection);
    }

    private UIElement RecorderPage()
    {
        var stack = new StackPanel { Margin = new Thickness(22) };
        stack.Children.Add(Text("Capture a session. Inspect it later.", 22, Theme.Accent));
        stack.Children.Add(Text("Telemetry and process snapshots stay on this computer. Windows writes v2 .afterglowtrace. A Mac v1 import keeps the shared counters plus cores, disks, GPU, temperature, and process parent, threads, and priority. Imported processes are not actionable.\nUp to 3,600 frames or 250,000 process snapshots. Recorded process actions are disabled.", 13, Theme.Muted));
        recorderLabel = Text("No frames captured", 18); recorderLabel.Margin = new Thickness(0, 25, 0, 20); stack.Children.Add(recorderLabel);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Button("Record / stop", (_, _) => ToggleRecord())); buttons.Children.Add(Button("Save trace…", async (_, _) => await SaveTrace()));
        buttons.Children.Add(Button("Open trace…", async (_, _) => await OpenTrace())); buttons.Children.Add(Button("Return to Live", (_, _) => ReturnLive())); stack.Children.Add(buttons);
        scrub = new Slider { Minimum = 0, Maximum = 0, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 28, 0, 15) };
        scrub.ValueChanged += (_, _) => { if (replay is not null && replayIndex != (int)scrub.Value) { replayIndex = (int)scrub.Value; replayElapsed = 0; replayStamp = Stopwatch.GetTimestamp(); Render(); } }; stack.Children.Add(scrub);
        stack.Children.Add(GraphCard("CPU history · hover for the top process", s => s.Cpu, 100, Theme.Accent));
        return stack;
    }
    private void TogglePause()
    {
        if (replay is null) paused = !paused;
        else
        {
            if (!replayPlaying && replayIndex == replay.Frames.Length - 1) replayIndex = 0;
            replayPlaying = !replayPlaying; replayStamp = Stopwatch.GetTimestamp(); replayElapsed = 0;
        }
        Render();
    }
    private void ToggleRecord()
    {
        if (replay is not null || loadingTrace) return;
        if (!recording && recorded.Frames.Count > savedFrameCount && MessageBox.Show(this, "Replace the current unsaved recording? Save it first to keep it.", "New recording", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!recording) { recorded.Clear(); savedFrameCount = 0; recordingGeneration++; paused = false; }
        recording = !recording; actionMessage = ""; Render();
    }
    private async Task SaveTrace()
    {
        var frames = replay?.Frames ?? recorded.Frames.ToArray();
        if (frames.Length == 0) { Message("Record some samples first."); return; }
        var dialog = new SaveFileDialog { Filter = "Afterglow recording|*.afterglowtrace", FileName = "Recording.afterglowtrace" };
        if (dialog.ShowDialog(this) != true) return;
        var platform = replay?.Platform ?? "windows"; var savingLive = replay is null; var generation = recordingGeneration;
        try { await Task.Run(() => TraceFile.Save(dialog.FileName, frames, platform)); if (savingLive && generation == recordingGeneration) savedFrameCount = Math.Max(savedFrameCount, frames.Length); actionMessage = "Recording saved."; Render(); } catch (Exception ex) { Message(ex.Message); }
    }
    private async Task OpenTrace()
    {
        if (loadingTrace) return;
        var dialog = new OpenFileDialog { Filter = "Afterglow recordings|*.afterglowtrace;*.pulsetrace|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        loadingTrace = true;
        try
        {
            var trace = await Task.Run(() => TraceFile.Load(dialog.FileName));
            epoch++; replay = trace; replayIndex = 0; replayPlaying = false; recording = false; actionMessage = "";
            navigation.SelectedItem = navigation.Items.Cast<ListBoxItem>().First(item => item.Tag as string == "Flight Recorder"); ShowPage("Flight Recorder");
        }
        catch (Exception ex) { Message("Could not open recording: " + ex.Message); }
        finally { loadingTrace = false; Render(); }
    }
    private void ReturnLive() { epoch++; replay = null; replayPlaying = false; paused = false; ShowPage(destination); _ = Tick(true); }
    private async Task Export()
    {
        var rows = destination == "Processes" ? FilteredProcesses() : table?.Items.OfType<ProcessRow>().ToArray() ?? [];
        var dialog = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "Processes.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try { await File.WriteAllTextAsync(dialog.FileName, Format.Csv(rows)); } catch (Exception ex) { Message(ex.Message); }
    }
    private void EndProcess()
    {
        if (replay is not null || SelectedProcess() is not { } row) return;
        if (MessageBox.Show(this, $"End {row.Name} (PID {row.Pid})? Unsaved work may be lost.", "End process", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { WindowsCollector.Terminate(row, replay is not null); actionMessage = $"Ended {row.Name}."; _ = Tick(true); } catch (Exception ex) { Message(ex.Message); }
    }

    private void ShowSettings()
    {
        var dialog = new Window { Title = "Afterglow settings & colors", Owner = this, Width = 450, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Background = Theme.Background, Foreground = Theme.Foreground, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Text("Sample interval (seconds)", 14));
        var interval = new ComboBox { ItemsSource = new[] { .25, .5, 1, 2, 5 }, SelectedItem = settings.Interval, Margin = new Thickness(0, 8, 0, 16), Foreground = Brushes.Black }; panel.Children.Add(interval);
        panel.Children.Add(Text("Phosphor color", 14));
        var color = new ComboBox { ItemsSource = new[] { "Full color", "Green", "Amber", "Blue", "Mono" }, SelectedItem = settings.Color, Margin = new Thickness(0, 8, 0, 16), Foreground = Brushes.Black }; panel.Children.Add(color);
        var saturationLabel = Text($"Saturation  {settings.Saturation:0}", 14); panel.Children.Add(saturationLabel);
        var saturation = new Slider { Minimum = 0, Maximum = 11, Value = settings.Saturation, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 6, 0, 12) };
        saturation.ValueChanged += (_, _) => saturationLabel.Text = $"Saturation  {saturation.Value:0}"; panel.Children.Add(saturation);
        var bloom = new CheckBox { Content = "Phosphor bloom", IsChecked = settings.Bloom, Margin = new Thickness(0, 5, 0, 10), Foreground = Theme.Foreground }; panel.Children.Add(bloom);
        var top = new CheckBox { Content = "Always on top", IsChecked = settings.AlwaysOnTop, Margin = new Thickness(0, 5, 0, 10), Foreground = Theme.Foreground }; panel.Children.Add(top);
        var alertsBox = new CheckBox { Content = "Memory pressure, temperature, and sustained CPU alerts", IsChecked = settings.Alerts, Margin = new Thickness(0, 5, 0, 10), Foreground = Theme.Foreground }; panel.Children.Add(alertsBox);
        panel.Children.Add(Text("Appearance", 14));
        var appearance = new ComboBox { ItemsSource = new[] { "Dark", "Light", "System" }, SelectedItem = settings.Appearance, Margin = new Thickness(0, 8, 0, 16), Foreground = Brushes.Black }; panel.Children.Add(appearance);
        var showSelf = new CheckBox { Content = "Show Afterglow in process lists", IsChecked = settings.ShowSelf, Margin = new Thickness(0, 5, 0, 16), Foreground = Theme.Foreground }; panel.Children.Add(showSelf);
        panel.Children.Add(Text("Version " + Inventory.ProductVersion, 13, Theme.Muted));
        panel.Children.Add(Button("Save settings", (_, _) =>
        {
            var previousLight = settings.ResolvedLight;
            settings = new((double)interval.SelectedItem, (string)color.SelectedItem, top.IsChecked == true, alertsBox.IsChecked == true, saturation.Value, bloom.IsChecked == true, (string)appearance.SelectedItem, showSelf.IsChecked != false);
            try { settings.Save(); } catch (Exception ex) { Message("Could not save settings: " + ex.Message); return; }
            Topmost = settings.AlwaysOnTop; if (compact is not null) compact.Topmost = Topmost;
            Theme.Configure(settings); timer.Interval = TimeSpan.FromSeconds(settings.Interval);
            if (settings.ResolvedLight != previousLight)
            {
                var reopen = compact is not null;
                compact?.Close();
                BuildShell();
                if (reopen) ShowCompact();
            }
            else RefreshBrand();
            ShowPage(destination); dialog.Close();
        })); dialog.Content = panel; dialog.ShowDialog();
    }
    private UIElement BenchmarkPage()
    {
        var stack = new StackPanel { Margin = new Thickness(22) };
        stack.Children.Add(Text("Small, repeatable workloads", 22, Theme.Accent));
        stack.Children.Add(Text("CPU: single-thread integer operations. Memory: SHA-256 throughput. Disk: 64 MiB temporary write + flush and verified cached read.\nResults depend on background work and power policy; these are not hardware ratings.", 13, Theme.Muted));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 22, 0, 22) };
        var result = Text(benchmarkRunning ? "A benchmark is already running…" : benchmarkResult, 16); result.FontFamily = new FontFamily("Consolas");
        foreach (var kind in new[] { "CPU", "Memory", "Disk" })
            buttons.Children.Add(Button("Run " + kind, async (_, _) =>
            {
                if (benchmarkRunning) { Message("A benchmark is already running."); return; }
                benchmarkRunning = true; buttons.IsEnabled = false; result.Text = "Running " + kind + "…";
                try { benchmarkResult = await Task.Run(() => Benchmarks.Run(kind)); } catch (Exception ex) { benchmarkResult = "Benchmark failed: " + ex.Message; }
                finally { benchmarkRunning = false; buttons.IsEnabled = true; result.Text = benchmarkResult; if (!closing && destination == "Benchmarks") ShowPage(destination); }
            }));
        stack.Children.Add(buttons); stack.Children.Add(Card(result)); return stack;
    }
    private void ShowCompact()
    {
        if (compact is not null) { compact.Activate(); return; }
        compactMeters.Clear();
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new() { Height = new GridLength(128) });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var gauges = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4 };
        gauges.Children.Add(CompactGauge("CPU", Theme.Cpu, s => s.Cpu / 100));
        gauges.Children.Add(CompactGauge("GPU", Theme.Gpu, s => s.Gpu / 100));
        gauges.Children.Add(CompactGauge("Disk", Theme.Thermal, s => s.DiskBusy / 100));
        gauges.Children.Add(CompactGauge("RAM", Theme.Memory, s => s.Total > 0 ? s.Memory / s.Total : null));
        root.Children.Add(gauges);
        compactGraph = new HistoryGraph { Value = s => s.Cpu, SecondaryValue = s => s.Kernel, SecondaryStroke = Theme.Kernel, Ceiling = 100, Stroke = Theme.Cpu, Tag = "percent", Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(compactGraph, 1); root.Children.Add(compactGraph);
        compactText = Text("Waiting for samples", 12, Theme.Muted); compactText.FontFamily = new FontFamily("Consolas");
        Grid.SetRow(compactText, 2); root.Children.Add(compactText);
        compact = new Window { Title = "AFTERGLOW Compact", Width = 680, Height = 440, MinWidth = 520, MinHeight = 340, Content = root, Background = Theme.Background, Foreground = Theme.Foreground, Owner = this, Topmost = settings.AlwaysOnTop };
        compact.Closed += (_, _) => { compact = null; compactText = null; compactGraph = null; compactMeters.Clear(); };
        compact.Show(); Render();
    }
    private UIElement CompactGauge(string title, Brush color, Func<Sample, double?> value)
    {
        var panel = new StackPanel { Margin = new Thickness(6, 0, 6, 0) };
        var label = Text(title, 11, color); label.HorizontalAlignment = HorizontalAlignment.Center; panel.Children.Add(label);
        var meter = new VfdMeter { Tone = color, Vertical = true, Height = 96, Cells = 16 };
        compactMeters.Add((meter, value)); panel.Children.Add(meter); return panel;
    }
    private void ToggleSaver()
    {
        if (saver is not null) { saver.Close(); return; }
        ribbon = new RibbonView();
        var overlay = new Grid { Background = Brushes.Black };
        overlay.Children.Add(ribbon);
        var legend = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 28) };
        void Chip(string name, Brush color) => legend.Children.Add(new TextBlock { Text = name, Foreground = color, FontFamily = new FontFamily("Consolas"), FontSize = 14, Margin = new Thickness(16, 0, 16, 0) });
        Chip("CPU", Theme.Cpu); Chip("DISK", Theme.Thermal); Chip("GPU", Theme.Gpu); Chip("MEMORY", Theme.Memory);
        overlay.Children.Add(legend);
        overlay.Children.Add(new TextBlock { Text = "ESC, F12, OR CLICK TO RETURN", Foreground = Theme.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 24, 0, 0), FontFamily = new FontFamily("Consolas") });
        saver = new Window { Owner = this, Title = "Afterglow ambient display", WindowStyle = WindowStyle.None, WindowState = WindowState.Maximized, Background = Brushes.Black, Content = overlay, Cursor = Cursors.Hand };
        saver.PreviewKeyDown += (_, e) => { if (e.Key is Key.Escape or Key.F12) saver?.Close(); };
        saver.MouseDown += (_, _) => saver?.Close();
        saver.Closed += (_, _) => { ribbon?.Stop(); ribbon = null; saver = null; };
        ribbon.Start(); saver.Show(); Render();
    }
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12) { ToggleSaver(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.P) TogglePause(); else if (e.Key == Key.R) _ = Tick(true); else if (e.Key == Key.O) _ = OpenTrace(); else if (e.Key == Key.S) _ = SaveTrace(); else return;
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.K) { ShowCompact(); e.Handled = true; }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (recorded.Frames.Count > savedFrameCount && MessageBox.Show(this, "Close Afterglow? Save your recording from Flight Recorder first if you want to keep it.", "Close Afterglow", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { e.Cancel = true; return; }
        closing = true; timer.Stop(); epoch++; tray.Remove(); scanCancel?.Cancel(); compact?.Close(); saver?.Close(); if (!busy) collector.Dispose();
    }

    private static TextBlock Text(string text, double size = 13, Brush? color = null) => new() { Text = text, FontSize = size, Foreground = color ?? Theme.Foreground, Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap };
    private static Button Button(string text, RoutedEventHandler? action) { var b = new Button { Content = text }; if (action is not null) b.Click += action; return b; }
    private static Border Card(UIElement content) => new() { Child = content, Background = Theme.Panel, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12) };
    private TextBox SearchBox()
    {
        var box = new TextBox { Width = 320, Padding = new Thickness(7, 4, 7, 4), Margin = new Thickness(0, 0, 12, 0), Background = Theme.Panel, BorderBrush = Theme.Line, Foreground = Theme.Foreground, ToolTip = "Filter by name, PID, owner or path" };
        System.Windows.Automation.AutomationProperties.SetName(box, "Filter rows"); box.TextChanged += (_, _) => UpdateTable(); return box;
    }
    private static DataGrid GridTable() => new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.None, Background = Theme.Background, RowBackground = Theme.Background, AlternatingRowBackground = Theme.RowAlt, Foreground = Theme.Foreground, BorderThickness = new Thickness(0), FontSize = 12, RowHeight = 26, EnableRowVirtualization = true, EnableColumnVirtualization = true };
    private static void Column(DataGrid grid, string title, string property, double width, bool star = false, string? sort = null) => grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(property), Width = new DataGridLength(width, star ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel), SortMemberPath = sort ?? property });
    private void EvaluateAlerts(Sample sample, ProcessRow[] processes)
    {
        if (!settings.Alerts) { alerts.Clear(); PaintAlerts(); return; }
        var now = DateTimeOffset.Now;
        void Consider(string id, string text, bool firing)
        {
            if (!firing || alerts.Any(alert => alert.Id == id) || alerts.Count >= 4) return;
            if (alertCooldown.TryGetValue(id, out var until) && until > now) return;
            alerts.Add((id, text));
        }
        var pressure = Sensors.Pressure(sample.Available, sample.Total);
        Consider("pressure", $"Memory pressure is {Sensors.PressureName(pressure)}.", pressure >= 1);
        Consider("temp", $"Hotspot temperature is {Format.Celsius(sample.Temperature)}.", sample.Temperature is >= 90);
        var hot = new HashSet<string>();
        foreach (var process in processes)
        {
            if (process.Cpu is not >= 150) continue;
            hot.Add(process.Identity);
            if (!cpuHotSince.ContainsKey(process.Identity)) cpuHotSince[process.Identity] = now;
            else if (now - cpuHotSince[process.Identity] >= TimeSpan.FromSeconds(10))
                Consider("cpu-" + process.Identity, $"{process.Name} has used at least 150% of one core for 10 seconds.", true);
        }
        foreach (var key in cpuHotSince.Keys.Where(key => !hot.Contains(key)).ToArray()) cpuHotSince.Remove(key);
        PaintAlerts();
    }
    private void DismissAlert(string id)
    {
        alerts.RemoveAll(alert => alert.Id == id);
        alertCooldown[id] = DateTimeOffset.Now.AddSeconds(60);
        PaintAlerts();
    }
    private void PaintAlerts()
    {
        alertsHost.Children.Clear();
        alertsHost.Visibility = alerts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var alert in alerts)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var dismiss = Button("Dismiss", (_, _) => DismissAlert(alert.Id));
            DockPanel.SetDock(dismiss, Dock.Right); row.Children.Add(dismiss);
            row.Children.Add(Text(alert.Text, 12, Theme.Thermal));
            alertsHost.Children.Add(new Border { Background = Theme.Panel, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 4, 8, 4), Margin = new Thickness(0, 0, 0, 4), Child = row });
        }
    }
    private void Message(string text) => MessageBox.Show(this, text, "Afterglow", MessageBoxButton.OK, MessageBoxImage.Information);
}
