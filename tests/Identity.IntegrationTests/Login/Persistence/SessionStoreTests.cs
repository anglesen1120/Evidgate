using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Persistence;

/// <summary>
/// 02.3.1-T1.4 / 02.3.1 §8: <c>SessionStore</c> against the real server — the credential is stored only as
/// an HMAC, both expiries come from the injected clock, and every refusal (disabled account, unknown user,
/// missing pepper) is a value with no row behind it.
/// </summary>
/// <remarks>
/// The row is read back through the owner connection, which is the only role that can see
/// <c>public.session</c> (0041); the write itself always runs as <c>evidgate_app</c> through
/// <c>evidgate_resolve.session_create</c>.
/// </remarks>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class SessionStoreTests : SessionTestBase
{
    public SessionStoreTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// The credential rule of 02.3.1 §8: the cookie value the caller receives is nowhere in the row, and the
    /// stored hash is exactly HMAC-SHA256(pepper, value) with the pepper's version beside it. A database read
    /// therefore yields no usable cookie.
    /// </summary>
    [Fact]
    public async Task Raw_Value_Is_Never_Persisted()
    {
        var userId = await SeedUserAsync("raw@example.com");
        var store = CreateSessionStore(Clock());

        var result = await store.CreateAsync(new SessionCreation(userId, "203.0.113.7", "xunit"), Token);

        result.IsSuccess.ShouldBeTrue(result.Error?.Code ?? "unexpected success");
        var ticket = result.Value;

        // base64url without padding: 32 bytes are 43 characters and nothing in the value needs escaping.
        ticket.CookieValue.Length.ShouldBe(43);
        ticket.CookieValue.ShouldNotContain("=");
        ticket.CookieValue.ShouldNotContain("+");
        ticket.CookieValue.ShouldNotContain("/");

        var row = await OwnerRowAsync(
            "select row_to_json(s)::text, cookie_hmac, pepper_version "
            + "from public.session s where s.id = @id",
            ("id", ticket.SessionId));

        var storedRow = (string)row[0]!;
        storedRow.ShouldNotContain(ticket.CookieValue, Case.Sensitive, "the raw cookie value must never reach the database");

        var stored = (byte[])row[1]!;
        stored.Length.ShouldBe(32);
        stored.ShouldBe(ExpectedHmac(ticket.CookieValue));
        Convert.ToBase64String(stored).ShouldNotBe(ticket.CookieValue);

        row[2].ShouldBe(PepperVersion);

        (await OwnerScalarAsync<string>(
            "select encode(cookie_hmac, 'hex') from public.session where id = @id",
            ("id", ticket.SessionId))).ShouldBe(Convert.ToHexString(ExpectedHmac(ticket.CookieValue)).ToLowerInvariant());
    }

    /// <summary>
    /// 02.3.2 §4: the idle deadline is the clock plus <c>IdleSeconds</c> and the absolute one is the clock plus
    /// <c>AbsoluteSeconds</c> — both read from the injected <see cref="TimeProvider"/>, never from the
    /// database's <c>now()</c>, so one session's pair is consistent and a test can move it.
    /// </summary>
    [Fact]
    public async Task Expiries_Come_From_The_Injected_Clock()
    {
        var userId = await SeedUserAsync("clock@example.com");
        var clock = Clock();
        var store = CreateSessionStore(clock);

        var result = await store.CreateAsync(new SessionCreation(userId, null, null), Token);
        result.IsSuccess.ShouldBeTrue();

        var row = await OwnerRowAsync(
            "select expires_at = @expires_at, absolute_expires_at = @absolute_expires_at, ip is null, user_agent is null "
            + "from public.session where id = @id",
            ("id", result.Value.SessionId),
            ("expires_at", clock.UtcNow.AddHours(8)),
            ("absolute_expires_at", clock.UtcNow.AddDays(7)));

        row[0].ShouldBe(true, "expires_at is now + IdleSeconds (8 h by default)");
        row[1].ShouldBe(true, "absolute_expires_at is now + AbsoluteSeconds (7 d by default)");
        row[2].ShouldBe(true, "no address was given");
        row[3].ShouldBe(true, "no user agent was given");

        result.Value.ExpiresAt.ShouldBe(clock.UtcNow.AddHours(8), TimeSpan.FromMilliseconds(1));
    }

    /// <summary>02.3.1 §6: a disabled account gets 403 <c>account_disabled</c> and no session row at all.</summary>
    [Fact]
    public async Task Disabled_User_Is_Refused_And_No_Row_Is_Written()
    {
        var userId = await SeedUserAsync("blocked@example.com");
        await OwnerExecuteAsync(
            "update public.\"user\" set status = 'disabled' where id = @id",
            ("id", userId));

        var result = await CreateSessionStore(Clock()).CreateAsync(new SessionCreation(userId, null, "xunit"), Token);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.AccountDisabled);
        result.Error.Status.ShouldBe(403);
        result.Error.Retryable.ShouldBeFalse();

        (await OwnerScalarAsync<long>("select count(*) from public.session")).ShouldBe(0);
    }

    /// <summary>
    /// A user id no row holds cannot be signed in: 503 <c>unavailable</c>, retryable, and no session — the
    /// function answers a status instead of raising, so the login route stays a typed 00 §9.2 answer.
    /// </summary>
    [Fact]
    public async Task Unknown_User_Is_Unavailable_And_No_Row_Is_Written()
    {
        var result = await CreateSessionStore(Clock())
            .CreateAsync(new SessionCreation(Guid.CreateVersion7(), null, "xunit"), Token);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.Unavailable);
        result.Error.Status.ShouldBe(503);
        result.Error.Retryable.ShouldBeTrue();

        (await OwnerScalarAsync<long>("select count(*) from public.session")).ShouldBe(0);
    }

    /// <summary>
    /// A deployment without the pepper cannot hash a cookie, so it must not mint one: 503
    /// <c>unavailable</c>, retryable, no row (the <c>EVIDGATE:Ci</c> posture applied to sessions).
    /// </summary>
    [Fact]
    public async Task Missing_Pepper_Is_Unavailable()
    {
        var userId = await SeedUserAsync("nopepper@example.com");
        var store = CreateSessionStore(Clock(), pepper: string.Empty);

        var result = await store.CreateAsync(new SessionCreation(userId, null, "xunit"), Token);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.Unavailable);
        result.Error.Retryable.ShouldBeTrue();

        (await OwnerScalarAsync<long>("select count(*) from public.session")).ShouldBe(0);
    }

    /// <summary>
    /// Two sessions never share a hash (the unique index of 0042), and each login gets its own row and its own
    /// cookie value — a replayed callback cannot alias an existing session.
    /// </summary>
    [Fact]
    public async Task Every_Login_Gets_Its_Own_Row_And_Hash()
    {
        var userId = await SeedUserAsync("twice@example.com");
        var store = CreateSessionStore(Clock());

        var first = await store.CreateAsync(new SessionCreation(userId, null, "xunit"), Token);
        var second = await store.CreateAsync(new SessionCreation(userId, null, "xunit"), Token);

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        first.Value.CookieValue.ShouldNotBe(second.Value.CookieValue);
        first.Value.SessionId.ShouldNotBe(second.Value.SessionId);

        (await OwnerScalarAsync<long>(
            "select count(distinct cookie_hmac) from public.session where user_id = @user_id",
            ("user_id", userId))).ShouldBe(2);
    }

    /// <summary>
    /// The client address and the user agent are untrusted client input: an address that is not an
    /// <c>inet</c> literal is stored as NULL, an IPv6 zone id is stripped (the logins of 02.3.1-T1.1 hand-over
    /// vi), and a user agent longer than the column's 512 characters is bounded instead of failing the login.
    /// </summary>
    [Fact]
    public async Task Bad_Address_Is_Stored_As_Null_And_The_User_Agent_Is_Bounded()
    {
        var userId = await SeedUserAsync("input@example.com");
        var store = CreateSessionStore(Clock());

        var bad = await store.CreateAsync(new SessionCreation(userId, "not-an-address", new string('a', 513)), Token);
        bad.IsSuccess.ShouldBeTrue("a bad address must never abort a login every other gate admitted");
        (await OwnerScalarAsync<bool>(
            "select ip is null from public.session where id = @id",
            ("id", bad.Value.SessionId))).ShouldBeTrue();
        (await OwnerScalarAsync<int>(
            "select char_length(user_agent) from public.session where id = @id",
            ("id", bad.Value.SessionId))).ShouldBe(512);

        var zoned = await store.CreateAsync(new SessionCreation(userId, "fe80::1%eth0", null), Token);
        zoned.IsSuccess.ShouldBeTrue();
        (await OwnerScalarAsync<string>(
            "select host(ip) from public.session where id = @id",
            ("id", zoned.Value.SessionId))).ShouldBe("fe80::1");
    }

    /// <summary>The store's own row is a live session: <c>revoked_at</c> and <c>rotated_from</c> stay null.</summary>
    [Fact]
    public async Task A_Created_Session_Is_Live()
    {
        var userId = await SeedUserAsync("live@example.com");
        var result = await CreateSessionStore(Clock()).CreateAsync(new SessionCreation(userId, null, "xunit"), Token);
        result.IsSuccess.ShouldBeTrue();

        (await OwnerScalarAsync<bool>(
            "select revoked_at is null and rotated_from is null and created_at = last_seen_at "
            + "from public.session where id = @id",
            ("id", result.Value.SessionId))).ShouldBeTrue();
    }

    /// <summary>A clock the test owns, so both expiries are computed from a known instant.</summary>
    private static MutableClock Clock() =>
        new(new DateTimeOffset(2031, 5, 4, 3, 2, 1, TimeSpan.Zero));
}
