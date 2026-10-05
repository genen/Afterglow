using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Afterglow.Core;

namespace Afterglow.Windows;

internal sealed partial class MainWindow
{
    private enum ProcessView { All, Applications, Mine, Hierarchy }
    private ProcessView processView = ProcessView.All;
    private string processSort = "Cpu";
    private bool processReverse = true;
    private bool fillingTable;
    private readonly HashSet<string> expandedGroups = [];
    private TextBlock? inspector;
    private readonly Dictionary<ProcessView, Button> viewButtons = [];

    private sealed class ProcessLine
    {
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";
        public int Pid { get; init; }
        public string Status { get; init; } = "";
        public string User { get; init; } = "";
        public double? Cpu { get; init; }
        public string CpuText => Format.Percent(Cpu);
        public double? Memory { get; init; }
        public string MemoryText => Format.Bytes(Memory);
        public double? ReadRate { get; init; }
        public string ReadText => Format.Rate(ReadRate);
        public double? WriteRate { get; init; }
        public string WriteText => Format.Rate(WriteRate);
        public double? Gpu { get; init; }
        public string GpuText => Gpu is null ? "—" : Format.Percent(Gpu);
        public double Energy { get; init; }
        public string EnergyText => Energy.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);
        public string Marker { get; init; } = "";
        public bool IsGroup { get; init; }
        public string Key { get; init; } = "";
        public int Depth { get; init; }
        public ProcessRow? Target { get; init; }
        public string Identity => Target?.Identity ?? Key;
    }

    private UIElement ProcessPage()
    {
        var panel = new DockPanel { Margin = new Thickness(22, 0, 22, 20) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        viewButtons.Clear();
        foreach (var (view, label) in new (ProcessView View, string Label)[] { (ProcessView.All, "All processes"), (ProcessView.Applications, "Applications"), (ProcessView.Mine, "My processes"), (ProcessView.Hierarchy, "Hierarchy") })
        {
            var button = Button(label, (_, _) => { processView = view; HighlightViews(); UpdateTable(); });
            viewButtons[view] = button;
            tools.Children.Add(button);
        }
        HighlightViews();
        DockPanel.SetDock(tools, Dock.Top); panel.Children.Add(tools);
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        search = SearchBox(); filters.Children.Add(search);
        filters.Children.Add(Button("Export CSV", async (_, _) => await Export()));
        terminate = Button("End process…", (_, _) => EndProcess()); terminate.IsEnabled = false; filters.Children.Add(terminate);
        filters.Children.Add(Button("End process tree…", (_, _) => EndTree()));
        DockPanel.SetDock(filters, Dock.Top); panel.Children.Add(filters);
        var note = Text("CPU is a percent of all logical processors, on the same scale as Task Manager. I/O includes file, device and network operations. Energy is a derived score, not watts. Ending a process may lose unsaved work.", 12, Theme.Muted);
        note.Margin = new Thickness(0, 0, 0, 12); DockPanel.SetDock(note, Dock.Top); panel.Children.Add(note);
        inspector = Text("Select a process to inspect it.", 12, Theme.Muted);
        inspector.FontFamily = new FontFamily("Consolas");
        var inspectorCard = Card(inspector); inspectorCard.Margin = new Thickness(0, 12, 0, 0); DockPanel.SetDock(inspectorCard, Dock.Bottom); panel.Children.Add(inspectorCard);
        table = ProcessGrid();
        table.Sorting += SortProcesses;
        table.SelectionChanged += (_, _) => { if (!fillingTable) RefreshProcessSelection(); };
        table.ContextMenu = ProcessMenu();
        panel.Children.Add(table);
        return panel;
    }

    private void HighlightViews()
    {
        foreach (var (view, button) in viewButtons)
            button.BorderBrush = view == processView ? Theme.Accent : Theme.ButtonBorder;
    }

    private ContextMenu ProcessMenu()
    {
        var menu = new ContextMenu { Background = Theme.Panel, Foreground = Theme.Foreground, BorderBrush = Theme.Line };
        MenuItem Item(string label, RoutedEventHandler action)
        {
            var item = new MenuItem { Header = label };
            item.Click += action;
            menu.Items.Add(item);
            return item;
        }
        var end = Item("End process…", (_, _) => EndProcess());
        var tree = Item("End process tree…", (_, _) => EndTree());
        var suspend = Item("Suspend", (_, _) => HoldProcess(false));
        var resume = Item("Resume", (_, _) => HoldProcess(true));
        Item("Reveal in Explorer", (_, _) => RevealPath(SelectedProcess()?.Path));
        menu.Opened += (_, _) =>
        {
            var enabled = replay is null && SelectedProcess() is { Started: > 0 };
            end.IsEnabled = tree.IsEnabled = suspend.IsEnabled = resume.IsEnabled = enabled;
        };
        return menu;
    }

    private void SortProcesses(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var path = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(path)) return;
        if (processSort == path) processReverse = !processReverse;
        else { processSort = path; processReverse = path is "Name" or "User" or "Status" ? false : true; }
        foreach (var column in table!.Columns) column.SortDirection = null;
        e.Column.SortDirection = processReverse ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending;
        UpdateTable();
    }

    private void ToggleGroup(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ProcessLine { IsGroup: true } line }) return;
        if (!expandedGroups.Add(line.Key)) expandedGroups.Remove(line.Key);
        UpdateTable();
        e.Handled = true;
    }

    private ProcessRow[] FilteredProcesses()
    {
        var all = Current?.Processes ?? [];
        var mine = all.FirstOrDefault(row => row.Pid == Environment.ProcessId)?.User ?? Environment.UserName;
        IEnumerable<ProcessRow> rows = all;
        if (!settings.ShowSelf) rows = rows.Where(row => row.Pid != Environment.ProcessId);
        if (processView == ProcessView.Mine) rows = rows.Where(row => SameUser(row.User, mine));
        var query = search?.Text ?? "";
        if (query.Length > 0) rows = rows.Where(row => $"{row.Name} {row.Pid} {row.User} {row.Path}".Contains(query, StringComparison.OrdinalIgnoreCase));
        var list = rows.ToArray();
        if (processView != ProcessView.Applications) return list;
        return list.GroupBy(row => string.IsNullOrEmpty(row.Path) ? $"\0{row.Pid}:{row.Started}" : row.Path).Where(group => group.Any(row => row.Windowed)).SelectMany(group => group).ToArray();
    }

    private static bool SameUser(string owner, string mine) =>
        owner.Equals(mine, StringComparison.OrdinalIgnoreCase) || owner.EndsWith("\\" + mine, StringComparison.OrdinalIgnoreCase);

    private ProcessLine[] BuildProcessLines()
    {
        var rows = FilteredProcesses();
        if (processView == ProcessView.Hierarchy) return Hierarchy(rows);
        var groups = rows.GroupBy(row => string.IsNullOrEmpty(row.Path) ? $"\0{row.Pid}:{row.Started}" : row.Path).ToArray();
        if (processView == ProcessView.Applications)
        {
            groups = groups.Where(group => group.Any(row => row.Windowed)).ToArray();
            if (groups.Length == 0)
                return [new ProcessLine { Name = "No windowed applications", Status = "Applications with a visible window will appear here." }];
        }
        var lines = new List<ProcessLine>();
        foreach (var group in groups.OrderBy(group => GroupScore(group.ToArray()), Comparer<ProcessLine>.Create(CompareLines)))
        {
            var members = group.Select(row => LineFrom(row, 1)).OrderBy(line => line, Comparer<ProcessLine>.Create(CompareLines)).ToArray();
            if (members.Length == 1) { lines.Add(LineFrom(members[0].Target!, 0)); continue; }
            var open = expandedGroups.Contains(group.Key);
            lines.Add(GroupScore(group.ToArray(), group.Key, open));
            if (open) lines.AddRange(members);
        }
        return lines.ToArray();
    }

    private ProcessLine[] Hierarchy(ProcessRow[] rows)
    {
        var byParent = rows.GroupBy(row => row.Parent).ToDictionary(group => group.Key, group => group.ToArray());
        var ids = rows.Select(row => row.Pid).ToHashSet();
        var seen = new HashSet<int>();
        var lines = new List<ProcessLine>();
        void Walk(ProcessRow row, int depth)
        {
            if (!seen.Add(row.Pid) || depth > 64) return;
            lines.Add(LineFrom(row, depth));
            if (!byParent.TryGetValue(row.Pid, out var children)) return;
            foreach (var child in children.Where(child => Sensors.IsChildOf(child, row, strict: false)).OrderBy(child => LineFrom(child, 0), Comparer<ProcessLine>.Create(CompareLines))) Walk(child, depth + 1);
        }
        foreach (var root in rows.Where(row => row.Parent == row.Pid || !ids.Contains(row.Parent)).OrderBy(row => LineFrom(row, 0), Comparer<ProcessLine>.Create(CompareLines)))
            Walk(root, 0);
        foreach (var row in rows.Where(row => !seen.Contains(row.Pid)).OrderBy(row => LineFrom(row, 0), Comparer<ProcessLine>.Create(CompareLines)))
            Walk(row, 0);
        return lines.ToArray();
    }

    private ProcessLine LineFrom(ProcessRow row, int depth) => new()
    {
        Name = row.Name, Path = row.Path, Pid = row.Pid, Status = row.Status, User = row.User, Cpu = row.Cpu, Memory = row.Memory,
        ReadRate = row.ReadRate, WriteRate = row.WriteRate, Gpu = row.Gpu, Energy = Sensors.Energy(row.Cpu, row.Memory, Current?.Sample.Total),
        Depth = depth, Target = row, Key = row.Identity
    };

    private ProcessLine GroupScore(ProcessRow[] members, string key = "", bool open = false)
    {
        double? Sum(Func<ProcessRow, double?> pick) => members.All(row => pick(row) is null) ? null : members.Sum(row => pick(row) ?? 0);
        var main = members.FirstOrDefault(row => row.Windowed) ?? members[0];
        var header = key.Length > 0;
        return new()
        {
            Name = header ? $"{main.Name}  ·  {members.Length}" : main.Name,
            Path = main.Path, Pid = main.Pid, Status = main.Status, User = main.User,
            Cpu = Sum(row => row.Cpu), Memory = Sum(row => row.Memory), ReadRate = Sum(row => row.ReadRate), WriteRate = Sum(row => row.WriteRate), Gpu = Sum(row => row.Gpu),
            Energy = members.Sum(row => Sensors.Energy(row.Cpu, row.Memory, Current?.Sample.Total)),
            Marker = header ? open ? "▾" : "▸" : "", IsGroup = header, Key = key.Length > 0 ? key : main.Identity, Target = main
        };
    }

    private int CompareLines(ProcessLine a, ProcessLine b)
    {
        var primary = processSort switch
        {
            "Name" => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            "Pid" => a.Pid.CompareTo(b.Pid),
            "Status" => string.Compare(a.Status, b.Status, StringComparison.OrdinalIgnoreCase),
            "User" => string.Compare(a.User, b.User, StringComparison.OrdinalIgnoreCase),
            "Memory" => Nullable.Compare(a.Memory, b.Memory),
            "ReadRate" => Nullable.Compare(a.ReadRate, b.ReadRate),
            "WriteRate" => Nullable.Compare(a.WriteRate, b.WriteRate),
            "Gpu" => Nullable.Compare(a.Gpu, b.Gpu),
            "Energy" => a.Energy.CompareTo(b.Energy),
            _ => Nullable.Compare(a.Cpu, b.Cpu)
        };
        if (processReverse) primary = -primary;
        return primary != 0 ? primary : a.Pid.CompareTo(b.Pid);
    }

    private void RefreshProcessSelection()
    {
        var row = SelectedProcess();
        if (terminate is not null) terminate.IsEnabled = replay is null && row is { Started: > 0 };
        if (inspector is null) return;
        if (row is null) { inspector.Text = "Select a process to inspect it."; return; }
        inspector.Text = $"{row.Name}\nPID {row.Pid}  ·  parent {row.Parent}  ·  threads {row.Threads}  ·  priority {row.Priority}\n{row.User}  ·  {row.ArchText}  ·  {row.Status}  ·  CPU time {Format.CpuTime(row.CpuTime)}\nGPU {row.GpuText}  ·  energy {Sensors.Energy(row.Cpu, row.Memory, Current?.Sample.Total):0.0}\n{row.Path}";
    }

    private ProcessRow? SelectedProcess() => table?.SelectedItem switch
    {
        ProcessRow row => row,
        ProcessLine { Target: { } row } => row,
        _ => null
    };

    private void EndTree()
    {
        if (replay is not null || SelectedProcess() is not { } row || Current is null) return;
        if (MessageBox.Show(this, $"End {row.Name} (PID {row.Pid}) and the processes it started? Unsaved work may be lost.", "End process tree", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { WindowsCollector.TerminateTree(row, Current.Processes, replay is not null); actionMessage = $"Ended {row.Name} and its child processes."; _ = Tick(true); }
        catch (Exception ex) { Message(ex.Message); }
    }

    private void HoldProcess(bool resume)
    {
        if (replay is not null || SelectedProcess() is not { } row) return;
        var verb = resume ? "Resume" : "Suspend";
        if (MessageBox.Show(this, $"{verb} {row.Name} (PID {row.Pid})?", verb + " process", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { WindowsCollector.Suspend(row, resume, replay is not null); actionMessage = $"{verb}d {row.Name}."; _ = Tick(true); }
        catch (Exception ex) { Message(ex.Message); }
    }

    private void RevealPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) { Message("This entry has no file path to reveal."); return; }
        var target = path.Trim().Trim('"');
        try
        {
            if (File.Exists(target))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + target + "\"") { UseShellExecute = true });
            else if (Directory.Exists(target))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = true });
            else Message("That path is not on this computer.");
        }
        catch (Exception ex) { Message(ex.Message); }
    }

    private DataGrid ProcessGrid()
    {
        var grid = GridTable();
        var marker = new FrameworkElementFactory(typeof(System.Windows.Controls.Button));
        marker.SetBinding(ContentControl.ContentProperty, new Binding("Marker"));
        marker.SetValue(Control.PaddingProperty, new Thickness(2, 0, 2, 0));
        marker.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        marker.SetValue(Control.BorderBrushProperty, Brushes.Transparent);
        marker.SetValue(FrameworkElement.WidthProperty, 28d);
        marker.AddHandler(System.Windows.Controls.Button.ClickEvent, new RoutedEventHandler(ToggleGroup));
        var markerTemplate = new DataTemplate { VisualTree = marker };
        grid.Columns.Add(new DataGridTemplateColumn { Header = "", CellTemplate = markerTemplate, Width = 36, CanUserSort = false });
        var name = new FrameworkElementFactory(typeof(StackPanel));
        name.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(Image));
        icon.SetValue(FrameworkElement.WidthProperty, 16d);
        icon.SetValue(FrameworkElement.HeightProperty, 16d);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
        icon.SetBinding(Image.SourceProperty, new Binding("Path") { Converter = new PathIconConverter() });
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        label.SetBinding(FrameworkElement.MarginProperty, new Binding("Depth") { Converter = new DepthMarginConverter() });
        label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        name.AppendChild(icon); name.AppendChild(label);
        grid.Columns.Add(new DataGridTemplateColumn { Header = "Name", CellTemplate = new DataTemplate { VisualTree = name }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), SortMemberPath = "Name" });
        Column(grid, "PID", "Pid", 70);
        Column(grid, "Status", "Status", 110);
        Column(grid, "User name", "User", 140);
        var cpu = new FrameworkElementFactory(typeof(CpuUsageBar));
        cpu.SetBinding(CpuUsageBar.ValueProperty, new Binding("Cpu"));
        grid.Columns.Add(new DataGridTemplateColumn { Header = "CPU usage", CellTemplate = new DataTemplate { VisualTree = cpu }, Width = 120, SortMemberPath = "Cpu" });
        Column(grid, "Memory", "MemoryText", 100, sort: "Memory");
        Column(grid, "Read/s", "ReadText", 95, sort: "ReadRate");
        Column(grid, "Write/s", "WriteText", 95, sort: "WriteRate");
        Column(grid, "GPU", "GpuText", 70, sort: "Gpu");
        Column(grid, "Energy", "EnergyText", 70, sort: "Energy");
        return grid;
    }
}
