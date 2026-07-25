namespace cxgpu.Stats;

/// <summary>
/// Identifies a GPU backend and the mechanism it is currently reading through.
/// </summary>
/// <param name="Name">
/// Stable identifier, unique per backend. Used as the config key and as the plugin service name
/// (<c>Gpu.{Name}</c>), so it must not change between releases. One per VENDOR — "NVIDIA", "AMD".
/// </param>
/// <param name="Vendor">Human-readable vendor, shown in the UI.</param>
/// <param name="Mechanism">
/// The data source actually in use ("nvidia-smi", "sysfs", "rocm-smi"). A vendor may support several;
/// the backend resolves which one at probe time, so this is only meaningful after a successful probe.
/// Surfaced in the UI purely so it is diagnosable which source is live.
/// </param>
/// <param name="Version">Driver or tool version when known, else null.</param>
internal record GpuBackendInfo(
    string Name,
    string Vendor,
    string Mechanism,
    string? Version = null);
