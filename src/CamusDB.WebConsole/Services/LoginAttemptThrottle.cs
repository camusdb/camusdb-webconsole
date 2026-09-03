using CamusDB.WebConsole.Options;
using Microsoft.Extensions.Options;

namespace CamusDB.WebConsole.Services;

/// <summary>
/// Counts failed sign-in attempts and makes the next one cost time.
///
/// <para>It is a singleton on purpose. A per-circuit counter is reset by opening a new circuit, which
/// is one page load — so a counter that lives with the circuit counts nothing an attacker cannot
/// discard. This one lives with the process and is keyed by the caller's address, which a browser
/// cannot change.</para>
///
/// <para>Every failure is counted twice: once against the pair (client, user name), and once against
/// the client alone. The pair is what stops a run of passwords against one account. The client alone
/// is what stops the opposite shape — one password tried against many account names, which never
/// reaches a per-account limit on this console or on the CamusDB server.</para>
///
/// <para>Nothing here proves the caller's address. See the note on
/// <see cref="ConsoleSecurityOptions"/>: behind a reverse proxy every visitor shares one key.</para>
/// </summary>
public sealed class LoginAttemptThrottle
{
    /// <summary>Key used when the caller's address could not be read.</summary>
    public const string UnknownClient = "unknown";

    /// <param name="Allowed">False when the attempt must not be made at all.</param>
    /// <param name="Delay">How long to wait before attempting. Zero for the first few failures.</param>
    /// <param name="RetryAfter">How long the refusal lasts. Meaningful only when <paramref name="Allowed"/> is false.</param>
    public readonly record struct ThrottleDecision(bool Allowed, TimeSpan Delay, TimeSpan RetryAfter);

    private sealed class Counter
    {
        public int Failures;
        public DateTimeOffset ExpiresAt;
        public DateTimeOffset BlockedUntil;
    }

    private readonly Dictionary<string, Counter> _counters = [];
    private readonly object _gate = new();
    private readonly ConsoleSecurityOptions _options;
    private readonly TimeProvider _time;

