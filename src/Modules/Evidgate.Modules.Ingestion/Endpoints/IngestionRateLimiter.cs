using System.Globalization;
using Evidgate.Modules.Ingestion.Application;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Ingestion.Endpoints;

/// <summary>
/// Fixed-window admission for the events route, partitioned by the authenticated
/// project and token id taken from the request principal, plus one project-wide
/// budget so minting more tokens cannot raise a project's rate. Per process by
/// design: replicas do not share a counter, so the effective limit multiplies by
/// the replica count (a shared store is a separate task).
/// <para>
/// Lookup, window reset, eviction and the increment happen under one lock, so an
/// expired window can never be removed between a caller's reset and its increment —
/// which would hand the same identity two budgets. The map is bounded: when it is
/// full the expired windows are swept first, and a new identity that still finds no
/// room is refused rather than admitted into an unbounded map.
/// </para>
/// </summary>
internal sealed class IngestionRateLimiter
{
    private readonly Dictionary<Partition, Window> _windows = new();
    private readonly object _sync = new();
    private readonly TimeSpan _window;
    private readonly int _permitLimit;
    private readonly int _maxPartitions;
    private readonly TimeProvider _clock;

    public IngestionRateLimiter(IOptions<IngestionRateLimits> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _permitLimit = options.Value.PermitLimit;
        _window = TimeSpan.FromSeconds(options.Value.WindowSeconds);
        _maxPartitions = options.Value.MaxPartitions;
        _clock = clock;
    }

    /// <summary>
    /// Counts one request against the budget of <paramref name="projectId"/> and, when
    /// <paramref name="tokenId"/> is given, of that token in it; returns whether it is admitted plus the
    /// whole seconds to wait when it is not. A null token id charges the project's whole budget.
    /// </summary>
    public RateLimitDecision Acquire(Guid projectId, string? tokenId)
    {
        if (tokenId is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(tokenId);
        }

        var now = _clock.GetUtcNow();
        var partition = new Partition(projectId, tokenId);

        lock (_sync)
        {
            if (!_windows.TryGetValue(partition, out var window))
            {
                if (_windows.Count >= _maxPartitions)
                {
                    Sweep(now);
                    if (_windows.Count >= _maxPartitions)
                    {
                        // No room for a new identity: refuse for one full window instead of growing the map.
                        return new RateLimitDecision(false, (int)_window.TotalSeconds);
                    }
                }

                window = new Window(now);
                _windows[partition] = window;
            }

            if (now - window.Start >= _window)
            {
                window.Start = now;
                window.Count = 0;
            }

            if (window.Count >= _permitLimit)
            {
                return new RateLimitDecision(false, SecondsUntil(window.Start + _window, now));
            }

            window.Count++;

            return new RateLimitDecision(true, 0);
        }
    }

    /// <summary>
    /// Drops windows whose fixed window has ended. Only called with <see cref="_sync"/> held and only
    /// when the map is full, so a live budget is never evicted and no caller can hold a removed window.
    /// </summary>
    private void Sweep(DateTimeOffset now)
    {
        List<Partition>? expired = null;

        foreach (var entry in _windows)
        {
            if (now - entry.Value.Start >= _window)
            {
                (expired ??= []).Add(entry.Key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var partition in expired)
        {
            _windows.Remove(partition);
        }
    }

    private int SecondsUntil(DateTimeOffset end, DateTimeOffset now)
    {
        var seconds = Math.Ceiling((end - now).TotalSeconds);

        return (int)Math.Clamp(seconds, 1, _window.TotalSeconds);
    }

    /// <summary>One resident fixed window; only touched while <see cref="IngestionRateLimiter._sync"/> is held.</summary>
    private sealed class Window(DateTimeOffset start)
    {
        internal DateTimeOffset Start { get; set; } = start;

        internal int Count { get; set; }
    }

    /// <summary>
    /// The authenticated identity a budget belongs to: a project and, when the budget is the token's own,
    /// the token that writes into it. A null token id is the project's shared budget.
    /// </summary>
    private readonly record struct Partition(Guid ProjectId, string? TokenId);

    /// <summary>The verdict of <see cref="Acquire"/>.</summary>
    /// <param name="Allowed">Whether the request may proceed.</param>
    /// <param name="RetryAfterSeconds">Whole seconds of the <c>Retry-After</c> header; 0 when admitted.</param>
    internal readonly record struct RateLimitDecision(bool Allowed, int RetryAfterSeconds);
}

/// <summary>Renders a limiter wait as the <c>Retry-After</c> wire value.</summary>
internal static class RetryAfterHeader
{
    /// <summary>The header value: RFC 9110 delay-seconds, never below 1 (00 §9.2).</summary>
    public static string Format(int seconds) => Math.Max(1, seconds).ToString(CultureInfo.InvariantCulture);
}
