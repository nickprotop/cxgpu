using System.Diagnostics;
using System.Globalization;

namespace cxnvmon.Stats;

internal class NvidiaSmiGpuStatsProvider : IGpuStatsProvider
{
    // CUDA version is a driver-level constant, so it's read once (lazily, on the first
    // ReadDeviceInfo) and cached. It is NOT a --query-gpu field — it only appears in
    // `nvidia-smi -q` / the plain-output header, hence the separate call.
    private string? _cudaVersion;

    public GpuSnapshot ReadSnapshot()
    {
        try
        {
            var gpuData = RunNvidiaSmi("--query-gpu=index,utilization.gpu,utilization.memory,memory.used,memory.total,temperature.gpu,power.draw,power.limit,fan.speed,clocks.sm,clocks.mem,utilization.encoder,utilization.decoder,clocks_throttle_reasons.hw_thermal_slowdown,clocks_throttle_reasons.sw_thermal_slowdown,clocks_throttle_reasons.sw_power_cap,clocks_throttle_reasons.hw_slowdown,clocks_throttle_reasons.hw_power_brake_slowdown --format=csv,noheader,nounits");
            var gpuSamples = new List<GpuSample>();
            var uuidToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in gpuData)
            {
                var parts = line.Split(',');
                if (parts.Length < 18) continue;

                // NOTE: parts[2] is utilization.memory (memory-controller activity %, ~0% at idle),
                // NOT the fraction of VRAM in use. The real "memory used %" is memory.used/total.
                var memUsedMb = ParseDouble(parts[3]);
                var memTotalMb = ParseDouble(parts[4]);
                var memUsedPercent = memTotalMb > 0 ? memUsedMb / memTotalMb * 100.0 : 0.0;

                gpuSamples.Add(new GpuSample(
                    Index: (int)ParseDouble(parts[0]),
                    UtilizationPercent: ParseDouble(parts[1]),
                    MemoryUsedPercent: memUsedPercent,
                    MemoryUsedMb: memUsedMb,
                    MemoryTotalMb: memTotalMb,
                    TemperatureC: ParseDouble(parts[5]),
                    PowerDrawWatts: ParseDouble(parts[6]),
                    PowerLimitWatts: ParseDouble(parts[7]),
                    FanSpeedPercent: ParseDouble(parts[8]),
                    SmClockMhz: ParseDouble(parts[9]),
                    MemClockMhz: ParseDouble(parts[10]),
                    EncoderPercent: ParseDouble(parts[11]),
                    DecoderPercent: ParseDouble(parts[12]),
                    // Thermal covers both the hardware slowdown and the softer driver-side
                    // thermal cap; either one means "heat is costing you clocks".
                    ThrottleThermal: ParseFlag(parts[13]) || ParseFlag(parts[14]),
                    ThrottlePower: ParseFlag(parts[15]),
                    // hw_slowdown is the umbrella HW bit; power_brake is the external-brake variant.
                    ThrottleHwSlowdown: ParseFlag(parts[16]) || ParseFlag(parts[17])
                ));
            }

            // Map GPU UUID -> index so compute-apps rows (which identify their GPU by uuid, not
            // index) can be scoped to a GPU in multi-GPU setups.
            var uuidData = RunNvidiaSmi("--query-gpu=index,uuid --format=csv,noheader,nounits");
            foreach (var line in uuidData)
            {
                var parts = line.Split(',');
                if (parts.Length < 2) continue;
                if (int.TryParse(parts[0].Trim(), out var idx))
                    uuidToIndex[parts[1].Trim()] = idx;
            }

            var procData = RunNvidiaSmi("--query-compute-apps=pid,process_name,used_memory,gpu_uuid --format=csv,noheader,nounits");
            var procSamples = new List<GpuProcessSample>();

            foreach (var line in procData)
            {
                var parts = line.Split(',');
                if (parts.Length < 3) continue;

                var gpuIndex = 0;
                if (parts.Length >= 4 && uuidToIndex.TryGetValue(parts[3].Trim(), out var mapped))
                    gpuIndex = mapped;

                if (!int.TryParse(parts[0].Trim(), out var pid)) continue;

                procSamples.Add(new GpuProcessSample(
                    GpuIndex: gpuIndex,
                    Pid: pid,
                    Name: parts[1].Trim(),
                    MemoryUsedMb: ParseDouble(parts[2])
                ));
            }

            return new GpuSnapshot(gpuSamples, procSamples);
        }
        catch
        {
            return new GpuSnapshot(new List<GpuSample>(), new List<GpuProcessSample>());
        }
    }

    public IReadOnlyList<GpuDeviceInfo> ReadDeviceInfo()
    {
        try
        {
            var deviceInfoData = RunNvidiaSmi("--query-gpu=index,name,driver_version,vbios_version,pcie.link.width.current,pcie.link.gen.current,memory.total,power.limit,temperature.gpu --format=csv,noheader,nounits");
            var deviceInfos = new List<GpuDeviceInfo>();
            var cuda = ReadCudaVersion();

            foreach (var line in deviceInfoData)
            {
                var parts = line.Split(',');
                if (parts.Length < 9) continue;

                deviceInfos.Add(new GpuDeviceInfo(
                    Index: (int)ParseDouble(parts[0]),
                    Name: parts[1].Trim(),
                    DriverVersion: parts[2].Trim(),
                    VBiosVersion: parts[3].Trim(),
                    PcieGenWidth: $"{parts[5].Trim()}x{parts[4].Trim()}",
                    CudaVersion: cuda,
                    MemoryTotalMb: ParseDouble(parts[6]),
                    PowerLimitWatts: ParseDouble(parts[7]),
                    TemperatureLimitC: 0
                ));
            }

            return deviceInfos;
        }
        catch
        {
            return new List<GpuDeviceInfo>();
        }
    }

    // Reads the driver's CUDA runtime version from `nvidia-smi -q` (line: "CUDA Version : 13.2").
    // Cached after the first successful read; returns "" when unavailable so the spec-sheet simply
    // omits the row (its CUDA row renders only when non-empty).
    private string ReadCudaVersion()
    {
        if (_cudaVersion != null) return _cudaVersion;

        try
        {
            foreach (var line in RunNvidiaSmi("-q"))
            {
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith("CUDA Version", StringComparison.OrdinalIgnoreCase)) continue;

                var colon = trimmed.IndexOf(':');
                if (colon < 0) continue;

                var value = trimmed[(colon + 1)..].Trim();
                if (value.Length > 0 && !value.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                    return _cudaVersion = value;
            }
        }
        catch
        {
        }

        return _cudaVersion = "";
    }

    // nvidia-smi emits "[N/A]", "[Not Supported]" and similar for unsupported fields; treat any
    // unparseable value as 0 rather than throwing away the whole sample.
    private static double ParseDouble(string raw) =>
        double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0.0;

    // Throttle-reason fields are the strings "Active" / "Not Active" (or "[N/A]" when the driver
    // doesn't expose them, which reads as not throttling).
    private static bool ParseFlag(string raw) =>
        raw.Trim().Equals("Active", StringComparison.OrdinalIgnoreCase);

    private List<string> RunNvidiaSmi(string args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "nvidia-smi",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null) return new List<string>();

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            return new List<string>();
        }

        return output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
