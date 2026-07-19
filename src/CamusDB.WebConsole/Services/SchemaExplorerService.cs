using CamusDB.Client;
using CamusDB.WebConsole.Models;

namespace CamusDB.WebConsole.Services;

public sealed class SchemaExplorerService
{
    private readonly CamusSessionService _session;

    public SchemaExplorerService(CamusSessionService session)
    {
        _session = session;
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(CancellationToken cancellationToken = default)
    {
        List<object?[]> rows = await QueryAsync("SHOW DATABASES", cancellationToken).ConfigureAwait(false);
        return rows
            .Select(r => GetString(r, 0))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> ListTablesAsync(CancellationToken cancellationToken = default)
    {
        List<object?[]> rows = await QueryAsync("SHOW TABLES", cancellationToken).ConfigureAwait(false);
        return rows
            .Select(r => GetString(r, 0))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<ColumnSchemaInfo>> ListColumnsAsync(
        string table,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        string sql = $"SHOW COLUMNS FROM {QuoteIdent(table)}";

        await using CamusCommand command = _session.GetConnection().CreateCamusCommand(sql);
        await using CamusDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, int> ordinals = BuildOrdinalMap(reader);
        List<ColumnSchemaInfo> columns = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string name = ReadField(reader, ordinals, "Field", "Column", "column", "name", "tables") ?? "";
            if (name.Length == 0 && reader.FieldCount > 0)
                name = Convert.ToString(reader.GetValue(0)) ?? "";
            if (name.Length == 0)
                continue;

            string? type = ReadField(reader, ordinals, "Type", "type", "DataType");
            string? nullable = ReadField(reader, ordinals, "Null", "Nullable", "nullable");
            string? defaultValue = ReadField(reader, ordinals, "Default", "default");
            string? key = ReadField(reader, ordinals, "Key", "key");

            columns.Add(new ColumnSchemaInfo
            {
                Name = name,
                Type = type,
                Nullable = nullable,
                Default = defaultValue,
                IsPrimaryKey = string.Equals(key, "PRI", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, "PRIMARY", StringComparison.OrdinalIgnoreCase)
                    || (key?.Contains("PRIMARY", StringComparison.OrdinalIgnoreCase) ?? false),
            });
        }

        return columns;
    }

    public async Task<IReadOnlyList<IndexSchemaInfo>> ListIndexesAsync(
        string table,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        string sql = $"SHOW INDEXES FROM {QuoteIdent(table)}";

        await using CamusCommand command = _session.GetConnection().CreateCamusCommand(sql);
        await using CamusDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, int> ordinals = BuildOrdinalMap(reader);
        List<IndexSchemaInfo> indexes = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string name = ReadField(reader, ordinals, "Key_name", "Index", "index", "Name", "name") ?? "";
            if (name.Length == 0 && reader.FieldCount > 0)
                name = Convert.ToString(reader.GetValue(0)) ?? "";
            if (name.Length == 0)
                continue;

            string? cols = ReadField(reader, ordinals, "Column_name", "Columns", "columns", "Column");
            string? uniqueRaw = ReadField(reader, ordinals, "Unique", "unique");
            string? nonUnique = ReadField(reader, ordinals, "Non_unique", "non_unique");

            bool unique = uniqueRaw is "1" or "true" or "True" or "UNIQUE"
                || nonUnique is "0" or "false" or "False";

            indexes.Add(new IndexSchemaInfo
            {
                Name = name,
                Columns = cols,
                Unique = unique,
            });
        }

        return indexes;
    }

    public async Task<IReadOnlyList<CamusBranchRow>> ListBranchesAsync(
        string database,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        return await _session.GetConnection().ShowBranchesAsync(database, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CamusSchemaNode> BuildRootAsync(CancellationToken cancellationToken = default)
    {
        CamusSchemaNode root = new()
        {
            Id = "root",
            Name = "Databases",
            Kind = CamusSchemaNodeKind.Root,
            ChildrenLoaded = true,
        };

        IReadOnlyList<string> databases = await ListDatabasesAsync(cancellationToken).ConfigureAwait(false);
        foreach (string db in databases)
        {
            root.Children.Add(new CamusSchemaNode
            {
                Id = $"db:{db}",
                Name = db,
                Kind = CamusSchemaNodeKind.Database,
                Database = db,
            });
        }

        return root;
    }

    /// <summary>
    /// Loads tables (and branches) directly under the database node.
    /// </summary>
    public async Task LoadDatabaseChildrenAsync(
        CamusSchemaNode databaseNode,
        bool forceReload = false,
        CancellationToken cancellationToken = default)
    {
        if (databaseNode.Kind != CamusSchemaNodeKind.Database || databaseNode.Database is null)
            return;

        if (databaseNode.ChildrenLoaded && !forceReload)
            return;

        string database = databaseNode.Database;

        // Switch without notifying — callers own UI refresh. Avoids tearing down the tree mid-load.
        await _session.ChangeDatabaseAsync(database, notify: false, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<string> tables = await ListTablesAsync(cancellationToken).ConfigureAwait(false);

        List<CamusSchemaNode> children = [];
        foreach (string table in tables)
        {
            children.Add(new CamusSchemaNode
            {
                Id = $"db:{database}:table:{table}",
                Name = table,
                Kind = CamusSchemaNodeKind.Table,
                Database = database,
                Table = table,
            });
        }

        try
        {
            IReadOnlyList<CamusBranchRow> branches =
                await ListBranchesAsync(database, cancellationToken).ConfigureAwait(false);
            if (branches.Count > 0)
            {
                CamusSchemaNode branchesFolder = new()
                {
                    Id = $"db:{database}:branches",
                    Name = $"Branches ({branches.Count})",
                    Kind = CamusSchemaNodeKind.BranchesFolder,
                    Database = database,
                    ChildrenLoaded = true,
                };

                foreach (CamusBranchRow branch in branches)
                {
                    string name = branch.Database ?? branch.Id ?? "(branch)";
                    branchesFolder.Children.Add(new CamusSchemaNode
                    {
                        Id = $"db:{database}:branch:{name}",
                        Name = name,
                        Kind = CamusSchemaNodeKind.Branch,
                        Database = database,
                        Detail = branch.Parent is null ? null : $"parent: {branch.Parent}",
                        ChildrenLoaded = true,
                    });
                }

                children.Add(branchesFolder);
            }
        }
        catch
        {
            // Branches are optional; table list is the important part.
        }

        databaseNode.Children.Clear();
        databaseNode.Children.AddRange(children);
        databaseNode.Name = $"{database} ({tables.Count})";
        databaseNode.ChildrenLoaded = true;
    }

    public async Task LoadTableChildrenAsync(
        CamusSchemaNode tableNode,
        bool forceReload = false,
        CancellationToken cancellationToken = default)
    {
        if (tableNode.Kind != CamusSchemaNodeKind.Table || tableNode.Table is null || tableNode.Database is null)
            return;

        if (tableNode.ChildrenLoaded && !forceReload)
            return;

        await _session.ChangeDatabaseAsync(tableNode.Database, notify: false, cancellationToken)
            .ConfigureAwait(false);

        string table = tableNode.Table;
        string database = tableNode.Database;

        CamusSchemaNode columnsFolder = new()
        {
            Id = $"{tableNode.Id}:columns",
            Name = "Columns",
            Kind = CamusSchemaNodeKind.ColumnsFolder,
            Database = database,
            Table = table,
            ChildrenLoaded = true,
        };

        IReadOnlyList<ColumnSchemaInfo> columns =
            await ListColumnsAsync(table, cancellationToken).ConfigureAwait(false);
        columnsFolder.Name = $"Columns ({columns.Count})";
        foreach (ColumnSchemaInfo column in columns)
        {
            columnsFolder.Children.Add(new CamusSchemaNode
            {
                Id = $"{tableNode.Id}:col:{column.Name}",
                Name = column.Name,
                Kind = CamusSchemaNodeKind.Column,
                Database = database,
                Table = table,
                Detail = column.Type,
                ChildrenLoaded = true,
            });
        }

        CamusSchemaNode indexesFolder = new()
        {
            Id = $"{tableNode.Id}:indexes",
            Name = "Indexes",
            Kind = CamusSchemaNodeKind.IndexesFolder,
            Database = database,
            Table = table,
            ChildrenLoaded = true,
        };

        IReadOnlyList<IndexSchemaInfo> indexes =
            await ListIndexesAsync(table, cancellationToken).ConfigureAwait(false);
        indexesFolder.Name = $"Indexes ({indexes.Count})";
        foreach (IndexSchemaInfo index in indexes)
        {
            indexesFolder.Children.Add(new CamusSchemaNode
            {
                Id = $"{tableNode.Id}:idx:{index.Name}",
                Name = index.Name,
                Kind = CamusSchemaNodeKind.Index,
                Database = database,
                Table = table,
                Detail = index.Columns,
                ChildrenLoaded = true,
            });
        }

        tableNode.Children.Clear();
        tableNode.Children.Add(columnsFolder);
        tableNode.Children.Add(indexesFolder);
        tableNode.ChildrenLoaded = true;
    }

    private async Task<List<object?[]>> QueryAsync(string sql, CancellationToken cancellationToken)
    {
        await using CamusCommand command = _session.GetConnection().CreateCamusCommand(sql);
        await using CamusDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<object?[]> rows = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            object?[] values = new object?[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++)
            {
                object value = reader.GetValue(i);
                values[i] = value is DBNull ? null : value;
            }

            rows.Add(values);
        }

        return rows;
    }

    private static Dictionary<string, int> BuildOrdinalMap(CamusDataReader reader)
    {
        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < reader.FieldCount; i++)
            map[reader.GetName(i)] = i;
        return map;
    }

    private static string? ReadField(CamusDataReader reader, Dictionary<string, int> ordinals, params string[] names)
    {
        foreach (string name in names)
        {
            if (ordinals.TryGetValue(name, out int ordinal))
            {
                object value = reader.GetValue(ordinal);
                return value is DBNull or null ? null : Convert.ToString(value);
            }
        }

        return null;
    }

    private static string GetString(object?[] row, int index)
    {
        if (index < 0 || index >= row.Length || row[index] is null)
            return "";
        return Convert.ToString(row[index]) ?? "";
    }

    private static string QuoteIdent(string ident)
    {
        if (ident.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            return ident;
        return "\"" + ident.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
