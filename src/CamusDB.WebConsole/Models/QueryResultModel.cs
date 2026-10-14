namespace CamusDB.WebConsole.Models;

public sealed class QueryColumn
{
    public required string Name { get; init; }

    public required string TypeName { get; init; }

    public Type ClrType { get; init; } = typeof(object);
}

public sealed class QueryResultModel
{
    public bool Success { get; init; }

    public bool IsResultSet { get; init; }

    public IReadOnlyList<QueryColumn> Columns { get; init; } = [];

    public IReadOnlyList<object?[]> Rows { get; init; } = [];

    public int RowsReturned { get; init; }

    public int? RowsAffected { get; init; }

    public bool Truncated { get; init; }

    public int MaxRows { get; init; }

    /// <summary>
    /// The <c>SELECT</c> that the grid pages through, without the <c>LIMIT</c> and <c>OFFSET</c> that the
    /// console adds. Null when the statement is not pageable.
    /// </summary>
    public string? PageSql { get; init; }

    /// <summary>True when the server has more rows after <see cref="Rows"/> for <see cref="PageSql"/>.</summary>
    public bool HasMore { get; init; }

    public TimeSpan ExecuteDuration { get; init; }

    public TimeSpan TotalDuration { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public string? Message { get; init; }

    /// <summary>The plan an EXPLAIN statement returned, read from <see cref="Rows"/>. Null for every other statement.</summary>
    public QueryPlanModel? Plan { get; init; }

    public static QueryResultModel Failure(string message, string? code, TimeSpan total) => new()
    {
        Success = false,
        ErrorMessage = message,
        ErrorCode = code,
        TotalDuration = total,
        ExecuteDuration = total,
    };
}

/// <summary>A sort the grid asks for: a column and a direction. A null column means no sort.</summary>
public readonly record struct GridSort(string? Column, bool Descending);
