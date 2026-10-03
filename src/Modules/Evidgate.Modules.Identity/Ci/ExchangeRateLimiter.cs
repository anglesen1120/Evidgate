using System.Collections.Concurrent;
using Evidgate.Modules.Identity.Options;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Ci;

/// <summary>
/// A fixed-window limiter for <c>POST /v1/ci/token</c> (02.4.1 AC8), keyed by whatever identifies
/// the caller before it is trusted (the client address, then the repository id / token prefix once
/// known). It runs before any signature, resolver or minting work, so a flood costs one dictionary
/// probe. State is per process; the limit is a brake on bursts, not a quota.
/// </summary>
internal sealed class ExchangeRateLimiter(IOptions<CiAuthOptions> options, TimeProvider clock)
{
    private const int MaxKeys = 50_000;

    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private long _sweptAtTicks;

    /// <summary>Counts one attempt for <paramref name="key"/>; false when the window's budget is spent.</summary>
    /// <param name="key">The limiter key.</param>
    /// <param name="retryAfterSeconds">Seconds until the window resets when refused.</param>
    public bool TryAcquire(string key, out int retryAfterSeconds)
    {
        var settings = options.Value;
        var length = TimeSpan.FromSeconds(settings.ExchangeWindowSeconds);
        var now = clock.GetUtcNow();

        Sweep(now, length);

        var window = _windows.GetOrAdd(key, _ => new Window(now));
        lock (window)
        {
            if (now - window.Start >= length)
            {
                window.Start = now;
                window.Count = 0;
            }

            if (window.Count >= settings.ExchangeBurstLimit)
            {
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((window.Start + length - now).TotalSeconds));

                return false;
            }

            window.Count++;
            retryAfterSeconds = 0;

            return true;
        }
    }

    /// <summary>Drops expired windows now and then so an address-spraying client cannot grow the map without bound.</summary>
    private void Sweep(DateTimeOffset now, TimeSpan length)
    {
        if (_windows.Count < MaxKeys && now.UtcTicks - Interlocked.Read(ref _sweptAtTicks) < length.Ticks)
        {
            return;
        }

        Interlocked.Exchange(ref _sweptAtTicks, now.UtcTicks);
        foreach (var (key, window) in _windows)
        {
            if (now - window.Start >= length)
            {
                _windows.TryRemove(key, out _);
            }
        }
    }

    private sealed class Window(DateTimeOffset start)
    {
        public DateTimeOffset Start { get; set; } = start;

        public int Count { get; set; }
    }
}
