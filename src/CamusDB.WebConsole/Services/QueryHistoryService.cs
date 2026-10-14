using CamusDB.WebConsole.Models;
using Microsoft.JSInterop;

namespace CamusDB.WebConsole.Services;

/// <summary>
/// Keeps the statements run from the query tabs, newest first, in the browser's localStorage. The
/// history is per browser, like the open tabs, and it holds at most <see cref="MaxEntries"/> entries.
/// </summary>
public sealed class QueryHistoryService
{
    public const string StorageKey = "camusdb.webconsole.history";

    public const int MaxEntries = 200;

    /// <summary>A longer statement (a pasted data dump, for example) is not recorded.</summary>
    public const int MaxSqlLength = 50_000;

    private readonly IJSRuntime _js;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<QueryHistoryEntry> _entries = [];
    private bool _loaded;

    public QueryHistoryService(IJSRuntime js)
    {
        _js = js;
    }

    public IReadOnlyList<QueryHistoryEntry> Entries => _entries;

    public async Task EnsureLoadedAsync()
    {
        if (_loaded)
            return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_loaded)
                return;

            try
            {
                List<QueryHistoryEntry>? stored = await _js
                    .InvokeAsync<List<QueryHistoryEntry>?>("camusStorage.getJson", StorageKey)
                    .ConfigureAwait(false);

                if (stored is not null)
                    _entries = stored.Where(e => !string.IsNullOrWhiteSpace(e.Sql)).Take(MaxEntries).ToList();
            }
            catch (JSException)
            {
                // localStorage unavailable or the stored value is not a list; start empty.
            }
            catch (InvalidOperationException)
            {
                // JS interop not ready yet.
            }

            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Records one statement. When the newest entry has the same SQL and database, that entry is
    /// updated instead, so a statement run again and again takes one line.
    /// </summary>
    public async Task AddAsync(string sql, string? database, QueryResultModel result)
    {
        if (string.IsNullOrWhiteSpace(sql) || sql.Length > MaxSqlLength)
            return;

        await EnsureLoadedAsync().ConfigureAwait(false);

        var entry = new QueryHistoryEntry
        {
            Sql = SqlScript.RedactSecrets(sql.Trim()),
            Database = database,
            ExecutedAt = DateTimeOffset.UtcNow,
            DurationMs = Math.Round(result.TotalDuration.TotalMilliseconds, 3),
            Success = result.Success,
            Rows = result.IsResultSet ? result.RowsReturned : result.RowsAffected,
            Error = result.Success ? null : result.ErrorMessage,
        };

        if (_entries.Count > 0
            && string.Equals(_entries[0].Sql, entry.Sql, StringComparison.Ordinal)
            && string.Equals(_entries[0].Database, entry.Database, StringComparison.Ordinal))
        {
            _entries[0] = entry;
        }
        else
        {
            _entries.Insert(0, entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        }

        await PersistAsync().ConfigureAwait(false);
    }

    public async Task ClearAsync()
    {
        await EnsureLoadedAsync().ConfigureAwait(false);
        _entries = [];
        await PersistAsync().ConfigureAwait(false);
    }

    private async Task PersistAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("camusStorage.setJson", StorageKey, _entries).ConfigureAwait(false);
        }
        catch (JSException)
        {
            // Ignore storage failures (quota, private mode).
        }
        catch (InvalidOperationException)
        {
            // Ignore when the circuit is disposing.
        }
    }
}
