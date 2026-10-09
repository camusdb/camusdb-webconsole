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
        "FLOAT32",
        "NUMERIC",
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
        "CHECK", "COLUMN", "COLUMNS", "COMMENT", "COMMIT", "CONSTRAINT", "CREATE", "CROSS", "DATABASE",
        "DATABASES", "DATE", "DATETIME", "DECIMAL", "DEFAULT", "DEFERRABLE", "DELETE", "DESC", "DESCRIBE",
        "DISTINCT", "DOUBLE", "DROP", "ELSE", "END", "EVICT", "EXISTS", "EXPLAIN", "FALSE", "FLOAT",
        "FLOAT32", "FLOAT64", "FOR", "FORCE", "FOREIGN", "FROM", "GRANT", "GRANTS", "GROUP", "GUID",
        "HAVING", "IDENTIFIED", "IF", "ILIKE", "IN", "INCLUDE", "INDEX", "INDEXES", "INITIALLY",
        "INNER", "INSERT", "INT", "INT64", "INTEGER", "INTO", "IS", "JOIN", "KEY", "LEFT", "LIKE",
        "LIMIT", "MATERIALIZED", "NOT", "NULL", "NUMERIC", "OBJECT_ID", "OFFSET", "OID", "ON", "OR",
        "ORDER", "ORPHAN", "OUTER", "PRIMARY", "PRIVILEGES", "REAL", "REFERENCES", "REFRESH", "RELINK",
        "RENAME", "RESET", "RETURNING", "REVOKE", "RIGHT", "ROLLBACK",
        "SELECT", "SEQUENCE", "SEQUENCES", "SET", "SHOW", "SMALLINT", "START", "STRING", "TABLE",
        "TABLES", "TEXT", "THEN", "TIMESTAMP", "TO", "TRANSACTION", "TRUE", "TRUNCATE", "UNIQUE",
        "UPDATE", "USER", "UUID", "VALUES", "VARCHAR", "VIEW", "VIEWS", "WHEN", "WHERE", "WITH",
        "WITHOUT",
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
            NumericText n => NumericLiteral(n.Text),
            decimal m => NumericLiteral(m.ToString(CultureInfo.InvariantCulture)),
            sbyte or byte or short or ushort or int or uint or long or ulong or float or double
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

        if (IsDecimalType(type))
        {
            string trimmed = text.Trim();
            return IsValidNumericLiteral(trimmed) ? NumericLiteral(trimmed) : QuoteString(text);
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

    public static string BuildDropSequence(string sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sequence);
        return $"DROP SEQUENCE {QuoteIdent(sequence)}";
    }

    public static string BuildShowCreateSequence(string sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sequence);
        return $"SHOW CREATE SEQUENCE {QuoteIdent(sequence)}";
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
    /// Each of <paramref name="foreignKeys"/> follows the primary key as a table constraint, which
    /// is also the form SHOW CREATE TABLE renders.
    /// </summary>
    public static string BuildCreateTable(
        string table,
        IReadOnlyList<ColumnDefinition> columns,
        string? tableComment = null,
        IReadOnlyList<ForeignKeyDefinition>? foreignKeys = null)
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

            AppendColumn(sb, columns[i]);
        }

        List<string> pk = columns.Where(c => c.PrimaryKey).Select(c => QuoteIdent(c.Name)).ToList();
        if (pk.Count > 0)
            sb.Append(", PRIMARY KEY (").Append(string.Join(", ", pk)).Append(')');

        foreach (ForeignKeyDefinition foreignKey in foreignKeys ?? [])
            AppendForeignKey(sb.Append(", "), foreignKey);

        sb.Append(')');

        if (tableComment is not null)
            sb.Append(" COMMENT ").Append(QuoteString(tableComment));

        return sb.ToString();
    }

    /// <summary>
    /// <c>ALTER TABLE t ADD COLUMN c type [NOT NULL] [DEFAULT(…)] [COMMENT '…']</c>. The column
    /// clauses are the ones CREATE TABLE takes. A primary key and a foreign key are separate schema
    /// changes, so the server refuses them here.
    /// </summary>
    public static string BuildAddColumn(string table, ColumnDefinition column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(column.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(column.Type);
        if (column.PrimaryKey)
            throw new ArgumentException("ADD COLUMN cannot declare a primary key.", nameof(column));

        StringBuilder sb = new();
        sb.Append("ALTER TABLE ").Append(QuoteIdent(table)).Append(" ADD COLUMN ");
        AppendColumn(sb, column);
        return sb.ToString();
    }

    /// <summary>
    /// One column definition, in the clause order SHOW CREATE TABLE renders: type, NOT NULL,
    /// DEFAULT, COMMENT.
    /// </summary>
    private static void AppendColumn(StringBuilder sb, ColumnDefinition col)
    {
        sb.Append(QuoteIdent(col.Name)).Append(' ').Append(col.Type.Trim());

        if (col.NotNull)
            sb.Append(" NOT NULL");

        // The parentheses are not optional: the grammar is DEFAULT LPAREN default_expr RPAREN.
        if (!string.IsNullOrWhiteSpace(col.DefaultExpression))
            sb.Append(" DEFAULT(").Append(col.DefaultExpression).Append(')');

        if (col.Comment is not null)
            sb.Append(" COMMENT ").Append(QuoteString(col.Comment));
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

    /// <summary>
    /// <c>ALTER TABLE t ADD [CONSTRAINT name] FOREIGN KEY (...) REFERENCES p [(...)]</c>. The server
    /// reads every existing row before the constraint takes effect, so the statement fails with
    /// CADB0304 when a row has no parent.
    /// </summary>
    public static string BuildAddForeignKey(string table, ForeignKeyDefinition foreignKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        StringBuilder sb = new();
        sb.Append("ALTER TABLE ").Append(QuoteIdent(table)).Append(" ADD ");
        AppendForeignKey(sb, foreignKey);
        return sb.ToString();
    }

    /// <summary>
    /// <c>ALTER TABLE t DROP CONSTRAINT name</c>. CHECK, named NOT NULL and FOREIGN KEY constraints
    /// share one name space per table, so the name alone selects the constraint.
    /// </summary>
    public static string BuildDropConstraint(string table, string constraint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(constraint);
        return $"ALTER TABLE {QuoteIdent(table)} DROP CONSTRAINT {QuoteIdent(constraint)}";
    }

    /// <summary>
    /// The table-constraint form, in the clause order SHOW CREATE TABLE renders. ON DELETE and ON
    /// UPDATE are written only for an action other than the default, NO ACTION, as the server does.
    /// </summary>
    private static void AppendForeignKey(StringBuilder sb, ForeignKeyDefinition foreignKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignKey.ReferencedTable);
        if (foreignKey.Columns.Count == 0)
            throw new ArgumentException("A foreign key needs at least one column.", nameof(foreignKey));
        if (foreignKey.ReferencedColumns.Count > 0 && foreignKey.ReferencedColumns.Count != foreignKey.Columns.Count)
            throw new ArgumentException(
                "A foreign key needs as many referenced columns as referencing columns.", nameof(foreignKey));

        if (!string.IsNullOrWhiteSpace(foreignKey.Name))
            sb.Append("CONSTRAINT ").Append(QuoteIdent(foreignKey.Name.Trim())).Append(' ');

        sb.Append("FOREIGN KEY (").Append(string.Join(", ", foreignKey.Columns.Select(QuoteIdent))).Append(')');
        sb.Append(" REFERENCES ").Append(QuoteIdent(foreignKey.ReferencedTable));

        if (foreignKey.ReferencedColumns.Count > 0)
            sb.Append(" (").Append(string.Join(", ", foreignKey.ReferencedColumns.Select(QuoteIdent))).Append(')');

        AppendForeignKeyAction(sb, "DELETE", foreignKey.OnDelete);
        AppendForeignKeyAction(sb, "UPDATE", foreignKey.OnUpdate);
    }

    private static void AppendForeignKeyAction(StringBuilder sb, string verb, string? action)
    {
        if (string.IsNullOrWhiteSpace(action) || action.Equals(SqlForeignKeyActions.NoAction, StringComparison.OrdinalIgnoreCase))
            return;

        if (!SqlForeignKeyActions.Supported.Contains(action, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"ON {verb} {action} is not supported. Use NO ACTION or RESTRICT.");

        sb.Append(" ON ").Append(verb).Append(' ').Append(action.ToUpperInvariant());
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

        if (IsDecimalType(type))
        {
            if (IsValidNumericLiteral(trimmed))
                return true;
            error = "Expected a decimal number, for example 12.50 or -0.001.";
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
        IsIntegerType(type) || IsFloatType(type) || IsDecimalType(type);

    /// <summary>
    /// NUMERIC and its alias DECIMAL. SHOW COLUMNS and SHOW CREATE TABLE print <c>NUMERIC</c>, and the
    /// client reports the result column type as <c>Numeric</c>.
    /// </summary>
    public static bool IsDecimalType(string? type) =>
        CanonicalType(type) == "NUMERIC";

    /// <summary>
    /// The text forms NumericMath.TryParse accepts: an optional sign, digits with an optional point,
    /// and an optional exponent. The range is left to the server, which answers CADB0417 when a value
    /// does not fit.
    /// </summary>
    public static bool IsValidNumericLiteral(string text) => NumericLiteralSyntax.IsMatch(text);

    /// <summary>
    /// The typed literal <c>NUMERIC '…'</c>. A bare <c>1.5</c> is a FLOAT64 literal: it is exact when
    /// it is written into a NUMERIC column, but it compares with one as a double, so a WHERE built from
    /// it can miss the row. The typed literal is exact in both places and can use an index.
    /// </summary>
    public static string NumericLiteral(string text) => "NUMERIC " + QuoteString(text.Trim());

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
    /// <c>DEFAULT(...)</c>, or null when the cell is empty. Four forms are accepted: a bare
    /// <c>fn()</c> call (validated against <see cref="DefaultFunctions"/> and the column's type),
    /// <c>nextval('seq')</c> on an INT64 column, the word NULL, and anything else as a literal of
    /// the column's type.
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

        // nextval('seq') is the one DEFAULT call that takes an argument. A sequence issues int64
        // values, and the engine refuses it on any other column type
        // (SQLExecutorCreateTableCreator.RequireIdentityColumnIsInteger).
        Match nextval = NextvalCallSyntax.Match(trimmed);
        if (nextval.Success)
        {
            string declared = CanonicalType(columnType);
            if (declared.Length > 0 && declared != "INT64")
            {
                error = $"nextval() returns INT64, but the column is {columnType}.";
                return false;
            }

            expression = $"nextval({QuoteString(nextval.Groups["seq"].Value)})";
            return true;
        }

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
    /// The DEFAULT functions a column of <paramref name="type"/> can take, for a dialog hint. The
    /// engine demands an exact type match, so a type that no function returns has none to offer.
    /// </summary>
    public static string DefaultFunctionHint(string? type, bool includeNextval = true)
    {
        List<string> usable = DefaultFunctions
            .Where(f => TryBuildDefaultExpression($"{f.Key}()", type, out _, out _))
            .Select(f => $"{f.Key}()")
            .ToList();

        if (includeNextval && TryBuildDefaultExpression("nextval('seq')", type, out _, out _))
            usable.Add("nextval('seq')");

        return usable.Count == 0 ? "" : string.Join(", ", usable);
    }

    /// <summary>
    /// True when <paramref name="expression"/>, as <see cref="TryBuildDefaultExpression"/> returned
    /// it, is a function call. Every literal form it returns ends in a quote, a digit or a word, so a
    /// closing parenthesis marks a call. The server evaluates such a default for each new row only.
    /// </summary>
    public static bool IsFunctionDefault(string? expression) =>
        expression is not null && expression.EndsWith(')');

    public static string DefaultPlaceholder(string? type) =>
        IsNumericType(type) ? "0" : IsBoolType(type) ? "false" : "value";

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
            "NUMERIC" or "DECIMAL" => "NUMERIC",
            "INT" or "INT64" or "INTEGER" or "SMALLINT" => "INT64",
            "STRING" or "VARCHAR" or "CHAR" or "TEXT" => "STRING",
            "BOOL" or "BOOLEAN" => "BOOL",
            "BYTES" or "BLOB" => "BYTES",
            _ => t.ToUpperInvariant(),
        };
    }

    private static readonly Regex NumericLiteralSyntax = new(
        @"^[+-]?([0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FunctionCallSyntax = new(
        @"^[A-Za-z_][A-Za-z0-9_]*\s*\(\s*\)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NextvalCallSyntax = new(
        @"^nextval\s*\(\s*(?<q>['""])(?<seq>[A-Za-z_][A-Za-z0-9_]*)\k<q>\s*\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
