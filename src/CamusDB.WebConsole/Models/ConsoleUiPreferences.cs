namespace CamusDB.WebConsole.Models;

/// <summary>
/// Browser-persisted console UI state (panel sizes, database, SQL tabs).
/// </summary>
public sealed class ConsoleUiPreferences
{
    public int? DrawerWidth { get; set; }

    public int? EditorHeight { get; set; }

    public string? Database { get; set; }

    /// <summary>
    /// Last user the console authenticated as, so the Configure dialog can prefill it. The password is
    /// deliberately never stored — the browser would keep it in plaintext localStorage.
    /// </summary>
    public string? User { get; set; }

    public string? ActiveTabId { get; set; }

    public int TabSeq { get; set; }

    public List<PersistedQueryTab> Tabs { get; set; } = [];
}

public sealed class PersistedQueryTab
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "SQL";

    public string Sql { get; set; } = "";
}
