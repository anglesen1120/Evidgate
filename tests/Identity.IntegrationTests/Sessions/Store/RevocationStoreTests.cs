using Evidgate.Modules.Identity.Sessions;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Store;

/// <summary>
/// 02.3.2 AC4/AC7 at the store level (<c>ISessionRevocation</c>, <c>ISessionPrivilegeChange</c>): the two
/// writes run on the CALLER's tenant scope — same transaction — so a rolled-back membership change or role
/// change takes its revocations with it, and nothing of another account is touched.
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class RevocationStoreTests : SessionHardeningTestBase
{
    public RevocationStoreTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// AC4: every live session of the member is revoked and counted; an already-revoked row keeps its original
    /// timestamp, and another account's sessions survive.
    /// </summary>
    [Fact]
    public async Task Revoke_All_For_A_User_Stamps_The_Live_Sessions_Only()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var member = await SeedUserAsync("revoke-all@example.com");
        var bystander = await SeedUserAsync("bystander@example.com");
        var clock = ClockAt();

        var first = await CreateSessionAsync(member, clock);
        var second = await CreateSessionAsync(member, clock);
        var alreadyRevoked = await CreateSessionAsync(member, clock);
        var other = await CreateSessionAsync(bystander, clock);

        await OwnerExecuteAsync(
            "update public.session set revoked_at = @stamp where id = @id",
            ("id", alreadyRevoked.SessionId),
            ("stamp", clock.UtcNow.Subtract(TimeSpan.FromDays(1))));

        await using var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token);
        var store = new SessionRevocationStore(scope.Db, clock);

        var revoked = await store.RevokeAllForUserAsync(member, Token);

        revoked.ShouldBe(2, "only the two live sessions are stamped");
        await scope.CommitAsync(Token);

        foreach (var session in new[] { first, second })
        {
            (await OwnerScalarAsync<bool>(
                "select revoked_at is not null and rotation_required_at is null from public.session where id = @id",
                ("id", session.SessionId)))
                .ShouldBeTrue("a revoked session carries no pending rotation");
        }

        (await OwnerScalarAsync<bool>(
            "select revoked_at = @stamp from public.session where id = @id",
            ("id", alreadyRevoked.SessionId),
            ("stamp", clock.UtcNow.Subtract(TimeSpan.FromDays(1)))))
            .ShouldBeTrue("a revocation is never re-stamped");

        (await OwnerScalarAsync<bool>(
            "select revoked_at is null from public.session where id = @id",
            ("id", other.SessionId)))
            .ShouldBeTrue("another account keeps its sessions");
    }

    /// <summary>
    /// AC4/§4: the write joins the caller's transaction — a membership change that rolls back leaves the
    /// member's sessions live, which is the whole reason this is not an outbox message.
    /// </summary>
    [Fact]
    public async Task Revoke_All_Rolls_Back_With_The_Callers_Transaction()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var member = await SeedUserAsync("revoke-rollback@example.com");
        var clock = ClockAt();
        var session = await CreateSessionAsync(member, clock);

        await using (var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token))
        {
            var store = new SessionRevocationStore(scope.Db, clock);
            (await store.RevokeAllForUserAsync(member, Token)).ShouldBe(1);

            // The scope is disposed WITHOUT CommitAsync: the caller's transaction rolled back.
        }

        (await OwnerScalarAsync<bool>(
            "select revoked_at is null from public.session where id = @id",
            ("id", session.SessionId)))
            .ShouldBeTrue("the revocation went away with the transaction that made it");
    }

    /// <summary>
    /// AC7: a role change flags every live session of the member, and the flag is stamped again by a later
    /// change so it always carries the most recent privilege change.
    /// </summary>
    [Fact]
    public async Task Require_Rotation_Flags_Live_Sessions_And_Re_Stamps_The_Latest_Change()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var member = await SeedUserAsync("require-rotation@example.com");
        var clock = ClockAt();

        var live = await CreateSessionAsync(member, clock);
        var expired = await CreateSessionAsync(member, clock);
        var revoked = await CreateSessionAsync(member, clock);

        await OwnerExecuteAsync(
            "update public.session set expires_at = @past, absolute_expires_at = @past where id = @id",
            ("id", expired.SessionId),
            ("past", clock.UtcNow.Subtract(TimeSpan.FromMinutes(5))));
        await OwnerExecuteAsync(
            "update public.session set revoked_at = @now where id = @id",
            ("id", revoked.SessionId),
            ("now", clock.UtcNow));

        await using var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token);
        var store = new SessionRevocationStore(scope.Db, clock);

        (await store.RequireRotationForUserAsync(member, Token)).ShouldBe(1, "only the live session is flagged");
        await scope.CommitAsync(Token);

        (await OwnerScalarAsync<bool>(
            "select rotation_required_at = @now from public.session where id = @id",
            ("id", live.SessionId),
            ("now", clock.UtcNow)))
            .ShouldBeTrue();

        (await OwnerScalarAsync<bool>(
            "select rotation_required_at is null from public.session where id = @id",
            ("id", expired.SessionId)))
            .ShouldBeTrue("an expired session is never rotated, it is answered");
        (await OwnerScalarAsync<bool>(
            "select rotation_required_at is null from public.session where id = @id",
            ("id", revoked.SessionId)))
            .ShouldBeTrue();

        // A second privilege change at a later instant moves the stamp forward.
        clock.Advance(TimeSpan.FromMinutes(30));
        await using (var second = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token))
        {
            var later = new SessionRevocationStore(second.Db, clock);
            (await later.RequireRotationForUserAsync(member, Token)).ShouldBe(1);
            await second.CommitAsync(Token);
        }

        (await OwnerScalarAsync<bool>(
            "select rotation_required_at = @now from public.session where id = @id",
            ("id", live.SessionId),
            ("now", clock.UtcNow)))
            .ShouldBeTrue("the flag records the latest privilege change");

        // And the next request with that session is told to rotate (AC7).
        var result = await AuthenticateAsync(live.CookieValue, clock);
        result.Value.Status.ShouldBe(Evidgate.Modules.Identity.Contracts.SessionAuthStatus.Active);
        result.Value.RotationRequired.ShouldBeTrue();
    }

    /// <summary>A user with no live session is not an error: both writes answer zero.</summary>
    [Fact]
    public async Task A_User_With_No_Live_Sessions_Is_Counted_As_Zero()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var member = await SeedUserAsync("no-sessions@example.com");
        var clock = ClockAt();

        await using var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token);
        var store = new SessionRevocationStore(scope.Db, clock);

        (await store.RevokeAllForUserAsync(member, Token)).ShouldBe(0);
        (await store.RequireRotationForUserAsync(member, Token)).ShouldBe(0);
        await scope.CommitAsync(Token);
    }
}
