using System.Globalization;
using CamusDB.WebConsole.Models;

namespace CamusDB.WebConsole.Services;

public enum PlanGraphNodeType
{
    /// <summary>An operator row of the plan.</summary>
    Operator,

    /// <summary>A join's right input that has no row of its own. Only the join's detail names it.</summary>
    Ghost,

    /// <summary>The rows that the statement returns.</summary>
    Result,
}

/// <summary>One box of the Graph view: an operator, the right input of a join, or the result.</summary>
public sealed class PlanGraphNode
{
    public required int Id { get; init; }

    public required PlanGraphNodeType Type { get; init; }

    /// <summary>The operator. Null for a ghost input and for the result.</summary>
    public QueryPlanNode? Plan { get; init; }

    public required string Title { get; init; }

    public string? Subtitle { get; init; }

    /// <summary>The CSS variable of the node's color.</summary>
    public required string Color { get; init; }

    /// <summary>The edges into this node, top to bottom: the main input first.</summary>
    public List<PlanGraphEdge> Inputs { get; } = [];

    public PlanGraphEdge? Output { get; set; }

    public int Column { get; set; }

    /// <summary>The left side of the bar.</summary>
    public double X { get; set; }

    /// <summary>The vertical center of the bar.</summary>
    public double Y { get; set; }

    public double BarHeight { get; set; }

    public double Top => Y - BarHeight / 2;

    public double Bottom => Y + BarHeight / 2;

    /// <summary>The rows that this node read and did not pass on, when that is more than it passed on.</summary>
    public PlanGraphDrain? Drain { get; set; }
}

/// <summary>A band from one node to the node that reads its rows.</summary>
public sealed class PlanGraphEdge
{
    public required int Id { get; init; }

    public required PlanGraphNode From { get; init; }

    public required PlanGraphNode To { get; init; }

    public long? ActualRows { get; init; }

    public long? EstimatedRows { get; init; }

    /// <summary>The rows that set the width: the measured count, else the estimate. Null when nothing gives a count.</summary>
    public long? Rows => ActualRows ?? EstimatedRows;

    /// <summary>What the band is for, when the node it enters has more than one input, such as <c>Right input</c>.</summary>
    public string? Role { get; init; }

    public double Width { get; set; }

    /// <summary>The width of the estimate, when the measured rows and the estimate are ten times apart or more.</summary>
    public double? EstimateWidth { get; set; }

    public double X1 { get; set; }

    public double Y1 { get; set; }

    public double X2 { get; set; }

    public double Y2 { get; set; }

    public string Path { get; set; } = "";

    /// <summary>The center of the band's label.</summary>
    public double LabelX { get; set; }

    public double LabelY { get; set; }
}

/// <summary>Rows that a node read and dropped: a red band that leaves the bottom of its bar.</summary>
public sealed record PlanGraphDrain(long Rows, double Width, string Path, double LabelX, double LabelY, string Title);

/// <summary>The laid-out Graph view of a plan.</summary>
public sealed class PlanGraph
{
    public required IReadOnlyList<PlanGraphNode> Nodes { get; init; }

    public required IReadOnlyList<PlanGraphEdge> Edges { get; init; }

    public required double Width { get; init; }

    public required double Height { get; init; }
}

/// <summary>
/// Lays out a plan from left to right: every table starts in the first column, and the rows flow to the
/// result on the right. A node's column is one more than the column of its furthest input, so a node
/// stands right of each node that feeds it. Each leaf takes a lane of its own, and a node with more than
/// one input sits halfway between its first and its last input.
///
/// <para>The width of a band follows the row count on a log scale (the rule of seqora.dev): 20 rows are
/// about 2 px and 12 million rows about 29 px. The bar of a node is as tall as the bands that touch it.</para>
/// </summary>
public static class QueryPlanGraphLayout
{
    public const double BarWidth = 12;
    public const double ColumnWidth = 180;
    public const double LaneHeight = 124;
    private const double Padding = 28;

