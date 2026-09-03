using System.Data;
using CamusDB.Client;
using CamusDB.WebConsole.Models;
using CamusDB.WebConsole.Options;
using Microsoft.Extensions.Options;

namespace CamusDB.WebConsole.Services;

/// <summary>
/// Per-circuit CamusDB connection. OpenAsync only validates the connection string;
/// connectivity is proven with ping.
///
/// <para>Credentials are never put in the connection string: the driver's parser splits on ';', so a
/// password containing one would be mangled, and configured credentials make the driver share its token
/// process-wide. Instead the console opens an unauthenticated connection and calls
/// <see cref="CamusConnection.LoginAsync"/>, which keeps the minted token private to this circuit while
/// still letting the driver renew it and replay a statement whose token went stale.</para>
/// </summary>
public sealed class CamusSessionService : IAsyncDisposable
{
    /// <summary>Authentication failed — bad token, unknown user, or wrong password.</summary>
    public const string AuthFailedCode = "CADB0516";

    /// <summary>Authenticated, but lacking the privilege the statement needs on some table.</summary>
    public const string PrivilegeDeniedCode = "CADB0517";

    /// <summary>Per-account login rate limit exceeded.</summary>
    public const string LoginRateLimitedCode = "CADB0518";

    /// <summary>Credentials sent over plaintext where the server requires TLS.</summary>
    public const string TlsRequiredCode = "CADB0519";

    /// <summary>The node has no <c>kahuna.backup_dir</c>, so the whole backup surface is unavailable.</summary>
    public const string BackupNotConfiguredCode = "CADB0700";

    /// <summary>A chain does not start at a full backup, or has a gap, broken link, or cycle.</summary>
    public const string BackupChainInvalidCode = "CADB0701";

    /// <summary>An incremental's parent aged past the retention floor; take a full backup instead.</summary>
    public const string BackupNeedsFullBackupCode = "CADB0702";

    /// <summary>The parent named by an incremental request does not exist.</summary>
    public const string BackupParentMissingCode = "CADB0705";

    /// <summary>An artifact is missing, truncated, duplicated, or fails its recorded digest.</summary>
    public const string BackupCorruptArtifactCode = "CADB0706";

    /// <summary>A coordinated backup reached a node that does not lead the backup meta partition.</summary>
    public const string BackupNotCoordinatorCode = "CADB070E";

    /// <summary>
    /// Asks the server directly whether this session is a superuser. Returns true/false for the calling
    /// session and SQL NULL when authentication is disabled — there is no verified identity to report,
    /// and in that case the gated statements are open to any caller anyway. It needs no privilege, and
    /// a FROM-less SELECT opens no table, so it is allowed to any authenticated caller.
    /// </summary>
    private const string SuperuserProbeSql = "SELECT is_superuser()";

    /// <summary>
    /// Fallback for a server too old to know <c>is_superuser()</c>. It infers the same bit the only way
    /// available there: run something carrying the identical superuser gate and see whether it is
    /// refused. A pattern matching nothing returns zero rows rather than raising, so a superuser pays
    /// one empty result set and anyone else is refused with CADB0517.
    /// </summary>
    private const string AdminProbeFallbackSql = "SHOW VARIABLES LIKE 'camus_console_admin_probe_%'";

    private readonly object _gate = new();
    private CamusConnection? _connection;
    private CamusConnectionStringBuilder? _builder;
    private bool _connected;
    private string? _lastError;

    // Held for the circuit's lifetime so a reconnect (or a database switch while disconnected)
    // re-authenticates without asking again. Never persisted anywhere.
    private string? _password;
    private string? _accessToken;

    private readonly EndpointAllowList _allowList;
    private readonly LoginAttemptThrottle _throttle;
    private readonly ILogger<CamusSessionService> _logger;

    // The endpoints this deployment was configured with. They stay acceptable whatever the allowlist
    // says: an operator who wrote an endpoint into configuration has already decided it, and refusing
    // it here would turn one missing list entry into a console that cannot reach its own server.
    private readonly string _configuredEndpoint;
    private readonly string _configuredBackupEndpoint;

