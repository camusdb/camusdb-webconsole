namespace CamusDB.WebConsole.Models;

/// <summary>The family of a plan operator. The flow view colours and describes a node by it.</summary>
public enum PlanNodeKind
{
    Source,
    Join,
    Filter,
    Aggregate,
    Sort,
    Window,
    Limit,
    Project,
    Exchange,
    SetOperation,
    Lock,
    Other,
}

/// <summary>
/// A query plan read back from the rows of an EXPLAIN result set.
///
/// <para>The server walks a plan that is mostly one chain: every operator has a single <c>Input</c>,
/// and it emits the chain root first. A join is a link in that chain whose left input is the next row.
/// Its right input usually gets no row of its own, so the join's detail text is the only place that
/// names it (see <see cref="QueryPlanNode.SideInput"/>). A derived table on the right side of a join
/// is the exception: it has a row under the join, with the plan of its query under it (see
/// <see cref="QueryPlanNode.SideBranches"/>).</para>
///
/// <para>A set operation (UNION, INTERSECT, EXCEPT, and the recursive UNION of WITH RECURSIVE) is the
/// source of its chain, and each of its two operands is a chain of its own under an <c>operand</c> row
/// (see <see cref="QueryPlanNode.Branches"/>).</para>
///
/// <para>A server that emits the <c>depth</c> column gives the tree directly. For an older server, the
/// parser reads the tree from the row order.</para>
/// </summary>
public sealed class QueryPlanModel
{
    /// <summary><c>physical</c>, <c>logical</c> or <c>analyze</c>, as the <c>stage</c> column gives it.</summary>
    public required string Stage { get; init; }

    /// <summary>True when the rows carry measured counters, which only EXPLAIN (ANALYZE) emits.</summary>
    public bool IsAnalyze { get; init; }

    /// <summary>The operators in data-flow order: the first one reads the source, the last one feeds the result.</summary>
    public required IReadOnlyList<QueryPlanNode> Nodes { get; init; }

    /// <summary>Every operator of the plan: the main chain and, under each set operation, the chains of its operands.</summary>
    public IEnumerable<QueryPlanNode> AllNodes => Flatten(Nodes);

    /// <summary>The trailing rows that describe the whole plan, such as <c>plan-info</c> and <c>cache</c>.</summary>
    public IReadOnlyList<QueryPlanNote> Notes { get; init; } = [];

    /// <summary>The sum of every node's own estimated cost. A node's cost is not cumulative.</summary>
    public double TotalEstimatedCost { get; init; }

    /// <summary>Measured wall-clock time of the whole plan. Only EXPLAIN (ANALYZE) reports it.</summary>
    public double? TotalTimeMs { get; init; }

    /// <summary>The time to bind and plan the statement, from <c>planning-ms=</c> on the <c>plan-info</c> row of EXPLAIN (ANALYZE).</summary>
    public double? PlanningMs { get; init; }

    /// <summary>The last operator of the main chain: the one that feeds the result.</summary>
    public QueryPlanNode Root => Nodes[^1];

    internal static IEnumerable<QueryPlanNode> Flatten(IEnumerable<QueryPlanNode> nodes) =>
        nodes.SelectMany(n => n.SideBranches.SelectMany(Flatten)
            .Concat(n.Branches.SelectMany(b => Flatten(b.Nodes)))
            .Prepend(n));
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

    /// <summary>The level of the operator in the plan tree, from the <c>depth</c> column. The root is 0. Null for an older server.</summary>
    public int? Depth { get; init; }

    /// <summary>How the planner made the row estimate, as the JSON of the <c>estimate_trace</c> column of EXPLAIN (ANALYZE).</summary>
    public string? EstimateTrace { get; init; }

    /// <summary>
    /// True for a source right under a filter. The server runs that filter inside the scan, so under
    /// EXPLAIN (ANALYZE) the source's rows are the rows that pass the filter, while its estimate is the
    /// rows before the filter. Its rows read are the count to compare with the estimate.
    /// </summary>
    public bool HasFoldedFilter { get; internal set; }

    /// <summary>
    /// The name of the WITH query that this operator reads, when the statement computes that query
    /// once: the first reader keeps its rows, and every other reader reads the kept rows. Null for
    /// every other operator.
    /// </summary>
    public string? ComputedOnce { get; init; }

    /// <summary>
    /// The span rows that EXPLAIN (ANALYZE) emits after a gather (<c>gather-span</c>) or after a join
    /// that ran a broadcast probe (<c>broadcast-span</c>), one per span.
    /// </summary>
    public List<QueryPlanSpan> Spans { get; } = [];

    /// <summary>The left and the right operand of a set operation. Empty for any other operator.</summary>
    public List<QueryPlanBranch> Branches { get; } = [];

    /// <summary>
    /// The inputs that have rows of their own besides the main input, each a chain in data-flow order.
    /// For a join, this is the derived table on its right side, with the plan of its query. Only a
    /// server that emits the <c>depth</c> column gives them.
    /// </summary>
    public List<IReadOnlyList<QueryPlanNode>> SideBranches { get; } = [];
}

/// <summary>
/// One operand of a set operation: the <c>operand</c> row, which under EXPLAIN (ANALYZE) counts the
/// rows the operand returned, and the operand's own chain in data-flow order.
/// </summary>
public sealed record QueryPlanBranch(QueryPlanNode Operand, IReadOnlyList<QueryPlanNode> Nodes)
{
    /// <summary><c>left</c> or <c>right</c>, as the detail of the operand row gives it.</summary>
    public string Side => SideOf(Operand);

    internal static string SideOf(QueryPlanNode operand) => operand.Detail.Split(',')[0].Trim();
}

public sealed record QueryPlanSpan(string Detail, long? ActualRows, long? RowsRead);

public sealed record QueryPlanNote(string Name, string Detail);
