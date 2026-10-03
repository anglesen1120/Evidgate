using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Tenancy.Contracts;
using Evidgate.Modules.Tenancy.Endpoints;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 02.1.1-T3.1: the <see cref="ITenantLimits"/> surface plan 12 reads, resolved from the
/// real Api composition and run inside a tenant scope like a handler would.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class TenantLimitsTests
{
    private readonly PostgresTenancyFixture _fixture;

    public TenantLimitsTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The org limits read back as stored; a project's retention override wins over the
    /// org default and an unset one inherits it; another org's project reads as null.
    /// </summary>
    [Fact]
    public async Task LimitsReadThroughTheTenantScope()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var orgA = Guid.CreateVersion7();
        var orgB = Guid.CreateVersion7();
        var overriding = Guid.CreateVersion7();
        var inheriting = Guid.CreateVersion7();
        var foreign = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            database,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                insert into public.organization (id, name, slug, max_projects, max_seats, retention_default_days)
                values ('{orgA}', 'A', 'org-a', 5, 12, 30), ('{orgB}', 'B', 'org-b', null, null, null);
                insert into public.project (id, org_id, name, slug, retention_days, monthly_run_quota)
                values ('{overriding}', '{orgA}', 'P1', 'p1', 7, 500),
                       ('{inheriting}', '{orgA}', 'P2', 'p2', null, null),
                       ('{foreign}', '{orgB}', 'P3', 'p3', 1, 1);
                """));

        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        var scopes = api.Services.GetRequiredService<ITenantScopeFactory>();
        var limits = api.Services.GetRequiredService<ITenantLimits>();
        var token = TestContext.Current.CancellationToken;

        await using var scope = await scopes.BeginAsync(orgA, readOnlyTransaction: true, cancellationToken: token);

        (await limits.ForOrganizationAsync(token)).ShouldBe(new OrganizationLimits(5, 12, 30));
        (await limits.ForProjectAsync(overriding, token)).ShouldBe(new ProjectLimits(500, 7));
        (await limits.ForProjectAsync(inheriting, token)).ShouldBe(new ProjectLimits(null, 30));
        (await limits.ForProjectAsync(foreign, token)).ShouldBeNull();
    }
}

/// <summary>
/// The id derivation of <c>POST /v1/orgs</c> (02.1.1 §3, AC6): stable for one caller and
/// key, different for another caller or key, and a well-formed RFC 9562 version 8 UUID.
/// </summary>
public sealed class OrganizationIdsTests
{
    [Fact]
    public void SameCallerAndKeyGiveTheSameId()
    {
        var user = Guid.CreateVersion7();

        OrganizationIds.For(user, "key-1").ShouldBe(OrganizationIds.For(user, "key-1"));
        OrganizationIds.For(user, "key-1").ShouldNotBe(OrganizationIds.For(user, "key-2"));
        OrganizationIds.For(user, "key-1").ShouldNotBe(OrganizationIds.For(Guid.CreateVersion7(), "key-1"));
    }

    [Fact]
    public void IdIsAVersion8RfcUuid()
    {
        var id = OrganizationIds.For(Guid.CreateVersion7(), "key").ToString("D", CultureInfo.InvariantCulture);

        id[14].ShouldBe('8');
        "89ab".ShouldContain(id[19]);
    }
}
