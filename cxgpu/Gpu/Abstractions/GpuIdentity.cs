namespace cxgpu.Gpu;

/// <summary>
/// Normalization for stable per-card identifiers.
///
/// The PCI address is the config key for per-card settings, so the SAME card must produce byte-identical
/// text from every source that reports it. The vendors do not agree on formatting: nvidia-smi pads the
/// domain to eight digits ("00000000:01:00.0") while the Linux sysfs symlink uses four
/// ("0000:c6:00.0"), and case varies. Without normalization a config entry written from one path would
/// not match the same card read from another.
/// </summary>
internal static class GpuIdentity
{
    /// <summary>
    /// Canonical form: lowercase <c>DDDD:BB:DD.F</c> (four-digit domain), matching what lspci and the
    /// Linux sysfs tree use — so a value written into config can be checked against lspci by hand.
    /// Returns "" for anything unrecognisable, which callers must treat as "no identity" rather than
    /// as a key.
    /// </summary>
    public static string NormalizePciAddress(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var text = raw.Trim().ToLowerInvariant();

        // Split off the function suffix (".0") before touching the colon-separated fields, so a
        // malformed value cannot be silently reassembled into something that looks valid.
        int dot = text.LastIndexOf('.');
        if (dot <= 0 || dot == text.Length - 1) return "";

        var function = text[(dot + 1)..];
        var fields = text[..dot].Split(':');

        // Accept both "domain:bus:device" and the bare "bus:device" some tools emit, defaulting the
        // domain to 0000 — every consumer machine is domain 0, and dropping the field entirely would
        // produce a key that collides with a real domain-0 address anyway.
        string domain, bus, device;
        switch (fields.Length)
        {
            case 3: domain = fields[0]; bus = fields[1]; device = fields[2]; break;
            case 2: domain = "0000"; bus = fields[0]; device = fields[1]; break;
            default: return "";
        }

        if (!IsHex(domain) || !IsHex(bus) || !IsHex(device) || !IsHex(function)) return "";

        // Domain is truncated, not padded, from nvidia-smi's eight digits: the leading zeros carry no
        // information, and the four-digit form is what sysfs and lspci show.
        if (domain.Length > 4) domain = domain[^4..];

        return $"{domain.PadLeft(4, '0')}:{bus.PadLeft(2, '0')}:{device.PadLeft(2, '0')}.{function}";
    }

    private static bool IsHex(string s) =>
        s.Length > 0 && s.All(Uri.IsHexDigit);
}