    public LoginAttemptThrottle(IOptions<ConsoleSecurityOptions> options, TimeProvider? timeProvider = null)
    {
        _options = options.Value;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Reports what this client must pay before its next attempt. The caller waits for
    /// <see cref="ThrottleDecision.Delay"/>, and refuses outright when the decision is not allowed.
    /// </summary>
    /// <param name="clientKey">The caller's address, or <see cref="UnknownClient"/>.</param>
    /// <param name="user">
    /// The account being signed in to. Empty for a token, which has no account name — token attempts
    /// then share one per-client bucket, which is what they should share.
    /// </param>
    public ThrottleDecision Check(string? clientKey, string? user)
    {
        if (!_options.LoginThrottleEnabled)
            return new ThrottleDecision(true, TimeSpan.Zero, TimeSpan.Zero);

        string client = Normalize(clientKey);
        DateTimeOffset now = _time.GetUtcNow();

        lock (_gate)
        {
            ThrottleDecision pair = Decide(PairKey(client, user), now);
            ThrottleDecision spray = Decide(client, now);

            return Worst(pair, spray);
        }
    }

    /// <summary>Records one failed attempt against both the pair counter and the client counter.</summary>
    public void RecordFailure(string? clientKey, string? user)
    {
        if (!_options.LoginThrottleEnabled)
            return;

        string client = Normalize(clientKey);
        DateTimeOffset now = _time.GetUtcNow();

        lock (_gate)
        {
            Prune(now);
            Increment(PairKey(client, user), MaxAttempts(), now);
            Increment(client, SprayLimit(), now);
        }
    }

    /// <summary>
    /// Forgets this client's failures after a successful sign-in. Only the pair counter is cleared:
    /// one account whose password is known says nothing about the other names the same client tried.
    /// </summary>
    public void RecordSuccess(string? clientKey, string? user)
    {
        if (!_options.LoginThrottleEnabled)
            return;

        lock (_gate)
            _counters.Remove(PairKey(Normalize(clientKey), user));
    }

    /// <summary>Live counters. Exposed for diagnostics; the value is a snapshot.</summary>
    public int TrackedClients
    {
        get
        {
            lock (_gate)
                return _counters.Count;
        }
    }

    private ThrottleDecision Decide(string key, DateTimeOffset now)
    {
        if (!_counters.TryGetValue(key, out Counter? counter) || counter.ExpiresAt <= now)
            return new ThrottleDecision(true, TimeSpan.Zero, TimeSpan.Zero);

        if (counter.BlockedUntil > now)
            return new ThrottleDecision(false, TimeSpan.Zero, counter.BlockedUntil - now);

        return new ThrottleDecision(true, DelayFor(counter.Failures), TimeSpan.Zero);
    }

    private void Increment(string key, int limit, DateTimeOffset now)
    {
        TimeSpan window = TimeSpan.FromSeconds(Positive(_options.LoginWindowSeconds, 300));

        if (!_counters.TryGetValue(key, out Counter? counter) || counter.ExpiresAt <= now)
        {
            counter = new Counter();
            _counters[key] = counter;
        }

        counter.Failures++;

        // The window is extended by every failure rather than fixed at the first one. A fixed window
        // lets a caller sit out the last seconds of it and start again with a clean count, which is
        // the whole attack this is meant to slow down.
        counter.ExpiresAt = now + window;

        if (counter.Failures >= limit)
        {
            counter.BlockedUntil = now + TimeSpan.FromSeconds(Positive(_options.LoginLockoutSeconds, 300));

            // The counter must outlive its own block, or the block is forgotten before it ends.
            if (counter.BlockedUntil > counter.ExpiresAt)
                counter.ExpiresAt = counter.BlockedUntil;
        }
    }

    private TimeSpan DelayFor(int failures)
    {
        int free = Math.Max(0, _options.LoginFreeAttempts);

        if (failures <= free)
            return TimeSpan.Zero;

        int max = Positive(_options.LoginMaxDelaySeconds, 8);

        // Doubling per failure, capped. The exponent is clamped before the shift because a large
        // failure count would otherwise overflow the shift rather than saturate.
        int steps = Math.Min(failures - free, 16);
        int seconds = Math.Min(max, 1 << (steps - 1));

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Drops expired counters, then drops whatever is closest to expiry until the store is inside its
    /// ceiling. Without the second step a caller with many source addresses turns this defence into a
    /// memory leak it controls.
    /// </summary>
    private void Prune(DateTimeOffset now)
    {
        int ceiling = Positive(_options.MaxTrackedClients, 20_000);

        if (_counters.Count < ceiling)
            return;

        List<string> expired = [.. _counters.Where(e => e.Value.ExpiresAt <= now).Select(e => e.Key)];

        foreach (string key in expired)
            _counters.Remove(key);

        while (_counters.Count >= ceiling)
        {
            KeyValuePair<string, Counter> oldest = _counters.MinBy(e => e.Value.ExpiresAt);

            if (oldest.Key is null)
                return;

            _counters.Remove(oldest.Key);
        }
    }

    private int MaxAttempts() => Positive(_options.LoginMaxAttempts, 10);

    private int SprayLimit() => MaxAttempts() * Positive(_options.LoginSprayMultiplier, 5);

    private static ThrottleDecision Worst(ThrottleDecision a, ThrottleDecision b)
    {
        if (!a.Allowed || !b.Allowed)
        {
            TimeSpan retry = a.Allowed ? b.RetryAfter : b.Allowed ? a.RetryAfter
                : a.RetryAfter > b.RetryAfter ? a.RetryAfter : b.RetryAfter;

            return new ThrottleDecision(false, TimeSpan.Zero, retry);
        }

        return new ThrottleDecision(true, a.Delay > b.Delay ? a.Delay : b.Delay, TimeSpan.Zero);
    }

    private static string Normalize(string? clientKey) =>
        string.IsNullOrWhiteSpace(clientKey) ? UnknownClient : clientKey.Trim();

    /// <summary>
    /// The separator is a character no address and no CamusDB user name may contain, so no pair of
    /// (client, user) values can be written two ways and land in one another's bucket.
    /// </summary>
    private static string PairKey(string client, string? user) =>
        $"{client}\n{(user ?? "").Trim().ToLowerInvariant()}";

    private static int Positive(int value, int fallback) => value > 0 ? value : fallback;
}
