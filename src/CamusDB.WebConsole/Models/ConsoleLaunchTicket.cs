namespace CamusDB.WebConsole.Models;

/// <summary>
/// What a vendor asked for, held server-side only.
///
/// <para><see cref="AccessToken"/> is the reason this type never leaves the server process: it is
/// the credential the console authenticates to CamusDB with, and a visitor who learned it could use
/// it against the database directly, outside the console and outside whatever the vendor's own UI
/// permits. It is never serialised into a page, a URL, a cookie, or JS-reachable state — the browser
/// only ever holds an opaque session id that indexes this record.</para>
/// </summary>
/// <param name="BrandName">Already normalised by <see cref="Services.BrandNameSanitizer"/>.</param>
/// <param name="AccessToken">A CamusDB bearer token the vendor minted, or null to launch unauthenticated.</param>
/// <param name="Database">Database to open, or null to keep the console's configured default.</param>
/// <param name="Endpoint">CamusDB endpoint to point at, or null to keep the configured one.</param>
/// <param name="Protocol">rest or grpc, or null to keep the configured one.</param>
public sealed record ConsoleLaunchTicket(
    string BrandName,
    string? AccessToken,
    string? Database,
    string? Endpoint,
    string? Protocol);
