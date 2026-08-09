namespace CamusDB.WebConsole.Options;

public sealed class CamusDbOptions
{
    public const string SectionName = "CamusDB";

    public string Endpoint { get; set; } = "http://localhost:5095";

    public string Database { get; set; } = "demo";

    /// <summary>Wire protocol: rest (default) or grpc.</summary>
    public string Protocol { get; set; } = "rest";

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Maximum rows materialised into the results grid.</summary>
    public int MaxRows { get; set; } = 1000;

    /// <summary>
    /// User to authenticate as. Empty against a server with authentication disabled (the default),
    /// which is what a stock CamusDB install expects.
    /// </summary>
    public string User { get; set; } = "";

    /// <summary>
    /// That user's password. Prefer supplying it per session in the Configure dialog, or from an
    /// environment variable / secret store (CamusDB__Password), over committing it to appsettings.json.
    /// </summary>
    public string Password { get; set; } = "";

    /// <summary>
    /// A bearer token obtained elsewhere, used verbatim instead of logging in. Mutually exclusive with
    /// <see cref="User"/>; the console cannot renew it, so it fails once the server expires it.
    /// </summary>
    public string AccessToken { get; set; } = "";

    /// <summary>
    /// When true, the Configure dialog cannot repoint the console at a different server: endpoint and
    /// protocol stay fixed to the configured values. Enable this (CamusDB__LockEndpoint=true) whenever
    /// the console is reachable beyond localhost, so a visitor cannot use the server as a proxy to
    /// probe internal hosts it can reach but they cannot.
    /// </summary>
    public bool LockEndpoint { get; set; }

    /// <summary>
    /// Fallback seconds to reuse a minted token when the server reports no expiry. 0 leaves the driver
    /// default (10 minutes). When the server does report an expiry, that value wins.
    /// </summary>
    public int TokenLifetimeSeconds { get; set; }

    /// <summary>
    /// Where the backup administration API lives. Empty falls back to <see cref="Endpoint"/>, which is
    /// what a REST deployment wants. Two cases need it set explicitly: <c>Protocol=grpc</c>, because
    /// these endpoints are REST-only and the gRPC port cannot serve them, and a multi-node
    /// <see cref="Endpoint"/> pool, because a coordinated backup must reach the coordinator and this
    /// value is used verbatim with no round-robin.
    /// </summary>
    public string BackupEndpoint { get; set; } = "";

    /// <summary>
    /// Timeout in seconds for backup administration calls. 0 leaves the driver default (300s) — a full
    /// backup copies a whole node's base image, so this is deliberately far longer than
    /// <see cref="TimeoutSeconds"/>.
    /// </summary>
    public int BackupTimeoutSeconds { get; set; }
}
