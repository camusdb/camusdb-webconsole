using System.Text;
using CamusDB.WebConsole.Models;

namespace CamusDB.WebConsole.Services;

/// <summary>
/// Pulls the table and per-column COMMENT clauses out of what SHOW CREATE TABLE emits.
///
/// <para>This exists because comments have no other read surface: the COMMENT ON spec explicitly
/// declined to add a column to SHOW COLUMNS, and there is no information_schema. The input is the
/// server's own rendering (SchemaQuerier.ShowCreateTable), so the shapes handled here are exactly
/// the ones it produces — backticked identifiers, single-quoted literals in either the plain
/// <c>'…'</c> or the escaped <c>E'…'</c> form, and a trailing table-level comment after the closing
/// parenthesis.</para>
/// </summary>
public static class ShowCreateTableParser
{
    private enum TokenKind
    {
        Identifier,
        Word,
        Literal,
        Symbol,
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Depth);

    public static TableCommentInfo Parse(string? createTableSql)
    {
        if (string.IsNullOrWhiteSpace(createTableSql))
            return new TableCommentInfo();

        List<Token> tokens = Tokenize(createTableSql);

        Dictionary<string, string> columnComments = new(StringComparer.OrdinalIgnoreCase);
        string? tableComment = null;

        // The column list is the first depth-1 region. Items inside it are comma-separated; an item
        // whose first token is a backticked identifier is a column, one starting with PRIMARY / KEY /
        // UNIQUE / CONSTRAINT is an index or a check and is skipped.
        int i = 0;
        while (i < tokens.Count && !(tokens[i].Kind == TokenKind.Symbol && tokens[i].Text == "("))
            i++;

        if (i < tokens.Count)
        {
            i++; // step past the opening parenthesis
            List<Token> item = [];

            while (i < tokens.Count)
            {
                Token token = tokens[i];

                bool endOfItem = token.Kind == TokenKind.Symbol
                    && ((token.Text == "," && token.Depth == 1) || (token.Text == ")" && token.Depth == 0));

                if (endOfItem)
                {
                    CollectColumnComment(item, columnComments);
                    item.Clear();

                    if (token.Text == ")")
                    {
                        i++;
                        break;
                    }
                }
                else
                {
                    item.Add(token);
                }

                i++;
            }
        }

        // Whatever follows the closing parenthesis is the table-level tail: COMMENT '…' then an
        // optional SET (...). Only the depth-0 COMMENT is the table's.
        for (; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Depth == 0
                && tokens[i].Kind == TokenKind.Word
                && tokens[i].Text.Equals("COMMENT", StringComparison.OrdinalIgnoreCase)
                && tokens[i + 1].Kind == TokenKind.Literal)
            {
                tableComment = tokens[i + 1].Text;
                break;
            }
        }

        return new TableCommentInfo
        {
            TableComment = tableComment,
            ColumnComments = columnComments,
        };
    }

    private static void CollectColumnComment(List<Token> item, Dictionary<string, string> into)
    {
        if (item.Count == 0 || item[0].Kind != TokenKind.Identifier)
            return;

        string column = item[0].Text;

        for (int i = 1; i + 1 < item.Count; i++)
        {
            if (item[i].Kind == TokenKind.Word
                && item[i].Text.Equals("COMMENT", StringComparison.OrdinalIgnoreCase)
                && item[i + 1].Kind == TokenKind.Literal)
            {
                into[column] = item[i + 1].Text;
                return;
            }
        }
    }

    /// <summary>
    /// Splits the DDL into tokens, decoding literals as it goes. Depth is the parenthesis nesting
    /// <em>before</em> the token, so the closing parenthesis of the column list reports depth 0 and
    /// commas inside an index's column list report depth 2 — which is what keeps them from being
    /// mistaken for item separators.
    /// </summary>
    private static List<Token> Tokenize(string sql)
    {
        List<Token> tokens = [];
        int depth = 0;
        int i = 0;

        while (i < sql.Length)
        {
            char c = sql[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '`')
            {
                int end = sql.IndexOf('`', i + 1);
                if (end < 0)
                    break;

                tokens.Add(new Token(TokenKind.Identifier, sql[(i + 1)..end], depth));
                i = end + 1;
                continue;
            }

            // E'…' is the escaped literal form the server falls back to for control characters.
            bool escaped = (c is 'E' or 'e') && i + 1 < sql.Length && sql[i + 1] == '\'';
            if (c == '\'' || escaped)
            {
                i = ReadLiteral(sql, i, escaped, depth, tokens);
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                int start = i;
                while (i < sql.Length && (char.IsAsciiLetterOrDigit(sql[i]) || sql[i] == '_'))
                    i++;

                tokens.Add(new Token(TokenKind.Word, sql[start..i], depth));
                continue;
            }

            if (c == '(')
            {
                tokens.Add(new Token(TokenKind.Symbol, "(", depth));
                depth++;
                i++;
                continue;
            }

            if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
                tokens.Add(new Token(TokenKind.Symbol, ")", depth));
                i++;
                continue;
            }

            tokens.Add(new Token(TokenKind.Symbol, c.ToString(), depth));
            i++;
        }

        return tokens;
    }

    private static int ReadLiteral(string sql, int start, bool escaped, int depth, List<Token> tokens)
    {
        int i = escaped ? start + 2 : start + 1;
        StringBuilder value = new();

        while (i < sql.Length)
        {
            char c = sql[i];

            if (escaped && c == '\\' && i + 1 < sql.Length)
            {
                i = ReadEscape(sql, i + 1, value);
                continue;
            }

            if (c == '\'')
            {
                // A doubled quote is one literal quote; a single one closes the literal.
                if (i + 1 < sql.Length && sql[i + 1] == '\'')
                {
                    value.Append('\'');
                    i += 2;
                    continue;
                }

                i++;
                break;
            }

            value.Append(c);
            i++;
        }

        tokens.Add(new Token(TokenKind.Literal, value.ToString(), depth));
        return i;
    }

    private static int ReadEscape(string sql, int i, StringBuilder value)
    {
        char kind = sql[i];

        switch (kind)
        {
            case '0': value.Append('\0'); return i + 1;
            case 'a': value.Append('\a'); return i + 1;
            case 'b': value.Append('\b'); return i + 1;
            case 'f': value.Append('\f'); return i + 1;
            case 'n': value.Append('\n'); return i + 1;
            case 'r': value.Append('\r'); return i + 1;
            case 't': value.Append('\t'); return i + 1;
            case 'v': value.Append('\v'); return i + 1;
            case 'u' when i + 4 < sql.Length
                && int.TryParse(
                    sql.AsSpan(i + 1, 4),
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int code):
                value.Append((char)code);
                return i + 5;
            default:
                value.Append(kind);
                return i + 1;
        }
    }
}
