using CamusDB.WebConsole.Options;
using Microsoft.Extensions.Options;

namespace CamusDB.WebConsole.Services;

/// <summary>
/// The name this console answers to for one render scope. Defaults to the configured name and is
/// replaced by a vendor's when a launch session is in play.
///
/// <para>Scoped, so a name never leaks between visitors: the static render and the interactive
/// circuit each get their own instance and are each told the name separately.</para>
/// </summary>
public sealed class ConsoleBranding
{
    public ConsoleBranding(IOptions<ConsoleLaunchOptions> options)
    {
        // Validated at startup too; re-checked here so a bad value can never reach a razor file even
        // if this type is constructed outside the host's validation path (a test, a future tool).
        Name = BrandNameSanitizer.Require(
            options.Value.DefaultBrandName,
            nameof(ConsoleLaunchOptions.DefaultBrandName));
    }

    public string Name { get; private set; }

    /// <summary>True once a vendor launch has renamed this console.</summary>
    public bool IsVendorBranded { get; private set; }

    /// <summary>
    /// Re-validates on the way in. The value has already passed the sanitizer at ingest, so this is
    /// redundant by construction — which is the point: it means no future path can set a name that
    /// skipped it, and the check is a string scan on a value under 64 characters.
    /// </summary>
    public void Apply(string? brandName)
    {
        if (!BrandNameSanitizer.TryNormalize(brandName, out string normalized, out _))
            return;

        Name = normalized;
        IsVendorBranded = true;
    }

    /// <summary>
    /// Title for a page within the console — "Administration · Acme Data". Both halves are plain
    /// text and Blazor encodes them; <c>&lt;PageTitle&gt;</c> is not an escape hatch.
    /// </summary>
    public string PageTitle(string? section = null) =>
        string.IsNullOrEmpty(section) ? Name : $"{section} · {Name}";
}
