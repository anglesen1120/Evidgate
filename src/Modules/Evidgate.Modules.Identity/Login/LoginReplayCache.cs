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
/// (<c>ExchangeRateLimiter</c>): a full set sweeps its expired entries first, and if there is still no
/// room the attempt fails closed — a <c>state</c> that cannot be recorded is refused rather than trusted.
/// </para>
/// <para>
/// <b>Cost per attempt.</b> The live count is an <see cref="Interlocked"/> counter, not
/// <c>ConcurrentDictionary.Count</c> (which takes every bucket lock), so the common path is one dictionary
/// probe plus one atomic increment. Expiry is evicted from a queue of
/// <c>(key, expiresAt)</c> pairs drained at the head: every record is queued in insertion order with the
/// same lifetime, so the queue is ordered by expiry and a sweep costs O(expired), never O(size). The
/// opportunity sweep runs at most once per <see cref="SweepInterval"/> — a read-mostly timestamp compared
/// with a compare-and-swap, the same shape as <c>ExchangeRateLimiter.Sweep</c> — and the full set forces
/// one drain of its own before refusing. Without that interval a fresh <c>TimeProvider</c> stamp (100 ns
/// ticks) would let every call at the cap walk the whole set.
/// </para>
/// <para>
/// <b>Strict cap.</b> A caller reserves a slot (<c>Interlocked.Increment</c> on the count) before it
/// writes the entry, and releases it when the write loses a race, so the count never exceeds
/// <see cref="MaxEntries"/> between a check and an add: the reservation is the authority, not a
/// <c>Count</c> read.
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

    /// <summary>
    /// Shortest interval between two opportunity sweeps of the expiry queue. One second bounds the sweep
    /// rate under load while still reclaiming an expired window within a second of it elapsing.
    /// </summary>
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    /// <summary>The live records and their expiry (the single source of "was this <c>state</c> used?").</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _consumed = new(StringComparer.Ordinal);

    /// <summary>
    /// Every record in insertion order. All of them share one lifetime, so the head always expires first
    /// and a sweep can stop at the first live entry.
    /// </summary>
    private readonly ConcurrentQueue<Entry> _expiryOrder = new();

    /// <summary>
    /// How many records the set holds, maintained only by the successful reservation, write and removal
    /// of an entry — never by walking the dictionary.
    /// </summary>
    private long _count;

    /// <summary>
    /// Serialises the drainers. One sweep at a time keeps <c>TryPeek</c> and <c>TryDequeue</c> consistent,
    /// so the queue head read is the entry removed; concurrent callers skip the sweep instead of scanning.
    /// </summary>
    private readonly object _drainGate = new();

    /// <summary>When the last opportunity sweep ran (<see cref="DateTimeOffset.UtcTicks"/>).</summary>
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

        if (_consumed.TryGetValue(key, out var recorded))
        {
            if (recorded > now)
            {
                // Still live: this exact state was already used.
                return false;
            }

            // Expired record for the same key: purge it (as the sweep would) and record the new use.
            Remove(key, recorded);
        }
        else
        {
            Sweep(now);
        }

        if (!TryReserve())
        {
            // At the cap: the head of the queue is the cheapest place to look for room before refusing.
            Drain(now);

            if (!TryReserve())
            {
                return false;
            }
        }

        if (!_consumed.TryAdd(key, now + lifetime))
        {
            // Another caller recorded the same key first (or the same expired pair was re-added): it is not
            // this caller's first use, so the reservation goes back and the attempt is refused.
            Interlocked.Decrement(ref _count);

            return false;
        }

        _expiryOrder.Enqueue(new Entry(key, now + lifetime));

        return true;
    }

    /// <summary>
    /// Reserves one slot: the increment is the check, so two concurrent callers can never both pass a
    /// count test and overshoot the cap. A failed reservation is released immediately.
    /// </summary>
    private bool TryReserve()
    {
        if (Interlocked.Increment(ref _count) <= MaxEntries)
        {
            return true;
        }

        Interlocked.Decrement(ref _count);

        return false;
    }

    /// <summary>
    /// Runs an opportunity sweep at most once per <see cref="SweepInterval"/>: one caller wins the
    /// compare-and-swap and drains, the others return without touching the queue.
    /// </summary>
    private void Sweep(DateTimeOffset now)
    {
        var stamp = now.UtcTicks;
        var last = Interlocked.Read(ref _sweptAtTicks);

        if (stamp - last < SweepInterval.Ticks
            || Interlocked.CompareExchange(ref _sweptAtTicks, stamp, last) != last)
        {
            return;
        }

        Drain(now);
    }

    /// <summary>
    /// Removes every expired record from the head of the queue. One drainer at a time, so a peeked head is
    /// the entry dequeued; an entry that is not expired yet, or a queue whose head is a live record, ends
    /// the loop. Nothing live is ever removed, so a state the set records stays recorded for its lifetime.
    /// </summary>
    private void Drain(DateTimeOffset now)
    {
        lock (_drainGate)
        {
            while (_expiryOrder.TryPeek(out var head) && head.ExpiresAt <= now)
            {
                if (_expiryOrder.TryDequeue(out var entry))
                {
                    Remove(entry.Key, entry.ExpiresAt);
                }
            }
        }
    }

    /// <summary>
    /// Drops the record if the dictionary still holds exactly this expiry, and releases its slot then. A
    /// pair removal is what makes the count exact: a key re-recorded with a new expiry, or already removed
    /// by the sweep, is left alone and its slot is not released twice.
    /// </summary>
    private void Remove(string key, DateTimeOffset expiresAt)
    {
        if (_consumed.TryRemove(new KeyValuePair<string, DateTimeOffset>(key, expiresAt)))
        {
            Interlocked.Decrement(ref _count);
        }
    }

    /// <summary>One queued record: the key and the instant it expires.</summary>
    /// <param name="Key">The recorded key.</param>
    /// <param name="ExpiresAt">The instant the record may be dropped.</param>
    private readonly record struct Entry(string Key, DateTimeOffset ExpiresAt);
}
