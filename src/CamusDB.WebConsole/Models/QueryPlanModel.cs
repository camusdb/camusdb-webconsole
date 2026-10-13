namespace CamusDB.WebConsole.Models;

/// <summary>The family of a plan operator. The flow view colours and describes a node by it.</summary>
public enum PlanNodeKind
{
    Source,
    Join,
    Filter,
    Aggregate,
    Sort,
    Limit,
    Project,
    Exchange,
    Other,
}

/// <summary>
/// A query plan read back from the rows of an EXPLAIN result set.
///
/// <para>The server walks a plan that is one chain: every operator has a single <c>Input</c>, and
/// it emits the chain root first. A join is a link in that chain whose left input is the next row;
/// its right input never gets a row of its own, so the join's detail text is the only place that
/// names it (see <see cref="QueryPlanNode.SideInput"/>).</para>
/// </summary>
public sealed class QueryPlanModel
{
    /// <summary><c>physical</c>, <c>logical</c> or <c>analyze</c>, as the <c>stage</c> column gives it.</summary>
    public required string Stage { get; init; }

    /// <summary>True when the rows carry measured counters, which only EXPLAIN (ANALYZE) emits.</summary>
    public bool IsAnalyze { get; init; }

    /// <summary>The operators in data-flow order: the first one reads the source, the last one feeds the result.</summary>
    public required IReadOnlyList<QueryPlanNode> Nodes { get; init; }

    /// <summary>The trailing rows that describe the whole plan, such as <c>plan-info</c> and <c>cache</c>.</summary>
    public IReadOnlyList<QueryPlanNote> Notes { get; init; } = [];

    /// <summary>The sum of every node's own estimated cost. A node's cost is not cumulative.</summary>
    public double TotalEstimatedCost { get; init; }

    /// <summary>Measured wall-clock time of the whole plan. Only EXPLAIN (ANALYZE) reports it.</summary>
    public double? TotalTimeMs { get; init; }
}

public sealed class QueryPlanNode
{
    /// <summary>The canonical operator name, such as <c>table-scan</c> or <c>hash-join</c>.</summary>
    public required string Operator { get; init; }

    /// <summary>The text that was inside the operator's parentheses. Empty when it had none.</summary>
    public string Detail { get; init; } = "";

    public PlanNodeKind Kind { get; init; }

    /// <summary>The table, index or alias this operator works on, when the detail names one.</summary>
    public string? Target { get; init; }

    /// <summary>
    /// The name of a join's second input, which has no row of its own in the plan: <c>Right input</c>,
    /// or <c>Subquery</c> for a semi-join or an anti-join. Null for an operator that is not a join.
    /// </summary>
    public string? SideInput { get; init; }

    /// <summary>What the detail text tells about the second input, such as its alias or its index.</summary>
    public string? SideDetail { get; init; }

    public long? EstimatedRows { get; init; }

    public double? EstimatedCost { get; init; }

    public long? ActualRows { get; init; }

    public long? RowsRead { get; init; }

    /// <summary>Wall-clock time of the whole plan. The server reports it on the root node only.</summary>
    public double? ActualTimeMs { get; init; }

    public long? KvLookups { get; init; }

    public long? KvScanEntries { get; init; }

    /// <summary>The <c>gather-span</c> rows that EXPLAIN (ANALYZE) emits after a gather, one per partition span.</summary>
    public List<QueryPlanSpan> Spans { get; } = [];
}

public sealed record QueryPlanSpan(string Detail, long? ActualRows, long? RowsRead);

public sealed record QueryPlanNote(string Name, string Detail);
