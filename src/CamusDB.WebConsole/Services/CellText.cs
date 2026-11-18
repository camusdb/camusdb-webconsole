using System.Globalization;

namespace CamusDB.WebConsole.Services;

/// <summary>
/// The text that the results grid shows for a cell. The grid's filter and sort read the same text, and
/// a plain copy of cells writes it, so that a copy holds what the screen shows.
/// </summary>
public static class CellText
{
    public static string Format(object? value)
    {
        if (value is null)
            return "NULL";

        return value switch
        {
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
            bool b => b ? "true" : "false",
            Array arr => $"[{string.Join(", ", arr.Cast<object?>().Select(Format))}]",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };
    }
}
