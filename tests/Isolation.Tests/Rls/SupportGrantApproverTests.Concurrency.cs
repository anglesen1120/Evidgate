using Npgsql;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// The share lock the approver clause takes on the membership and its role grant: a removal or a role
/// revoke that overlaps an approval either waits for it or is already committed and refuses it. Both
/// orders are asserted, and the waiting side is observed through <c>pg_blocking_pids</c>, not a delay.
/// </summary>
public sealed partial class SupportGrantApproverTests
{
    [Fact]
    public async Task ApproverRule_ApprovalLockingMembership_RemovalWaitsForApprovalCommit()
    {
        var c = await CaseAsync();
        await using var approval = await ApproverSession.OpenAsync(_fixture.AppConnectionString(c.Database), c.Org);
        await using var removal = await ApproverSession.OpenAsync(_fixture.AppConnectionString(c.Database), c.Org);
        await removal.ExecAsync("set local lock_timeout = '20s'");

        (await approval.ExecAsync(ApproveSql, ("a", c.Approver), ("g", c.Grant))).ShouldBe(1);
        var blocked = removal.ExecAsync(
            "update public.membership set status = 'removed' where id = @m", ("m", c.Membership));
        await WaitBlockedByAsync(c.Database, approval.BackendPid, removal.BackendPid, blocked);

        await approval.CommitAsync();
        (await blocked).ShouldBe(1, "the removal applies once the approving transaction commits");
        await removal.CommitAsync();
        await AssertApprovedAsync(c, "the approval was already decided when the removal waited");
        (await MembershipStatusAsync(c)).ShouldBe("removed");
    }

    [Fact]
    public async Task ApproverRule_MembershipRemovedBeforeApproval_Refuses()
    {
        var c = await CaseAsync();
        await _fixture.ExecuteAsync(c.Database,
            $"update public.membership set status = 'removed' where id = '{c.Membership}'");

        await AssertRefusedAsync(c);
        await RestoreAdminAsync(c);
        await ApproveAndCommitAsync(c, false, "the same grant approves once the approver is active again");
    }

    [Fact]
    public async Task ApproverRule_ApprovalLockingMembershipRole_RoleDeleteWaitsForApprovalCommit()
    {
        var c = await CaseAsync();
        await using var approval = await ApproverSession.OpenAsync(_fixture.AppConnectionString(c.Database), c.Org);
        await using var revoke = await ApproverSession.OpenAsync(_fixture.AppConnectionString(c.Database), c.Org);
        await revoke.ExecAsync("set local lock_timeout = '20s'");

        (await approval.ExecAsync(ApproveSql, ("a", c.Approver), ("g", c.Grant))).ShouldBe(1);
        var blocked = revoke.ExecAsync(
            "delete from public.membership_role where id = @r", ("r", c.MembershipRole));
        await WaitBlockedByAsync(c.Database, approval.BackendPid, revoke.BackendPid, blocked);

        await approval.CommitAsync();
        (await blocked).ShouldBe(1, "the role revoke applies once the approving transaction commits");
        await revoke.CommitAsync();
        await AssertApprovedAsync(c, "the approval was already decided when the revoke waited");
        (await _fixture.ScalarAsync<int>(c.Database,
            $"select count(*)::int from public.membership_role where id = '{c.MembershipRole}'"))
            .ShouldBe(0);
    }

    [Fact]
    public async Task ApproverRule_MembershipRoleDeletedBeforeApproval_Refuses()
    {
        var c = await CaseAsync();
        await _fixture.ExecuteAsync(c.Database,
            $"delete from public.membership_role where id = '{c.MembershipRole}'");

        await AssertRefusedAsync(c);
        await RestoreAdminAsync(c);
        await ApproveAndCommitAsync(c, false, "the same grant approves once the tenant_admin role is held again");
    }

    private async Task<string> MembershipStatusAsync(GrantCase c) =>
        await _fixture.ScalarAsync<string>(c.Database,
            $"select status from public.membership where id = '{c.Membership}'");

    /// <summary>
    /// Waits until the server reports <paramref name="blockedPid"/> waiting on a lock held by
    /// <paramref name="holderPid"/>. A row lock's waiter parks on the holder's transaction id, which
    /// <c>pg_locks</c> does not show as a relation; <c>pg_blocking_pids</c> resolves it. Bounded by
    /// attempts, and an operation that finishes first is a failure, not a pass.
    /// </summary>
    private async Task WaitBlockedByAsync(string database, int holderPid, int blockedPid, Task operation)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (operation.IsCompleted)
            {
                throw new InvalidOperationException(operation.IsFaulted
                    ? $"the blocked statement failed instead of waiting: {operation.Exception?.GetBaseException().Message}"
                    : "the blocked statement completed instead of waiting on the approval's share lock");
            }

            if (await _fixture.ScalarAsync<bool>(database,
                    $"select {holderPid} = any (pg_blocking_pids({blockedPid}))"))
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException($"no backend was observed waiting on a lock held by backend {holderPid}");
    }

    /// <summary>One app-role transaction with the tenant set, its own backend pid, and explicit commit.</summary>
    private sealed class ApproverSession : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly NpgsqlTransaction _transaction;

        private ApproverSession(NpgsqlConnection connection, NpgsqlTransaction transaction, int backendPid)
        {
            _connection = connection;
            _transaction = transaction;
            BackendPid = backendPid;
        }

        public int BackendPid { get; }

        public static async Task<ApproverSession> OpenAsync(string connectionString, Guid org)
        {
            var token = TestContext.Current.CancellationToken;
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(token);
            var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(token);
            await using var pidCommand = connection.CreateCommand();
            pidCommand.Transaction = transaction;
            pidCommand.CommandText = "select pg_backend_pid()";
            var pid = (int)(await pidCommand.ExecuteScalarAsync(token))!;
            var session = new ApproverSession(connection, transaction, pid);
            await session.SetAsync("evidgate.org_id", org.ToString("D"));
            return session;
        }

        public async Task<int> ExecAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = Command(sql, parameters);
            return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        public Task CommitAsync() => _transaction.CommitAsync(TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private async Task SetAsync(string key, string value)
        {
            await using var command = Command("select set_config(@key, @value, true)", [("key", key), ("value", value)]);
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        private NpgsqlCommand Command(string sql, (string Name, object Value)[] parameters)
        {
            var command = _connection.CreateCommand();
            command.Transaction = _transaction;
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            return command;
        }
    }
}
