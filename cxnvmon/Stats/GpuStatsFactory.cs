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
    public static IGpuStatsProvider Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new NvidiaSmiGpuStatsProvider();
        }

        // For now, we only implement Linux with nvidia-smi
        throw new PlatformNotSupportedException("Only Linux with nvidia-smi is currently supported.");
    }

    /// <summary>
    /// Gets a human-readable name for the current platform.
    /// </summary>
    public static string GetPlatformName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "Linux (NVIDIA)";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "Windows";

        return "Unknown";
    }
}
