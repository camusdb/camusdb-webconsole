using CamusDB.WebConsole.Endpoints;
using CamusDB.WebConsole.Options;
using CamusDB.WebConsole.Services;
using MudBlazor;
using MudBlazor.Services;
using CamusDB.WebConsole.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CamusDbOptions>(builder.Configuration.GetSection(CamusDbOptions.SectionName));

IConfigurationSection launchSection = builder.Configuration.GetSection(ConsoleLaunchOptions.SectionName);
builder.Services.Configure<ConsoleLaunchOptions>(launchSection);

// The configuration binder maps a scalar to string[] by producing nothing at all, so
// `ConsoleLaunch__AllowedEndpoints=https://a,https://b` — the obvious way to write a list in one
// environment variable — would bind to an empty allowlist and silently leave the endpoint override
// wide open. Picking the scalar up here makes that form work; EndpointAllowList splits it.
builder.Services.PostConfigure<ConsoleLaunchOptions>(options =>
{
    string? scalar = launchSection["AllowedEndpoints"];

    if (!string.IsNullOrWhiteSpace(scalar))
        options.AllowedEndpoints = [.. options.AllowedEndpoints, scalar];
});
builder.Services.AddMudServices(config =>
{
    config.PopoverOptions.ThrowOnDuplicateProvider = false;
    config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
});

builder.Services.AddScoped<CamusSessionService>();
builder.Services.AddScoped<ConsolePreferencesService>();
builder.Services.AddScoped<SchemaExplorerService>();
builder.Services.AddScoped<QueryExecutionService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<AdminService>();
builder.Services.AddScoped<BackupService>();

// Singleton: launch tickets have to outlive the request that created them and be visible to the
// request that redeems them. Scoped per circuit, ConsoleBranding is what each render actually reads.
builder.Services.AddSingleton<ConsoleLaunchStore>();
builder.Services.AddScoped<ConsoleBranding>();
builder.Services.AddSingleton(sp => EndpointAllowList.Parse(
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ConsoleLaunchOptions>>().Value.AllowedEndpoints));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

ValidateCamusDbOptions(app);
ValidateConsoleLaunchOptions(app);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    IHeaderDictionary headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Content-Security-Policy"] = "frame-ancestors 'none'";
    await next();
});

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapConsoleLaunch();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>
/// Fails startup when the console is told to accept tokens only while a user or a password is also
/// configured. The two cannot both be honoured, and dropping the credentials quietly would leave an
/// operator looking at an unauthenticated console with nothing to explain it.
/// </summary>
static void ValidateCamusDbOptions(WebApplication app)
{
    CamusDbOptions camus = app.Services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<CamusDbOptions>>().Value;

    if (!camus.RequireAccessToken)
        return;

    if (!string.IsNullOrWhiteSpace(camus.User) || !string.IsNullOrEmpty(camus.Password))
    {
        throw new InvalidOperationException(
            $"{CamusDbOptions.SectionName}:RequireAccessToken is true, so this console accepts an access "
            + $"token only. Clear {CamusDbOptions.SectionName}:User and {CamusDbOptions.SectionName}:Password "
            + $"(CamusDB__User, CamusDB__Password), or turn the flag off.");
    }

    app.Logger.LogInformation(
        "{Section}:RequireAccessToken is on: user/password sign-in is refused and the Configure dialog "
        + "offers an access token only.",
        CamusDbOptions.SectionName);
}

/// <summary>
/// Fails startup rather than serving a half-configured launch surface. Both checks guard something
/// that would otherwise be discovered by an attacker before an operator: a launch endpoint with a
/// weak or absent key is an open door for planting access tokens into visitors' sessions, and a
/// default brand name that cannot be rendered would be swapped for something else at request time,
/// which is exactly the kind of silent substitution that hides a misconfiguration.
/// </summary>
static void ValidateConsoleLaunchOptions(WebApplication app)
{
    ConsoleLaunchOptions options = app.Services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<ConsoleLaunchOptions>>().Value;

    _ = BrandNameSanitizer.Require(options.DefaultBrandName, $"{ConsoleLaunchOptions.SectionName}:DefaultBrandName");

    if (!options.Enabled)
        return;

    if (options.ApiKey.Length < ConsoleLaunchOptions.MinApiKeyLength)
    {
        throw new InvalidOperationException(
            $"{ConsoleLaunchOptions.SectionName}:Enabled is true but {ConsoleLaunchOptions.SectionName}:ApiKey "
            + $"is missing or shorter than {ConsoleLaunchOptions.MinApiKeyLength} characters. Set it from the "
            + "environment (ConsoleLaunch__ApiKey) with a value from a CSPRNG.");
    }

    CamusDbOptions camus = app.Services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<CamusDbOptions>>().Value;

    // Resolving it here is what turns a malformed entry into a startup failure rather than a
    // request-time surprise, and it logs what was understood so a typo that parsed into something
    // unintended is visible in the log rather than only in a later refusal.
    EndpointAllowList allowList = app.Services.GetRequiredService<EndpointAllowList>();

    if (allowList.IsEmpty && !camus.LockEndpoint)
    {
        app.Logger.LogWarning(
            "Console launch is enabled with neither {Section}:AllowedEndpoints nor CamusDB:LockEndpoint set. "
            + "A launch payload may then name any http(s) URL, which this server will open on the visitor's "
            + "behalf — set one of them on any console reachable beyond localhost.",
            ConsoleLaunchOptions.SectionName);
    }
    else if (!allowList.IsEmpty)
    {
        app.Logger.LogInformation(
            "Console launch endpoint allowlist ({Count} entries): {AllowList}",
            allowList.Count, allowList.ToString());
    }
}
