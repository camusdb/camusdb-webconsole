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

    private static readonly Regex InsertPrefix = new(
        @"^\s*INSERT\b",
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
                return await ExecuteReaderAsync(command, total, insertReturning: false, cancellationToken).ConfigureAwait(false);

            // ExecuteNonQuery asks the server to discard the RETURNING rows, so the reader path is the
            // one that shows them.
            if (IsInsertReturning(sql))
                return await ExecuteReaderAsync(command, total, insertReturning: true, cancellationToken).ConfigureAwait(false);

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

    private async Task<QueryResultModel> ExecuteReaderAsync(
        CamusCommand command,
        Stopwatch total,
        bool insertReturning,
        CancellationToken cancellationToken)
    {
        int maxRows = _session.MaxRows;
        Stopwatch execute = Stopwatch.StartNew();

        await using CamusDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

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

        if (insertReturning)
        {
            message = $"Statement completed. Rows inserted: {reader.RecordsAffected}."
                + (message is null ? "" : " " + message);
        }

        return new QueryResultModel
        {
            Success = true,
            IsResultSet = true,
            Columns = columns,
            Rows = rows,
            RowsReturned = rows.Count,
            RowsAffected = insertReturning ? reader.RecordsAffected : null,
            Truncated = truncated,
            MaxRows = maxRows,
            ExecuteDuration = execute.Elapsed,
            TotalDuration = total.Elapsed,
            Message = message,
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

    private static bool IsInsertReturning(string sql) =>
        InsertPrefix.IsMatch(sql) && HasReturningKeyword(sql);

    /// <summary>
    /// True when RETURNING appears as a word outside string literals, backtick names, comments and
    /// <c>@parameters</c>. The lexer reserves RETURNING, so a bare word is always the clause; a
    /// column called <c>returning</c> has to be backticked.
    /// </summary>
    internal static bool HasReturningKeyword(string sql)
    {
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];

            if (c is '\'' or '"' or '`')
            {
                i = SkipQuoted(sql, i, c);
                continue;
            }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                int end = sql.IndexOf('\n', i);
                i = end < 0 ? sql.Length : end + 1;
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                continue;
            }

            if (char.IsAsciiLetter(c) || c is '_' or '@')
            {
                int start = i;
                i++;
                while (i < sql.Length && (char.IsAsciiLetterOrDigit(sql[i]) || sql[i] == '_'))
                    i++;

                if (c != '@' && sql.AsSpan(start, i - start).Equals("RETURNING", StringComparison.OrdinalIgnoreCase))
                    return true;

                continue;
            }

            i++;
        }

        return false;
    }

    /// <summary>
    /// Steps past a quoted run that opens at <paramref name="start"/>. A doubled quote stays inside the
    /// run, and so does a backslash escape in a string literal (the lexer's EscChr).
    /// </summary>
    private static int SkipQuoted(string sql, int start, char quote)
    {
        int i = start + 1;
        while (i < sql.Length)
        {
            char c = sql[i];

            if (c == '\\' && quote != '`')
            {
                i += 2;
                continue;
            }

            if (c == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return sql.Length;
    }

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
