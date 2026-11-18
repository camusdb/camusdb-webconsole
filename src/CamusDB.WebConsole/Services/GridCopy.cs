using System.Text;
using CamusDB.WebConsole.Models;

namespace CamusDB.WebConsole.Services;

/// <summary>The forms in which the results grid copies the cells that it selected.</summary>
public enum CopyFormat
{
    /// <summary>Tab-separated text, without the column names. A spreadsheet pastes it as cells.</summary>
    Text,

    /// <summary>Tab-separated text, with the column names as the first line.</summary>
    TextWithHeaders,

    /// <summary>CSV with a header line, as the CSV export writes it.</summary>
    Csv,

    /// <summary>An array of objects, as the JSON export writes it.</summary>
    Json,

    /// <summary>A Markdown table.</summary>
    Markdown,

    /// <summary>One <c>INSERT</c> statement for each row.</summary>
    SqlInsert,
}

/// <summary>
/// Writes the cells that the results grid selected as clipboard text. The grid gives the selected
/// columns and, for each selected row, the values of those columns.
/// </summary>
public static class GridCopy
{
    /// <summary>The table name of a copied <c>INSERT</c> when the result does not come from one table.</summary>
    public const string PlaceholderTable = "table_name";

    /// <summary>Reads the format name that <c>grid.js</c> sends.</summary>
    public static bool TryParseFormat(string? name, out CopyFormat format)
    {
        CopyFormat? parsed = name switch
        {
            "text" => CopyFormat.Text,
            "text-headers" => CopyFormat.TextWithHeaders,
            "csv" => CopyFormat.Csv,
            "json" => CopyFormat.Json,
            "markdown" => CopyFormat.Markdown,
            "sql-insert" => CopyFormat.SqlInsert,
            _ => null,
        };

        format = parsed ?? CopyFormat.Text;
        return parsed is not null;
    }

    /// <summary>The end of the message after a copy, such as " as CSV". Empty for plain text.</summary>
    public static string Describe(CopyFormat format) => format switch
    {
        CopyFormat.TextWithHeaders => " with headers",
        CopyFormat.Csv => " as CSV",
        CopyFormat.Json => " as JSON",
        CopyFormat.Markdown => " as Markdown",
        CopyFormat.SqlInsert => " as SQL INSERT",
        _ => "",
    };

    /// <param name="columns">The selected columns, in grid order.</param>
    /// <param name="rows">One array for each selected row, with one value for each selected column.</param>
    /// <param name="format">The form of the text.</param>
    /// <param name="table">The table of a data tab, for <see cref="CopyFormat.SqlInsert"/>. Null for any other result.</param>
    public static string Format(IReadOnlyList<QueryColumn> columns, IReadOnlyList<object?[]> rows, CopyFormat format, string? table)
    {
        return format switch
        {
            CopyFormat.Text => WriteText(columns, rows, headers: false),
            CopyFormat.TextWithHeaders => WriteText(columns, rows, headers: true),
            CopyFormat.Csv => Exported(columns, rows, ExportFormat.Csv),
            CopyFormat.Json => Exported(columns, rows, ExportFormat.Json),
            CopyFormat.Markdown => WriteMarkdown(columns, rows),
            CopyFormat.SqlInsert => WriteInserts(columns, rows, table),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    private static object? At(object?[] row, int index) => index < row.Length ? row[index] : null;

    /// <summary>
    /// The grid's text of each cell, with a tab between the cells and a line break between the rows.
    /// A cell that holds a tab or a line break, or that starts with a double quote, goes in double
    /// quotes, with each double quote doubled. Excel and Google Sheets write and read cells in this
    /// form.
    ///
    /// <para>One cell is the exception: its text goes in as it is, so that a paste into the editor
    /// gets the value without quotes.</para>
    /// </summary>
    private static string WriteText(IReadOnlyList<QueryColumn> columns, IReadOnlyList<object?[]> rows, bool headers)
    {
        if (!headers && rows.Count == 1 && columns.Count == 1)
            return CellText.Format(At(rows[0], 0));

        StringBuilder text = new();

        if (headers)
            AppendLine(text, columns.Select(c => c.Name));

        foreach (object?[] row in rows)
            AppendLine(text, Enumerable.Range(0, columns.Count).Select(i => CellText.Format(At(row, i))));

        return text.ToString();

        static void AppendLine(StringBuilder text, IEnumerable<string> fields)
        {
            if (text.Length > 0)
                text.Append('\n');

            bool first = true;
            foreach (string field in fields)
            {
                if (!first)
                    text.Append('\t');
                first = false;

                if (field.AsSpan().IndexOfAny('\t', '\n', '\r') >= 0 || field.StartsWith('"'))
                    text.Append('"').Append(field.Replace("\"", "\"\"")).Append('"');
                else
                    text.Append(field);
            }
        }
    }

    /// <summary>
    /// The text of the file that the export writes for these rows, without its last line break. A
    /// copy and an export of the same cells are the same text.
    /// </summary>
    private static string Exported(IReadOnlyList<QueryColumn> columns, IReadOnlyList<object?[]> rows, ExportFormat format)
    {
        ExportResult export = ExportService.ExportRows(columns, rows, format, "copy");
        return Encoding.UTF8.GetString(export.Content).TrimEnd('\r', '\n');
    }

    /// <summary>
    /// A GitHub-flavored Markdown table. A <c>|</c> in a cell is escaped, and a line break becomes
    /// <c>&lt;br&gt;</c>, because a table row is one line.
    /// </summary>
    private static string WriteMarkdown(IReadOnlyList<QueryColumn> columns, IReadOnlyList<object?[]> rows)
    {
        StringBuilder text = new();

        AppendRow(text, columns.Select(c => c.Name));
        AppendRow(text, columns.Select(_ => "---"));

        foreach (object?[] row in rows)
            AppendRow(text, Enumerable.Range(0, columns.Count).Select(i => CellText.Format(At(row, i))));

        return text.ToString().TrimEnd('\n');

        static void AppendRow(StringBuilder text, IEnumerable<string> cells)
        {
            text.Append('|');
            foreach (string cell in cells)
            {
                string escaped = cell
                    .Replace("|", "\\|")
                    .Replace("\r\n", "<br>")
                    .Replace("\n", "<br>")
                    .Replace("\r", "<br>");
                text.Append(' ').Append(escaped.Length == 0 ? " " : escaped).Append(" |");
            }

            text.Append('\n');
        }
    }

    /// <summary>
    /// One <c>INSERT</c> for each row, with the selected columns. The values are SQL literals, as the
    /// row edit dialog writes them: a <c>NUMERIC</c> is <c>NUMERIC '…'</c> and a <c>JSONB</c> is
    /// <c>JSONB '…'</c>.
    /// </summary>
    private static string WriteInserts(IReadOnlyList<QueryColumn> columns, IReadOnlyList<object?[]> rows, string? table)
    {
        string target = string.IsNullOrWhiteSpace(table) ? PlaceholderTable : SqlBuilder.QuoteIdent(table);
        string names = string.Join(", ", columns.Select(c => SqlBuilder.QuoteIdent(c.Name)));

        StringBuilder text = new();
        foreach (object?[] row in rows)
        {
            if (text.Length > 0)
                text.Append('\n');

            string values = string.Join(", ", Enumerable.Range(0, columns.Count).Select(i => SqlBuilder.FormatLiteral(At(row, i))));
            text.Append("INSERT INTO ").Append(target)
                .Append(" (").Append(names).Append(") VALUES (").Append(values).Append(");");
        }

        return text.ToString();
    }
}
