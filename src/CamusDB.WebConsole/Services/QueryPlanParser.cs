using System.Globalization;
using System.Text.RegularExpressions;
using CamusDB.WebConsole.Models;

namespace CamusDB.WebConsole.Services;

/// <summary>
/// Reads the rows of an EXPLAIN result set back into a <see cref="QueryPlanModel"/>.
///
/// <para>The input is the server's own rendering (ExplainExecutor and PlanRenderer): one row per
/// operator with the columns <c>stage</c>, <c>node</c>, <c>detail</c>, <c>estimated_rows</c> and
/// <c>estimated_cost</c>, plus the measured columns that EXPLAIN (ANALYZE) adds. After the operators
/// come rows that describe the whole plan (<c>plan-info</c>, <c>distribution</c>, <c>cache</c>), and
/// ANALYZE puts span rows after a gather (<c>gather-span</c>) or after a join that ran a broadcast
/// probe (<c>broadcast-span</c>). The parser keeps those apart, because they are not steps of the
/// flow.</para>
///
/// <para>A newer server adds the <c>depth</c> column: the level of each operator row in the plan tree,
/// parent first. With it, the parser builds the tree directly (see <see cref="ReadTree"/>). Without it,
/// the parser reads the tree from the row order, which works for the shapes that older servers
/// emit.</para>
/// </summary>
public static class QueryPlanParser
{
    private static readonly HashSet<string> NoteRows = new(StringComparer.OrdinalIgnoreCase)
    {
        "plan-info", "distribution", "cache",
    };

    private static readonly HashSet<string> SpanRows = new(StringComparer.OrdinalIgnoreCase)
    {
        "gather-span", "broadcast-span",
    };

    private const string SetOperationOperand = "operand";

    // A detail is "key=value, key=value". A value can hold an expression with commas, so each pattern
    // anchors its key at the start of the detail or after ", " and stops the value at the next comma.
    private static readonly Regex TableKey = DetailKey("table");
    private static readonly Regex IndexKey = DetailKey("index");
    private static readonly Regex AliasKey = DetailKey("alias");
    private static readonly Regex RightKey = DetailKey("right");
    private static readonly Regex InnerKey = DetailKey("inner");
    private static readonly Regex RightOrderKey = DetailKey("right-order");
    private static readonly Regex RightIndexKey = DetailKey("right-index");
    private static readonly Regex RightInListKey = DetailKey("right-in-list");
    private static readonly Regex CteKey = DetailKey("cte");
    private static readonly Regex LateralKey = DetailKey("lateral");

