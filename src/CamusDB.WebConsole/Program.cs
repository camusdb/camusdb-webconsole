using System.Globalization;
using System.Threading.RateLimiting;
using CamusDB.WebConsole.Endpoints;
using CamusDB.WebConsole.Options;
using CamusDB.WebConsole.Services;
using Microsoft.AspNetCore.RateLimiting;
using MudBlazor;
using MudBlazor.Services;
using CamusDB.WebConsole.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CamusDbOptions>(builder.Configuration.GetSection(CamusDbOptions.SectionName));
builder.Services.Configure<ConsoleSecurityOptions>(
    builder.Configuration.GetSection(ConsoleSecurityOptions.SectionName));

// Read once, before the container is built: the rate limiter is configured here, and IOptions is not
// resolvable yet. Everything else reads the same values through IOptions.
ConsoleSecurityOptions security =
    builder.Configuration.GetSection(ConsoleSecurityOptions.SectionName).Get<ConsoleSecurityOptions>()
    ?? new ConsoleSecurityOptions();

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

// Singleton for the same reason as the store, and for a sharper one: a counter of failed sign-ins
// that lived with the circuit would be reset by opening a new circuit, which is one page load.
builder.Services.AddSingleton<LoginAttemptThrottle>();
builder.Services.AddScoped<ConsoleBranding>();
builder.Services.AddSingleton(sp => EndpointAllowList.Parse(
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ConsoleLaunchOptions>>().Value.AllowedEndpoints));

if (security.RateLimitEnabled)
{
    // Fixed window per caller address. The launch endpoints are the only ones limited: a limiter over
    // the Blazor circuit's own transport would count reconnects and long-polling as abuse.
    builder.Services.AddRateLimiter(limiter =>
    {
        limiter.AddPolicy(ConsoleLaunchEndpoints.RateLimitPolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                // Path and caller, not caller alone: the two legs are called by different parties —
                // the vendor's backend and the visitor's browser — and one leg must not spend the
                // other's allowance when they happen to share an egress address.
                $"{context.Request.Path}|{ClientAddress.Key(context)}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = security.LaunchPermitLimit > 0 ? security.LaunchPermitLimit : 30,
                    Window = TimeSpan.FromSeconds(security.LaunchWindowSeconds > 0 ? security.LaunchWindowSeconds : 60),

                    // No queue: a caller over the limit is told so now. Holding its request open would
                    // spend a connection on a caller that has already been judged too eager.
                    QueueLimit = 0,
                }));

        limiter.OnRejected = async (context, cancellationToken) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
            {
                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            }

            context.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("ConsoleLaunch")
                .LogWarning(
                    "Rate-limited a console launch request from {ClientKey} to {Path}.",
                    ClientAddress.Key(context.HttpContext), context.HttpContext.Request.Path);

            await context.HttpContext.Response.WriteAsJsonAsync(
                new { error = "Too many requests." }, cancellationToken);
        };
    });
}

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

if (security.RateLimitEnabled)
    app.UseRateLimiter();

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
///
/// <para>It also resolves the endpoint allowlist, which is not only a launch concern: the same list
/// governs the Configure dialog. That part runs whether or not the launch surface is enabled.</para>
/// </summary>
static void ValidateConsoleLaunchOptions(WebApplication app)
{
    ConsoleLaunchOptions options = app.Services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<ConsoleLaunchOptions>>().Value;

    _ = BrandNameSanitizer.Require(options.DefaultBrandName, $"{ConsoleLaunchOptions.SectionName}:DefaultBrandName");

    CamusDbOptions camus = app.Services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<CamusDbOptions>>().Value;

    // Resolved whether or not the launch surface is on, because the allowlist governs the Configure
    // dialog as well. Resolving it here is what turns a malformed entry into a startup failure rather
    // than a request-time surprise, and it logs what was understood so a typo that parsed into
    // something unintended is visible in the log rather than only in a later refusal.
    EndpointAllowList allowList = app.Services.GetRequiredService<EndpointAllowList>();

    if (!allowList.IsEmpty)
    {
        app.Logger.LogInformation(
            "Endpoint allowlist in force for launch payloads and the Configure dialog ({Count} entries): {AllowList}",
            allowList.Count, allowList.ToString());
    }
    else if (!camus.LockEndpoint)
    {
        // Not gated on the launch surface. The Configure dialog is the wider of the two paths and it
        // is open on every console, so the warning belongs on every console that leaves both guards
        // off — not only on one that also turned the vendor handoff on.
        app.Logger.LogWarning(
            "Neither {Section}:AllowedEndpoints nor CamusDB:LockEndpoint is set. A launch payload or the "
            + "Configure dialog may then name any http(s) URL, which this server will open on the visitor's "
            + "behalf — set one of them on any console reachable beyond localhost.",
            ConsoleLaunchOptions.SectionName);
    }

    if (!options.Enabled)
        return;

    if (options.ApiKey.Length < ConsoleLaunchOptions.MinApiKeyLength)
    {
        throw new InvalidOperationException(
            $"{ConsoleLaunchOptions.SectionName}:Enabled is true but {ConsoleLaunchOptions.SectionName}:ApiKey "
            + $"is missing or shorter than {ConsoleLaunchOptions.MinApiKeyLength} characters. Set it from the "
            + "environment (ConsoleLaunch__ApiKey) with a value from a CSPRNG.");
    }
}
