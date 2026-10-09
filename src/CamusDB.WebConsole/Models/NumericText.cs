namespace CamusDB.WebConsole.Models;

/// <summary>
/// A NUMERIC cell, kept as the canonical decimal text the server sends ("1.5", "1200", "0").
///
/// <para>A NUMERIC holds 38 digits, 9 of them after the point. A <see cref="decimal"/> holds 28 or 29,
/// so the client throws on a wide value rather than round it. The console never does arithmetic on a
/// cell, so it keeps the text: the grid shows every digit, and an edit or delete matches the row with
/// the exact <c>NUMERIC '…'</c> literal instead of a float that compares as a double.</para>
/// </summary>
public readonly record struct NumericText(string Text)
{
    public override string ToString() => Text;
}
