using System.Globalization;
using System.Text;
using CamusDB.WebConsole.Models;

namespace CamusDB.WebConsole.Services;

public static class SqlBuilder
{
    public static readonly string[] ColumnTypes =
    [
        "OID",
        "INT64",
        "INT",
        "FLOAT64",
        "DOUBLE",
        "STRING",
        "BOOL",
        "UUID",
    ];

    public static string QuoteIdent(string ident)
    {
        if (string.IsNullOrEmpty(ident))
            return ident;

        if (ident.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            return ident;

        return "\"" + ident.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public static string FormatLiteral(object? value)
    {
        if (value is null or DBNull)
            return "NULL";

        return value switch
        {
            bool b => b ? "TRUE" : "FALSE",
            sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal
                => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
            DateTime dt => $"'{dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}'",
            DateOnly d => $"'{d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'",
            TimeOnly t => $"'{t.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}'",
            Guid g => $"'{g:D}'",
            byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
            _ => QuoteString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""),
        };
    }

    public static string FormatLiteralFromText(string? text, string? columnType, bool isNull)
    {
        if (isNull || text is null)
            return "NULL";

        string type = columnType ?? "";
        if (IsBoolType(type))
        {
            if (bool.TryParse(text, out bool b))
                return b ? "TRUE" : "FALSE";
            if (text is "1" or "0")
                return text == "1" ? "TRUE" : "FALSE";
            return QuoteString(text);
        }

        if (IsNumericType(type))
        {
            if (IsIntegerType(type))
            {
                string trimmed = text.Trim();
                return IsValidIntegerLiteral(trimmed) ? trimmed : QuoteString(text);
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                return text.Trim();
            return QuoteString(text);
        }

        return QuoteString(text);
    }

    public static string BuildSelectAll(string table, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        return $"SELECT * FROM {QuoteIdent(table)}\nLIMIT {Math.Max(1, limit)}";
    }

    public static string BuildDropTable(string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        return $"DROP TABLE {QuoteIdent(table)}";
    }

    public static string BuildDropDatabase(string database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        return $"DROP DATABASE {QuoteIdent(database)}";
    }

    public static string BuildCreateTable(
        string table,
        IReadOnlyList<(string Name, string Type, bool NotNull, bool PrimaryKey)> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        if (columns.Count == 0)
            throw new ArgumentException("At least one column is required.", nameof(columns));

        StringBuilder sb = new();
        sb.Append("CREATE TABLE ").Append(QuoteIdent(table)).Append(" (");

        for (int i = 0; i < columns.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");

            var col = columns[i];
            sb.Append(QuoteIdent(col.Name)).Append(' ').Append(col.Type.Trim());
            if (col.NotNull)
                sb.Append(" NOT NULL");
        }

        List<string> pk = columns.Where(c => c.PrimaryKey).Select(c => QuoteIdent(c.Name)).ToList();
        if (pk.Count > 0)
            sb.Append(", PRIMARY KEY (").Append(string.Join(", ", pk)).Append(')');

        sb.Append(')');
        return sb.ToString();
    }

    public static string BuildCreateIndex(
        string indexName,
        string table,
        IReadOnlyList<string> columns,
        bool unique)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        if (columns.Count == 0)
            throw new ArgumentException("At least one column is required.", nameof(columns));

        string prefix = unique ? "CREATE UNIQUE INDEX " : "CREATE INDEX ";
        string cols = string.Join(", ", columns.Select(QuoteIdent));
        return $"{prefix}{QuoteIdent(indexName)} ON {QuoteIdent(table)} ({cols})";
    }

    public static string BuildUpdate(
        string table,
        IReadOnlyList<(string Column, object? Value)> setValues,
        IReadOnlyList<(string Column, object? Value)> whereValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        if (setValues.Count == 0)
            throw new ArgumentException("At least one SET column is required.", nameof(setValues));
        if (whereValues.Count == 0)
            throw new ArgumentException("At least one WHERE column is required.", nameof(whereValues));

        string sets = string.Join(", ", setValues.Select(s =>
            $"{QuoteIdent(s.Column)} = {FormatLiteral(s.Value)}"));
        string wheres = string.Join(" AND ", whereValues.Select(w =>
            w.Value is null
                ? $"{QuoteIdent(w.Column)} IS NULL"
                : $"{QuoteIdent(w.Column)} = {FormatLiteral(w.Value)}"));

        return $"UPDATE {QuoteIdent(table)} SET {sets} WHERE {wheres}";
    }

    public static string BuildUpdateFromText(
        string table,
        IReadOnlyList<(string Column, string? Text, string? Type, bool IsNull)> setValues,
        IReadOnlyList<(string Column, object? Value)> whereValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        if (setValues.Count == 0)
            throw new ArgumentException("At least one SET column is required.", nameof(setValues));
        if (whereValues.Count == 0)
            throw new ArgumentException("At least one WHERE column is required.", nameof(whereValues));

        string sets = string.Join(", ", setValues.Select(s =>
            $"{QuoteIdent(s.Column)} = {FormatLiteralFromText(s.Text, s.Type, s.IsNull)}"));
        string wheres = string.Join(" AND ", whereValues.Select(w =>
            w.Value is null
                ? $"{QuoteIdent(w.Column)} IS NULL"
                : $"{QuoteIdent(w.Column)} = {FormatLiteral(w.Value)}"));

        return $"UPDATE {QuoteIdent(table)} SET {sets} WHERE {wheres}";
    }

    public static string BuildDelete(
        string table,
        IReadOnlyList<(string Column, object? Value)> whereValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        if (whereValues.Count == 0)
            throw new ArgumentException("At least one WHERE column is required.", nameof(whereValues));

        string wheres = string.Join(" AND ", whereValues.Select(w =>
            w.Value is null
                ? $"{QuoteIdent(w.Column)} IS NULL"
                : $"{QuoteIdent(w.Column)} = {FormatLiteral(w.Value)}"));

        return $"DELETE FROM {QuoteIdent(table)} WHERE {wheres}";
    }

    public static bool TryValidateField(
        string? text,
        string? columnType,
        bool allowNull,
        bool isNull,
        out string? error)
    {
        error = null;

        if (isNull || text is null)
        {
            if (!allowNull)
            {
                error = "Value is required (NOT NULL).";
                return false;
            }

            return true;
        }

        string trimmed = text.Trim();
        if (trimmed.Length == 0 && !allowNull)
        {
            error = "Value is required (NOT NULL).";
            return false;
        }

        string type = columnType ?? "";
        if (IsBoolType(type))
        {
            if (bool.TryParse(trimmed, out _) || trimmed is "0" or "1" or "TRUE" or "FALSE" or "true" or "false")
                return true;
            error = "Expected a boolean (true/false).";
            return false;
        }

        if (IsIntegerType(type))
        {
            if (IsValidIntegerLiteral(trimmed))
                return true;
            error = "Expected an integer (digits and optional leading -).";
            return false;
        }

        if (IsFloatType(type))
        {
            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                return true;
            error = "Expected a number.";
            return false;
        }

        return true;
    }

    public static bool IsNullable(ColumnSchemaInfo column)
    {
        string? n = column.Nullable;
        if (string.IsNullOrWhiteSpace(n))
            return true;

        return n is "YES" or "Yes" or "yes" or "1" or "true" or "True" or "NULL" or "Y";
    }

    public static bool IsBoolType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return false;
        string t = type.Trim();
        return t.Equals("BOOL", StringComparison.OrdinalIgnoreCase)
            || t.Equals("BOOLEAN", StringComparison.OrdinalIgnoreCase)
            || t.Contains("bool", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNumericType(string? type) =>
        IsIntegerType(type) || IsFloatType(type);

    public static bool IsIntegerType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return false;
        string t = type.Trim();
        return t.Equals("INT64", StringComparison.OrdinalIgnoreCase)
            || t.Equals("INT", StringComparison.OrdinalIgnoreCase)
            || t.Equals("INTEGER", StringComparison.OrdinalIgnoreCase)
            || t.Equals("BIGINT", StringComparison.OrdinalIgnoreCase)
            || t.Contains("int", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Keeps only digits and a single leading minus for INT64-style inputs.
    /// </summary>
    public static string FilterIntegerInput(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        StringBuilder sb = new(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c is >= '0' and <= '9')
                sb.Append(c);
            else if (c == '-' && sb.Length == 0)
                sb.Append(c);
        }

        return sb.ToString();
    }

    public static bool IsValidIntegerLiteral(string text)
    {
        if (string.IsNullOrEmpty(text) || text == "-")
            return false;

        int i = 0;
        if (text[0] == '-')
        {
            if (text.Length == 1)
                return false;
            i = 1;
        }

        for (; i < text.Length; i++)
        {
            if (text[i] is < '0' or > '9')
                return false;
        }

        return true;
    }

    public static bool IsFloatType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return false;
        string t = type.Trim();
        return t.Equals("FLOAT64", StringComparison.OrdinalIgnoreCase)
            || t.Equals("DOUBLE", StringComparison.OrdinalIgnoreCase)
            || t.Equals("FLOAT", StringComparison.OrdinalIgnoreCase)
            || t.Contains("float", StringComparison.OrdinalIgnoreCase)
            || t.Contains("double", StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteString(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
