using System.Collections.Concurrent;
using System.Security.Cryptography;
using CamusDB.WebConsole.Models;
using CamusDB.WebConsole.Options;
using Microsoft.Extensions.Options;

namespace CamusDB.WebConsole.Services;

/// <summary>
/// Holds vendor launch tickets for the short window between the vendor's back-channel call and the
/// visitor's browser arriving. Everything here is in-process and deliberately not persisted: these
/// records contain access tokens, and a restart losing them costs a re-launch, which is cheap.
///
/// <para><b>Single instance per process.</b> A multi-instance deployment needs sticky sessions, or a
/// shared store — a launch code minted on one node cannot be redeemed on another.</para>
///
/// <para>Three kinds of secret pass through, all 256-bit values from
/// <see cref="RandomNumberGenerator"/> so they cannot be guessed and need no rate limiting of their
/// own:</para>
/// <list type="bullet">
/// <item><b>Launch code</b> — returned to the vendor, spent by the visitor's first request. Single use.</item>
/// <item><b>Session id</b> — lives in an HttpOnly cookie for the browser session's lifetime.</item>
/// <item><b>Handoff</b> — minted per page render, spent by the Blazor circuit. Single use, seconds long.
/// It exists because the circuit cannot read the cookie: only the static render can, and passing the
/// session id itself to the circuit would put a session-lifetime secret where an XSS could reach it.</item>
/// </list>
/// </summary>
public sealed class ConsoleLaunchStore
{
    /// <summary>Window a handoff stays redeemable: one static render to one circuit start.</summary>
    private static readonly TimeSpan HandoffLifetime = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Unspent handoffs one session may hold. A circuit spends its handoff within a second of the page
    /// load, so only a burst of reloads gets near this — and that burst is all it limits.
    /// </summary>
    private const int MaxHandoffsPerSession = 8;

    private sealed record Entry(ConsoleLaunchTicket Ticket, DateTimeOffset ExpiresAt, string? SessionId = null);

    /// <summary>
    /// Launch codes and handoffs are kept apart, so one cannot be spent as the other, and so the
    /// cheap one does not share a budget with the expensive one. A handoff costs one page load; a
    /// launch code costs the vendor's key.
    /// </summary>
    private readonly ConcurrentDictionary<string, Entry> _launchCodes = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Entry> _handoffs = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Entry> _sessions = new(StringComparer.Ordinal);

    private readonly ConsoleLaunchOptions _options;
    private readonly TimeProvider _clock;

    public ConsoleLaunchStore(IOptions<ConsoleLaunchOptions> options, TimeProvider? clock = null)
    {
        _options = options.Value;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Mints the code the vendor redirects the visitor with, or null when the store is at capacity.
    /// Null is a refusal, not a retry hint — see <see cref="ConsoleLaunchOptions.MaxLiveEntries"/>.
    /// </summary>
    public string? TryIssueLaunchCode(ConsoleLaunchTicket ticket)
    {
        Sweep();

        if (_launchCodes.Count + _sessions.Count >= _options.MaxLiveEntries)
            return null;

        string code = NewSecret();
        int lifetime = _options.CodeLifetimeSeconds > 0 ? _options.CodeLifetimeSeconds : 60;
        _launchCodes[code] = new Entry(ticket, _clock.GetUtcNow().AddSeconds(lifetime));
        return code;
    }

    public bool TryRedeemLaunchCode(string? code, out ConsoleLaunchTicket? ticket) =>
        TryRedeemSingleUse(_launchCodes, code, out ticket);

    /// <summary>
    /// Opens the browser session a launch code buys. The returned id is what goes in the cookie.
    /// </summary>
    public string? TryOpenSession(ConsoleLaunchTicket ticket)
    {
        Sweep();

        if (_launchCodes.Count + _sessions.Count >= _options.MaxLiveEntries)
            return null;

        string sessionId = NewSecret();
        int minutes = _options.SessionLifetimeMinutes > 0 ? _options.SessionLifetimeMinutes : 60;
        _sessions[sessionId] = new Entry(ticket, _clock.GetUtcNow().AddMinutes(minutes));
        return sessionId;
    }

    /// <summary>
    /// Resolves a session without consuming it — used by the static render to know which name to
    /// paint. It deliberately hands back the whole ticket rather than just the name: the caller is
    /// server-side, and narrowing it here would only invite a second lookup.
    /// </summary>
    public bool TryGetSession(string? sessionId, out ConsoleLaunchTicket? ticket)
    {
        ticket = null;

        if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out Entry? entry))
            return false;

