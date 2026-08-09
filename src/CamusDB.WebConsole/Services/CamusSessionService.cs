using System.Data;
using CamusDB.Client;
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

    private readonly object _gate = new();
    private CamusConnection? _connection;
    private CamusConnectionStringBuilder? _builder;
    private bool _connected;
    private string? _lastError;

    // Held for the circuit's lifetime so a reconnect (or a database switch while disconnected)
    // re-authenticates without asking again. Never persisted anywhere.
    private string? _password;
    private string? _accessToken;

    public CamusSessionService(IOptions<CamusDbOptions> options)
    {
        CamusDbOptions o = options.Value;
        Endpoint = o.Endpoint;
        Database = o.Database;
        Protocol = string.IsNullOrWhiteSpace(o.Protocol) ? "rest" : o.Protocol;
        TimeoutSeconds = o.TimeoutSeconds > 0 ? o.TimeoutSeconds : 30;
        MaxRows = o.MaxRows > 0 ? o.MaxRows : 1000;
        TokenLifetimeSeconds = o.TokenLifetimeSeconds;
        EndpointLocked = o.LockEndpoint;

        User = o.User.Trim();
        _password = string.IsNullOrEmpty(o.Password) ? null : o.Password;
        _accessToken = string.IsNullOrWhiteSpace(o.AccessToken) ? null : o.AccessToken.Trim();
    }

    public string Endpoint { get; private set; }

    public string Database { get; private set; }

    public string Protocol { get; private set; }

    public int TimeoutSeconds { get; private set; }

    public int MaxRows { get; private set; }

    public int TokenLifetimeSeconds { get; private set; }

    /// <summary>True when server configuration pins the endpoint and protocol — see <see cref="CamusDbOptions.LockEndpoint"/>.</summary>
    public bool EndpointLocked { get; }

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
        if (IsConnected || string.IsNullOrWhiteSpace(user) || !string.IsNullOrEmpty(User))
            return;

        User = user.Trim();
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

            // Configured DB may not exist (ping still succeeds). Prefer a real database.
            await ResolveExistingDatabaseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (CamusException ex)
        {
            await CleanupConnectionAsync().ConfigureAwait(false);
            _connected = false;
            _lastError = $"{ex.Code}: {Describe(ex)}";
            RequiresAuthentication = ex.Code == AuthFailedCode;
            throw new CamusException(ex.Code, Describe(ex));
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

        return connectionString;
    }

    /// <summary>
    /// Turns the driver's authentication codes into something a console user can act on. Everything
    /// else keeps the driver's own wording. The code is not included — callers that show it separately
    /// would otherwise repeat it.
    /// </summary>
    public static string Describe(CamusException ex) => ex.Code switch
    {
        AuthFailedCode =>
            "Authentication failed. The server rejected the credentials, or has authentication enabled "
            + "and this session sent none — open Configure and sign in.",
        PrivilegeDeniedCode =>
            $"Insufficient privilege: {ex.Message}. Every table a statement touches needs the privilege; "
            + "grant it with GRANT … ON database.table TO user.",
        LoginRateLimitedCode =>
            "Too many login attempts for that account. Wait a minute and try again.",
        TlsRequiredCode =>
            "The server refuses credentials over a plaintext connection. Use an https:// endpoint, or "
            + "start the server with --require-tls-when-auth-enabled false when TLS terminates in front of it.",
        _ => ex.Message,
    };

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

    public async Task ConfigureAndConnectAsync(
        string endpoint,
        string database,
        string protocol,
        int timeoutSeconds,
        int maxRows,
        string? user = null,
        string? password = null,
        string? accessToken = null,
        CancellationToken cancellationToken = default)
    {
        string? token = string.IsNullOrWhiteSpace(accessToken) ? null : accessToken.Trim();
        if (token is not null && token.Contains(';', StringComparison.Ordinal))
            throw new ArgumentException("An access token cannot contain ';'.", nameof(accessToken));

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

        if (newDatabase.Contains(';', StringComparison.Ordinal))
            throw new ArgumentException("A database name cannot contain ';'.", nameof(database));

        if (EndpointLocked
            && (!string.Equals(newEndpoint, Endpoint, StringComparison.Ordinal)
                || !string.Equals(newProtocol, Protocol, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The endpoint is locked by server configuration (CamusDB:LockEndpoint) and cannot be changed.");
        }

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
        }

        NotifyChanged();
        return Task.CompletedTask;
    }

    private void NotifyChanged() => Changed?.Invoke();
}
