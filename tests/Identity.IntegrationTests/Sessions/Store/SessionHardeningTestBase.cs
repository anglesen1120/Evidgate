using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Testing;
using Identity.IntegrationTests.Login;
using Identity.IntegrationTests.Login.Persistence;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Store;

/// <summary>
/// The shared arrangement of the 02.3.2-T1.1 store suites (AC1-AC4, AC7): a session store, the cookie
/// authenticator and the revocation store, each over the app-role data source with a clock the test moves,
/// plus the pepper windows a rotation test needs.
/// </summary>
/// <remarks>
/// Every credential assertion reads the row through the owner connection (<c>evidgate_app</c> holds no
/// privilege on <c>public.session</c>, 0041) while the write under test always runs as the app role through the
/// <c>evidgate_resolve</c> definers. The sessions are created by the real <c>SessionStore</c>, so the cookie
/// values are the ones production mints.
/// </remarks>
[Trait("Category", "Docker")]
public abstract class SessionHardeningTestBase : SessionTestBase
{
    /// <summary>The version stored beside hashes made with <see cref="SessionTestBase.Pepper"/>.</summary>
    protected const int CurrentVersion = PepperVersion;

    /// <summary>The version stored beside hashes made with the retired pepper.</summary>
    protected const int RetiredVersion = 6;

    /// <summary>A second pepper, the one a rotation window keeps alive.</summary>
    protected static readonly byte[] RetiredPepper =
        Enumerable.Range(100, 32).Select(index => (byte)index).ToArray();

    /// <summary>The retired pepper as configuration carries it.</summary>
    protected static string RetiredPepperBase64 => Convert.ToBase64String(RetiredPepper);

    protected SessionHardeningTestBase(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// Resets the harness database, then restores the migration-seeded permission catalogue.
    /// </summary>
    /// <remarks>
    /// These suites arrange memberships with a built-in role (<c>tenant_admin</c>, <c>viewer</c>) and custom
    /// roles carrying <c>membership:write</c>, so the catalogue rows Respawn's blanket truncate removes have to
    /// be back before the test body runs — the same step <see cref="CatalogueTestBase"/> takes for its suites.
    /// </remarks>
    public override async ValueTask InitializeAsync()
    {
        await Fixture.ResetAsync(Token);
        await CatalogueSeed.ApplyAsync(Fixture, Token);
    }

    /// <summary>A clock the test owns and moves, so every deadline is provable without waiting.</summary>
    /// <param name="start">The first instant, or the suite's fixed default.</param>
    private protected static MutableClock ClockAt(DateTimeOffset? start = null) =>
        new(start ?? new DateTimeOffset(2032, 3, 4, 5, 6, 7, TimeSpan.Zero));

    /// <summary>The session options a rotation window is described with.</summary>
    /// <param name="pepper">The current pepper (base64), or null.</param>
    /// <param name="version">The current pepper's version.</param>
    /// <param name="previous">The previous pepper (base64), or null for no window.</param>
    /// <param name="previousVersion">The previous pepper's version.</param>
    private protected static IOptions<SessionOptions> OptionsFor(
        string? pepper,
        int version = CurrentVersion,
        string? previous = null,
        int? previousVersion = null) =>
        Options.Create(new SessionOptions
        {
            Pepper = pepper,
            PepperVersion = version,
            PreviousPepper = previous,
            PreviousPepperVersion = previousVersion,
        });

    /// <summary>A session store over the app-role data source.</summary>
    /// <param name="clock">The clock the expiries come from.</param>
    /// <param name="pepper">The current pepper (base64); the suite's pepper by default.</param>
    /// <param name="version">The current pepper's version.</param>
    /// <param name="previous">The previous pepper (base64), or null.</param>
    /// <param name="previousVersion">The previous pepper's version.</param>
    private protected SessionStore CreateStore(
        TimeProvider clock,
        string? pepper = null,
        int version = CurrentVersion,
        string? previous = null,
        int? previousVersion = null)
    {
        var options = OptionsFor(pepper ?? PepperBase64, version, previous, previousVersion);

        return new SessionStore(AppDataSource, options, new SessionSecrets(options), clock);
    }

    /// <summary>The cookie authenticator over the app-role data source.</summary>
    /// <param name="clock">The clock the slide decision comes from.</param>
    /// <param name="pepper">The current pepper (base64); the suite's pepper by default.</param>
    /// <param name="version">The current pepper's version.</param>
    /// <param name="previous">The previous pepper (base64), or null.</param>
    /// <param name="previousVersion">The previous pepper's version.</param>
    private protected SessionAuthenticationStore CreateAuthenticator(
        TimeProvider clock,
        string? pepper = null,
        int version = CurrentVersion,
        string? previous = null,
        int? previousVersion = null)
    {
        var options = OptionsFor(pepper ?? PepperBase64, version, previous, previousVersion);

        return new SessionAuthenticationStore(AppDataSource, options, new SessionSecrets(options), clock);
    }

    /// <summary>One cookie-authenticated request against the default pepper window.</summary>
    /// <param name="cookieValue">The presented cookie value, or null for a request without one.</param>
    /// <param name="clock">The clock the decision and the slide use.</param>
    /// <param name="pepper">The current pepper (base64); the suite's pepper by default.</param>
    /// <param name="version">The current pepper's version.</param>
    /// <param name="previous">The previous pepper (base64), or null.</param>
    /// <param name="previousVersion">The previous pepper's version.</param>
    protected Task<Result<SessionAuthResult>> AuthenticateAsync(
        string? cookieValue,
        TimeProvider clock,
        string? pepper = null,
        int version = CurrentVersion,
        string? previous = null,
        int? previousVersion = null) =>
        CreateAuthenticator(clock, pepper, version, previous, previousVersion)
            .AuthenticateAsync(cookieValue, Token)
            .AsTask();

    /// <summary>Creates one session through the real store and returns its cookie value.</summary>
    /// <param name="userId">The account the session belongs to.</param>
    /// <param name="clock">The clock the store stamps with.</param>
    /// <param name="predecessorCookieValue">The cookie the login callback forwarded, or null.</param>
    /// <param name="ip">The client address to store, or null.</param>
    /// <param name="userAgent">The user agent to store, or null.</param>
    protected async Task<SessionTicket> CreateSessionAsync(
        Guid userId,
        TimeProvider clock,
        string? predecessorCookieValue = null,
        string? ip = "203.0.113.7",
        string? userAgent = "xunit")
    {
        var result = await CreateStore(clock)
            .CreateAsync(new SessionCreation(userId, ip, userAgent, predecessorCookieValue), Token);

        result.IsSuccess.ShouldBeTrue(result.Error?.Code ?? "session creation must succeed");

        return result.Value;
    }

    /// <summary>HMAC-SHA256 of a presented value under an explicit pepper.</summary>
    /// <param name="pepper">The pepper bytes.</param>
    /// <param name="cookieValue">The presented value.</param>
    protected static byte[] HmacOf(byte[] pepper, string cookieValue) =>
        HMACSHA256.HashData(pepper, Encoding.ASCII.GetBytes(cookieValue));

    /// <summary>A value no session was minted from (43 base64url characters, like a real one).</summary>
    protected static string UnknownCookieValue() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