        if (entry.ExpiresAt <= _clock.GetUtcNow())
        {
            _sessions.TryRemove(sessionId, out _);
            return false;
        }

        ticket = entry.Ticket;
        return true;
    }

    /// <summary>
    /// Mints a one-shot handoff for a live session, for the static render to pass to the circuit.
    /// </summary>
    public bool TryIssueHandoff(string? sessionId, out string handoff)
    {
        handoff = "";

        if (!TryGetSession(sessionId, out ConsoleLaunchTicket? ticket) || ticket is null)
            return false;

        // Bounded like the other two. One handoff is minted per page load, so a visitor holding a
        // valid session could otherwise grow the store by reloading — each entry only lives 30
        // seconds, but the ceiling has to be a ceiling. Refusing degrades that one page load to
        // branded-but-unauthenticated rather than letting the process take on unbounded state.
        //
        // The per-session limit comes first: without it, one session reloading fast enough would use
        // up the shared ceiling and refuse the handoffs of every other session.
        Sweep();

        if (_handoffs.Count >= _options.MaxLiveEntries
            || _handoffs.Values.Count(e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal))
                >= MaxHandoffsPerSession)
        {
            return false;
        }

        handoff = NewSecret();
        _handoffs[handoff] = new Entry(ticket, _clock.GetUtcNow().Add(HandoffLifetime), sessionId);
        return true;
    }

    public bool TryRedeemHandoff(string? handoff, out ConsoleLaunchTicket? ticket) =>
        TryRedeemSingleUse(_handoffs, handoff, out ticket);

    public void CloseSession(string? sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId))
            _sessions.TryRemove(sessionId, out _);
    }

    /// <summary>
    /// Removal is what makes a secret single-use, so it happens before the expiry check: a value that
    /// arrives too late is still spent, never left behind for a second attempt.
    /// </summary>
    private bool TryRedeemSingleUse(
        ConcurrentDictionary<string, Entry> store,
        string? secret,
        out ConsoleLaunchTicket? ticket)
    {
        ticket = null;

        if (string.IsNullOrEmpty(secret))
            return false;

        if (!store.TryRemove(secret, out Entry? entry))
            return false;

        if (entry.ExpiresAt <= _clock.GetUtcNow())
            return false;

        ticket = entry.Ticket;
        return true;
    }

    /// <summary>
    /// Drops expired entries. Called on every mint rather than from a timer: mints are the only thing
    /// that grows the store, so that is exactly when the sweep is needed, and it keeps this type free
    /// of a background service to own and dispose.
    /// </summary>
    private void Sweep()
    {
        DateTimeOffset now = _clock.GetUtcNow();

        SweepStore(_launchCodes, now);
        SweepStore(_handoffs, now);
        SweepStore(_sessions, now);
    }

    private static void SweepStore(ConcurrentDictionary<string, Entry> store, DateTimeOffset now)
    {
        foreach (KeyValuePair<string, Entry> kv in store)
        {
            if (kv.Value.ExpiresAt <= now)
                store.TryRemove(kv.Key, out _);
        }
    }

    private static string NewSecret() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Base64url so the value survives a query string and a cookie unescaped — '+' and '/' do not.
    /// </summary>
    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
