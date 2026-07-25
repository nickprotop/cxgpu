using System.Globalization;

namespace cxnvmon.Stats;

/// <summary>
/// Reads AMD GPU telemetry straight from the Linux kernel: <c>/sys/class/drm/cardN/device</c> for
/// utilization and memory, its <c>hwmon</c> node for temperature/power/clock, and
/// <c>/proc/&lt;pid&gt;/fdinfo</c> for per-process memory.
///
/// Preferred over the CLI on Linux for measured reasons: reading files costs no subprocess (~0 ms
/// versus ~80 ms per tick for rocm-smi), needs no root, requires nothing installed, and is the only
/// source here that can attribute memory to processes.
/// </summary>
internal sealed class AmdSysfsReader : IAmdReader
{
    private const string DrmRoot = "/sys/class/drm";
    private const string AmdVendorId = "0x1002";

    public string Mechanism => "sysfs";

    public IReadOnlyList<string> MechanismAliases => new[] { "sysfs" };

    /// <summary>
    /// Fan is absent on this class of part (an APU has no dedicated fan, so <c>fan1_input</c> is
    /// ENOENT) and amdgpu exposes no encoder/decoder utilization or named throttle-reason bits.
    /// Declaring those false is what keeps the UI from rendering an unsupported metric as a measured 0%.
    /// </summary>
    public GpuCapabilities Capabilities => new(
        FanSpeed: false,
        PowerLimit: false,
        ThrottleReasons: false,
        EncoderDecoder: false,
        PerProcessMemory: true,
        PerProcessSm: false,
        ProcessSignal: true,
        CudaVersion: false);

