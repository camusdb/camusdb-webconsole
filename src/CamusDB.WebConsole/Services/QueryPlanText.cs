using System.Globalization;
using System.Text.RegularExpressions;
using CamusDB.WebConsole.Models;

namespace CamusDB.WebConsole.Services;

/// <summary>
/// The words and numbers that the plan views (Steps and Graph) show for an operator. Both views use
/// these, so an operator reads the same in each one.
/// </summary>
public static class QueryPlanText
{
    public static string KindLabel(PlanNodeKind kind) => kind switch
    {
        PlanNodeKind.Source => "Source",
        PlanNodeKind.Join => "Join",
        PlanNodeKind.Filter => "Filter",
        PlanNodeKind.Aggregate => "Aggregate",
        PlanNodeKind.Sort => "Sort",
        PlanNodeKind.Window => "Window",
        PlanNodeKind.Limit => "Limit",
        PlanNodeKind.Project => "Output",
        PlanNodeKind.Exchange => "Exchange",
        PlanNodeKind.SetOperation => "Set operation",
        PlanNodeKind.Lock => "Lock",
        _ => "Step",
    };

    /// <summary>The CSS class that gives an operator the color of its family.</summary>
    public static string KindClass(PlanNodeKind kind) => "kind-" + kind.ToString().ToLowerInvariant();

    public static string? Describe(string op) => op.ToLowerInvariant() switch
    {
        "table-scan" => "Reads every row of the table.",
        "index-lookup" => "Finds the rows for one exact index key.",
        "index-range-scan" => "Reads a range of keys from an index.",
        "index-in-list" => "Looks up each value of an IN list in an index.",
        "gin-scan" => "Finds the candidate rows for a JSONB condition in a GIN index. The filter above checks each one.",
        "derived-table-scan" => "Reads the rows of a subquery in FROM or of a WITH query.",
        "function-scan" => "Makes rows with a function in FROM, without reading a table.",
        "values" => "Makes the rows of a VALUES list, without reading a table.",
        "work-table-scan" => "Reads the rows that the previous step of the recursive WITH query made.",
        "constant-source" => "Makes one row without reading a table.",
        "filter" => "Keeps only the rows that match the condition.",
        "having-filter" => "Keeps only the groups that match HAVING.",
        "aggregate" => "Groups the rows and computes the aggregates.",
        "distinct" => "Removes duplicate rows.",
        "sort" => "Sorts all the rows.",
        "topk" => "Keeps only the first k rows in sort order, without a full sort.",
        "window" => "Sorts the rows once for each window and computes the window functions, one partition at a time.",
        "limit" => "Stops after the row limit.",
        "project" => "Computes the output columns.",
        "nested-loop-join" => "Compares each left row with each right row.",
        "index-nested-loop-join" => "For each left row, finds the matching right rows through an index.",
        "hash-join" => "Puts one input in a hash table, then probes it with the other input.",
        "merge-join" => "Merges two inputs that are sorted on the join keys.",
        "semi-join" => "Keeps each row that has a match in the subquery (IN, EXISTS).",
        "anti-join" => "Keeps each row that has no match in the subquery (NOT IN, NOT EXISTS).",
        "null-aware-anti-join" => "Keeps each row that has no match in the subquery, with the NULL rules of NOT IN.",
        "gather" => "Collects the rows from the partitions of the cluster.",
        "lock_rows" => "Locks each row before it passes the row on (FOR UPDATE).",
        "union" => "Returns the rows of both operands. Without ALL, it removes duplicate rows.",
        "intersect" => "Returns the rows that are in both operands.",
        "except" => "Returns the rows of the left operand that are not in the right operand.",
        "recursive-union" => "Runs the non-recursive term one time, then the recursive term one time for each step, until a step makes no new rows. Without ALL, it removes duplicate rows.",
        _ => null,
    };

    public static string NoteLabel(string name) => name.ToLowerInvariant() switch
    {
        "plan-info" => "Plan info",
        "distribution" => "Distribution",
        "cache" => "Result cache",
        _ => name,
    };

    /// <summary>
    /// The title of an operand of <paramref name="setOperation"/>. The two operands of a recursive UNION
    /// are its terms: the left one runs one time, and the right one runs for each step.
    /// </summary>
    public static string OperandTitle(QueryPlanNode setOperation, QueryPlanBranch branch)
    {
        bool recursive = setOperation.Operator.Equals("recursive-union", StringComparison.OrdinalIgnoreCase);

        return branch.Side.ToLowerInvariant() switch
        {
            "left" when recursive => "Non-recursive term",
            "right" when recursive => "Recursive term",
            "" => "Operand",
            string side => $"{char.ToUpperInvariant(side[0])}{side[1..]} operand",
        };
    }

