using System.Globalization;
using System.Text.Json;
using CamusDB.WebConsole.Models;
using CsvHelper;
using CsvHelper.Configuration;

namespace CamusDB.WebConsole.Services;

public sealed class ExportService
{
    private readonly QueryExecutionService _queries;
    private readonly CamusSessionService _session;

    public ExportService(QueryExecutionService queries, CamusSessionService session)
    {
        _queries = queries;
        _session = session;
    }

    public async Task<ExportResult> ExportTableAsync(
        string table,
        ExportFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        string sql = SqlBuilder.BuildSelectAll(table, _session.MaxRows);
        QueryResultModel result = await _queries.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return new ExportResult
            {
                Success = false,
                ErrorMessage = result.ErrorMessage ?? "Export query failed.",
            };
        }

        if (!result.IsResultSet)
        {
            return new ExportResult
            {
                Success = false,
                ErrorMessage = "Expected a result set for export.",
            };
        }

        ExportResult export = ExportRows(result.Columns, result.Rows, format, table);
        return new ExportResult
        {
            Success = true,
            Content = export.Content,
            FileName = export.FileName,
            MimeType = export.MimeType,
            RowsExported = export.RowsExported,
            Truncated = result.Truncated,
            MaxRows = result.MaxRows,
        };
    }

    /// <summary>
    /// Writes rows that the console already holds (a result in the grid) to CSV or JSON. No query
    /// runs. The file is called <c>{baseName}.csv</c> or <c>{baseName}.json</c>.
    /// </summary>
    public static ExportResult ExportRows(
        IReadOnlyList<QueryColumn> columns,
        IReadOnlyCollection<object?[]> rows,
        ExportFormat format,
        string baseName)
    {
        string content = format switch
        {
            ExportFormat.Csv => ToCsv(columns, rows),
            ExportFormat.Json => ToJson(columns, rows),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

        string ext = format == ExportFormat.Csv ? "csv" : "json";
        string mime = format == ExportFormat.Csv ? "text/csv" : "application/json";

        return new ExportResult
        {
            Success = true,
            Content = content,
            FileName = $"{baseName}.{ext}",
            MimeType = mime,
            RowsExported = rows.Count,
        };
    }

    private static string ToCsv(IReadOnlyList<QueryColumn> columns, IEnumerable<object?[]> rows)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            NewLine = Environment.NewLine,
        };

        using var writer = new StringWriter();
        using var csv = new CsvWriter(writer, config);

        foreach (QueryColumn column in columns)
            csv.WriteField(column.Name);
        csv.NextRecord();

        foreach (object?[] row in rows)
        {
            for (int i = 0; i < columns.Count; i++)
            {
                object? value = i < row.Length ? row[i] : null;
                csv.WriteField(FormatCell(value));
            }

            csv.NextRecord();
        }

        return writer.ToString();
    }

    private static string ToJson(IReadOnlyList<QueryColumn> columns, IEnumerable<object?[]> rows)
    {
        string[] keys = JsonKeys(columns);
        var objects = new List<Dictionary<string, object?>>();
        foreach (object?[] row in rows)
        {
            var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 0; i < columns.Count; i++)
            {
                object? value = i < row.Length ? row[i] : null;
                obj[keys[i]] = NormalizeJsonValue(value);
            }

            objects.Add(obj);
        }

        return JsonSerializer.Serialize(objects, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// One JSON key for each column. A query can return two columns with one name
    /// (<c>SELECT a.id, b.id</c>), and one object cannot hold both, so the second one becomes
    /// <c>id_2</c>, the third <c>id_3</c>, and so on.
    /// </summary>
    private static string[] JsonKeys(IReadOnlyList<QueryColumn> columns)
    {
        string[] keys = new string[columns.Count];
        HashSet<string> used = new(StringComparer.Ordinal);

        for (int i = 0; i < columns.Count; i++)
        {
            string key = columns[i].Name;
            for (int n = 2; !used.Add(key); n++)
                key = $"{columns[i].Name}_{n}";
            keys[i] = key;
        }

        return keys;
    }

    private static object? NormalizeJsonValue(object? value) => value switch
    {
        null => null,
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToHexString(bytes),
        Guid g => g.ToString("D"),
        // A JSON string, as the server sends it: a JSON number is a double to most readers, and a
        // NUMERIC has 38 digits.
        NumericText n => n.Text,
        _ => value,
    };

    private static string FormatCell(object? value)
    {
        if (value is null)
            return "";

        return value switch
        {
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            byte[] bytes => Convert.ToHexString(bytes),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };
    }
}

public enum ExportFormat
{
    Csv,
    Json,
}

public sealed class ExportResult
{
    public bool Success { get; init; }

    public string? ErrorMessage { get; init; }

    public string Content { get; init; } = "";

    public string FileName { get; init; } = "export.txt";

    public string MimeType { get; init; } = "text/plain";

    public int RowsExported { get; init; }

    public bool Truncated { get; init; }

    public int MaxRows { get; init; }
}
