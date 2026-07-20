namespace CamusDB.WebConsole.Models;

public sealed class TableDataContext
{
    public required string Database { get; init; }

    public required string Table { get; init; }

    public required IReadOnlyList<ColumnSchemaInfo> Columns { get; init; }

    public IReadOnlyList<ColumnSchemaInfo> PrimaryKeyColumns =>
        Columns.Where(c => c.IsPrimaryKey).ToList();

    public bool HasPrimaryKey => PrimaryKeyColumns.Count > 0;
}
