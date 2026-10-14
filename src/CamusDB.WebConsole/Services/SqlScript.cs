using System.Text;

namespace CamusDB.WebConsole.Services;

/// <summary>
/// One statement of a script. <c>Text</c> is the trimmed statement without its <c>;</c>.
/// <c>Start</c> and <c>End</c> bound the raw piece in the script: <c>Start</c> is the first character
/// after the previous <c>;</c>, and <c>End</c> is the index of this statement's <c>;</c>, or the
/// script length for the last statement.
/// </summary>
public sealed record SqlStatement(string Text, int Start, int End);

/// <summary>
/// Reads SQL text the way the CamusDB lexer does, without a parser: it splits a script into its
/// statements and finds words outside strings, quoted names and comments.
///
/// <para>The rules copy the server's <c>SqlScriptSplitter</c>, which copies its lexer
/// (<c>SQLParser.Language.analyzer.lex</c>). Change them together:</para>
/// <list type="bullet">
/// <item><c>'…'</c> and <c>"…"</c> strings, where a doubled quote is a literal quote and a backslash is
/// an ordinary character;</item>
/// <item><c>E'…'</c> and <c>E"…"</c> strings, where a backslash also escapes the next character;</item>
/// <item><c>`…`</c> quoted identifiers;</item>
/// <item><c>-- …</c> comments to the end of the line, and <c>/* … */</c> comments, which do not nest.</item>
/// </list>
/// An unterminated quote or comment runs to the end of the text. The server reports that error.
/// </summary>
public static class SqlScript
{
    internal enum TokenKind
    {
        Word,
        Parameter,
        String,
        QuotedIdentifier,
        Comment,
        Whitespace,
        Number,
        Symbol,
    }

    internal readonly record struct Token(TokenKind Kind, int Start, int End);

    /// <summary>
    /// Returns the statements of <paramref name="script"/> in order. A piece that holds only
    /// whitespace and comments is dropped, so a trailing <c>;</c> gives no empty statement.
    /// </summary>
    public static IReadOnlyList<SqlStatement> Split(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        List<SqlStatement> statements = [];
        int pieceStart = 0;
        bool hasCode = false;

        foreach (Token token in Tokenize(script))
        {
            if (token.Kind == TokenKind.Symbol && script[token.Start] == ';')
            {
                AddStatement(statements, script, pieceStart, token.Start, hasCode);
                pieceStart = token.End;
                hasCode = false;
                continue;
            }

            if (token.Kind is not (TokenKind.Whitespace or TokenKind.Comment))
                hasCode = true;
        }

        AddStatement(statements, script, pieceStart, script.Length, hasCode);
        return statements;
    }

    /// <summary>
    /// The statement that holds the character offset <paramref name="offset"/>, or null when the
    /// script has no statement. A cursor after the last statement gives the last statement. A cursor
    /// on the same line as a <c>;</c>, with only whitespace after it, gives the statement that the
    /// <c>;</c> ends, because that is the statement the user can see on that line.
    /// </summary>
    public static SqlStatement? StatementAt(string script, int offset)
    {
        IReadOnlyList<SqlStatement> statements = Split(script);
        if (statements.Count == 0)
            return null;

        offset = Math.Clamp(offset, 0, script.Length);

        for (int i = 0; i < statements.Count; i++)
        {
            SqlStatement statement = statements[i];
            if (offset > statement.End)
                continue;

            if (offset < statement.Start)
                return i > 0 ? statements[i - 1] : statement;

            if (i > 0 && statements[i - 1].End == statement.Start - 1 && IsBlankOnOneLine(script, statement.Start, offset))
                return statements[i - 1];

            return statement;
        }

        return statements[^1];
    }

    /// <summary>The first word of the statement, upper case, or an empty string.</summary>
    public static string FirstWord(string sql)
    {
        foreach (Token token in Tokenize(sql))
        {
            if (token.Kind is TokenKind.Whitespace or TokenKind.Comment)
                continue;

            return token.Kind == TokenKind.Word
                ? sql[token.Start..token.End].ToUpperInvariant()
                : "";
        }

        return "";
    }

    /// <summary>True when <paramref name="word"/> appears as a word anywhere in the statement.</summary>
    public static bool HasWord(string sql, string word) => FindWord(sql, word, topLevelOnly: false);

    /// <summary>True when <paramref name="word"/> appears as a word outside every parenthesis.</summary>
    public static bool HasTopLevelWord(string sql, string word) => FindWord(sql, word, topLevelOnly: true);

    /// <summary>
    /// True when the console can page through the statement: a single <c>SELECT</c> with no
    /// <c>LIMIT</c> or <c>OFFSET</c> of its own. The grammar puts <c>LIMIT</c> and <c>OFFSET</c> last
    /// in a <c>SELECT</c>, so the console can add them at the end.
    /// </summary>
    public static bool IsPageableSelect(string sql)
    {
        if (FirstWord(sql) != "SELECT")
            return false;

        if (Split(sql).Count != 1)
            return false;

        return !HasTopLevelWord(sql, "LIMIT") && !HasTopLevelWord(sql, "OFFSET");
    }

