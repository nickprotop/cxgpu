using System.Runtime.InteropServices;

namespace cxgpu.Gpu;

/// <summary>
/// Factory for creating GPU statistics providers.
/// </summary>
internal static class GpuStatsFactory
{
    /// <summary>
    /// Creates a GPU statistics provider based on the current operating system.
    /// </summary>
    /// <returns>An implementation of IGpuStatsProvider</returns>
    /// <exception cref="PlatformNotSupportedException">
    /// Thrown when the current platform is not supported.
    /// </exception>
    public static IGpuStatsProvider Create(string[]? args = null,
                                           Configuration.CxgpuConfig? config = null)
    {
        // Demo mode (--demo[=n], or CXGPU_FAKE_GPUS=n) substitutes a synthetic multi-GPU provider
        // so the multi-GPU UI — summary strip, selector, throttle chip — can be exercised on a
        // single-GPU machine, or with no NVIDIA hardware at all. Off in normal use.
        var fakeCount = DemoBackend.ConfiguredCount(args);
        if (fakeCount.HasValue)
        {
            // Demo mode loads ONLY the demo backend — real vendors are not probed — so the
            // "DEMO · N simulated GPUs" header cannot be showing a mix of real and synthetic data.
            return new GpuBackendRegistry(new IGpuBackend[] { new DemoBackend(fakeCount.Value) });
        }

        return new GpuBackendRegistry(VendorCandidates(config));
    }

    /// <summary>
    /// The vendor backends to probe, in the order that fixes GPU numbering (NVIDIA first, so the
    /// discrete card stays index 0 on a hybrid machine). Probing is what decides which survive, so
    /// listing a backend here is not a claim that it is present.
    /// </summary>
    private static IEnumerable<IGpuBackend> VendorCandidates(Configuration.CxgpuConfig? config)
    {
        // Order fixes GPU numbering: NVIDIA first, so a discrete card stays index 0 on a hybrid
        // machine and the selected GPU does not move between runs.
        if (config?.EnableNvidiaBackend ?? true)
            yield return Configured(new NvidiaBackend(), config);

        if (config?.EnableAmdBackend ?? true)
            yield return Configured(new AmdBackend(), config);
    }

    /// <summary>
    /// A vendor cxgpu knows how to read, whether or not it is present or enabled on this machine.
    /// </summary>
    /// <param name="Name">Stable identifier, matching <see cref="GpuBackendInfo.Name"/> and the config key.</param>
    /// <param name="IsEnabled">Reads the per-vendor toggle from config.</param>
    /// <param name="Create">Builds an unprobed instance, for reading its declared settings.</param>
    internal sealed record KnownBackend(
        string Name,
        Func<Configuration.CxgpuConfig, bool> IsEnabled,
        Func<IGpuBackend> Create);

    /// <summary>
    /// Every vendor the app can read, independent of what actually probed.
    ///
    /// The settings UI needs this rather than the registry's active list: a backend that is disabled or
    /// absent still has to appear, or there would be no way to switch one on. The registry only knows
    /// what succeeded.
    /// </summary>
    public static IReadOnlyList<KnownBackend> KnownBackends { get; } = new[]
    {
        new KnownBackend("NVIDIA", c => c.EnableNvidiaBackend, () => new NvidiaBackend()),
        new KnownBackend("AMD", c => c.EnableAmdBackend, () => new AmdBackend()),
    };

    /// <summary>
    /// Feeds a backend its stored settings BEFORE it is probed. Order matters: the AMD reader choice
    /// decides which mechanism probing even attempts, so applying settings afterwards would leave the
    /// setting with no effect until the next restart.
    /// </summary>
    private static IGpuBackend Configured(IGpuBackend backend, Configuration.CxgpuConfig? config)
    {
        if (config != null &&
            config.BackendSettings.TryGetValue(backend.InfoVia().Name, out var values) &&
            values.Count > 0)
        {
            try
            {
                backend.ApplySettingsVia(values);
            }
            catch
            {
                // A backend that cannot digest its stored settings still runs on its defaults; a bad
                // config value must not remove the vendor.
            }
        }

        return backend;
    }

    /// <summary>
    /// Gets a human-readable name for the current platform. In demo mode this says so explicitly —
    /// the numbers on screen are synthetic, and the header is the one place that can't be mistaken.
    /// </summary>
    public static string GetPlatformName(string[]? args = null)
    {
        var demo = args != null
            ? DemoBackend.ConfiguredCount(args)
            : DemoBackend.ActiveCount;
        if (demo.HasValue)
            return $"DEMO · {Math.Clamp(demo.Value, 1, DemoBackend.MaxDemoGpuCount)} simulated GPUs";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "Linux (NVIDIA)";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "Windows";

        return "Unknown";
    }
}
