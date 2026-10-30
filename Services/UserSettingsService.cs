using System;
using System.IO;
using System.Text.Json;

namespace DemCleaner.Services;

public class UserSettings
{
    public int ThemeIndex { get; set; } = 0;
    public string MapProvider { get; set; } = "Google Earth";
    public bool UseGdalNoData { get; set; } = true;
    public bool UseCustomThreshold { get; set; } = true;
    public int ComparisonIndex { get; set; } = 0;
    public string CustomNoDataValue { get; set; } = "-9999";
    public int FillMethod { get; set; } = 0; // 0: Min, 1: Max, 2: Mean, 3: Interpolated
    public int InterpolationPasses { get; set; } = 10;
    public bool UseGlobalForConstantFill { get; set; } = true;
    public bool EnablePerTileFillOverride { get; set; } = false;
    public int SeamSmoothingSteps { get; set; } = 0;
    public int ExportLayout { get; set; } = 0; // 0: SourceGrid, 1: SingleImage, 2: CustomGrid
    public string ExportGridSize { get; set; } = "2";
    public int ResolutionPresetSetIndex { get; set; } = 0;
    public string ExportWidth { get; set; } = "1024";
    public string ExportHeight { get; set; } = "1024";
    public bool LockExportAspectRatio { get; set; } = false;
    public int ExportFileFormatIndex { get; set; } = 0;
    public string ExportFolder { get; set; } = string.Empty;
    public bool ExportStandardXyNaming { get; set; } = true;
    public string ExportPrefix { get; set; } = string.Empty;
    public string ExportSuffix { get; set; } = "_cleaned";
}

public static class UserSettingsService
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DemCleaner"
    );

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<UserSettings>(json);
                if (settings != null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // Fall back to default settings on read or parse failure
        }
        return new UserSettings();
    }

    public static void Save(UserSettings settings)
    {
        try
        {
            if (!Directory.Exists(SettingsDirectory))
            {
                Directory.CreateDirectory(SettingsDirectory);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(settings, options);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Do not crash application if saving settings fails
        }
    }
}

