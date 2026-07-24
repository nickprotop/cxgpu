using System.Diagnostics;
using System.Globalization;
using cxnvmon.Helpers;

namespace cxnvmon.Stats;

internal class NvidiaSmiGpuStatsProvider : IGpuStatsProvider
{
    public GpuSnapshot ReadSnapshot()
    {
        try
        {
            var gpuData = RunNvidiaSmi("--query-gpu=index,utilization.gpu,utilization.memory,memory.used,memory.total,temperature.gpu,power.draw,power.limit,fan.speed,clocks.sm,clocks.mem --format=csv,noheader,nounits");
            var gpuSamples = new List<GpuSample>();

            foreach (var line in gpuData)
            {
                var parts = line.Split(',');
                if (parts.Length < 11) continue;

                // NOTE: parts[2] is utilization.memory (memory-controller activity %, ~0% at idle),
                // NOT the fraction of VRAM in use. The real "memory used %" is memory.used/total.
                var memUsedMb = double.Parse(parts[3].Trim(), CultureInfo.InvariantCulture);
                var memTotalMb = double.Parse(parts[4].Trim(), CultureInfo.InvariantCulture);
                var memUsedPercent = memTotalMb > 0 ? memUsedMb / memTotalMb * 100.0 : 0.0;

                gpuSamples.Add(new GpuSample(
                    Index: int.Parse(parts[0].Trim()),
                    UtilizationPercent: double.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
                    MemoryUsedPercent: memUsedPercent,
                    MemoryUsedMb: memUsedMb,
                    MemoryTotalMb: memTotalMb,
                    TemperatureC: double.Parse(parts[5].Trim(), CultureInfo.InvariantCulture),
                    PowerDrawWatts: double.Parse(parts[6].Trim(), CultureInfo.InvariantCulture),
                    PowerLimitWatts: double.Parse(parts[7].Trim(), CultureInfo.InvariantCulture),
                    FanSpeedPercent: double.Parse(parts[8].Trim(), CultureInfo.InvariantCulture),
                    SmClockMhz: double.Parse(parts[9].Trim(), CultureInfo.InvariantCulture),
                    MemClockMhz: double.Parse(parts[10].Trim(), CultureInfo.InvariantCulture)
                ));
            }

            var procData = RunNvidiaSmi("--query-compute-apps=pid,process_name,used_memory --format=csv,noheader,nounits");
            var procSamples = new List<GpuProcessSample>();

            foreach (var line in procData)
            {
                var parts = line.Split(',');
                if (parts.Length < 3) continue;

                procSamples.Add(new GpuProcessSample(
                    GpuIndex: 0,
                    Pid: int.Parse(parts[0].Trim()),
                    Name: parts[1].Trim(),
                    MemoryUsedMb: double.Parse(parts[2].Trim(), CultureInfo.InvariantCulture)
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

            foreach (var line in deviceInfoData)
            {
                var parts = line.Split(',');
                if (parts.Length < 9) continue;

                deviceInfos.Add(new GpuDeviceInfo(
                    Index: int.Parse(parts[0].Trim()),
                    Name: parts[1].Trim(),
                    DriverVersion: parts[2].Trim(),
                    VBiosVersion: parts[3].Trim(),
                    PcieGenWidth: $"{parts[5].Trim()}x{parts[4].Trim()}",
                    CudaVersion: "", 
                    MemoryTotalMb: double.Parse(parts[6].Trim(), CultureInfo.InvariantCulture),
                    PowerLimitWatts: double.Parse(parts[7].Trim(), CultureInfo.InvariantCulture),
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
