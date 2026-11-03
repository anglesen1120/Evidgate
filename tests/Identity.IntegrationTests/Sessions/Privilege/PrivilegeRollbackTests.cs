using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Testing;
using Identity.IntegrationTests.Sessions.Store;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Privilege;

/// <summary>
/// R3 of the 02.3.2-T1 review: <see cref="ISessionPrivilegeChange.RequireRotationForUserAsync"/> runs on the
/// CALLER's tenant transaction (D5b/AC7), so a privilege change whose transaction rolls back leaves
/// <c>rotation_required_at</c> NULL — and the same write is visible once its caller commits.
/// </summary>
/// <remarks>
/// <para>
/// This is the reason the marking is not an outbox message: the flag has to appear and disappear with the role
/// change that caused it. The store ran and reported one marked session in both halves, so the null after the
/// rollback is the rollback's doing and not a write that never happened.
/// </para>
/// <para>
/// The session suites run as one block with <c>--filter-namespace 'Identity.IntegrationTests.Sessions*'</c>;
/// this file's namespace <c>Identity.IntegrationTests.Sessions.Privilege</c> is inside that wildcard.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class PrivilegeRollbackTests : SessionHardeningTestBase
{
    /// <summary>Shares the run's database: the one session row it reads is addressed by id.</summary>
    public PrivilegeRollbackTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// R3: the marking joins the caller's transaction — disposed without a commit it is gone, and the same call
    /// in a committed transaction stamps the live session with the clock's instant.
    /// </summary>
    [Fact]
    public async Task Require_Rotation_Rolls_Back_With_The_Callers_Transaction_And_Sticks_On_Commit()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var member = await SeedUserAsync(Unique.Email("require-rotation-rollback"));
        var clock = ClockAt();
        var session = await CreateSessionAsync(member, clock);

        await using (var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token))
        {
            // Concrete type (CA1859): the case exercises the store that implements the contract, not a sub.
            var privileges = new SessionRevocationStore(scope.Db, clock);

            (await privileges.RequireRotationForUserAsync(member, Token))
                .ShouldBe(1, "the live session of the member is the one the marking write matches");

            // The scope is disposed WITHOUT CommitAsync: the caller's privilege change rolled back.
        }

        (await OwnerScalarAsync<bool>(
            "select rotation_required_at is null and revoked_at is null from public.session where id = @id",
            ("id", session.SessionId)))
            .ShouldBeTrue("the rotation flag went away with the transaction that set it");

        await using (var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token))
        {
            // Concrete type (CA1859): the case exercises the store that implements the contract, not a sub.
            var privileges = new SessionRevocationStore(scope.Db, clock);

            (await privileges.RequireRotationForUserAsync(member, Token))
                .ShouldBe(1, "the same write in a committing transaction marks the same session");

            await scope.CommitAsync(Token);
        }

        (await OwnerScalarAsync<bool>(
            "select rotation_required_at = @now from public.session where id = @id",
            ("id", session.SessionId),
            ("now", clock.UtcNow)))
            .ShouldBeTrue("a committed privilege change leaves the flag carrying the caller's clock instant");

        // And the store's answer to the next request carries the flag (AC7), which is what the request path acts
        // on: the HTTP layer proves the presented antiforgery pair before it rotates (R1/SEC-1), the store is
        // where the flag itself is reported.
        var authenticated = await AuthenticateAsync(session.CookieValue, clock);
        authenticated.IsSuccess.ShouldBeTrue();
        authenticated.Value.Status.ShouldBe(SessionAuthStatus.Active);
        authenticated.Value.RotationRequired.ShouldBeTrue();
    }
}
