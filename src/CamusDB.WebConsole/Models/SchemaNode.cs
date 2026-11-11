namespace CamusDB.WebConsole.Models;

public enum CamusSchemaNodeKind
{
    Root,
    Database,
    TablesFolder,
    Table,
    ColumnsFolder,
    Column,
    IndexesFolder,
    Index,
    BranchesFolder,
    Branch,
}

public sealed class CamusSchemaNode
{
    public required string Id { get; init; }

    public required string Name { get; set; }

    public required CamusSchemaNodeKind Kind { get; init; }

    public string? Database { get; init; }

    public string? Table { get; init; }

    public string? Detail { get; init; }

    public bool ChildrenLoaded { get; set; }

    public List<CamusSchemaNode> Children { get; } = [];
}

public sealed class ColumnSchemaInfo
{
    public required string Name { get; init; }

    public string? Type { get; init; }

    public string? Nullable { get; init; }

    public string? Default { get; init; }

    public bool IsPrimaryKey { get; init; }
}

/// <summary>
/// One column as the Create table dialog defines it. <c>DefaultExpression</c> is already-formatted
/// SQL (a literal, or a <c>fn()</c> call) that goes inside <c>DEFAULT(...)</c>; a null
/// <c>Comment</c> means no COMMENT clause at all, while <c>""</c> declares an empty one.
/// </summary>
public sealed record ColumnDefinition(
    string Name,
    string Type,
    bool NotNull,
    bool PrimaryKey,
    string? DefaultExpression = null,
    string? Comment = null);

/// <summary>
/// Comments read back out of SHOW CREATE TABLE. They are deliberately absent from SHOW COLUMNS —
/// the COMMENT ON spec refuses to change that statement's row shape — so this is the only surface
/// the console can read them from.
/// </summary>
public sealed class TableCommentInfo
{
    public string? TableComment { get; init; }

    public IReadOnlyDictionary<string, string> ColumnComments { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class IndexSchemaInfo
{
    public required string Name { get; init; }

    public string? Columns { get; init; }

    public bool Unique { get; init; }

    /// <summary>
    /// The Index_type of SHOW INDEXES: <c>GIN</c> for an index on a JSONB column, else the ordered
    /// index type, or null when the server does not send the column.
    /// </summary>
    public string? Method { get; init; }

    public bool IsGin => string.Equals(Method, "GIN", StringComparison.OrdinalIgnoreCase);

    /// <summary>The text after the name in the schema tree: the columns, and GIN for a GIN index.</summary>
    public string? Detail => IsGin ? $"{Columns} (GIN)".TrimStart() : Columns;
}

/// <summary>
/// One row of SHOW SEQUENCES. <c>OwnedBy</c> names the <c>table.column</c> whose identity or SERIAL
/// declaration created the sequence; a free-standing sequence has none.
/// </summary>
public sealed class SequenceSchemaInfo
{
    public required string Name { get; init; }

    public string? StartValue { get; init; }

    public string? Increment { get; init; }

    public string? MinValue { get; init; }

    public string? MaxValue { get; init; }

    public string? Cache { get; init; }

    public string? OwnedBy { get; init; }

    public string? Comment { get; init; }
}

/// <summary>
/// One row of SHOW FUNCTIONS: an inline function, a scalar function with a C# body. <c>State</c> is
/// what the node that answered did with it: <c>not compiled</c>, <c>compiled</c> or
/// <c>failed: reason</c>. Each node compiles a function at its own first call.
/// </summary>
public sealed class FunctionSchemaInfo
{
    public required string Name { get; init; }

    /// <summary>The parameter list without its parentheses, for example <c>a INT64, b STRING</c>.</summary>
    public string? Arguments { get; init; }

    public string? Returns { get; init; }

    /// <summary><c>called</c>, or <c>returns null</c> for RETURNS NULL ON NULL INPUT.</summary>
    public string? NullInput { get; init; }

    public string? Language { get; init; }

    public string? Owner { get; init; }

    public string? State { get; init; }

    /// <summary>The signature the schema tree shows: <c>name(arguments)</c>.</summary>
    public string Signature => $"{Name}({Arguments})";

    public bool Failed => State?.StartsWith("failed", StringComparison.OrdinalIgnoreCase) ?? false;
}

/// <summary>
/// One row of SHOW TRIGGERS ON table. <c>Form</c> is the language of an inline body
/// (<c>csharp</c>), or <c>extension</c> for a type of an installed extension.
/// </summary>
public sealed class TriggerSchemaInfo
{
    public required string Name { get; init; }

    public required string Table { get; init; }

    /// <summary><c>BEFORE</c> or <c>AFTER</c>.</summary>
    public string? Timing { get; init; }

    /// <summary>The events, for example <c>INSERT, UPDATE</c>.</summary>
    public string? Events { get; init; }

    public string? Form { get; init; }

    /// <summary>The extension type that runs the trigger. Null for an inline body.</summary>
    public string? TypeName { get; init; }

    public string? Extension { get; init; }

    /// <summary>The user whose rights the SQL of an inline body runs with. Null for an extension trigger.</summary>
    public string? Owner { get; init; }

    /// <summary>When and on what it fires, for example <c>BEFORE INSERT, UPDATE</c>.</summary>
    public string FiresOn => $"{Timing} {Events}".Trim();

    /// <summary>
    /// The text after the name in the schema tree: the timing only. The events make the row too
    /// long for the name, so they are in the tooltip.
    /// </summary>
    public string Detail => Timing ?? "trigger";
}

/// <summary>
/// One foreign key, read back out of SHOW CREATE TABLE. There is no SHOW FOREIGN KEYS, so the
/// constraint clause the server renders is the only read surface. <c>OnDelete</c> and
/// <c>OnUpdate</c> hold the action words as rendered (<c>NO ACTION</c> when the clause is absent).
/// </summary>
public sealed class ForeignKeySchemaInfo
{
    public required string Name { get; init; }

    public IReadOnlyList<string> Columns { get; init; } = [];

    public required string ReferencedTable { get; init; }

    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    public string OnDelete { get; init; } = SqlForeignKeyActions.NoAction;

    public string OnUpdate { get; init; } = SqlForeignKeyActions.NoAction;
}

/// <summary>
/// A foreign key as the console builds it. An empty <c>ReferencedColumns</c> references the parent's
/// primary key; a null <c>Name</c> lets the server pick <c>{table}_{column}_fkey</c>.
/// </summary>
public sealed record ForeignKeyDefinition(
    string? Name,
    IReadOnlyList<string> Columns,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    string OnDelete = SqlForeignKeyActions.NoAction,
    string OnUpdate = SqlForeignKeyActions.NoAction);

/// <summary>
/// The referential actions. CamusDB stores all four PostgreSQL actions but runs only NO ACTION and
/// RESTRICT; CASCADE, SET NULL and SET DEFAULT are refused with CADB0533, so the console offers only
/// <see cref="Supported"/>.
/// </summary>
public static class SqlForeignKeyActions
{
    public const string NoAction = "NO ACTION";

    public const string Restrict = "RESTRICT";

    public static readonly string[] Supported = [NoAction, Restrict];
}
