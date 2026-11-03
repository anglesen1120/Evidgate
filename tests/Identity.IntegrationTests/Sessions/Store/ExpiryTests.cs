using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Store;

/// <summary>
/// 02.3.2 AC2/AC3/AC4/AC8 at the store level: the idle deadline slides at most once per 60 s and never past
/// the absolute one, an expired or revoked session is answered before any handler runs, and a disabled
/// account is the caller's 403 rather than an authenticated request.
/// </summary>
/// <remarks>
/// Every deadline is driven by the injected <see cref="TimeProvider"/>, so no test waits. The slide is
/// observed on the row, not on the result: <c>last_seen_at</c> moving (or not) is exactly what the throttle
/// promises.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class ExpiryTests : SessionHardeningTestBase
{
    private static readonly TimeSpan Idle = TimeSpan.FromHours(8);

    /// <summary>Shares the run's database: every deadline is observed on the row the test created.</summary>
    public ExpiryTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>AC2: activity inside the 60 s window returns the session and writes nothing.</summary>
    [Fact]
    public async Task Idle_Activity_Inside_The_Throttle_Window_Writes_Nothing()
    {
        var userId = await SeedUserAsync(Unique.Email("slide-throttled"));
        var clock = ClockAt();
        var session = await CreateSessionAsync(userId, clock);

        clock.Advance(TimeSpan.FromSeconds(30));
        var result = await AuthenticateAsync(session.CookieValue, clock);

        result.IsSuccess.ShouldBeTrue(result.Error?.Code ?? "a live session authenticates");
        result.Value.Status.ShouldBe(SessionAuthStatus.Active);
        result.Value.SessionId.ShouldBe(session.SessionId);
        result.Value.UserId.ShouldBe(userId);
        result.Value.RotationRequired.ShouldBeFalse();

        (await OwnerScalarAsync<bool>(
            "select last_seen_at = @created and expires_at = @expires from public.session where id = @id",
            ("id", session.SessionId),
            ("created", clock.UtcNow.Subtract(TimeSpan.FromSeconds(30))),
            ("expires", clock.UtcNow.Subtract(TimeSpan.FromSeconds(30)).Add(Idle))))
            .ShouldBeTrue("inside 60 s of the last write the row is untouched");
    }

    /// <summary>AC2: activity after the window slides both deadlines once, and then throttles again.</summary>
    [Fact]
    public async Task Idle_Activity_Slides_Once_And_Then_Throttles()
    {
        var userId = await SeedUserAsync(Unique.Email("slide"));
        var clock = ClockAt();
        var session = await CreateSessionAsync(userId, clock);

        clock.Advance(TimeSpan.FromSeconds(90));
        var slid = await AuthenticateAsync(session.CookieValue, clock);
        slid.Value.Status.ShouldBe(SessionAuthStatus.Active);

        var afterSlide = clock.UtcNow;
        (await OwnerScalarAsync<bool>(
            "select last_seen_at = @now and expires_at = @expires from public.session where id = @id",
            ("id", session.SessionId),
            ("now", afterSlide),
            ("expires", afterSlide.Add(Idle))))
            .ShouldBeTrue("the slide moves last_seen_at and the idle deadline together");

        clock.Advance(TimeSpan.FromSeconds(30));
        var throttled = await AuthenticateAsync(session.CookieValue, clock);
        throttled.Value.Status.ShouldBe(SessionAuthStatus.Active);

        (await OwnerScalarAsync<bool>(
            "select last_seen_at = @last from public.session where id = @id",
            ("id", session.SessionId),
            ("last", afterSlide)))
            .ShouldBeTrue("a second request inside 60 s of the slide writes nothing");

        clock.Advance(TimeSpan.FromSeconds(40));
        var slidAgain = await AuthenticateAsync(session.CookieValue, clock);
        slidAgain.Value.Status.ShouldBe(SessionAuthStatus.Active);

        (await OwnerScalarAsync<bool>(
            "select last_seen_at = @now from public.session where id = @id",
            ("id", session.SessionId),
            ("now", clock.UtcNow)))
            .ShouldBeTrue("once 60 s have passed since the last write the next request slides again");
    }

    /// <summary>AC3: the slide is capped by <c>absolute_expires_at</c> — the row would violate its own check
    /// constraint otherwise, so this is the load-bearing half of the absolute deadline.</summary>
    [Fact]
    public async Task Absolute_Expiry_Caps_The_Idle_Slide()
    {
        var userId = await SeedUserAsync(Unique.Email("cap"));
        var clock = ClockAt();
        var session = await CreateSessionAsync(userId, clock);

        // The hard deadline is brought inside the idle lifetime, keeping the row's check constraint valid.
        var hard = clock.UtcNow.AddHours(2);
        await OwnerExecuteAsync(
            "update public.session set expires_at = @hard, absolute_expires_at = @hard where id = @id",
            ("id", session.SessionId),
            ("hard", hard));

        clock.Advance(TimeSpan.FromSeconds(90));
        var result = await AuthenticateAsync(session.CookieValue, clock);

        result.Value.Status.ShouldBe(SessionAuthStatus.Active);
        result.Value.SessionId.ShouldBe(session.SessionId);

        (await OwnerScalarAsync<bool>(
            "select expires_at = @hard from public.session where id = @id",
            ("id", session.SessionId),
            ("hard", hard)))
            .ShouldBeTrue("the slide stops at the absolute deadline instead of extending it");
    }

    /// <summary>AC3: past the absolute deadline the session is expired, and the row is not written.</summary>
    [Fact]
    public async Task A_Session_Past_Its_Absolute_Deadline_Is_Expired()
    {
        var userId = await SeedUserAsync(Unique.Email("absolute"));
        var clock = ClockAt();
        var session = await CreateSessionAsync(userId, clock);

        clock.Advance(TimeSpan.FromDays(7).Add(TimeSpan.FromSeconds(1)));
        var result = await AuthenticateAsync(session.CookieValue, clock);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Status.ShouldBe(SessionAuthStatus.Expired);

        // The contract's `SessionAuthResult` carries the ROW's ids for every status but `unknown` (only a value
        // that matched no row has nothing to name), so a caller can attribute an expired request to its account
        // without a second statement. Nothing of it reaches the client: the edge renders the 401 and the ids stay
        // inside the host.
        result.Value.SessionId.ShouldBe(session.SessionId);
        result.Value.UserId.ShouldBe(userId);

        (await OwnerScalarAsync<bool>(
            "select last_seen_at = @created and revoked_at is null from public.session where id = @id",
            ("id", session.SessionId),
            ("created", clock.UtcNow.Subtract(TimeSpan.FromDays(7).Add(TimeSpan.FromSeconds(1))))))
            .ShouldBeTrue("an expired session is not touched and not stamped");
    }

    /// <summary>AC2: past the idle deadline (still inside the absolute one) the session is expired.</summary>
    [Fact]
    public async Task A_Session_Past_Its_Idle_Deadline_Is_Expired()
    {
        var userId = await SeedUserAsync(Unique.Email("idle"));
        var clock = ClockAt();
        var session = await CreateSessionAsync(userId, clock);

        clock.Advance(Idle.Add(TimeSpan.FromSeconds(1)));
        var result = await AuthenticateAsync(session.CookieValue, clock);

        result.Value.Status.ShouldBe(SessionAuthStatus.Expired);

        // And the deadline handed back to the browser is the one the row carried, not a fresh one.
        (await OwnerScalarAsync<bool>(
            "select expires_at < @now and absolute_expires_at > @now from public.session where id = @id",
            ("id", session.SessionId),
            ("now", clock.UtcNow)))
            .ShouldBeTrue("only the idle deadline passed");
    }

    /// <summary>AC4/AC8: a revoked row answers <c>revoked</c> — on the very next request — and outranks
    /// <c>expired</c>, because the truth of the row is that it was revoked.</summary>
    [Fact]
    public async Task A_Revoked_Session_Is_Revoked_And_Outranks_Expiry()
    {
        var userId = await SeedUserAsync(Unique.Email("revoked"));
        var clock = ClockAt();
        var session = await CreateSessionAsync(userId, clock);

        await OwnerExecuteAsync(
            "update public.session set revoked_at = @now, expires_at = @past where id = @id",
            ("id", session.SessionId),
            ("now", clock.UtcNow),
            ("past", clock.UtcNow.Subtract(TimeSpan.FromMinutes(1))));

        var result = await AuthenticateAsync(session.CookieValue, clock);

        result.Value.Status.ShouldBe(SessionAuthStatus.Revoked);
        result.Value.SessionId.ShouldBe(session.SessionId, "a revoked row is still named by its own ids");
    }

    /// <summary>AC8: a live session of an account disabled afterwards answers <c>disabled</c> (403 at the edge).</summary>
    [Fact]
    public async Task A_Disabled_Accounts_Live_Session_Is_Disabled()
    {
        var userId = await SeedUserAsync(Unique.Email("disabled-later"));
        var clock = ClockAt();
        var session = await CreateSessionAsync(userId, clock);
        await OwnerExecuteAsync("update public.\"user\" set status = 'disabled' where id = @id", ("id", userId));

        var result = await AuthenticateAsync(session.CookieValue, clock);

        result.Value.Status.ShouldBe(SessionAuthStatus.Disabled);
        result.Value.UserId.ShouldBe(userId, "the host needs the account to answer 403 account_disabled");
    }

    /// <summary>
    /// A value no session was minted from is <c>unknown</c> — never an error, never a write — and so is a
    /// missing or over-long value, which is refused before it is hashed.
    /// </summary>
    [Fact]
    public async Task Unknown_Missing_And_Overlong_Values_Are_Unknown_And_Write_Nothing()
    {
        var userId = await SeedUserAsync(Unique.Email("unknown"));
        var clock = ClockAt();
        var session = await CreateSessionAsync(userId, clock);
        var created = clock.UtcNow;
        clock.Advance(TimeSpan.FromMinutes(5));

        foreach (var value in new[] { UnknownCookieValue(), null, string.Empty, new string('a', 200) })
        {
            var result = await AuthenticateAsync(value, clock);

            result.IsSuccess.ShouldBeTrue(result.Error?.Code ?? "an unknown cookie is a status, not a failure");
            result.Value.Status.ShouldBe(SessionAuthStatus.Unknown);
            result.Value.SessionId.ShouldBe(Guid.Empty);
            result.Value.UserId.ShouldBe(Guid.Empty);
        }

        (await OwnerScalarAsync<bool>(
            "select last_seen_at = @created and expires_at = @expires from public.session where id = @id",
            ("id", session.SessionId),
            ("created", created),
            ("expires", created.Add(Idle))))
            .ShouldBeTrue("no request with a bogus value may slide a real session");
    }

    /// <summary>A deployment without a pepper cannot verify anything: 503 <c>unavailable</c>, not "anonymous".</summary>
    [Fact]
    public async Task Without_A_Pepper_Authentication_Is_Unavailable()
    {
        var clock = ClockAt();
        var result = await AuthenticateAsync(UnknownCookieValue(), clock, pepper: string.Empty);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.Unavailable);
        result.Error.Status.ShouldBe(503);
        result.Error.Retryable.ShouldBeTrue();
    }
}
