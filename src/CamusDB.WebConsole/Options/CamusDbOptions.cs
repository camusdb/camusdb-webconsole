namespace CamusDB.WebConsole.Options;

public sealed class CamusDbOptions
{
    public const string SectionName = "CamusDB";

    public string Endpoint { get; set; } = "http://localhost:5095";

    public string Database { get; set; } = "demo";

    /// <summary>Wire protocol: rest (default) or grpc.</summary>
    public string Protocol { get; set; } = "rest";

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Maximum rows materialised into the results grid.</summary>
    public int MaxRows { get; set; } = 1000;
}
