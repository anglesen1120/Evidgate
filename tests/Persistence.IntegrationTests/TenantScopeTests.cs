using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using Shouldly;
using Xunit;

namespace Persistence.IntegrationTests;

/// <summary>
/// 01.4.2-T1.6: the tenant scope, the EF Core and Dapper binding and the
/// interceptor guard, proven against real PostgreSQL as the app role (AC1, AC2,
/// AC3, AC6). Every test gets a fresh database migrated by the real Migrator, and
/// no test can leave a row behind: a scope that is not committed rolls back.
/// </summary>
[Collection(PersistenceSuite.Name)]
[Trait("Category", "Docker")]
public sealed class TenantScopeTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid OrgB = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid ProjectA = Guid.Parse("33333333-3333-7333-8333-333333333333");

    private readonly PostgresPersistenceFixture _fixture;

    public TenantScopeTests(PostgresPersistenceFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// AC1: SQL on either path without a prior <c>BeginAsync</c> throws
    /// <see cref="TenantScopeMissingException"/> — the accessor for Dapper, the
    /// factory for EF Core, and the interceptor for a context that was built by
    /// hand on the app-role data source.
    /// </summary>
    [Fact]
    public async Task Command_Without_Scope_Throws()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = TenantTestHost.Start(_fixture, database);

        var dapper = await Should.ThrowAsync<TenantScopeMissingException>(
            () => host.Db.QueryAsync<Guid>("select id from public.rls_probe", cancellationToken: Token));
        dapper.Message.ShouldContain("tenant scope");

        Should.Throw<TenantScopeMissingException>(() => host.Contexts.Create());

        await using var context = new ProbeDbContext(host.UnscopedOptions);
        await Should.ThrowAsync<TenantScopeMissingException>(
            () => context.Set<RlsProbe>().CountAsync(Token));
    }

    /// <summary>
    /// AC1/C3: with a scope open, a context whose command runs on a different
    /// connection is still refused — the interceptor checks the connection, not
    /// just the presence of a scope.
    /// </summary>
    [Fact]
    public async Task Interceptor_Rejects_A_Connection_Outside_The_Scope()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await _fixture.SeedProbeAsync(database, (OrgA, Guid.CreateVersion7(), "a"));
        await using var host = TenantTestHost.Start(_fixture, database, maximumPoolSize: 4);

        await using (var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token))
        {
            await using var foreign = new ProbeDbContext(host.UnscopedOptions);
            await Should.ThrowAsync<TenantScopeMissingException>(
                () => foreign.Set<RlsProbe>().CountAsync(Token));

            // The scope's own context is accepted, so the guard is not simply refusing everything.
            await using var scoped = host.Contexts.Create();
            (await scoped.Set<RlsProbe>().CountAsync(Token)).ShouldBe(1);
            scope.IsActive.ShouldBeTrue();
        }
    }

    /// <summary>
    /// AC2: inside a scope only that tenant's rows are visible, on both data-access
    /// paths, and both tenant settings are in force.
    /// </summary>
    [Fact]
    public async Task Cross_Tenant_Zero_Rows()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var rowA = Guid.CreateVersion7();
        var rowB = Guid.CreateVersion7();
        await _fixture.SeedProbeAsync(database, (OrgA, rowA, "a"), (OrgB, rowB, "b"));
        await using var host = TenantTestHost.Start(_fixture, database);

        await using var scope = await host.Scopes.BeginAsync(OrgA, ProjectA, cancellationToken: Token);
        scope.OrgId.ShouldBe(OrgA);
        scope.ProjectId.ShouldBe(ProjectA);
        scope.IsReadOnly.ShouldBeFalse();

        (await host.Db.ScalarAsync<string>("select current_setting('evidgate.org_id')", cancellationToken: Token))
            .ShouldBe(OrgA.ToString("D", CultureInfo.InvariantCulture));
        (await host.Db.ScalarAsync<string>("select current_setting('evidgate.project_id')", cancellationToken: Token))
            .ShouldBe(ProjectA.ToString("D", CultureInfo.InvariantCulture));

        // Dapper and EF Core read through RLS on the scope's own transaction.
        (await host.Db.QueryAsync<Guid>("select id from public.rls_probe", cancellationToken: Token))
            .ShouldBe([rowA]);

        await using var context = host.Contexts.Create();
        (await context.Set<RlsProbe>().Select(probe => probe.Id).ToListAsync(Token)).ShouldBe([rowA]);

        // The other tenant's row is not visible and cannot be reached by id either.
        (await host.Db.ScalarAsync<int>(
            "select count(*)::int from public.rls_probe where org_id = @org",
            new { org = OrgB },
            Token)).ShouldBe(0);
        (await host.Db.ScalarAsync<int>(
            "select count(*)::int from public.rls_probe where id = @id",
            new { id = rowB },
            Token)).ShouldBe(0);
    }

    /// <summary>
    /// AC2/T1.2: the next borrow of the same pooled connection carries no tenant:
    /// <c>set_config(…, true)</c> is transaction-scoped, and the scope's commit or
    /// rollback is what returns the connection clean.
    /// </summary>
    [Fact]
    public async Task Pooled_Connection_Has_No_Tenant()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var rowA = Guid.CreateVersion7();
        var rowB = Guid.CreateVersion7();
        await _fixture.SeedProbeAsync(database, (OrgA, rowA, "a"), (OrgB, rowB, "b"));

        // One connection in the pool, so the next borrow is provably the same backend.
        await using var host = TenantTestHost.Start(_fixture, database, maximumPoolSize: 1);

        int backend;
        await using (var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token))
        {
            backend = await host.Db.ScalarAsync<int>("select pg_backend_pid()", cancellationToken: Token);
            (await host.Db.QueryAsync<Guid>("select id from public.rls_probe", cancellationToken: Token))
                .ShouldBe([rowA]);
        }

        await using (var connection = await host.DataSource.OpenConnectionAsync(Token))
        {
            (await ScalarAsync<int>(connection, "select pg_backend_pid()", Token)).ShouldBe(backend);
            (await ScalarAsync<string>(
                connection,
                "select coalesce(current_setting('evidgate.org_id', true), '')",
                Token)).ShouldBeEmpty();
            (await ScalarAsync<string>(
                connection,
                "select coalesce(current_setting('evidgate.project_id', true), '')",
                Token)).ShouldBeEmpty();

            // T2: no tenant context is zero rows — never an error, never every row.
            (await ScalarAsync<int>(connection, "select count(*)::int from public.rls_probe", Token)).ShouldBe(0);
        }

        // The next scope starts from a clean slate on that same backend.
        await using (var scope = await host.Scopes.BeginAsync(OrgB, cancellationToken: Token))
        {
            (await host.Db.ScalarAsync<int>("select pg_backend_pid()", cancellationToken: Token)).ShouldBe(backend);
            (await host.Db.QueryAsync<Guid>("select id from public.rls_probe", cancellationToken: Token))
                .ShouldBe([rowB]);
        }
    }

    /// <summary>
    /// AC3: an EF Core write and a Dapper read inside one scope see each other's
    /// uncommitted rows (one transaction), and <c>CommitAsync</c> is what makes the
    /// row visible to everyone else.
    /// </summary>
    [Fact]
    public async Task Write_Then_Read_Same_Transaction()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var row = Guid.CreateVersion7();
        await using var host = TenantTestHost.Start(_fixture, database);

        var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token);
        await using (scope)
        {
            await using (var context = host.Contexts.Create())
            {
                await context.Set<RlsProbe>().AddAsync(NewProbe(OrgA, row, "ef"), Token);
                (await context.SaveChangesAsync(Token)).ShouldBe(1);
            }

            (await host.Db.ScalarAsync<string>(
                "select label from public.rls_probe where id = @id",
                new { id = row },
                Token)).ShouldBe("ef");

            // Not durable yet: another connection sees nothing.
            (await _fixture.CountProbeRowsAsOwnerAsync(database, row)).ShouldBe(0);
            scope.IsActive.ShouldBeTrue();

            await scope.CommitAsync(Token);
        }

        scope.IsActive.ShouldBeFalse();
        (await _fixture.CountProbeRowsAsOwnerAsync(database, row)).ShouldBe(1);
    }

    /// <summary>AC3/T1.2: a scope disposed without a commit rolls its work back.</summary>
    [Fact]
    public async Task Dispose_Without_Commit_Rolls_Back()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var row = Guid.CreateVersion7();
        await using var host = TenantTestHost.Start(_fixture, database);

        await using (var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token))
        {
            (await host.Db.ExecuteAsync(
                "insert into public.rls_probe (org_id, id, label) values (@org, @id, 'uncommitted')",
                new { org = OrgA, id = row },
                Token)).ShouldBe(1);
            (await host.Db.ScalarAsync<int>(
                "select count(*)::int from public.rls_probe where id = @id",
                new { id = row },
                Token)).ShouldBe(1);
        }

        (await _fixture.CountProbeRowsAsOwnerAsync(database, row)).ShouldBe(0);
    }

    /// <summary>T1.2: one scope per request/task — a nested <c>BeginAsync</c> throws and leaves the outer scope alone.</summary>
    [Fact]
    public async Task Nested_Scope_Throws()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = TenantTestHost.Start(_fixture, database);

        await using var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token);

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => host.Scopes.BeginAsync(OrgB, cancellationToken: Token));
        error.ShouldNotBeOfType<TenantScopeMissingException>();
        error.Message.ShouldContain("already open");

        host.Scopes.Current.ShouldBeSameAs(scope);
        (await host.Db.ScalarAsync<string>("select current_setting('evidgate.org_id')", cancellationToken: Token))
            .ShouldBe(OrgA.ToString("D", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// T1.2/T1.5: after the commit the transaction-scoped settings are gone, so the
    /// guard refuses further SQL instead of running it with no tenant.
    /// </summary>
    [Fact]
    public async Task Sql_After_Commit_Throws()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = TenantTestHost.Start(_fixture, database);

        await using var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token);
        await scope.CommitAsync(Token);

        scope.IsActive.ShouldBeFalse();
        await Should.ThrowAsync<TenantScopeMissingException>(
            () => host.Db.ScalarAsync<int>("select 1", cancellationToken: Token));
        Should.Throw<TenantScopeMissingException>(() => host.Contexts.Create());
    }

    /// <summary>
    /// AC6/T95: a read-only scope is <c>BEGIN READ ONLY</c> with the tenant
    /// <c>set_config</c> inside it, and what this test observes is the effect from
    /// the scope's first statement on: the transaction is read-only and
    /// <c>evidgate.org_id</c> is in force in the same statement, and RLS narrows the
    /// read. (The ordering — mode before the settings — is a code requirement in
    /// <c>TenantScope</c>, not something a caller can observe from outside.)
    /// </summary>
    [Fact]
    public async Task Query_Scope_Is_Read_Only_And_Tenant_Scoped_From_Its_First_Statement()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var rowA = Guid.CreateVersion7();
        var rowB = Guid.CreateVersion7();
        await _fixture.SeedProbeAsync(database, (OrgA, rowA, "a"), (OrgB, rowB, "b"));
        await using var host = TenantTestHost.Start(_fixture, database);

        await using var scope = await host.Scopes.BeginAsync(OrgA, readOnlyTransaction: true, cancellationToken: Token);
        scope.IsReadOnly.ShouldBeTrue();

        // Both hold in the same statement: the transaction is read-only and the
        // tenant setting is applied, from the scope's first statement on.
        (await host.Db.ScalarAsync<bool>(
            "select current_setting('transaction_read_only') = 'on'"
            + " and current_setting('evidgate.org_id') = @org",
            new { org = OrgA.ToString("D", CultureInfo.InvariantCulture) },
            Token)).ShouldBeTrue();

        // RLS still narrows the read to the tenant inside that read-only transaction.
        (await host.Db.QueryAsync<Guid>("select id from public.rls_probe", cancellationToken: Token))
            .ShouldBe([rowA]);
        (await host.Db.ScalarAsync<int>(
            "select count(*)::int from public.rls_probe where id = @id",
            new { id = rowB },
            Token)).ShouldBe(0);
    }

    /// <summary>
    /// AC6: a write inside a read-only scope is rejected by PostgreSQL — through
    /// Dapper and through EF Core — with SQLSTATE 25006, and nothing lands.
    /// </summary>
    [Fact]
    public async Task Query_Tx_Is_Read_Only()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = TenantTestHost.Start(_fixture, database);

        var dapperRow = Guid.CreateVersion7();
        await using (var scope = await host.Scopes.BeginAsync(OrgA, readOnlyTransaction: true, cancellationToken: Token))
        {
            var error = await Should.ThrowAsync<PostgresException>(() => host.Db.ExecuteAsync(
                "insert into public.rls_probe (org_id, id, label) values (@org, @id, 'forbidden')",
                new { org = OrgA, id = dapperRow },
                Token));
            error.SqlState.ShouldBe(PostgresErrorCodes.ReadOnlySqlTransaction);
        }

        var efRow = Guid.CreateVersion7();
        await using (var scope = await host.Scopes.BeginAsync(OrgA, readOnlyTransaction: true, cancellationToken: Token))
        {
            await using var context = host.Contexts.Create();
            await context.Set<RlsProbe>().AddAsync(NewProbe(OrgA, efRow, "forbidden"), Token);

            var error = await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Token));
            error.InnerException.ShouldBeOfType<PostgresException>()
                .SqlState.ShouldBe(PostgresErrorCodes.ReadOnlySqlTransaction);
        }

        (await _fixture.CountProbeRowsAsOwnerAsync(database, dapperRow)).ShouldBe(0);
        (await _fixture.CountProbeRowsAsOwnerAsync(database, efRow)).ShouldBe(0);
    }

    /// <summary>
    /// T1.3: the module model is mapped by the conventions — snake_case singular
    /// table and column names, <c>version</c> as the optimistic-concurrency token,
    /// and an application-generated (UUIDv7) id EF never fills in itself.
    /// </summary>
    [Fact]
    public async Task Model_Follows_Tenant_Conventions()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = TenantTestHost.Start(_fixture, database);
        await using var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token);
        await using var context = host.Contexts.Create();

        var entity = context.Model.FindEntityType(typeof(RlsProbe));
        entity.ShouldNotBeNull();

        entity.GetTableName().ShouldBe("rls_probe");
        entity.FindProperty(nameof(RlsProbe.OrgId))!.GetColumnName().ShouldBe("org_id");
        entity.FindProperty(nameof(RlsProbe.CreatedAt))!.GetColumnName().ShouldBe("created_at");
        entity.FindProperty(nameof(RlsProbe.Version))!.IsConcurrencyToken.ShouldBeTrue();
        entity.FindProperty(nameof(RlsProbe.Id))!.ValueGenerated.ShouldBe(ValueGenerated.Never);

        Guid.CreateVersion7().Version.ShouldBe(7);
    }

    private static RlsProbe NewProbe(Guid orgId, Guid id, string label) => new()
    {
        OrgId = orgId,
        Id = id,
        Label = label,
        Version = 1,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, parameterValue) in parameters)
        {
            command.Parameters.AddWithValue(name, parameterValue);
        }

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? default! : (T)value;
    }
}
