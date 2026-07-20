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

        string content = format switch
        {
            ExportFormat.Csv => ToCsv(result),
            ExportFormat.Json => ToJson(result),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

        string ext = format == ExportFormat.Csv ? "csv" : "json";
        string mime = format == ExportFormat.Csv ? "text/csv" : "application/json";

        return new ExportResult
        {
            Success = true,
            Content = content,
            FileName = $"{table}.{ext}",
            MimeType = mime,
            RowsExported = result.RowsReturned,
            Truncated = result.Truncated,
            MaxRows = result.MaxRows,
        };
    }

    private static string ToCsv(QueryResultModel result)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            NewLine = Environment.NewLine,
        };

        using var writer = new StringWriter();
        using var csv = new CsvWriter(writer, config);

        foreach (QueryColumn column in result.Columns)
            csv.WriteField(column.Name);
        csv.NextRecord();

        foreach (object?[] row in result.Rows)
        {
            for (int i = 0; i < result.Columns.Count; i++)
            {
                object? value = i < row.Length ? row[i] : null;
                csv.WriteField(FormatCell(value));
            }

            csv.NextRecord();
        }

        return writer.ToString();
    }

    private static string ToJson(QueryResultModel result)
    {
        var rows = new List<Dictionary<string, object?>>(result.Rows.Count);
        foreach (object?[] row in result.Rows)
        {
            var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 0; i < result.Columns.Count; i++)
            {
                object? value = i < row.Length ? row[i] : null;
                obj[result.Columns[i].Name] = NormalizeJsonValue(value);
            }

            rows.Add(obj);
        }

        return JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
    }

    private static object? NormalizeJsonValue(object? value) => value switch
    {
        null => null,
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToHexString(bytes),
        Guid g => g.ToString("D"),
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
