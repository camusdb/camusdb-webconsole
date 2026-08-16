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
