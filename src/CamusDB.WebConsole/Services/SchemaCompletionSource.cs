using CamusDB.WebConsole.Models;
using Microsoft.JSInterop;

namespace CamusDB.WebConsole.Services;

/// <summary>A column as the editor's completion list shows it.</summary>
public sealed record CompletionColumn(string Name, string? Type, bool PrimaryKey);

/// <summary>
/// Gives the SQL editor the tables and the columns of the session database, for completion. The
/// editor script (<c>wwwroot/js/camus-sql.js</c>) calls the <see cref="JSInvokableAttribute"/>
/// methods through a <see cref="DotNetObjectReference"/> when the suggestion list opens.
///
/// <para>Tables are read once with <c>SHOW TABLES</c>. The columns of a table are read with
/// <c>SHOW COLUMNS FROM</c> the first time a statement names that table. Both stay cached until the
/// session changes (a new endpoint, database or user) or <see cref="Invalidate"/> is called after a
/// statement that can change the schema.</para>
/// </summary>
public sealed class SchemaCompletionSource : IDisposable
{
    private readonly SchemaExplorerService _schema;
    private readonly CamusSessionService _session;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IReadOnlyList<CompletionColumn>> _columns = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string>? _tables;
    private string? _cacheKey;
    private int _generation;

    public SchemaCompletionSource(SchemaExplorerService schema, CamusSessionService session)
    {
        _schema = schema;
        _session = session;
        _session.Changed += Invalidate;
    }

    [JSInvokable]
    public async Task<IReadOnlyList<string>> GetTables()
    {
        if (!_session.IsConnected)
            return [];

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ResetIfSessionMoved();
            if (_tables is not null)
                return _tables;

            try
            {
                _tables = await _schema.ListTablesAsync().ConfigureAwait(false);
            }
            catch
            {
                // Completion is a convenience: a failed SHOW TABLES (no privilege, lost connection)
                // gives no table names, and the next request tries again.
                return [];
            }

            return _tables;
        }
        finally
        {
            _gate.Release();
        }
    }

    [JSInvokable]
    public async Task<IReadOnlyList<CompletionColumn>> GetColumns(string table)
    {
        if (string.IsNullOrWhiteSpace(table) || !_session.IsConnected)
            return [];

        IReadOnlyList<string> tables = await GetTables().ConfigureAwait(false);
        string? known = tables.FirstOrDefault(t => string.Equals(t, table, StringComparison.OrdinalIgnoreCase));

        // Only a table that SHOW TABLES listed: an alias or a typo must not cost a SHOW COLUMNS.
        if (known is null)
            return [];

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ResetIfSessionMoved();
            if (_columns.TryGetValue(known, out IReadOnlyList<CompletionColumn>? cached))
                return cached;

            IReadOnlyList<ColumnSchemaInfo> columns;
            try
            {
                columns = await _schema.ListColumnsAsync(known).ConfigureAwait(false);
            }
            catch
            {
                return [];
            }

            List<CompletionColumn> result = columns
                .Select(c => new CompletionColumn(c.Name, c.Type, c.IsPrimaryKey))
                .ToList();
            _columns[known] = result;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Drops the cached names, so the next completion reads the schema again. It only moves a counter,
    /// so it never waits for a completion request that is in progress.
    /// </summary>
    public void Invalidate() => Interlocked.Increment(ref _generation);

    /// <summary>
    /// The session can switch database without raising <see cref="CamusSessionService.Changed"/>, so
    /// each request also compares the session against the one that filled the cache.
    /// </summary>
    private void ResetIfSessionMoved()
    {
        string key = $"{Volatile.Read(ref _generation)}\n{_session.Endpoint}\n{_session.Database}\n{_session.User}";
        if (string.Equals(key, _cacheKey, StringComparison.Ordinal))
            return;

        _tables = null;
        _columns.Clear();
        _cacheKey = key;
    }

    public void Dispose()
    {
        _session.Changed -= Invalidate;
    }
}
