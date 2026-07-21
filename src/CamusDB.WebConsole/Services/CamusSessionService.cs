using System.Data;
using CamusDB.Client;
using CamusDB.WebConsole.Options;
using Microsoft.Extensions.Options;

namespace CamusDB.WebConsole.Services;

/// <summary>
/// Per-circuit CamusDB connection. OpenAsync only validates the connection string;
/// connectivity is proven with ping.
/// </summary>
public sealed class CamusSessionService : IAsyncDisposable
{
    private readonly object _gate = new();
    private CamusConnection? _connection;
    private CamusConnectionStringBuilder? _builder;
    private bool _connected;
    private string? _lastError;

    public CamusSessionService(IOptions<CamusDbOptions> options)
    {
        CamusDbOptions o = options.Value;
        Endpoint = o.Endpoint;
        Database = o.Database;
        Protocol = string.IsNullOrWhiteSpace(o.Protocol) ? "rest" : o.Protocol;
        TimeoutSeconds = o.TimeoutSeconds > 0 ? o.TimeoutSeconds : 30;
        MaxRows = o.MaxRows > 0 ? o.MaxRows : 1000;
    }

    public string Endpoint { get; private set; }

    public string Database { get; private set; }

    public string Protocol { get; private set; }

    public int TimeoutSeconds { get; private set; }

    public int MaxRows { get; private set; }

    public bool IsConnected => _connected && _connection?.State == ConnectionState.Open;

    public string? LastError => _lastError;

    public string ServerVersion => _connection?.ServerVersion ?? "";

    public event Action? Changed;

    /// <summary>
    /// Applies a preferred database name before the first connect (e.g. from localStorage).
    /// Ignored once a connection is open.
    /// </summary>
    public void PreferDatabase(string database)
    {
        if (IsConnected || string.IsNullOrWhiteSpace(database))
            return;

        Database = database.Trim();
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
            string connectionString =
                $"Endpoint={Endpoint};Database={Database};Timeout={TimeoutSeconds};Protocol={Protocol}";

            _builder = new CamusConnectionStringBuilder(connectionString);
            _connection = new CamusConnection(_builder);
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using CamusCommand ping = _connection.CreatePingCommand();
            await ping.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _connected = true;
            _lastError = null;

            // Configured DB may not exist (ping still succeeds). Prefer a real database.
            await ResolveExistingDatabaseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (CamusException ex)
        {
            await CleanupConnectionAsync().ConfigureAwait(false);
            _connected = false;
            _lastError = $"{ex.Code}: {ex.Message}";
            throw;
        }
        catch (Exception ex)
        {
            await CleanupConnectionAsync().ConfigureAwait(false);
            _connected = false;
            _lastError = ex.Message;
            throw;
        }
        finally
        {
            NotifyChanged();
        }
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

    public async Task ConfigureAndConnectAsync(
        string endpoint,
        string database,
        string protocol,
        int timeoutSeconds,
        int maxRows,
        CancellationToken cancellationToken = default)
    {
        Endpoint = endpoint.Trim();
        Database = database.Trim();
        Protocol = string.IsNullOrWhiteSpace(protocol) ? "rest" : protocol.Trim();
        TimeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 30;
        MaxRows = maxRows > 0 ? maxRows : 1000;

        await ConnectAsync(cancellationToken).ConfigureAwait(false);
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

    public ValueTask DisposeAsync() => new(CleanupConnectionAsync());

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