    /// <summary>Room left of the first column, for the labels that center on its bars.</summary>
    private const double LeftSpace = 104;
    private const double TopSpace = 46;
    private const double BottomSpace = 52;
    private const double InputGap = 2;
    private const double ResultWidth = 70;
    private const double ResultHeight = 28;
    private const double GhostHeight = 20;

    public static PlanGraph Build(QueryPlanModel plan)
    {
        Builder builder = new();
        PlanGraphNode root = builder.Chain(plan.Nodes);
        PlanGraphNode result = builder.Add(PlanGraphNodeType.Result, null, "Result", null, "--camus-green");
        builder.Connect(root, result, root.Plan?.ActualRows, root.Plan?.EstimatedRows, null);

        Columns(result);
        double laneY = Padding + TopSpace;
        Lanes(result, ref laneY);

        foreach (PlanGraphNode node in builder.Nodes)
            node.X = LeftSpace + node.Column * ColumnWidth;

        foreach (PlanGraphEdge edge in builder.Edges)
            edge.Width = BandWidth(edge.Rows);

        foreach (PlanGraphNode node in builder.Nodes)
            Size(node, plan.IsAnalyze);

        foreach (PlanGraphNode node in builder.Nodes)
            RouteInputs(node);

        double width = builder.Nodes.Max(n => n.X + (n.Type == PlanGraphNodeType.Result ? ResultWidth : BarWidth)) + Padding + 90;
        double height = builder.Nodes.Max(n => n.Bottom + (n.Drain is null ? 0 : 40)) + BottomSpace + Padding;

        return new PlanGraph { Nodes = builder.Nodes, Edges = builder.Edges, Width = width, Height = height };
    }

    /// <summary><c>1.2 + 0.55 × log10(rows)²</c>, from 1.5 px to 34 px.</summary>
    public static double BandWidth(long? rows)
    {
        if (rows is not long r || r <= 0)
            return 1.5;

        double log = Math.Log10(r);
        return Math.Clamp(1.2 + 0.55 * log * log, 1.5, 34);
    }

    /// <summary>The CSS variable that colors an operator of <paramref name="kind"/>.</summary>
    public static string KindColor(PlanNodeKind kind) => kind switch
    {
        PlanNodeKind.Source => "--camus-accent",
        PlanNodeKind.Join => "--camus-kind-join",
        PlanNodeKind.Filter => "--camus-kind-filter",
        PlanNodeKind.Aggregate => "--camus-kind-aggregate",
        PlanNodeKind.Sort => "--camus-kind-sort",
        PlanNodeKind.Window => "--camus-kind-window",
        PlanNodeKind.Limit => "--camus-kind-limit",
        PlanNodeKind.Project => "--camus-green",
        PlanNodeKind.Exchange => "--camus-kind-exchange",
        PlanNodeKind.SetOperation => "--camus-kind-setop",
        PlanNodeKind.Lock => "--camus-kind-lock",
        _ => "--camus-muted",
    };

    private static int Columns(PlanGraphNode node)
    {
        node.Column = node.Inputs.Count == 0 ? 0 : node.Inputs.Max(e => Columns(e.From)) + 1;
        return node.Column;
    }

    private static void Lanes(PlanGraphNode node, ref double laneY)
    {
        if (node.Inputs.Count == 0)
        {
            node.Y = laneY;
            laneY += LaneHeight;
            return;
        }

        foreach (PlanGraphEdge input in node.Inputs)
            Lanes(input.From, ref laneY);

        node.Y = (node.Inputs[0].From.Y + node.Inputs[^1].From.Y) / 2;
    }