    public CamusSessionService(
        IOptions<CamusDbOptions> options,
        EndpointAllowList allowList,
        LoginAttemptThrottle throttle,
        ILogger<CamusSessionService> logger)
    {
        _allowList = allowList;
        _throttle = throttle;
        _logger = logger;

        CamusDbOptions o = options.Value;
        Endpoint = o.Endpoint;
        Database = o.Database;
        Protocol = string.IsNullOrWhiteSpace(o.Protocol) ? "rest" : o.Protocol;
        TimeoutSeconds = o.TimeoutSeconds > 0 ? o.TimeoutSeconds : 30;
        MaxRows = o.MaxRows > 0 ? o.MaxRows : 1000;
        TokenLifetimeSeconds = o.TokenLifetimeSeconds;
        EndpointLocked = o.LockEndpoint;

        BackupEndpoint = o.BackupEndpoint.Trim();
        BackupTimeoutSeconds = o.BackupTimeoutSeconds;

        _configuredEndpoint = Endpoint.Trim();
        _configuredBackupEndpoint = BackupEndpoint;

        RequiresAccessToken = o.RequireAccessToken;

        // Startup already refuses this combination, so reaching here with credentials means the
        // service was built outside the host. Dropping them keeps the one invariant the flag states.
        User = RequiresAccessToken ? "" : o.User.Trim();
        _password = RequiresAccessToken || string.IsNullOrEmpty(o.Password) ? null : o.Password;
        _accessToken = string.IsNullOrWhiteSpace(o.AccessToken) ? null : o.AccessToken.Trim();
    }

    public string Endpoint { get; private set; }

    public string Database { get; private set; }

    public string Protocol { get; private set; }

    public int TimeoutSeconds { get; private set; }

    public int MaxRows { get; private set; }

    public int TokenLifetimeSeconds { get; private set; }

    /// <summary>
    /// Explicit backup administration endpoint, or empty to let the driver fall back to
    /// <see cref="Endpoint"/>. See <see cref="Options.CamusDbOptions.BackupEndpoint"/>.
    /// </summary>
    public string BackupEndpoint { get; private set; }

    public int BackupTimeoutSeconds { get; private set; }

    /// <summary>
    /// The endpoint backup calls will actually reach. The backup API is REST-only, so a gRPC
    /// connection has nowhere to send them unless <see cref="BackupEndpoint"/> is set — that case
    /// reports null so the UI can say which key to set instead of surfacing a driver error per click.
    /// </summary>
    public string? EffectiveBackupEndpoint =>
        !string.IsNullOrEmpty(BackupEndpoint) ? BackupEndpoint
        : string.Equals(Protocol, "grpc", StringComparison.OrdinalIgnoreCase) ? null
        : Endpoint;

    /// <summary>True when server configuration pins the endpoint and protocol — see <see cref="CamusDbOptions.LockEndpoint"/>.</summary>
    public bool EndpointLocked { get; private set; }

    /// <summary>
    /// True when this deployment accepts a supplied access token as the only way to authenticate —
    /// see <see cref="CamusDbOptions.RequireAccessToken"/>. It does not require authentication: an
    /// unauthenticated connection to a server that permits one is still allowed.
    /// </summary>
    public bool RequiresAccessToken { get; }

    /// <summary>
    /// True when this circuit was opened by a vendor launch. The console then holds a token it did
    /// not mint and cannot show, and its database was chosen by the vendor rather than by the
    /// visitor — so remembered browser preferences must not override it.
    /// </summary>
    public bool IsVendorSession { get; private set; }

    /// <summary>
    /// Address of the browser this circuit belongs to, used to count failed sign-in attempts across
    /// circuits. <see cref="LoginAttemptThrottle.UnknownClient"/> until the root component supplies
    /// it — the request that carries the address is finished before the circuit starts, so the value
    /// has to be handed down rather than read here.
    /// </summary>
    public string ClientKey { get; private set; } = LoginAttemptThrottle.UnknownClient;

