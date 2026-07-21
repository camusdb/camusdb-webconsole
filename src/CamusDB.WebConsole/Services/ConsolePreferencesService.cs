using CamusDB.WebConsole.Models;
using Microsoft.JSInterop;

namespace CamusDB.WebConsole.Services;

/// <summary>
/// Loads and saves console UI preferences in the browser's localStorage.
/// </summary>
public sealed class ConsolePreferencesService
{
    public const string StorageKey = "camusdb.webconsole.ui";

    private readonly IJSRuntime _js;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConsoleUiPreferences _prefs = new();
    private bool _loaded;
    private CancellationTokenSource? _tabsSaveCts;

    public ConsolePreferencesService(IJSRuntime js)
    {
        _js = js;
    }

    public ConsoleUiPreferences Current => _prefs;

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
                ConsoleUiPreferences? stored = await _js
                    .InvokeAsync<ConsoleUiPreferences?>("camusStorage.getJson", StorageKey)
                    .ConfigureAwait(false);

                if (stored is not null)
                    _prefs = stored;
            }
            catch (JSException)
            {
                // localStorage unavailable; keep defaults.
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

    public async Task SetDrawerWidthAsync(int width)
    {
        await EnsureLoadedAsync().ConfigureAwait(false);
        if (_prefs.DrawerWidth == width)
            return;

        _prefs.DrawerWidth = width;
        await PersistAsync().ConfigureAwait(false);
    }

    public async Task SetEditorHeightAsync(int height)
    {
        await EnsureLoadedAsync().ConfigureAwait(false);
        if (_prefs.EditorHeight == height)
            return;

        _prefs.EditorHeight = height;
        await PersistAsync().ConfigureAwait(false);
    }

    public async Task SetDatabaseAsync(string? database)
    {
        await EnsureLoadedAsync().ConfigureAwait(false);
        string? trimmed = string.IsNullOrWhiteSpace(database) ? null : database.Trim();
        if (string.Equals(_prefs.Database, trimmed, StringComparison.Ordinal))
            return;

        _prefs.Database = trimmed;
        await PersistAsync().ConfigureAwait(false);
    }

    public async Task SaveTabsAsync(
        IReadOnlyList<PersistedQueryTab> tabs,
        string activeTabId,
        int tabSeq,
        bool debounce = false)
    {
        await EnsureLoadedAsync().ConfigureAwait(false);

        _tabsSaveCts?.Cancel();
        _tabsSaveCts?.Dispose();
        _tabsSaveCts = null;

        if (debounce)
        {
            var cts = new CancellationTokenSource();
            _tabsSaveCts = cts;
            try
            {
                await Task.Delay(400, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        _prefs.Tabs = tabs.Select(t => new PersistedQueryTab
        {
            Id = t.Id,
            Title = t.Title,
            Sql = t.Sql ?? "",
        }).ToList();
        _prefs.ActiveTabId = activeTabId;
        _prefs.TabSeq = tabSeq;
        await PersistAsync().ConfigureAwait(false);
    }

    private async Task PersistAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("camusStorage.setJson", StorageKey, _prefs).ConfigureAwait(false);
        }
        catch (JSException)
        {
            // Ignore storage failures.
        }
        catch (InvalidOperationException)
        {
            // Ignore when the circuit is disposing.
        }
    }
}