    // range-seek=<index>(<bounds>): only the index name, because the bounds are an expression.
    private static readonly Regex RangeSeekKey = new(@"(?:^|,\s)range-seek=(?<value>[^(,]+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // The plan-info row of EXPLAIN (ANALYZE) gives the bind and plan time.
    private static readonly Regex PlanningMsKey = DetailKey("planning-ms");

    // A reader of a WITH query that the statement computes once (PlanRenderer.RenderDerivedTableScan):
    // a derived-table-scan, or the set operation of a query that is one.
    private static readonly Regex ComputedOnceKey = DetailKey("computed_once");

    /// <summary>
    /// Returns the plan, or null when the columns are not the EXPLAIN shape or no operator row came
    /// back. A null result leaves the caller with the plain grid.
    /// </summary>
    public static QueryPlanModel? TryParse(IReadOnlyList<QueryColumn> columns, IReadOnlyList<object?[]> rows)
    {
        Dictionary<string, int> ordinals = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < columns.Count; i++)
            ordinals.TryAdd(columns[i].Name, i);

        if (!ordinals.ContainsKey("node") || !ordinals.ContainsKey("detail"))
            return null;

        List<QueryPlanNode> nodes = [];
        List<QueryPlanNote> notes = [];
        string stage = "";
        bool isAnalyze = ordinals.ContainsKey("actual_rows");
        bool hasDepth = ordinals.ContainsKey("depth");

        foreach (object?[] row in rows)
        {
            string name = Text(row, ordinals, "node") ?? "";
            string detail = Text(row, ordinals, "detail") ?? "";

            if (stage.Length == 0)
                stage = Text(row, ordinals, "stage") ?? "";

            if (NoteRows.Contains(name))
            {
                notes.Add(new QueryPlanNote(name, detail));
                continue;
            }

            if (SpanRows.Contains(name))
            {
                QueryPlanSpan span = new(detail, Long(row, ordinals, "actual_rows"), Long(row, ordinals, "rows_read"));
                if (nodes.Count > 0)
                    nodes[^1].Spans.Add(span);
                else
                    notes.Add(new QueryPlanNote(name, detail));
                continue;
            }

            if (name.Length == 0)
                continue;

            nodes.Add(CreateNode(name, detail, row, ordinals, hasDepth));
        }

        if (nodes.Count == 0)
            return null;

        List<QueryPlanNode>? flow = hasDepth ? ReadTree(nodes) : null;

        if (flow is not null)
        {
            // The depth column gave the tree.
        }
        else if (nodes.Any(n => n.Kind == PlanNodeKind.SetOperation))
        {
            int position = 0;
            flow = ReadChain(nodes, ref position, branch: false);
        }
        else
        {
            // A plan comes root first, so the source is the last row. The FROM-less shape of an older
            // server is the one exception: it lists constant-source, then project, then limit. The
            // source is the only node with no input, so whichever end holds it is where the flow starts.
            flow = nodes;
            if (!(flow[0].Kind == PlanNodeKind.Source && flow[^1].Kind != PlanNodeKind.Source))
                flow.Reverse();
        }

        MarkFoldedFilters(flow);

        return new QueryPlanModel
        {
            Stage = stage.Length == 0 ? (isAnalyze ? "analyze" : "physical") : stage,
            IsAnalyze = isAnalyze,
            Nodes = flow,
            Notes = notes,
            TotalEstimatedCost = QueryPlanModel.Flatten(flow).Sum(n => n.EstimatedCost ?? 0),
            // The root has the time of the statement. A derived table whose query the plan shows has
            // the time of that query too, so the first time in data-flow order is not the total.
            TotalTimeMs = flow[^1].ActualTimeMs ?? flow.Select(n => n.ActualTimeMs).LastOrDefault(t => t is not null),
            PlanningMs = notes
                .Where(n => n.Name.Equals("plan-info", StringComparison.OrdinalIgnoreCase))
                .Select(n => Value(PlanningMsKey, n.Detail))
                .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) ? ms : (double?)null)
                .FirstOrDefault(ms => ms is not null),
        };
    }

    /// <summary>
    /// Builds the plan from the <c>depth</c> column and returns the main chain in data-flow order, or null
    /// when an operator row has no depth or the rows do not form one tree.
    ///
    /// <para>The rows come parent first. Under each node the server walks its extra children first (the
    /// operands of a set operation, the derived table on the right side of a join, the plan of a derived
    /// table's query) and then its <c>Input</c>. So the main input of a node is its last child, and any
    /// child before it is a side input.</para>
    /// </summary>
    private static List<QueryPlanNode>? ReadTree(List<QueryPlanNode> rows)
    {
        if (rows.Any(n => n.Depth is null) || rows[0].Depth != 0)
            return null;

        List<TreeNode> roots = [];
        Stack<TreeNode> open = new();

        foreach (QueryPlanNode row in rows)
        {
            while (open.Count > 0 && open.Peek().Node.Depth >= row.Depth)
                open.Pop();

            TreeNode node = new(row);

            if (open.Count == 0)
                roots.Add(node);
            else
                open.Peek().Children.Add(node);

            open.Push(node);
        }

        return roots.Count == 1 ? ToChain(roots[0]) : null;
    }

    /// <summary>
    /// The chain that ends at <paramref name="top"/>, in data-flow order. It follows the main input of
    /// each node down to a node with none, and hangs the other children on the node they feed: the
    /// operands of a set operation as <see cref="QueryPlanNode.Branches"/>, every other child as one of
    /// its <see cref="QueryPlanNode.SideBranches"/>.
    /// </summary>
    private static List<QueryPlanNode> ToChain(TreeNode top)
    {
        List<QueryPlanNode> chain = [];
        TreeNode? current = top;

        while (current is not null)
        {
            QueryPlanNode node = current.Node;
            chain.Add(node);

            if (node.Kind == PlanNodeKind.SetOperation)
            {
                // A set operation reads its operands, so it is the start of its chain.
                foreach (TreeNode child in current.Children)
                {
                    if (!IsOperand(child.Node))
                    {
                        node.SideBranches.Add(ToChain(child));
                        continue;
                    }

                    List<QueryPlanNode> operand = child.Children.Count == 0 ? [] : ToChain(child.Children[^1]);
                    for (int i = 0; i < child.Children.Count - 1; i++)
                        operand[^1].SideBranches.Add(ToChain(child.Children[i]));

                    node.Branches.Add(new QueryPlanBranch(child.Node, operand));
                }

                break;
            }

            if (current.Children.Count == 0)
                break;

            for (int i = 0; i < current.Children.Count - 1; i++)
                node.SideBranches.Add(ToChain(current.Children[i]));

            current = current.Children[^1];
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>Marks each source that a filter reads right after it, in every chain of the plan.</summary>
    private static void MarkFoldedFilters(IReadOnlyList<QueryPlanNode> chain)
    {
        for (int i = 0; i < chain.Count; i++)
        {
            QueryPlanNode node = chain[i];
            node.HasFoldedFilter = node.Kind == PlanNodeKind.Source && i + 1 < chain.Count && chain[i + 1].Kind == PlanNodeKind.Filter;

            foreach (QueryPlanBranch branch in node.Branches)
                MarkFoldedFilters(branch.Nodes);

            foreach (IReadOnlyList<QueryPlanNode> side in node.SideBranches)
                MarkFoldedFilters(side);
        }
    }

    private sealed class TreeNode(QueryPlanNode node)
    {
        public QueryPlanNode Node { get; } = node;

        public List<TreeNode> Children { get; } = [];
    }

    /// <summary>
    /// Reads one chain from the rows, which come root first in a depth-first walk, and returns it in
    /// data-flow order. A set operation is followed by its <c>operand(left)</c> row and the left
    /// chain, then by its <c>operand(right)</c> row and the right chain.
    ///
    /// <para>The chain of an operand ends at its source, or at a nested set operation once both of
    /// its operands are read. The main chain reads every row that is left, so a shape the parser
    /// does not know still shows each of its operators.</para>
    /// </summary>
    private static List<QueryPlanNode> ReadChain(List<QueryPlanNode> rows, ref int position, bool branch)
    {
        List<QueryPlanNode> chain = [];

        while (position < rows.Count)
        {
            QueryPlanNode node = rows[position];

            // In an operand's chain, the next operand row belongs to the set operation above it.
            if (branch && IsOperand(node))
                break;

            position++;
            chain.Add(node);

            if (node.Kind == PlanNodeKind.SetOperation)
            {
                ReadBranch(rows, ref position, node, "left");
                ReadBranch(rows, ref position, node, "right");
            }

            if (branch && node.Kind is PlanNodeKind.Source or PlanNodeKind.SetOperation)
                break;
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// Reads one operand of <paramref name="setOperation"/> when the next row is its operand row.
    /// The side must match, so a nested set operation never takes the right operand of the one
    /// above it.
    /// </summary>
    private static void ReadBranch(List<QueryPlanNode> rows, ref int position, QueryPlanNode setOperation, string side)
    {
        if (position >= rows.Count || !IsOperand(rows[position]))
            return;

        QueryPlanNode operand = rows[position];
        if (!string.Equals(QueryPlanBranch.SideOf(operand), side, StringComparison.OrdinalIgnoreCase))
            return;

        position++;
        setOperation.Branches.Add(new QueryPlanBranch(operand, ReadChain(rows, ref position, branch: true)));
    }

    private static bool IsOperand(QueryPlanNode node) =>
        node.Operator.Equals(SetOperationOperand, StringComparison.OrdinalIgnoreCase);

    private static QueryPlanNode CreateNode(string name, string detail, object?[] row, Dictionary<string, int> ordinals, bool hasDepth)
    {
        PlanNodeKind kind = Classify(name);
        (string? side, string? sideDetail) = DescribeSideInput(name, detail);

        return new QueryPlanNode
        {
            Operator = name,
            Detail = detail,
            Kind = kind,
            Target = DescribeTarget(name, detail),
            SideInput = side,
            SideDetail = sideDetail,
            EstimatedRows = Long(row, ordinals, "estimated_rows"),
            EstimatedCost = Double(row, ordinals, "estimated_cost"),
            ActualRows = Long(row, ordinals, "actual_rows"),
            RowsRead = Long(row, ordinals, "rows_read"),
            ActualTimeMs = Double(row, ordinals, "actual_time_ms"),
            KvLookups = Long(row, ordinals, "kv_lookups"),
            KvScanEntries = Long(row, ordinals, "kv_scan_entries"),
            ComputedOnce = Value(ComputedOnceKey, detail),
            Depth = hasDepth && Long(row, ordinals, "depth") is long depth ? (int)depth : null,
            EstimateTrace = Text(row, ordinals, "estimate_trace") is { Length: > 0 } trace ? trace : null,
        };
    }

    internal static PlanNodeKind Classify(string name) => name.ToLowerInvariant() switch
    {
        "table-scan" or "index-lookup" or "index-range-scan" or "index-in-list"
            or "derived-table-scan" or "function-scan" or "constant-source" or "gin-scan"
            or "values" or "work-table-scan" => PlanNodeKind.Source,
        "nested-loop-join" or "index-nested-loop-join" or "hash-join" or "merge-join"
            or "semi-join" or "anti-join" or "null-aware-anti-join" => PlanNodeKind.Join,
        "filter" or "having-filter" => PlanNodeKind.Filter,
        "aggregate" or "distinct" => PlanNodeKind.Aggregate,
        "sort" or "topk" => PlanNodeKind.Sort,
        "window" => PlanNodeKind.Window,
        "limit" => PlanNodeKind.Limit,
        "project" => PlanNodeKind.Project,
        "gather" => PlanNodeKind.Exchange,
        "union" or "intersect" or "except" or "recursive-union" => PlanNodeKind.SetOperation,
        "lock_rows" => PlanNodeKind.Lock,
        _ => PlanNodeKind.Other,
    };

    private static string? DescribeTarget(string name, string detail) => name.ToLowerInvariant() switch
    {
        "table-scan" => Value(TableKey, detail),
        "index-lookup" or "index-range-scan" or "index-in-list" or "gin-scan" => Value(IndexKey, detail),
        "derived-table-scan" or "values" => Value(AliasKey, detail),
        "function-scan" => FunctionCall(detail),
        "work-table-scan" or "recursive-union" => Value(CteKey, detail),
        _ => null,
    };

    /// <summary>
    /// The call of a function in FROM. The detail is <c>generate_series(1, 10), alias=g, column=g</c>:
    /// the call comes first and can hold commas, so it ends where the alias key starts. The one-row
    /// source of a FROM-less operand is <c>*single_row*()</c>, which is not a call a developer wrote,
    /// so it is left out.
    /// </summary>
    private static string? FunctionCall(string detail)
    {
        int alias = detail.IndexOf(", alias=", StringComparison.Ordinal);
        string call = (alias < 0 ? detail : detail[..alias]).Trim();
        return call.Length == 0 || call.StartsWith('*') ? null : call;
    }

    /// <summary>
    /// Names a join's second input from the detail text. Only the facts each operator states without
    /// doubt are used. A hash join's <c>build=</c> is not one of them: when the left side builds, it
    /// names the left input, so it says nothing about which alias the right input is.
    /// </summary>
    private static (string? Side, string? Detail) DescribeSideInput(string name, string detail)
    {
        switch (name.ToLowerInvariant())
        {
            case "nested-loop-join":
                return ("Right input", Facts(
                    Value(RightKey, detail),
                    Lateral(detail),
                    Value(RangeSeekKey, detail) is { } seekIndex ? $"seeks index {seekIndex} for each left row" : null,
                    RightAccess(detail)));

            case "index-nested-loop-join":
                return ("Right input", Value(IndexKey, detail) is { } lookupIndex ? $"looked up in index {lookupIndex}" : null);

            case "hash-join":
                return ("Right input", RightAccess(detail));

            case "merge-join":
                return ("Right input", Value(RightOrderKey, detail) switch
                {
                    null => null,
                    "sort" => "sorted before the merge",
                    string order => $"read in order from {order.Replace("forced-index=", "index ", StringComparison.Ordinal)}",
                });

            case "semi-join":
            case "anti-join":
            case "null-aware-anti-join":
                {
                    string? inner = Value(InnerKey, detail);
                    string? index = Value(IndexKey, detail);
                    return ("Subquery", inner is null ? null : index is null ? inner : $"{inner} via index {index}");
                }

            default:
                return (null, null);
        }
    }

    /// <summary>How a join reads its right table when the planner gave it an access path of its own.</summary>
    private static string? RightAccess(string detail) =>
        Value(RightIndexKey, detail) is { } index ? $"read through index {index}"
        : Value(RightInListKey, detail) is { } inList ? $"IN list through index {inList}"
        : null;

    /// <summary>A LATERAL right side runs for each left row; a body of expressions only runs no subquery.</summary>
    private static string? Lateral(string detail)
    {
        if (Value(LateralKey, detail) is { } value && value.StartsWith("expressions(", StringComparison.Ordinal))
            return $"LATERAL {value}, computed for each left row";

        return Regex.IsMatch(detail, @"(?:^|,\s)lateral(?:,|$)", RegexOptions.CultureInvariant)
            ? "LATERAL: runs for each left row"
            : null;
    }

    private static string? Facts(params string?[] facts)
    {
        string text = string.Join(" · ", facts.Where(f => f is not null));
        return text.Length == 0 ? null : text;
    }

    private static Regex DetailKey(string key) => new(
        $@"(?:^|,\s){Regex.Escape(key)}=(?<value>[^,]+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string? Value(Regex key, string detail)
    {
        Match match = key.Match(detail);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static object? Cell(object?[] row, Dictionary<string, int> ordinals, string column) =>
        ordinals.TryGetValue(column, out int index) && index < row.Length ? row[index] : null;

    private static string? Text(object?[] row, Dictionary<string, int> ordinals, string column) =>
        Cell(row, ordinals, column) is { } value
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : null;

    private static long? Long(object?[] row, Dictionary<string, int> ordinals, string column) =>
        Cell(row, ordinals, column) switch
        {
            null => null,
            long l => l,
            int i => i,
            double d when double.IsFinite(d) => (long)d,
            decimal m => (long)m,
            object other when long.TryParse(Convert.ToString(other, CultureInfo.InvariantCulture),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
            _ => null,
        };

    private static double? Double(object?[] row, Dictionary<string, int> ordinals, string column) =>
        Cell(row, ordinals, column) switch
        {
            null => null,
            double d => d,
            float f => f,
            long l => l,
            int i => i,
            decimal m => (double)m,
            object other when double.TryParse(Convert.ToString(other, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
            _ => null,
        };
}
