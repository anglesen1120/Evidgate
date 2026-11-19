using Evidgate.Modules.Identity.Ci;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Options;

/// <summary>
/// Per-surface attempt budgets of the pre-tenant routes (login, explicit link, invite staging and the CI exchange),
/// bound from <c>EVIDGATE:Identity:RateLimits</c>. A surface owns its own window partition in the store, so one
/// surface's traffic can neither spend another's budget nor fill another's key capacity.
/// </summary>
internal sealed class RateLimitOptions
{
    /// <summary>Configuration section; every key below is one scalar of it.</summary>
    public const string SectionName = "EVIDGATE:Identity:RateLimits";

    /// <summary>
    /// Highest accepted limit: the stored counter is clamped at limit + 1 and the column is a 32-bit integer, so a
    /// budget must stay well inside it.
    /// </summary>
    public const int MaxLimit = 1_000_000;

    /// <summary>Login <c>start</c> and <c>callback</c> attempts admitted per key within the window.</summary>
    public int LoginLimit { get; init; } = 30;

    /// <summary>Login window length in seconds; the database clock opens and closes it.</summary>
    public int LoginWindowSeconds { get; init; } = 60;

    /// <summary>Login keys kept before the surface evicts its oldest windows.</summary>
    public int LoginMaxKeys { get; init; } = 50_000;

    /// <summary>Explicit second-provider link attempts admitted per key within the window.</summary>
    public int LinkLimit { get; init; } = 30;

    /// <summary>Link window length in seconds.</summary>
    public int LinkWindowSeconds { get; init; } = 60;

    /// <summary>Link keys kept before the surface evicts its oldest windows.</summary>
    public int LinkMaxKeys { get; init; } = 50_000;

    /// <summary>Invite-token staging attempts admitted per key within the window.</summary>
    public int InviteLimit { get; init; } = 30;

    /// <summary>Invite window length in seconds.</summary>
    public int InviteWindowSeconds { get; init; } = 60;

    /// <summary>Invite keys kept before the surface evicts its oldest windows.</summary>
    public int InviteMaxKeys { get; init; } = 50_000;

    /// <summary>CI exchange attempts admitted per key within the window, including the ingest-token path.</summary>
    public int CiExchangeLimit { get; init; } = 30;

    /// <summary>CI exchange window length in seconds.</summary>
    public int CiExchangeWindowSeconds { get; init; } = 60;

    /// <summary>CI exchange keys kept before the surface evicts its oldest windows.</summary>
    public int CiExchangeMaxKeys { get; init; } = 50_000;

    /// <summary>The budget <paramref name="surface"/> spends, as one value the limiter passes to the store.</summary>
    /// <param name="surface">The surface the calling route belongs to.</param>
    internal SurfaceRateLimit For(RateSurface surface) => surface switch
    {
        RateSurface.Login => new SurfaceRateLimit(LoginLimit, LoginWindowSeconds, LoginMaxKeys),
        RateSurface.Link => new SurfaceRateLimit(LinkLimit, LinkWindowSeconds, LinkMaxKeys),
        RateSurface.Invite => new SurfaceRateLimit(InviteLimit, InviteWindowSeconds, InviteMaxKeys),
        RateSurface.CiExchange => new SurfaceRateLimit(CiExchangeLimit, CiExchangeWindowSeconds, CiExchangeMaxKeys),
        _ => throw new ArgumentOutOfRangeException(nameof(surface)),
    };
}

/// <summary>
/// One surface's budget: attempts per key, window length, and the key capacity paid with eviction of the oldest
/// windows. The defaults are the values every surface shared before the windows moved to PostgreSQL, so an
/// unconfigured deployment keeps the limits it had.
/// </summary>
/// <param name="Limit">Attempts admitted per key within <paramref name="WindowSeconds"/>.</param>
/// <param name="WindowSeconds">Length of the fixed window in seconds.</param>
/// <param name="MaxKeys">Keys this surface keeps before its oldest windows are evicted.</param>
internal readonly record struct SurfaceRateLimit(int Limit, int WindowSeconds, int MaxKeys);

/// <summary>
/// Refuses a surface budget the limiter cannot honour, naming the configuration key that must change. Bound with
/// <c>ValidateOnStart</c>: a bad value fails the boot, never a request.
/// </summary>
internal sealed class RateLimitOptionsValidator : IValidateOptions<RateLimitOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, RateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        CheckLimit(failures, nameof(RateLimitOptions.LoginLimit), options.LoginLimit);
        CheckWindow(failures, nameof(RateLimitOptions.LoginWindowSeconds), options.LoginWindowSeconds);
        CheckMaxKeys(failures, nameof(RateLimitOptions.LoginMaxKeys), options.LoginMaxKeys);
        CheckLimit(failures, nameof(RateLimitOptions.LinkLimit), options.LinkLimit);
        CheckWindow(failures, nameof(RateLimitOptions.LinkWindowSeconds), options.LinkWindowSeconds);
        CheckMaxKeys(failures, nameof(RateLimitOptions.LinkMaxKeys), options.LinkMaxKeys);
        CheckLimit(failures, nameof(RateLimitOptions.InviteLimit), options.InviteLimit);
        CheckWindow(failures, nameof(RateLimitOptions.InviteWindowSeconds), options.InviteWindowSeconds);
        CheckMaxKeys(failures, nameof(RateLimitOptions.InviteMaxKeys), options.InviteMaxKeys);
        CheckLimit(failures, nameof(RateLimitOptions.CiExchangeLimit), options.CiExchangeLimit);
        CheckWindow(failures, nameof(RateLimitOptions.CiExchangeWindowSeconds), options.CiExchangeWindowSeconds);
        CheckMaxKeys(failures, nameof(RateLimitOptions.CiExchangeMaxKeys), options.CiExchangeMaxKeys);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void CheckLimit(List<string> failures, string key, int value)
    {
        if (value is < 1 or > RateLimitOptions.MaxLimit)
        {
            failures.Add($"{Key(key)} must be between 1 and {RateLimitOptions.MaxLimit}");
        }
    }

    // The store refuses a window outside 1..86400 as well; this names the key instead of a SQLSTATE at request time.
    private static void CheckWindow(List<string> failures, string key, int value)
    {
        if (value is < 1 or > 86_400)
        {
            failures.Add($"{Key(key)} must be between 1 and 86400");
        }
    }

    private static void CheckMaxKeys(List<string> failures, string key, int value)
    {
        if (value < 1)
        {
            failures.Add($"{Key(key)} must be at least 1");
        }
    }

    private static string Key(string property) => $"{RateLimitOptions.SectionName}:{property}";
}
