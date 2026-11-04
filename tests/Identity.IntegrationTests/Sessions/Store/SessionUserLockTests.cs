using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Store;

/// <summary>
/// Session writers of one user are serialised by a per-user advisory lock (0065). Each case holds the lock
/// from an open owner transaction, proves the competing writer is parked ON that lock (a waiting advisory
/// entry in <c>pg_locks</c>, not a timer), then commits and asserts the order the lock forces: no live session
/// survives a revoke-all that committed after the other writer started.
/// </summary>
/// <remarks>
/// Without the lock the revoke-all of the race cases takes its snapshot before the other transaction commits
/// and misses the row that transaction inserted, so a live successor remains: the final count assertions
/// fail on the 0045 bodies, and the wait assertion fails because nothing parks.
/// </remarks>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class SessionUserLockTests : SessionHardeningTestBase
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

    public SessionUserLockTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>A rotation in flight when revoke-all starts: the revoke-all waits, then revokes the successor too.</summary>
    [Fact]
    public async Task A_Revoke_All_Started_During_A_Rotation_Also_Revokes_The_Successor()
    {
        var userId = await SeedUserAsync("lock-rotate-first@example.com");
        var clock = ClockAt();
        var predecessor = await CreateSessionAsync(userId, clock);

        await using var rotating = await OpenOwnerTransactionAsync();
        await ExecuteAsync(
            rotating,
            "select status from evidgate_resolve.session_rotate(@old, @new, @hmac, @version, @now, 1800, null, null)",
            ("old", predecessor.SessionId),
            ("new", Guid.NewGuid()),
            ("hmac", HmacOf(Pepper, "lock-rotate-" + Guid.NewGuid())),
            ("version", CurrentVersion),
            ("now", clock.GetUtcNow()));

        var revokeAll = RevokeAllOnOwnConnectionAsync(userId, clock.GetUtcNow());
        await WaitForAdvisoryWaiterAsync(userId);
        revokeAll.IsCompleted.ShouldBeFalse("revoke-all parks on the user's lock while a rotation is uncommitted");

        await rotating.Transaction.CommitAsync(Token);

        (await revokeAll).ShouldBe(1, "the successor the rotation committed is visible to the revoke-all statement");
        (await LiveCountAsync(userId)).ShouldBe(0L, "no live session survives the removal");
    }

    /// <summary>A login in flight when revoke-all starts: the revoke-all waits, then revokes the new session too.</summary>
    [Fact]
    public async Task A_Revoke_All_Started_During_A_Login_Also_Revokes_The_New_Session()
    {
        var userId = await SeedUserAsync("lock-login-first@example.com");
        var clock = ClockAt();
        await CreateSessionAsync(userId, clock);

        await using var login = await OpenOwnerTransactionAsync();
        await ExecuteAsync(
            login,
            "select status from evidgate_resolve.session_create(@id, @user, @hmac, @version, null, 'xunit', @exp, @abs, null, @now)",
            ("id", Guid.NewGuid()),
            ("user", userId),
            ("hmac", HmacOf(Pepper, "lock-login-" + Guid.NewGuid())),
            ("version", CurrentVersion),
            ("exp", clock.GetUtcNow().AddHours(1)),
            ("abs", clock.GetUtcNow().AddDays(1)),
            ("now", clock.GetUtcNow()));

        var revokeAll = RevokeAllOnOwnConnectionAsync(userId, clock.GetUtcNow());
        await WaitForAdvisoryWaiterAsync(userId);
        revokeAll.IsCompleted.ShouldBeFalse("revoke-all parks on the user's lock while a login is uncommitted");

        await login.Transaction.CommitAsync(Token);

        (await revokeAll).ShouldBe(2, "the pre-existing session and the one the login committed");
        (await LiveCountAsync(userId)).ShouldBe(0L, "no live session survives the removal");
    }

    /// <summary>Revoke-all first: a rotation parks on the lock, then finds its predecessor revoked and mints nothing.</summary>
    [Fact]
    public async Task A_Rotation_Started_During_A_Revoke_All_Mints_No_Successor()
    {
        var userId = await SeedUserAsync("lock-revoke-first@example.com");
        var clock = ClockAt();
        var predecessor = await CreateSessionAsync(userId, clock);

        await using var revoking = await OpenOwnerTransactionAsync();
        await ExecuteAsync(
            revoking,
            "select revoked_count from evidgate_resolve.session_revoke_all_for_user(@user, @now)",
            ("user", userId),
            ("now", clock.GetUtcNow()));

        var rotation = CreateStore(clock)
            .RotateAsync(new SessionRotation(predecessor.SessionId, null, null), Token)
            .AsTask();
        await WaitForAdvisoryWaiterAsync(userId);
        rotation.IsCompleted.ShouldBeFalse("the rotation waits for the revoke-all's lock");

        await revoking.Transaction.CommitAsync(Token);

        var result = await rotation;
        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.SessionRevoked);
        (await LiveCountAsync(userId)).ShouldBe(0L);
    }

    /// <summary>The lock is per user: a held revoke-all of A blocks neither a revoke-all nor a login of B.</summary>
    [Fact]
    public async Task A_Revoke_All_Of_One_User_Does_Not_Block_Another_User()
    {
        var userA = await SeedUserAsync("lock-user-a@example.com");
        var userB = await SeedUserAsync("lock-user-b@example.com");
        var clock = ClockAt();
        await CreateSessionAsync(userA, clock);
        await CreateSessionAsync(userB, clock);

        await using var holder = await OpenOwnerTransactionAsync();
        await ExecuteAsync(
            holder,
            "select revoked_count from evidgate_resolve.session_revoke_all_for_user(@user, @now)",
            ("user", userA),
            ("now", clock.GetUtcNow()));

        var other = Task.Run(
            async () =>
            {
                var revoked = await RevokeAllOnOwnConnectionAsync(userB, clock.GetUtcNow());
                await CreateSessionAsync(userB, clock);
                return revoked;
            },
            Token);

        (await other.WaitAsync(WaitLimit, Token)).ShouldBe(1, "user B is served while user A's transaction is still open");

        await holder.Transaction.RollbackAsync(Token);
    }

    /// <summary>The three writers call the one helper, keep their grants, and the helper is not callable by the app role.</summary>
    [Fact]
    public async Task The_Definers_Keep_Their_Contract_And_Share_The_Helper()
    {
        foreach (var function in new[] { "session_create", "session_rotate", "session_revoke_all_for_user" })
        {
            (await OwnerScalarAsync<bool>(
                "select p.prosecdef and array_to_string(p.proconfig, ',') = 'search_path=\"\"' and position('session_user_lock' in p.prosrc) > 0 " +
                "from pg_proc p join pg_namespace n on n.oid = p.pronamespace " +
                "where n.nspname = 'evidgate_resolve' and p.proname = @name",
                ("name", function))).ShouldBeTrue($"{function} is a definer with an empty search_path that takes the user lock");

            (await OwnerScalarAsync<bool>(
                "select has_function_privilege('evidgate_app', p.oid, 'execute') and not has_function_privilege('public', p.oid, 'execute') " +
                "from pg_proc p join pg_namespace n on n.oid = p.pronamespace " +
                "where n.nspname = 'evidgate_resolve' and p.proname = @name",
                ("name", function))).ShouldBeTrue($"{function} stays executable by the app role alone");
        }

        (await OwnerScalarAsync<bool>(
            "select not has_function_privilege('evidgate_app', 'evidgate.session_user_lock(uuid)', 'execute') " +
            "and not has_function_privilege('public', 'evidgate.session_user_lock(uuid)', 'execute')"))
            .ShouldBeTrue("the lock helper is only reachable from the owner-run definers");
    }

    private async Task<OwnerTransaction> OpenOwnerTransactionAsync()
    {
        var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);

        return new OwnerTransaction(connection, await connection.BeginTransactionAsync(Token));
    }

    private static async Task ExecuteAsync(OwnerTransaction owner, string sql, params (string Name, object Value)[] values)
    {
        await using var command = new NpgsqlCommand(sql, owner.Connection, owner.Transaction);
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteScalarAsync(Token);
    }

    private Task<int> RevokeAllOnOwnConnectionAsync(Guid userId, DateTimeOffset now) =>
        Task.Run(
            async () =>
            {
                await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
                await connection.OpenAsync(Token);
                await using var command = new NpgsqlCommand(
                    "select revoked_count from evidgate_resolve.session_revoke_all_for_user(@user, @now)",
                    connection);
                command.Parameters.AddWithValue("user", userId);
                command.Parameters.AddWithValue("now", now);

                return (int)(await command.ExecuteScalarAsync(Token))!;
            },
            Token);

    /// <summary>Polls <c>pg_locks</c> until a backend is parked on this user's advisory key (the 0065 two-int form).</summary>
    private async Task WaitForAdvisoryWaiterAsync(Guid userId)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (DateTime.UtcNow < deadline)
        {
            var waiting = await OwnerScalarAsync<long>(
                "select count(*) from pg_locks " +
                "where locktype = 'advisory' and not granted " +
                "and classid::bigint = (hashtext('evidgate.session_user')::bigint & 4294967295) " +
                "and objid::bigint = (hashtext(@user::text)::bigint & 4294967295)",
                ("user", userId.ToString()));

            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), Token);
        }

        throw new TimeoutException("no backend parked on the per-user session lock");
    }

    private Task<long> LiveCountAsync(Guid userId) =>
        OwnerScalarAsync<long>(
            "select count(*) from public.session where user_id = @user and revoked_at is null",
            ("user", userId));

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
