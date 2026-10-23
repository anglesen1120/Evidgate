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

        var hasRoom = Sweep(now, length);

        // Full of live windows (a spraying client): fail closed for new keys; the keys already tracked keep their budgets.
        if (!hasRoom && !_windows.ContainsKey(key))
        {
            retryAfterSeconds = (int)length.TotalSeconds;

            return false;
        }

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

    /// <summary>
    /// The limiter key of a client address: an IPv6 address is reduced to its /64, because one subscriber
    /// routinely holds a whole /64 and could otherwise rotate keys for free.
    /// </summary>
    /// <param name="address">The socket address (behind a proxy, configure forwarded headers with known proxies first).</param>
    public static string ClientKey(System.Net.IPAddress? address)
    {
        if (address is null)
        {
            return "none";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();

        return new System.Net.IPAddress(bytes).ToString() + "/64";
    }

    /// <summary>
    /// Drops expired windows at most once per window length (a compare-and-swap, so concurrent callers do not each scan),
    /// and reports whether there is room for another key.
    /// </summary>
    private bool Sweep(DateTimeOffset now, TimeSpan length)
    {
        var last = Interlocked.Read(ref _sweptAtTicks);
        if (now.UtcTicks - last >= length.Ticks && Interlocked.CompareExchange(ref _sweptAtTicks, now.UtcTicks, last) == last)
        {
            foreach (var (key, window) in _windows)
            {
                if (now - window.Start >= length)
                {
                    _windows.TryRemove(key, out _);
                }
            }
        }

        return _windows.Count < MaxKeys;
    }

    private sealed class Window(DateTimeOffset start)
    {
        public DateTimeOffset Start { get; set; } = start;

        public int Count { get; set; }
    }
}
