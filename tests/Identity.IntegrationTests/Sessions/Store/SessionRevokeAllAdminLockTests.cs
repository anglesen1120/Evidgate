using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Store;

/// <summary>
/// The lock order of the administrative revoke-all, and what it does when another writer of the same org or the
/// same user runs beside it: the per-user lock comes before any session row, the org's chain lock after them, and
/// two writers that take those locks in the same order always finish.
/// </summary>
/// <remarks>
/// Each case parks the calls on a lock the test holds itself and proves the wait from <c>pg_locks</c>, so the
/// interleaving is observed rather than hoped for and no case waits out a sleep.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class SessionRevokeAllAdminLockTests : SessionRevokeAllAdminTestBase
{
    /// <summary>Own database: a lock observation wants a database no other test class writes.</summary>
    /// <param name="fixture">The assembly fixture.</param>
    public SessionRevokeAllAdminLockTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// The lock order is the invariant: the call parks on the target's per-user lock FIRST, holding no lock on a
    /// session row and none on the org's chain, and only after that lock is free does it revoke and append.
    /// </summary>
    [Fact]
    public async Task An_Admin_Revoke_All_Waits_For_The_Users_Lock_Before_It_Touches_A_Session_Row()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        await CreateSessionAsync(member, clock);
        await CreateSessionAsync(member, clock);

        var proof = ProofOf(adminTicket);
        var now = clock.GetUtcNow();

        // The holder takes the same per-user key the writers take, and keeps it open.
        await using var holder = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await holder.OpenAsync(Token);
        await using var held = await holder.BeginTransactionAsync(Token);
        await ExecuteAsync(holder, held, "select evidgate.session_user_lock(@user)", ("user", member));

        await using var waiter = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await waiter.OpenAsync(Token);

        var pending = Task.Run(
            () => AdminRevokeAllSqlAsync(waiter, null, proof, member, org, now),
            Token);

        await WaitForUserLockWaitersAsync(member, expected: 1);
        pending.IsCompleted.ShouldBeFalse("the call parks on the target's user lock");

        (await OwnerScalarAsync<long>(
            "select count(*) from pg_locks where pid = @pid and locktype = 'relation' and granted "
            + "and relation = 'public.session'::regclass and mode = 'RowExclusiveLock'",
            ("pid", waiter.ProcessID))).ShouldBe(0L, "no session row is touched before the user lock is held");

        (await OwnerScalarAsync<long>(
            "select count(*) from pg_locks where pid = @pid and locktype = 'advisory' and granted and objsubid = 1 "
            + "and objid::bigint = (hashtext('audit:' || @org)::bigint & 4294967295)",
            ("pid", waiter.ProcessID),
            ("org", org.ToString("D")))).ShouldBe(0L, "the org's chain lock comes after the user lock");

        await held.CommitAsync(Token);

        var (status, revoked) = await pending.WaitAsync(WaitLimit, Token);

        status.ShouldBe("revoked");
        revoked.ShouldBe(2);
        (await LiveCountAsync(member)).ShouldBe(0L);
        (await AdminRowCountAsync(org)).ShouldBe(1L);
    }

    /// <summary>
    /// The caller is proved AGAIN once the target's user lock is held: an administrator whose membership ends (or
    /// loses its role), or a target who leaves the org, while the call is parked on the lock is refused with the
    /// same <c>not_found</c> every refusal gets, and nothing is revoked or recorded.
    /// </summary>
    /// <param name="change">What commits while the call waits.</param>
    [Theory]
    [InlineData("actor_removed")]
    [InlineData("actor_role_revoked")]
    [InlineData("target_removed")]
    public async Task An_Admin_Revoke_All_Is_Refused_When_The_Caller_No_Longer_Qualifies_After_The_Lock_Wait(
        string change)
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var memberTicket = await CreateSessionAsync(member, clock);

        await using var holder = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await holder.OpenAsync(Token);
        await using var held = await holder.BeginTransactionAsync(Token);
        await ExecuteAsync(holder, held, "select evidgate.session_user_lock(@user)", ("user", member));

        await using var waiter = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await waiter.OpenAsync(Token);

        var pending = Task.Run(
            () => AdminRevokeAllSqlAsync(waiter, null, ProofOf(adminTicket), member, org, clock.GetUtcNow()),
            Token);

        await WaitForUserLockWaitersAsync(member, expected: 1);
        pending.IsCompleted.ShouldBeFalse("the call parks on the target's user lock after authorising");

        // Committed while the call waits: the first check already passed, only the re-check can see it.
        switch (change)
        {
            case "actor_removed":
                await OwnerExecuteAsync(
                    "update public.membership set status = 'removed' where org_id = @org and user_id = @user",
                    ("org", org), ("user", admin));
                break;
            case "actor_role_revoked":
                await OwnerExecuteAsync(
                    "delete from public.membership_role mr using public.membership m "
                    + "where mr.membership_id = m.id and m.org_id = @org and m.user_id = @user",
                    ("org", org), ("user", admin));
                break;
            default:
                await OwnerExecuteAsync(
                    "update public.membership set status = 'removed' where org_id = @org and user_id = @user",
                    ("org", org), ("user", member));
                break;
        }

        await held.CommitAsync(Token);

        var (status, revoked) = await pending.WaitAsync(WaitLimit, Token);

        status.ShouldBe("not_found", "the caller no longer qualifies once the lock is held");
        revoked.ShouldBe(0);
        (await IsLiveAsync(memberTicket.SessionId)).ShouldBeTrue("a refused call revokes nothing");
        (await AdminRowCountAsync(org)).ShouldBe(0L, "a refusal writes no audit row");
    }

    /// <summary>
    /// Two writers of one user — the administrative call and the member-removal definer's bare
    /// <c>session_revoke_all_for_user</c> call (not the self-service revoke-all route) — queue on the same per-user
    /// lock and BOTH complete whatever order they were started in: no deadlock (<c>40P01</c>) in either direction,
    /// the two counts add up to the rows that existed, and a row is written only when the administrative call
    /// revoked something.
    /// </summary>
    /// <param name="first">The writer started first; the other starts once the first is queued.</param>
    [Theory]
    [InlineData("admin")]
    [InlineData("bare")]
    public async Task Admin_Revoke_All_And_A_Bare_Revoke_All_For_User_Complete_In_Either_Order_Without_A_Deadlock(
        string first)
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        await CreateSessionAsync(member, clock);
        await CreateSessionAsync(member, clock);

        var proof = ProofOf(adminTicket);
        var now = clock.GetUtcNow();

        await using var holder = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await holder.OpenAsync(Token);
        await using var held = await holder.BeginTransactionAsync(Token);
        await ExecuteAsync(holder, held, "select evidgate.session_user_lock(@user)", ("user", member));

        await using var adminConnection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await using var bareConnection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await adminConnection.OpenAsync(Token);
        await bareConnection.OpenAsync(Token);

        Task<(string Status, int Revoked)> adminCall;
        Task<int> bareCall;
        if (string.Equals(first, "admin", StringComparison.Ordinal))
        {
            adminCall = Task.Run(() => AdminRevokeAllSqlAsync(adminConnection, null, proof, member, org, now), Token);
            await WaitForUserLockWaitersAsync(member, expected: 1);
            bareCall = Task.Run(() => BareRevokeAllForUserSqlAsync(bareConnection, member, now), Token);
        }
        else
        {
            bareCall = Task.Run(() => BareRevokeAllForUserSqlAsync(bareConnection, member, now), Token);
            await WaitForUserLockWaitersAsync(member, expected: 1);
            adminCall = Task.Run(() => AdminRevokeAllSqlAsync(adminConnection, null, proof, member, org, now), Token);
        }

        // Both are queued on ONE key before anything is let through: the serialisation is observed, not hoped for.
        await WaitForUserLockWaitersAsync(member, expected: 2);
        adminCall.IsCompleted.ShouldBeFalse();
        bareCall.IsCompleted.ShouldBeFalse();

        await held.RollbackAsync(Token);

        var adminResult = await adminCall.WaitAsync(WaitLimit, Token);
        var bareRevoked = await bareCall.WaitAsync(WaitLimit, Token);

        adminResult.Status.ShouldBe("revoked", "the administrative call completes after the bare writer released the lock");
        (adminResult.Revoked + bareRevoked).ShouldBe(
            2,
            "the two writers saw the same rows in sequence: between them they revoked every live session once");
        (await LiveCountAsync(member)).ShouldBe(0L);
        (await AdminRowCountAsync(org)).ShouldBe(
            adminResult.Revoked > 0 ? 1L : 0L,
            "the row exists exactly when the administrative call itself revoked something");
        (await IsLiveAsync(adminTicket.SessionId)).ShouldBeTrue("neither writer targets the caller's own session");
    }

    /// <summary>
    /// The administrative bulk call and the single administrative revoke of a row of the SAME target, started in
    /// either order: the first writer parks on the org's chain lock, the second on the row the first has already
    /// stamped, and after the chain lock is released both finish — no deadlock (<c>40P01</c>), because both
    /// take their session-row locks BEFORE the chain lock, so no writer waits for a row while holding the chain.
    /// </summary>
    /// <param name="first">The writer started first; the other starts once the first is queued.</param>
    [Theory]
    [InlineData("bulk")]
    [InlineData("single")]
    public async Task Admin_Revoke_All_And_A_Single_Admin_Revoke_Complete_In_Either_Order_Without_A_Deadlock(
        string first)
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var addressed = await CreateSessionAsync(member, clock);
        await CreateSessionAsync(member, clock);

        var proof = ProofOf(adminTicket);
        var now = clock.GetUtcNow();

        // The chain lock both writers take last is held here, so each start order is forced rather than raced.
        await using var holder = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await holder.OpenAsync(Token);
        await using var held = await holder.BeginTransactionAsync(Token);
        await ExecuteAsync(
            holder,
            held,
            "select pg_advisory_xact_lock(hashtext('audit:' || @org))",
            ("org", org.ToString("D")));

        await using var bulkConnection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await bulkConnection.OpenAsync(Token);
        await using var singleConnection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await singleConnection.OpenAsync(Token);

        var (bulkCall, singleCall) = await StartInOrderAsync(
            first, org, proof, member, addressed.SessionId, now, bulkConnection, singleConnection);

        bulkCall.IsCompleted.ShouldBeFalse("both writers are queued before either is let through");
        singleCall.IsCompleted.ShouldBeFalse();

        await held.RollbackAsync(Token);

        var bulkResult = await bulkCall.WaitAsync(WaitLimit, Token);
        var singleStatus = await singleCall.WaitAsync(WaitLimit, Token);

        await AssertBothWritersFinishedAsync(first, org, bulkResult, singleStatus);

        (await LiveCountAsync(member)).ShouldBe(0L, "the two calls together end every live row of the target");
        (await IsLiveAsync(adminTicket.SessionId)).ShouldBeTrue("neither call targets the caller's own session");
    }

    /// <summary>
    /// Starts the two writers in <paramref name="first"/>'s order, and waits until the second one is queued: the
    /// first is observably parked on the chain lock (this case holds it), the second on whatever the first holds.
    /// </summary>
    /// <param name="first">The writer started first: <c>bulk</c> or <c>single</c>.</param>
    /// <param name="org">The org whose chain the held lock blocks.</param>
    /// <param name="proof">The admin's cookie proof, the caller both calls use.</param>
    /// <param name="member">The target account.</param>
    /// <param name="addressedSessionId">The one session row the single revoke addresses.</param>
    /// <param name="now">The instant both calls pass as <c>p_now</c>.</param>
    /// <param name="bulkConnection">The bulk call's own connection.</param>
    /// <param name="singleConnection">The single call's own connection.</param>
    private async Task<(Task<(string Status, int Revoked)> Bulk, Task<string> Single)> StartInOrderAsync(
        string first,
        Guid org,
        byte[] proof,
        Guid member,
        Guid addressedSessionId,
        DateTimeOffset now,
        NpgsqlConnection bulkConnection,
        NpgsqlConnection singleConnection)
    {
        Task<(string Status, int Revoked)> bulk;
        Task<string> single;

        if (string.Equals(first, "bulk", StringComparison.Ordinal))
        {
            bulk = Task.Run(() => AdminRevokeAllSqlAsync(bulkConnection, null, proof, member, org, now), Token);
            await WaitForChainLockWaitAsync(bulkConnection.ProcessID, org);
            single = Task.Run(
                () => SingleAdminRevokeSqlAsync(singleConnection, proof, addressedSessionId, org, now),
                Token);
            await WaitForUngrantedLockAsync(singleConnection.ProcessID);

            return (bulk, single);
        }

        single = Task.Run(
            () => SingleAdminRevokeSqlAsync(singleConnection, proof, addressedSessionId, org, now),
            Token);
        await WaitForChainLockWaitAsync(singleConnection.ProcessID, org);
        bulk = Task.Run(() => AdminRevokeAllSqlAsync(bulkConnection, null, proof, member, org, now), Token);
        await WaitForUngrantedLockAsync(bulkConnection.ProcessID);

        return (bulk, single);
    }

    /// <summary>The outcome each start order has to produce, and the chain it leaves behind.</summary>
    /// <param name="first">The writer that was started first.</param>
    /// <param name="org">The org whose chain both writers append to.</param>
    /// <param name="bulk">The administrative bulk call's answer.</param>
    /// <param name="single">The single administrative revoke's answer.</param>
    private async Task AssertBothWritersFinishedAsync(
        string first,
        Guid org,
        (string Status, int Revoked) bulk,
        string single)
    {
        if (string.Equals(first, "bulk", StringComparison.Ordinal))
        {
            // The bulk call stamped both rows first, so the single revoke re-evaluates its predicate against the
            // committed rows, finds nothing live, and writes no second row.
            bulk.Status.ShouldBe("revoked");
            bulk.Revoked.ShouldBe(2);
            single.ShouldBe("not_found", "the row it addressed was already revoked by the bulk call");
            (await AdminRowCountAsync(org)).ShouldBe(1L, "one row for the only call that revoked something");
            return;
        }

        // The single revoke committed first; the bulk call then ends the remaining row and records that count.
        single.ShouldBe("revoked");
        bulk.Status.ShouldBe("revoked");
        bulk.Revoked.ShouldBe(1);
        (await AdminRowCountAsync(org)).ShouldBe(2L, "one row per call that revoked something");
    }
}
