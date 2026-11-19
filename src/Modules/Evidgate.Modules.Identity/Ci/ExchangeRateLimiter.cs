using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Evidgate.Modules.Identity.Ci;

/// <summary>The surface an attempt budget belongs to; each one keeps its own limit, window and key capacity.</summary>
internal enum RateSurface
{
    /// <summary>Federated login <c>start</c> and <c>callback</c>.</summary>
    Login,

    /// <summary>Explicit second-provider link routes.</summary>
    Link,

    /// <summary>Invite-token staging.</summary>
    Invite,

    /// <summary>CI token exchange, bearer and ingest-token paths alike.</summary>
    CiExchange,
}

/// <summary>One throttle decision; <see cref="RetryAfterSeconds"/> is 0 when admitted and at least 1 when refused.</summary>
internal readonly record struct RateDecision(bool Allowed, int RetryAfterSeconds);

/// <summary>
/// Fixed-window attempt budgets for the pre-tenant routes (02.4.1 AC8 and the login, link and invite routes), keyed by
/// whatever identifies the caller before it is trusted (the client address, then the repository id or token prefix).
/// The window lives in PostgreSQL, so every replica counts into the same one and the limit does not multiply with the
/// replica count; a full surface evicts its oldest keys on a sampled capacity check instead of refusing new
/// clients, and each new key sweeps a bounded batch of that surface's expired windows. The count is virtual only so a
/// test can make the store fail with a chosen exception type; nothing in production derives from it.
/// </summary>
internal class ExchangeRateLimiter(NpgsqlDataSource dataSource, IOptions<RateLimitOptions> options)
{
    /// <summary>
    /// A budget decision must not hold a request open: the store answers in milliseconds or it answers late, and
    /// the route's fail-closed path (503 with <c>Retry-After</c>) is the better outcome.
    /// </summary>
    private const int CommandTimeoutSeconds = 2;

    /// <summary>Counts one attempt for <paramref name="key"/> and reports whether the surface's budget admits it.</summary>
    /// <param name="surface">The budget to spend from.</param>
    /// <param name="key">The limiter key; the store receives only its unsalted SHA-256 digest, which is pseudonymous, not anonymous.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <exception cref="NpgsqlException">The store could not answer; the caller fails closed.</exception>
    public virtual async ValueTask<RateDecision> AcquireAsync(
        RateSurface surface, string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var settings = options.Value.For(surface);

        // The database decides the window with its own clock and row, so a replica whose clock is off can neither
        // shorten nor extend another replica's window, and the check-and-increment is one statement.
        await using var command = dataSource.CreateCommand(
            "select allowed, retry_after_seconds from evidgate_resolve.rate_limit_acquire(" +
            "@surface, @key_hash, @limit, @window_seconds, @max_keys)");
        command.Parameters.AddWithValue("surface", SurfaceName(surface));
        command.Parameters.Add(new NpgsqlParameter("key_hash", NpgsqlDbType.Bytea)
        {
            Value = SHA256.HashData(Encoding.UTF8.GetBytes(key)),
        });
        command.Parameters.AddWithValue("limit", settings.Limit);
        command.Parameters.AddWithValue("window_seconds", settings.WindowSeconds);
        command.Parameters.AddWithValue("max_keys", settings.MaxKeys);
        command.CommandTimeout = CommandTimeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("evidgate_resolve.rate_limit_acquire answered no row");
        }

        return new RateDecision(reader.GetBoolean(0), reader.GetInt32(1));
    }

    /// <summary>
    /// The limiter key of a client address: an IPv6 address is reduced to its /64, because one subscriber
    /// routinely holds a whole /64 and could otherwise rotate keys for free.
    /// </summary>
    /// <param name="address">The socket address (behind a proxy, configure forwarded headers with known proxies first).</param>
    public static string ClientKey(IPAddress? address)
    {
        if (address is null)
        {
            return "none";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();

        return new IPAddress(bytes).ToString() + "/64";
    }

    private static string SurfaceName(RateSurface surface) => surface switch
    {
        RateSurface.Login => "login",
        RateSurface.Link => "link",
        RateSurface.Invite => "invite",
        RateSurface.CiExchange => "ci_exchange",
        _ => throw new ArgumentOutOfRangeException(nameof(surface)),
    };
}

/// <summary>
/// The one place a throttled route turns a budget into an answer: the 429 <c>rate_limited</c> envelope with
/// <c>Retry-After</c>, or the shared 503 when the store cannot answer (fail closed: an unknown count never admits).
/// </summary>
internal static class RateLimitGate
{
    /// <summary>Spends up to <paramref name="cost"/> units of the caller's window; null means continue.</summary>
    /// <param name="context">The request whose response carries the refusal headers.</param>
    /// <param name="limiter">The shared window store.</param>
    /// <param name="surface">The budget this route spends from.</param>
    /// <param name="key">The limiter key, already prefixed by the route.</param>
    /// <param name="cost">Units this attempt is worth; only the first one can refuse.</param>
    /// <param name="refusalMessage">The route's 429 message.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    public static async ValueTask<Error?> AcquireAsync(
        HttpContext context,
        ExchangeRateLimiter limiter,
        RateSurface surface,
        string key,
        int cost,
        string refusalMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(limiter);

        if (await TryAcquireAsync(limiter, surface, key, cancellationToken).ConfigureAwait(false) is not { } decision)
        {
            return Unavailable(context);
        }

        if (!decision.Allowed)
        {
            context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);

            return new Error(ApiErrorCode.RateLimited, refusalMessage, StatusCodes.Status429TooManyRequests, Retryable: true);
        }

        // Only the first unit decides: a route that must be stricter spends its extra units when they are available and
        // never refuses by itself, so a legal limit of 1 (or an odd one) cannot make the route unusable.
        for (var unit = 1; unit < cost; unit++)
        {
            if (await TryAcquireAsync(limiter, surface, key, cancellationToken).ConfigureAwait(false) is null)
            {
                return Unavailable(context);
            }
        }

        return null;
    }

    private static async ValueTask<RateDecision?> TryAcquireAsync(
        ExchangeRateLimiter limiter, RateSurface surface, string key, CancellationToken cancellationToken)
    {
        try
        {
            return await limiter.AcquireAsync(surface, key, cancellationToken).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A cancellation the request did not ask for is a database command timeout, not a client that left.
            return null;
        }
    }

    private static Error Unavailable(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.Headers.RetryAfter = "1";

        return StoreUnavailable;
    }

    /// <summary>
    /// One outage answer for all four surfaces: 503 <c>unavailable</c>, retryable. The body names no store, host or
    /// SQL, because the reason a window could not be counted is an operator's concern, never a client's.
    /// </summary>
    private static readonly Error StoreUnavailable = new(
        ApiErrorCode.Unavailable,
        "the service cannot answer right now; retry shortly",
        StatusCodes.Status503ServiceUnavailable,
        Retryable: true);
}
