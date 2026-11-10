using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// The two guards of 0097 share one transaction advisory lock per role id, so a grant insert and a kind change of
/// the same role cannot both pass on their own snapshot: whichever side commits first, the other side's check runs
/// after the lock and sees it. Each case holds one side open, proves the other parks on that key from
/// <c>pg_locks</c> rather than waiting out a sleep, then commits and asserts the refusal the winner forces.
/// </summary>
[Trait("Category", "Docker")]
public sealed class MembershipRoleKeyConcurrencyTests : CatalogueTestBase
{
    private const string InsertGrant =
        "insert into public.membership_role (id, org_id, membership_id, role_id) values (@id, @org, @membership, @role)";

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    public MembershipRoleKeyConcurrencyTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>A kind change that commits first: the grant that was waiting behind its lock must be refused.</summary>
    [Fact]
    public async Task A_Kind_Change_That_Commits_First_Makes_The_Waiting_Grant_Fail()
    {
        var (org, membership) = await SeedMembershipAsync();
        var role = await SeedCustomRoleAsync(org, "member");
        using var bound = new CancellationTokenSource(WaitLimit);

        await using var holder = await OpenOwnerTransactionAsync();
        await ExecuteAsync(
            holder,
            "update public.role set kind = 'staff' where id = @role",
            ("role", role));

        var grant = InsertGrantOnOwnConnectionAsync(org, membership, role, bound.Token);
        await WaitForAdvisoryWaiterAsync(role, bound.Token);
        grant.IsCompleted.ShouldBeFalse("the grant parks on the role's key while the kind change is uncommitted");

        await holder.Transaction.CommitAsync(bound.Token);

        var error = (await grant.WaitAsync(WaitLimit, bound.Token)).ShouldNotBeNull("the waiting grant must not go through");
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.ShouldBe("membership_role_role_guard");
        (await OwnerScalarAsync(
            "select count(*) from public.membership_role where role_id = @role",
            ("role", role))).ShouldBe(0L, "the refused grant writes nothing");
    }

    /// <summary>A grant that commits first: the kind change that was waiting behind its lock must be refused.</summary>
    [Fact]
    public async Task A_Grant_That_Commits_First_Makes_The_Waiting_Kind_Change_Fail()
    {
        var (org, membership) = await SeedMembershipAsync();
        var role = await SeedCustomRoleAsync(org, "member");
        using var bound = new CancellationTokenSource(WaitLimit);

        await using var holder = await OpenOwnerTransactionAsync();
        await ExecuteAsync(holder, InsertGrant, Grant(org, membership, role));

        var change = ChangeKindOnOwnConnectionAsync(role, bound.Token);
        await WaitForAdvisoryWaiterAsync(role, bound.Token);
        change.IsCompleted.ShouldBeFalse("the kind change parks on the role's key while the grant is uncommitted");

        await holder.Transaction.CommitAsync(bound.Token);

        var error = (await change.WaitAsync(WaitLimit, bound.Token)).ShouldNotBeNull("the waiting kind change must not go through");
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.ShouldBe("role_granted_scope_guard");
        (await OwnerScalarAsync(
            "select count(*) from public.role where id = @role and kind = 'member'",
            ("role", role))).ShouldBe(1L, "the refused change leaves the role as it was");
        (await OwnerScalarAsync(
            "select count(*) from public.membership_role where role_id = @role",
            ("role", role))).ShouldBe(1L, "the committed grant stays");
    }

    /// <summary>The lock is keyed by role id: a grant of another member role does not wait for an open kind change.</summary>
    [Fact]
    public async Task A_Grant_Of_Another_Role_Commits_While_A_Kind_Change_Is_Open()
    {
        var (org, membership) = await SeedMembershipAsync();
        var changed = await SeedCustomRoleAsync(org, "member");
        var granted = await SeedCustomRoleAsync(org, "member");
        using var bound = new CancellationTokenSource(WaitLimit);

        await using var holder = await OpenOwnerTransactionAsync();
        await ExecuteAsync(
            holder,
            "update public.role set kind = 'staff' where id = @role",
            ("role", changed));

        var grant = InsertGrantOnOwnConnectionAsync(org, membership, granted, bound.Token);
        (await grant.WaitAsync(WaitLimit, bound.Token))
            .ShouldBeNull("another role's grant must not serialise with this role's kind change");
        await holder.Transaction.RollbackAsync(bound.Token);
        (await OwnerScalarAsync(
            "select count(*) from public.membership_role where role_id = @role",
            ("role", granted))).ShouldBe(1L);
    }

