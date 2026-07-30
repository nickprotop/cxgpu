# Writing a GPU backend

A **backend** is how cxgpu talks to one vendor's hardware. NVIDIA, AMD and the demo simulator are all
backends; adding a third vendor — Intel Arc, say — means writing one class and registering it. The
UI, the alert engine and the Prometheus exporter all adapt to what your backend declares it can read,
so none of them need changing.

This guide assumes you can already build the project (see the README).

---

## The one rule

**Never report a number you did not measure.**

If your vendor tool cannot read fan speed, do not return `0` — declare `FanSpeed: false` and leave the
field alone. Everything downstream keys off that declaration: the UI omits the card, the exporter
omits the series, the alert engine never evaluates it.

A zero looks like a measurement. In the UI that is a card reading "0 RPM" on a fanless part; in
Prometheus it is a fabricated value averaged into a dashboard forever with nobody noticing. This rule
is why `GpuCapabilities` exists, and it is the one thing a reviewer will check first.

---

## The interface

Backends implement `IGpuBackend` (`cxgpu/Gpu/Abstractions/IGpuBackend.cs`). Most inherit
`GpuBackendPlugin`, which adapts it to SharpConsoleUI's plugin system for you.

You write typed members only. cxgpu itself calls every one of them through the framework's agnostic
`IPluginService.Execute(name, parameters)` ABI, but `GpuBackendPlugin` derives that surface from your
typed implementation — so there is no dictionary dispatch to write, and adding an operation there is
not something a vendor backend needs to do.

| Member | Contract |
|---|---|
| `BackendInfo` | Name and the mechanism actually selected. Only meaningful after `Probe()`. |
| `Capabilities` | What you can read. May depend on which mechanism `Probe()` chose. |
| `Probe()` | Can you serve data on this machine *right now*? Cheap, never throws. |
| `ReadSamples()` | Live per-GPU metrics. Empty on transient failure — never throws. |
| `ReadDeviceInfo()` | Static facts: name, driver, PCI address, VRAM size. Empty on failure. |
| `ReadProcesses()` | Processes using your GPUs. Empty when unsupported. |
| `SignalProcess()` | Deliver SIGTERM/SIGKILL. Report honestly; never assume success. |
| `GetSettings()` | Settings to expose in the F9 dialog. Empty is normal. |
| `ApplySettings()` | Apply stored values. Unknown keys ignored, unparseable values skipped. |

### `Probe()` must actually try

The single most common mistake is checking that a tool *exists*:

```csharp
// WRONG — a driver/library version mismatch leaves nvidia-smi installed
// and on PATH while every invocation fails.
public override bool Probe() => File.Exists("/usr/bin/nvidia-smi");

// RIGHT — run a real query and require real output.
public override bool Probe()
{
    try   { return RunNvidiaSmi("--query-gpu=index --format=csv,noheader").Count > 0; }
    catch { return false; }
}
```

A backend that probes true and then returns nothing is worse than one that probes false: the registry
counts it as live, so the user sees a vendor listed with no data and no explanation.

### Nothing may throw

`ReadSamples()`, `ReadDeviceInfo()` and `ReadProcesses()` are called on every refresh. A vendor tool
that fails once — a driver reload, a transient permissions error — must cost one empty frame, not the
app. Catch, return empty, let the next tick recover.

---

## Declaring capabilities

```csharp
public override GpuCapabilities Capabilities => new(
    FanSpeed:         true,   // is there a fan sensor?
    PowerLimit:       true,   // is a power cap reported?
    ThrottleReasons:  true,   // named throttle flags, not inferred
    EncoderDecoder:   true,   // NVENC/NVDEC style engine counters
    PerProcessMemory: true,   // can you attribute VRAM to a PID?
    PerProcessSm:     false,  // per-process compute %
    ProcessSignal:    true,   // can you signal those PIDs?
    CudaVersion:      false); // CUDA runtime version
```

Be conservative. Declaring a capability you cannot deliver produces confident wrong output; declaring
`false` for something you *can* read only means a metric is missing, which someone will notice and
fix.

Capabilities may depend on the mechanism. `AmdBackend` chooses between sysfs and `rocm-smi` at probe
time and reports different capabilities for each — `ThrottleReasons: false` in both cases, because
amdgpu exposes no named reason flags and inventing them would breach the one rule.

---

## Card identity

Fill `CardId` in `GpuDeviceInfo` with the card's PCI address, normalized through
`GpuIdentity.NormalizePciAddress`.

```csharp
CardId = GpuIdentity.NormalizePciAddress(rawAddressFromYourTool)
```

This is the key for per-card alert thresholds and the stable `card` label on exported metrics. Index
cannot serve: the registry reassigns indices globally, so a backend failing to probe shifts every
later card's index — silently applying one card's settings to another.

Normalization is not cosmetic. `nvidia-smi` pads the domain to eight digits (`00000000:01:00.0`) and
`rocm-smi` reports uppercase (`0000:C6:00.0`) where sysfs reports lowercase. Unnormalized, the same
card produces different keys depending on which path read it.

Return `""` if your vendor exposes no address. Empty means "no identity" and falls through to vendor
defaults — it must never match a `""` config key.

---

## Writing one

Create `cxgpu/Gpu/Backends/YourVendor/YourVendorBackend.cs`:

