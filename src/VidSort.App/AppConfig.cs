using System.IO;
using System.Text.Json;

namespace VidSort.App;

public sealed class AppConfig
{
    public string RootDir { get; set; } = string.Empty;

    public string[] HiddenCategories { get; set; } = Array.Empty<string>();

    public string OnConflict { get; set; } = "Rename";

    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            string path = ConfigPath;
            if (!File.Exists(path))
            {
                var defaults = new AppConfig();
                File.WriteAllText(path, JsonSerializer.Serialize(defaults, JsonOptions));
                return defaults;
            }

            AppConfig config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions)
                ?? new AppConfig();
            return Normalize(config);
        }
        catch
        {
            return new AppConfig();
        }
    }

    private static AppConfig Normalize(AppConfig config)
    {
        return new AppConfig
        {
            RootDir = config.RootDir?.Trim() ?? string.Empty,
            HiddenCategories = (config.HiddenCategories ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .ToArray(),
            OnConflict = string.IsNullOrWhiteSpace(config.OnConflict) ? "Rename" : config.OnConflict.Trim()
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
