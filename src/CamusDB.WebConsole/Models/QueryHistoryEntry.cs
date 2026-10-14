namespace CamusDB.WebConsole.Models;

/// <summary>
/// One statement that the console ran from a query tab. <c>Sql</c> has its passwords replaced (see
/// <c>SqlScript.RedactSecrets</c>), because the history is kept in the browser's localStorage.
/// </summary>
public sealed class QueryHistoryEntry
{
    public string Sql { get; set; } = "";

    public string? Database { get; set; }

    public DateTimeOffset ExecutedAt { get; set; }

    public double DurationMs { get; set; }

    public bool Success { get; set; }

    /// <summary>The rows returned for a result set, or the rows affected for each other statement.</summary>
    public int? Rows { get; set; }

    public string? Error { get; set; }
}
