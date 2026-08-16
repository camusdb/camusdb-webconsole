using System.Security.Cryptography;
using System.Text;
using CamusDB.WebConsole.Models;
using CamusDB.WebConsole.Options;
using CamusDB.WebConsole.Services;
using Microsoft.Extensions.Options;

namespace CamusDB.WebConsole.Endpoints;

/// <summary>
/// The two-leg vendor handoff.
///
/// <para><b>Leg 1</b> — the vendor's <em>backend</em> POSTs the brand and the access token to
/// <c>/api/console/sessions</c> with its shared key, and gets back a link holding a single-use code.
/// <b>Leg 2</b> — the vendor redirects the visitor's browser to that link; the console spends the
/// code, sets an HttpOnly cookie, and 302s to the console.</para>
///
/// <para>Splitting it in two is what keeps the token off the visitor's machine. A single-leg design —
/// the vendor's page POSTing the token straight from the browser — would put the token in markup the
/// visitor can read. Here the token only ever travels vendor-backend → console-backend, and the
/// browser holds an opaque id that means nothing anywhere else.</para>
/// </summary>
public static class ConsoleLaunchEndpoints
{
    /// <summary>
    /// Name is prefixed <c>__Host-</c> so the browser enforces what the attributes ask for: HTTPS
    /// only, no Domain (so no subdomain can set or read it), and Path=/. A browser that honours the
    /// prefix will reject the cookie outright if any of those is violated, which is a stronger
    /// guarantee than trusting this code to set them right forever.
    /// </summary>
    public const string SessionCookieName = "__Host-cwc-launch";

    /// <summary>Fallback name for plaintext development, where <c>__Host-</c> cookies are rejected.</summary>
    public const string InsecureSessionCookieName = "cwc-launch";

    private const string ApiKeyHeader = "X-Console-Key";

    public static string CookieName(bool secure) => secure ? SessionCookieName : InsecureSessionCookieName;

    public static void MapConsoleLaunch(this WebApplication app)
    {
        ConsoleLaunchOptions options = app.Services.GetRequiredService<IOptions<ConsoleLaunchOptions>>().Value;

        // Nothing is routed when the feature is off, so both paths answer exactly as any other
        // unmatched URL does (404 on the GET, 400 from the antiforgery middleware on the POST). A
        // probe cannot tell a disabled console from one that never had the feature.
        if (!options.Enabled)
            return;

        app.MapPost("/api/console/sessions", CreateSessionAsync)
            .DisableAntiforgery();   // server-to-server with a bearer-style key; no browser, no cookie, no CSRF

        app.MapGet("/console/launch", Launch);
    }

