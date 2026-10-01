using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Afterglow.Windows;

internal sealed record Settings(double Interval = .5, string Color = "Full color", bool AlwaysOnTop = false, bool Alerts = false, double Saturation = 7, bool Bloom = true, string Appearance = "Dark", bool ShowSelf = true, bool AlertsChosen = false)
{
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Afterglow", "settings.json");

    [JsonIgnore]
    public bool ResolvedLight => Appearance == "Light" || (Appearance == "System" && AppsUseLightTheme());

    public static Settings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return new();
            var root = doc.RootElement;
            var interval = Number(root, "Interval") ?? .5;
            var color = Text(root, "Color") ?? "Full color";
            var top = Flag(root, "AlwaysOnTop", false);
            var alerts = Flag(root, "AlertsChosen", false) && Flag(root, "Alerts", false);
            var saturation = Number(root, "Saturation") ?? 7;
            var bloom = Flag(root, "Bloom", true);
            var appearance = Text(root, "Appearance") ?? "Dark";
            if (appearance is not ("System" or "Light")) appearance = "Dark";
            var showSelf = Flag(root, "ShowSelf", true);
            var intervalOk = new[] { .25, .5, 1d, 2d, 5d }.Contains(interval);
            var colorOk = new[] { "Full color", "Green", "Amber", "Blue", "Mono" }.Contains(color);
            if (!intervalOk || !colorOk || !double.IsFinite(saturation) || saturation is < 0 or > 11) return new();
            return new(interval, color, top, alerts, saturation, bloom, appearance, showSelf, Flag(root, "AlertsChosen", false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this with { AlertsChosen = true }));
        File.Move(temp, FilePath, true);
    }

    private static bool AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int theme && theme != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    private static double? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : null;

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // A missing flag keeps the supplied default. Only an explicit false turns a default-true flag off.
    private static bool Flag(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback
        } : fallback;
}