    private static (string, object)[] Grant(Guid org, Guid membership, Guid role) =>
        [("id", Guid.CreateVersion7()), ("org", org), ("membership", membership), ("role", role)];

    private Task<PostgresException?> InsertGrantOnOwnConnectionAsync(
        Guid org,
        Guid membership,
        Guid role,
        CancellationToken cancellationToken) =>
        RunOnOwnConnectionAsync(InsertGrant, Grant(org, membership, role), cancellationToken);

    private Task<PostgresException?> ChangeKindOnOwnConnectionAsync(Guid role, CancellationToken cancellationToken) =>
        RunOnOwnConnectionAsync(
            "update public.role set kind = 'staff' where id = @role",
            [("role", role)],
            cancellationToken);

    /// <summary>Runs one statement in its own owner transaction: commits it, or rolls back and returns the refusal.</summary>
    private Task<PostgresException?> RunOnOwnConnectionAsync(
        string sql,
        (string Name, object Value)[] values,
        CancellationToken cancellationToken) =>
        Task.Run(
            async () =>
            {
                await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                try
                {
                    await using var command = new NpgsqlCommand(sql, connection, transaction);
                    foreach (var (name, value) in values)
                    {
                        command.Parameters.AddWithValue(name, value);
                    }

                    await command.ExecuteNonQueryAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return (PostgresException?)null;
                }
                catch (PostgresException error)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return error;
                }
            },
            cancellationToken);

    /// <summary>Polls <c>pg_locks</c> until a backend is parked on this role's advisory key (the two-int form).</summary>
    private async Task WaitForAdvisoryWaiterAsync(Guid role, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (DateTime.UtcNow < deadline)
        {
            var waiting = await OwnerScalarAsync(
                "select count(*) from pg_locks " +
                "where locktype = 'advisory' and not granted and objsubid = 2 " +
                "and classid::bigint = (hashtext('evidgate.membership_role')::bigint & 4294967295) " +
                "and objid::bigint = (hashtext(@role::text)::bigint & 4294967295)",
                ("role", role.ToString()));

            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        throw new TimeoutException("no backend parked on the membership_role guard lock of this role");
    }

    private async Task<(Guid Org, Guid Membership)> SeedMembershipAsync()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var membership = Guid.CreateVersion7();
        await Fixture.SeedAsync(
            "insert into public.membership (id, org_id, invited_email, status) values (@id, @org, @email, 'pending')",
            [("id", membership), ("org", org), ("email", $"m-{membership:N}@example.test")],
            Token);
        return (org, membership);
    }

    private async Task<Guid> SeedCustomRoleAsync(Guid org, string kind)
    {
        var id = Guid.CreateVersion7();
        await Fixture.SeedAsync(
            "insert into public.role (id, org_id, code, name, is_builtin, kind) values (@id, @org, @code, 'Custom', false, @kind)",
            [("id", id), ("org", org), ("code", "c_" + id.ToString("N")[..12]), ("kind", kind)],
            Token);
        return id;
    }

    private async Task<OwnerTransaction> OpenOwnerTransactionAsync()
    {
        var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);

        return new OwnerTransaction(connection, await connection.BeginTransactionAsync(Token));
    }

    private static async Task ExecuteAsync(
        OwnerTransaction owner,
        string sql,
        params (string Name, object Value)[] values)
    {
        await using var command = new NpgsqlCommand(sql, owner.Connection, owner.Transaction);
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task<long> OwnerScalarAsync(string sql, params (string Name, object Value)[] values)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (long)(await command.ExecuteScalarAsync(Token))!;
    }

    private sealed class OwnerTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public NpgsqlConnection Connection { get; } = connection;

        public NpgsqlTransaction Transaction { get; } = transaction;

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
