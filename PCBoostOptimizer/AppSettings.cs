using System.Text.Json;

namespace PCBoostOptimizer;

internal sealed class AppSettings
{
    public bool BackgroundMonitoringEnabled { get; set; } = true;
    public bool AutoMaintenanceEnabled { get; set; } = true;
    public bool OverlayVisible { get; set; } = true;
    public int OverlayLeft { get; set; } = -1;
    public int OverlayTop { get; set; } = -1;
    public DateTime LastAutomaticMaintenanceUtc { get; set; } = DateTime.MinValue;
}

internal static class AppSettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCBoostOptimizer");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var content = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(content) ?? new AppSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var temporaryPath = SettingsPath + ".tmp";
            var content = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The monitor remains usable even when settings cannot be persisted.
        }
    }
}
