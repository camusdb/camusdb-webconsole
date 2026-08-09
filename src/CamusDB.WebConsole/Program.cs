using CamusDB.WebConsole.Options;
using CamusDB.WebConsole.Services;
using MudBlazor;
using MudBlazor.Services;
using CamusDB.WebConsole.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CamusDbOptions>(builder.Configuration.GetSection(CamusDbOptions.SectionName));
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

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
