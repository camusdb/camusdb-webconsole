using System.Globalization;
using System.Reflection;

namespace CamusDB.WebConsole.Hosting;

/// <summary>
/// The command line of the <c>camusdb-webconsole</c> tool. It reads the console's own options and passes
/// every other argument to the ASP.NET Core host unchanged, so the host's options (<c>--urls</c>,
/// <c>--environment</c>, <c>--CamusDB:LockEndpoint=true</c>, …) still work.
///
/// <para>Each option takes its value as <c>--name value</c> or <c>--name=value</c>. The password has no
/// option on purpose: a command line is visible to every user of the machine in the process list. Use
/// <c>CamusDB__Password</c> or the Configure dialog.</para>
/// </summary>
public sealed class ConsoleCommandLine
{
    /// <summary>The port of the local address when nothing else sets one.</summary>
    public const int DefaultPort = 5164;

    private static readonly Dictionary<string, string> SettingOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["--endpoint"] = "CamusDB:Endpoint",
        ["--database"] = "CamusDB:Database",
        ["--protocol"] = "CamusDB:Protocol",
        ["--user"] = "CamusDB:User",
    };

    public bool ShowHelp { get; private init; }

    public bool ShowVersion { get; private init; }

    /// <summary>True when the browser must not open at startup.</summary>
    public bool NoBrowser { get; private init; }

    /// <summary>The port given with <c>--port</c>; 0 asks for any free port.</summary>
    public int? Port { get; private init; }

    /// <summary>Configuration values from the console's options, keyed as in appsettings.json.</summary>
    public IReadOnlyDictionary<string, string?> Settings { get; private init; } = new Dictionary<string, string?>();

    /// <summary>The arguments that are not the console's own, for the ASP.NET Core host.</summary>
    public string[] HostArgs { get; private init; } = [];

    /// <summary>Why the command line is not valid, or null.</summary>
    public string? Error { get; private init; }

    public static ConsoleCommandLine Parse(string[] args)
    {
        Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase);
        List<string> hostArgs = [];
        bool help = false, version = false, noBrowser = false;
        int? port = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            int equals = arg.IndexOf('=', StringComparison.Ordinal);
            string name = equals > 0 ? arg[..equals] : arg;
            string? inlineValue = equals > 0 ? arg[(equals + 1)..] : null;

            switch (name.ToLowerInvariant())
            {
                case "-h" or "--help" or "-?":
                    help = true;
                    continue;

                case "--version":
                    version = true;
                    continue;

                case "--no-browser":
                    noBrowser = true;
                    continue;

                case "--port":
                    {
                        if (!TryTakeValue(args, ref i, inlineValue, out string? value))
                            return Invalid("--port needs a value.");

                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed > 65535)
                            return Invalid($"--port must be a number from 0 to 65535, got '{value}'.");

                        port = parsed;
                        continue;
                    }
            }

            if (SettingOptions.TryGetValue(name, out string? key))
            {
                if (!TryTakeValue(args, ref i, inlineValue, out string? value) || string.IsNullOrWhiteSpace(value))
                    return Invalid($"{name} needs a value.");

                settings[key] = value;
                continue;
            }

            hostArgs.Add(arg);
        }

        return new ConsoleCommandLine
        {
            ShowHelp = help,
            ShowVersion = version,
            NoBrowser = noBrowser,
            Port = port,
            Settings = settings,
            HostArgs = [.. hostArgs],
        };
    }

    /// <summary>
    /// The content root for the host, or null to keep the host's default (the current directory).
    ///
    /// <para>A .NET tool starts in whatever directory the user is in, so the default would find neither
    /// <c>wwwroot</c> nor <c>appsettings.json</c>. Outside Development, the console therefore uses the
    /// directory of its own files. In the Docker image that is the working directory already. Development
    /// keeps the default, because <c>dotnet run</c> serves the project's own files. A content root set
    /// with <c>--contentRoot</c> or an environment variable always wins.</para>
    /// </summary>
    public static string? ContentRoot(string[] hostArgs)
    {
        if (HasHostSetting(hostArgs, "contentRoot", "ASPNETCORE_CONTENTROOT", "DOTNET_CONTENTROOT"))
            return null;

        string? environment = HostSetting(hostArgs, "environment", "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT");
        if (string.Equals(environment, Environments.Development, StringComparison.OrdinalIgnoreCase))
            return null;

        return AppContext.BaseDirectory;
    }

    /// <summary>The version of the console, without the source revision that the build adds.</summary>
    public static string Version
    {
        get
        {
            string? informational = typeof(ConsoleCommandLine).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(informational))
                return typeof(ConsoleCommandLine).Assembly.GetName().Version?.ToString() ?? "unknown";

            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }
    }

    public static string HelpText => $"""
        CamusDB Web Console {Version}

        Usage: camusdb-webconsole [options]

        Starts the console on http://127.0.0.1:{DefaultPort} and opens it in the browser.

        Options:
          --endpoint <url>      CamusDB server to connect to (default: http://localhost:5095)
          --database <name>     Database to open first
          --protocol <name>     rest or grpc (default: rest)
          --user <name>         User to sign in as; give the password in the Configure dialog
          --port <number>       Local port of the console; 0 picks a free port (default: {DefaultPort})
          --no-browser          Do not open the browser
          --version             Show the version
          -h, --help            Show this help

        The console listens on 127.0.0.1 only. To serve other machines, set the address yourself with
        --urls or ASPNETCORE_URLS, and read the security notes first:
        https://github.com/camusdb/camusdb-webconsole/blob/main/SECURITY.md

        Every setting of appsettings.json can also come from the environment, for example
        CamusDB__Endpoint or CamusDB__Password.
        """;

    private static bool TryTakeValue(string[] args, ref int i, string? inlineValue, out string? value)
    {
        if (inlineValue is not null)
        {
            value = inlineValue;
            return true;
        }

        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = args[++i];
            return true;
        }

        value = null;
        return false;
    }

    private static bool HasHostSetting(string[] hostArgs, string key, params string[] variables) =>
        HostSetting(hostArgs, key, variables) is not null;

    /// <summary>A host setting from <c>--key value</c>, <c>--key=value</c> or the first environment variable that is set.</summary>
    private static string? HostSetting(string[] hostArgs, string key, params string[] variables)
    {
        string option = "--" + key;
        for (int i = 0; i < hostArgs.Length; i++)
        {
            if (hostArgs[i].Equals(option, StringComparison.OrdinalIgnoreCase))
                return i + 1 < hostArgs.Length ? hostArgs[i + 1] : "";

            if (hostArgs[i].StartsWith(option + "=", StringComparison.OrdinalIgnoreCase))
                return hostArgs[i][(option.Length + 1)..];
        }

        return variables
            .Select(Environment.GetEnvironmentVariable)
            .FirstOrDefault(value => !string.IsNullOrEmpty(value));
    }

    private static ConsoleCommandLine Invalid(string error) => new() { Error = error };
}
