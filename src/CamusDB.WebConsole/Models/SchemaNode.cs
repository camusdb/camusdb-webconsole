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
