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

    public TimeSpan ExecuteDuration { get; init; }

    public TimeSpan TotalDuration { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public string? Message { get; init; }

    public static QueryResultModel Failure(string message, string? code, TimeSpan total) => new()
    {
        Success = false,
        ErrorMessage = message,
        ErrorCode = code,
        TotalDuration = total,
        ExecuteDuration = total,
    };
}
