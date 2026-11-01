using System.Text.Encodings.Web;
using System.Text.Json;

namespace CamusDB.WebConsole.Models;

/// <summary>
/// A JSONB cell, kept as the PostgreSQL text form the client gives (<c>{"a": 1, "b": [2]}</c>).
///
/// <para>The client reads a document as a plain string, so without this type the console cannot tell
/// a document from a STRING that holds the same text. The type lets the grid color the cell, the
/// JSON export write the document itself instead of a string that holds it, and an edit or delete
/// match the row with a <c>JSONB '…'</c> literal.</para>
///
/// <para>JSON <c>null</c> is the text <c>null</c>. SQL <c>NULL</c> is never a JsonbText: the cell is
/// then a C# null, as for every other type.</para>
/// </summary>
public readonly record struct JsonbText(string Text)
{
    /// <summary>The server's nesting limit for a document. A deeper one fails with CADB0419.</summary>
    public const int MaxDepth = 512;

    private static readonly JsonDocumentOptions ReadOptions = new() { MaxDepth = MaxDepth };

    private static readonly JsonWriterOptions IndentedOptions = new()
    {
        Indented = true,
        MaxDepth = MaxDepth,
        // The text goes into a text field, never into raw HTML, so only what JSON requires is escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonWriterOptions CompactOptions = IndentedOptions with { Indented = false };

    /// <summary>
    /// The document with 2-space indents, for the edit dialog. A number keeps its exact digits,
    /// because JsonElement writes the number token as it was read. The server stores the canonical
    /// form again, so the indents do not change the stored document. Text that does not parse comes
    /// back unchanged.
    /// </summary>
    public string Indented() => Rewrite(Text, IndentedOptions);

    /// <summary>
    /// <paramref name="text"/> on one line with no whitespace between tokens, for a SQL literal. Text
    /// that does not parse comes back unchanged, so the server gives the error.
    /// </summary>
    public static string Compact(string text) => Rewrite(text, CompactOptions);

    private static string Rewrite(string text, JsonWriterOptions options)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text, ReadOptions);
            using MemoryStream buffer = new();
            using (Utf8JsonWriter writer = new(buffer, options))
                document.RootElement.WriteTo(writer);

            return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    /// <summary>
    /// True when <paramref name="text"/> is one JSON document. System.Text.Json refuses what the
    /// server refuses most often (a trailing comma, a leading zero, NaN, a comment), so an edit fails
    /// in the dialog and not as CADB0418. The server checks again, and its check is the one that
    /// counts.
    /// </summary>
    public static bool IsValid(string text, out string? error)
    {
        error = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(text, ReadOptions);
            return true;
        }
        catch (JsonException ex)
        {
            error = "Expected a JSON document. " + ex.Message;
            return false;
        }
    }

    public override string ToString() => Text;
}
