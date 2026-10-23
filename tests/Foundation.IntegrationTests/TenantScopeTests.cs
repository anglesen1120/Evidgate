using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Xunit;

namespace Foundation.IntegrationTests;

/// <summary>
/// 01.6.1-T2.3 (AC4): <see cref="TenantTestScope"/> over the harness — a real
/// <c>ITenantScope</c> for a seeded organization, and the C3 guard in front of it.
/// The rows are arranged through the harness's owner seam
/// (<see cref="DatabaseFixture.SeedOrgAsync"/>, <see cref="DatabaseFixture.SeedProjectAsync"/>)
/// and read back through the app role, so the arrangement is marked and the
/// assertion is not (00 §5.5 C3).
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class TenantScopeTests : HarnessTest
{
    public TenantScopeTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// AC4/C3: without an open scope no SQL gets out, on either half of the guard —
    /// the Dapper accessor and the EF Core <c>DbCommandInterceptor</c> — and a scope
    /// that closed is closed for its connection too. The last two legs also show the
    /// guard is not refusing everything: the same probe context is accepted once it
    /// runs on the scope's connection and transaction, which is exactly how the
    /// production pipeline binds it (00 §5.5 C3).
    /// </summary>
    [Fact]
    public async Task TenantScope_MissingOrgContext_Throws()
    {
        var dapper = await Should.ThrowAsync<TenantScopeMissingException>(
            () => Fixture.Db.QueryAsync<Guid>("select id from public.organization", cancellationToken: Token));
        dapper.Message.ShouldContain("tenant scope");

        Should.Throw<TenantScopeMissingException>(() => Fixture.Scopes.Require());

        // The EF Core half of C3: a context on a connection from the app pool that is
        // not the scope's runs no command.
        await using (var foreign = await Fixture.AppDataSource.OpenConnectionAsync(Token))
        {
            await using var unscoped = new CommandProbeContext(foreign, Fixture.Interceptor);
            var intercepted = await Should.ThrowAsync<TenantScopeMissingException>(
                () => unscoped.Database.ExecuteSqlRawAsync("select 1", Token));
            intercepted.Message.ShouldContain(nameof(TenantCommandInterceptor));
        }

        await using (var scope = await Fixture.BeginTenantScopeAsync(Guid.CreateVersion7(), cancellationToken: Token))
        {
            await using var scoped = new CommandProbeContext(scope.Connection, Fixture.Interceptor);
            await scoped.Database.UseTransactionAsync(scope.Transaction, Token);

            var user = await scoped.Database
                .SqlQueryRaw<string>("select current_user as \"Value\"")
                .SingleAsync(Token);
            user.ShouldBe("evidgate_app");
        }

        var closed = await Fixture.BeginTenantScopeAsync(Guid.CreateVersion7(), cancellationToken: Token);
        await closed.DisposeAsync();
        Should.Throw<TenantScopeMissingException>(() => closed.Connection);
    }

    /// <summary>
    /// AC4: a seeded org and project are readable through a real scope for that
    /// org, on both the raw connection and the Dapper path, and are invisible from a
    /// scope for another org — the scope, not the seed helper, is the boundary.
    /// </summary>
    [Fact]
    public async Task SeedOrgAndProject_RoundTrip()
    {
        var (orgId, projectId) = await Fixture.SeedProjectAsync(cancellationToken: Token);

        await using (var scope = await Fixture.BeginTenantScopeAsync(orgId, projectId, cancellationToken: Token))
        {
            scope.OrgId.ShouldBe(orgId);
            scope.ProjectId.ShouldBe(projectId);
            scope.IsReadOnly.ShouldBeFalse();

            (await scope.ScalarAsync<long>("select count(*) from public.organization", Token)).ShouldBe(1L);
            (await scope.ScalarAsync<long>("select count(*) from public.project", Token)).ShouldBe(1L);
            (await scope.ScalarAsync<Guid>("select id from public.project", Token)).ShouldBe(projectId);

            // The Dapper path runs on the scope's connection and transaction (C3).
            var organizations = await scope.Db.QueryAsync<string>(
                "select name from public.organization",
                cancellationToken: Token);
            organizations.ShouldHaveSingleItem().ShouldBe("Seeded organization");

            // The fixture's own accessor resolves the same ambient scope, so the
            // factory's publication reached the test's frame and not only the
            // wrapper's (the AsyncLocal trap of NpgsqlTenantScopeFactory).
            var projects = await Fixture.Db.QueryAsync<string>(
                "select name from public.project",
                cancellationToken: Token);
            projects.ShouldHaveSingleItem().ShouldBe("Seeded project");
        }

        // Another tenant's scope sees none of it: RLS applies to the app role.
        await using (var other = await Fixture.BeginTenantScopeAsync(Guid.CreateVersion7(), cancellationToken: Token))
        {
            (await other.ScalarAsync<long>("select count(*) from public.organization", Token)).ShouldBe(0L);
            (await other.ScalarAsync<long>("select count(*) from public.project", Token)).ShouldBe(0L);
        }
    }

    /// <summary>
    /// AC4/C1: the scope's read-only mode is in force at the database, so a query
    /// scope cannot be used to write (T95) — the harness exposes the flag, and this
    /// proves it is not decoration.
    /// </summary>
    [Fact]
    public async Task TenantScope_ReadOnly_RejectsWrite()
    {
        var orgId = await Fixture.SeedOrgAsync(cancellationToken: Token);

        await using var scope = await Fixture.BeginTenantScopeAsync(
            orgId,
            readOnlyTransaction: true,
            cancellationToken: Token);

        scope.IsReadOnly.ShouldBeTrue();

        await using var command = scope.Command(
            "insert into public.rls_probe (org_id, id, label) values (@org_id, @id, @label)");
        command.Parameters.AddWithValue("org_id", orgId);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("label", "read-only probe");

        var failure = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Token));
        failure.SqlState.ShouldBe(PostgresErrorCodes.ReadOnlySqlTransaction);

        // Nothing leaked out of the aborted scope.
        (await Fixture.CountProbeRowsAsync(orgId, Token)).ShouldBe(0L);
    }

    /// <summary>
    /// The smallest EF Core context the C3 interceptor can be exercised with: no
    /// entity types, because a command is all the guard looks at, and a real module
    /// context belongs to its module (01.6.1-T4 runs the isolation read and write
    /// paths over one). It mirrors the production binding: a connection, the tenant
    /// interceptor, and the scope's transaction added by the caller.
    /// </summary>
    private sealed class CommandProbeContext : DbContext
    {
        internal CommandProbeContext(NpgsqlConnection connection, TenantCommandInterceptor interceptor)
            : base(Options(connection, interceptor))
        {
        }

        private static DbContextOptions<CommandProbeContext> Options(
            NpgsqlConnection connection,
            TenantCommandInterceptor interceptor) =>
            new DbContextOptionsBuilder<CommandProbeContext>()
                .UseNpgsql(connection)
                .AddInterceptors(interceptor)
                .Options;
    }
}
