using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Tenancy.Domain;
using Evidgate.Modules.Tenancy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// M1W3-01: <c>version</c> is incremented by the database, not by the caller. The
/// <c>set_updated_at_and_version</c> BEFORE UPDATE trigger of migrations 0004/0005
/// bumps it on every row update — raw SQL and EF Core alike — and EF Core reads the
/// new token back (<c>ValueGenerated.OnUpdate</c> in <c>TenantModelConventions</c>),
/// so the optimistic-concurrency contract survives a second update of the same
/// tracked entity inside one scope. Before the trigger existed nothing incremented
/// the token and a stale write overwrote the winner silently.
/// </summary>
[Trait("Category", "Docker")]
public sealed class VersionConcurrencyTests
{
    private readonly PostgresTenancyFixture _fixture;

    public VersionConcurrencyTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The trigger as the application sees it, without EF in the way: an app-role
    /// UPDATE of <c>organization</c> and <c>project</c> moves <c>version</c> 1 → 2
    /// and <c>updated_at</c> past <c>created_at</c>.
    /// </summary>
    [Fact]
    public async Task AppRoleUpdateBumpsVersionAndUpdatedAt()
    {
        var (database, orgId, projectId) = await SeedRegistryAsync();
        await using var source = NpgsqlDataSource.Create(_fixture.AppConnectionString(database));
        var scopes = new NpgsqlTenantScopeFactory(source);
        var db = new TenantDb(scopes);

        await using var scope = await scopes.BeginAsync(orgId, cancellationToken: Token);

        (await db.ExecuteAsync(
            "update public.organization set name = 'renamed' where id = @id",
            new { id = orgId },
            Token)).ShouldBe(1);
        (await db.ExecuteAsync(
            "update public.project set name = 'renamed' where id = @id",
            new { id = projectId },
            Token)).ShouldBe(1);

        foreach (var (table, id) in new[] { ("organization", orgId), ("project", projectId) })
        {
            (await db.ScalarAsync<int>(
                $"select version from public.{table} where id = @id",
                new { id },
                Token)).ShouldBe(2, $"{table} must be versioned by the migration trigger");
            (await db.ScalarAsync<bool>(
                $"select updated_at > created_at from public.{table} where id = @id",
                new { id },
                Token)).ShouldBeTrue($"{table}'s trigger must refresh updated_at");
        }
    }

