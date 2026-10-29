using CamusDB.WebConsole.Services;

namespace CamusDB.WebConsole.Hosting;

/// <summary>
/// Caps the open connections to the Blazor circuit transport per client address.
///
/// <para>The launch rate limiter does not cover <c>/_blazor</c>, and must not: it would count
/// reconnects and long-polling as abuse. What makes the transport expensive is not the rate of
/// requests but how many stay open — a WebSocket request lasts as long as its circuit — so this
/// counts requests in flight, not requests per window. A circuit that loses its connection is kept
/// by the framework for a while, but that pool has a fixed size of its own.</para>
///
/// <para>The key is <see cref="ClientAddress.Key(HttpContext)"/>, so it agrees with the sign-in
/// throttle on what one caller is, and it inherits the same caveat behind a reverse proxy.</para>
/// </summary>
public sealed class CircuitConnectionLimit
{
    private const string TransportPath = "/_blazor";

    private readonly Dictionary<string, int> _open = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly RequestDelegate _next;
    private readonly int _limit;
    private readonly ILogger<CircuitConnectionLimit> _logger;

    public CircuitConnectionLimit(RequestDelegate next, int limit, ILogger<CircuitConnectionLimit> logger)
    {
        _next = next;
        _limit = limit;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(TransportPath))
        {
            await _next(context);
            return;
        }

        string key = ClientAddress.Key(context);

        if (!TryEnter(key))
        {
            _logger.LogWarning(
                "Refused a circuit connection from {ClientKey}: it already holds {Limit} open connections.",
                key, _limit);

            // A body is what keeps the status-code-pages middleware from re-running the request
            // against the not-found page, which would answer a POST with an antiforgery error.
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsync("Too many open connections from this address.");
            return;
        }

        try
        {
            await _next(context);
        }
        finally
        {
            Leave(key);
        }
    }

    private bool TryEnter(string key)
    {
        lock (_gate)
        {
            int open = _open.GetValueOrDefault(key);

            if (open >= _limit)
                return false;

            _open[key] = open + 1;
            return true;
        }
    }

    // An address with nothing open is removed, so the table holds only callers that are connected now.
    private void Leave(string key)
    {
        lock (_gate)
        {
            int open = _open.GetValueOrDefault(key) - 1;

            if (open > 0)
                _open[key] = open;
            else
                _open.Remove(key);
        }
    }
}
