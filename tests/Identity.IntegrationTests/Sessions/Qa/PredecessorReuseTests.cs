using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Identity.IntegrationTests.Sessions.Store;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Qa;

/// <summary>
/// QA5 of the 02.3.2-T1 review: two logins (<c>ISessions.CreateAsync</c>) run at the same time and both present
/// the SAME predecessor cookie value — a browser that fired two history entries, or a client that retried the
/// callback. Both logins succeed, the predecessor is revoked, and exactly one successor is linked to it.
/// </summary>
/// <remarks>
/// <para>
/// The invariant is timing-independent, which is why these cases need no artificial lock and no wait: the
/// predecessor half of <c>evidgate_resolve.session_create</c> (migration 0045) is a single
/// <c>update … where cookie_hmac = any (p_predecessor_hmacs) and revoked_at is null returning s.id</c>, and the
/// insert takes <c>rotated_from</c> from that CTE. A row can be revoked once, so the update matches for exactly
/// one of the two statements; the other's update matches no row at all, its CTE is empty, and its insert lands
/// with <c>rotated_from is null</c>. Both inserts therefore succeed: the second session is an UNLINKED FRESH
/// session of the same user, not a second copy of the predecessor, and it does not resurrect anything — the
/// predecessor's own cookie answers <c>revoked</c> afterwards.
/// </para>
/// <para>
/// The same holds when the two statements are serialized by the row lock instead of interleaving: whichever
/// order the server picks, one statement sees the row live and one sees it revoked or already revoked. That is
/// what makes the assertions below exact rather than probabilistic.
/// </para>
/// <para>
/// The session suites run as one block with <c>--filter-namespace 'Identity.IntegrationTests.Sessions*'</c>;
/// this file's namespace <c>Identity.IntegrationTests.Sessions.Qa</c> is inside that wildcard.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class PredecessorReuseTests : SessionHardeningTestBase
{
    /// <summary>Shares the run's database: the rows it counts belong to the account it seeded.</summary>
    public PredecessorReuseTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// QA5/D5a: two concurrent logins forwarding one predecessor value both mint a session, the predecessor is
    /// revoked once, exactly one successor carries <c>rotated_from</c>, the other is an unlinked fresh session,
    /// and the account is left with exactly the two successors live.
    /// </summary>
    [Fact]
    public async Task Two_Concurrent_Logins_On_One_Predecessor_Both_Succeed_And_Link_Only_One()
    {
        var userId = await SeedUserAsync(Unique.Email("reused-predecessor"));
        var clock = ClockAt();
        var predecessor = await CreateSessionAsync(userId, clock);
        var store = CreateStore(clock);

        // Started before either is awaited: both statements are in flight at the same time on purpose, and the
        // assertions below hold for every interleaving the server may choose.
        var first = store
            .CreateAsync(new SessionCreation(userId, "203.0.113.7", "xunit", predecessor.CookieValue), Token)
            .AsTask();
        var second = store
            .CreateAsync(new SessionCreation(userId, "203.0.113.7", "xunit", predecessor.CookieValue), Token)
            .AsTask();

        var created = await Task.WhenAll(first, second);

        for (var index = 0; index < created.Length; index++)
        {
            created[index].IsSuccess.ShouldBeTrue(
                "login " + index + " must succeed: a reused predecessor never fails a login ("
                + (created[index].Error?.Code ?? "no error") + ")");
        }

        var successors = created.Select(result => result.Value).ToArray();
        successors[0].SessionId.ShouldNotBe(successors[1].SessionId, "two logins, two rows");
        successors[0].CookieValue.ShouldNotBe(successors[1].CookieValue, "each login minted its own credential");

        (await OwnerScalarAsync<bool>(
            "select revoked_at is not null and rotation_required_at is null from public.session where id = @id",
            ("id", predecessor.SessionId)))
            .ShouldBeTrue("the predecessor is revoked once, and a revoked row carries no pending rotation");

        (await OwnerScalarAsync<long>(
            "select count(*) from public.session where rotated_from = @id",
            ("id", predecessor.SessionId)))
            .ShouldBe(1, "the update behind rotated_from matches the live row for exactly one of the two logins");

        var linked = await OwnerScalarAsync<Guid>(
            "select id from public.session where rotated_from = @id",
            ("id", predecessor.SessionId));
        var unlinked = successors.Single(ticket => ticket.SessionId != linked).SessionId;

        (await OwnerScalarAsync<bool>(
            "select rotated_from is null and revoked_at is null from public.session where id = @id",
            ("id", unlinked)))
            .ShouldBeTrue(
                "the login that found the predecessor already revoked is an unlinked fresh session, which is "
                + "accepted: it does not resurrect the predecessor");

        (await OwnerScalarAsync<long>(
            "select count(*) from public.session where user_id = @user and revoked_at is null",
            ("user", userId)))
            .ShouldBe(2, "the browser ends up with exactly the two successors, and the predecessor is dead");

        // Both successors really are credentials: the request path admits each of them.
        foreach (var successor in successors)
        {
            var authenticated = await AuthenticateAsync(successor.CookieValue, clock);
            authenticated.IsSuccess.ShouldBeTrue();
            authenticated.Value.Status.ShouldBe(SessionAuthStatus.Active);
        }

        // …and the predecessor is not: the reuse did not leave it usable.
        var recycled = await AuthenticateAsync(predecessor.CookieValue, clock);
        recycled.IsSuccess.ShouldBeTrue();
        recycled.Value.Status.ShouldBe(SessionAuthStatus.Revoked);
        recycled.Value.SessionId.ShouldBe(predecessor.SessionId);
    }
}
