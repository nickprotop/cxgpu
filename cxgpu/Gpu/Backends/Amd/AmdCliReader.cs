using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace cxgpu.Gpu;

/// <summary>
/// Reads AMD GPU telemetry through the <c>amd-smi</c> / <c>rocm-smi</c> CLI.
///
/// This is the Windows path — sysfs is a Linux kernel interface and cannot exist there — and the Linux
/// fallback when sysfs is unavailable. On Linux the sysfs reader is preferred on merit: it costs no
/// subprocess (measured ~0 ms per tick against ~80 ms here) and is the only source that can attribute
/// memory to processes.
/// </summary>
internal sealed class AmdCliReader : IAmdReader
{
    // amd-smi is the modern tool and the one shipped on Windows; rocm-smi is the older name still
    // present on many Linux installs. Both accept --json.
    private static readonly string[] CandidateTools = { "amd-smi", "rocm-smi" };

    // Windows installs are not on PATH by default.
    private static readonly string[] WindowsSearchDirs =
    {
        @"C:\Program Files\AMD\ROCm\bin",
        @"C:\Program Files\AMD\RyzenMasterSDK",
    };

    private string? _tool;

    public string Mechanism => _tool == null ? "amd-smi" : Path.GetFileNameWithoutExtension(_tool);

    /// <summary>Either CLI name selects this reader; which one wins is decided by Probe.</summary>
    public IReadOnlyList<string> MechanismAliases => new[] { "amd-smi", "rocm-smi", "cli" };

    /// <summary>
    /// Deliberately narrower than the sysfs reader's: <c>rocm-smi --showpids</c> reports
    /// "No JSON data to report" on this hardware, so per-process attribution is NOT available here.
    /// Declaring that honestly is what lets the Processes tab show "unsupported" instead of an empty
    /// list that would read as "nothing is using the GPU".
    /// </summary>
    public GpuCapabilities Capabilities => new(
        FanSpeed: false,
        PowerLimit: false,
        ThrottleReasons: false,
        EncoderDecoder: false,
        PerProcessMemory: false,
        PerProcessSm: false,
        ProcessSignal: true,
        CudaVersion: false);

    public bool Probe()
    {
        foreach (var candidate in ResolveToolPaths())
        {
            try
            {
                var json = RunJson(candidate, "--showtemp --json");
                if (json == null) continue;

                using var doc = JsonDocument.Parse(json);
                // A tool that runs but enumerates no card is not usable.
                if (doc.RootElement.EnumerateObject().Any(p => p.Name.StartsWith("card", StringComparison.OrdinalIgnoreCase)))
                {
                    _tool = candidate;
                    return true;
                }
            }
            catch
            {
                // Try the next candidate.
            }
        }

        return false;
    }

    public IReadOnlyList<GpuSample> ReadSamples()
    {
        // Only real cards; the payload can also carry a "system" entry (driver version).
        var cards = ReadCards("--showtemp --showuse --showmemuse --showpower --showclocks --showmeminfo vram --json")
            .Where(c => c.Values != null && c.Name.StartsWith("card", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var samples = new List<GpuSample>();

        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i].Values!;

            double totalBytes = Number(c, "VRAM Total Memory (B)") ?? 0;
            double usedBytes = Number(c, "VRAM Total Used Memory (B)") ?? 0;
            double totalMb = totalBytes / (1024.0 * 1024.0);
            double usedMb = usedBytes / (1024.0 * 1024.0);

            // Prefer the absolute figures; fall back to the percentage when --showmeminfo was refused.
            double memPercent = totalMb > 0
                ? usedMb / totalMb * 100.0
                : Number(c, "GPU Memory Allocated (VRAM%)") ?? 0;

            samples.Add(new GpuSample(
                Index: i,
                UtilizationPercent: Number(c, "GPU use (%)") ?? 0,
                MemoryUsedPercent: memPercent,
                MemoryUsedMb: usedMb,
                MemoryTotalMb: totalMb,
                TemperatureC: Number(c, "Temperature (Sensor edge) (C)") ?? 0,
                PowerDrawWatts: Number(c, "Current Socket Graphics Package Power (W)") ?? 0,
                // No cap is reported; Capabilities.PowerLimit is false so the UI omits the limit.
                PowerLimitWatts: 0,
                FanSpeedPercent: 0,
                SmClockMhz: Clock(c, "sclk clock speed:") ?? 0,
                MemClockMhz: Clock(c, "mclk clock speed:") ?? 0));
        }

        return samples;
    }

