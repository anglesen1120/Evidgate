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

    [Theory]
    [InlineData("login", "login", "ok")]
    [InlineData("login", "rotate", "rotated")]
    [InlineData("login", "revoke", "2")]
    [InlineData("rotate", "login", "ok")]
    [InlineData("rotate", "rotate", "not_live")]
    [InlineData("rotate", "revoke", "1")]
    [InlineData("revoke", "login", "ok")]
    [InlineData("revoke", "rotate", "not_live")]
    [InlineData("revoke", "revoke", "0")]
    public async Task Writers_Same_User_Serialise_Without_Deadlock(string first, string second, string expected)
    {
        var user = await SeedUserAsync(Unique.Email("writer-order"));
        var now = ClockAt().GetUtcNow();
        var predecessor = await CreateSessionAsync(user, ClockAt());
        await using var holder = await OpenOwnerTransactionAsync();
        await RunWriterAsync(holder, first, user, predecessor.SessionId, now);
        await using var waiter = await OpenOwnerTransactionAsync();
        var pending = RunWriterAsync(waiter, second, user, predecessor.SessionId, now);
        await WaitForAdvisoryWaiterAsync(user);
        pending.IsCompleted.ShouldBeFalse();
        await holder.Transaction.CommitAsync(Token);
        (await pending.WaitAsync(WaitLimit, Token)).ShouldBe(expected);
        await waiter.Transaction.CommitAsync(Token);
        var live = second == "revoke" || (first == "revoke" && second == "rotate") ? 0L
            : first == "login" && second == "login" ? 3L
            : first == "login" || second == "login" && first == "rotate" ? 2L : 1L;
        (await LiveCountAsync(user)).ShouldBe(live);
    }

    [Theory]
    [InlineData("login")]
    [InlineData("rotate")]
    [InlineData("revoke")]
    public async Task Writers_Waiting_For_User_Lock_Hold_No_Session_Row_Lock(string operation)
    {
        var user = await SeedUserAsync(Unique.Email("lock-order"));
        var predecessor = await CreateSessionAsync(user, ClockAt());
        await using var holder = await OpenOwnerTransactionAsync();
        await ExecuteAsync(holder, "select evidgate.session_user_lock(@user)", ("user", user));
        await using var waiter = await OpenOwnerTransactionAsync();
        var pending = RunWriterAsync(waiter, operation, user, predecessor.SessionId, ClockAt().GetUtcNow());
        await WaitForAdvisoryWaiterAsync(user);
        (await OwnerScalarAsync<long>(
            "select count(*) from pg_locks where pid = @pid and locktype = 'relation' and granted " +
            "and relation = 'public.session'::regclass and mode = 'RowExclusiveLock'",
            ("pid", waiter.Connection.ProcessID))).ShouldBe(0L, "no session mutation begins before the user lock");
        await using (var probe = await OpenOwnerTransactionAsync())
        {
            await ExecuteAsync(probe, "select id from public.session where id = @id for update nowait",
                ("id", predecessor.SessionId));
            await probe.Transaction.RollbackAsync(Token);
        }

        await holder.Transaction.CommitAsync(Token);
        (await pending.WaitAsync(WaitLimit, Token)).ShouldBe(operation switch
        {
            "login" => "ok",
            "rotate" => "rotated",
            _ => "1",
        });
        await waiter.Transaction.CommitAsync(Token);
    }

    [Theory]
    [InlineData("login", "ok")]
    [InlineData("rotate", "rotated")]
    [InlineData("revoke", "1")]
    public async Task Writers_Other_User_Complete_While_First_User_Lock_Is_Held(string operation, string expected)
    {
        var first = await SeedUserAsync(Unique.Email("held-user"));
        var other = await SeedUserAsync(Unique.Email("free-user"));
        (await OwnerScalarAsync<bool>("select hashtext(@a::text) <> hashtext(@b::text)",
            ("a", first), ("b", other))).ShouldBeTrue("this case tests non-colliding user keys");
        var predecessor = await CreateSessionAsync(other, ClockAt());
        await using var holder = await OpenOwnerTransactionAsync();
        await ExecuteAsync(holder, "select evidgate.session_user_lock(@user)", ("user", first));
        await using var writer = await OpenOwnerTransactionAsync();
        var result = await RunWriterAsync(writer, operation, other, predecessor.SessionId, ClockAt().GetUtcNow())
            .WaitAsync(WaitLimit, Token);
        result.ShouldBe(expected);
        await writer.Transaction.CommitAsync(Token);
        (await LiveCountAsync(other)).ShouldBe(operation == "login" ? 2L : operation == "rotate" ? 1L : 0L);
    }

    [Fact]
    public async Task User_Lock_Does_Not_Collide_With_Case_Prefix_Or_Identical_Bigint_Key()
    {
        var user = await SeedUserAsync(Unique.Email("namespace"));
        (await OwnerScalarAsync<bool>(
            "select hashtext('evidgate.session_user') <> hashtext('evidgate.case_prefix')")).ShouldBeTrue();
        await using var holder = await OpenOwnerTransactionAsync();
        await ExecuteAsync(holder,
            "select pg_advisory_xact_lock(hashtext('evidgate.case_prefix'), hashtext(@user::text))", ("user", user));
        await ExecuteAsync(holder,
            "select pg_advisory_xact_lock((hashtext('evidgate.session_user')::bigint << 32) | " +
            "(hashtext(@user::text)::bigint & 4294967295))", ("user", user));
        await using var writer = await OpenOwnerTransactionAsync();
        await ExecuteAsync(writer, "select evidgate.session_user_lock(@user)", ("user", user))
            .WaitAsync(WaitLimit, Token);
        (await OwnerScalarAsync<long>(
            "select count(*) from pg_locks where locktype = 'advisory' and granted and pid = @pid " +
            "and objsubid = 2 and classid::bigint = (hashtext('evidgate.session_user')::bigint & 4294967295) " +
            "and objid::bigint = (hashtext(@user::text)::bigint & 4294967295)",
            ("pid", writer.Connection.ProcessID), ("user", user))).ShouldBe(1L);
    }

    [Fact]
    public async Task Definer_Catalog_Contracts_Equal_Original_Migration_Except_Revocation_Language()
    {
        var names = new[] { "session_create", "session_rotate", "session_revoke_all_for_user" };
        var baseline = await File.ReadAllTextAsync(
            Path.Combine(FixtureLoader.RepoRoot, "migrations", "0045_identity_session_hardening.sql"), Token);
        await using var owner = await OpenOwnerTransactionAsync();
        var current = await ReadDefinerContractsAsync(owner);
        (await OwnerScalarAsync<long>(
            "select count(*) from pg_proc p join pg_namespace n on n.oid = p.pronamespace " +
            "join pg_language l on l.oid = p.prolang where n.nspname = 'evidgate_resolve' " +
            "and p.proname in ('session_create','session_rotate','session_revoke_all_for_user') " +
            "and l.lanname = 'plpgsql'")).ShouldBe(3L);
        foreach (var name in names)
        {
            await using var signature = new NpgsqlCommand(
                "select p.oid::regprocedure::text from pg_proc p join pg_namespace n on n.oid = p.pronamespace " +
                "where n.nspname = 'evidgate_resolve' and p.proname = @name", owner.Connection, owner.Transaction);
            signature.Parameters.AddWithValue("name", name);
            var identity = (string)(await signature.ExecuteScalarAsync(Token))!;
            await ExecuteAsync(owner, "drop function " + identity);
            var start = baseline.IndexOf("create or replace function evidgate_resolve." + name + "(", StringComparison.Ordinal);
            start.ShouldBeGreaterThanOrEqualTo(0);
            var end = baseline.IndexOf("$$;", start, StringComparison.Ordinal);
            end.ShouldBeGreaterThan(start);
            await ExecuteAsync(owner, baseline[start..(end + 3)]);
            await ExecuteAsync(owner, "revoke all on function " + identity + " from public");
            await ExecuteAsync(owner, "grant execute on function " + identity + " to evidgate_app");
        }

        (await ReadDefinerContractsAsync(owner)).ShouldBe(current,
            "argument names/types/defaults, result, security, owner, volatility and effective grants must not drift");
        await using var language = new NpgsqlCommand(
            "select l.lanname from pg_proc p join pg_namespace n on n.oid = p.pronamespace " +
            "join pg_language l on l.oid = p.prolang where n.nspname = 'evidgate_resolve' " +
            "and p.proname = 'session_revoke_all_for_user'", owner.Connection, owner.Transaction);
        ((string)(await language.ExecuteScalarAsync(Token))!).ShouldBe("sql");
        await owner.Transaction.RollbackAsync(Token);
    }

    [Fact]
    public async Task Rotation_Unknown_Predecessor_Returns_NotLive_And_Takes_No_User_Lock()
    {
        await using var owner = await OpenOwnerTransactionAsync();
        (await RunWriterAsync(owner, "rotate", Guid.Empty, Guid.NewGuid(), ClockAt().GetUtcNow())).ShouldBe("not_live");
        (await OwnerScalarAsync<long>(
            "select count(*) from pg_locks where pid = @pid and locktype = 'advisory'",
            ("pid", owner.Connection.ProcessID))).ShouldBe(0L);
    }

    [Theory]
    [InlineData("rotate", "not_live", 1L)]
    [InlineData("revoke", "1", 0L)]
    public async Task Login_With_Predecessor_Serialises_With_Rotation_And_RevokeAll(
        string next, string expected, long live)
    {
        var user = await SeedUserAsync(Unique.Email("forwarded-login"));
        var now = ClockAt().GetUtcNow();
        var predecessor = await CreateSessionAsync(user, ClockAt());
        await using var login = await OpenOwnerTransactionAsync();
        await ExecuteAsync(login,
            "select status from evidgate_resolve.session_create(@new, @user, @hmac, @version, null, 'xunit', " +
            "@now + interval '1 hour', @now + interval '1 day', @hashes, @now)",
            ("new", Guid.NewGuid()), ("user", user), ("hmac", HmacOf(Pepper, "forwarded-" + Guid.NewGuid())),
            ("version", CurrentVersion), ("now", now), ("hashes", new[] { HmacOf(Pepper, predecessor.CookieValue) }));
        await using var waiter = await OpenOwnerTransactionAsync();
        var pending = RunWriterAsync(waiter, next, user, predecessor.SessionId, now);
        await WaitForAdvisoryWaiterAsync(user);
        await login.Transaction.CommitAsync(Token);
        (await pending.WaitAsync(WaitLimit, Token)).ShouldBe(expected);
        await waiter.Transaction.CommitAsync(Token);
        (await LiveCountAsync(user)).ShouldBe(live);
        (await OwnerScalarAsync<long>("select count(*) from public.session where rotated_from = @old",
            ("old", predecessor.SessionId))).ShouldBe(1L);
    }

    private static async Task<string> RunWriterAsync(
        OwnerTransaction owner, string operation, Guid user, Guid predecessor, DateTimeOffset now)
    {
        var sql = operation switch
        {
            "login" => "select status from evidgate_resolve.session_create(@new, @user, @hmac, @version, " +
                "null, 'xunit', @now + interval '1 hour', @now + interval '1 day', null, @now)",
            "rotate" => "select status from evidgate_resolve.session_rotate(@old, @new, @hmac, @version, @now, 1800, null, null)",
            "revoke" => "select revoked_count::text from evidgate_resolve.session_revoke_all_for_user(@user, @now)",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        await using var command = new NpgsqlCommand(sql, owner.Connection, owner.Transaction);
        command.Parameters.AddWithValue("user", user);
        command.Parameters.AddWithValue("old", predecessor);
        command.Parameters.AddWithValue("new", Guid.NewGuid());
        command.Parameters.AddWithValue("hmac", HmacOf(Pepper, "writer-" + Guid.NewGuid()));
        command.Parameters.AddWithValue("version", CurrentVersion);
        command.Parameters.AddWithValue("now", now);
        return (string)(await command.ExecuteScalarAsync(Token))!;
    }

    private static async Task<string> ReadDefinerContractsAsync(OwnerTransaction owner)
    {
        await using var command = new NpgsqlCommand(
            """
            select jsonb_agg(jsonb_build_object(
                'identity', p.oid::regprocedure::text, 'arguments', pg_get_function_arguments(p.oid),
                'result', pg_get_function_result(p.oid), 'arg_names', p.proargnames, 'arg_modes', p.proargmodes,
                'all_arg_types', p.proallargtypes, 'kind', p.prokind, 'definer', p.prosecdef,
                'strict', p.proisstrict, 'set', p.proretset, 'volatility', p.provolatile,
                'parallel', p.proparallel, 'config', p.proconfig, 'owner', p.proowner::regrole::text,
                'grants', (select jsonb_agg(jsonb_build_array(a.grantor, a.grantee, a.privilege_type, a.is_grantable)
                    order by a.grantor, a.grantee, a.privilege_type, a.is_grantable)
                    from aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a)) order by p.proname)::text
              from pg_proc p join pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate_resolve'
               and p.proname in ('session_create', 'session_rotate', 'session_revoke_all_for_user')
            """, owner.Connection, owner.Transaction);
        return (string)(await command.ExecuteScalarAsync(Token))!;
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
