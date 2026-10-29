using System.Diagnostics;
using System.Text.RegularExpressions;
using CamusDB.Client;
using CamusDB.WebConsole.Models;

namespace CamusDB.WebConsole.Services;

public sealed class QueryExecutionService
{
    // A statement that starts with '(' is a query: a set operation whose first operand has its own
    // ORDER BY or LIMIT, as in (SELECT …) UNION SELECT …. DESCRIBE and DESC are SHOW COLUMNS.
    private static readonly Regex ResultSetPrefix = new(
        @"^\s*(\(|(SELECT|SHOW|EXPLAIN|WITH|DESCRIBE|DESC)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // The statements read through the driver's streaming reader. SHOW, EXPLAIN and DESCRIBE return
    // metadata and stay on the buffered reader; a write with RETURNING stays there too, because the
    // streaming reader reports no affected-row count.
    private static readonly Regex StreamPrefix = new(
        @"^\s*(\(|(SELECT|WITH)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ExplainPrefix = new(
        @"^\s*EXPLAIN\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // UPSERT INTO is an INSERT … ON CONFLICT (primary key) DO UPDATE. UPSERT is not a reserved word,
    // but no other statement starts with it.
    private static readonly Regex WritePrefix = new(
        @"^\s*(INSERT|UPSERT|UPDATE|DELETE)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex InsertPrefix = new(
        @"^\s*(INSERT|UPSERT)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // CREATE DATABASE [IF NOT EXISTS] name [BRANCH FROM source]
    private static readonly Regex CreateDatabaseRegex = new(
        @"^\s*CREATE\s+DATABASE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>(?:""[^""]+"")|(?:`[^`]+`)|(?:[A-Za-z_][A-Za-z0-9_]*))"
        + @"(?:\s+BRANCH\s+FROM\s+(?<source>(?:""[^""]+"")|(?:`[^`]+`)|(?:[A-Za-z_][A-Za-z0-9_]*)))?\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DropDatabaseRegex = new(
        @"^\s*DROP\s+DATABASE\s+(?:IF\s+EXISTS\s+)?(?<name>(?:""[^""]+"")|(?:`[^`]+`)|(?:[A-Za-z_][A-Za-z0-9_]*))\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly CamusSessionService _session;

    public QueryExecutionService(CamusSessionService session)
    {
        _session = session;
    }

    public Task<QueryResultModel> ExecuteAsync(string sql, CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(sql, paged: false, cancellationToken);

    /// <summary>
    /// Runs one statement for the results grid. A <c>SELECT</c> without its own <c>LIMIT</c> or
    /// <c>OFFSET</c> gets the first page only (<c>MaxRows</c> rows), and <see cref="FetchMoreAsync"/>
    /// gets the next pages. Each other statement runs as <see cref="ExecuteAsync"/> runs it.
    /// </summary>
    public Task<QueryResultModel> ExecutePagedAsync(string sql, CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(sql, paged: true, cancellationToken);

    /// <summary>
    /// Gets the page after the rows of <paramref name="previous"/> and returns a result that holds
    /// all the rows. The new page starts at <c>OFFSET</c> equal to the rows already loaded. Without an
    /// <c>ORDER BY</c> the server does not promise the same row order on each call, so a row can
    /// show twice or not at all. That is also true in other SQL clients.
    /// </summary>
    public async Task<QueryResultModel> FetchMoreAsync(QueryResultModel previous, CancellationToken cancellationToken = default)
    {
        Stopwatch total = Stopwatch.StartNew();

        if (previous.PageSql is null || !previous.HasMore)
            return previous;

        if (!_session.IsConnected)
            return QueryResultModel.Failure("Not connected to CamusDB.", null, total.Elapsed);

        try
        {
            return await ExecutePageAsync(_session.GetConnection(), previous.PageSql, previous, total, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return QueryResultModel.Failure("Query cancelled.", null, total.Elapsed);
        }
        catch (CamusException ex)
        {
            return QueryResultModel.Failure(CamusSessionService.Describe(ex), ex.Code, total.Elapsed);
        }
        catch (Exception ex)
        {
            return QueryResultModel.Failure(ex.Message, null, total.Elapsed);
        }
    }

    private async Task<QueryResultModel> ExecuteCoreAsync(string sql, bool paged, CancellationToken cancellationToken)
    {
        Stopwatch total = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(sql))
            return QueryResultModel.Failure("SQL is empty.", null, total.Elapsed);

        if (!_session.IsConnected)
            return QueryResultModel.Failure("Not connected to CamusDB.", null, total.Elapsed);

        try
        {
            CamusConnection connection = _session.GetConnection();

            // REST admin endpoints — CREATE/DROP DATABASE are not valid SQL non-queries over REST.
            if (TryParseCreateDatabase(sql, out string createName, out bool ifNotExists, out string? branchFrom))
            {
                return await ExecuteAdminAsync(
                    total,
                    cancellationToken,
                    async ct =>
                    {
                        if (branchFrom is not null)
                        {
                            await connection.CreateBranchDatabaseAsync(createName, branchFrom, ifNotExists, ct)
                                .ConfigureAwait(false);
                            return $"Branch database '{createName}' created from '{branchFrom}'.";
                        }

                        await connection.CreateDatabaseAsync(createName, ifNotExists, ct).ConfigureAwait(false);
                        return $"Database '{createName}' created.";
                    }).ConfigureAwait(false);
            }

            if (TryParseDropDatabase(sql, out string dropName))
            {
                return await ExecuteAdminAsync(
                    total,
                    cancellationToken,
                    async ct =>
                    {
                        await connection.DropDatabaseAsync(dropName, ct).ConfigureAwait(false);
                        return $"Database '{dropName}' dropped.";
                    }).ConfigureAwait(false);
            }

            if (paged && SqlScript.IsPageableSelect(sql))
                return await ExecutePageAsync(connection, sql.Trim(), previous: null, total, cancellationToken).ConfigureAwait(false);

            await using CamusCommand command = connection.CreateCamusCommand(sql);

            if (IsResultSetStatement(sql))
                return await ExecuteReaderAsync(command, total, _session.MaxRows, writeReturning: null, explain: IsExplainStatement(sql), stream: IsStreamStatement(sql), cancellationToken).ConfigureAwait(false);

            // ExecuteNonQuery asks the server to discard the RETURNING rows, so the reader path is the
            // one that shows them.
            if (IsWriteReturning(sql))
            {
                string rowsVerb = InsertPrefix.IsMatch(sql) ? "inserted" : "affected";
                return await ExecuteReaderAsync(command, total, _session.MaxRows, writeReturning: rowsVerb, explain: false, stream: false, cancellationToken).ConfigureAwait(false);
            }

            return await ExecuteNonQueryAsync(command, total, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return QueryResultModel.Failure("Query cancelled.", null, total.Elapsed);
        }
        catch (CamusException ex)
        {
            return QueryResultModel.Failure(CamusSessionService.Describe(ex), ex.Code, total.Elapsed);
        }
        catch (Exception ex)
        {
            return QueryResultModel.Failure(ex.Message, null, total.Elapsed);
        }
    }

    private static async Task<QueryResultModel> ExecuteAdminAsync(
        Stopwatch total,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<string>> action)
    {
        Stopwatch execute = Stopwatch.StartNew();
        string message = await action(cancellationToken).ConfigureAwait(false);
        execute.Stop();
        total.Stop();

        return new QueryResultModel
        {
            Success = true,
            IsResultSet = false,
            RowsAffected = 0,
            ExecuteDuration = execute.Elapsed,
            TotalDuration = total.Elapsed,
            Message = message,
        };
    }

    /// <summary>
    /// Runs <paramref name="sql"/> with <c>LIMIT pageSize + 1</c>. The extra row only tells that a next
    /// page exists, and the grid does not show it. One result holds at most
    /// <see cref="CamusSessionService.MaxRowsLimit"/> rows: the rows live in the circuit's memory, and
    /// each page makes the server scan to a deeper <c>OFFSET</c>.
    /// </summary>
    private async Task<QueryResultModel> ExecutePageAsync(
        CamusConnection connection,
        string sql,
        QueryResultModel? previous,
        Stopwatch total,
        CancellationToken cancellationToken)
    {
        int pageSize = Math.Max(1, _session.MaxRows);
        int offset = previous?.Rows.Count ?? 0;
        int limit = Math.Max(pageSize, CamusSessionService.MaxRowsLimit);

        // The last page before the ceiling is shorter, so the result stops exactly at it.
        int fetch = Math.Min(pageSize, limit - offset);
        if (fetch <= 0)
            return previous!;

        await using CamusCommand command = connection.CreateCamusCommand(SqlScript.WithPage(sql, fetch + 1, offset));
        QueryResultModel page = await ExecuteReaderAsync(command, total, fetch, writeReturning: null, explain: false, stream: true, cancellationToken)
            .ConfigureAwait(false);

        List<object?[]> rows = previous is null ? [] : [.. previous.Rows];
        rows.AddRange(page.Rows);

        // Truncated means "more rows exist" here, because the query asked for one row more than a page.
        bool atLimit = page.Truncated && rows.Count >= limit;
        bool hasMore = page.Truncated && !atLimit;

        return new QueryResultModel
        {
            Success = true,
            IsResultSet = true,
            Columns = previous?.Columns ?? page.Columns,
            Rows = rows,
            RowsReturned = rows.Count,
            MaxRows = pageSize,
            ExecuteDuration = page.ExecuteDuration,
            TotalDuration = page.TotalDuration,
            Message = hasMore ? $"Showing the first {rows.Count:N0} rows. More rows are available."
                : atLimit ? $"Showing the first {rows.Count:N0} rows, the most that one result holds. "
                    + "Add a WHERE condition, or a LIMIT and OFFSET, to see other rows."
                : null,
            PageSql = sql,
            HasMore = hasMore,
        };
    }

    private static async Task<QueryResultModel> ExecuteReaderAsync(
        CamusCommand command,
        Stopwatch total,
        int maxRows,
        string? writeReturning,
        bool explain,
        bool stream,
        CancellationToken cancellationToken)
    {
        Stopwatch execute = Stopwatch.StartNew();

        // The buffered reader holds the whole response before the first row is read, so maxRows would
        // cut a result only after all of it is in memory. The streaming reader pulls rows as they are
        // read, and disposing it at the cap ends the response.
        await using CamusDataReader reader = stream
            ? await command.ExecuteStreamReaderAsync(cancellationToken).ConfigureAwait(false)
            : await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        execute.Stop();

        List<QueryColumn> columns = new(reader.FieldCount);
        bool[] numeric = new bool[reader.FieldCount];
        for (int i = 0; i < reader.FieldCount; i++)
        {
            string typeName = reader.GetDataTypeName(i);
            numeric[i] = string.Equals(typeName, nameof(ColumnType.Numeric), StringComparison.Ordinal);

            columns.Add(new QueryColumn
            {
                Name = reader.GetName(i),
                TypeName = typeName,
                ClrType = reader.GetFieldType(i),
            });
        }

        List<object?[]> rows = [];
        bool truncated = false;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count >= maxRows)
            {
                truncated = true;
                break;
            }

            object?[] values = new object?[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++)
            {
                if (reader.IsDBNull(i))
                    continue;

                // GetValue gives a decimal, which throws for a NUMERIC wider than 28 digits. GetString
                // gives the exact text of every NUMERIC.
                if (numeric[i])
                {
                    values[i] = new NumericText(reader.GetString(i));
                    continue;
                }

                object value = reader.GetValue(i);
                values[i] = value is DBNull ? null : value;
            }

            rows.Add(values);
        }

        total.Stop();

        string? message = truncated
            ? $"Showing first {rows.Count} of more rows (cap {maxRows})."
            : null;

        // A write with a RETURNING list: writeReturning is the verb of the row count, "inserted" or
        // "affected".
        if (writeReturning is not null)
        {
            message = $"Statement completed. Rows {writeReturning}: {reader.RecordsAffected}."
                + (message is null ? "" : " " + message);
        }

        return new QueryResultModel
        {
            Success = true,
            IsResultSet = true,
            Columns = columns,
            Rows = rows,
            RowsReturned = rows.Count,
            RowsAffected = writeReturning is not null ? reader.RecordsAffected : null,
            Truncated = truncated,
            MaxRows = maxRows,
            ExecuteDuration = execute.Elapsed,
            TotalDuration = total.Elapsed,
            Message = message,
            // A table can have columns called node and detail, so the column shape alone does not
            // make a result a plan; the statement has to be an EXPLAIN as well.
            Plan = explain ? QueryPlanParser.TryParse(columns, rows) : null,
        };
    }

    private static async Task<QueryResultModel> ExecuteNonQueryAsync(
        CamusCommand command,
        Stopwatch total,
        CancellationToken cancellationToken)
    {
        Stopwatch execute = Stopwatch.StartNew();
        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        execute.Stop();
        total.Stop();

        return new QueryResultModel
        {
            Success = true,
            IsResultSet = false,
            RowsAffected = affected,
            ExecuteDuration = execute.Elapsed,
            TotalDuration = total.Elapsed,
            Message = $"Statement completed. Rows affected: {affected}.",
        };
    }

    private static bool IsResultSetStatement(string sql) => ResultSetPrefix.IsMatch(sql);

    private static bool IsExplainStatement(string sql) => ExplainPrefix.IsMatch(sql);

    private static bool IsStreamStatement(string sql) => StreamPrefix.IsMatch(sql);

    private static bool IsWriteReturning(string sql) =>
        WritePrefix.IsMatch(sql) && HasReturningKeyword(sql);

    /// <summary>
    /// True when RETURNING appears as a word outside string literals, backtick names, comments and
    /// <c>@parameters</c>. The lexer reserves RETURNING, so a bare word is always the clause; a
    /// column called <c>returning</c> has to be backticked.
    /// </summary>
    internal static bool HasReturningKeyword(string sql) => SqlScript.HasWord(sql, "RETURNING");

    private static bool TryParseCreateDatabase(
        string sql,
        out string name,
        out bool ifNotExists,
        out string? branchFrom)
    {
        name = "";
        ifNotExists = false;
        branchFrom = null;

        Match match = CreateDatabaseRegex.Match(sql);
        if (!match.Success)
            return false;

        ifNotExists = sql.Contains("IF NOT EXISTS", StringComparison.OrdinalIgnoreCase);
        name = UnquoteIdent(match.Groups["name"].Value);
        if (match.Groups["source"].Success && match.Groups["source"].Value.Length > 0)
            branchFrom = UnquoteIdent(match.Groups["source"].Value);

        return name.Length > 0;
    }

    private static bool TryParseDropDatabase(string sql, out string name)
    {
        name = "";
        Match match = DropDatabaseRegex.Match(sql);
        if (!match.Success)
            return false;

        name = UnquoteIdent(match.Groups["name"].Value);
        return name.Length > 0;
    }

    private static string UnquoteIdent(string ident)
    {
        if (ident.Length >= 2
            && ((ident[0] == '"' && ident[^1] == '"') || (ident[0] == '`' && ident[^1] == '`')))
        {
            return ident[1..^1];
        }

        return ident;
    }
}