    public IReadOnlyList<GpuDeviceInfo> ReadDeviceInfo()
    {
        var cards = ReadCards("--showproductname --showdriverversion --showvbios --showmeminfo vram --json");
        var infos = new List<GpuDeviceInfo>();

        // The driver version is reported under a "system" key rather than per card.
        var driver = cards.FirstOrDefault(c => c.Name.Equals("system", StringComparison.OrdinalIgnoreCase))
                          .Values?.GetValueOrDefault("Driver version") ?? "";

        var gpuCards = cards.Where(c => c.Name.StartsWith("card", StringComparison.OrdinalIgnoreCase)).ToList();

        for (int i = 0; i < gpuCards.Count; i++)
        {
            var c = gpuCards[i].Values!;

            infos.Add(new GpuDeviceInfo(
                Index: i,
                Name: DeviceName(c),
                DriverVersion: driver,
                VBiosVersion: Text(c, "VBIOS version") ?? "",
                // The CLI does not report PCIe link state in this query; leave it blank rather than
                // guess, so the spec-sheet row is simply omitted.
                PcieGenWidth: "",
                CudaVersion: "",
                MemoryTotalMb: (Number(c, "VRAM Total Memory (B)") ?? 0) / (1024.0 * 1024.0),
                PowerLimitWatts: 0,
                TemperatureLimitC: 0));
        }

        return infos;
    }

    /// <summary>
    /// Always empty: <c>--showpids</c> yields "No JSON data to report" on this hardware, and
    /// <see cref="Capabilities"/> reports PerProcessMemory=false so the UI presents that as
    /// "unsupported" rather than as "no processes running".
    /// </summary>
    public IReadOnlyList<GpuProcessSample> ReadProcesses() => Array.Empty<GpuProcessSample>();

    #region CLI plumbing

    /// <summary>
    /// A card entry from the JSON payload. Keys are the tool's own card numbering, which is NOT the
    /// DRM card number: rocm-smi calls this machine's only AMD device "card0" while DRM calls it
    /// card1. Index is therefore assigned by position, never parsed from the key.
    /// </summary>
    private readonly record struct CardEntry(string Name, Dictionary<string, string>? Values);

    private List<CardEntry> ReadCards(string args)
    {
        var result = new List<CardEntry>();
        if (_tool == null) return result;

        var json = RunJson(_tool, args);
        if (json == null) return result;

        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in property.Value.EnumerateObject())
                {
                    if (field.Value.ValueKind == JsonValueKind.String)
                        values[field.Name] = field.Value.GetString() ?? "";
                }
                result.Add(new CardEntry(property.Name, values));
            }
        }
        catch (JsonException)
        {
            // Malformed output is treated as no data rather than crashing the tick.
        }

        // Card order must be deterministic so indices are stable between reads.
        return result.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Runs the tool and returns its JSON payload.
    ///
    /// The payload is located by finding the first '{': rocm-smi prints diagnostics to stdout BEFORE
    /// the JSON — on this box, a low-power-state warning and an "Exception caught: map::at" line — so
    /// parsing from byte zero fails on a perfectly working system.
    /// </summary>
    private static string? RunJson(string tool, string args)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = tool,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(5000)) return null;

            int start = output.IndexOf('{');
            if (start < 0) return null;

            int end = output.LastIndexOf('}');
            return end > start ? output[start..(end + 1)] : null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> ResolveToolPaths()
    {
        foreach (var tool in CandidateTools)
        {
            yield return tool;   // found on PATH

            if (OperatingSystem.IsWindows())
            {
                foreach (var dir in WindowsSearchDirs)
                {
                    var full = Path.Combine(dir, tool + ".exe");
                    if (File.Exists(full)) yield return full;
                }
            }
        }
    }

    private static string? Text(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var v) && v.Length > 0 && !v.Equals("N/A", StringComparison.OrdinalIgnoreCase)
            ? v
            : null;

    private static double? Number(Dictionary<string, string> values, string key)
    {
        var text = Text(values, key);
        if (text == null) return null;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// Parses a clock value, which arrives parenthesised and unit-suffixed: <c>(800Mhz)</c>.
    /// </summary>
    private static double? Clock(Dictionary<string, string> values, string key)
    {
        var text = Text(values, key);
        if (text == null) return null;

        var digits = new string(text.SkipWhile(c => !char.IsDigit(c))
                                    .TakeWhile(c => char.IsDigit(c) || c == '.')
                                    .ToArray());
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz)
            ? mhz
            : null;
    }

    /// <summary>
    /// A display name. "Card Series" is N/A on this part, so fall back through SKU and GFX version
    /// rather than reporting an empty name.
    /// </summary>
    private static string DeviceName(Dictionary<string, string> values)
    {
        var series = Text(values, "Card Series");
        if (series != null) return series;

        var sku = Text(values, "Card SKU");
        var gfx = Text(values, "GFX Version");
        if (sku != null && gfx != null) return $"AMD {sku} ({gfx})";
        if (sku != null) return $"AMD {sku}";
        if (gfx != null) return $"AMD {gfx}";

        return "AMD GPU";
    }

    #endregion
}