    /// <summary>The bar's height, and the drain of a node that dropped more rows than it passed on.</summary>
    private static void Size(PlanGraphNode node, bool analyze)
    {
        double inputs = node.Inputs.Sum(e => e.Width) + InputGap * Math.Max(node.Inputs.Count - 1, 0);
        double output = node.Output?.Width ?? 0;
        (long Rows, string Title)? dropped = node.Plan is QueryPlanNode plan ? Dropped(node, plan, analyze) : null;
        double drainWidth = dropped is null ? 0 : BandWidth(dropped.Value.Rows);

        // The output band leaves the middle of the bar, so the drain needs room under it.
        if (dropped is not null)
            output += 2 * (drainWidth + 4);

        node.BarHeight = node.Type switch
        {
            PlanGraphNodeType.Ghost => GhostHeight,
            PlanGraphNodeType.Result => ResultHeight,
            _ => Math.Max(inputs, output) + 12,
        };

        if (dropped is (long rows, string title))
        {
            double sx = node.X + BarWidth;
            double sy = node.Bottom - 6 - drainWidth / 2;
            double ex = sx + 44;
            double ey = sy + 40;
            string path = Invariant($"M{sx:0.#},{sy:0.#} C{sx + 26:0.#},{sy:0.#} {ex:0.#},{sy + 10:0.#} {ex:0.#},{ey:0.#}");
            node.Drain = new PlanGraphDrain(rows, drainWidth, path, ex, ey + 13, title);
        }
    }

    /// <summary>
    /// The rows that a node read and dropped, when it dropped more than it passed on. A scan applies its
    /// filter as it reads, so under EXPLAIN (ANALYZE) its rows read minus its rows are the rows the filter
    /// dropped. Any other node compares its main input with what it returns, but only for a filter: a
    /// join or a group makes rows of its own, so fewer rows out is not a drop.
    /// </summary>
    private static (long, string)? Dropped(PlanGraphNode node, QueryPlanNode plan, bool analyze)
    {
        long input;
        long output;

        if (plan.Kind == PlanNodeKind.Source && analyze && plan.RowsRead is long read && plan.ActualRows is long kept)
        {
            input = read;
            output = kept;
        }
        else if (plan.Kind == PlanNodeKind.Filter && node.Inputs.Count > 0
            && node.Inputs[0].Rows is long incoming && QueryPlanText.Rows(plan) is long passed)
        {
            input = incoming;
            output = passed;
        }
        else
        {
            return null;
        }

        long dropped = input - output;
        if (dropped < 10 || input <= 2 * output)
            return null;

        string estimate = plan.ActualRows is null ? " (estimate)" : "";
        return (dropped, $"Read {input:N0} rows and dropped {dropped:N0} of them{estimate}. {output:N0} rows went on.");
    }

