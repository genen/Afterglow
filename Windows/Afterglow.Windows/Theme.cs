using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Afterglow.Windows;

internal static class Theme
{
    public static Brush Background { get; private set; } = null!;
    public static Brush Panel { get; private set; } = null!;
    public static Brush Sidebar { get; private set; } = null!;
    public static Brush Line { get; private set; } = null!;
    public static Brush Muted { get; private set; } = null!;
    public static Brush Foreground { get; private set; } = null!;
    public static Brush Graph { get; private set; } = null!;
    public static Brush RowAlt { get; private set; } = null!;
    public static Brush Selected { get; private set; } = null!;
    public static Brush ButtonFace { get; private set; } = null!;
    public static Brush ButtonBorder { get; private set; } = null!;
    public static Brush Hover { get; private set; } = null!;
    public static Brush NavHover { get; private set; } = null!;
    public static Brush NavSelected { get; private set; } = null!;
    public static Brush NavFocus { get; private set; } = null!;
    private static string name = "Full color";
    private static double saturation = 7;
    public static bool Bloom { get; private set; } = true;
    public static Brush Cpu => Phosphor("#6BEB5C");
    public static Brush Memory => Phosphor("#C76BFA");
    public static Brush Network => Phosphor("#52A8FF");
    public static Brush Energy => Phosphor("#FAD138");
    public static Brush Thermal => Phosphor("#FF9E2E");
    public static Brush Gpu => Phosphor("#4794FF");
    public static Brush Npu => Phosphor("#F24747");
    public static Brush Kernel => Phosphor("#F23838");
    public static Brush Accent => Cpu;

    static Theme() => Use(false);

    public static void Use(bool light)
    {
        if (light)
        {
            Background = Brush("#F3F3F5"); Panel = Brush("#FFFFFF"); Sidebar = Brush("#E7E7EB"); Line = Brush("#D0D0D4");
            Muted = Brush("#5E5E64"); Foreground = Brush("#1C1C1E"); Graph = Brush("#F7F7F9"); RowAlt = Brush("#F0F0F3");
            Selected = Brush("#D6E6F5"); ButtonFace = Brush("#E8E8EC"); ButtonBorder = Brush("#C8C8CC"); Hover = Brush("#E4E4E8");
            NavHover = Brush("#DEDEE4"); NavSelected = Brush("#D0E2F4"); NavFocus = Brush("#C2D8EE");
        }
        else
        {
            Background = Brush("#1C1C1E"); Panel = Brush("#252527"); Sidebar = Brush("#101011"); Line = Brush("#38383A");
            Muted = Brush("#A0A0A5"); Foreground = Brush("#E8E8EB"); Graph = Brush("#171719"); RowAlt = Brush("#202022");
            Selected = Brush("#28435E"); ButtonFace = Brush("#303032"); ButtonBorder = Brush("#48484C"); Hover = Brush("#3C3C40");
            NavHover = Brush("#232326"); NavSelected = Brush("#244468"); NavFocus = Brush("#31567C");
        }
    }

    public static Brush Brush(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    public static Brush Alpha(Brush brush, double opacity) { var b = brush.Clone(); b.Opacity = opacity; b.Freeze(); return b; }
    public static void Configure(Settings settings)
    {
        name = settings.Color; saturation = settings.Saturation; Bloom = settings.Bloom; Use(settings.ResolvedLight);
        if (Application.Current is { } app) Install(app);
    }
    private static Brush Phosphor(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(name switch { "Green" => "#6BF261", "Amber" => "#FFB338", "Blue" => "#52B8FF", "Mono" => "#D1D1D1", _ => hex });
        var grey = color.R * .3 + color.G * .59 + color.B * .11;
        var k = Math.Min(saturation / 7, 1.6);
        byte Channel(byte n) => (byte)Math.Clamp(grey + (n - grey) * k, 0, 255);
        var b = new SolidColorBrush(Color.FromRgb(Channel(color.R), Channel(color.G), Channel(color.B))); b.Freeze(); return b;
    }

    public static void Install(Application app)
    {
        var control = new Style(typeof(Control));
        control.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Segoe UI")));
        control.Setters.Add(new Setter(Control.FontSizeProperty, 12d));
        control.Setters.Add(new Setter(Control.ForegroundProperty, Foreground));
        app.Resources[typeof(Control)] = control;
        var button = new Style(typeof(Button), control);
        button.Setters.Add(new Setter(Control.BackgroundProperty, ButtonFace));
        button.Setters.Add(new Setter(Control.BorderBrushProperty, ButtonBorder));
        button.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 4, 10, 4)));
        button.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0)));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content); template.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Hover)); template.Triggers.Add(hover);
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Control.BorderBrushProperty, Foreground)); template.Triggers.Add(focus);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .4)); template.Triggers.Add(disabled);
        button.Setters.Add(new Setter(Control.TemplateProperty, template)); app.Resources[typeof(Button)] = button;

        var cell = new Style(typeof(DataGridCell));
        cell.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        cell.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 3, 6, 3)));
        var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, Selected));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, Foreground)); cell.Triggers.Add(selected);
        app.Resources[typeof(DataGridCell)] = cell;
        var header = new Style(typeof(DataGridColumnHeader));
        header.Setters.Add(new Setter(Control.BackgroundProperty, Background));
        header.Setters.Add(new Setter(Control.ForegroundProperty, Muted));
        header.Setters.Add(new Setter(Control.FontSizeProperty, 11d));
        header.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 7, 6, 7)));
        header.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
        header.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        app.Resources[typeof(DataGridColumnHeader)] = header;
        var tooltip = new Style(typeof(ToolTip));
        tooltip.Setters.Add(new Setter(Control.BackgroundProperty, Sidebar)); tooltip.Setters.Add(new Setter(Control.ForegroundProperty, Foreground));
        tooltip.Setters.Add(new Setter(Control.BorderBrushProperty, Line)); app.Resources[typeof(ToolTip)] = tooltip;
    }

    public static Style NavigationStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Foreground));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(8, 1, 8, 1)));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 34d));
        var template = new ControlTemplate(typeof(ListBoxItem));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter))); template.VisualTree = border;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, NavHover)); style.Triggers.Add(hover);
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, NavSelected));
        selected.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold)); style.Triggers.Add(selected);
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Control.BackgroundProperty, NavFocus)); style.Triggers.Add(focus);
        return style;
    }
}
