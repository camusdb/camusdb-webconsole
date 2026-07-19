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

public sealed class IndexSchemaInfo
{
    public required string Name { get; init; }

    public string? Columns { get; init; }

    public bool Unique { get; init; }
}
