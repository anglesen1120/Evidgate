using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Xunit;

namespace Persistence.IntegrationTests;

/// <summary>
/// 01.4.2-T1 (AC1–AC3, AC6) boundaries the happy-path suite does not reach: a scope
/// that fails to open, a scope that is used from two async flows at once, pool
/// hygiene after a rolled-back or aborted transaction, the commit/dispose
/// lifecycle, the <c>version</c> concurrency token and the interceptor's coverage
/// of EF's batched path. Independent QA of the same task, real PostgreSQL as the
/// app role.
/// </summary>
[Collection(PersistenceSuite.Name)]
[Trait("Category", "Docker")]
public sealed class TenantScopeBoundaryTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid OrgB = Guid.Parse("22222222-2222-7222-8222-222222222222");

    private readonly PostgresPersistenceFixture _fixture;

    public TenantScopeBoundaryTests(PostgresPersistenceFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// T1.1/T1.2: a scope that fails to open never becomes the ambient scope, and it
    /// gives the pooled connection back — with a pool of one, a leaked connection
    /// would make every later <c>BeginAsync</c> time out.
    /// </summary>
    [Fact]
    public async Task Open_Failure_Leaves_No_Ambient_Scope_And_Releases_The_Connection()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = TenantTestHost.Start(_fixture, database, maximumPoolSize: 1);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => host.Scopes.BeginAsync(OrgA, cancellationToken: cancelled.Token));
        host.Scopes.Current.ShouldBeNull();

        await using (var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token))
        {
            (await host.Db.ScalarAsync<int>("select 1", cancellationToken: Token)).ShouldBe(1);
        }

        // The same for a failure raised by the server before any transaction exists:
        // the data source authenticates as the app role with a wrong password.
        var badPassword = new NpgsqlConnectionStringBuilder(_fixture.AppConnectionString(database))
        {
            MaxPoolSize = 1,
            Password = "definitely-not-the-password",
        };

        await using var badSource = NpgsqlDataSource.Create(badPassword.ConnectionString);
        var badFactory = new NpgsqlTenantScopeFactory(badSource);

        var error = await Should.ThrowAsync<PostgresException>(
            () => badFactory.BeginAsync(OrgA, cancellationToken: Token));
        error.SqlState.ShouldBe(PostgresErrorCodes.InvalidPassword);
        badFactory.Current.ShouldBeNull();
        Should.Throw<TenantScopeMissingException>(() => badFactory.Require());

        // Closing a scope after a failed open must stay idempotent and harmless.
        await using var recovered = await host.Scopes.BeginAsync(OrgB, cancellationToken: Token);
        recovered.IsActive.ShouldBeTrue();
    }

    /// <summary>
    /// T1.2: two flows that overlap in time each get their own scope and their own
    /// tenant — the ambient slot is per async flow, not per factory, and the second
    /// <c>BeginAsync</c> must not be mistaken for a nested one.
    /// </summary>
    [Fact]
    public async Task Parallel_Flows_Keep_Their_Own_Tenant()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var rowA = Guid.CreateVersion7();
        var rowB = Guid.CreateVersion7();
        await _fixture.SeedProbeAsync(database, (OrgA, rowA, "a"), (OrgB, rowB, "b"));
        await using var host = TenantTestHost.Start(_fixture, database, maximumPoolSize: 2);

        var arrivals = 0;
        var bothOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var flows = await Task.WhenAll(
            Task.Run(() => ScopedRowAsync(host, OrgA, rowA)),
            Task.Run(() => ScopedRowAsync(host, OrgB, rowB)));

        flows.ShouldBe([rowA, rowB]);
        arrivals.ShouldBe(2);

        async Task<Guid> ScopedRowAsync(TenantTestHost scopedHost, Guid orgId, Guid expected)
        {
            await using var scope = await scopedHost.Scopes.BeginAsync(orgId, cancellationToken: Token);

            // Both scopes are open at the same instant before either reads.
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                bothOpen.SetResult();
            }

            await bothOpen.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

            scope.OrgId.ShouldBe(orgId);
            var row = await scopedHost.Db.ScalarAsync<Guid>("select id from public.rls_probe", cancellationToken: Token);
            row.ShouldBe(expected);
            return row;
        }
    }

    /// <summary>
    /// T1.1: the read-only mode is a transaction characteristic, so it must not
    /// survive into the next borrow of the same pooled connection — otherwise every
    /// later write on that backend would fail with 25006.
    /// </summary>
    [Fact]
    public async Task Read_Only_Scope_Leaves_The_Pooled_Connection_Writable()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = TenantTestHost.Start(_fixture, database, maximumPoolSize: 1);

        int backend;
        await using (var scope = await host.Scopes.BeginAsync(OrgA, readOnlyTransaction: true, cancellationToken: Token))
        {
            backend = await host.Db.ScalarAsync<int>("select pg_backend_pid()", cancellationToken: Token);
            (await host.Db.ScalarAsync<bool>(
                "select current_setting('transaction_read_only') = 'on'",
                cancellationToken: Token)).ShouldBeTrue();
        }

        var row = Guid.CreateVersion7();
        await using (var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token))
        {
            (await host.Db.ScalarAsync<int>("select pg_backend_pid()", cancellationToken: Token)).ShouldBe(backend);
            (await host.Db.ScalarAsync<bool>(
                "select current_setting('transaction_read_only') = 'off'",
                cancellationToken: Token)).ShouldBeTrue();
            (await host.Db.ExecuteAsync(
                "insert into public.rls_probe (org_id, id, label) values (@org, @id, 'after-read-only')",
                new { org = OrgA, id = row },
                Token)).ShouldBe(1);

            await scope.CommitAsync(Token);
        }

        await using (var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token))
        {
            (await host.Db.ScalarAsync<int>(
                "select count(*)::int from public.rls_probe where id = @id",
                new { id = row },
                Token)).ShouldBe(1);
        }
    }

    /// <summary>
    /// AC2/T1.2, failure side: a scope whose transaction the server already aborted
    /// (a write rejected with 25006) must still roll back and hand the connection
    /// back with no tenant setting, no read-only mode and no open aborted
    /// transaction — the next borrow is a clean session, not 25P02 forever.
    /// </summary>
    [Fact]
    public async Task Pooled_Connection_Has_No_Tenant_After_A_Rolled_Back_Transaction()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var existing = Guid.CreateVersion7();
        await _fixture.SeedProbeAsync(database, (OrgA, existing, "a"));
        await using var host = TenantTestHost.Start(_fixture, database, maximumPoolSize: 1);

        int backend;
        await using (var scope = await host.Scopes.BeginAsync(OrgA, readOnlyTransaction: true, cancellationToken: Token))
        {
            backend = await host.Db.ScalarAsync<int>("select pg_backend_pid()", cancellationToken: Token);
            var refused = await Should.ThrowAsync<PostgresException>(() => host.Db.ExecuteAsync(
                "insert into public.rls_probe (org_id, id, label) values (@org, @id, 'aborted')",
                new { org = OrgA, id = Guid.CreateVersion7() },
                Token));
            refused.SqlState.ShouldBe(PostgresErrorCodes.ReadOnlySqlTransaction);
        }

        await using var connection = await host.DataSource.OpenConnectionAsync(Token);
        (await RawScalarAsync<int>(connection, "select pg_backend_pid()")).ShouldBe(backend);
        (await RawScalarAsync<string>(
            connection,
            "select coalesce(current_setting('evidgate.org_id', true), '')")).ShouldBeEmpty();
        (await RawScalarAsync<string>(
            connection,
            "select coalesce(current_setting('evidgate.project_id', true), '')")).ShouldBeEmpty();
        (await RawScalarAsync<string>(connection, "select current_setting('transaction_read_only')")).ShouldBe("off");

        // Usable, not left inside the aborted transaction, and tenant-less: zero rows (T2).
        (await RawScalarAsync<int>(connection, "select 1")).ShouldBe(1);
        (await RawScalarAsync<int>(connection, "select count(*)::int from public.rls_probe")).ShouldBe(0);
    }

    /// <summary>
    /// T1.2/§6: the scope commits at most once and disposes idempotently; a commit
    /// that is cancelled commits nothing and leaves the scope rollback-able, so a
    /// cancelled request cannot half-apply.
    /// </summary>
    [Fact]
    public async Task Commit_Is_Once_Dispose_Is_Idempotent_And_A_Cancelled_Commit_Writes_Nothing()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = TenantTestHost.Start(_fixture, database, maximumPoolSize: 1);

        var committed = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token);
        await committed.CommitAsync(Token);
        committed.IsActive.ShouldBeFalse();

        var twice = await Should.ThrowAsync<InvalidOperationException>(() => committed.CommitAsync(Token));
        twice.ShouldNotBeOfType<TenantScopeMissingException>();

        await committed.DisposeAsync();
        await committed.DisposeAsync();
        committed.IsActive.ShouldBeFalse();

        var row = Guid.CreateVersion7();
        var cancelledScope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token);
        await using (cancelledScope)
        {
            (await host.Db.ExecuteAsync(
                "insert into public.rls_probe (org_id, id, label) values (@org, @id, 'cancelled-commit')",
                new { org = OrgA, id = row },
                Token)).ShouldBe(1);

            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await Should.ThrowAsync<OperationCanceledException>(() => cancelledScope.CommitAsync(cancelled.Token));
            cancelledScope.IsActive.ShouldBeTrue();
        }

        (await _fixture.CountProbeRowsAsOwnerAsync(database, row)).ShouldBe(0);
    }

    /// <summary>
    /// T1.1/T1.2: <see cref="ITenantScope.Connection"/> and
    /// <see cref="ITenantScope.Transaction"/> are documented to throw
    /// <see cref="TenantScopeMissingException"/> "while the scope is still opening
    /// and after it closed", so a caller that keeps the scope object cannot keep
    /// issuing SQL through the closed one.
    /// <para>
    /// Independent QA of 01.4.2-T1 filed this test RED against the shipped getters,
    /// which kept returning the scope's connection and transaction after
    /// <c>CommitAsync</c> and after <c>DisposeAsync</c>. That defect is fixed, so the
    /// test now pins the contract above.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Closed_Scope_Refuses_Its_Connection_And_Transaction()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = TenantTestHost.Start(_fixture, database);

        var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token);
        await scope.CommitAsync(Token);

        Should.Throw<TenantScopeMissingException>(() => scope.Connection);
        Should.Throw<TenantScopeMissingException>(() => scope.Transaction);

        await scope.DisposeAsync();

        Should.Throw<TenantScopeMissingException>(() => scope.Connection);
        Should.Throw<TenantScopeMissingException>(() => scope.Transaction);
    }

    /// <summary>
    /// T1.3/00 §7: <c>version</c> is the optimistic-concurrency token, so a write
    /// against a row that another transaction changed in the meantime must surface
    /// as <see cref="DbUpdateConcurrencyException"/> — not as a silent overwrite.
    /// </summary>
    [Fact]
    public async Task Stale_Version_Update_Raises_DbUpdateConcurrencyException()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var row = Guid.CreateVersion7();
        await _fixture.SeedProbeAsync(database, (OrgA, row, "original"));
        await using var host = TenantTestHost.Start(_fixture, database);

        await using var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token);
        await using var context = host.Contexts.Create();

        var entity = await context.Set<RlsProbe>().SingleAsync(probe => probe.Id == row, Token);
        entity.Label = "changed-in-scope";

        await using (var other = await _fixture.OpenSuperuserAsync(database))
        {
            await using var command = other.CreateCommand();
            command.CommandText =
                "update public.rls_probe set label = 'changed-outside', version = version + 1 where id = @id";
            command.Parameters.AddWithValue("id", row);
            (await command.ExecuteNonQueryAsync(Token)).ShouldBe(1);
        }

        var error = await Should.ThrowAsync<DbUpdateConcurrencyException>(
            () => context.SaveChangesAsync(Token));
        error.Entries.Count.ShouldBe(1);

        // The loser's value is not written, and the winner's is still the only one.
        (await host.Db.ScalarAsync<string>(
            "select label from public.rls_probe where id = @id",
            new { id = row },
            Token)).ShouldBe("changed-outside");
    }

    /// <summary>
    /// AC1/T1.5: EF Core 10's <c>DbCommandInterceptor</c> has no batch hook — every
    /// command of a batched <c>SaveChanges</c> still arrives as a <c>DbCommand</c>.
    /// The guard must therefore also refuse a <c>MaxBatchSize</c>-batched context
    /// that is not on the scope's connection.
    /// </summary>
    [Fact]
    public async Task Interceptor_Refuses_Batched_Commands_Outside_The_Scope()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await _fixture.SeedProbeAsync(database, (OrgA, Guid.CreateVersion7(), "a"));
        await using var host = TenantTestHost.Start(_fixture, database, maximumPoolSize: 2);

        var batched = new DbContextOptionsBuilder<ProbeDbContext>()
            .UseNpgsql(host.DataSource, npgsql => npgsql.MaxBatchSize(10))
            .AddInterceptors(new TenantCommandInterceptor(host.Scopes))
            .UseTenantModel(typeof(ProbeDbContext).Assembly)
            .Options;

        await using var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token);
        await using var context = new ProbeDbContext(batched);

        foreach (var label in new[] { "batched-1", "batched-2" })
        {
            await context.Set<RlsProbe>().AddAsync(
                new RlsProbe
                {
                    OrgId = OrgA,
                    Id = Guid.CreateVersion7(),
                    Label = label,
                    Version = 1,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
                Token);
        }

        var error = await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Token));
        Flatten(error).ShouldContain(exception => exception is TenantScopeMissingException);

        (await host.Db.ScalarAsync<int>(
            "select count(*)::int from public.rls_probe where label like 'batched-%'",
            cancellationToken: Token)).ShouldBe(0);
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    private static async Task<T> RawScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(Token);
        return value is null or DBNull ? default! : (T)value;
    }
}
