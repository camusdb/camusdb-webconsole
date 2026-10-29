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
/// ANALYZE puts a <c>gather-span</c> row after each gather. The parser keeps those apart, because
/// they are not steps of the flow.</para>
/// </summary>
public static class QueryPlanParser
{
    private static readonly HashSet<string> NoteRows = new(StringComparer.OrdinalIgnoreCase)
    {
        "plan-info", "distribution", "cache",
    };

    private const string GatherSpanRow = "gather-span";

    private const string SetOperationOperand = "operand";

    // A detail is "key=value, key=value". A value can hold an expression with commas, so each pattern
    // anchors its key at the start of the detail or after ", " and stops the value at the next comma.
    private static readonly Regex TableKey = DetailKey("table");
    private static readonly Regex IndexKey = DetailKey("index");
    private static readonly Regex AliasKey = DetailKey("alias");
    private static readonly Regex RightKey = DetailKey("right");
    private static readonly Regex InnerKey = DetailKey("inner");
    private static readonly Regex RightOrderKey = DetailKey("right-order");

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

            if (name.Equals(GatherSpanRow, StringComparison.OrdinalIgnoreCase))
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

            nodes.Add(CreateNode(name, detail, row, ordinals));
        }

        if (nodes.Count == 0)
            return null;

        // A set operation (union, intersect, except) lists its operands after it, each one an
        // operand(left) or operand(right) row followed by that operand's own plan. The flow draws one
        // chain of inputs and cannot show two, so such a plan stays in the plain grid.
        if (nodes.Any(n => n.Operator.Equals(SetOperationOperand, StringComparison.OrdinalIgnoreCase)))
            return null;

        // A plan comes root first, so the source is the last row. The FROM-less shape is the one
        // exception: it lists constant-source, then project, then limit. The source is the only node
        // with no input, so whichever end holds it is where the flow starts.
        if (!(nodes[0].Kind == PlanNodeKind.Source && nodes[^1].Kind != PlanNodeKind.Source))
            nodes.Reverse();

        return new QueryPlanModel
        {
            Stage = stage.Length == 0 ? (isAnalyze ? "analyze" : "physical") : stage,
            IsAnalyze = isAnalyze,
            Nodes = nodes,
            Notes = notes,
            TotalEstimatedCost = nodes.Sum(n => n.EstimatedCost ?? 0),
            TotalTimeMs = nodes.Select(n => n.ActualTimeMs).FirstOrDefault(t => t is not null),
        };
    }

    private static QueryPlanNode CreateNode(string name, string detail, object?[] row, Dictionary<string, int> ordinals)
    {
        PlanNodeKind kind = Classify(name);
        (string? side, string? sideDetail) = DescribeSideInput(name, detail);

        return new QueryPlanNode
        {
            Operator = name,
            Detail = detail,
            Kind = kind,
            Target = kind == PlanNodeKind.Source ? DescribeTarget(name, detail) : null,
            SideInput = side,
            SideDetail = sideDetail,
            EstimatedRows = Long(row, ordinals, "estimated_rows"),
            EstimatedCost = Double(row, ordinals, "estimated_cost"),
            ActualRows = Long(row, ordinals, "actual_rows"),
            RowsRead = Long(row, ordinals, "rows_read"),
            ActualTimeMs = Double(row, ordinals, "actual_time_ms"),
            KvLookups = Long(row, ordinals, "kv_lookups"),
            KvScanEntries = Long(row, ordinals, "kv_scan_entries"),
        };
    }

    internal static PlanNodeKind Classify(string name) => name.ToLowerInvariant() switch
    {
        "table-scan" or "index-lookup" or "index-range-scan" or "index-in-list"
            or "derived-table-scan" or "function-scan" or "constant-source" => PlanNodeKind.Source,
        "nested-loop-join" or "index-nested-loop-join" or "hash-join" or "merge-join"
            or "semi-join" or "anti-join" or "null-aware-anti-join" => PlanNodeKind.Join,
        "filter" or "having-filter" => PlanNodeKind.Filter,
        "aggregate" or "distinct" => PlanNodeKind.Aggregate,
        "sort" or "topk" => PlanNodeKind.Sort,
        "limit" => PlanNodeKind.Limit,
        "project" => PlanNodeKind.Project,
        "gather" => PlanNodeKind.Exchange,
        _ => PlanNodeKind.Other,
    };

    private static string? DescribeTarget(string name, string detail) => name.ToLowerInvariant() switch
    {
        "table-scan" => Value(TableKey, detail),
        "index-lookup" or "index-range-scan" or "index-in-list" => Value(IndexKey, detail),
        "derived-table-scan" => Value(AliasKey, detail),
        "function-scan" => FunctionCall(detail),
        _ => null,
    };

    /// <summary>
    /// The call of a function in FROM. The detail is <c>generate_series(1, 10), alias=g, column=g</c>:
    /// the call comes first and can hold commas, so it ends where the alias key starts.
    /// </summary>
    private static string? FunctionCall(string detail)
    {
        int alias = detail.IndexOf(", alias=", StringComparison.Ordinal);
        string call = (alias < 0 ? detail : detail[..alias]).Trim();
        return call.Length == 0 ? null : call;
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
                return ("Right input", Value(RightKey, detail));

            case "index-nested-loop-join":
                return ("Right input", Value(IndexKey, detail) is { } lookupIndex ? $"looked up in index {lookupIndex}" : null);

            case "hash-join":
                return ("Right input", null);

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
