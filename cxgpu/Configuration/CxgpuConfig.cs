using System.Text.Json;
using System.Text.Json.Serialization;

namespace cxgpu.Configuration;

/// <summary>
/// User-editable cxgpu settings, persisted as JSON at the platform config location
/// (<c>~/.config/cxgpu/config.json</c> on Linux, <c>%APPDATA%\cxgpu\config.json</c> on
/// Windows). Loading tolerates a missing or corrupt file by falling back to defaults, so the
/// app always starts. Saving creates the directory as needed.
/// </summary>
internal sealed class CxgpuConfig
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
    public bool ShowTimeAxis { get; set; } = true;
    public bool ShowOverviewTab { get; set; } = true;
    public bool ShowProcessesTab { get; set; } = true;
    public bool ShowDetailsTab { get; set; } = true;

    // Per-vendor backend toggles. A disabled backend is never PROBED — no subprocess spawned, no
    // sysfs read — so switching one off on a machine that lacks it removes even the failed launch.
    public bool EnableNvidiaBackend { get; set; } = true;
    public bool EnableAmdBackend { get; set; } = true;

    /// <summary>
    /// Settings declared by the backends themselves, keyed by backend name then setting key.
    ///
    /// Stored as a nested dictionary rather than named properties so a backend can add a setting
    /// without changing this schema. Unknown keys are PRESERVED across save/load: a config written by
    /// a newer build, or by a backend that is currently disabled, must survive a round-trip rather
    /// than being silently dropped.
    /// </summary>
    public Dictionary<string, Dictionary<string, string?>> BackendSettings { get; set; } = new();

    [JsonIgnore]
    public static CxgpuConfig Default => new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// The absolute path to the config file:
    /// <c>{ApplicationData}/cxgpu/config.json</c> (honours <c>XDG_CONFIG_HOME</c> on Linux via
    /// <see cref="Environment.SpecialFolder.ApplicationData"/>).
    /// </summary>
    public static string FilePath
    {
        get
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(baseDir))
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(baseDir, "cxgpu", "config.json");
        }
    }

    /// <summary>
    /// Loads the config from <see cref="FilePath"/>, returning defaults if the file is absent,
    /// empty, or unparseable. Never throws — a bad file must not stop the app from starting.
    /// </summary>
    public static CxgpuConfig Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
                return Default;

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return Default;

            var config = JsonSerializer.Deserialize<CxgpuConfig>(json, JsonOptions);
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
    public CxgpuConfig Clamped() => new()
    {
        RefreshIntervalMs = Math.Clamp(RefreshIntervalMs, MinRefreshIntervalMs, MaxRefreshIntervalMs),
        SparklineHeight = Math.Clamp(SparklineHeight, MinSparklineHeight, MaxSparklineHeight),
        ShowTimeAxis = ShowTimeAxis,
        ShowOverviewTab = ShowOverviewTab,
        ShowProcessesTab = ShowProcessesTab,
        ShowDetailsTab = ShowDetailsTab,
        // NOTE: this method rebuilds the object field by field, so anything omitted here is silently
        // reset to its default on every save. Add new settings to this list as well as above.
        EnableNvidiaBackend = EnableNvidiaBackend,
        EnableAmdBackend = EnableAmdBackend,
        BackendSettings = BackendSettings,
    };
}
