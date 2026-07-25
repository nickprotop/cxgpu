namespace cxgpu.Stats;

/// <summary>The editor a setting needs, which decides the form control the host renders.</summary>
internal enum PluginSettingKind
{
    Bool,
    Int,
    Choice,
    Text
}

/// <summary>
/// A setting a backend declares about itself, for the host to render and persist WITHOUT knowing what
/// it means.
///
/// This exists because some settings are meaningful only to one backend. "Read AMD through sysfs or
/// the rocm-smi CLI?" is a real question on a Linux box where both work, but cxgpu knows nothing
/// about sysfs and no other backend has an opinion — so the knowledge belongs to the plugin, and the
/// host should present it generically.
///
/// Deliberately a sibling of the framework's <c>ServiceParameter</c> rather than a reuse of it:
/// that type describes OPERATION ARGUMENTS and so carries no min/max for a slider, no option list for
/// a dropdown, and no notion of persistence. Stretching it would have muddied both.
/// </summary>
/// <param name="Key">
/// Stable identifier, persisted in config. Must not change between releases, or stored values are
/// silently orphaned.
/// </param>
/// <param name="Label">Field label shown in the settings dialog.</param>
/// <param name="Kind">Which editor to render.</param>
/// <param name="Default">Value used when nothing is stored.</param>
/// <param name="Hint">The "why", shown beneath the field. Worth filling in — it is the only
/// explanation the user gets for a setting the host itself does not understand.</param>
/// <param name="Min">Lower bound, <see cref="PluginSettingKind.Int"/> only.</param>
/// <param name="Max">Upper bound, <see cref="PluginSettingKind.Int"/> only.</param>
/// <param name="Options">Allowed values, <see cref="PluginSettingKind.Choice"/> only.</param>
/// <param name="RequiresRestart">
/// True when the change cannot take effect live. Backends are probed once at startup, so anything
/// affecting probing falls in here; the dialog says so rather than appearing to apply and not.
/// </param>
internal sealed record PluginSetting(
    string Key,
    string Label,
    PluginSettingKind Kind,
    object? Default = null,
    string? Hint = null,
    double? Min = null,
    double? Max = null,
    IReadOnlyList<string>? Options = null,
    bool RequiresRestart = false);
