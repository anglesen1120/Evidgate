using System.Collections.Concurrent;

namespace Evidgate.Modules.Identity.Login;

/// <summary>
/// The one-time-use record of login <c>state</c> values (02.3.1 §5, AC2/§9: "<c>state</c> one-time").
/// The pending-login cookie is deleted on every callback, but a client that kept a copy could present the
/// same cookie and <c>state</c> twice; this set is what makes the second attempt 401 <c>token_invalid</c>
/// even then.
/// </summary>
/// <remarks>
/// <para>
/// Entries live for the cookie's own lifetime (<see cref="LoginOptions.StateLifetime"/>), driven by the
/// injected <see cref="TimeProvider"/>, so an expired cookie can never be resurrected and the set cannot
/// grow without bound. It is size-capped like the CI exchange limiter
/// (<c>ExchangeRateLimiter</c>): at the cap the expired entries are swept, and a full set fails closed —
/// a <c>state</c> that cannot be recorded is refused rather than trusted.
/// </para>
/// <para>
/// <b>Single node:</b> the record is per process, so two replicas answering the same replay would each see
/// it once. A shared store (Redis, or a <c>login_state</c> row) belongs to the 10.x deployment work; until
/// then a login attempt and its callback must reach the same node, exactly as
/// <see cref="LoginStateCookie"/> requires.
/// </para>
/// </remarks>
internal sealed class LoginReplayCache(TimeProvider clock)
{
    /// <summary>Largest number of live <c>state</c> values the set holds before it sweeps and then refuses.</summary>
    internal const int MaxEntries = 50_000;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _consumed = new(StringComparer.Ordinal);
    private long _sweptAtTicks;

    /// <summary>
    /// Records <paramref name="key"/> as used and reports whether this is the first time.
    /// </summary>
    /// <param name="key">The provider and <c>state</c>, joined (neither is a secret at this point: the
    /// state is already public in the redirect the browser carried).</param>
    /// <param name="lifetime">How long the record must live — the pending-login cookie's lifetime.</param>
    /// <returns><c>true</c> on first use; <c>false</c> for a replay, or when the set is full.</returns>
    public bool TryConsume(string key, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var now = clock.GetUtcNow();

        if (!Sweep(now))
        {
            return false;
        }

        return _consumed.TryAdd(key, now + lifetime);
    }

    /// <summary>
    /// Drops expired records once the set is at its cap — at most one sweep per timestamp, so concurrent
    /// callers do not each walk the dictionary — and reports whether there is room for another entry.
    /// </summary>
    private bool Sweep(DateTimeOffset now)
    {
        if (_consumed.Count < MaxEntries)
        {
            return true;
        }

        var stamp = now.UtcTicks;
        var last = Interlocked.Read(ref _sweptAtTicks);

        if (last == stamp || Interlocked.CompareExchange(ref _sweptAtTicks, stamp, last) != last)
        {
            return false;
        }

        foreach (var entry in _consumed)
        {
            if (entry.Value <= now)
            {
                _consumed.TryRemove(entry);
            }
        }

        return _consumed.Count < MaxEntries;
    }
}
