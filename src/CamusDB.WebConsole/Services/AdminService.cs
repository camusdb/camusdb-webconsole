using System.Globalization;
using CamusDB.Client;
using CamusDB.WebConsole.Models;

namespace CamusDB.WebConsole.Services;

/// <summary>
/// Runs the node-local administration statements — <c>SHOW ENGINE STATS</c> and <c>SHOW VARIABLES</c> —
/// and shapes their rows for the administration views.
///
/// <para>Both answer for <em>the node that served them</em> and never forward to the leader, so these
/// views describe the node behind <see cref="CamusSessionService.Endpoint"/> and no other. Comparing a
/// cluster means pointing the console at each node in turn.</para>
///
/// <para>Both require a superuser when the server has authentication enabled — no per-database grant
/// scopes down Raft topology or a node's whole security posture — and a non-superuser gets
/// <see cref="CamusSessionService.PrivilegeDeniedCode"/>.</para>
/// </summary>
public sealed class AdminService
{
    private const string EngineStatsStatement = "SHOW ENGINE STATS";
    private const string VariablesStatement = "SHOW VARIABLES";

    /// <summary>Shown next to the pattern box, and used as the rejection message.</summary>
    public const string PatternHelp =
        "LIKE pattern: % matches any run of characters, _ exactly one. Matching is case-sensitive and "
        + "names are lowercase, so 'TTL_%' matches nothing.";

    private const string InvalidPatternMessage =
        "A pattern may only contain letters, digits, and _ % . - , = : / and spaces.";

    private readonly CamusSessionService _session;

    public AdminService(CamusSessionService session)
    {
        _session = session;
    }

    public async Task<EngineStatsSnapshot> LoadEngineStatsAsync(
        string? pattern,
        CancellationToken cancellationToken = default)
    {
        List<EngineStatRow> rows = [];

        await ReadAsync(
            BuildStatement(EngineStatsStatement, pattern),
            (reader, ordinals) => rows.Add(new EngineStatRow
            {
                Node = ReadString(reader, ordinals, "node") ?? "",
                Source = ReadString(reader, ordinals, "source") ?? "",
                Metric = ReadString(reader, ordinals, "metric") ?? "",
                Tags = ReadString(reader, ordinals, "tags") ?? "",
                Kind = ReadString(reader, ordinals, "kind") ?? "",
                Count = ReadDouble(reader, ordinals, "count"),
                Total = ReadDouble(reader, ordinals, "total"),
                Min = ReadDouble(reader, ordinals, "min"),
                Max = ReadDouble(reader, ordinals, "max"),
                Last = ReadDouble(reader, ordinals, "last"),
            }),
            cancellationToken).ConfigureAwait(false);

        return new EngineStatsSnapshot { TakenAtUtc = DateTime.UtcNow, Rows = rows };
    }

    public async Task<IReadOnlyList<VariableRow>> LoadVariablesAsync(
        string? pattern,
        CancellationToken cancellationToken = default)
    {
        List<VariableRow> rows = [];

        await ReadAsync(
            BuildStatement(VariablesStatement, pattern),
            (reader, ordinals) =>
            {
                string name = ReadString(reader, ordinals, "variable") ?? "";
                if (name.Length == 0)
                    return;

                rows.Add(new VariableRow
                {
                    Variable = name,
                    Value = ReadString(reader, ordinals, "value"),
                    Type = ReadString(reader, ordinals, "type") ?? "",
                    Default = ReadString(reader, ordinals, "default"),
                    SourceLayer = ReadString(reader, ordinals, "source") ?? "",
                });
            },
            cancellationToken).ConfigureAwait(false);

        return rows;
    }

    /// <summary>
    /// The pattern reaches the server inside a SQL string literal, and these statements have no
    /// parameter form, so the console constrains what may go in rather than escaping its way out.
    /// Variable and metric names are lowercase identifiers and tags are <c>k=v</c> pairs, so nothing
    /// legitimate needs a quote or a backslash.
    /// </summary>
    public static bool IsValidPattern(string pattern) =>
        pattern.All(c =>
            char.IsAsciiLetterOrDigit(c) || c is '_' or '%' or '.' or '-' or ',' or '=' or ':' or '/' or ' ');

    private static string BuildStatement(string statement, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return statement;

        string trimmed = pattern.Trim();
        if (!IsValidPattern(trimmed))
            throw new ArgumentException(InvalidPatternMessage, nameof(pattern));

        return $"{statement} LIKE '{trimmed}'";
    }

    private async Task ReadAsync(
        string sql,
        Action<CamusDataReader, Dictionary<string, int>> readRow,
        CancellationToken cancellationToken)
    {
        await using CamusCommand command = _session.GetConnection().CreateCamusCommand(sql);
        await using CamusDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, int> ordinals = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < reader.FieldCount; i++)
            ordinals[reader.GetName(i)] = i;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            readRow(reader, ordinals);
    }

    private static object? ReadValue(CamusDataReader reader, Dictionary<string, int> ordinals, string name)
    {
        if (!ordinals.TryGetValue(name, out int ordinal))
            return null;

        object value = reader.GetValue(ordinal);
        return value is DBNull ? null : value;
    }

    private static string? ReadString(CamusDataReader reader, Dictionary<string, int> ordinals, string name) =>
        ReadValue(reader, ordinals, name) is object value
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : null;

    private static double? ReadDouble(CamusDataReader reader, Dictionary<string, int> ordinals, string name)
    {
        object? value = ReadValue(reader, ordinals, name);

        return value switch
        {
            null => null,
            double d => d,
            float f => f,
            long l => l,
            int i => i,
            decimal m => (double)m,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
            _ => null,
        };
    }
}
