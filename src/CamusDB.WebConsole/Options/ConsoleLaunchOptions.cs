namespace CamusDB.WebConsole.Options;

/// <summary>
/// Configures the vendor launch surface: a back-channel endpoint an embedding vendor calls to
/// rename the console and hand it a CamusDB access token, plus the single-use redirect that opens
/// the branded console in the visitor's browser.
///
/// <para>The whole surface is <b>off</b> unless <see cref="Enabled"/> is set and an
/// <see cref="ApiKey"/> is configured. Anything that can call it can plant an access token into a
/// visitor's session, so it is opt-in rather than something a stock install exposes.</para>
/// </summary>
public sealed class ConsoleLaunchOptions
{
    public const string SectionName = "ConsoleLaunch";

    /// <summary>Master switch. While false both endpoints answer 404 and nothing is registered.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Shared secret the vendor sends as <c>X-Console-Key</c>, compared in constant time. Supply it
    /// from the environment (<c>ConsoleLaunch__ApiKey</c>) or a secret store, never appsettings.json.
    /// Startup fails when <see cref="Enabled"/> is set and this is missing or shorter than
    /// <see cref="MinApiKeyLength"/>.
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Refuse to mint launch codes over plaintext HTTP. The API key travels in a request header, so
    /// plaintext hands it to anyone on the path. Set false only when TLS terminates in front of the
    /// console <em>and</em> forwarded headers are not wired up.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// How long a launch code stays redeemable. Short by design: it exists only to survive one
    /// redirect from the vendor's site to this one.
    /// </summary>
    public int CodeLifetimeSeconds { get; set; } = 60;

    /// <summary>How long the resulting browser session stays valid before the visitor must be re-launched.</summary>
    public int SessionLifetimeMinutes { get; set; } = 60;

    /// <summary>
    /// Name shown when no vendor launch is in play. Validated by the same rules as a vendor-supplied
    /// name; an invalid value here fails startup rather than silently reverting.
    /// </summary>
    public string DefaultBrandName { get; set; } = "CamusDB Web Console";

    /// <summary>
    /// Ceiling on live launch codes plus sessions. Reached, the endpoint fails closed with 503 rather
    /// than letting an unbounded store grow — the entries hold access tokens.
    /// </summary>
    public int MaxLiveEntries { get; set; } = 2_000;

    /// <summary>
    /// When non-empty, an endpoint change may only name a CamusDB endpoint matching one of these.
    /// Leave it empty to accept any absolute http/https URL.
    ///
    /// <para>It governs <b>both</b> paths that can repoint the console: a vendor launch payload, and
    /// the Configure dialog. The dialog is the wider of the two — a launch needs the vendor key,
    /// while the dialog is open to whoever can load the page. The key still lives in this section
    /// because that is where it was introduced and where deployments already set it.</para>
    ///
    /// <para>The endpoint in <c>CamusDB:Endpoint</c> always passes, whatever this list holds. An
    /// operator who wrote it into configuration has already decided it, and refusing it here would
    /// turn one missing entry into a console that cannot reach its own server.</para>
    ///
    /// <para><b>Leaving it empty is a server-side request forgery surface</b>: the console's own
    /// process opens the URL, so a caller can reach hosts the visitor cannot — link-local metadata
    /// services, internal admin ports. Set this, or set <c>CamusDB:LockEndpoint</c>, on any console
    /// reachable beyond localhost.</para>
    ///
    /// <para>Entries are full origins (<c>https://db.acme.example</c>) or bare hosts with an
    /// optional port (<c>db.acme.example</c>, <c>db.acme.example:5095</c>). Several may share one
    /// element, separated by commas, semicolons or whitespace, so the whole list fits in a single
    /// environment variable:</para>
    /// <code>
    /// ConsoleLaunch__AllowedEndpoints=https://db.acme.example,https://replica.acme.example
    /// </code>
    /// <para>An entry that parses as neither form fails startup rather than being skipped — see
    /// <see cref="Services.EndpointAllowList"/>.</para>
    /// </summary>
    public string[] AllowedEndpoints { get; set; } = [];

    /// <summary>
    /// Absolute base URL to build the returned launch link from (e.g. <c>https://console.example.com</c>).
    /// Empty derives it from the request, which trusts the Host header — fine when
    /// <c>AllowedHosts</c> is pinned, worth setting explicitly otherwise.
    /// </summary>
    public string PublicBaseUrl { get; set; } = "";

    public const int MinApiKeyLength = 32;
}
