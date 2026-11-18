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

    /// <summary>
    /// How an EXPLAIN result opens: <c>table</c>, <c>steps</c> or <c>graph</c>. Null until the visitor
    /// picks one; then <see cref="PlanAsFlow"/> from an older console still decides.
    /// </summary>
    public string? PlanView { get; set; }

    /// <summary>The choice of an older console: true opened a plan as the step flow. Read only when <see cref="PlanView"/> is null.</summary>
    public bool PlanAsFlow { get; set; }

    /// <summary>The view that an EXPLAIN result opens in.</summary>
    public string EffectivePlanView => PlanView switch
    {
        PlanViews.Table or PlanViews.Steps or PlanViews.Graph => PlanView,
        _ => PlanAsFlow ? PlanViews.Steps : PlanViews.Table,
    };

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

/// <summary>The views of an EXPLAIN result in the results toolbar.</summary>
public static class PlanViews
{
    public const string Table = "table";
    public const string Steps = "steps";
    public const string Graph = "graph";
}