```csharp
namespace cxgpu.Gpu;

internal sealed class IntelBackend : GpuBackendPlugin
{
    // Name is the registry key and the settings-page title; Mechanism is the source actually
    // selected, surfaced in the spec-sheet so "which reader is live" is answerable from the screen.
    public override GpuBackendInfo BackendInfo => new(
        Name: "Intel", Vendor: "Intel", Mechanism: _mechanism);

    private string _mechanism = "";

    public override GpuCapabilities Capabilities => new(
        FanSpeed: false, PowerLimit: true, ThrottleReasons: false,
        EncoderDecoder: true, PerProcessMemory: true, PerProcessSm: false,
        ProcessSignal: true, CudaVersion: false);

    public override bool Probe()
    {
        try
        {
            if (ReadSamples().Count == 0) return false;
            _mechanism = "xpu-smi";
            return true;
        }
        catch { return false; }
    }

    public override IReadOnlyList<GpuSample> ReadSamples()
    {
        try
        {
            // Index is LOCAL to your backend here — the registry reassigns global indices.
            return Query().Select(row => new GpuSample(
                Index: row.Index,
                UtilizationPercent: row.Util,
                MemoryUsedPercent: row.MemUsed / row.MemTotal * 100,
                MemoryUsedMb: row.MemUsed,
                MemoryTotalMb: row.MemTotal,
                TemperatureC: row.Temp,
                PowerDrawWatts: row.Power,
                PowerLimitWatts: row.PowerCap,
                FanSpeedPercent: 0,          // declared unsupported — value is ignored
                SmClockMhz: row.CoreClock,
                MemClockMhz: row.MemClock)).ToList();
        }
        catch { return Array.Empty<GpuSample>(); }
    }

    // ReadDeviceInfo, ReadProcesses, SignalProcess as above.
}
```

Then register it in `GpuStatsFactory.KnownBackends`:

```csharp
public static IReadOnlyList<KnownBackend> KnownBackends { get; } = new[]
{
    new KnownBackend("NVIDIA", c => c.EnableNvidiaBackend, () => new NvidiaBackend()),
    new KnownBackend("AMD",    c => c.EnableAmdBackend,    () => new AmdBackend()),
    new KnownBackend("Intel",  c => c.EnableIntelBackend,  () => new IntelBackend()),
};
```

Every *known* backend gets a settings page whether or not it probed, so a user can enable one that is
currently switched off. Add the matching `EnableIntelBackend` to `CxgpuConfig`.

---

## Optional: exposing settings

If your backend has a real choice to offer — which of two mechanisms to read through, say — declare
it and the settings dialog renders it for you:

```csharp
public override IReadOnlyList<PluginSetting> GetSettings() => new[]
{
    new PluginSetting(
        Key: "Reader",
        Label: "Data source",
        Kind: PluginSettingKind.Choice,
        Default: "auto",
        Hint: "auto prefers sysfs on Linux; the CLI is used on Windows",
        Options: new[] { "auto", "sysfs", "rocm-smi" },
        RequiresRestart: true)
};

public override void ApplySettings(IReadOnlyDictionary<string, string?> values)
{
    // Unknown keys ignored, unparseable values leave the current setting untouched — a config
    // written by a newer build must not break startup.
    if (values.TryGetValue("Reader", out var v) && !string.IsNullOrWhiteSpace(v))
        _reader = v.Trim();
}
```

Set `RequiresRestart` honestly. The registry probes once at startup, so a setting that changes which
mechanism is probed cannot take effect until the next launch — claiming otherwise leaves the user
waiting for a change that never comes.

Settings are applied **before** `Probe()`, so a mechanism choice takes effect on the probe it governs.

---

## Testing

Unit-test the parsing, not the process. Split the pure parse from the invocation so it can be
exercised against captured output:

```csharp
// In the backend:
private Dictionary<int, PmonSample> ReadPmon() => ParsePmon(RunTool("pmon -c 1"));
internal static Dictionary<int, PmonSample> ParsePmon(IReadOnlyList<string> lines) { ... }
```

Use **real captured output** as fixtures, not invented text. The existing pmon tests carry verbatim
output from two driver versions because the column set differs between them — a bug that hand-written
fixtures would not have caught.

Then verify your tests can actually fail: revert the behaviour they guard and confirm they go red. A
test written against your own implementation often passes for the wrong reason. One of ours initially
passed against the very bug it was written to prevent, because both fixtures happened to share a
column layout.

Run the suite with:

```bash
dotnet test cxgpu.Tests/cxgpu.Tests.csproj
```

### Testing without the hardware

`--demo` exercises the multi-GPU paths on any machine, and is the right place to test anything
destructive — the demo backend refuses signals and uses PIDs above `pid_max` precisely so a misfired
kill cannot reach a real process.

---

## Checklist

- [ ] `Probe()` runs a real query and returns false on any failure
- [ ] No read method throws — all return empty on failure
- [ ] Capabilities describe only what you can genuinely deliver
- [ ] No metric is reported as `0` to stand in for "unknown"
- [ ] `CardId` filled from the PCI address via `GpuIdentity.NormalizePciAddress`, or `""`
- [ ] Registered in `GpuStatsFactory.KnownBackends` with a config toggle
- [ ] Parsing split from invocation and unit-tested against captured output
- [ ] Tests verified to fail when the behaviour they guard is reverted
- [ ] Checked on real hardware and under `--demo`

---

## Where to look

| Reference | File |
|---|---|
| Simplest backend | `Gpu/Backends/Demo/DemoBackend.cs` |
| CLI-driven vendor | `Gpu/Backends/Nvidia/NvidiaBackend.cs` |
| Multi-mechanism, settings | `Gpu/Backends/Amd/AmdBackend.cs` |
| The contract | `Gpu/Abstractions/IGpuBackend.cs` |
| Capability model | `Gpu/Abstractions/GpuCapabilities.cs` |
