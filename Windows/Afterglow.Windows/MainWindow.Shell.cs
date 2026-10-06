using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Afterglow.Windows;

internal sealed partial class MainWindow
{
    private readonly Ellipse statusLight = new() { Width = 7, Height = 7, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock clockLabel = Text("—", 11);
    private readonly StackPanel alertsHost = new() { Margin = new Thickness(16, 4, 16, 4) };
    private readonly TrayIcon tray = new();
    private Symbol? brandMark;
    private TextBlock? brandLabel;
    private bool suppressNav;

    public MainWindow()
    {
        Theme.Configure(settings);
        Title = "AFTERGLOW"; Width = 1440; Height = 920; MinWidth = 1100; MinHeight = 720;
        Topmost = settings.AlwaysOnTop;
        FontFamily = new FontFamily("Segoe UI"); UseLayoutRounding = true;
        BuildShell();
        record.Click += (_, _) => ToggleRecord();
        pause.Click += (_, _) => TogglePause();
        navigation.SelectionChanged += (_, _) => { if (!suppressNav && navigation.SelectedItem is ListBoxItem { Tag: string page }) ShowPage(page); };
        PreviewKeyDown += OnKey;
        timer.Interval = TimeSpan.FromSeconds(settings.Interval);
        timer.Tick += async (_, _) => await Tick();
        Loaded += async (_, _) => { ShowPage("Summary"); timer.Start(); await Tick(); };
        Closing += OnClosing;
        SourceInitialized += (_, _) => tray.Attach(this, TogglePause);
    }

    private void BuildShell()
    {
        foreach (var element in new FrameworkElement[] { navigation, heading, footer, notice, pause, record, statusLight, clockLabel, body, alertsHost })
            Detach(element);
        Background = Theme.Background; Foreground = Theme.Foreground;
        var root = new Grid(); root.ColumnDefinitions.Add(new() { Width = new GridLength(188) }); root.ColumnDefinitions.Add(new());
        var sidebar = new DockPanel { Background = Theme.Sidebar };
        var bottom = new StackPanel();
        var brand = new StackPanel { Margin = new Thickness(16, 12, 16, 18) };
        brandMark = new Symbol("Computer", Theme.Network) { Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Center };
        brandLabel = Text("A F T E R G L O W", 11, Theme.Network); brandLabel.FontWeight = FontWeights.Bold; brandLabel.HorizontalAlignment = HorizontalAlignment.Center;
        brand.Children.Add(brandMark); brand.Children.Add(brandLabel); bottom.Children.Add(brand);
        var settingsRow = new Grid { Height = 40 }; settingsRow.ColumnDefinitions.Add(new()); settingsRow.ColumnDefinitions.Add(new());
        var prefs = SidebarButton("Settings", (_, _) => ShowSettings()); var colors = SidebarButton("Colors", (_, _) => ShowSettings());
        settingsRow.Children.Add(prefs); Grid.SetColumn(colors, 1); settingsRow.Children.Add(colors);
        bottom.Children.Add(new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = settingsRow });
        DockPanel.SetDock(bottom, Dock.Bottom); sidebar.Children.Add(bottom);

        navigation.Background = Brushes.Transparent; navigation.BorderThickness = new Thickness(0); navigation.Foreground = Theme.Foreground;
        navigation.Padding = new Thickness(0, 2, 0, 0); navigation.ItemContainerStyle = Theme.NavigationStyle();
        suppressNav = true;
        navigation.Items.Clear();
        foreach (var page in Destinations)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Symbol(page) { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = page, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var item = new ListBoxItem { Content = row, Tag = page };
            System.Windows.Automation.AutomationProperties.SetName(item, page); navigation.Items.Add(item);
        }
        navigation.SelectedItem = navigation.Items.Cast<ListBoxItem>().FirstOrDefault(item => item.Tag as string == destination) ?? navigation.Items[0];
        suppressNav = false;
        sidebar.Children.Add(navigation);
        root.Children.Add(new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = sidebar });

