using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Store;

/// <summary>
/// 02.3.2 §4, §6 and AC1/AC7 (D5a/D5b) at the store level: the successor of a rotation is a fresh random
/// value stored only as a hash, the predecessor dies in the same statement, and the login callback's
/// forwarded cookie becomes <c>rotated_from</c> only when it belongs to the same account.
/// </summary>
[Trait("Category", "Docker")]
public sealed class SessionStoreTests : SessionHardeningTestBase
{
    /// <summary>
    /// A database of its own: several cases assert an exact whole-table session count (0, 1, 2), which a shared
    /// database cannot keep.
    /// </summary>
    public SessionStoreTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    /// <summary>
    /// AC1 + AC7: the rotated cookie value is new, 43 base64url characters, and nowhere in the database —
    /// only HMAC-SHA256(current pepper, value) with the current version beside it. The superseded row is
    /// revoked and names its successor's parent.
    /// </summary>
    [Fact]
    public async Task Raw_Value_Is_Never_Persisted_On_Rotation()
    {
        var userId = await SeedUserAsync("rotate-raw@example.com");
        var clock = ClockAt();
        var predecessor = await CreateSessionAsync(userId, clock);

        clock.Advance(TimeSpan.FromMinutes(5));

        var result = await CreateStore(clock).RotateAsync(new SessionRotation(predecessor.SessionId, null, null), Token);

        result.IsSuccess.ShouldBeTrue(result.Error?.Code ?? "rotation must succeed");
        var successor = result.Value;

        successor.SessionId.ShouldNotBe(predecessor.SessionId);
        successor.CookieValue.ShouldNotBe(predecessor.CookieValue);
        successor.CookieValue.Length.ShouldBe(43);
        successor.CookieValue.ShouldNotContain("=");

        var row = await OwnerRowAsync(
            "select row_to_json(s)::text, cookie_hmac, pepper_version, rotated_from, revoked_at from public.session s where id = @id",
            ("id", successor.SessionId));

        ((string)row[0]!).ShouldNotContain(successor.CookieValue, Case.Sensitive, "the raw cookie value must never reach the database");
        ((byte[])row[1]!).ShouldBe(ExpectedHmac(successor.CookieValue));
        row[2].ShouldBe(CurrentVersion);
        row[3].ShouldBe(predecessor.SessionId, "the successor records the row it rotated from");
        row[4].ShouldBeNull("the successor is live");

        (await OwnerScalarAsync<bool>(
            "select revoked_at is not null from public.session where id = @id",
            ("id", predecessor.SessionId))).ShouldBeTrue("the predecessor is revoked in the same statement");
    }

    /// <summary>
    /// D5b: the successor inherits the hard deadline and the client metadata — a rotation never extends the
    /// absolute lifetime — and its idle deadline is the rotation instant plus the idle lifetime capped by it.
    /// </summary>
    [Fact]
    public async Task Rotation_Inherits_Metadata_And_Keeps_The_Absolute_Deadline()
    {
        var userId = await SeedUserAsync("rotate-inherit@example.com");
        var clock = ClockAt();
        var predecessor = await CreateSessionAsync(userId, clock, ip: "198.51.100.9", userAgent: "Mozilla/5.0 xunit");

        clock.Advance(TimeSpan.FromHours(1));

        var result = await CreateStore(clock).RotateAsync(new SessionRotation(predecessor.SessionId, null, null), Token);
        result.IsSuccess.ShouldBeTrue(result.Error?.Code ?? "rotation must succeed");

        var row = await OwnerRowAsync(
            "select host(ip), user_agent, expires_at = @expires_at, absolute_expires_at = @absolute_expires_at "
            + "from public.session where id = @id",
            ("id", result.Value.SessionId),
            ("expires_at", clock.UtcNow.AddHours(8)),
            ("absolute_expires_at", clock.UtcNow.AddDays(7).Subtract(TimeSpan.FromHours(1))));

        row[0].ShouldBe("198.51.100.9");
        row[1].ShouldBe("Mozilla/5.0 xunit");
        row[2].ShouldBe(true, "the idle deadline is the rotation instant plus the idle lifetime");
        row[3].ShouldBe(true, "an absolute deadline is never extended by a rotation");

        // The ticket carries that hard deadline, which is what the cookie writer bounds a rotated cookie by
        // (addendum (b)): it is the predecessor's, not the rotation instant plus the absolute lifetime.
        result.Value.AbsoluteExpiresAt.ShouldBe(
            clock.UtcNow.AddDays(7).Subtract(TimeSpan.FromHours(1)),
            TimeSpan.FromMilliseconds(1));
        result.Value.ExpiresAt.ShouldBe(clock.UtcNow.AddHours(8), TimeSpan.FromMilliseconds(1));
    }

    /// <summary>
    /// D5b, single winner: the second rotation of the same predecessor matches no live row, so it inserts
    /// nothing and answers 401 <c>session_revoked</c> — the old cookie is dead, which is the truth of the row.
    /// </summary>
    [Fact]
    public async Task The_Second_Rotation_Of_One_Session_Answers_Revoked_And_Inserts_Nothing()
    {
        var userId = await SeedUserAsync("rotate-twice@example.com");
        var clock = ClockAt();
        var predecessor = await CreateSessionAsync(userId, clock);
        var store = CreateStore(clock);

        var first = await store.RotateAsync(new SessionRotation(predecessor.SessionId, null, null), Token);
        first.IsSuccess.ShouldBeTrue(first.Error?.Code ?? "the first rotation wins");

        var second = await store.RotateAsync(new SessionRotation(predecessor.SessionId, null, null), Token);

        second.IsSuccess.ShouldBeFalse();
        second.Error!.Code.ShouldBe(ApiErrorCode.SessionRevoked);
        second.Error.Status.ShouldBe(401);
        second.Error.Retryable.ShouldBeFalse();

        (await OwnerScalarAsync<long>(
            "select count(*) from public.session where rotated_from = @id",
            ("id", predecessor.SessionId))).ShouldBe(1, "exactly one successor exists");
    }

