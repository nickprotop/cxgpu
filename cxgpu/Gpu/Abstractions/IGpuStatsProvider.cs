using cxgpu.Gpu;

namespace cxgpu.Gpu;

/// <summary>
/// Interface for collecting GPU statistics.
/// </summary>
internal interface IGpuStatsProvider
{
    /// <summary>
    /// Reads a complete snapshot of GPU statistics.
    /// </summary>
    /// <returns>A complete GPU snapshot</returns>
    GpuSnapshot ReadSnapshot();

    /// <summary>
    /// Reads static GPU device information.
    /// </summary>
    /// <returns>A list of static GPU information</returns>
    IReadOnlyList<GpuDeviceInfo> ReadDeviceInfo();
}
