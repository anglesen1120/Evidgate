using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Options;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// <see cref="RateLimitOptionsValidator"/>: the shipped defaults are the budgets the process-local limiter used,
/// <see cref="RateLimitOptions.For"/> maps each surface to its three scalars, and every out-of-range scalar is
/// refused with a message naming the exact configuration key an operator must fix.
/// </summary>
public sealed class RateLimitOptionsValidatorTests
{
    private const string Section = "EVIDGATE:Identity:RateLimits";

    private static readonly string[] Surfaces = ["Login", "Link", "Invite", "CiExchange"];

    private static readonly RateLimitOptionsValidator Validator = new();

    [Fact]
    public void The_Defaults_Keep_The_Process_Local_Budgets_And_Pass()
    {
        var options = new RateLimitOptions();

        foreach (var surface in new[] { RateSurface.Login, RateSurface.Link, RateSurface.Invite, RateSurface.CiExchange })
        {
            options.For(surface).ShouldBe(new SurfaceRateLimit(30, 60, 50_000), $"{surface} keeps 30/60/50000");
        }

        Validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Login", 0)]
    [InlineData("Login", -1)]
    [InlineData("Link", 0)]
    [InlineData("Invite", -5)]
    [InlineData("CiExchange", 0)]
    [InlineData("CiExchange", 1_000_001)]
    public void A_Limit_Outside_The_Range_Is_Refused_With_Its_Key(string surface, int limit)
    {
        var result = Validator.Validate(null, With(surface, limit: limit));

        result.Succeeded.ShouldBeFalse();
        result.Failures!.ShouldContain($"{Section}:{surface}Limit must be between 1 and 1000000");
    }

    [Theory]
    [InlineData("Login", 0)]
    [InlineData("Login", -1)]
    [InlineData("Link", 86_401)]
    [InlineData("Invite", 0)]
    [InlineData("CiExchange", 86_401)]
    public void A_Window_Outside_The_Range_Is_Refused_With_Its_Key(string surface, int windowSeconds)
    {
        var result = Validator.Validate(null, With(surface, windowSeconds: windowSeconds));

        result.Succeeded.ShouldBeFalse();
        result.Failures!.ShouldContain($"{Section}:{surface}WindowSeconds must be between 1 and 86400");
    }

    [Theory]
    [InlineData("Login", 0)]
    [InlineData("Link", -1)]
    [InlineData("Invite", 0)]
    [InlineData("CiExchange", -100)]
    public void A_Key_Bound_Below_One_Is_Refused_With_Its_Key(string surface, int maxKeys)
    {
        var result = Validator.Validate(null, With(surface, maxKeys: maxKeys));

        result.Succeeded.ShouldBeFalse();
        result.Failures!.ShouldContain($"{Section}:{surface}MaxKeys must be at least 1");
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1_000_000, 86_400, 50_000)]
    public void The_Boundaries_Of_Every_Scalar_Pass(int limit, int windowSeconds, int maxKeys)
    {
        foreach (var surface in Surfaces)
        {
            Validator.Validate(null, With(surface, limit, windowSeconds, maxKeys)).Succeeded
                .ShouldBeTrue($"{surface} accepts {limit}/{windowSeconds}/{maxKeys}");
        }
    }

    [Fact]
    public void Every_Broken_Surface_Is_Reported_In_Declaration_Order()
    {
        var result = Validator.Validate(null, WithAllBroken());

        var messages = result.Failures!.ToList();
        messages.Count.ShouldBe(12, "three scalars on each of the four surfaces");
        messages[0].ShouldStartWith($"{Section}:LoginLimit");
        messages[3].ShouldStartWith($"{Section}:LinkLimit");
        messages[6].ShouldStartWith($"{Section}:InviteLimit");
        messages[9].ShouldStartWith($"{Section}:CiExchangeLimit");
    }

    /// <summary>Defaults with the named surface's three scalars replaced.</summary>
    private static RateLimitOptions With(string surface, int? limit = null, int? windowSeconds = null, int? maxKeys = null)
    {
        var defaults = new RateLimitOptions();
        var changed = new SurfaceRateLimit(
            limit ?? defaults.LoginLimit,
            windowSeconds ?? defaults.LoginWindowSeconds,
            maxKeys ?? defaults.LoginMaxKeys);
        var login = surface == "Login" ? changed : defaults.For(RateSurface.Login);
        var link = surface == "Link" ? changed : defaults.For(RateSurface.Link);
        var invite = surface == "Invite" ? changed : defaults.For(RateSurface.Invite);
        var ci = surface == "CiExchange" ? changed : defaults.For(RateSurface.CiExchange);

        return new RateLimitOptions
        {
            LoginLimit = login.Limit,
            LoginWindowSeconds = login.WindowSeconds,
            LoginMaxKeys = login.MaxKeys,
            LinkLimit = link.Limit,
            LinkWindowSeconds = link.WindowSeconds,
            LinkMaxKeys = link.MaxKeys,
            InviteLimit = invite.Limit,
            InviteWindowSeconds = invite.WindowSeconds,
            InviteMaxKeys = invite.MaxKeys,
            CiExchangeLimit = ci.Limit,
            CiExchangeWindowSeconds = ci.WindowSeconds,
            CiExchangeMaxKeys = ci.MaxKeys,
        };
    }

    /// <summary>Every scalar of every surface outside its range, so all twelve failures must appear.</summary>
    private static RateLimitOptions WithAllBroken() =>
        new()
        {
            LoginLimit = 0,
            LoginWindowSeconds = 0,
            LoginMaxKeys = 0,
            LinkLimit = 0,
            LinkWindowSeconds = 0,
            LinkMaxKeys = 0,
            InviteLimit = 0,
            InviteWindowSeconds = 0,
            InviteMaxKeys = 0,
            CiExchangeLimit = 0,
            CiExchangeWindowSeconds = 0,
            CiExchangeMaxKeys = 0,
        };
}