    /// <summary>
    /// Applies the client address for this circuit. Called once, from the root component. Later calls
    /// are ignored: a circuit belongs to one browser for its whole life, and a second value could only
    /// come from something trying to trade a counted address for a fresh one.
    /// </summary>
    public void SetClientKey(string? clientKey)
    {
        if (!string.Equals(ClientKey, LoginAttemptThrottle.UnknownClient, StringComparison.Ordinal))
            return;

        if (!string.IsNullOrWhiteSpace(clientKey))
            ClientKey = clientKey.Trim();
    }

    /// <summary>User the console authenticates as, or empty when connecting unauthenticated.</summary>
    public string User { get; private set; }

    public bool IsConnected => _connected && _connection?.State == ConnectionState.Open;

    /// <summary>True when a bearer token has been minted (or supplied) for this session.</summary>
    public bool IsAuthenticated => IsConnected && _connection?.AccessToken is not null;

    /// <summary>
    /// True when the session can actually authenticate. A remembered user name alone does not count —
    /// the password is never persisted, so it has to be supplied again.
    /// </summary>
    public bool HasCredentials =>
        (!string.IsNullOrEmpty(User) && _password is not null) || !string.IsNullOrEmpty(_accessToken);

    /// <summary>True when a token was supplied verbatim rather than minted from a password.</summary>
    public bool UsesSuppliedToken => !string.IsNullOrEmpty(_accessToken);

    /// <summary>
    /// Set when the last connect attempt failed because the server has authentication enabled and this
    /// session had no usable credentials. The UI uses it to prompt for a login rather than just
    /// reporting an error.
    /// </summary>
    public bool RequiresAuthentication { get; private set; }

    /// <summary>
    /// True when the probe run at connect time showed this session may run the administration
    /// statements — a superuser, or any caller against a server with authentication disabled.
    /// </summary>
    public bool HasAdminAccess { get; private set; }

    /// <summary>
    /// True when the probe reached a verdict. It stays false when the probe failed for a reason that
    /// says nothing about privilege (an older server that does not know SHOW VARIABLES, a transport
    /// hiccup), so the console can offer the views anyway and let the statement report the problem.
    /// </summary>
    public bool AdminAccessProbed { get; private set; }

    /// <summary>
    /// Whether this session is a superuser, as the server reports it. Null means the question does not
    /// apply — authentication is disabled, so there is no verified identity and the gated statements
    /// are open to everyone — or that the probe reached no verdict.
    /// </summary>
    public bool? IsSuperuser { get; private set; }

    /// <summary>True when the administration views are worth offering — see <see cref="AdminAccessProbed"/>.</summary>
    public bool ShowAdministration => IsConnected && (HasAdminAccess || !AdminAccessProbed);

    public string? LastError => _lastError;

    public string ServerVersion => _connection?.ServerVersion ?? "";

    public event Action? Changed;

    /// <summary>
    /// Applies a preferred database name before the first connect (e.g. from localStorage).
    /// Ignored once a connection is open.
    /// </summary>
    public void PreferDatabase(string database)
    {
        if (IsConnected || string.IsNullOrWhiteSpace(database) || database.Contains(';', StringComparison.Ordinal))
            return;

        Database = database.Trim();
    }

    /// <summary>
    /// Applies a remembered user name before the first connect. The password is never remembered, so
    /// this only prefills the Configure dialog — it does not make the session able to authenticate.
    /// </summary>
    public void PreferUser(string user)
    {
        // A remembered name would prefill a dialog that no longer offers the field, and would then
        // be reported as this session's identity while the token is what actually authenticated it.
        if (RequiresAccessToken)
            return;

        if (IsConnected || string.IsNullOrWhiteSpace(user) || !string.IsNullOrEmpty(User))
            return;

        User = user.Trim();
    }

