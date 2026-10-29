namespace CamusDB.WebConsole.Services;

/// <summary>What a run of statements changed in the schema tree. See <see cref="SchemaEffect"/>.</summary>
public sealed class SchemaChange
{
    /// <summary>True when a database was created, dropped or renamed.</summary>
    public bool DatabaseList { get; init; }

    /// <summary>The databases whose tables, views, indexes or sequences changed.</summary>
    public IReadOnlyCollection<string> Databases { get; init; } = [];
}

/// <summary>
/// Tells the schema explorer that SQL from the query workspace changed the schema, so the tree can load
/// it again. The workspace and the explorer do not reference each other, and one instance serves one
/// circuit, so a change in one browser tab never reaches another.
///
/// <para>The explorer's own dialogs do not use it: each one reloads the part of the tree it changed.</para>
/// </summary>
public sealed class SchemaChangeNotifier
{
    public event Action<SchemaChange>? Changed;

    public void Notify(SchemaChange change) => Changed?.Invoke(change);
}
