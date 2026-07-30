using System.Globalization;
using cxgpu.Configuration;
using cxgpu.Gpu.Alerts;
using cxgpu.Helpers;
using cxgpu.Gpu;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Rendering;

namespace cxgpu.Dashboard;

/// <summary>
/// The modal settings dialog, laid out as a NavigationView: a rail of pages on the left, the selected
/// page's fields on the right.
///
/// It was a single scrolling form until the per-backend settings arrived, at which point Save fell
/// below the fold and the sections stopped being scannable. The rail also gives plugin-owned settings
/// a natural home — one page per backend — so a third vendor adds a rail item rather than more scroll.
///
/// Each page owns its own <see cref="FormControl"/>, because a form only reports the fields it holds
/// and the rail builds pages lazily. Save therefore merges every form built so far, and anything
/// absent keeps its current value.
/// </summary>
internal static class SettingsDialog
{
    private const int DialogWidth = 78;
    private const int DialogHeight = 26;
    private const int NavWidth = 20;

    // Slider track width, leaving room for the value label beside it inside the dialog's field column.
    private const int SliderTrackWidth = 24;

    // App-owned field keys.
    private const string KeyRefresh = "refresh";
    private const string KeySparkHeight = "sparkheight";
    private const string KeyTimeAxis = "timeaxis";
    private const string KeyOverview = "overview";
    private const string KeyProcesses = "processes";
    private const string KeyDetails = "details";
    private const string KeyAlertsEnabled = "alertsenabled";
    private const string KeyToastWarning = "toastwarn";
    private const string KeyToastCritical = "toastcrit";
    private const string KeySessionSummary = "sessionsummary";
    private const string KeyNvidiaWarn = "nvwarn";
    private const string KeyNvidiaCritical = "nvcrit";
    private const string KeyAmdWarn = "amdwarn";
    private const string KeyAmdCritical = "amdcrit";