        var main = new DockPanel(); Grid.SetColumn(main, 1); root.Children.Add(main);
        var header = new DockPanel { Margin = new Thickness(22, 12, 18, 12), Height = 40 };
        var more = Button("⋯", (_, _) => { }); more.FontSize = 22; more.Background = Brushes.Transparent; more.BorderBrush = Brushes.Transparent; more.Width = 36;
        more.ToolTip = "Monitor commands"; System.Windows.Automation.AutomationProperties.SetName(more, "Monitor commands");
        var menu = new ContextMenu { Background = Theme.Panel, Foreground = Theme.Foreground, BorderBrush = Theme.Line };
        void Command(string label, RoutedEventHandler action) { var item = new MenuItem { Header = label }; item.Click += action; menu.Items.Add(item); }
        Command("Open recording…     Ctrl+O", async (_, _) => await OpenTrace());
        Command("Save recording…     Ctrl+S", async (_, _) => await SaveTrace());
        menu.Items.Add(new Separator());
        Command("Pause / resume     Ctrl+P", (_, _) => TogglePause());
        Command("Refresh now     Ctrl+R", async (_, _) => await Tick(true));
        Command("Compact summary     Ctrl+Shift+K", (_, _) => ShowCompact());
        Command("Ambient display     F12", (_, _) => ToggleSaver());
        more.ContextMenu = menu; more.Click += (_, _) => { menu.PlacementTarget = more; menu.IsOpen = true; };
        DockPanel.SetDock(more, Dock.Right); header.Children.Add(more);
        var headingRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        headingRow.Children.Add(new Symbol("Window") { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        heading.Margin = new Thickness(0); heading.FontSize = 28; heading.Foreground = Theme.Foreground; headingRow.Children.Add(heading); header.Children.Add(headingRow);
        var headerLine = new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = header };
        DockPanel.SetDock(headerLine, Dock.Top); main.Children.Add(headerLine);
        notice.Margin = new Thickness(16, 6, 16, 0); notice.Foreground = Theme.Thermal;
        DockPanel.SetDock(notice, Dock.Top); main.Children.Add(notice);
        DockPanel.SetDock(alertsHost, Dock.Top); main.Children.Add(alertsHost);

        var footerBar = new DockPanel { LastChildFill = true, Margin = new Thickness(16, 0, 12, 0), Height = 30 };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in new[] { record, pause }) { b.Background = Brushes.Transparent; b.BorderBrush = Brushes.Transparent; b.Padding = new Thickness(5, 3, 5, 3); b.Margin = new Thickness(2, 0, 2, 0); }
        controls.Children.Add(record); controls.Children.Add(pause);
        clockLabel.FontFamily = new FontFamily("Consolas"); clockLabel.Foreground = Theme.Muted; clockLabel.Margin = new Thickness(12, 0, 0, 0); clockLabel.VerticalAlignment = VerticalAlignment.Center;
        controls.Children.Add(clockLabel); DockPanel.SetDock(controls, Dock.Right); footerBar.Children.Add(controls);
        statusLight.Fill = Theme.Cpu; DockPanel.SetDock(statusLight, Dock.Left); footerBar.Children.Add(statusLight);
        footer.Margin = new Thickness(0); footer.Foreground = Theme.Muted; footer.FontSize = 10; footer.TextWrapping = TextWrapping.NoWrap; footer.TextTrimming = TextTrimming.CharacterEllipsis; footer.VerticalAlignment = VerticalAlignment.Center; footerBar.Children.Add(footer);
        var footerLine = new Border { Background = Theme.Graph, BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = footerBar };
        DockPanel.SetDock(footerLine, Dock.Bottom); main.Children.Add(footerLine); main.Children.Add(body);
        Content = root; RefreshBrand(); PaintAlerts();
    }

    private static void Detach(FrameworkElement element)
    {
        switch (element.Parent)
        {
            case Panel panel: panel.Children.Remove(element); break;
            case Decorator decorator: decorator.Child = null; break;
            case ContentControl control: control.Content = null; break;
        }
    }

    private static Button SidebarButton(string name, RoutedEventHandler action)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Symbol(name) { Width = 13, Height = 13, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(Text(name, 11, Theme.Muted));
        var button = Button(name, action); button.Content = row; button.Margin = new Thickness(0); button.Background = Brushes.Transparent; button.BorderBrush = Brushes.Transparent; button.Padding = new Thickness(5);
        System.Windows.Automation.AutomationProperties.SetName(button, name); return button;
    }
    private void RefreshBrand()
    {
        if (brandMark is not null) { brandMark.Tone = Theme.Network; brandMark.InvalidateVisual(); }
        if (brandLabel is not null) brandLabel.Foreground = Theme.Network;
        foreach (var element in new FrameworkElement?[] { brandMark, brandLabel })
            if (element is not null) element.Effect = Theme.Bloom ? new DropShadowEffect { Color = ((SolidColorBrush)Theme.Network).Color, BlurRadius = 8, ShadowDepth = 0, Opacity = .65 } : null;
    }
}