    private static async Task<IResult> CreateSessionAsync(
        HttpContext context,
        ConsoleLaunchRequest? request,
        IOptions<ConsoleLaunchOptions> optionsAccessor,
        IOptions<CamusDbOptions> camusAccessor,
        ConsoleLaunchStore store,
        EndpointAllowList allowList,
        ILoggerFactory loggerFactory)
    {
        ConsoleLaunchOptions options = optionsAccessor.Value;
        CamusDbOptions camus = camusAccessor.Value;
        ILogger logger = loggerFactory.CreateLogger("ConsoleLaunch");

        if (options.RequireHttps && !context.Request.IsHttps)
        {
            // The key is in a header on this very request, so answering at all over plaintext would
            // be answering a request that already leaked it.
            return Results.Json(
                new { error = "This endpoint requires HTTPS." },
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!IsAuthorized(context, options))
        {
            // Deliberately identical to a missing key: the response must not reveal whether a key was
            // recognised, only that this request is not getting in.
            logger.LogWarning("Rejected a console launch request with a bad or missing {Header}.", ApiKeyHeader);
            return Results.Json(new { error = "Unauthorized." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        if (request is null)
            return Problem("A JSON body is required.");

        if (!BrandNameSanitizer.TryNormalize(request.BrandName, out string brandName, out string? brandError))
            return Problem(brandError!);

        if (!TryReadAccessToken(request.AccessToken, out string? accessToken, out string? tokenError))
            return Problem(tokenError!);

        if (!TryReadDatabase(request.Database, out string? database, out string? databaseError))
            return Problem(databaseError!);

        if (!TryReadEndpoint(request.Endpoint, allowList, camus, out string? endpoint, out string? endpointError))
            return Problem(endpointError!);

        if (!TryReadProtocol(request.Protocol, out string? protocol, out string? protocolError))
            return Problem(protocolError!);

        ConsoleLaunchTicket ticket = new(brandName, accessToken, database, endpoint, protocol);

        string? code = store.TryIssueLaunchCode(ticket);
        if (code is null)
        {
            logger.LogError("Console launch store is at capacity ({Max} entries); refusing new launches.",
                options.MaxLiveEntries);
            return Results.Json(
                new { error = "The console is at capacity for pending launches. Retry shortly." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        int lifetime = options.CodeLifetimeSeconds > 0 ? options.CodeLifetimeSeconds : 60;

        await Task.CompletedTask;

        return Results.Ok(new
        {
            launchUrl = $"{BaseUrl(context, options)}/console/launch?code={Uri.EscapeDataString(code)}",
            expiresInSeconds = lifetime,
        });
    }

    /// <summary>
    /// Leg 2. Runs in the visitor's browser, carries no key, and is safe without one because the code
    /// is a 256-bit single-use secret that only the vendor was given.
    /// </summary>
    private static IResult Launch(
        HttpContext context,
        string? code,
        IOptions<ConsoleLaunchOptions> optionsAccessor,
        ConsoleLaunchStore store)
    {
        ConsoleLaunchOptions options = optionsAccessor.Value;

        if (!store.TryRedeemLaunchCode(code, out ConsoleLaunchTicket? ticket) || ticket is null)
            return ExpiredPage();

        string? sessionId = store.TryOpenSession(ticket);
        if (sessionId is null)
            return ExpiredPage();

        bool secure = context.Request.IsHttps || options.RequireHttps;

        context.Response.Cookies.Append(CookieName(secure), sessionId, new CookieOptions
        {
            HttpOnly = true,          // the whole point: script cannot read the session id
            Secure = secure,
            SameSite = SameSiteMode.Lax,   // Lax, not Strict: this cookie is set during a cross-site redirect
            Path = "/",
            IsEssential = true,
            MaxAge = TimeSpan.FromMinutes(options.SessionLifetimeMinutes > 0 ? options.SessionLifetimeMinutes : 60),
        });

        return Results.Redirect("/");
    }

    /// <summary>
    /// A fixed page with no request data interpolated into it — an expired-link message is exactly
    /// the kind of place a reflected parameter usually creeps in.
    /// </summary>
    private static IResult ExpiredPage() =>
        Results.Content(
            "<!doctype html><meta charset=\"utf-8\"><title>Link expired</title>"
            + "<body style=\"font-family:system-ui;padding:2rem;color:#e8eaed;background:#1a1c1e\">"
            + "<h1 style=\"font-size:1.1rem\">This console link has expired</h1>"
            + "<p>Launch links may be opened once, and only for a short time. Ask the application "
            + "that sent you here for a new one.</p></body>",
            contentType: "text/html; charset=utf-8",
            statusCode: StatusCodes.Status400BadRequest);

    private static IResult Problem(string error) =>
        Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>
    /// Constant-time key comparison. The lengths are compared first — unavoidable, since the byte
    /// arrays must match in length — which leaks only the key's length, not its content.
    /// </summary>
    private static bool IsAuthorized(HttpContext context, ConsoleLaunchOptions options)
    {
        if (options.ApiKey.Length < ConsoleLaunchOptions.MinApiKeyLength)
            return false;

        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out Microsoft.Extensions.Primitives.StringValues values))
            return false;

        string? presented = values.Count == 1 ? values[0] : null;
        if (string.IsNullOrEmpty(presented))
            return false;

        byte[] expectedBytes = Encoding.UTF8.GetBytes(options.ApiKey);
        byte[] presentedBytes = Encoding.UTF8.GetBytes(presented);

        return CryptographicOperations.FixedTimeEquals(expectedBytes, presentedBytes);
    }

    /// <summary>
    /// The token is passed to the driver through a ';'-separated connection string, so a ';' would
    /// let a caller append connection settings of its own. Non-ASCII and whitespace are refused for
    /// the same reason: a bearer token has no need of either, and both are ways to smuggle structure.
    /// </summary>
    private static bool TryReadAccessToken(string? raw, out string? token, out string? error)
    {
        token = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
            return true;

        string trimmed = raw.Trim();

        if (trimmed.Length > 4096)
        {
            error = "The access token is too long.";
            return false;
        }

        foreach (char c in trimmed)
        {
            if (c is < (char)0x21 or > (char)0x7E || c == ';')
            {
                error = "The access token may only contain printable ASCII, and may not contain ';'.";
                return false;
            }
        }

        token = trimmed;
        return true;
    }

    private static bool TryReadDatabase(string? raw, out string? database, out string? error)
    {
        database = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
            return true;

        string trimmed = raw.Trim();

        // CamusDB identifiers are letters, digits and underscore; anything else cannot name a real
        // database, and ';' would reach the connection string.
        if (trimmed.Length > 128 || !trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            error = "A database name may only contain ASCII letters, digits and underscores.";
            return false;
        }

        database = trimmed;
        return true;
    }

    /// <summary>
    /// The endpoint is opened by the console's own process, not the visitor's browser, so a payload
    /// naming an internal address turns this console into a request-forgery proxy. Two controls
    /// stand between: <c>CamusDB:LockEndpoint</c>, which refuses any override at all, and
    /// <see cref="ConsoleLaunchOptions.AllowedEndpoints"/>, which pins the acceptable origins.
    /// </summary>
    private static bool TryReadEndpoint(
        string? raw,
        EndpointAllowList allowList,
        CamusDbOptions camus,
        out string? endpoint,
        out string? error)
    {
        endpoint = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
            return true;

        string trimmed = raw.Trim();

        // LockEndpoint is the deployment's flat refusal to be repointed, and it outranks a vendor.
        // Refusing here rather than silently ignoring the field means a vendor that expected its
        // endpoint to take effect finds out at integration time instead of wondering later why every
        // session lands on the wrong server.
        if (camus.LockEndpoint && !string.Equals(trimmed, camus.Endpoint.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            error = "This console pins its CamusDB endpoint (CamusDB:LockEndpoint); a launch may not change it.";
            return false;
        }

        if (trimmed.Contains(';', StringComparison.Ordinal)
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "The endpoint must be an absolute http:// or https:// URL and may not contain ';'.";
            return false;
        }

        if (!allowList.IsAllowed(uri))
        {
            error = "That endpoint is not in this console's allowed endpoint list.";
            return false;
        }

        endpoint = trimmed;
        return true;
    }

    private static bool TryReadProtocol(string? raw, out string? protocol, out string? error)
    {
        protocol = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
            return true;

        string trimmed = raw.Trim().ToLowerInvariant();

        if (trimmed is not ("rest" or "grpc"))
        {
            error = "Protocol must be 'rest' or 'grpc'.";
            return false;
        }

        protocol = trimmed;
        return true;
    }

    /// <summary>
    /// Prefers the configured public base URL. Falling back to the request means trusting the Host
    /// header — acceptable only because the reply goes to the authenticated vendor, who would simply
    /// receive back whatever host it sent.
    /// </summary>
    private static string BaseUrl(HttpContext context, ConsoleLaunchOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl))
            return options.PublicBaseUrl.TrimEnd('/');

        return $"{context.Request.Scheme}://{context.Request.Host}";
    }
}

/// <summary>Body of the vendor's back-channel call.</summary>
public sealed class ConsoleLaunchRequest
{
    /// <summary>Console name to display. Required.</summary>
    public string? BrandName { get; set; }

    /// <summary>A CamusDB bearer token the vendor already minted. Optional.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Database to open on arrival. Optional.</summary>
    public string? Database { get; set; }

    /// <summary>CamusDB endpoint to point the session at. Optional; see the SSRF note on the reader.</summary>
    public string? Endpoint { get; set; }

    /// <summary>rest or grpc. Optional.</summary>
    public string? Protocol { get; set; }
}