    /// <summary>
    /// ANALYZE reports the storage counters on every operator, and they are 0 on each one that
    /// does not touch storage. A zero is news only on a source that reads storage (a table or an
    /// index), so the other operators leave it out.
    /// </summary>
    public static long? Counter(QueryPlanNode node, long? value) =>
        value is long v && (v > 0 || ReadsStorage(node)) ? v : null;

    private static bool ReadsStorage(QueryPlanNode node) => node.Operator.ToLowerInvariant() is
        "table-scan" or "index-lookup" or "index-range-scan" or "index-in-list" or "gin-scan";

    /// <summary>
    /// The measured count to compare with the estimate. A source whose filter runs inside it is estimated
    /// before the filter, so its rows read are the count; for any other node, its rows.
    /// </summary>
    public static long? MeasuredForEstimate(QueryPlanNode node) =>
        node.HasFoldedFilter && node.RowsRead is long read ? read : node.ActualRows;

    /// <summary>The rows that an operator returns: the measured count under ANALYZE, else the estimate.</summary>
    public static long? Rows(QueryPlanNode node) => node.ActualRows ?? node.EstimatedRows;

    /// <summary>
    /// The factor between the measured rows and the estimate, when they are ten times apart or more.
    /// Small counts are left out: 2 rows against an estimate of 25 is not worth a developer's attention.
    /// </summary>
    public static double? EstimateFactor(QueryPlanNode node)
    {
        if (MeasuredForEstimate(node) is not long actual || node.EstimatedRows is not long estimated)
            return null;

        long high = Math.Max(actual, estimated);
        long low = Math.Max(Math.Min(actual, estimated), 1);
        if (high < 100 || high / (double)low < 10)
            return null;

        return high / (double)low;
    }

    /// <summary>A label when the measured rows are ten times the estimate or a tenth of it.</summary>
    public static string? EstimateMiss(QueryPlanNode node) =>
        EstimateFactor(node) is double factor ? $"Estimate off {factor:N0}×" : null;

    public static string FormatCost(double cost) => cost.ToString(cost >= 100 ? "N0" : "#,0.##");

    public static string FormatMs(double ms) =>
        ms < 1 ? $"{ms * 1000:0.##}μs" : ms < 1000 ? $"{ms:0.##}ms" : $"{ms / 1000:0.###}s";

    public static string FormatShare(double share) =>
        share < 0.005 ? "<1%" : $"{share * 100:0}%";

    /// <summary>A short row count for a diagram label: 950, 12.4K, 3.1M.</summary>
    public static string FormatCompact(long value) => Math.Abs(value) switch
    {
        < 1_000 => value.ToString(CultureInfo.InvariantCulture),
        < 10_000 => (value / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        < 1_000_000 => (value / 1_000d).ToString("0", CultureInfo.InvariantCulture) + "K",
        < 10_000_000 => (value / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        < 1_000_000_000 => (value / 1_000_000d).ToString("0", CultureInfo.InvariantCulture) + "M",
        _ => (value / 1_000_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "B",
    };

    public static string RowsLabel(long rows) => $"{rows:N0} row{(rows == 1 ? "" : "s")}";

    // A detail is "key=value, key=value". A value can hold commas (an expression, a list), so a new
    // pair starts only at ", " followed by a key and "=".
    private static readonly Regex PairStart = new(@",\s(?=[a-z][a-z0-9_-]*=)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // A window detail separates its groups with "; " and the parts of a group with a space:
    // "partition=[grp] order=[id] functions=[…]; partition=[…] …".
    private static readonly Regex WindowPairStart = new(@"(?:;\s|\s)(?=[a-z][a-z0-9_-]*=)", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Pair = new(@"^(?<key>[a-z][a-z0-9_-]*)=(?<value>.*)$", RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// The detail text as key and value pairs, in the server's order. A part that is not
    /// <c>key=value</c> (such as <c>all</c> in <c>union(all)</c>) has no key.
    /// </summary>
    public static IReadOnlyList<(string? Key, string Value)> DetailPairs(QueryPlanNode node)
    {
        string detail = node.Detail;
        if (detail.Length == 0)
            return [];

        List<(string? Key, string Value)> pairs = [];
        Regex start = node.Kind == PlanNodeKind.Window ? WindowPairStart : PairStart;

        foreach (string part in start.Split(detail))
        {
            Match match = Pair.Match(part.Trim());
            pairs.Add(match.Success ? (match.Groups["key"].Value, match.Groups["value"].Value) : (null, part.Trim()));
        }

        return pairs;
    }
}