    /// <summary>
    /// Adds <c>LIMIT</c> and <c>OFFSET</c> to a statement that <see cref="IsPageableSelect"/> accepts.
    /// The clauses go on a new line, so a <c>--</c> comment at the end of the statement cannot
    /// hide them.
    /// </summary>
    public static string WithPage(string sql, int limit, int offset)
    {
        string page = $"\nLIMIT {Math.Max(1, limit)}";
        if (offset > 0)
            page += $" OFFSET {offset}";

        return sql.TrimEnd() + page;
    }

    /// <summary>
    /// Replaces each string literal with <c>'********'</c> when the statement sets a password
    /// (<c>CREATE USER … IDENTIFIED BY '…'</c>, <c>ALTER USER … IDENTIFIED BY '…'</c>). Other
    /// statements come back unchanged. The query history stores the result, and the history must
    /// never keep a password.
    /// </summary>
    public static string RedactSecrets(string sql)
    {
        if (!HasWord(sql, "IDENTIFIED"))
            return sql;

        StringBuilder builder = new(sql.Length);
        int last = 0;

        foreach (Token token in Tokenize(sql))
        {
            if (token.Kind != TokenKind.String)
                continue;

            builder.Append(sql, last, token.Start - last).Append("'********'");
            last = token.End;
        }

        builder.Append(sql, last, sql.Length - last);
        return builder.ToString();
    }

    internal static IEnumerable<Token> Tokenize(string sql)
    {
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            int start = i;

            if (char.IsWhiteSpace(c))
            {
                while (i < sql.Length && char.IsWhiteSpace(sql[i]))
                    i++;
                yield return new Token(TokenKind.Whitespace, start, i);
                continue;
            }

            if (c == '-' && Next(sql, i) == '-')
            {
                int end = sql.IndexOf('\n', i);
                i = end < 0 ? sql.Length : end;
                yield return new Token(TokenKind.Comment, start, i);
                continue;
            }

            if (c == '/' && Next(sql, i) == '*')
            {
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                yield return new Token(TokenKind.Comment, start, i);
                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                i = SkipQuoted(sql, i, backslashEscapes: false);
                yield return new Token(c == '`' ? TokenKind.QuotedIdentifier : TokenKind.String, start, i);
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                i++;
                while (i < sql.Length && (char.IsAsciiLetterOrDigit(sql[i]) || sql[i] == '_'))
                    i++;

                // E'…' and E"…": the E has to be a whole token, so name'x' is not an escape string.
                if (i - start == 1 && c is 'E' or 'e' && i < sql.Length && sql[i] is '\'' or '"')
                {
                    i = SkipQuoted(sql, i, backslashEscapes: true);
                    yield return new Token(TokenKind.String, start, i);
                    continue;
                }

                yield return new Token(TokenKind.Word, start, i);
                continue;
            }

            if (c == '@' && i + 1 < sql.Length && (char.IsAsciiLetter(sql[i + 1]) || sql[i + 1] == '_'))
            {
                i += 2;
                while (i < sql.Length && (char.IsAsciiLetterOrDigit(sql[i]) || sql[i] == '_'))
                    i++;
                yield return new Token(TokenKind.Parameter, start, i);
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                // 1E5 and 1_000 are one number, and 1E'x' is a number and a plain string.
                while (i < sql.Length && (char.IsAsciiLetterOrDigit(sql[i]) || sql[i] is '_' or '.'))
                    i++;
                yield return new Token(TokenKind.Number, start, i);
                continue;
            }

            i++;
            yield return new Token(TokenKind.Symbol, start, i);
        }
    }

    private static bool FindWord(string sql, string word, bool topLevelOnly)
    {
        int depth = 0;
        foreach (Token token in Tokenize(sql))
        {
            if (token.Kind == TokenKind.Symbol)
            {
                char c = sql[token.Start];
                if (c == '(')
                    depth++;
                else if (c == ')' && depth > 0)
                    depth--;
                continue;
            }

            if (token.Kind != TokenKind.Word || (topLevelOnly && depth > 0))
                continue;

            if (sql.AsSpan(token.Start, token.End - token.Start).Equals(word, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Steps past a quoted run that opens at <paramref name="start"/>. A doubled quote stays inside a
    /// string. A backslash escapes the next character only in an <c>E'…'</c> string.
    /// </summary>
    private static int SkipQuoted(string sql, int start, bool backslashEscapes)
    {
        char quote = sql[start];
        int i = start + 1;
        while (i < sql.Length)
        {
            char c = sql[i];

            if (backslashEscapes && c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == quote)
            {
                if (quote != '`' && Next(sql, i) == quote)
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

    private static char Next(string sql, int i) => i + 1 < sql.Length ? sql[i + 1] : '\0';

    private static bool IsBlankOnOneLine(string script, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (script[i] == '\n' || !char.IsWhiteSpace(script[i]))
                return false;
        }

        return true;
    }

    private static void AddStatement(List<SqlStatement> statements, string script, int start, int end, bool hasCode)
    {
        if (hasCode)
            statements.Add(new SqlStatement(script[start..end].Trim(), start, end));
    }
}