    /// <summary>
    /// The path of each band into <paramref name="node"/>. A band leaves the middle of its node's bar, and
    /// the bands into a node stack along its bar from the top, in the order of its inputs. A band that
    /// spans more than one column stays in its lane until the last column, then curves.
    /// </summary>
    private static void RouteInputs(PlanGraphNode node)
    {
        double total = node.Inputs.Sum(e => e.Width) + InputGap * Math.Max(node.Inputs.Count - 1, 0);
        double y = node.Y - total / 2;

        foreach (PlanGraphEdge edge in node.Inputs)
        {
            PlanGraphNode from = edge.From;

            edge.X1 = from.X + BarWidth;
            edge.Y1 = from.Y;
            edge.X2 = node.X;
            edge.Y2 = y + edge.Width / 2;
            y += edge.Width + InputGap;

            double curveStart = Math.Max(edge.X1, edge.X2 - ColumnWidth + BarWidth);
            double bend = (edge.X2 - curveStart) * 0.5;
            string straight = curveStart > edge.X1 ? Invariant($" H{curveStart:0.#}") : "";
            edge.Path = Invariant($"M{edge.X1:0.#},{edge.Y1:0.#}{straight} C{curveStart + bend:0.#},{edge.Y1:0.#} {edge.X2 - bend:0.#},{edge.Y2:0.#} {edge.X2:0.#},{edge.Y2:0.#}");

            // The label sits on the straight part of a long band. On a short band it sits a quarter of the
            // way along the curve, near the node the rows leave: the middle of a curve can run under the
            // labels of the node it enters. B(t) of the curve at t = 0.25, with its control points
            // half-way along: x moves 30% of the way, y 16%.
            if (curveStart > edge.X1)
            {
                edge.LabelX = (edge.X1 + curveStart) / 2;
                edge.LabelY = edge.Y1;
            }
            else
            {
                edge.LabelX = edge.X1 + 0.3 * (edge.X2 - edge.X1) + 8;
                edge.LabelY = edge.Y1 + 0.15625 * (edge.Y2 - edge.Y1);
            }

            if (edge.ActualRows is long actual && edge.EstimatedRows is long estimated
                && Math.Max(actual, estimated) >= 100
                && Math.Max(actual, estimated) / (double)Math.Max(Math.Min(actual, estimated), 1) >= 10)
                edge.EstimateWidth = BandWidth(estimated);
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>Turns the chains of the plan model into nodes and edges.</summary>
    private sealed class Builder
    {
        public List<PlanGraphNode> Nodes { get; } = [];

        public List<PlanGraphEdge> Edges { get; } = [];

        public PlanGraphNode Add(PlanGraphNodeType type, QueryPlanNode? node, string title, string? subtitle, string color)
        {
            PlanGraphNode added = new() { Id = Nodes.Count, Type = type, Plan = node, Title = title, Subtitle = subtitle, Color = color };
            Nodes.Add(added);
            return added;
        }

        public void Connect(PlanGraphNode from, PlanGraphNode to, long? actual, long? estimated, string? role)
        {
            PlanGraphEdge edge = new() { Id = Edges.Count, From = from, To = to, ActualRows = actual, EstimatedRows = estimated, Role = role };
            Edges.Add(edge);
            to.Inputs.Add(edge);
            from.Output = edge;
        }

        /// <summary>The nodes of one chain in data-flow order. Returns the last one, which the chain feeds out of.</summary>
        public PlanGraphNode Chain(IReadOnlyList<QueryPlanNode> chain)
        {
            PlanGraphNode? previous = null;

            foreach (QueryPlanNode node in chain)
            {
                PlanGraphNode current = Add(PlanGraphNodeType.Operator, node, node.Operator, node.Target, KindColor(node.Kind));

                if (previous is not null)
                {
                    // A source estimates the rows before the filter that runs inside it, so under ANALYZE
                    // the rows it passes on compare with the filter's estimate.
                    QueryPlanNode input = previous.Plan!;
                    long? estimated = input.HasFoldedFilter && input.ActualRows is not null ? node.EstimatedRows : input.EstimatedRows;
                    Connect(previous, current, input.ActualRows, estimated, node.Kind == PlanNodeKind.Join ? "Left input" : null);
                }

                foreach (QueryPlanBranch branch in node.Branches)
                {
                    string role = QueryPlanText.OperandTitle(node, branch);

                    if (branch.Nodes.Count == 0)
                    {
                        Connect(Add(PlanGraphNodeType.Ghost, null, role, "no operator in the plan", KindColor(node.Kind)), current, null, null, role);
                        continue;
                    }

                    PlanGraphNode last = Chain(branch.Nodes);
                    Connect(last, current,
                        branch.Operand.ActualRows ?? last.Plan!.ActualRows,
                        branch.Operand.EstimatedRows ?? last.Plan!.EstimatedRows,
                        role);
                }

                foreach (IReadOnlyList<QueryPlanNode> side in node.SideBranches)
                {
                    PlanGraphNode last = Chain(side);
                    Connect(last, current, last.Plan!.ActualRows, last.Plan.EstimatedRows, node.SideInput ?? "Right input");
                }

                if (node.SideInput is not null && node.SideBranches.Count == 0)
                {
                    PlanGraphNode ghost = Add(PlanGraphNodeType.Ghost, null, node.SideInput, node.SideDetail, KindColor(node.Kind));
                    Connect(ghost, current, null, null, node.SideInput);
                }

                previous = current;
            }

            return previous ?? Add(PlanGraphNodeType.Ghost, null, "Empty plan", null, "--camus-muted");
        }
    }
}
