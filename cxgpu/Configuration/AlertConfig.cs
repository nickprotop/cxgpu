using cxgpu.Gpu;
using cxgpu.Gpu.Alerts;

namespace cxgpu.Configuration;

/// <summary>A configurable warn/critical pair. Null members fall through to the built-in defaults.</summary>
internal sealed class ThresholdConfig
{
    public double? Warn { get; set; }
    public double? Critical { get; set; }
}

/// <summary>Per-card or per-vendor threshold overrides.</summary>
internal sealed class CardAlertConfig
{
    /// <summary>
    /// The card's name, written by us and never read back. Present so a config file keyed on PCI
    /// addresses is legible six months later.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The card's vendor UUID where it has one. Recorded only — nothing is looked up by it today.
    /// Kept so a later version could recognise "same UUID, new PCI address" and migrate this entry
    /// when a card moves slots, rather than silently reverting it to defaults.
    /// </summary>
    public string? Uuid { get; set; }

    public ThresholdConfig? TemperatureC { get; set; }
    public ThresholdConfig? MemoryPercent { get; set; }
    public ThresholdConfig? PowerPercent { get; set; }
}

/// <summary>
/// Alerting configuration.
///
/// Evaluation is ON by default because it costs nothing — arithmetic over a snapshot already in hand —
/// and because a feature nobody can discover may as well not exist. The badge only appears once
/// something fires, so a healthy machine shows no new chrome.
/// </summary>
internal sealed class AlertConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Warning toasts auto-dismiss after a few seconds.</summary>
    public bool ToastOnWarning { get; set; } = true;

    /// <summary>
    /// Critical toasts are STICKY — they stay until dismissed, because a thermal throttle that
    /// scrolled past unseen defeats the point of raising it.
    /// </summary>
    public bool ToastOnCritical { get; set; } = true;

    /// <summary>Print a per-GPU summary of the session's peaks and events on exit.</summary>
    public bool SessionSummaryOnExit { get; set; } = true;

    /// <summary>Overrides by vendor ("nvidia", "amd").</summary>
    public Dictionary<string, CardAlertConfig> Vendors { get; set; } = new();

    /// <summary>
    /// Overrides by card, keyed on the normalized PCI address ("0000:01:00.0").
    ///
    /// PCI rather than index: the registry reassigns indices globally, so a backend failing to probe
    /// on one boot would shift every later card's index and silently apply one card's thresholds to
    /// another.
    /// </summary>
    public Dictionary<string, CardAlertConfig> Cards { get; set; } = new();

    /// <summary>
    /// Resolves the thresholds for a card: per-card overrides win, then per-vendor, then the built-in
    /// defaults. Each metric falls through independently, so overriding one card's temperature does
    /// not mean restating every other metric.
    /// </summary>
    public AlertThresholds ResolveFor(GpuDeviceInfo info)
    {
        var defaults = AlertThresholds.DefaultFor(info.Backend, info.Name);

        // An empty CardId is "no identity", not a key — it must never match a "" config entry.
        var card = !string.IsNullOrEmpty(info.CardId) && Cards.TryGetValue(info.CardId, out var c)
            ? c : null;

        var vendor = Vendors.TryGetValue(info.Backend ?? "", out var v) ? v : null;

        return new AlertThresholds(
            TemperatureC: Merge(defaults.TemperatureC, card?.TemperatureC, vendor?.TemperatureC),
            MemoryPercent: Merge(defaults.MemoryPercent, card?.MemoryPercent, vendor?.MemoryPercent),
            PowerPercent: Merge(defaults.PowerPercent, card?.PowerPercent, vendor?.PowerPercent));
    }

    // The clear margin is deliberately NOT configurable: it exists to stop a value resting on the
    // threshold from flapping, and a user setting it to 0 would reintroduce exactly that bug. It is
    // scaled from the default pair so an override keeps proportionate hysteresis.
    private static ThresholdPair? Merge(ThresholdPair? fallback, ThresholdConfig? card, ThresholdConfig? vendor)
    {
        double? warn = card?.Warn ?? vendor?.Warn;
        double? critical = card?.Critical ?? vendor?.Critical;

        if (warn == null && critical == null) return fallback;
        if (fallback == null && (warn == null || critical == null)) return null;

        return new ThresholdPair(
            Warn: warn ?? fallback!.Warn,
            Critical: critical ?? fallback!.Critical,
            ClearMargin: fallback?.ClearMargin ?? 3);
    }
}
