namespace CamusDB.WebConsole.Services;

/// <summary>
/// Dark or light, for one render scope. App.razor reads the choice from a cookie during the static
/// render and hands it down, so the server paints the right theme before the circuit connects.
///
/// <para>The cookie value is browser-controlled. It is only ever compared against "light", and what
/// reaches the page is one of two constant strings, never the cookie itself.</para>
/// </summary>
public sealed class ConsoleTheme
{
    /// <summary>Written by wwwroot/js/theme.js when the visitor toggles the theme.</summary>
    public const string CookieName = "camus-theme";

    public const string Dark = "dark";

    public const string Light = "light";

    public bool IsDark { get; private set; } = true;

    public string Name => IsDark ? Dark : Light;

    /// <summary>Monaco theme id, defined in wwwroot/js/camus-sql.js.</summary>
    public string EditorTheme => IsDark ? "camus-dark" : "camus-light";

    /// <summary>Anything but "light" — including no cookie — is the dark default.</summary>
    public static string Normalize(string? name) =>
        string.Equals(name, Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;

    public void Apply(string? name) => IsDark = Normalize(name) == Dark;
}
