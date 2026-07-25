using cxnvmon.Helpers;
using cxnvmon.Tabs;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Layout;

namespace cxnvmon.Dashboard;

/// <summary>
/// The keyboard-shortcut overlay (<c>?</c> / <c>F1</c>). A modal built with the same
/// <see cref="WindowBuilder"/> pattern as <see cref="SettingsDialog"/>.
///
/// This is the app's discoverability surface, so it must describe what the code ACTUALLY binds —
/// a help screen that lists a key which does nothing is worse than no help screen. Keys that are
/// conditional (the GPU selectors only do anything with more than one GPU) say so rather than
/// silently misleading.
/// </summary>
internal static class HelpDialog
{
    private const int DialogWidth = 64;
    private const int DialogHeight = 26;

    private const int KeyColumnWidth = 14;

    public static void Show(ConsoleWindowSystem windowSystem, bool multiGpu)
    {
        var accent = UIConstants.Accent.ToMarkup();
        var muted = UIConstants.MutedText.ToMarkup();
        var text = UIConstants.PrimaryText.ToMarkup();

        var lines = new List<string>();

        void Section(string title)
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add($"[{accent} bold]{title}[/]");
        }

        void Key(string keys, string description, bool enabled = true)
        {
            // Disabled entries (e.g. GPU selection on a single-GPU host) are shown muted rather than
            // hidden: knowing a key exists but doesn't apply here is useful information.
            var keyColor = enabled ? accent : muted;
            var descColor = enabled ? text : muted;
            lines.Add($"  [{keyColor}]{keys.PadRight(KeyColumnWidth)}[/][{descColor}]{description}[/]");
        }

        Section("VIEWS");
        Key("F2", "Overview tab");
        Key("F3", "Processes tab");

        Section("GPU SELECTION");
        if (multiGpu)
        {
            Key("[  ]", "Previous / next GPU");
            Key("1 - 9", "Select GPU by number");
            Key("click", "Click a tile in the summary strip");
        }
        else
        {
            Key("[  ]", "Previous / next GPU  (single GPU — n/a)", enabled: false);
            Key("1 - 9", "Select GPU by number  (single GPU — n/a)", enabled: false);
        }

        Section("PROCESSES TAB");
        Key("↑ ↓", "Move between processes");
        Key("→ / Enter", "Expand a process (full path + live detail)");
        Key("← ", "Collapse");
        Key("k", "Signal the selected process");

        Section("APPLICATION");
        Key("? / F1", "This help");
        Key("F9", "Settings");
        Key("F10 / Esc", "Quit");

        lines.Add("");
        lines.Add($"[{muted}]Percentages shown as[/] [{text}]-[/] [{muted}]mean nvidia-smi reported no[/]");
        lines.Add($"[{muted}]data for that engine — not zero.[/]");

        var panel = BaseResponsiveTab.BuildScrollablePanel();
        panel.AddControl(Controls.Markup($"[{accent} bold]Keyboard shortcuts[/]")
            .WithMargin(1, 0, 1, 0)
            .Build());
        panel.AddControl(Controls.RuleBuilder().WithMargin(1, 0, 1, 0).Build());

        var body = Controls.Markup();
        foreach (var line in lines)
            body.AddLine(line);
        panel.AddControl(body.WithMargin(1, 0, 1, 0).Build());

        var hint = Controls.Markup($"[{muted}]Esc or F10 closes this help[/]")
            .WithMargin(1, 0, 1, 0)
            .Build();

        Window? dialog = null;

        dialog = new WindowBuilder(windowSystem)
            .WithTitle("Help")
            .WithSize(DialogWidth, DialogHeight)
            .Centered()
            .AsModal()
            .WithBorderColor(UIConstants.Accent)
            .AddControls(panel, hint)
            // Any of Esc / F10 / F1 / '?' dismisses. This handler MUST consume the key: the main
            // window also binds Esc and F10 to Shutdown, so an unhandled Esc here would quit the app
            // instead of just closing help.
            .OnKeyPressed((sender, e) =>
            {
                bool dismiss = e.KeyInfo.Key is ConsoleKey.Escape or ConsoleKey.F10 or ConsoleKey.F1
                               || e.KeyInfo.KeyChar == '?';
                if (!dismiss) return;

                if (dialog != null) windowSystem.CloseWindow(dialog);
                e.Handled = true;
            })
            .BuildAndShow();
    }
}
