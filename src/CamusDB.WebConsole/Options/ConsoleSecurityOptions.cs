namespace CamusDB.WebConsole.Options;

/// <summary>
/// Throttles that stand in front of the two surfaces an unauthenticated caller can reach: the
/// Configure dialog's sign-in, and the vendor launch endpoints.
///
/// <para>Neither throttle replaces a server-side control. CamusDB applies its own per-account login
/// limit, and the launch endpoints are already behind a key from a random source. These limits exist
/// because both of those are per-account or per-secret: a caller that spreads its guesses across many
/// accounts, or that guesses the key itself, meets nothing that counts the attempts.</para>
///
/// <para><b>The client key is the socket peer address.</b> This console does not read
/// <c>X-Forwarded-For</c>, because a header a caller writes is a key the caller chooses, and a chosen
/// key defeats every counter below. Behind a reverse proxy every visitor therefore shares one key —
/// wire up <c>ForwardedHeaders</c> with known proxies, or raise the limits, or set
/// <see cref="LoginThrottleEnabled"/> to false and rely on the server.</para>
/// </summary>
public sealed class ConsoleSecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// Count and slow failed sign-in attempts from the Configure dialog. On by default: without it
    /// the console is a thin pass-through, and a password may be guessed as fast as the circuit can
    /// carry the attempts.
    /// </summary>
    public bool LoginThrottleEnabled { get; set; } = true;

    /// <summary>
    /// Failures inside the window that cost nothing. A person who mistypes a password twice must not
    /// be made to wait, so the delay starts only after this many.
    /// </summary>
    public int LoginFreeAttempts { get; set; } = 3;

    /// <summary>
    /// Failures inside the window, for one client and one user name, before the console refuses to
    /// attempt at all until <see cref="LoginLockoutSeconds"/> passes.
    /// </summary>
    public int LoginMaxAttempts { get; set; } = 10;

    /// <summary>
    /// Multiplies <see cref="LoginMaxAttempts"/> to give the ceiling for one client across every user
    /// name it tries. It is what a per-user counter alone cannot see: a caller that guesses one
    /// password against a thousand accounts never reaches the per-user limit.
    /// </summary>
    public int LoginSprayMultiplier { get; set; } = 5;

    /// <summary>How long failures are remembered. A client that stops guessing is forgotten after this.</summary>
    public int LoginWindowSeconds { get; set; } = 300;

    /// <summary>How long a client that passed <see cref="LoginMaxAttempts"/> is refused.</summary>
    public int LoginLockoutSeconds { get; set; } = 300;

    /// <summary>
    /// Ceiling on the delay added before an attempt. The delay doubles per failure past
    /// <see cref="LoginFreeAttempts"/> and stops here, because the lockout — not an ever-growing
    /// wait — is what ends a run of guesses.
    /// </summary>
    public int LoginMaxDelaySeconds { get; set; } = 8;

    /// <summary>
    /// Ceiling on tracked clients. Reached, the entry closest to expiry is dropped to make room, so a
    /// caller that rotates its source address cannot grow this store without bound.
    /// </summary>
    public int MaxTrackedClients { get; set; } = 20_000;

    /// <summary>
    /// Rate-limit the vendor launch endpoints per client address. On by default. It is what turns the
    /// launch surface from "guess the key as fast as the network allows" into a counted number of
    /// tries per minute, and it is the only thing that bounds how fast the allowed-endpoint list can
    /// be probed by a caller that already holds the key.
    /// </summary>
    public bool RateLimitEnabled { get; set; } = true;

    /// <summary>Requests one client may make to each launch endpoint per <see cref="LaunchWindowSeconds"/>.</summary>
    public int LaunchPermitLimit { get; set; } = 30;

    /// <summary>Length of the launch rate-limit window, in seconds.</summary>
    public int LaunchWindowSeconds { get; set; } = 60;
}