    /// <summary>
    /// Applies a vendor launch ticket before the first connect. Called once, from the interactive
    /// circuit, with a ticket that never left the server.
    ///
    /// <para>The token is stored in the same private field a configured <c>AccessToken</c> uses, so
    /// it is reachable only through <see cref="UsesSuppliedToken"/> — there is no getter that hands
    /// it back, and the Configure dialog never prefills it.</para>
    ///
    /// <para>A ticket that pins the endpoint also locks it: the visitor of a vendor-provisioned
    /// console must not be able to repoint it at another server and carry the vendor's token there.</para>
    /// </summary>
    public void ApplyLaunchTicket(ConsoleLaunchTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        // Arriving after a connection is open would mean a second handoff into a live circuit;
        // there is no such path, and honouring it would swap credentials underneath open state.
        if (IsConnected)
            return;

        IsVendorSession = true;

        if (!string.IsNullOrEmpty(ticket.AccessToken))
        {
            // A supplied token and a user/password pair are exclusive in the driver.
            _accessToken = ticket.AccessToken;
            User = "";
            _password = null;
        }

        if (!string.IsNullOrWhiteSpace(ticket.Database))
            Database = ticket.Database.Trim();

        if (!string.IsNullOrWhiteSpace(ticket.Protocol))
            Protocol = ticket.Protocol.Trim();

        if (!string.IsNullOrWhiteSpace(ticket.Endpoint))
        {
            Endpoint = ticket.Endpoint.Trim();
            EndpointLocked = true;
        }

        NotifyChanged();
    }

    public CamusConnection GetConnection()
    {
        if (_connection is null || _connection.State != ConnectionState.Open)
            throw new InvalidOperationException("Not connected to CamusDB. Open Configure and connect first.");

        return _connection;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        // Only an attempt that presents a secret is counted. The console autoconnects without
        // credentials on every page load, and counting that would let an ordinary visitor lock out
        // the address they share with everyone else behind the same proxy.
        bool credentialed = HasCredentials;

        if (credentialed)
            await AwaitLoginThrottleAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _builder = new CamusConnectionStringBuilder(BuildConnectionString());
            _connection = new CamusConnection(_builder);
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Authenticate before anything else: on an auth-enabled server even ping needs a token.
            if (!string.IsNullOrEmpty(User) && _password is not null)
                await _connection.LoginAsync(User, _password, cancellationToken).ConfigureAwait(false);

            await using CamusCommand ping = _connection.CreatePingCommand();
            await ping.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _connected = true;
            _lastError = null;
            RequiresAuthentication = false;

            // Cleared here, not at the end of the method: the ping is the point at which the
            // credentials are proven. What follows can still fail for reasons that say nothing about
            // them, and a proven password must not go on paying for earlier wrong ones.
            if (credentialed)
                _throttle.RecordSuccess(ClientKey, User);

            // Configured DB may not exist (ping still succeeds). Prefer a real database.
            await ResolveExistingDatabaseAsync(cancellationToken).ConfigureAwait(false);

            await ProbeAdminAccessAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (CamusException ex)
        {
            await CleanupConnectionAsync().ConfigureAwait(false);
            _connected = false;
            _lastError = $"{ex.Code}: {Describe(ex, RequiresAccessToken)}";
            RequiresAuthentication = ex.Code == AuthFailedCode;

            if (credentialed && ex.Code is AuthFailedCode or LoginRateLimitedCode)
                RecordLoginFailure(ex.Code);

            throw new CamusException(ex.Code, Describe(ex, RequiresAccessToken));
        }
        catch (Exception ex)
        {
            await CleanupConnectionAsync().ConfigureAwait(false);
            _connected = false;
            _lastError = ex.Message;
            RequiresAuthentication = false;
            throw;
        }
        finally
        {
            NotifyChanged();
        }
    }

    /// <summary>
    /// Pays whatever this client owes for its earlier failures before the attempt is made, and
    /// refuses outright once it has spent its allowance.
    /// </summary>
    /// <exception cref="InvalidOperationException">The client is inside a lockout.</exception>
    private async Task AwaitLoginThrottleAsync(CancellationToken cancellationToken)
    {
        LoginAttemptThrottle.ThrottleDecision decision = _throttle.Check(ClientKey, User);

        if (!decision.Allowed)
        {
            int seconds = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));

