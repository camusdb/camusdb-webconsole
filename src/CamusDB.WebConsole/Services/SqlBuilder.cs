using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CamusDB.WebConsole.Models;

namespace CamusDB.WebConsole.Services;

public static class SqlBuilder
{
    /// <summary>
    /// The server's bound on any comment (CamusDBConstants.MaxCommentLength). Checked here so an
    /// over-long comment is caught in the dialog instead of coming back as CADB0511.
    /// </summary>
    public const int MaxCommentLength = 65_535;

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
        "DATE",
        "DATETIME",
        "BYTES",
    ];

    /// <summary>
    /// Every word the CamusDB lexer turns into a keyword token
    /// (SQLParser.Language.analyzer.lex). A table or column named after one of these has to be
    /// backticked or it lexes as that keyword — <c>comment</c> is the live example, made reserved
    /// when COMMENT ON landed.
    /// </summary>
    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADD", "ALTER", "ANALYZE", "ANCESTORS", "AND", "ARRAY", "AS", "ASC", "BEGIN", "BETWEEN",
        "BLOB", "BOOL", "BOOLEAN", "BRANCH", "BRANCHES", "BY", "BYTES", "CASE", "CAST", "CHAR",
        "CHECK", "COLUMN", "COLUMNS", "COMMENT", "COMMIT", "CONSTRAINT", "CREATE", "DATABASE",
        "DATABASES", "DATE", "DATETIME", "DEFAULT", "DELETE", "DESC", "DESCRIBE", "DISTINCT",
        "DOUBLE", "DROP", "ELSE", "END", "EVICT", "EXISTS", "EXPLAIN", "FALSE", "FLOAT", "FLOAT32",
        "FLOAT64", "FOR", "FORCE", "FROM", "GRANT", "GRANTS", "GROUP", "GUID", "HAVING",
        "IDENTIFIED", "IF", "ILIKE", "IN", "INCLUDE", "INDEX", "INDEXES", "INNER", "INSERT", "INT",
        "INT64", "INTEGER", "INTO", "IS", "JOIN", "KEY", "LIKE", "LIMIT", "MATERIALIZED", "NOT",
        "NULL", "OBJECT_ID", "OFFSET", "OID", "ON", "OR", "ORDER", "ORPHAN", "PRIMARY",
        "PRIVILEGES", "REAL", "REFRESH", "RELINK", "RENAME", "RESET", "REVOKE", "ROLLBACK",
        "SELECT", "SET", "SHOW", "SMALLINT", "START", "STRING", "TABLE", "TABLES", "TEXT", "THEN",
        "TIMESTAMP", "TO", "TRANSACTION", "TRUE", "UNIQUE", "UPDATE", "USER", "UUID", "VALUES",
        "VARCHAR", "VIEW", "VIEWS", "WHEN", "WHERE", "WITH",
    };

    /// <summary>
    /// Zero-argument volatile functions CamusDB accepts inside <c>DEFAULT(...)</c>, mapped to the
    /// canonical column type each returns. The engine requires an <em>exact</em> type match
    /// (SQLExecutorBaseCreator.ValidateDefaultFunctionType), so the mismatch is reported here rather
    /// than as a failed CREATE TABLE. Session-scoped functions (current_user and friends) are
    /// volatile too but are rejected as defaults, so they are deliberately absent.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultFunctions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["gen_id"] = "OID",
            ["gen_uuid_v4"] = "UUID",
            ["gen_uuid_v7"] = "UUID",
            ["current_date"] = "DATE",
            ["current_timestamp"] = "DATETIME",
            ["now"] = "DATETIME",
            ["random"] = "FLOAT64",
        };

    /// <summary>
    /// Backticks are the only identifier quoting CamusDB has (the lexer's EscIdentifier rule).
    /// A double-quoted name is a <em>string literal</em> in this dialect — both String and
    /// StringSingle return TSTRING — so quoting with <c>"</c> produces a literal where an identifier
    /// was meant. Reserved words are quoted for the same reason a name with odd characters is: bare,
    /// they do not lex as identifiers.
    /// </summary>
    public static string QuoteIdent(string ident)
    {
        if (string.IsNullOrEmpty(ident))
            return ident;

        if (!ReservedWords.Contains(ident) && ident.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            return ident;

        return "`" + ident + "`";
    }

    /// <summary>
    /// Renders the dotted <c>table.element</c> reference that COMMENT ON COLUMN / COMMENT ON INDEX
    /// require. The grammar folds <c>qualified_identifier</c> into <c>any_identifier</c>, so each
    /// half is quoted independently and the dot stays outside the backticks.
    /// </summary>
    public static string QuoteQualifiedIdent(string table, string element) =>
        QuoteIdent(table) + "." + QuoteIdent(element);

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

    /// <summary>
    /// Emits the column list in the clause order SHOW CREATE TABLE renders — type, NOT NULL,
    /// DEFAULT, COMMENT — so a table created here and one round-tripped through the server's own
    /// DDL read the same. <paramref name="tableComment"/> becomes the trailing <c>) COMMENT '…'</c>.
    /// </summary>
    public static string BuildCreateTable(
        string table,
        IReadOnlyList<ColumnDefinition> columns,
        string? tableComment = null)
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

            ColumnDefinition col = columns[i];
            sb.Append(QuoteIdent(col.Name)).Append(' ').Append(col.Type.Trim());

            if (col.NotNull)
                sb.Append(" NOT NULL");

            // The parentheses are not optional: the grammar is DEFAULT LPAREN default_expr RPAREN.
            if (!string.IsNullOrWhiteSpace(col.DefaultExpression))
                sb.Append(" DEFAULT(").Append(col.DefaultExpression).Append(')');

            if (col.Comment is not null)
                sb.Append(" COMMENT ").Append(QuoteString(col.Comment));
        }

        List<string> pk = columns.Where(c => c.PrimaryKey).Select(c => QuoteIdent(c.Name)).ToList();
        if (pk.Count > 0)
            sb.Append(", PRIMARY KEY (").Append(string.Join(", ", pk)).Append(')');

        sb.Append(')');

        if (tableComment is not null)
            sb.Append(" COMMENT ").Append(QuoteString(tableComment));

        return sb.ToString();
    }

    /// <summary>
    /// <c>COMMENT ON TABLE t IS '…'</c>. A null <paramref name="comment"/> emits <c>IS NULL</c>,
    /// which removes the comment — distinct from <c>IS ''</c>, which stores an empty one.
    /// </summary>
    public static string BuildCommentOnTable(string table, string? comment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        return $"COMMENT ON TABLE {QuoteIdent(table)} IS {(comment is null ? "NULL" : QuoteString(comment))}";
    }

    /// <summary>
    /// <c>COMMENT ON COLUMN t.c IS '…'</c>. The table qualifier is required — the creator rejects
    /// an unqualified name — and a null <paramref name="comment"/> removes the comment.
    /// </summary>
    public static string BuildCommentOnColumn(string table, string column, string? comment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        return $"COMMENT ON COLUMN {QuoteQualifiedIdent(table, column)} IS "
            + (comment is null ? "NULL" : QuoteString(comment));
    }

    public static string BuildShowCreateTable(string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        return $"SHOW CREATE TABLE {QuoteIdent(table)}";
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

    /// <summary>
    /// Renders a re-parseable SQL string literal, mirroring CamusDB's own SqlStringLiteral.Quote.
    /// A plain <c>'…'</c> literal cannot carry a control character — the lexer's RawChs class
    /// excludes them outright — so a value holding one switches to the <c>E'…'</c> escape form,
    /// where a backslash becomes meaningful and therefore has to be doubled.
    /// </summary>
    public static string QuoteString(string value)
    {
        bool hasControl = false;
        foreach (char c in value)
        {
            if (char.IsControl(c))
            {
                hasControl = true;
                break;
            }
        }

        if (!hasControl)
            return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

        StringBuilder sb = new((value.Length * 6) + 3);
        sb.Append("E'");

        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("\\'"); break;
                case '\0': sb.Append("\\0"); break;
                case '\a': sb.Append("\\a"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\v': sb.Append("\\v"); break;
                default:
                    if (char.IsControl(c))
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }

        return sb.Append('\'').ToString();
    }

    public static bool TryValidateComment(string? comment, out string? error)
    {
        error = null;

        if (comment is null)
            return true;

        if (comment.Length > MaxCommentLength)
        {
            error = $"Comment is {comment.Length} characters; the limit is {MaxCommentLength}.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Turns what the user typed in a Default cell into the expression that goes inside
    /// <c>DEFAULT(...)</c>, or null when the cell is empty. Three forms are accepted: a bare
    /// <c>fn()</c> call (validated against <see cref="DefaultFunctions"/> and the column's type),
    /// the word NULL, and anything else as a literal of the column's type.
    /// </summary>
    public static bool TryBuildDefaultExpression(
        string? text,
        string? columnType,
        out string? expression,
        out string? error)
    {
        expression = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
            return true;

        string trimmed = text.Trim();

        if (FunctionCallSyntax.IsMatch(trimmed))
        {
            string name = trimmed[..trimmed.IndexOf('(', StringComparison.Ordinal)].Trim();

            if (!DefaultFunctions.TryGetValue(name, out string? returns))
            {
                error = $"'{name}()' is not a usable DEFAULT function. Supported: "
                    + string.Join(", ", DefaultFunctions.Keys.Order(StringComparer.Ordinal).Select(f => f + "()"))
                    + ".";
                return false;
            }

            string declared = CanonicalType(columnType);
            if (declared.Length > 0 && !declared.Equals(returns, StringComparison.OrdinalIgnoreCase))
            {
                error = $"{name}() returns {returns}, but the column is {columnType}.";
                return false;
            }

            expression = name.ToLowerInvariant() + "()";
            return true;
        }

        if (trimmed.Equals("NULL", StringComparison.OrdinalIgnoreCase))
        {
            expression = "NULL";
            return true;
        }

        if (!TryValidateField(trimmed, columnType, allowNull: true, isNull: false, out error))
            return false;

        expression = FormatLiteralFromText(trimmed, columnType, isNull: false);
        return true;
    }

    /// <summary>
    /// Collapses the type spellings the lexer treats as synonyms onto one name, so a DEFAULT
    /// function's return type can be compared against a column declared as any of them.
    /// </summary>
    private static string CanonicalType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return "";

        // A parameterised spelling such as DECIMAL(10,2) is not a CamusDB type; compare on the head.
        string t = type.Trim();
        int paren = t.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0)
            t = t[..paren].Trim();

        return t.ToUpperInvariant() switch
        {
            "OID" or "OBJECT_ID" => "OID",
            "UUID" or "GUID" => "UUID",
            "DATE" => "DATE",
            "DATETIME" or "TIMESTAMP" => "DATETIME",
            "FLOAT64" or "DOUBLE" or "FLOAT" => "FLOAT64",
            "FLOAT32" or "REAL" => "FLOAT32",
            "INT" or "INT64" or "INTEGER" or "SMALLINT" => "INT64",
            "STRING" or "VARCHAR" or "CHAR" or "TEXT" => "STRING",
            "BOOL" or "BOOLEAN" => "BOOL",
            "BYTES" or "BLOB" => "BYTES",
            _ => t.ToUpperInvariant(),
        };
    }

    private static readonly Regex FunctionCallSyntax = new(
        @"^[A-Za-z_][A-Za-z0-9_]*\s*\(\s*\)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
