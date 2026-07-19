using System.Diagnostics;
using System.Text.RegularExpressions;
using CamusDB.Client;
using CamusDB.WebConsole.Models;

namespace CamusDB.WebConsole.Services;

public sealed class QueryExecutionService
{
    private static readonly Regex ResultSetPrefix = new(
        @"^\s*(SELECT|SHOW|EXPLAIN|WITH)\b",
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

    public async Task<QueryResultModel> ExecuteAsync(string sql, CancellationToken cancellationToken = default)
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

            await using CamusCommand command = connection.CreateCamusCommand(sql);

            if (IsResultSetStatement(sql))
                return await ExecuteReaderAsync(command, total, cancellationToken).ConfigureAwait(false);

            return await ExecuteNonQueryAsync(command, total, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return QueryResultModel.Failure("Query cancelled.", null, total.Elapsed);
        }
        catch (CamusException ex)
        {
            return QueryResultModel.Failure(ex.Message, ex.Code, total.Elapsed);
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

    private async Task<QueryResultModel> ExecuteReaderAsync(
        CamusCommand command,
        Stopwatch total,
        CancellationToken cancellationToken)
    {
        int maxRows = _session.MaxRows;
        Stopwatch execute = Stopwatch.StartNew();

        await using CamusDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        execute.Stop();

        List<QueryColumn> columns = new(reader.FieldCount);
        for (int i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(new QueryColumn
            {
                Name = reader.GetName(i),
                TypeName = reader.GetDataTypeName(i),
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
                object value = reader.GetValue(i);
                values[i] = value is DBNull ? null : value;
            }

            rows.Add(values);
        }

        total.Stop();

        return new QueryResultModel
        {
            Success = true,
            IsResultSet = true,
            Columns = columns,
            Rows = rows,
            RowsReturned = rows.Count,
            Truncated = truncated,
            MaxRows = maxRows,
            ExecuteDuration = execute.Elapsed,
            TotalDuration = total.Elapsed,
            Message = truncated
                ? $"Showing first {rows.Count} of more rows (cap {maxRows})."
                : null,
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