            _logger.LogWarning(
                "Refused a sign-in attempt from {ClientKey} for user '{User}': too many failures. "
                + "Blocked for another {Seconds}s.",
                ClientKey, User, seconds);

            _lastError = $"Too many failed sign-in attempts. Try again in {seconds} seconds.";
            NotifyChanged();

            throw new InvalidOperationException(
                $"Too many failed sign-in attempts from this address. Try again in {seconds} seconds.");
        }

        // The wait is served here rather than after the failure so that a caller which abandons the
        // circuit mid-wait still pays for its next attempt.
        if (decision.Delay > TimeSpan.Zero)
            await Task.Delay(decision.Delay, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Counts one failure and writes it to the log. The log line is the only durable record of a
    /// guessing run — <see cref="LastError"/> lives in one circuit's memory and is gone with it.
    /// </summary>
    private void RecordLoginFailure(string code)
    {
        _throttle.RecordFailure(ClientKey, User);

        _logger.LogWarning(
            "Failed CamusDB sign-in from {ClientKey} for user '{User}' at {Endpoint} ({Code}).",
            ClientKey, string.IsNullOrEmpty(User) ? "(token)" : User, Endpoint, code);
    }

    /// <summary>
    /// Credentials are deliberately absent here — see the type remarks. A supplied
    /// <c>AccessToken</c> has no other way in, so it is validated rather than escaped.
    /// </summary>
    private string BuildConnectionString()
    {
        string connectionString =
            $"Endpoint={Endpoint};Database={Database};Timeout={TimeoutSeconds};Protocol={Protocol}";

        if (!string.IsNullOrEmpty(_accessToken))
            connectionString += $";AccessToken={_accessToken}";

        if (TokenLifetimeSeconds > 0)
            connectionString += $";TokenLifetime={TokenLifetimeSeconds}";

        if (!string.IsNullOrEmpty(BackupEndpoint))
            connectionString += $";BackupEndpoint={BackupEndpoint}";

        if (BackupTimeoutSeconds > 0)
            connectionString += $";BackupTimeout={BackupTimeoutSeconds}";

        return connectionString;
    }

    /// <summary>
    /// Turns the driver's authentication codes into something a console user can act on. Everything
    /// else keeps the driver's own wording. The code is not included — callers that show it separately
    /// would otherwise repeat it.
    /// </summary>
    /// <param name="ex">The driver's exception.</param>
    /// <param name="tokenOnly">
    /// Whether this console refuses user/password sign-in. It changes only the wording, and it has to:
    /// telling somebody to sign in, on a console with no field to sign in with, sends them looking for
    /// a password box that does not exist.
    /// </param>
    public static string Describe(CamusException ex, bool tokenOnly = false) => ex.Code switch
    {
        AuthFailedCode when tokenOnly =>
            "Authentication failed. The access token was missing, rejected, or has expired — open "
            + "Configure and supply a current one.",
        AuthFailedCode =>
            "Authentication failed. The server rejected the credentials, or has authentication enabled "
            + "and this session sent none — open Configure and sign in.",
        // Two different refusals share this code. A table-grant miss is fixed with GRANT; a
        // superuser-only surface is not, and offering GRANT there sends the operator down a dead end.
        PrivilegeDeniedCode when ex.Message.Contains("superuser", StringComparison.OrdinalIgnoreCase) =>
            $"Insufficient privilege: {Sentence(ex.Message)} No per-database grant covers this — it "
            + "needs a superuser account.",
        PrivilegeDeniedCode =>
            $"Insufficient privilege: {ex.Message}. Every table a statement touches needs the privilege; "
            + "grant it with GRANT … ON database.table TO user.",
        LoginRateLimitedCode =>
            "Too many login attempts for that account. Wait a minute and try again.",
        TlsRequiredCode =>
            "The server refuses credentials over a plaintext connection. Use an https:// endpoint, or "
            + "start the server with --require-tls-when-auth-enabled false when TLS terminates in front of it.",
        BackupNotConfiguredCode =>
            "Backups are not configured on this node. Set kahuna.backup_dir in the server's config.yml "
            + "and restart it; until then the whole backup surface is unavailable.",
        BackupChainInvalidCode =>
            $"The backup chain could not be assembled: {ex.Message}. It does not start at a full backup, "
            + "or has a gap, a broken parent link, or a cycle — this backup would not restore.",
        BackupNeedsFullBackupCode =>
            "That parent has aged past the retention floor, so a contiguous incremental is impossible. "
            + "Take a full backup instead.",
        BackupParentMissingCode =>
            "The parent backup named by this request is no longer in the catalog.",
        BackupCorruptArtifactCode =>
            $"A backup artifact is missing, truncated, or fails its recorded digest: {ex.Message}.",
        BackupNotCoordinatorCode =>
            "A coordinated backup must be taken on the coordinator node. Point the backup endpoint at "
            + "the current coordinator and retry.",
        _ => ex.Message,
    };

    /// <summary>
    /// Ends a server message with a period so console prose can continue after it. Server messages are
    /// inconsistent about trailing punctuation, and the two run together without this.
    /// </summary>
    private static string Sentence(string message)
    {
        string trimmed = message.TrimEnd();

        return trimmed.Length == 0 || trimmed[^1] is '.' or '!' or '?' or ':' ? trimmed : trimmed + ".";
    }

    /// <summary>
    /// If the configured database is missing, switch to the first name from SHOW DATABASES.
    /// </summary>
    private async Task ResolveExistingDatabaseAsync(CancellationToken cancellationToken)
    {
        if (_connection is null)
            return;

        await using CamusCommand command = _connection.CreateCamusCommand("SHOW DATABASES");
        await using CamusDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<string> names = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.FieldCount == 0)
                continue;
            object value = reader.GetValue(0);
            if (value is DBNull)
                continue;
            string name = Convert.ToString(value) ?? "";
            if (name.Length > 0)
                names.Add(name);
        }

        if (names.Count == 0)
            return;

        if (names.Any(n => string.Equals(n, Database, StringComparison.OrdinalIgnoreCase)))
            return;

        string fallback = names[0];
        _connection.ChangeDatabase(fallback);
        Database = fallback;
    }

    /// <summary>
    /// Decides whether this session may reach the administration statements. Never throws: a session
    /// that cannot run them is a normal state, not a failed connection.
    /// </summary>
    private async Task ProbeAdminAccessAsync(CancellationToken cancellationToken)
    {
        HasAdminAccess = false;
        AdminAccessProbed = false;
        IsSuperuser = null;

        if (_connection is null)
            return;

        try
        {
            await using CamusCommand command = _connection.CreateCamusCommand(SuperuserProbeSql);
            await using CamusDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.FieldCount > 0)
            {
                object value = reader.GetValue(0);

                // NULL means authentication is disabled, and then every caller may run these
                // statements — so "no identity" is access, not the absence of it.
                IsSuperuser = value is DBNull ? null : Convert.ToBoolean(value);
                HasAdminAccess = IsSuperuser ?? true;
                AdminAccessProbed = true;
                return;
            }
        }
        catch (CamusException ex) when (ex.Code == PrivilegeDeniedCode)
        {
            // Not expected — the function needs no privilege — but a refusal is still a definitive no.
            AdminAccessProbed = true;
            IsSuperuser = false;
            return;
        }
        catch
        {
            // Most likely a server that predates is_superuser(); fall through and infer it instead.
        }

        await ProbeAdminAccessLegacyAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ProbeAdminAccessLegacyAsync(CancellationToken cancellationToken)
    {
        if (_connection is null)
            return;

        try
        {
            await using CamusCommand command = _connection.CreateCamusCommand(AdminProbeFallbackSql);
            await using CamusDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // The pattern matches nothing by construction; draining is just protocol hygiene.
            }

            HasAdminAccess = true;
            AdminAccessProbed = true;
        }
        catch (CamusException ex) when (ex.Code == PrivilegeDeniedCode)
        {
            // A definitive "not a superuser".
            AdminAccessProbed = true;
            IsSuperuser = false;
        }
        catch
        {
            // Says nothing about privilege — leave the verdict open.
        }
    }

    public async Task ConfigureAndConnectAsync(
        string endpoint,
        string database,
        string protocol,
        int timeoutSeconds,
        int maxRows,
        string? user = null,
        string? password = null,
        string? accessToken = null,
        string? backupEndpoint = null,
        CancellationToken cancellationToken = default)
    {
        string? token = string.IsNullOrWhiteSpace(accessToken) ? null : accessToken.Trim();
        if (token is not null && token.Contains(';', StringComparison.Ordinal))
            throw new ArgumentException("An access token cannot contain ';'.", nameof(accessToken));

        // The dialog hides the user and password fields under this flag, but the dialog is not the
        // boundary — this method is. Refusing here is what keeps a password out of the console for
        // any caller, and turns a stale prefill into a visible error instead of a silent sign-in.
        if (RequiresAccessToken && (!string.IsNullOrWhiteSpace(user) || !string.IsNullOrEmpty(password)))
        {
            throw new InvalidOperationException(
                "This console accepts an access token only (CamusDB:RequireAccessToken). "
                + "User and password sign-in is disabled.");
        }

        string newEndpoint = endpoint.Trim();
        string newProtocol = string.IsNullOrWhiteSpace(protocol) ? "rest" : protocol.Trim();
        string newDatabase = database.Trim();

        // The endpoint reaches the driver via a ';'-separated connection string, and the server —
        // not the visitor's browser — opens it. Validate the shape, and honour the deployment lock.
        if (!Uri.TryCreate(newEndpoint, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Endpoint must be an absolute http:// or https:// URL.", nameof(endpoint));
        }

        if (newEndpoint.Contains(';', StringComparison.Ordinal))
            throw new ArgumentException("Endpoint cannot contain ';'.", nameof(endpoint));

        // The allowlist governs this path too, not only a vendor launch payload. A shape check says
        // the string is a URL; it does not say the console is willing to open it, and the console —
        // not the visitor's browser — is what opens it.
        if (!IsEndpointAllowed(newEndpoint, uri, _configuredEndpoint))
        {
            throw new ArgumentException(
                "That endpoint is not in this console's allowed endpoint list "
                + "(ConsoleLaunch:AllowedEndpoints).", nameof(endpoint));
        }

        if (newDatabase.Contains(';', StringComparison.Ordinal))
            throw new ArgumentException("A database name cannot contain ';'.", nameof(database));

        // The backup endpoint is a second URL the server opens on the visitor's behalf, so it gets the
        // same shape check and the same deployment lock as the main endpoint.
        string newBackupEndpoint = (backupEndpoint ?? "").Trim();
        if (newBackupEndpoint.Length > 0)
        {
            if (!Uri.TryCreate(newBackupEndpoint, UriKind.Absolute, out Uri? backupUri)
                || (backupUri.Scheme != Uri.UriSchemeHttp && backupUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException(
                    "The backup endpoint must be an absolute http:// or https:// URL.", nameof(backupEndpoint));
            }

            if (newBackupEndpoint.Contains(';', StringComparison.Ordinal))
                throw new ArgumentException("A backup endpoint cannot contain ';'.", nameof(backupEndpoint));

            if (!IsEndpointAllowed(newBackupEndpoint, backupUri, _configuredBackupEndpoint))
            {
                throw new ArgumentException(
                    "That backup endpoint is not in this console's allowed endpoint list "
                    + "(ConsoleLaunch:AllowedEndpoints).", nameof(backupEndpoint));
            }
        }

        if (EndpointLocked
            && (!string.Equals(newEndpoint, Endpoint, StringComparison.Ordinal)
                || !string.Equals(newProtocol, Protocol, StringComparison.Ordinal)
                || !string.Equals(newBackupEndpoint, BackupEndpoint, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The endpoint is locked by server configuration (CamusDB:LockEndpoint) and cannot be changed.");
        }

        BackupEndpoint = newBackupEndpoint;
        Endpoint = newEndpoint;
        Database = newDatabase;
        Protocol = newProtocol;
        TimeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 30;
        MaxRows = maxRows > 0 ? maxRows : 1000;

        // A supplied token wins over a user/password pair — the driver treats them as exclusive.
        _accessToken = token;
        User = token is null ? (user ?? "").Trim() : "";
        _password = token is null && !string.IsNullOrEmpty(User) ? password ?? "" : null;

        await ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the console may open this URL. The deployment's own configured value always passes;
    /// anything else has to be on the allowlist, which permits everything while it is empty.
    /// </summary>
    /// <param name="raw">The URL as typed, compared against the configured value.</param>
    /// <param name="parsed">The same URL parsed, which is what the allowlist matches on.</param>
    /// <param name="configured">The value this deployment was started with, or empty.</param>
    private bool IsEndpointAllowed(string raw, Uri parsed, string configured) =>
        (configured.Length > 0 && string.Equals(raw, configured, StringComparison.OrdinalIgnoreCase))
        || _allowList.IsAllowed(parsed);

    /// <summary>
    /// Revokes the session's token server-side, forgets the credentials, and drops the connection.
    /// A token supplied verbatim is only forgotten, not revoked — the console does not own it, and
    /// another process may still be using it.
    /// </summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await RevokeTokenAsync(cancellationToken).ConfigureAwait(false);

        User = "";
        _password = null;
        _accessToken = null;

        await CleanupConnectionAsync().ConfigureAwait(false);
    }

    private async Task RevokeTokenAsync(CancellationToken cancellationToken)
    {
        if (UsesSuppliedToken || _connection is null || _connection.AccessToken is null)
            return;

        try
        {
            await _connection.LogoutAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The token expires on its own; a failed revoke must not block signing out.
        }
    }

    /// <param name="notify">
    /// When false, updates the active database without raising <see cref="Changed"/>
    /// (used while loading schema so the tree is not rebuilt mid-expand).
    /// </param>
    public async Task ChangeDatabaseAsync(
        string databaseName,
        bool notify = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        string trimmed = databaseName.Trim();
        if (trimmed.Contains(';', StringComparison.Ordinal))
            throw new ArgumentException("A database name cannot contain ';'.", nameof(databaseName));

        if (!IsConnected)
        {
            Database = trimmed;
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(Database, trimmed, StringComparison.OrdinalIgnoreCase))
            return;

        CamusConnection connection = GetConnection();
        connection.ChangeDatabase(trimmed);
        Database = trimmed;

        await using CamusCommand ping = connection.CreatePingCommand();
        await ping.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (notify)
            NotifyChanged();
    }

    public Task DisconnectAsync()
    {
        return CleanupConnectionAsync();
    }

    public async ValueTask DisposeAsync()
    {
        // Best effort on circuit teardown: a token left alive is valid until the server expires it.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        try
        {
            await RevokeTokenAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // Nothing left to do while the circuit is going away.
        }

        await CleanupConnectionAsync().ConfigureAwait(false);
    }

    private Task CleanupConnectionAsync()
    {
        lock (_gate)
        {
            if (_connection is not null)
            {
                try
                {
                    _connection.Close();
                    _connection.Dispose();
                }
                catch
                {
                    // ignore dispose races on circuit teardown
                }

                _connection = null;
                _builder = null;
            }

            _connected = false;

            // The verdict belongs to the connection that was probed, not to the console.
            HasAdminAccess = false;
            AdminAccessProbed = false;
            IsSuperuser = null;
        }

        NotifyChanged();
        return Task.CompletedTask;
    }

    private void NotifyChanged() => Changed?.Invoke();
}
