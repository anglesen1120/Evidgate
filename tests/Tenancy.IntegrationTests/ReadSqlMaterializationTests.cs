using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Tenancy.Contracts;
using Evidgate.Modules.Tenancy.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// M1W3-02, the other half of <see cref="ReadSqlContractTests"/>: that suite proves
/// the statements project the DTO's columns in the DTO's order, this one proves
/// Dapper can actually materialise them. The registry DTOs are positional records
/// whose timestamps are <see cref="DateTimeOffset"/> while Npgsql reports
/// <c>timestamptz</c> as <see cref="DateTime"/>, so materialisation needs both the
/// underscore name matching and a <c>DateTimeOffset</c> type handler — configured by
/// <c>AddEvidgateTenancy</c> and exercised here through that registration, the
/// production <c>ITenantDb</c> and a real tenant scope.
/// </summary>
/// <remarks>
/// Every field is asserted, not just "not null": a column the SQL no longer returns
/// (or returns in another position) leaves that DTO member at its default, which a
/// null check cannot see.
/// </remarks>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class ReadSqlMaterializationTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset UpdatedAt = new(2026, 1, 3, 4, 5, 6, TimeSpan.Zero);

    private readonly PostgresTenancyFixture _fixture;

    public ReadSqlMaterializationTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Every <see cref="OrganizationDto"/> member binds from <c>TenancyReadSql.OrganizationById</c>.</summary>
    [Fact]
    public async Task OrganizationByIdMaterializesEveryField()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var orgId = Guid.CreateVersion7();

        await using (var seeding = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            await seeding.ExecuteAsync(
                "insert into public.organization "
                + "(id, name, slug, status, retention_default_days, allowed_email_domains, created_at, updated_at, version) "
                + "values (@id, 'Acme', 'acme', 'suspended', 30, array['acme.example'], @created_at, @updated_at, 7)",
                ("id", orgId),
                ("created_at", CreatedAt.UtcDateTime),
                ("updated_at", UpdatedAt.UtcDateTime));
            await seeding.CommitAsync();
        }

        await using var host = Host.Start(_fixture, database);
        await using var scope = await host.Scopes.BeginAsync(orgId, readOnlyTransaction: true, cancellationToken: Token);

        var dto = await host.Db.QuerySingleOrDefaultAsync<OrganizationDto>(
            TenancyReadSql.OrganizationById,
            new { org_id = orgId },
            Token);

        dto.ShouldNotBeNull();
        dto.ShouldSatisfyAllConditions(
            materialized => materialized.Id.ShouldBe(orgId),
            materialized => materialized.Name.ShouldBe("Acme"),
            materialized => materialized.Slug.ShouldBe("acme"),
            materialized => materialized.Status.ShouldBe("suspended"),
            materialized => materialized.RetentionDefaultDays.ShouldBe(30),
            materialized => materialized.AllowedEmailDomains.ShouldBe(["acme.example"]),
            materialized => materialized.CreatedAt.ShouldBe(CreatedAt),
            materialized => materialized.CreatedAt.Offset.ShouldBe(TimeSpan.Zero),
            materialized => materialized.UpdatedAt.ShouldBe(UpdatedAt),
            materialized => materialized.UpdatedAt.Offset.ShouldBe(TimeSpan.Zero),
            materialized => materialized.Version.ShouldBe(7));
    }

    /// <summary>Every <see cref="ProjectDto"/> member binds from <c>TenancyReadSql.ProjectById</c>, the null one included.</summary>
    [Fact]
    public async Task ProjectByIdMaterializesEveryField()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var orgId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        var emptyProjectId = Guid.CreateVersion7();

        await using (var seeding = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            await seeding.ExecuteAsync(
                "insert into public.organization (id, name, slug) values (@id, 'Acme', 'acme')",
                ("id", orgId));
            // project <-> environment is a foreign-key cycle (0024): one statement inserts both, and the
            // non-deferred checks run at its end.
            await seeding.ExecuteAsync(
                "with p as (insert into public.project "
                + "(id, org_id, name, slug, status, default_environment_id, created_at, updated_at, version) "
                + "values (@id, @org_id, 'Checkout', 'checkout', 'suspended', @environment_id, @created_at, @updated_at, 4) returning id) "
                + "insert into public.environment (id, org_id, project_id, environment_key, name) "
                + "select @environment_id, @org_id, p.id, 'staging', 'Staging' from p",
                ("id", projectId),
                ("org_id", orgId),
                ("environment_id", environmentId),
                ("created_at", CreatedAt.UtcDateTime),
                ("updated_at", UpdatedAt.UtcDateTime));
            await seeding.ExecuteAsync(
                "insert into public.project (id, org_id, name, slug) values (@id, @org_id, 'Cart', 'cart')",
                ("id", emptyProjectId),
                ("org_id", orgId));
            await seeding.CommitAsync();
        }

        await using var host = Host.Start(_fixture, database);
        await using var scope = await host.Scopes.BeginAsync(orgId, readOnlyTransaction: true, cancellationToken: Token);

        var dto = await host.Db.QuerySingleOrDefaultAsync<ProjectDto>(
            TenancyReadSql.ProjectById,
            new { project_id = projectId },
            Token);

        dto.ShouldNotBeNull();
        dto.ShouldSatisfyAllConditions(
            materialized => materialized.Id.ShouldBe(projectId),
            materialized => materialized.OrgId.ShouldBe(orgId),
            materialized => materialized.Name.ShouldBe("Checkout"),
            materialized => materialized.Slug.ShouldBe("checkout"),
            materialized => materialized.Status.ShouldBe("suspended"),
            materialized => materialized.DefaultEnvironmentId.ShouldBe(environmentId),
            materialized => materialized.CreatedAt.ShouldBe(CreatedAt),
            materialized => materialized.CreatedAt.Offset.ShouldBe(TimeSpan.Zero),
            materialized => materialized.UpdatedAt.ShouldBe(UpdatedAt),
            materialized => materialized.Version.ShouldBe(4));

        // The nullable column really is null, not a default: the same statement
        // materialises the row the migration left without a preferred environment.
        var withoutEnvironment = await host.Db.QuerySingleOrDefaultAsync<ProjectDto>(
            TenancyReadSql.ProjectById,
            new { project_id = emptyProjectId },
            Token);

        withoutEnvironment.ShouldNotBeNull().ShouldSatisfyAllConditions(
            materialized => materialized.Id.ShouldBe(emptyProjectId),
            materialized => materialized.OrgId.ShouldBe(orgId),
            materialized => materialized.Name.ShouldBe("Cart"),
            materialized => materialized.Slug.ShouldBe("cart"),
            materialized => materialized.Status.ShouldBe("active"),
            materialized => materialized.DefaultEnvironmentId.ShouldBeNull(),
            materialized => materialized.Version.ShouldBe(1));
    }

    /// <summary>
    /// Zero rows materialises to <c>default</c> rather than throwing — the DTO has one
    /// constructor and no parameterless fallback, so this is the case a query handler
    /// maps to 404.
    /// </summary>
    [Fact]
    public async Task MissingRowMaterializesToNull()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var orgId = Guid.CreateVersion7();

        await using (var seeding = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            await seeding.ExecuteAsync(
                "insert into public.organization (id, name, slug) values (@id, 'Acme', 'acme')",
                ("id", orgId));
            await seeding.CommitAsync();
        }

        await using var host = Host.Start(_fixture, database);
        await using var scope = await host.Scopes.BeginAsync(orgId, readOnlyTransaction: true, cancellationToken: Token);

        (await host.Db.QuerySingleOrDefaultAsync<OrganizationDto>(
            TenancyReadSql.OrganizationById,
            new { org_id = Guid.CreateVersion7() },
            Token)).ShouldBeNull();
    }

    /// <summary>
    /// The Dapper path as a host builds it: an app-role <c>NpgsqlDataSource</c> plus the
    /// production registration. <c>AddEvidgateTenancy</c> is what configures Dapper for
    /// the read path (underscore matching + the <c>DateTimeOffset</c> handler), and this
    /// suite deliberately goes through it instead of setting the statics itself — a
    /// registration that forgets them fails here.
    /// </summary>
    private sealed class Host : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly NpgsqlDataSource _dataSource;

        private Host(ServiceProvider provider, NpgsqlDataSource dataSource)
        {
            _provider = provider;
            _dataSource = dataSource;
        }

        internal ITenantDb Db => _provider.GetRequiredService<ITenantDb>();

        internal ITenantScopeFactory Scopes => _provider.GetRequiredService<ITenantScopeFactory>();

        internal static Host Start(PostgresTenancyFixture fixture, string database)
        {
            var dataSource = NpgsqlDataSource.Create(fixture.AppConnectionString(database));
            var services = new ServiceCollection();
            services.AddSingleton(dataSource);
            services.AddEvidgateTenancy();

            return new Host(services.BuildServiceProvider(), dataSource);
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await _dataSource.DisposeAsync();
        }
    }
}