    public bool Probe()
    {
        if (!OperatingSystem.IsLinux()) return false;

        try
        {
            return AmdCardPaths().Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The AMD render devices, in card order.
    ///
    /// Filtering matters here: <c>/sys/class/drm</c> contains one entry per DISPLAY CONNECTOR as well
    /// as per card (on this box, 14 <c>card*</c> entries for 2 actual GPUs — card1-DP-1,
    /// card1-HDMI-A-1 and so on). Connectors have no <c>device/vendor</c>, so requiring that file
    /// readable as 0x1002 selects exactly the AMD devices.
    /// </summary>
    private static List<string> AmdCardPaths()
    {
        var result = new List<string>();
        if (!Directory.Exists(DrmRoot)) return result;

        foreach (var entry in Directory.GetDirectories(DrmRoot, "card*").OrderBy(p => p, StringComparer.Ordinal))
        {
            // Reject connectors by name too (cardN-DP-1); cheap and makes the intent explicit.
            var name = Path.GetFileName(entry);
            if (name.Contains('-')) continue;

            var devicePath = Path.Combine(entry, "device");
            var vendor = ReadText(Path.Combine(devicePath, "vendor"));
            if (vendor != null && vendor.Trim().Equals(AmdVendorId, StringComparison.OrdinalIgnoreCase))
                result.Add(devicePath);
        }

        return result;
    }

    public IReadOnlyList<GpuSample> ReadSamples()
    {
        var samples = new List<GpuSample>();
        var cards = AmdCardPaths();

        for (int i = 0; i < cards.Count; i++)
        {
            var device = cards[i];
            var hwmon = HwmonPath(device);

            double vramUsed = ReadDouble(Path.Combine(device, "mem_info_vram_used")) ?? 0;
            double vramTotal = ReadDouble(Path.Combine(device, "mem_info_vram_total")) ?? 0;
            double usedMb = vramUsed / (1024.0 * 1024.0);
            double totalMb = vramTotal / (1024.0 * 1024.0);

            // hwmon reports millidegrees, microwatts and hertz.
            double? tempC = hwmon == null ? null : ReadDouble(Path.Combine(hwmon, "temp1_input")) / 1000.0;
            double? powerW = hwmon == null ? null : ReadDouble(Path.Combine(hwmon, "power1_average")) / 1_000_000.0;
            double? clockMhz = hwmon == null ? null : ReadDouble(Path.Combine(hwmon, "freq1_input")) / 1_000_000.0;

            samples.Add(new GpuSample(
                Index: i,
                UtilizationPercent: ReadDouble(Path.Combine(device, "gpu_busy_percent")) ?? 0,
                MemoryUsedPercent: totalMb > 0 ? usedMb / totalMb * 100.0 : 0,
                MemoryUsedMb: usedMb,
                MemoryTotalMb: totalMb,
                TemperatureC: tempC ?? 0,
                PowerDrawWatts: powerW ?? 0,
                // power1_cap does not exist on this part (verified: ENOENT), so there is no cap to
                // report. 0 means "unknown" and Capabilities.PowerLimit is false.
                //
                // KNOWN GAP: GpuSample.PowerLimitWatts and FanSpeedPercent are non-nullable doubles,
                // so an unsupported metric can only be expressed as 0 — which the UI currently renders
                // as a measured "0 W" / "🌀 0%". Making these metrics optional changes the record's
                // shape and every consumer, so it is done in the step that wires Capabilities into the
                // UI (cards for unsupported metrics are omitted rather than shown as zero), not here.
                PowerLimitWatts: ReadDouble(Path.Combine(hwmon ?? device, "power1_cap")) / 1_000_000.0 ?? 0,
                FanSpeedPercent: 0,
                SmClockMhz: clockMhz ?? 0,
                MemClockMhz: CurrentDpmClockMhz(Path.Combine(device, "pp_dpm_mclk")) ?? 0));
        }

        return samples;
    }

    public IReadOnlyList<GpuDeviceInfo> ReadDeviceInfo()
    {
        var infos = new List<GpuDeviceInfo>();
        var cards = AmdCardPaths();

        for (int i = 0; i < cards.Count; i++)
        {
            var device = cards[i];
            var hwmon = HwmonPath(device);

            double vramTotal = ReadDouble(Path.Combine(device, "mem_info_vram_total")) ?? 0;

            infos.Add(new GpuDeviceInfo(
                Index: i,
                Name: DeviceName(device),
                DriverVersion: DriverVersion(),
                VBiosVersion: ReadText(Path.Combine(device, "vbios_version"))?.Trim() ?? "",
                PcieGenWidth: PcieGenWidth(device),
                // amdgpu is not CUDA; leaving this empty is what keeps the spec-sheet row hidden
                // rather than showing something meaningless.
                CudaVersion: "",
                MemoryTotalMb: vramTotal / (1024.0 * 1024.0),
                PowerLimitWatts: ReadDouble(Path.Combine(hwmon ?? device, "power1_cap")) / 1_000_000.0 ?? 0,
                TemperatureLimitC: 0));
        }

        return infos;
    }

    /// <summary>
    /// Per-process GPU memory from <c>/proc/&lt;pid&gt;/fdinfo</c>, which is how modern DRM exposes it.
    ///
    /// Two things this must get right, both verified on the box:
    /// - A process holds MANY drm fds — gnome-shell has 21 with a <c>drm-driver</c> line but only two
    ///   distinct <c>drm-client-id</c> values. Summing every fd would inflate its memory roughly
    ///   tenfold, so totals are accumulated per distinct client id.
    /// - Another user's <c>/proc</c> entries are unreadable. Those processes are OMITTED, never
    ///   reported as using 0 bytes, which would be a measurement we did not make.
    /// </summary>
    public IReadOnlyList<GpuProcessSample> ReadProcesses()
    {
        var result = new List<GpuProcessSample>();
        if (!OperatingSystem.IsLinux()) return result;

        foreach (var procDir in SafeEnumerateDirectories("/proc"))
        {
            var pidText = Path.GetFileName(procDir);
            if (!int.TryParse(pidText, out var pid)) continue;

            var seenClients = new HashSet<string>();
            double totalKib = 0;
            bool isAmdClient = false;

            foreach (var fdinfo in SafeEnumerateFiles(Path.Combine(procDir, "fdinfo")))
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(fdinfo);
                }
                catch
                {
                    continue;   // fd closed underneath us, or not permitted
                }

                string? driver = null, clientId = null;
                double vramKib = 0;

                foreach (var line in lines)
                {
                    if (line.StartsWith("drm-driver:", StringComparison.Ordinal))
                        driver = line[11..].Trim();
                    else if (line.StartsWith("drm-client-id:", StringComparison.Ordinal))
                        clientId = line[14..].Trim();
                    else if (line.StartsWith("drm-memory-vram:", StringComparison.Ordinal))
                        vramKib = ParseKib(line[16..]);
                }

                if (driver != "amdgpu" || clientId == null) continue;

                isAmdClient = true;
                // Count each DRM client once, however many fds reference it.
                if (seenClients.Add(clientId))
                    totalKib += vramKib;
            }

            if (!isAmdClient) continue;

            result.Add(new GpuProcessSample(
                Pid: pid,
                Name: ProcessName(procDir),
                MemoryUsedMb: totalKib / 1024.0,
                // Single-AMD-GPU assumption: fdinfo does not name which card a client used. With one
                // AMD device this is exact; with several it would need the render-node minor, which
                // fdinfo does not expose. Left at 0 deliberately rather than guessed.
                GpuIndex: 0,
                // amdgpu exposes no per-process compute/encode figures — null, not zero.
                SmPercent: null,
                MemPercent: null,
                EncPercent: null,
                DecPercent: null));
        }

        return result;
    }

