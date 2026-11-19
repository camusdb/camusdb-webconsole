using System.Diagnostics;
using System.Text.RegularExpressions;
using CamusDB.Client;
using CamusDB.WebConsole.Models;

namespace CamusDB.WebConsole.Services;

public sealed class QueryExecutionService
{
    // A statement that starts with '(' is a query: a set operation whose first operand has its own
    // ORDER BY or LIMIT, as in (SELECT …) UNION SELECT …. DESCRIBE and DESC are SHOW COLUMNS.
    //
    // The prefixes are matched against the statement after a leading WITH clause
    // (SqlScript.SkipLeadingWith), so WITH s AS (…) UPDATE … is a write. A WITH stays in the two query
    // prefixes for a clause that SkipLeadingWith cannot read: the server then reports the error.
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

        string pageSql = previous.PageSql;
        return await RunAsync(
            sql: null,
            async transaction =>
            {
                try
                {
                    return await ExecutePageAsync(_session.GetConnection(), pageSql, previous, transaction, total, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return FailureOf(ex, total);
                }
            },
            total,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<QueryResultModel> ExecuteCoreAsync(string sql, bool paged, CancellationToken cancellationToken)
    {
        Stopwatch total = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(sql))
            return QueryResultModel.Failure("SQL is empty.", null, total.Elapsed);

        if (!_session.IsConnected)
            return QueryResultModel.Failure("Not connected to CamusDB.", null, total.Elapsed);

        TransactionStatement transactionStatement = SqlScript.TransactionStatementOf(sql);
        if (transactionStatement != TransactionStatement.None)
            return await ExecuteTransactionStatementAsync(transactionStatement, total, cancellationToken).ConfigureAwait(false);

        return await RunAsync(
            sql,
            transaction => ExecuteStatementAsync(sql, paged, transaction, total, cancellationToken),
            total,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one statement through <paramref name="run"/>. In autocommit the statement runs at once,
    /// with no transaction. While a transaction is open, the statement waits for the statements
    /// before it in the transaction and then runs in it. Then:
    /// <list type="bullet">
    /// <item>A statement that cannot run in a transaction (<see cref="TransactionRefusal"/>) is refused,
    /// and the transaction stays open, because nothing went to the server.</item>
    /// <item>A statement that fails closes the transaction. The server rolls back the transaction of a
    /// statement that fails, so the console must not show it as open.</item>
    /// </list>
    /// No statement goes to the server outside an open transaction. Such a statement would commit at
    /// once, and a later ROLLBACK would appear to undo it.
    /// </summary>
    private async Task<QueryResultModel> RunAsync(
        string? sql,
        Func<CamusTransaction?, Task<QueryResultModel>> run,
        Stopwatch total,
        CancellationToken cancellationToken)
    {
        if (!_session.InTransaction)
            return await run(null).ConfigureAwait(false);

        try
        {
            await _session.TransactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return QueryResultModel.Failure("Query cancelled. The transaction is still open.", null, total.Elapsed);
        }

        try
        {
            // The transaction can end while the statement waits for its turn.
            if (_session.Transaction is not { } transaction)
                return await run(null).ConfigureAwait(false);

            if (sql is not null && TransactionRefusal(sql) is { } refusal)
                return QueryResultModel.Failure(refusal + " The transaction is still open.", null, total.Elapsed);

            QueryResultModel result = await run(transaction).ConfigureAwait(false);
            if (result.Success)
            {
                _session.CountTransactionStatement();
                return result;
            }

            await _session.DiscardTransactionAsync().ConfigureAwait(false);
            return QueryResultModel.Failure(
                (result.ErrorMessage ?? "The statement failed.") + " The transaction was rolled back.",
                result.ErrorCode,
                result.TotalDuration);
        }
        finally
        {
            _session.TransactionGate.Release();
        }
    }

    /// <summary>
    /// Why the statement cannot run in the open transaction, or null when it can. A statement on a
    /// whole database (<c>CREATE</c>, <c>DROP</c>, <c>ALTER</c> or <c>RENAME DATABASE</c>) is refused.
    /// The console sends <c>CREATE</c> and <c>DROP DATABASE</c> to admin endpoints that take no
    /// transaction, and the server runs the others outside every transaction. So each one commits at
    /// once. A rename of the transaction's own database also leaves the transaction on a name that is
    /// gone. Each other statement goes to the server in the transaction, and the server accepts or
    /// refuses it.
    /// </summary>
    private static string? TransactionRefusal(string sql)
    {
        if (SqlScript.SchemaEffectOf(sql) != SchemaEffect.Databases)
            return null;

        return $"{SqlScript.FirstWord(sql)} DATABASE cannot run in a transaction, because a ROLLBACK cannot undo it. "
            + "Commit the transaction or roll it back first.";
    }

    /// <summary>
    /// Runs <c>BEGIN</c>, <c>COMMIT</c> or <c>ROLLBACK</c> on the session's transaction. The server has
    /// no SQL form of them over REST: the driver starts and ends a transaction with requests of its
    /// own. A <c>COMMIT</c> or a <c>ROLLBACK</c> closes the transaction also when it fails, because the
    /// server ends a transaction whose commit or rollback it refuses.
    /// </summary>
    private async Task<QueryResultModel> ExecuteTransactionStatementAsync(
        TransactionStatement statement,
        Stopwatch total,
        CancellationToken cancellationToken)
    {
        string stillOpen = _session.InTransaction ? " The transaction is still open." : "";

        if (statement == TransactionStatement.Unsupported)
        {
            return QueryResultModel.Failure(
                "The console runs BEGIN, START TRANSACTION, COMMIT and ROLLBACK with nothing after them. "
                + "To set the isolation level, run SET TRANSACTION ISOLATION LEVEL … as the first statement after BEGIN."
                + stillOpen,
                null,
                total.Elapsed);
        }

        try
        {
            await _session.TransactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return QueryResultModel.Failure("Query cancelled." + stillOpen, null, total.Elapsed);
        }

        try
        {
            Stopwatch execute = Stopwatch.StartNew();
            string message;

            switch (statement)
            {
                case TransactionStatement.Begin:
                    if (_session.InTransaction)
                    {
                        return QueryResultModel.Failure(
                            $"A transaction is already open in database '{_session.TransactionDatabase}'. "
                            + "Commit it or roll it back first. The transaction is still open.",
                            null,
                            total.Elapsed);
                    }

                    await _session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                    message = $"Transaction started in database '{_session.TransactionDatabase}'. "
                        + "The statements after it run in the transaction until COMMIT or ROLLBACK.";
                    break;

                case TransactionStatement.Commit:
                    if (!_session.InTransaction)
                        return QueryResultModel.Failure("No transaction is open, so there is nothing to commit.", null, total.Elapsed);

                    await _session.CommitTransactionAsync().ConfigureAwait(false);
                    message = "Transaction committed.";
                    break;

                default:
                    if (!_session.InTransaction)
                        return QueryResultModel.Failure("No transaction is open, so there is nothing to roll back.", null, total.Elapsed);

                    await _session.RollbackTransactionAsync().ConfigureAwait(false);
                    message = "Transaction rolled back.";
                    break;
            }

            execute.Stop();
            total.Stop();

            return new QueryResultModel
            {
                Success = true,
                IsResultSet = false,
                ExecuteDuration = execute.Elapsed,
                TotalDuration = total.Elapsed,
                Message = message,
            };
        }
        catch (Exception ex)
        {
            QueryResultModel failure = FailureOf(ex, total);
            if (statement == TransactionStatement.Begin)
                return failure;

            return QueryResultModel.Failure(
                failure.ErrorMessage + " The transaction is no longer open.",
                failure.ErrorCode,
                failure.TotalDuration);
        }
        finally
        {
            _session.TransactionGate.Release();
        }
    }

    private static QueryResultModel FailureOf(Exception ex, Stopwatch total) => ex switch
    {
        OperationCanceledException => QueryResultModel.Failure("Query cancelled.", null, total.Elapsed),
        CamusException camus => QueryResultModel.Failure(CamusSessionService.Describe(camus), camus.Code, total.Elapsed),
        _ => QueryResultModel.Failure(ex.Message, null, total.Elapsed),
    };

    /// <summary>
    /// Runs one statement that is not a transaction statement, with <paramref name="transaction"/> when
    /// it is not null.
    /// </summary>
    private async Task<QueryResultModel> ExecuteStatementAsync(
        string sql,
        bool paged,
        CamusTransaction? transaction,
        Stopwatch total,
        CancellationToken cancellationToken)
    {
        try
        {
            CamusConnection connection = _session.GetConnection();

            // REST admin endpoints — CREATE/DROP DATABASE are not valid SQL non-queries over REST. They
            // take no transaction, and RunAsync refuses them while a transaction is open.
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
                return await ExecutePageAsync(connection, sql.Trim(), previous: null, transaction, total, cancellationToken).ConfigureAwait(false);

            await using CamusCommand command = connection.CreateCamusCommand(sql);

            // The driver sends the transaction's identifiers with each kind of statement, DDL included.
            // So the server decides what can run in the transaction: it refuses TRUNCATE (CADB0538), and
            // the driver refuses it before it sends it.
            command.Transaction = transaction;

            // The server runs the statement after a leading WITH clause, so that statement chooses the
            // reader and the message.
            string body = SqlScript.SkipLeadingWith(sql);

            if (IsResultSetStatement(body))
            {
                bool stream = IsStreamStatement(body);
                return await ExecuteReaderAsync(command, total, _session.MaxRows, writeReturning: null, explain: IsExplainStatement(body), stream, readToEnd: stream && transaction is not null, cancellationToken).ConfigureAwait(false);
            }

            // ExecuteNonQuery asks the server to discard the RETURNING rows, so the reader path is the
            // one that shows them.
            if (IsWriteReturning(body))
            {
                string rowsVerb = InsertPrefix.IsMatch(body) ? "inserted" : "affected";
                return await ExecuteReaderAsync(command, total, _session.MaxRows, writeReturning: rowsVerb, explain: false, stream: false, readToEnd: false, cancellationToken).ConfigureAwait(false);
            }

            string? note = transaction is not null && SqlScript.CommitsOutsideTransaction(body)
                ? "CamusDB commits this statement at once, also in a transaction, so a ROLLBACK does not undo it."
                : null;

            return await ExecuteNonQueryAsync(command, total, note, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return FailureOf(ex, total);
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
        CamusTransaction? transaction,
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
        command.Transaction = transaction;
        QueryResultModel page = await ExecuteReaderAsync(command, total, fetch, writeReturning: null, explain: false, stream: true, readToEnd: transaction is not null, cancellationToken)
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
        bool readToEnd,
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
        bool[] jsonb = new bool[reader.FieldCount];
        for (int i = 0; i < reader.FieldCount; i++)
        {
            string typeName = reader.GetDataTypeName(i);
            numeric[i] = string.Equals(typeName, nameof(ColumnType.Numeric), StringComparison.Ordinal);
            jsonb[i] = string.Equals(typeName, nameof(ColumnType.Jsonb), StringComparison.Ordinal);

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

                // In a transaction, the rows after the cap are read and dropped. A stream that the
                // console closes before its end is a request that the client stopped, and the server
                // rolls back the transaction of such a request.
                if (readToEnd)
                    continue;

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

                // GetString gives the PostgreSQL text form of the document on every transport. The
                // client asks REST for that form, so a JSON null document is the text "null" here
                // and SQL NULL is caught by IsDBNull above.
                if (jsonb[i])
                {
                    values[i] = new JsonbText(reader.GetString(i));
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

    /// <param name="note">A sentence that the message adds after the row count, or null.</param>
    private static async Task<QueryResultModel> ExecuteNonQueryAsync(
        CamusCommand command,
        Stopwatch total,
        string? note,
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
            Message = $"Statement completed. Rows affected: {affected}." + (note is null ? "" : " " + note),
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