    /// <summary>An unknown session id rotates nothing and answers 401 <c>session_revoked</c>.</summary>
    [Fact]
    public async Task Rotating_An_Unknown_Session_Answers_Revoked()
    {
        var result = await CreateStore(ClockAt()).RotateAsync(new SessionRotation(Guid.CreateVersion7(), null, null), Token);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.SessionRevoked);
        (await OwnerScalarAsync<long>("select count(*) from public.session")).ShouldBe(0);
    }

    /// <summary>An expired session is not rotated: the answer is <c>session_revoked</c> and no successor exists.</summary>
    [Fact]
    public async Task Rotating_An_Expired_Session_Answers_Revoked()
    {
        var userId = await SeedUserAsync("rotate-expired@example.com");
        var clock = ClockAt();
        var session = await CreateSessionAsync(userId, clock);
        clock.Advance(TimeSpan.FromDays(8));

        var result = await CreateStore(clock).RotateAsync(new SessionRotation(session.SessionId, null, null), Token);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.SessionRevoked);
        (await OwnerScalarAsync<long>(
            "select count(*) from public.session where rotated_from = @id",
            ("id", session.SessionId))).ShouldBe(0);
    }

    /// <summary>An account disabled after the session was minted is not rotated: 403 <c>account_disabled</c>.</summary>
    [Fact]
    public async Task Rotating_A_Disabled_Accounts_Session_Answers_AccountDisabled()
    {
        var userId = await SeedUserAsync("rotate-disabled@example.com");
        var clock = ClockAt();
        var session = await CreateSessionAsync(userId, clock);
        await OwnerExecuteAsync("update public.\"user\" set status = 'disabled' where id = @id", ("id", userId));

        var result = await CreateStore(clock).RotateAsync(new SessionRotation(session.SessionId, null, null), Token);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.AccountDisabled);
        result.Error.Status.ShouldBe(403);
        (await OwnerScalarAsync<long>("select count(*) from public.session")).ShouldBe(1, "no successor was minted");
    }

    /// <summary>
    /// D5a: the cookie the login callback forwards is revoked in the same statement as the insert, and it
    /// becomes the new row's <c>rotated_from</c> — a browser signing in again never leaves two live sessions.
    /// </summary>
    [Fact]
    public async Task The_Forwarded_Cookie_Is_Revoked_And_Linked_As_The_Predecessor()
    {
        var userId = await SeedUserAsync("login-rotate@example.com");
        var clock = ClockAt();
        var previous = await CreateSessionAsync(userId, clock);

        var current = await CreateSessionAsync(userId, clock, predecessorCookieValue: previous.CookieValue);

        (await OwnerScalarAsync<bool>(
            "select revoked_at is not null from public.session where id = @id",
            ("id", previous.SessionId))).ShouldBeTrue("the forwarded session dies");

        (await OwnerRowAsync(
            "select rotated_from from public.session where id = @id",
            ("id", current.SessionId)))[0].ShouldBe(previous.SessionId);
    }

    /// <summary>
    /// D5a boundary: a forwarded cookie of ANOTHER account in the same browser is revoked too (it is not this
    /// account's session, so it is not the predecessor) — the stale credential of the previous person dies
    /// and the new row records no parent.
    /// </summary>
    [Fact]
    public async Task A_Foreign_Forwarded_Cookie_Is_Revoked_But_Not_Recorded_As_Predecessor()
    {
        var owner = await SeedUserAsync("first-person@example.com");
        var next = await SeedUserAsync("second-person@example.com");
        var clock = ClockAt();
        var foreign = await CreateSessionAsync(owner, clock);

        var current = await CreateSessionAsync(next, clock, predecessorCookieValue: foreign.CookieValue);

        (await OwnerScalarAsync<bool>(
            "select revoked_at is not null from public.session where id = @id",
            ("id", foreign.SessionId))).ShouldBeTrue("a stale forwarded cookie is always killed");

        (await OwnerRowAsync(
            "select rotated_from from public.session where id = @id",
            ("id", current.SessionId)))[0].ShouldBeNull();
    }

    /// <summary>
    /// D5a boundary: a forwarded value that matches no row, or one no server could have minted, never fails the
    /// login — the insert still happens and nothing is revoked.
    /// </summary>
    [Fact]
    public async Task An_Unmatched_Forwarded_Cookie_Does_Not_Fail_The_Login()
    {
        var userId = await SeedUserAsync("bogus-forward@example.com");
        var clock = ClockAt();

        var byUnknownValue = await CreateSessionAsync(userId, clock, predecessorCookieValue: UnknownCookieValue());
        var byOverlongValue = await CreateSessionAsync(userId, clock, predecessorCookieValue: new string('a', 200));

        byUnknownValue.CookieValue.Length.ShouldBe(43);
        byOverlongValue.CookieValue.Length.ShouldBe(43);

        (await OwnerScalarAsync<long>("select count(*) from public.session")).ShouldBe(2, "both logins inserted a session");
        (await OwnerScalarAsync<long>("select count(*) from public.session where rotated_from is not null")).ShouldBe(0);
        (await OwnerScalarAsync<long>("select count(*) from public.session where revoked_at is not null")).ShouldBe(0);
    }
}
