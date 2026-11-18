using System.Globalization;
using System.Text.RegularExpressions;
using CamusDB.WebConsole.Models;

namespace CamusDB.WebConsole.Services;

public enum PlanFindingSeverity
{
    Warning,
    Notice,
    Info,
}

/// <summary>One fact about a plan that is worth a developer's attention, on the operator it is about.</summary>
public sealed record PlanFinding(QueryPlanNode Node, string Title, string Text, PlanFindingSeverity Severity);

/// <summary>
/// The rules that the Graph view runs over a plan. Each rule uses only what the EXPLAIN rows state, and
/// most of them need the measured counters of EXPLAIN (ANALYZE). The findings come most important first.
/// </summary>
public static class QueryPlanFindings
{
    /// <summary>A full scan is a finding when it reads at least this many rows…</summary>
    private const long FullScanRows = 10_000;

    /// <summary>…and keeps this share of them or less.</summary>
    private const double FullScanKeptShare = 0.2;

    /// <summary>A nested-loop join without an index is a finding from this many left rows.</summary>
    private const long NestedLoopLeftRows = 1_000;

    /// <summary>One operator with this share of the estimated cost is a finding.</summary>
    private const double HighCostShare = 0.5;

    private static readonly Regex RanKey = new(@"(?:^|,\s)ran=(?<value>[^,]+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SpilledKey = new(@"\bspilled=(?<value>\d+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<PlanFinding> Find(QueryPlanModel plan)
    {
        Dictionary<QueryPlanNode, QueryPlanNode> consumers = Consumers(plan);
        Dictionary<QueryPlanNode, QueryPlanNode> mainInputs = MainInputs(plan);
        List<(PlanFinding Finding, double Weight)> found = [];

        foreach (QueryPlanNode node in plan.AllNodes)
        {
            if (Ran(node) is PlanFinding ran)
                found.Add((ran, 3e12));

            if (Spilled(node) is PlanFinding spilled)
                found.Add((spilled, 2e12));

            if (FullScan(node, consumers.GetValueOrDefault(node)) is (PlanFinding scan, long read))
                found.Add((scan, 1e12 + read));

            if (QueryPlanText.EstimateFactor(node) is double factor)
            {
                found.Add((new PlanFinding(node, $"Estimate off {factor:N0}×",
                    $"The planner expected {node.EstimatedRows:N0} rows, and {node.Operator} {(node.HasFoldedFilter ? "read" : "returned")} {QueryPlanText.MeasuredForEstimate(node):N0}. "
                    + "A plan made from a wrong estimate can pick a bad join order or a bad join method. "
                    + "ANALYZE TABLE gives the planner fresh statistics.",
                    PlanFindingSeverity.Notice), 1e9 + factor));
            }

            if (NestedLoop(node, mainInputs.GetValueOrDefault(node)) is (PlanFinding loop, long left))
                found.Add((loop, 1e6 + left));
        }

        if (HighCost(plan) is PlanFinding cost)
            found.Add((cost, 0));

        return found
            .OrderBy(f => f.Finding.Severity)
            .ThenByDescending(f => f.Weight)
            .Select(f => f.Finding)
            .ToList();
    }

    /// <summary>A join that ran another way than its plan says, which only EXPLAIN (ANALYZE) reports.</summary>
    private static PlanFinding? Ran(QueryPlanNode node)
    {
        if (RanKey.Match(node.Detail) is not { Success: true } match)
            return null;

        return match.Groups["value"].Value.Trim() switch
        {
            "grace-hash" => new PlanFinding(node, "Hash join spilled to disk",
                "The hash table was larger than the spill threshold, so the join ran as a Grace hash join over spill files.",
                PlanFindingSeverity.Warning),
            "nested-loop-fallback" => new PlanFinding(node, "Hash join fell back to a nested loop",
                "The hash table was larger than the build limit and spilling is off, so the join compared each left row with each right row.",
                PlanFindingSeverity.Warning),
            "nested-loop-rescan" => new PlanFinding(node, "Right side read for each left row",
                "The nested-loop join read its right side again for every left row, instead of one time.",
                PlanFindingSeverity.Warning),
            _ => null,
        };
    }

    /// <summary>A window operator whose partition buffers went to disk (<c>spilled=</c> above zero).</summary>
    private static PlanFinding? Spilled(QueryPlanNode node)
    {
        if (node.Kind != PlanNodeKind.Window
            || SpilledKey.Match(node.Detail) is not { Success: true } match
            || !long.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long spilled)
            || spilled == 0)
            return null;

        return new PlanFinding(node, "Window spilled to disk",
            $"{spilled:N0} partition buffer{(spilled == 1 ? "" : "s")} went to disk, because the rows of a partition did not fit in memory.",
            PlanFindingSeverity.Warning);
    }

    /// <summary>
    /// A full table scan that keeps a small share of the rows it reads. The scan applies its filter as it
    /// reads, so its own row count is the rows it keeps; a filter right above it keeps fewer still.
    /// </summary>
    private static (PlanFinding, long)? FullScan(QueryPlanNode node, QueryPlanNode? consumer)
    {
        if (!node.Operator.Equals("table-scan", StringComparison.OrdinalIgnoreCase))
            return null;

        QueryPlanNode? filter = consumer?.Kind == PlanNodeKind.Filter ? consumer : null;
        long read;
        long kept;

        if (node.RowsRead is long rowsRead && node.ActualRows is long actual)
        {
            read = rowsRead;
            kept = filter?.ActualRows is long filtered ? Math.Min(filtered, actual) : actual;
        }
        else if (node.EstimatedRows is long estimated && filter?.EstimatedRows is long estimatedKept)
        {
            read = estimated;
            kept = estimatedKept;
        }
        else
        {
            return null;
        }

        if (read < FullScanRows || kept > read * FullScanKeptShare)
            return null;

        string table = node.Target ?? "the table";
        string share = QueryPlanText.FormatShare(read == 0 ? 0 : kept / (double)read);
        string verb = node.RowsRead is null ? "is expected to read" : "reads";

        return (new PlanFinding(node, $"Full scan of {table}",
            $"The scan {verb} {read:N0} rows and keeps {kept:N0} ({share}). "
            + "An index on the filtered columns lets the server read only the rows that it keeps.",
            PlanFindingSeverity.Notice), read);
    }

    /// <summary>
    /// A nested-loop join that has no index, range seek or IN list for its right side, over many left rows:
    /// it compares each left row with each right row.
    /// </summary>
    private static (PlanFinding, long)? NestedLoop(QueryPlanNode node, QueryPlanNode? leftInput)
    {
        if (!node.Operator.Equals("nested-loop-join", StringComparison.OrdinalIgnoreCase)
            || node.Detail.Contains("range-seek=", StringComparison.Ordinal)
            || node.Detail.Contains("right-index=", StringComparison.Ordinal)
            || node.Detail.Contains("right-in-list=", StringComparison.Ordinal)
            || leftInput is null
            || QueryPlanText.Rows(leftInput) is not long left
            || left < NestedLoopLeftRows)
            return null;

        return (new PlanFinding(node, "Nested loop without an index",
            $"The join compares each of {left:N0} left rows with each right row. "
            + "An index on the join column of the right table lets the planner find the matching rows directly.",
            PlanFindingSeverity.Notice), left);
    }

    private static PlanFinding? HighCost(QueryPlanModel plan)
    {
        List<QueryPlanNode> costed = plan.AllNodes.Where(n => n.EstimatedCost > 0).ToList();
        if (costed.Count < 2 || plan.TotalEstimatedCost <= 0)
            return null;

        QueryPlanNode top = costed.MaxBy(n => n.EstimatedCost!.Value)!;
        double share = top.EstimatedCost!.Value / plan.TotalEstimatedCost;
        if (share < HighCostShare)
            return null;

        string what = top.Target is null ? top.Operator : $"{top.Operator} on {top.Target}";
        return new PlanFinding(top, "Most of the cost",
            $"{what} has {QueryPlanText.FormatShare(share)} of the plan's estimated cost.",
            PlanFindingSeverity.Info);
    }

    /// <summary>The operator that reads the rows of each operator. The root has none.</summary>
    public static Dictionary<QueryPlanNode, QueryPlanNode> Consumers(QueryPlanModel plan)
    {
        Dictionary<QueryPlanNode, QueryPlanNode> consumers = new(ReferenceEqualityComparer.Instance);
        AddChain(plan.Nodes, null);
        return consumers;

        void AddChain(IReadOnlyList<QueryPlanNode> chain, QueryPlanNode? end)
        {
            for (int i = 0; i < chain.Count; i++)
            {
                QueryPlanNode node = chain[i];
                QueryPlanNode? consumer = i + 1 < chain.Count ? chain[i + 1] : end;
                if (consumer is not null)
                    consumers[node] = consumer;

                foreach (QueryPlanBranch branch in node.Branches)
                    AddChain(branch.Nodes, node);

                foreach (IReadOnlyList<QueryPlanNode> side in node.SideBranches)
                    AddChain(side, node);
            }
        }
    }

    /// <summary>The main input of each operator: the operator before it in its chain.</summary>
    private static Dictionary<QueryPlanNode, QueryPlanNode> MainInputs(QueryPlanModel plan)
    {
        Dictionary<QueryPlanNode, QueryPlanNode> inputs = new(ReferenceEqualityComparer.Instance);
        AddChain(plan.Nodes);
        return inputs;

        void AddChain(IReadOnlyList<QueryPlanNode> chain)
        {
            for (int i = 0; i < chain.Count; i++)
            {
                if (i > 0)
                    inputs[chain[i]] = chain[i - 1];

                foreach (QueryPlanBranch branch in chain[i].Branches)
                    AddChain(branch.Nodes);

                foreach (IReadOnlyList<QueryPlanNode> side in chain[i].SideBranches)
                    AddChain(side);
            }
        }
    }
}
