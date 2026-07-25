using System.Globalization;
using cxnvmon.Configuration;
using cxnvmon.Helpers;
using cxnvmon.Tabs;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace cxnvmon.Dashboard;

/// <summary>
/// A small modal settings dialog for editing <see cref="CxnvmonConfig"/>. Built with the
/// framework's <see cref="Controls.Form"/> inside a modal <see cref="WindowBuilder"/> — the same
/// pattern the DemoApp form dialogs use. On submit it saves to disk and invokes
/// <paramref name="onApply"/> so the running app can pick up the change live.
/// </summary>
internal static class SettingsDialog
{
    private const int DialogWidth = 56;
    private const int DialogHeight = 20;

    // Form field keys.
    private const string KeyRefresh = "refresh";
    private const string KeySparkHeight = "sparkheight";
    private const string KeyTimeAxis = "timeaxis";
    private const string KeyOverview = "overview";
    private const string KeyProcesses = "processes";
    private const string KeyDetails = "details";

    /// <summary>
    /// Opens the modal settings dialog seeded from <paramref name="current"/>. When the user
    /// submits, the edited config is saved to disk and passed to <paramref name="onApply"/>.
    /// </summary>
    public static void Show(ConsoleWindowSystem windowSystem, CxnvmonConfig current, Action<CxnvmonConfig> onApply)
    {
        var header = Controls.Markup($"[{UIConstants.Accent.ToMarkup()} bold]cxnvmon settings[/]")
            .WithMargin(1, 0, 1, 0)
            .Build();

        var hint = Controls.Markup($"[{UIConstants.MutedText.ToMarkup()}]Tab between fields · Save applies & writes to disk · Esc cancels[/]")
            .WithMargin(1, 0, 1, 0)
            .Build();

        var form = Controls.Form()
            .AddSection("Refresh")
            .AddSlider(KeyRefresh, "Interval (ms)",
                CxnvmonConfig.MinRefreshIntervalMs, CxnvmonConfig.MaxRefreshIntervalMs,
                current.RefreshIntervalMs,
                hint: $"{CxnvmonConfig.MinRefreshIntervalMs}–{CxnvmonConfig.MaxRefreshIntervalMs} ms between updates")
            .AddSection("Graphs")
            .AddSlider(KeySparkHeight, "Sparkline height",
                CxnvmonConfig.MinSparklineHeight, CxnvmonConfig.MaxSparklineHeight,
                current.SparklineHeight,
                hint: $"{CxnvmonConfig.MinSparklineHeight}–{CxnvmonConfig.MaxSparklineHeight} rows (restart to apply)")
            .AddCheckbox(KeyTimeAxis, "Show time axis on graphs", current.ShowTimeAxis)
            .AddSection("Tabs")
            .AddCheckbox(KeyOverview, "Show Overview tab", current.ShowOverviewTab)
            .AddCheckbox(KeyProcesses, "Show Processes tab", current.ShowProcessesTab)
            .WithButtons(ok: "Save")
            .Build();

        var panel = BaseResponsiveTab.BuildScrollablePanel();
        panel.AddControl(header);
        panel.AddControl(Controls.RuleBuilder().WithMargin(1, 0, 1, 0).Build());
        panel.AddControl(form);

        Window? dialogRef = null;
        var dialog = new WindowBuilder(windowSystem)
            .WithTitle("Settings")
            .WithSize(DialogWidth, DialogHeight)
            .Centered()
            .AsModal()
            .AddControls(panel, hint)
            // Esc must close the dialog AND consume the key. The form's own Cancelled event does not
            // fire for it here, and the main window binds Esc to Shutdown — so without this, pressing
            // Esc in Settings either did nothing or would quit the app. Same fix as the process
            // signal/confirm dialogs and the help overlay.
            .OnKeyPressed((_, e) =>
            {
                if (e.KeyInfo.Key != ConsoleKey.Escape) return;
                if (dialogRef != null) windowSystem.CloseWindow(dialogRef);
                e.Handled = true;
            })
            .BuildAndShow();
        dialogRef = dialog;

        form.Cancelled += (_, _) => windowSystem.CloseWindow(dialog);
        form.Submitted += (_, values) =>
        {
            var updated = ApplyValues(current, values);
            updated.Save();
            windowSystem.CloseWindow(dialog);
            onApply(updated);
        };
    }

    /// <summary>
    /// Builds a new config from the form's collected string values, keeping any value that fails
    /// to parse at its current setting. Checkboxes serialize as "true"/"false"; the slider as a
    /// numeric string.
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
