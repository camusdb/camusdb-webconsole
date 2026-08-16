using System.Globalization;
using System.Text;

namespace CamusDB.WebConsole.Services;

/// <summary>
/// The one gate a vendor-supplied console name passes through.
///
/// <para>Blazor HTML-encodes interpolated text, so the name cannot break out of the markup it is
/// rendered into today. This exists because that is a property of every current call site rather
/// than of the value: one <c>MarkupString</c>, one <c>style="…"</c> interpolation, or one
/// <c>document.title = …</c> written later would turn a stored name into stored XSS. Validating at
/// ingest — and again in <see cref="ConsoleBranding.Apply"/> — means the value cannot carry a
/// payload for such a bug to find.</para>
///
/// <para>It is an <b>allowlist</b>, not a denylist: a name is letters, digits, spaces and a short
/// list of punctuation, and everything else is refused with a message naming the character. That
/// rules out <c>&lt; &gt; " \ / { } ; :</c> and backtick — the characters that matter in HTML, CSS
/// and JS contexts — without having to enumerate every escape that might reach them.</para>
///
/// <para>Also refused: control characters, and the Unicode <c>Cf</c> (format) category, which is
/// where the zero-width joiners and the bidi overrides live. Those carry no payload, but they let a
/// name render as something other than what it says, which is its own kind of injection when the
/// name is the thing telling a user whose console they are typing into.</para>
/// </summary>
public static class BrandNameSanitizer
{
    public const int MaxLength = 64;

    /// <summary>
    /// Punctuation a real company name needs. <c>&amp;</c> and <c>'</c> are here deliberately —
    /// "Smith &amp; Co", "Acme's Console" — and are safe precisely because every render path
    /// HTML-encodes them; they are the reason this list is reviewed rather than extended casually.
    /// </summary>
    private const string AllowedPunctuation = "-_.,&'()+";

    /// <summary>
    /// Normalises and validates <paramref name="raw"/>. On success <paramref name="name"/> is the
    /// trimmed, NFC-normalised, whitespace-collapsed name to store and render.
    /// </summary>
    public static bool TryNormalize(string? raw, out string name, out string? error)
    {
        name = "";
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "A console name is required.";
            return false;
        }

        // Ahead of the length check: an over-long name should be reported against what will actually
        // be stored, not against the caller's leading whitespace.
        string normalized;
        try
        {
            normalized = raw.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // Unpaired surrogates make normalisation throw. Nothing legitimate arrives that way.
            error = "The console name is not valid Unicode text.";
            return false;
        }

        normalized = CollapseWhitespace(normalized);

        if (normalized.Length == 0)
        {
            error = "A console name is required.";
            return false;
        }

        if (normalized.Length > MaxLength)
        {
            error = $"The console name is {normalized.Length} characters; the limit is {MaxLength}.";
            return false;
        }

        bool hasAlphanumeric = false;

        foreach (char c in normalized)
        {
            if (char.IsLetter(c) || char.IsDigit(c))
            {
                hasAlphanumeric = true;
                continue;
            }

            if (c == ' ' || AllowedPunctuation.Contains(c, StringComparison.Ordinal))
                continue;

            error = $"The console name may not contain {Describe(c)}. Allowed: letters, digits, "
                + $"spaces and {AllowedPunctuation}.";
            return false;
        }

        if (!hasAlphanumeric)
        {
            error = "The console name must contain at least one letter or digit.";
            return false;
        }

        name = normalized;
        return true;
    }

    /// <summary>
    /// Validates a name that is expected to be good already — the configured default at startup, or
    /// a stored name on its way to the renderer. Throws rather than substituting a fallback: a name
    /// that fails here means something set it without going through <see cref="TryNormalize"/>, and
    /// silently rendering a different name would hide that.
    /// </summary>
    public static string Require(string? raw, string parameterName)
    {
        if (TryNormalize(raw, out string name, out string? error))
            return name;

        throw new ArgumentException(error, parameterName);
    }

    /// <summary>
    /// Folds every run of whitespace — including the tabs, newlines and non-breaking spaces that a
    /// name has no business containing — into a single ordinary space, then trims.
    /// </summary>
    private static string CollapseWhitespace(string value)
    {
        StringBuilder sb = new(value.Length);
        bool pendingSpace = false;

        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Names the offending character without echoing it — the error travels back to the vendor in a
    /// JSON body, and reflecting arbitrary input into a response is the habit this whole type exists
    /// to break.
    /// </summary>
    private static string Describe(char c)
    {
        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);

        return category switch
        {
            UnicodeCategory.Control => $"a control character (U+{(int)c:X4})",
            UnicodeCategory.Format => $"a formatting character (U+{(int)c:X4})",
            UnicodeCategory.Surrogate => "characters outside the Basic Multilingual Plane (such as emoji)",
            UnicodeCategory.PrivateUse => $"a private-use character (U+{(int)c:X4})",
            _ => $"the character U+{(int)c:X4}",
        };
    }
}