    /// <summary>
    /// The other half of "the database owns the increment": the token is
    /// store-generated on <em>update</em> only, so an INSERT still carries the
    /// caller's value — a backfill or an import can seed the version it reads from
    /// its source instead of silently getting the column default.
    /// </summary>
    [Fact]
    public async Task InsertedRowKeepsTheCallersVersion()
    {
        var (database, orgId, projectId) = await SeedRegistryAsync();
        await using var source = NpgsqlDataSource.Create(_fixture.AppConnectionString(database));
        var scopes = new NpgsqlTenantScopeFactory(source);
        var db = new TenantDb(scopes);

        await using var scope = await scopes.BeginAsync(orgId, cancellationToken: Token);
        await using var context = await CreateContextAsync(scope, scopes);

        var insertedId = Guid.CreateVersion7();
        await context.Set<Project>().AddAsync(
            new Project
            {
                Id = insertedId,
                OrgId = orgId,
                Name = "Imported",
                Slug = "imported",
                Status = TenancyStatus.Active,
                Version = 5,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            Token);

        (await context.SaveChangesAsync(Token)).ShouldBe(1);
        (await VersionAsync(db, "project", insertedId)).ShouldBe(5);
    }

    /// <summary>
    /// The interop this task is about: two tracked updates of the same entity in one
    /// scope both succeed, the version the caller sees after each <c>SaveChanges</c>
    /// is the one the database holds, and a second entity updated afterwards starts
    /// from its own token.
    /// </summary>
    [Fact]
    public async Task TwoTrackedUpdatesInOneScopeBothSucceedAndBumpTheVersion()
    {
        var (database, orgId, projectId) = await SeedRegistryAsync();
        await using var source = NpgsqlDataSource.Create(_fixture.AppConnectionString(database));
        var scopes = new NpgsqlTenantScopeFactory(source);
        var db = new TenantDb(scopes);

        await using var scope = await scopes.BeginAsync(orgId, cancellationToken: Token);
        await using var context = await CreateContextAsync(scope, scopes);

        var organization = await context.Set<Organization>().SingleAsync(entity => entity.Id == orgId, Token);
        organization.Version.ShouldBe(1);

        organization.Name = "first";
        (await context.SaveChangesAsync(Token)).ShouldBe(1);
        organization.Version.ShouldBe(2, "EF must read the token the trigger wrote back");
        (await VersionAsync(db, "organization", orgId)).ShouldBe(2);

        // The same tracked entity again: the WHERE clause matches the new token only
        // because the previous SaveChanges read it back.
        organization.Name = "second";
        (await context.SaveChangesAsync(Token)).ShouldBe(1);
        organization.Version.ShouldBe(3);
        (await VersionAsync(db, "organization", orgId)).ShouldBe(3);

        // A different entity of the same scope carries its own token.
        var project = await context.Set<Project>().SingleAsync(entity => entity.Id == projectId, Token);
        project.Name = "renamed";
        (await context.SaveChangesAsync(Token)).ShouldBe(1);
        project.Version.ShouldBe(2);
        (await VersionAsync(db, "project", projectId)).ShouldBe(2);

        await scope.CommitAsync(Token);

        (await _fixture.ScalarAsync<int>(
            database,
            $"select version from public.organization where id = '{orgId.ToString("D", CultureInfo.InvariantCulture)}'"))
            .ShouldBe(3);
    }

    /// <summary>
    /// The losing half of the same contract: a row another transaction already moved
    /// on is refused with <see cref="DbUpdateConcurrencyException"/> instead of being
    /// overwritten, and the winner's value survives.
    /// </summary>
    [Fact]
    public async Task StaleVersionUpdateRaisesDbUpdateConcurrencyException()
    {
        var (database, orgId, _) = await SeedRegistryAsync();
        await using var source = NpgsqlDataSource.Create(_fixture.AppConnectionString(database));
        var scopes = new NpgsqlTenantScopeFactory(source);

        await using var scope = await scopes.BeginAsync(orgId, cancellationToken: Token);
        await using var context = await CreateContextAsync(scope, scopes);

        var organization = await context.Set<Organization>().SingleAsync(entity => entity.Id == orgId, Token);
        organization.Name = "loser";

        // The row is only read, not locked, so another writer wins the race — its own
        // UPDATE goes through the same trigger and leaves version 2.
        await using (var other = await _fixture.OpenSuperuserAsync(database))
        {
            await using var command = other.CreateCommand();
            command.CommandText = "update public.organization set name = 'winner' where id = @id";
            command.Parameters.AddWithValue("id", orgId);
            (await command.ExecuteNonQueryAsync(Token)).ShouldBe(1);
        }

        var error = await Should.ThrowAsync<DbUpdateConcurrencyException>(
            () => context.SaveChangesAsync(Token));
        error.Entries.Count.ShouldBe(1);

        var db = new TenantDb(scopes);
        (await db.ScalarAsync<string>(
            "select name from public.organization where id = @id",
            new { id = orgId },
            Token)).ShouldBe("winner");
        (await VersionAsync(db, "organization", orgId)).ShouldBe(2);
    }

    /// <summary>
    /// The production context path of 01.4.2-T1.3, as <c>TenantDbContextFactory</c>
    /// assembles it: the scope's connection, the scope's transaction joined with
    /// <c>UseTransaction</c>, the AC1 interceptor and the module's configuration
    /// assemblies.
    /// </summary>
    private static async Task<TenancyWriteModelContext> CreateContextAsync(
        ITenantScope scope,
        ITenantScopeAccessor scopes)
    {
        var options = new DbContextOptionsBuilder<TenancyWriteModelContext>()
            .UseNpgsql(scope.Connection)
            .AddInterceptors(new TenantCommandInterceptor(scopes))
            .UseTenantModel(typeof(OrganizationConfiguration).Assembly)
            .Options;

        var context = new TenancyWriteModelContext(options);
        await context.Database.UseTransactionAsync(scope.Transaction, Token);
        return context;
    }

    private static async Task<int> VersionAsync(TenantDb db, string table, Guid id) =>
        await db.ScalarAsync<int>($"select version from public.{table} where id = @id", new { id }, Token);

    /// <summary>One organization and one project, inserted as the migration role with fixed timestamps.</summary>
    private async Task<(string Database, Guid OrgId, Guid ProjectId)> SeedRegistryAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var orgId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var orgSlug = Unique.Slug("acme");
        var createdAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await scope.ExecuteAsync(
            "insert into public.organization (id, name, slug, created_at, updated_at) "
            + "values (@id, 'Acme', @slug, @at, @at)",
            ("id", orgId),
            ("slug", orgSlug),
            ("at", createdAt));
        await scope.ExecuteAsync(
            "insert into public.project (id, org_id, name, slug, created_at, updated_at) "
            + "values (@id, @org_id, 'Checkout', 'checkout', @at, @at)",
            ("id", projectId),
            ("org_id", orgId),
            ("at", createdAt));
        await scope.CommitAsync();

        return (database, orgId, projectId);
    }
}
