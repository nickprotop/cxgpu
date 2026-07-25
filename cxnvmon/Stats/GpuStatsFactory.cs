using System.Runtime.InteropServices;

namespace cxnvmon.Stats;

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
    public static IGpuStatsProvider Create(string[]? args = null)
    {
        // Demo mode (--demo[=n], or CXNVMON_FAKE_GPUS=n) substitutes a synthetic multi-GPU provider
        // so the multi-GPU UI — summary strip, selector, throttle chip — can be exercised on a
        // single-GPU machine, or with no NVIDIA hardware at all. Off in normal use.
        var fakeCount = FakeMultiGpuStatsProvider.ConfiguredCount(args);
        if (fakeCount.HasValue)
            return new FakeMultiGpuStatsProvider(fakeCount.Value);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new NvidiaSmiGpuStatsProvider();
        }

        // For now, we only implement Linux with nvidia-smi
        throw new PlatformNotSupportedException("Only Linux with nvidia-smi is currently supported.");
    }

    /// <summary>
    /// Gets a human-readable name for the current platform. In demo mode this says so explicitly —
    /// the numbers on screen are synthetic, and the header is the one place that can't be mistaken.
    /// </summary>
    public static string GetPlatformName(string[]? args = null)
    {
        var demo = args != null
            ? FakeMultiGpuStatsProvider.ConfiguredCount(args)
            : FakeMultiGpuStatsProvider.ActiveCount;
        if (demo.HasValue)
            return $"DEMO · {Math.Clamp(demo.Value, 1, FakeMultiGpuStatsProvider.MaxDemoGpuCount)} simulated GPUs";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "Linux (NVIDIA)";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "Windows";

        return "Unknown";
    }
}
