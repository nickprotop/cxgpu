using System.Text.Json;
using System.Text.Json.Serialization;

namespace cxnvmon.Configuration;

/// <summary>
/// User-editable cxnvmon settings, persisted as JSON at the platform config location
/// (<c>~/.config/cxnvmon/config.json</c> on Linux, <c>%APPDATA%\cxnvmon\config.json</c> on
/// Windows). Loading tolerates a missing or corrupt file by falling back to defaults, so the
/// app always starts. Saving creates the directory as needed.
/// </summary>
internal sealed class CxnvmonConfig
{
    // Bounds for the refresh interval, shared with the settings dialog's slider.
    public const int MinRefreshIntervalMs = 250;
    public const int MaxRefreshIntervalMs = 5000;
    public const int DefaultRefreshIntervalMs = 1000;

    // Sparkline height (rows) for the Overview cards.
    public const int MinSparklineHeight = 2;
    public const int MaxSparklineHeight = 12;
    public const int DefaultSparklineHeight = 5;

    public int RefreshIntervalMs { get; set; } = DefaultRefreshIntervalMs;
    public int SparklineHeight { get; set; } = DefaultSparklineHeight;
    public bool ShowOverviewTab { get; set; } = true;
    public bool ShowProcessesTab { get; set; } = true;
    public bool ShowDetailsTab { get; set; } = true;

    [JsonIgnore]
    public static CxnvmonConfig Default => new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// The absolute path to the config file:
    /// <c>{ApplicationData}/cxnvmon/config.json</c> (honours <c>XDG_CONFIG_HOME</c> on Linux via
    /// <see cref="Environment.SpecialFolder.ApplicationData"/>).
    /// </summary>
    public static string FilePath
    {
        get
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(baseDir))
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(baseDir, "cxnvmon", "config.json");
        }
    }

    /// <summary>
    /// Loads the config from <see cref="FilePath"/>, returning defaults if the file is absent,
    /// empty, or unparseable. Never throws — a bad file must not stop the app from starting.
    /// </summary>
    public static CxnvmonConfig Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
                return Default;

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return Default;

            var config = JsonSerializer.Deserialize<CxnvmonConfig>(json, JsonOptions);
            return config?.Clamped() ?? Default;
        }
        catch
        {
            // Corrupt/unreadable config must never block startup.
            return Default;
        }
    }

    /// <summary>
    /// Persists the config to <see cref="FilePath"/>, creating the directory if needed.
    /// Returns <c>true</c> on success; failures (e.g. read-only location) are swallowed and
    /// reported via the return value rather than throwing.
    /// </summary>
    public bool Save()
    {
        try
        {
            var path = FilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, JsonSerializer.Serialize(Clamped(), JsonOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns a copy of these values with the refresh interval clamped to valid bounds.</summary>
    public CxnvmonConfig Clamped() => new()
    {
        RefreshIntervalMs = Math.Clamp(RefreshIntervalMs, MinRefreshIntervalMs, MaxRefreshIntervalMs),
        SparklineHeight = Math.Clamp(SparklineHeight, MinSparklineHeight, MaxSparklineHeight),
        ShowOverviewTab = ShowOverviewTab,
        ShowProcessesTab = ShowProcessesTab,
        ShowDetailsTab = ShowDetailsTab,
    };
}
