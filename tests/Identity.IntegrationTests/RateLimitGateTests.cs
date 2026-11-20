using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Options;
using Evidgate.Testing;
using Identity.IntegrationTests.Login.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class RateLimitGateTests(DatabaseFixture fixture)
    : SessionTestBase(fixture, DatabaseIsolation.Dedicated)
{
    [Theory]
    [InlineData("Login", 1, 1)]
    [InlineData("Login", 3, 2)]
    [InlineData("Link", 1, 1)]
    [InlineData("Link", 3, 2)]
    public async Task Acquire_TwoUnitCostAtAnOddLimit_OnlyTheFirstUnitMayRefuse(
        string surfaceName, int limit, int admittedAttempts)
    {
        var surface = Enum.Parse<RateSurface>(surfaceName);
        var limiter = Limiter(AppDataSource, limit);

        for (var attempt = 0; attempt < admittedAttempts; attempt++)
        {
            var allowed = await RateLimitGate.AcquireAsync(
                new DefaultHttpContext(), limiter, surface, "weighted", 2, "refused", Token);
            allowed.ShouldBeNull("the first unit decides an admitted weighted attempt; a later unit never refuses it");
        }

        var context = new DefaultHttpContext();
        var refused = await RateLimitGate.AcquireAsync(
            context, limiter, surface, "weighted", 2, "refused", Token);

        refused.ShouldNotBeNull();
        refused.Code.ShouldBe(ApiErrorCode.RateLimited);
        refused.Status.ShouldBe(StatusCodes.Status429TooManyRequests);
        refused.Retryable.ShouldBeTrue();
        int.TryParse(context.Response.Headers.RetryAfter.ToString(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var retryAfter).ShouldBeTrue();
        retryAfter.ShouldBeInRange(1, 3600);
    }

    [Theory]
    [InlineData("Login")]
    [InlineData("Link")]
    [InlineData("Invite")]
    [InlineData("CiExchange")]
    public async Task Acquire_UnreachableStoreOnEachSurface_ReturnsNeutral503WithOneSecondRetry(
        string surfaceName)
    {
        var surface = Enum.Parse<RateSurface>(surfaceName);
        var healthy = await RateLimitGate.AcquireAsync(
            new DefaultHttpContext(), Limiter(AppDataSource), surface, "outage-control", 1, "refused", Token);
        healthy.ShouldBeNull("the same surface and key are admitted when the store is reachable");
        await using var closed = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Username=x;Timeout=1;Command Timeout=1;Pooling=false");
        var context = new DefaultHttpContext();

        var refused = await RateLimitGate.AcquireAsync(
            context, Limiter(closed), surface, "outage-control", 1, "refused", Token);

        refused.ShouldNotBeNull();
        refused.Code.ShouldBe(ApiErrorCode.Unavailable);
        refused.Status.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        refused.Retryable.ShouldBeTrue();
        refused.Message.ShouldBe("the service cannot answer right now; retry shortly");
        context.Response.Headers.RetryAfter.ToString().ShouldBe("1");
    }

    [Fact]
    public async Task Acquire_TheSameKeyOnTwoSurfaces_KeepsIndependentLimits()
    {
        var limiter = new ExchangeRateLimiter(AppDataSource, Options.Create(new RateLimitOptions
        {
            LoginLimit = 1,
            LoginWindowSeconds = 3600,
            LinkLimit = 2,
            LinkWindowSeconds = 3600,
        }));
        var login = new DefaultHttpContext();
        var link = new DefaultHttpContext();

        (await RateLimitGate.AcquireAsync(login, limiter, RateSurface.Login, "same-key", 1, "refused", Token))
            .ShouldBeNull();
        (await RateLimitGate.AcquireAsync(login, limiter, RateSurface.Login, "same-key", 1, "refused", Token))
            .ShouldNotBeNull().Code.ShouldBe(ApiErrorCode.RateLimited);
        (await RateLimitGate.AcquireAsync(link, limiter, RateSurface.Link, "same-key", 1, "refused", Token))
            .ShouldBeNull();
        (await RateLimitGate.AcquireAsync(link, limiter, RateSurface.Link, "same-key", 1, "refused", Token))
            .ShouldBeNull("the link limit is two, independently of the exhausted login limit of one");
        (await RateLimitGate.AcquireAsync(link, limiter, RateSurface.Link, "same-key", 1, "refused", Token))
            .ShouldNotBeNull().Code.ShouldBe(ApiErrorCode.RateLimited);
    }

    [Fact]
    public async Task Acquire_AnAddressKey_StoresOnlyItsSha256Digest()
    {
        const string key = "login:2001:db8:1:2::/64";
        (await RateLimitGate.AcquireAsync(
            new DefaultHttpContext(), Limiter(AppDataSource), RateSurface.Login, key, 1, "refused", Token))
            .ShouldBeNull();

        var stored = await OwnerScalarAsync<byte[]>(
            "select key_hash from public.rate_limit_window where surface = 'login'");

        stored.ShouldBe(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        stored.Length.ShouldBe(32);
    }

    [Fact]
    public async Task Acquire_ARequestCancellation_PropagatesInsteadOfBecomingStoreUnavailable()
    {
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await cancelled.CancelAsync();
        var context = new DefaultHttpContext();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await RateLimitGate.AcquireAsync(
                context, Limiter(AppDataSource), RateSurface.Login, "cancelled", 1, "refused", cancelled.Token));

        context.Response.Headers.ContainsKey("Retry-After").ShouldBeFalse();
    }

    private static ExchangeRateLimiter Limiter(NpgsqlDataSource dataSource, int limit = 3) =>
        new(dataSource, Options.Create(new RateLimitOptions
        {
            LoginLimit = limit,
            LoginWindowSeconds = 3600,
            LinkLimit = limit,
            LinkWindowSeconds = 3600,
            InviteLimit = limit,
            InviteWindowSeconds = 3600,
            CiExchangeLimit = limit,
            CiExchangeWindowSeconds = 3600,
        }));
}
