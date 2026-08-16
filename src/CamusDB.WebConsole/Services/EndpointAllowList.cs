using System.Globalization;

namespace CamusDB.WebConsole.Services;

/// <summary>
/// The hosts a vendor launch payload may point the console at.
///
/// <para>This is the control that closes the request-forgery surface described on
/// <see cref="Options.ConsoleLaunchOptions.AllowedEndpoints"/>: the console's own process opens the
/// endpoint a payload names, so without a list it can be made to reach cloud metadata services and
/// internal admin ports the visitor could never reach directly.</para>
///
/// <para>Entries are written as a full origin (<c>https://db.acme.example</c>), or as a bare host
/// with an optional port (<c>db.acme.example</c>, <c>db.acme.example:5095</c>). The two are told
/// apart by the presence of <c>://</c> rather than by trying to parse and seeing what happens —
/// URI schemes may contain dots, so <c>db.acme.example:5095</c> is a genuinely ambiguous string that
/// .NET will happily read as scheme <c>db.acme.example</c>.</para>
///
/// <para>Matching is on scheme, host and port as parsed values, never on the raw string: a path
/// suffix or a trailing slash must not be able to make one origin look like another. There is no
/// wildcard form — <c>*.acme.example</c> is not supported, deliberately, because a subdomain
/// wildcard is exactly how one of these lists stops being a list.</para>
/// </summary>
public sealed class EndpointAllowList
{
    /// <param name="Scheme">Null when the entry named a bare host, meaning http and https alike.</param>
    /// <param name="Port">Null when the entry named no port, meaning any port.</param>
    private readonly record struct Rule(string? Scheme, string Host, int? Port);

    private static readonly char[] Separators = [',', ';', ' ', '\t', '\r', '\n'];

    private readonly List<Rule> _rules;

    private EndpointAllowList(List<Rule> rules) => _rules = rules;

    /// <summary>True when no entries were configured, so every http(s) endpoint is accepted.</summary>
    public bool IsEmpty => _rules.Count == 0;

    public int Count => _rules.Count;

    /// <summary>
    /// Parses configured entries. Each element may itself hold several entries separated by commas,
    /// semicolons or whitespace, which is what makes the list expressible as one environment
    /// variable — <c>ConsoleLaunch__AllowedEndpoints=https://a.example,https://b.example</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// An entry is neither an absolute http(s) URL nor a host[:port]. This throws rather than
    /// skipping the entry: a silently-dropped entry leaves an allowlist that looks configured and
    /// is not, which is the failure mode this whole type exists to prevent.
    /// </exception>
    public static EndpointAllowList Parse(IEnumerable<string>? entries)
    {
        List<Rule> rules = [];

        foreach (string raw in entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            foreach (string token in raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
                rules.Add(ParseEntry(token.Trim()));
        }

        return new EndpointAllowList(rules);
    }

    private static Rule ParseEntry(string entry)
    {
        if (entry.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(entry, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException(
                    $"'{entry}' is not an absolute http:// or https:// URL.", nameof(entry));
            }

            // Uri.Port supplies the scheme default when none was written, so an entry naming no port
            // pins the default port rather than accepting any — which is what writing an origin means.
            return new Rule(uri.Scheme, uri.Host, uri.Port);
        }

        string host = entry;
        int? port = null;

        int colon = entry.LastIndexOf(':');
        if (colon > 0)
        {
            string portText = entry[(colon + 1)..];

            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                || parsed is < 1 or > 65535)
            {
                throw new ArgumentException(
                    $"'{entry}' has an invalid port. Write a URL, a host, or host:port.", nameof(entry));
            }

            host = entry[..colon];
            port = parsed;
        }

        if (host.Length == 0 || host.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"'{entry}' is neither an absolute http(s) URL nor a host[:port].", nameof(entry));
        }

        // Round-tripping through Uri normalises the host the same way the candidate's will be
        // (case, IDN, bracketed IPv6), so the comparison below is between like and like.
        if (!Uri.TryCreate($"http://{host}", UriKind.Absolute, out Uri? probe) || probe.Host.Length == 0)
        {
            throw new ArgumentException(
                $"'{entry}' is not a usable host name.", nameof(entry));
        }

        return new Rule(null, probe.Host, port);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is permitted. An empty list permits everything — the
    /// caller warns about that at startup; it is not this type's decision to make.
    /// </summary>
    public bool IsAllowed(Uri candidate)
    {
        if (_rules.Count == 0)
            return true;

        foreach (Rule rule in _rules)
        {
            if (rule.Scheme is not null
                && !string.Equals(rule.Scheme, candidate.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(rule.Host, candidate.Host, StringComparison.OrdinalIgnoreCase))
                continue;

            if (rule.Port is not null && rule.Port != candidate.Port)
                continue;

            return true;
        }

        return false;
    }

    /// <summary>Human-readable form for startup logging, so an operator can see what was understood.</summary>
    public override string ToString() =>
        _rules.Count == 0
            ? "(empty — any http(s) endpoint accepted)"
            : string.Join(", ", _rules.Select(r =>
                $"{r.Scheme ?? "http(s)"}://{r.Host}{(r.Port is null ? ":*" : $":{r.Port}")}"));
}
