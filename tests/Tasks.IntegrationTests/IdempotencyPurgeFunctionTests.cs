using Npgsql;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// 01.5.1-T6.1/T6.2 hardening of the owner sweep that expires
/// <c>idempotency_record</c> (TL T80): the function deletes only expired rows, in
/// bounded batches, and only <c>evidgate_worker_sched</c> may call it.
/// <para>
/// T80 also asks for an audit row per call. <c>audit_log</c> is 02.7.1's table and
/// arrives in M2, so this wave asserts what exists — <c>proconfig</c>,
/// <c>prosecdef</c>, the owner, and the EXECUTE grants — and records the audit row
/// as a deviation.
/// </para>
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class IdempotencyPurgeFunctionTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-7111-8111-111111111111");

    private const string PurgeFunction = "evidgate.purge_expired_idempotency(integer)";
    private const string PurgeCall = "select evidgate.purge_expired_idempotency(@Limit)";

    private const string InsertRecordSql = """
        insert into public.idempotency_record
          (id, org_id, operation, key, request_hash, created_at, expires_at)
        values
          (@Id, @OrgId, 'runs.create', @Key, 'sha256:seed', @CreatedAt, @ExpiresAt)
        """;

    private readonly PostgresTasksFixture _fixture;

    public IdempotencyPurgeFunctionTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// AC6, "PurgeTask_DeletesOnlyExpired": a sweep removes the records whose
    /// retention has run out and leaves every live one — a purge that removed a live
    /// record would break the replay guarantee it exists to enforce.
    /// </summary>
    [Fact]
    public async Task PurgeFn_DeletesOnlyExpired()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var owner = await OutboxIdempotencyTestHost.OpenAsync(
            _fixture.OwnerConnectionString(database),
            Token);

        var now = DateTimeOffset.UtcNow;
        var expired = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        var live = Guid.CreateVersion7();

        foreach (var id in expired)
        {
            await InsertAsync(owner, id, "expired:" + id, now.AddHours(-48), now.AddHours(-24));
        }

        await InsertAsync(owner, live, "live:" + live, now, now.AddHours(24));

        await using var sweep = await OutboxIdempotencyTestHost.OpenAsync(
            _fixture.WorkerSchedConnectionString(database),
            Token);

        var first = await OutboxIdempotencyTestHost.ScalarAsync<int>(sweep, PurgeCall, Token, ("Limit", 100));
        first.ShouldBe(expired.Length);

        // Nothing expired is left, so a second sweep is a no-op — it does not start
        // eating live records.
        var second = await OutboxIdempotencyTestHost.ScalarAsync<int>(sweep, PurgeCall, Token, ("Limit", 100));
        second.ShouldBe(0);

        var remaining = await OutboxIdempotencyTestHost.ScalarAsync<long>(
            owner,
            "select count(*) from public.idempotency_record",
            Token);
        remaining.ShouldBe(1);

        var survivor = await OutboxIdempotencyTestHost.ScalarAsync<Guid>(
            owner,
            "select id from public.idempotency_record",
            Token);
        survivor.ShouldBe(live);
    }

    /// <summary>
    /// The batches are bounded at the function, not only by convention at the
    /// caller: a schedule task loops until a short batch comes back, so a large
    /// backlog is deleted in short transactions and a caller cannot turn the sweep
    /// into one unbounded delete.
    /// </summary>
    [Fact]
    public async Task PurgeFn_BatchesAreBounded()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var owner = await OutboxIdempotencyTestHost.OpenAsync(
            _fixture.OwnerConnectionString(database),
            Token);

        var now = DateTimeOffset.UtcNow;
        var expired = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
        var live = Guid.CreateVersion7();

        foreach (var id in expired)
        {
            await InsertAsync(owner, id, "expired:" + id, now.AddHours(-48), now.AddHours(-24));
        }

        await InsertAsync(owner, live, "live:" + live, now, now.AddHours(24));

        await using var sweep = await OutboxIdempotencyTestHost.OpenAsync(
            _fixture.WorkerSchedConnectionString(database),
            Token);

        (await OutboxIdempotencyTestHost.ScalarAsync<int>(sweep, PurgeCall, Token, ("Limit", 2))).ShouldBe(2);
        (await CountAsync(owner)).ShouldBe(2);
        (await OutboxIdempotencyTestHost.ScalarAsync<int>(sweep, PurgeCall, Token, ("Limit", 2))).ShouldBe(1);
        (await CountAsync(owner)).ShouldBe(1);

        // A non-positive batch asks for nothing and deletes nothing.
        (await OutboxIdempotencyTestHost.ScalarAsync<int>(sweep, PurgeCall, Token, ("Limit", 0))).ShouldBe(0);
        (await CountAsync(owner)).ShouldBe(1);
    }

    /// <summary>
    /// "PurgeFn_SearchPath_Empty_And_Execute_Scoped" (T80): the sweep is an owner
    /// <c>SECURITY DEFINER</c> function with an empty <c>search_path</c> so no
    /// caller-controlled schema can be resolved from inside it, it owns the deletion
    /// instead of handing out table privileges, and its EXECUTE grant is scoped to
    /// the dedicated sweep role — the app role is refused by the database itself.
    /// </summary>
    [Fact]
    public async Task PurgeFn_SearchPath_Empty_And_Execute_Scoped()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var superuser = await _fixture.OpenSuperuserAsync(database);

        var definition = await OutboxIdempotencyTestHost.ScalarAsync<string>(
            superuser,
            """
            select p.prosecdef::text || '|' || pg_catalog.pg_get_userbyid(p.proowner) || '|'
                   || coalesce(array_to_string(p.proconfig, ','), '')
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate' and p.proname = 'purge_expired_idempotency'
            """,
            Token);

        // PostgreSQL renders the empty search_path this way in proconfig (the
        // canonical form of `set search_path = ''`): the sweep resolves no schema
        // of its own, only what it names explicitly.
        definition.ShouldBe("true|evidgate_owner|search_path=\"\"");

        var appAllowed = await OutboxIdempotencyTestHost.ScalarAsync<bool>(
            superuser,
            $"select pg_catalog.has_function_privilege('evidgate_app', '{PurgeFunction}', 'execute')",
            Token);
        appAllowed.ShouldBeFalse();

        var sweepAllowed = await OutboxIdempotencyTestHost.ScalarAsync<bool>(
            superuser,
            $"select pg_catalog.has_function_privilege('evidgate_worker_sched', '{PurgeFunction}', 'execute')",
            Token);
        sweepAllowed.ShouldBeTrue();

        await using var app = await OutboxIdempotencyTestHost.OpenAsync(
            _fixture.AppConnectionString(database),
            Token);

        var denied = await Should.ThrowAsync<PostgresException>(
            () => OutboxIdempotencyTestHost.ScalarAsync<int>(app, PurgeCall, Token, ("Limit", 1)));
        denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        await using var sweep = await OutboxIdempotencyTestHost.OpenAsync(
            _fixture.WorkerSchedConnectionString(database),
            Token);

        var purged = await OutboxIdempotencyTestHost.ScalarAsync<int>(sweep, PurgeCall, Token, ("Limit", 100));
        purged.ShouldBe(0);
    }

    private static Task<long> CountAsync(NpgsqlConnection connection) => OutboxIdempotencyTestHost.ScalarAsync<long>(
        connection,
        "select count(*) from public.idempotency_record",
        Token);

    private static Task<int> InsertAsync(
        NpgsqlConnection connection,
        Guid id,
        string key,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt) => OutboxIdempotencyTestHost.ExecuteAsync(
        connection,
        InsertRecordSql,
        Token,
        ("Id", id),
        ("OrgId", OrgA),
        ("Key", key),
        ("CreatedAt", createdAt),
        ("ExpiresAt", expiresAt));
}