    /// <summary>
    /// A labelled slider that SHOWS ITS CURRENT VALUE.
    ///
    /// FormBuilder.AddSlider leaves SliderControl.ShowValueLabel at its default of false, so a bare
    /// slider renders as a track with no number: the label says what is being set, but not what it is
    /// being set TO. For a threshold in °C that makes the control unusable — you cannot tell whether
    /// you have landed on 83 or 87.
    /// </summary>
    private static FormBuilder AddValueSlider(FormBuilder form, string key, string label,
                                              double min, double max, double initial,
                                              string suffix = "", string? hint = null)
    {
        var slider = new SliderControl
        {
            MinValue = min,
            MaxValue = max,
            Value = initial,
            ShowValueLabel = true,
            // Whole numbers: the readout says "83", not "83.00".
            ValueLabelFormat = "F0",
            // The track must be sized explicitly. SliderControl defaults to Stretch, which fills the
            // field column and pushes the value label off the right edge of the dialog — visible as a
            // slider you can drag with no number attached. An explicit width also requires switching
            // alignment away from Stretch.
            Width = SliderTrackWidth,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        return form.AddField(key, label, slider, () => slider.Value.ToString("F0"), hint: hint);
    }

    /// <summary>
    /// A vendor's configured temperature threshold, or the built-in default when unset — so the
    /// sliders open showing what is actually in force rather than an arbitrary starting value.
    /// </summary>
    private static double VendorTemp(CxgpuConfig config, string vendor, bool warn)
    {
        var configured = config.Alerts.Vendors.TryGetValue(vendor, out var v) ? v.TemperatureC : null;
        var value = warn ? configured?.Warn : configured?.Critical;
        if (value is { } set) return set;

        var defaults = vendor.Equals("amd", StringComparison.OrdinalIgnoreCase)
            ? AlertThresholds.AmdDiscrete
            : AlertThresholds.NvidiaConsumer;

        return warn ? defaults.TemperatureC!.Warn : defaults.TemperatureC!.Critical;
    }

    // Prefix for backend-owned fields, so they cannot collide with app keys and can be told apart when
    // collecting values back: "backend:AMD:Reader".
    private const string BackendFieldPrefix = "backend:";

    // Per-backend enable toggle, e.g. "backend:AMD:__enabled". An app-level setting that happens to be
    // rendered on the backend's page, so it is filtered out of the backend's own stored settings.
    private const string EnabledKey = "__enabled";

    /// <summary>
    /// Opens the dialog seeded from <paramref name="current"/>. On submit the edited config is saved to
    /// disk and passed to <paramref name="onApply"/>.
    /// </summary>
    /// <param name="activeBackends">
    /// The backends that actually probed, used to report each vendor's live state. The rail itself is
    /// built from every KNOWN vendor, so one that is absent or disabled can still be configured.
    /// </param>
    public static void Show(ConsoleWindowSystem windowSystem, CxgpuConfig current,
                            Action<CxgpuConfig> onApply,
                            IReadOnlyList<IGpuBackend>? activeBackends = null)
    {
        var accent = UIConstants.Accent.ToMarkup();
        var muted = UIConstants.MutedText.ToMarkup();

        // Every form built so far, so Save can gather fields from pages the user did or did not visit.
        var forms = new List<FormControl>();

        var nav = Controls.NavigationView()
            .WithNavWidth(NavWidth)
            // Without this the pane collapses to first-letter icons: the default expanded threshold is
            // wider than this dialog, so Auto mode resolved to Compact and the page names vanished.
            .WithExpandedThreshold(50)
            .WithPaneHeader($"[{accent} bold]cxgpu[/]")
            .WithContentBorder(BorderStyle.Rounded)
            .WithContentBorderColor(UIConstants.SeparatorColor)
            .WithContentPadding(1, 0, 1, 0)
            .WithSelectedColors(UIConstants.Accent, UIConstants.TileSelectedBg)
            .AddHeader("General", UIConstants.Accent, header => header
                .AddItem("Refresh", subtitle: "Update interval",
                    content: panel => AddForm(panel, forms, f =>
                        AddValueSlider(f, KeyRefresh, "Interval (ms)",
                            CxgpuConfig.MinRefreshIntervalMs, CxgpuConfig.MaxRefreshIntervalMs,
                            current.RefreshIntervalMs,
                            hint: $"{CxgpuConfig.MinRefreshIntervalMs}–{CxgpuConfig.MaxRefreshIntervalMs} ms between updates")))
                .AddItem("Graphs", subtitle: "Sparklines",
                    content: panel => AddForm(panel, forms, f =>
                        AddValueSlider(f, KeySparkHeight, "Sparkline height",
                            CxgpuConfig.MinSparklineHeight, CxgpuConfig.MaxSparklineHeight,
                            current.SparklineHeight,
                            hint: $"{CxgpuConfig.MinSparklineHeight}–{CxgpuConfig.MaxSparklineHeight} rows (restart to apply)")
                        .AddCheckbox(KeyTimeAxis, "Show time axis", current.ShowTimeAxis)))
                .AddItem("Tabs", subtitle: "Visible views",
                    content: panel => AddForm(panel, forms, f => f
                        .AddCheckbox(KeyOverview, "Overview", current.ShowOverviewTab)
                        .AddCheckbox(KeyProcesses, "Processes", current.ShowProcessesTab)))
                .AddItem("Alerts", subtitle: "Thresholds & notifications",
                    content: panel => AddForm(panel, forms, f =>
                    {
                        f.AddCheckbox(KeyAlertsEnabled, "Enable alerts", current.Alerts.Enabled)
                         .AddCheckbox(KeyToastWarning, "Toast on warning (auto-dismiss)",
                             current.Alerts.ToastOnWarning)
                         .AddCheckbox(KeyToastCritical, "Toast on critical (stays until dismissed)",
                             current.Alerts.ToastOnCritical)
                         .AddCheckbox(KeySessionSummary, "Print session summary on exit",
                             current.Alerts.SessionSummaryOnExit);

                        // Temperature only: memory and power thresholds are percentages whose
                        // defaults hold across vendors, while °C is the one that genuinely differs
                        // per part — and the one whose built-in values are judgement calls.
                        AddValueSlider(f, KeyNvidiaWarn, "NVIDIA warn °C", 50, 100,
                            VendorTemp(current, "nvidia", warn: true),
                            hint: "NVIDIA: warn above this, critical above the next");
                        AddValueSlider(f, KeyNvidiaCritical, "NVIDIA crit °C", 50, 110,
                            VendorTemp(current, "nvidia", warn: false));
                        AddValueSlider(f, KeyAmdWarn, "AMD warn °C", 50, 105,
                            VendorTemp(current, "amd", warn: true),
                            hint: "AMD parts run hotter by design; defaults are higher");
                        AddValueSlider(f, KeyAmdCritical, "AMD crit °C", 50, 115,
                            VendorTemp(current, "amd", warn: false));

                        return f;
                    })))
            .AddHeader("Backends", UIConstants.Accent, header =>
            {
                // Every KNOWN vendor gets a page, not only those that probed — otherwise a disabled or
                // absent backend could never be switched on.
                foreach (var known in GpuStatsFactory.KnownBackends)
                {
                    var live = activeBackends?.FirstOrDefault(b => b.InfoVia().Name == known.Name);
                    var enabled = known.IsEnabled(current);
                    var entry = known;

                    header.AddItem(
                        known.Name,
                        subtitle: StateSubtitle(live, enabled),
                        content: panel => BuildBackendPage(panel, forms, entry, live, enabled, current));
                }
            })
            .Fill()
            .Build();

        var hint = Controls.Markup($"[{muted}]↑↓ pages · Tab fields · Esc cancels[/]")
            .WithMargin(1, 0, 1, 0)
            .Build();

        var saveButton = Controls.Button("  Save  ")
            .WithBorder(ButtonBorderStyle.Rounded)
            .WithBorderColor(UIConstants.Accent)
            .WithBackgroundColor(Color.Transparent)
            .WithBorderBackgroundColor(Color.Transparent)
            .Build();

        var cancelButton = Controls.Button(" Cancel ")
            .WithBorder(ButtonBorderStyle.Rounded)
            .WithBorderColor(UIConstants.SeparatorColor)
            .WithBackgroundColor(Color.Transparent)
            .WithBorderBackgroundColor(Color.Transparent)
            .Build();

        var buttons = HorizontalGridControl.ButtonRow(saveButton, cancelButton);

        Window? dialogRef = null;
        var dialog = new WindowBuilder(windowSystem)
            .WithTitle("Settings")
            .WithSize(DialogWidth, DialogHeight)
            .Centered()
            .AsModal()
            .WithBorderColor(UIConstants.Accent)
            .AddControls(nav, buttons, hint)
            // Esc closes and CONSUMES the key: the main window binds Esc to Shutdown, so leaving it
            // unhandled would quit the app from a dialog the user was backing out of.
            .OnKeyPressed((_, e) =>
            {
                if (e.KeyInfo.Key != ConsoleKey.Escape) return;
                if (dialogRef != null) windowSystem.CloseWindow(dialogRef);
                e.Handled = true;
            })
            .BuildAndShow();
        dialogRef = dialog;

        // Vertical gradient behind the dialog, matching how cxfiles/cxpost treat their modals. Set on
        // the built window rather than through the builder, which is the pattern those apps use
        // (cxfiles OptionsModal). Uses cxgpu's own palette rather than their literal blues, so it
        // reads as this app — the main window already gradients BaseBg -> BaseEnd, so this stays in
        // family while sitting a shade darker to separate the modal from what is behind it.
        // ColorGradient is fully qualified: cxgpu.Helpers is already imported here, so the bare name
        // would be ambiguous with the framework's SharpConsoleUI.Helpers.
        dialog.BackgroundGradient = new GradientBackground(
            SharpConsoleUI.Helpers.ColorGradient.FromColors(UIConstants.BaseEnd, UIConstants.HeaderBg),
            GradientDirection.Vertical);

        cancelButton.Click += (_, _) => windowSystem.CloseWindow(dialog);
        saveButton.Click += (_, _) =>
        {
            var updated = ApplyValues(current, CollectAllValues(forms));
            updated.Save();
            windowSystem.CloseWindow(dialog);
            onApply(updated);
        };
    }

    /// <summary>
    /// A one-line state summary for the rail. "Disabled" and "Not detected" are different facts and are
    /// reported as such, rather than both reading as simply unavailable.
    /// </summary>
    private static string StateSubtitle(IGpuBackend? live, bool enabled)
    {
        if (!enabled) return "Disabled";
        if (live == null) return "Not detected";

        var mechanism = live.InfoVia().Mechanism;
        return string.IsNullOrWhiteSpace(mechanism) ? "Active" : $"Active · {mechanism}";
    }

    /// <summary>
    /// A backend's page: its enable toggle, whatever settings it declares for itself, and what it is
    /// currently reporting.
    /// </summary>
    private static void BuildBackendPage(ScrollablePanelControl panel, List<FormControl> forms,
                                         GpuStatsFactory.KnownBackend known, IGpuBackend? live,
                                         bool enabled, CxgpuConfig current)
    {
        var muted = UIConstants.MutedText.ToMarkup();
        var text = UIConstants.PrimaryText.ToMarkup();

        // Read declared settings from the LIVE backend when it probed, otherwise from a fresh instance:
        // a disabled vendor still has to show what it could be configured to do.
        IReadOnlyList<PluginSetting> settings;
        try
        {
            // Instance-resolved, not by service name: this page must also show what a DISABLED or
            // absent vendor could be configured to do, and such a backend was never registered.
            settings = (live ?? known.Create()).GetSettingsVia();
        }
        catch
        {
            settings = Array.Empty<PluginSetting>();
        }

        var stored = current.BackendSettings.TryGetValue(known.Name, out var values)
            ? values
            : new Dictionary<string, string?>();

        AddForm(panel, forms, form =>
        {
            form.AddCheckbox($"{BackendFieldPrefix}{known.Name}:{EnabledKey}",
                $"Enable {known.Name}", enabled,
                hint: "Disabled backends are never probed (restart to apply)");

            // ONE generic loop over the descriptors: the host renders a setting without knowing what it
            // means, which is the entire point of PluginSetting. No per-plugin UI code, no reflection.
            foreach (var setting in settings)
            {
                var fieldKey = $"{BackendFieldPrefix}{known.Name}:{setting.Key}";
                var value = stored.TryGetValue(setting.Key, out var v) && v != null
                    ? v
                    : setting.Default?.ToString();

                var fieldHint = setting.RequiresRestart && setting.Hint != null
                    ? $"{setting.Hint} (restart to apply)"
                    : setting.Hint;

                switch (setting.Kind)
                {
                    case PluginSettingKind.Bool:
                        form.AddCheckbox(fieldKey, setting.Label, bool.TryParse(value, out var b) && b, fieldHint);
                        break;

                    case PluginSettingKind.Int:
                        form.AddSlider(fieldKey, setting.Label,
                            setting.Min ?? 0, setting.Max ?? 100,
                            double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0,
                            fieldHint);
                        break;

                    case PluginSettingKind.Choice:
                        form.AddDropdown(fieldKey, setting.Label,
                            setting.Options ?? Array.Empty<string>(), value, fieldHint);
                        break;

                    default:
                        form.AddText(fieldKey, setting.Label, value ?? "", hint: fieldHint);
                        break;
                }
            }

            return form;
        });

        // Live facts below the fields. Useful beyond configuration: this is what makes "why is there no
        // fan card for this GPU?" answerable without reading the source.
        if (live != null)
        {
            var liveInfo = live.InfoVia();
            panel.AddControl(Controls.Markup()
                .AddLine("")
                .AddLine($"[{muted}]Source[/]    [{text}]{liveInfo.Mechanism}[/]")
                .AddLine($"[{muted}]Version[/]   [{text}]{liveInfo.Version ?? "—"}[/]")
                .AddLine($"[{muted}]Reports[/]   [{text}]{DescribeCapabilities(live.CapabilitiesVia())}[/]")
                .WithMargin(1, 0, 1, 0)
                .Build());
        }
        else if (enabled)
        {
            panel.AddControl(Controls.Markup()
                .AddLine("")
                .AddLine($"[{muted}]No {known.Name} GPU was detected on this machine.[/]")
                .WithMargin(1, 0, 1, 0)
                .Build());
        }
    }

    /// <summary>The metrics a backend can actually report, as a short readable list.</summary>
    private static string DescribeCapabilities(GpuCapabilities caps)
    {
        var supported = new List<string>();
        if (caps.FanSpeed) supported.Add("fan");
        if (caps.PowerLimit) supported.Add("power cap");
        if (caps.ThrottleReasons) supported.Add("throttle");
        if (caps.EncoderDecoder) supported.Add("enc/dec");
        if (caps.PerProcessMemory) supported.Add("proc mem");
        if (caps.PerProcessSm) supported.Add("proc sm");
        if (caps.ProcessSignal) supported.Add("signal");

        return supported.Count > 0 ? string.Join(", ", supported) : "—";
    }

    /// <summary>Builds a form into a page and records it, so Save can collect its values later.</summary>
    private static void AddForm(ScrollablePanelControl panel, List<FormControl> forms,
                                Func<FormBuilder, FormBuilder> configure)
    {
        // No WithButtons: Save and Cancel live once at the dialog level, not on every page.
        var form = configure(Controls.Form()).Build();
        forms.Add(form);
        panel.AddControl(form);
    }

    /// <summary>
    /// Merges the values of every form built so far. A form reports only the fields it holds and the
    /// rail builds pages lazily, so a page never opened contributes nothing — which is why
    /// <see cref="ApplyValues"/> falls back to the current value for anything missing.
    /// </summary>
    private static IReadOnlyDictionary<string, string?> CollectAllValues(IEnumerable<FormControl> forms)
    {
        var merged = new Dictionary<string, string?>();
        foreach (var form in forms)
        {
            foreach (var pair in form.GetValues())
                merged[pair.Key] = pair.Value;
        }
        return merged;
    }

    /// <summary>
    /// Collects backend-owned fields into the nested config dictionary.
    ///
    /// Starts from the STORED dictionary rather than an empty one, so keys this build did not render —
    /// written by a newer version, or belonging to a page never opened — survive the round-trip.
    /// </summary>
    private static Dictionary<string, Dictionary<string, string?>> CollectBackendSettings(
        CxgpuConfig current, IReadOnlyDictionary<string, string?> values)
    {
        var result = current.BackendSettings.ToDictionary(
            kv => kv.Key,
            kv => new Dictionary<string, string?>(kv.Value));

        foreach (var pair in values)
        {
            if (!pair.Key.StartsWith(BackendFieldPrefix, StringComparison.Ordinal)) continue;

            var parts = pair.Key[BackendFieldPrefix.Length..].Split(':', 2);
            if (parts.Length != 2) continue;

            // The enable toggle is an app-level setting, not one of the backend's own.
            if (parts[1] == EnabledKey) continue;

            if (!result.TryGetValue(parts[0], out var bucket))
                result[parts[0]] = bucket = new Dictionary<string, string?>();

            bucket[parts[1]] = pair.Value;
        }

        return result;
    }

    /// <summary>
    /// Builds a new config from the collected values, keeping anything absent or unparseable at its
    /// current setting.
    /// </summary>
    private static CxgpuConfig ApplyValues(CxgpuConfig current, IReadOnlyDictionary<string, string?> values)
    {
        return new CxgpuConfig
        {
            RefreshIntervalMs = ParseInt(values, KeyRefresh, current.RefreshIntervalMs),
            SparklineHeight = ParseInt(values, KeySparkHeight, current.SparklineHeight),
            ShowTimeAxis = ParseBool(values, KeyTimeAxis, current.ShowTimeAxis),
            ShowOverviewTab = ParseBool(values, KeyOverview, current.ShowOverviewTab),
            ShowProcessesTab = ParseBool(values, KeyProcesses, current.ShowProcessesTab),
            ShowDetailsTab = ParseBool(values, KeyDetails, current.ShowDetailsTab),
            EnableNvidiaBackend = ParseBool(values, $"{BackendFieldPrefix}NVIDIA:{EnabledKey}", current.EnableNvidiaBackend),
            EnableAmdBackend = ParseBool(values, $"{BackendFieldPrefix}AMD:{EnabledKey}", current.EnableAmdBackend),
            BackendSettings = CollectBackendSettings(current, values),
            // MUST be assigned: this builds a NEW config, so anything omitted here is silently reset
            // to its default on every save — including per-card overrides the user hand-edited.
            Alerts = ApplyAlertValues(current.Alerts, values),
        }.Clamped();
    }

    /// <summary>
    /// Merges the Alerts page's fields into the existing alert config.
    ///
    /// Mutates a copy of what was there rather than building fresh: <see cref="AlertConfig.Cards"/>
    /// holds PER-CARD overrides that this dialog does not edit (they are hand-written, keyed by PCI
    /// address), and rebuilding would delete them the first time anyone pressed Save.
    /// </summary>
    private static AlertConfig ApplyAlertValues(AlertConfig current,
                                                IReadOnlyDictionary<string, string?> values)
    {
        var alerts = new AlertConfig
        {
            Enabled = ParseBool(values, KeyAlertsEnabled, current.Enabled),
            ToastOnWarning = ParseBool(values, KeyToastWarning, current.ToastOnWarning),
            ToastOnCritical = ParseBool(values, KeyToastCritical, current.ToastOnCritical),
            SessionSummaryOnExit = ParseBool(values, KeySessionSummary, current.SessionSummaryOnExit),
            // Carried across untouched — see the summary above.
            Cards = current.Cards,
            Vendors = new Dictionary<string, CardAlertConfig>(current.Vendors),
        };

        SetVendorTemp(alerts, "nvidia",
            ParseInt(values, KeyNvidiaWarn, (int)VendorTempOf(current, "nvidia", warn: true)),
            ParseInt(values, KeyNvidiaCritical, (int)VendorTempOf(current, "nvidia", warn: false)));

        SetVendorTemp(alerts, "amd",
            ParseInt(values, KeyAmdWarn, (int)VendorTempOf(current, "amd", warn: true)),
            ParseInt(values, KeyAmdCritical, (int)VendorTempOf(current, "amd", warn: false)));

        return alerts;
    }

    private static void SetVendorTemp(AlertConfig alerts, string vendor, double warn, double critical)
    {
        // A critical below its warning would make the warning unreachable — clamp rather than accept
        // a pair that can never fire.
        if (critical < warn) critical = warn;

        if (!alerts.Vendors.TryGetValue(vendor, out var entry) || entry == null)
            alerts.Vendors[vendor] = entry = new CardAlertConfig();

        entry.TemperatureC = new ThresholdConfig { Warn = warn, Critical = critical };
    }

    /// <summary>The configured vendor temperature, or the built-in default when unset.</summary>
    private static double VendorTempOf(AlertConfig alerts, string vendor, bool warn)
    {
        var configured = alerts.Vendors.TryGetValue(vendor, out var v) ? v?.TemperatureC : null;
        var value = warn ? configured?.Warn : configured?.Critical;
        if (value is { } set) return set;

        var defaults = vendor.Equals("amd", StringComparison.OrdinalIgnoreCase)
            ? AlertThresholds.AmdDiscrete
            : AlertThresholds.NvidiaConsumer;

        return warn ? defaults.TemperatureC!.Warn : defaults.TemperatureC!.Critical;
    }

    private static bool ParseBool(IReadOnlyDictionary<string, string?> values, string key, bool fallback)
        => values.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;

    private static int ParseInt(IReadOnlyDictionary<string, string?> values, string key, int fallback)
    {
        if (values.TryGetValue(key, out var v) && v != null &&
            double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
        {
            return (int)Math.Round(d);
        }
        return fallback;
    }
}