    #region sysfs helpers

    /// <summary>
    /// Resolves the hwmon node by GLOB, never by remembering an index: it is <c>hwmon6</c> on this box
    /// today, but the number is assigned at driver load and is not stable across reboots.
    /// </summary>
    private static string? HwmonPath(string devicePath)
    {
        try
        {
            var root = Path.Combine(devicePath, "hwmon");
            if (!Directory.Exists(root)) return null;
            return Directory.GetDirectories(root, "hwmon*").OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The active level from a <c>pp_dpm_*</c> table, whose format is one level per line with the
    /// current one starred: <c>0: 800Mhz *</c>.
    /// </summary>
    private static double? CurrentDpmClockMhz(string path)
    {
        var text = ReadText(path);
        if (text == null) return null;

        foreach (var line in text.Split('\n'))
        {
            if (!line.TrimEnd().EndsWith('*')) continue;

            // Split off the level index ("0:") so the clock is what remains: "0: 800Mhz *".
            var parts = line.Split(':', 2);
            if (parts.Length != 2) continue;

            var value = new string(parts[1].SkipWhile(c => !char.IsDigit(c))
                                           .TakeWhile(char.IsDigit)
                                           .ToArray());
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz))
                return mhz;
        }

        return null;
    }

    /// <summary>
    /// A display name. sysfs exposes no marketing string, so this reports the PCI ids honestly rather
    /// than inventing a product name.
    /// </summary>
    private static string DeviceName(string devicePath)
    {
        var device = ReadText(Path.Combine(devicePath, "device"))?.Trim() ?? "";
        return device.Length > 0 ? $"AMD GPU {device}" : "AMD GPU";
    }

    /// <summary>Kernel release — the amdgpu driver ships with the kernel, so this is its version.</summary>
    private static string DriverVersion()
    {
        var text = ReadText("/proc/sys/kernel/osrelease");
        return text?.Trim() ?? "";
    }

    private static string PcieGenWidth(string devicePath)
    {
        var gen = ReadText(Path.Combine(devicePath, "current_link_speed"))?.Trim();
        var width = ReadText(Path.Combine(devicePath, "current_link_width"))?.Trim();
        if (string.IsNullOrEmpty(gen) || string.IsNullOrEmpty(width)) return "";

        // current_link_speed reads like "16.0 GT/s PCIe"; map the transfer rate to a PCIe generation.
        var generation = gen switch
        {
            var s when s.StartsWith("2.5") => "1",
            var s when s.StartsWith("5.0") => "2",
            var s when s.StartsWith("8.0") => "3",
            var s when s.StartsWith("16.0") => "4",
            var s when s.StartsWith("32.0") => "5",
            _ => ""
        };
        return generation.Length > 0 ? $"{generation}x{width}" : "";
    }

    private static string ProcessName(string procDir)
    {
        // cmdline gives the full path (matching how nvidia-smi names processes); comm is the fallback.
        try
        {
            var cmdline = File.ReadAllText(Path.Combine(procDir, "cmdline"));
            var first = cmdline.Split('\0', 2)[0];
            if (first.Length > 0) return first;
        }
        catch
        {
        }

        return ReadText(Path.Combine(procDir, "comm"))?.Trim() ?? "?";
    }

    /// <summary>Parses "912452 KiB" — fdinfo memory values carry a unit suffix.</summary>
    private static double ParseKib(string raw)
    {
        var text = raw.Trim();
        var number = new string(text.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return 0;

        if (text.Contains("MiB", StringComparison.OrdinalIgnoreCase)) return value * 1024;
        if (text.Contains("GiB", StringComparison.OrdinalIgnoreCase)) return value * 1024 * 1024;
        return value;   // KiB, the usual case
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static double? ReadDouble(string path)
    {
        var text = ReadText(path);
        if (text == null) return null;
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.EnumerateFiles(path) : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();   // another user's /proc — omit, do not report zero
        }
    }

    #endregion
}
