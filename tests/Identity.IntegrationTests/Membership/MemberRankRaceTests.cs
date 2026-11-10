using System.Diagnostics;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Membership;

/// <summary>
/// The lock half of the member rank rule, exercised on two real application connections: the decision
/// transaction locks the target and actor membership rows exactly as <c>MemberRankGuard</c> does, and a
/// concurrent grant on the target's row must wait for the foreign key's key-share lock. Without that wait a
/// promotion could commit between the rank read and the write, letting a lower-rank administrator make a change
/// the promoted target would have refused.
/// </summary>
/// <remarks>
/// The blocked state is observed in <c>pg_locks</c> by pid, never inferred from a sleep. The grant transaction
/// carries a short <c>lock_timeout</c> so a missing conflict fails the test instead of hanging it.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class MemberRankRaceTests : TenantTest
{
    /// <summary>How long the grant's insert may wait for the decision's row lock.</summary>
    private const int GrantLockTimeoutSeconds = 5;

    /// <summary>The deadline for observing the grant's ungranted lock in <c>pg_locks</c>.</summary>
    private const int BlockedDeadlineMilliseconds = 3000;

    /// <summary>Poll interval while the observer looks for the grant backend's waiting lock.</summary>
    private const int PollMilliseconds = 20;

    public MemberRankRaceTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    /// <summary>
    /// A lower-rank decision (equal rank at read time) locks the target, reads rank one and deletes the target's
    /// custom grant; the concurrent <c>tenant_admin</c> grant is not granted a lock until that decision commits.
    /// The final row therefore never has the lower-rank delete committed after the promotion.
    /// </summary>
    [Fact]
    public async Task Concurrent_Promotion_Waits_For_The_Lower_Rank_Decision_To_Commit()
    {
        var world = await SeedRaceWorldAsync();
        await using var observer = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await observer.OpenAsync(Token);

        await using var decision = await OpenScopedAsync(Fixture.AppConnectionString, world.OrgId);
        await using var decisionTransaction = (NpgsqlTransaction)await decision.BeginTransactionAsync(Token);

        (await LockAndCompareAsync(decision, decisionTransaction, world)).ShouldBeTrue(
            "the actor and target both rank one before the promotion");

        await using var grant = await OpenScopedAsync(Fixture.AppConnectionString, world.OrgId);
        await using var grantTransaction = (NpgsqlTransaction)await grant.BeginTransactionAsync(Token);
        await ExecuteAsync(
            grant,
            grantTransaction,
            $"set local lock_timeout = '{GrantLockTimeoutSeconds}s'",
            Token);

        var grantPid = await ScalarAsync<int>(
            grant,
            grantTransaction,
            "select pg_backend_pid()",
            Token);

        var insert = InsertTenantAdminGrantAsync(grant, grantTransaction, world);
        await WaitUntilBlockedAsync(observer, grantPid, insert);
        insert.IsCompleted.ShouldBeFalse(
            "the foreign key's key-share lock must keep the promotion behind the decision");

        (await ExecuteAsync(
            decision,
            decisionTransaction,
            "delete from public.membership_role "
            + "where org_id = @org_id and membership_id = @target_membership and role_id = @custom_role",
            Token,
            ("org_id", world.OrgId),
            ("target_membership", world.TargetMembershipId),
            ("custom_role", world.CustomRoleId))).ShouldBe(1);

        await decisionTransaction.CommitAsync(Token);
        (await insert).ShouldBe(1);
        await grantTransaction.CommitAsync(Token);

        await using var reader = await OpenScopedAsync(Fixture.AppConnectionString, world.OrgId);
        (await RankAsync(reader, world)).ShouldBe(
            2,
            "the promotion is the only grant left on the target");
        (await HoldsGrantAsync(reader, world, world.CustomRoleId)).ShouldBeFalse();
    }

    /// <summary>
    /// The same wait when the decision rolls back: the grant stays ungranted while the lock is held, then lands
    /// after the rollback. The decision performs the same one-row delete before rolling back, so the surviving
    /// custom grant proves the mutation was undone rather than never attempted.
    /// </summary>
    [Fact]
    public async Task Concurrent_Promotion_Waits_For_The_Lower_Rank_Decision_To_Roll_Back()
    {
        var world = await SeedRaceWorldAsync();
        await using var observer = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await observer.OpenAsync(Token);

        await using var decision = await OpenScopedAsync(Fixture.AppConnectionString, world.OrgId);
        await using var decisionTransaction = (NpgsqlTransaction)await decision.BeginTransactionAsync(Token);

        (await LockAndCompareAsync(decision, decisionTransaction, world)).ShouldBeTrue();

        await using var grant = await OpenScopedAsync(Fixture.AppConnectionString, world.OrgId);
        await using var grantTransaction = (NpgsqlTransaction)await grant.BeginTransactionAsync(Token);
        await ExecuteAsync(
            grant,
            grantTransaction,
            $"set local lock_timeout = '{GrantLockTimeoutSeconds}s'",
            Token);

        var grantPid = await ScalarAsync<int>(
            grant,
            grantTransaction,
            "select pg_backend_pid()",
            Token);

        var insert = InsertTenantAdminGrantAsync(grant, grantTransaction, world);
        await WaitUntilBlockedAsync(observer, grantPid, insert);
        insert.IsCompleted.ShouldBeFalse(
            "the promotion waits for the decision even when that decision will roll back");

        (await ExecuteAsync(
            decision,
            decisionTransaction,
            "delete from public.membership_role "
            + "where org_id = @org_id and membership_id = @target_membership and role_id = @custom_role",
            Token,
            ("org_id", world.OrgId),
            ("target_membership", world.TargetMembershipId),
            ("custom_role", world.CustomRoleId))).ShouldBe(1);

        await decisionTransaction.RollbackAsync(Token);
        (await insert).ShouldBe(1);
        await grantTransaction.CommitAsync(Token);

        await using var reader = await OpenScopedAsync(Fixture.AppConnectionString, world.OrgId);
        (await RankAsync(reader, world)).ShouldBe(2);
        (await HoldsGrantAsync(reader, world, world.CustomRoleId)).ShouldBeTrue(
            "the rolled-back decision's delete left the target's original grant in place");
    }

    /// <summary>The guard's one lock statement and its one comparison, on the decision's connection.</summary>
    private static async Task<bool> LockAndCompareAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RaceWorld world)
    {
        var locked = await ScalarAsync<long>(
            connection,
            transaction,
            """
            select count(*) from (
              select m.id
                from public.membership m
               where m.org_id = @org_id
                 and (m.id = @target_membership
                      or (m.user_id = @actor_user and m.status = 'active'))
               order by m.id
                 for update) locked
            """,
            TestContext.Current.CancellationToken,
            ("org_id", world.OrgId),
            ("target_membership", world.TargetMembershipId),
            ("actor_user", world.ActorUserId));

        locked.ShouldBe(2, "the decision locks exactly the target and the actor's membership rows");

        return await ScalarAsync<bool>(
            connection,
            transaction,
            "select evidgate.session_admin_rank(@org_id, @actor_user) "
            + ">= evidgate.membership_rank(@org_id, @target_membership)",
            TestContext.Current.CancellationToken,
            ("org_id", world.OrgId),
            ("actor_user", world.ActorUserId),
            ("target_membership", world.TargetMembershipId));
    }

    /// <summary>The concurrent grant that adds the built-in <c>tenant_admin</c> to the target membership.</summary>
    private static Task<int> InsertTenantAdminGrantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RaceWorld world) =>
        ExecuteAsync(
            connection,
            transaction,
            """
            insert into public.membership_role
                (id, org_id, membership_id, role_id, granted_by)
            values
                (@id, @org_id, @target_membership, @admin_role, @actor_user)
            """,
            TestContext.Current.CancellationToken,
            ("id", Guid.CreateVersion7()),
            ("org_id", world.OrgId),
            ("target_membership", world.TargetMembershipId),
            ("admin_role", world.TenantAdminRoleId),
            ("actor_user", world.ActorUserId));

    /// <summary>
    /// Polls <c>pg_locks</c> until the grant backend owns an ungranted lock. The insert is already awaiting the
    /// foreign key check, so an insert that finished here never conflicted with the decision's row lock.
    /// </summary>
    private static async Task WaitUntilBlockedAsync(
        NpgsqlConnection observer,
        int grantPid,
        Task<int> insert)
    {
        var deadline = Stopwatch.StartNew();
        var token = TestContext.Current.CancellationToken;

        while (deadline.ElapsedMilliseconds < BlockedDeadlineMilliseconds)
        {
            var waiting = await ScalarAsync<long>(
                observer,
                transaction: null,
                "select count(*) from pg_locks where pid = @pid and not granted",
                token,
                ("pid", grantPid));

            if (waiting > 0)
            {
                return;
            }

            insert.IsCompleted.ShouldBeFalse(
                "the grant completed without waiting: the target row was not locked");
            await Task.Delay(PollMilliseconds, token);
        }

        throw new InvalidOperationException(
            $"the grant backend {grantPid} showed no waiting lock within "
            + $"{BlockedDeadlineMilliseconds} ms; the decision's row lock did not conflict with the grant");
    }

    private static async Task<int> RankAsync(NpgsqlConnection connection, RaceWorld world) =>
        await ScalarAsync<int>(
            connection,
            transaction: null,
            "select evidgate.membership_rank(@org_id, @target_membership)",
            TestContext.Current.CancellationToken,
            ("org_id", world.OrgId),
            ("target_membership", world.TargetMembershipId));

    private static async Task<bool> HoldsGrantAsync(
        NpgsqlConnection connection,
        RaceWorld world,
        Guid roleId) =>
        await ScalarAsync<long>(
            connection,
            transaction: null,
            "select count(*) from public.membership_role "
            + "where org_id = @org_id and membership_id = @membership_id and role_id = @role_id",
            TestContext.Current.CancellationToken,
            ("org_id", world.OrgId),
            ("membership_id", world.TargetMembershipId),
            ("role_id", roleId)) > 0;

    private async Task<RaceWorld> SeedRaceWorldAsync()
    {
        var orgId = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var actorUser = Guid.CreateVersion7();
        var targetUser = Guid.CreateVersion7();
        var actorMembership = Guid.CreateVersion7();
        var targetMembership = Guid.CreateVersion7();
        var customRole = Guid.CreateVersion7();
        var tenantAdminRole = await IdentityTestData.RoleIdAsync(Fixture, orgId, "tenant_admin", Token);

        await Fixture.SeedAsync(
            """
            insert into public."user" (id, email)
            values (@actor_user, @actor_email), (@target_user, @target_email);

            insert into public.membership (id, org_id, user_id, invited_email, status)
            values
              (@actor_membership, @org_id, @actor_user, @actor_email, 'active'),
              (@target_membership, @org_id, @target_user, @target_email, 'active');

            insert into public.role (id, org_id, code, name, is_builtin, kind)
            values (@custom_role, @org_id, @custom_code, 'Member administrator', false, 'member');

            insert into public.role_permission (id, org_id, role_id, permission_code)
            values (gen_random_uuid(), @org_id, @custom_role, 'membership:write');

            insert into public.membership_role (id, org_id, membership_id, role_id)
            values
              (gen_random_uuid(), @org_id, @actor_membership, @custom_role),
              (gen_random_uuid(), @org_id, @target_membership, @custom_role);
            """,
            [
                ("actor_user", actorUser),
                ("actor_email", $"actor-{actorUser:N}@example.test"),
                ("target_user", targetUser),
                ("target_email", $"target-{targetUser:N}@example.test"),
                ("actor_membership", actorMembership),
                ("target_membership", targetMembership),
                ("org_id", orgId),
                ("custom_role", customRole),
                ("custom_code", "race_admin_" + customRole.ToString("N")),
            ],
            Token);

        return new RaceWorld(
            orgId,
            actorUser,
            targetMembership,
            customRole,
            tenantAdminRole);
    }

    /// <summary>An application connection scoped to the request's org, as the tenant middleware does.</summary>
    private static async Task<NpgsqlConnection> OpenScopedAsync(string connectionString, Guid orgId)
    {
        var token = TestContext.Current.CancellationToken;
        var connection = new NpgsqlConnection(connectionString);

        try
        {
            await connection.OpenAsync(token);

            await using var command = connection.CreateCommand();
            command.CommandText = "select set_config('evidgate.org_id', @org, false)";
            command.Parameters.AddWithValue("org", orgId.ToString("D"));
            await command.ExecuteNonQueryAsync(token);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] values)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is null or DBNull ? default! : (T)scalar;
    }

    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] values)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record RaceWorld(
        Guid OrgId,
        Guid ActorUserId,
        Guid TargetMembershipId,
        Guid CustomRoleId,
        Guid TenantAdminRoleId);
}
