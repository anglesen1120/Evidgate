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
/// Lookup, window reset, eviction and both increments happen under one lock, and a
/// request that either budget refuses is charged to neither: see <see cref="AcquireBoth"/>.
/// The map is bounded, so a token that finds no room is charged to the project's
/// budget alone rather than growing the map; only a project that cannot get a
/// partition of its own is refused.
/// </para>
/// </summary>
internal sealed class IngestionRateLimiter
{
    /// <summary>Smallest gap between two eviction passes, so a flooded map cannot pay a full scan per request.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    private readonly Dictionary<Partition, Window> _windows = new();
    private readonly object _sync = new();
    private readonly TimeSpan _window;
    private readonly int _permitLimit;
    private readonly int _projectPermitLimit;
    private readonly int _maxPartitions;
    private readonly TimeProvider _clock;

    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    public IngestionRateLimiter(IOptions<IngestionRateLimits> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _permitLimit = options.Value.PermitLimit;
        _projectPermitLimit = options.Value.ProjectPermitLimit;
        _window = TimeSpan.FromSeconds(options.Value.WindowSeconds);
        _maxPartitions = options.Value.MaxPartitions;
        _clock = clock;
    }

    /// <summary>
    /// Counts one request against the token's budget and the project's budget in one
    /// step and admits it only when both allow: a refusal charges neither window, so a
    /// token that is over its own limit cannot spend the budget its siblings share.
    /// </summary>
    /// <param name="projectId">The project scope of the authenticated caller.</param>
    /// <param name="tokenId">The caller's credential id; its budget is always narrower than the project's.</param>
    /// <returns>The verdict plus the refusing window's whole-second <c>Retry-After</c> value.</returns>
    public RateLimitDecision AcquireBoth(Guid projectId, string tokenId)
    {
        ArgumentException.ThrowIfNullOrEmpty(tokenId);

        var now = _clock.GetUtcNow();
        var projectPartition = new Partition(projectId, TokenId: null);
        var tokenPartition = new Partition(projectId, tokenId);

        lock (_sync)
        {
            // A project that cannot get a partition of its own is refused: admitting it
            // without a counter would hand it an unbounded rate.
            if (FindOrCreate(projectPartition, now) is not { } projectWindow)
            {
                return new RateLimitDecision(false, (int)_window.TotalSeconds);
            }

            ResetIfExpired(projectWindow, now);
            if (projectWindow.Count >= _projectPermitLimit)
            {
                return Refuse(projectWindow, now);
            }

            // A token window that is live and spent refuses the request outright; one that
            // finds no room in a full map is charged to the project alone, so the request
            // keeps its rate limit (the project's) instead of failing shut on map pressure.
            var tokenWindow = FindOrCreate(tokenPartition, now);
            if (tokenWindow is not null)
            {
                ResetIfExpired(tokenWindow, now);
                if (tokenWindow.Count >= _permitLimit)
                {
                    return Refuse(tokenWindow, now);
                }
            }

            projectWindow.Count++;
            if (tokenWindow is not null)
            {
                tokenWindow.Count++;
            }

            return new RateLimitDecision(true, 0);
        }
    }

    /// <summary>
    /// The resident window of <paramref name="partition"/>, a new one when the map has
    /// room, or null when it has none. Only runs with <see cref="_sync"/> held, so the
    /// eviction pass it may trigger cannot race a caller's increment.
    /// </summary>
    private Window? FindOrCreate(Partition partition, DateTimeOffset now)
    {
        if (_windows.TryGetValue(partition, out var window))
        {
            return window;
        }

        if (_windows.Count >= _maxPartitions)
        {
            SweepIfDue(now);
            if (_windows.Count >= _maxPartitions)
            {
                return null;
            }
        }

        window = new Window(now);
        _windows[partition] = window;

        return window;
    }

    private void ResetIfExpired(Window window, DateTimeOffset now)
    {
        if (now - window.Start >= _window)
        {
            window.Start = now;
            window.Count = 0;
        }
    }

    /// <summary>
    /// Drops windows whose fixed window has ended, at most once per <see cref="SweepInterval"/>.
    /// A live budget is never evicted, and no caller can hold a removed window because the
    /// whole pass runs under <see cref="_sync"/>.
    /// </summary>
    private void SweepIfDue(DateTimeOffset now)
    {
        if (now - _lastSweep < SweepInterval)
        {
            return;
        }

        _lastSweep = now;
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

    private RateLimitDecision Refuse(Window window, DateTimeOffset now) =>
        new(false, SecondsUntil(window.Start + _window, now));

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

    /// <summary>The verdict of <see cref="AcquireBoth"/>.</summary>
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
