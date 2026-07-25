using System.Globalization;
using cxnvmon.Configuration;
using cxnvmon.Helpers;
using cxnvmon.Stats;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace cxnvmon.Dashboard;

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

    // App-owned field keys.
    private const string KeyRefresh = "refresh";
    private const string KeySparkHeight = "sparkheight";
    private const string KeyTimeAxis = "timeaxis";
    private const string KeyOverview = "overview";
    private const string KeyProcesses = "processes";
    private const string KeyDetails = "details";

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
    public static void Show(ConsoleWindowSystem windowSystem, CxnvmonConfig current,
                            Action<CxnvmonConfig> onApply,
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
            .WithPaneHeader($"[{accent} bold]cxnvmon[/]")
            .WithContentBorder(BorderStyle.Rounded)
            .WithContentBorderColor(UIConstants.SeparatorColor)
            .WithContentPadding(1, 0, 1, 0)
            .WithSelectedColors(UIConstants.Accent, UIConstants.TileSelectedBg)
            .AddHeader("General", UIConstants.Accent, header => header
                .AddItem("Refresh", subtitle: "Update interval",
                    content: panel => AddForm(panel, forms, f => f
                        .AddSlider(KeyRefresh, "Interval (ms)",
                            CxnvmonConfig.MinRefreshIntervalMs, CxnvmonConfig.MaxRefreshIntervalMs,
                            current.RefreshIntervalMs,
                            hint: $"{CxnvmonConfig.MinRefreshIntervalMs}–{CxnvmonConfig.MaxRefreshIntervalMs} ms between updates")))
                .AddItem("Graphs", subtitle: "Sparklines",
                    content: panel => AddForm(panel, forms, f => f
                        .AddSlider(KeySparkHeight, "Sparkline height",
                            CxnvmonConfig.MinSparklineHeight, CxnvmonConfig.MaxSparklineHeight,
                            current.SparklineHeight,
                            hint: $"{CxnvmonConfig.MinSparklineHeight}–{CxnvmonConfig.MaxSparklineHeight} rows (restart to apply)")
                        .AddCheckbox(KeyTimeAxis, "Show time axis", current.ShowTimeAxis)))
                .AddItem("Tabs", subtitle: "Visible views",
                    content: panel => AddForm(panel, forms, f => f
                        .AddCheckbox(KeyOverview, "Overview", current.ShowOverviewTab)
                        .AddCheckbox(KeyProcesses, "Processes", current.ShowProcessesTab))))
            .AddHeader("Backends", UIConstants.Accent, header =>
            {
                // Every KNOWN vendor gets a page, not only those that probed — otherwise a disabled or
                // absent backend could never be switched on.
                foreach (var known in GpuStatsFactory.KnownBackends)
                {
                    var live = activeBackends?.FirstOrDefault(b => b.BackendInfo.Name == known.Name);
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

        var mechanism = live.BackendInfo.Mechanism;
        return string.IsNullOrWhiteSpace(mechanism) ? "Active" : $"Active · {mechanism}";
    }

    /// <summary>
    /// A backend's page: its enable toggle, whatever settings it declares for itself, and what it is
    /// currently reporting.
    /// </summary>
    private static void BuildBackendPage(ScrollablePanelControl panel, List<FormControl> forms,
                                         GpuStatsFactory.KnownBackend known, IGpuBackend? live,
                                         bool enabled, CxnvmonConfig current)
    {
        var muted = UIConstants.MutedText.ToMarkup();
        var text = UIConstants.PrimaryText.ToMarkup();

        // Read declared settings from the LIVE backend when it probed, otherwise from a fresh instance:
        // a disabled vendor still has to show what it could be configured to do.
        IReadOnlyList<PluginSetting> settings;
        try
        {
            settings = (live ?? known.Create()).GetSettings();
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
            panel.AddControl(Controls.Markup()
                .AddLine("")
                .AddLine($"[{muted}]Source[/]    [{text}]{live.BackendInfo.Mechanism}[/]")
                .AddLine($"[{muted}]Version[/]   [{text}]{live.BackendInfo.Version ?? "—"}[/]")
                .AddLine($"[{muted}]Reports[/]   [{text}]{DescribeCapabilities(live.Capabilities)}[/]")
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
        CxnvmonConfig current, IReadOnlyDictionary<string, string?> values)
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
    private static CxnvmonConfig ApplyValues(CxnvmonConfig current, IReadOnlyDictionary<string, string?> values)
    {
        return new CxnvmonConfig
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
        }.Clamped();
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
