using System.Globalization;
using System.Text;
using System.Text.Json;
using CamusDB.WebConsole.Models;
using CsvHelper;

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
    ///
    /// <para>The file is written once, as UTF-8 bytes, row by row. A string, or a list of row objects
    /// to serialize, would hold the whole file two or three times over in the circuit's memory.</para>
    /// </summary>
    public static ExportResult ExportRows(
        IReadOnlyList<QueryColumn> columns,
        IReadOnlyCollection<object?[]> rows,
        ExportFormat format,
        string baseName)
    {
        using MemoryStream output = new();

        switch (format)
        {
            case ExportFormat.Csv:
                WriteCsv(output, columns, rows);
                break;
            case ExportFormat.Json:
                WriteJson(output, columns, rows);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }

        byte[] content = output.ToArray();

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

    private static void WriteCsv(Stream output, IReadOnlyList<QueryColumn> columns, IEnumerable<object?[]> rows)
    {
        var options = new CsvWriterOptions
        {
            NewLine = Environment.NewLine,
            FormatProvider = CultureInfo.InvariantCulture,
            // Quote a value with a leading or trailing space, as CsvHelper 33 did, so that a reader that
            // trims unquoted fields keeps the spaces.
            Quoting = CsvQuoting.WhenNeededOrWhitespace,
        };

        // No BOM: the file is the same bytes that the browser made of a string before.
        using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        using var csv = CsvWriter.Create(writer, options, leaveOpen: true);

        foreach (QueryColumn column in columns)
            csv.WriteField(column.Name);
        csv.EndRecord();

        foreach (object?[] row in rows)
        {
            for (int i = 0; i < columns.Count; i++)
            {
                object? value = i < row.Length ? row[i] : null;
                csv.WriteField(FormatCell(value));
            }

            csv.EndRecord();
        }

        // The writers buffer their output and only disposal flushes it, which happens after the caller
        // has read the stream.
        csv.Flush();
        writer.Flush();
    }

    private static void WriteJson(Stream output, IReadOnlyList<QueryColumn> columns, IEnumerable<object?[]> rows)
    {
        string[] keys = JsonKeys(columns);

        using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        json.WriteStartArray();

        foreach (object?[] row in rows)
        {
            json.WriteStartObject();
            for (int i = 0; i < columns.Count; i++)
            {
                object? value = i < row.Length ? row[i] : null;
                json.WritePropertyName(keys[i]);

                // Declared as object, so the serializer writes the value by its runtime type — as it
                // did for the values of a Dictionary<string, object?>.
                JsonSerializer.Serialize(json, NormalizeJsonValue(value));
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.Flush();
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

    /// <summary>The file, as UTF-8 bytes.</summary>
    public byte[] Content { get; init; } = [];

    public string FileName { get; init; } = "export.txt";

    public string MimeType { get; init; } = "text/plain";

    public int RowsExported { get; init; }

    public bool Truncated { get; init; }

    public int MaxRows { get; init; }
}
