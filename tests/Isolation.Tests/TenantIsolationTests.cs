using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Tenancy.Contracts;
using Evidgate.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// 01.6.1-T4.1 and 00 §11: the tenant boundary on the real business tables of
/// migrations 0004/0005 (<c>organization</c>, <c>project</c>), not the 0003 probe
/// template. Both orgs are seeded through the harness's owner seam (marked
/// arrangement, 00 §5.5 C3), and every assertion goes through a real
/// <c>ITenantScope</c> as <c>evidgate_app</c>: an EF Core read, a Dapper read and an
/// EF Core write, so a leak shows on whichever path introduced it.
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class TenantIsolationTests : HarnessTest
{
    /// <summary>Binds the test to the shared harness fixture (container, migrations, Respawn).</summary>
    public TenantIsolationTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// As org A the EF Core write model reads org A's project and cannot see org
    /// B's — queried by id alone, with no <c>org_id</c> predicate and no EF-level
    /// query filter (01.4.2-T1.3's conventions name tables and columns only), so the
    /// empty result is the database's row level security, not application code.
    /// </summary>
    [Fact]
    public async Task Isolation_EfRead_OtherOrg_ReturnsEmpty()
    {
        var (orgA, projectA) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var (_, projectB) = await Fixture.SeedProjectAsync(cancellationToken: Token);

        await using var scope = await Fixture.BeginTenantScopeAsync(orgA, cancellationToken: Token);
        await using var context = IsolationProbe.CreateContext(scope, Fixture.Interceptor);

        // The probe is not blind: org A's row is there, and the count is exactly one.
        (await context.Projects.CountAsync(Token)).ShouldBe(1);
        (await context.Projects.SingleOrDefaultAsync(p => p.Id == projectA, Token)).ShouldNotBeNull();
        (await context.Projects.SingleOrDefaultAsync(p => p.Id == projectB, Token)).ShouldBeNull();
    }

    /// <summary>
    /// The same read through the Dapper path the API queries use
    /// (<see cref="ITenantDb"/> on the scope's transaction): another org's project id
    /// is indistinguishable from a missing one, and so is another org's organization
    /// id on the exception table — <c>organization</c> carries no <c>org_id</c> column
    /// (00 §5.4.1, T4) and its policy compares the row's own <c>id</c>.
    /// </summary>
    [Fact]
    public async Task Isolation_DapperRead_OtherOrg_ReturnsEmpty()
    {
        var (orgA, projectA) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var (orgB, projectB) = await Fixture.SeedProjectAsync(cancellationToken: Token);

        await using var scope = await Fixture.BeginTenantScopeAsync(orgA, cancellationToken: Token);

        var own = await scope.Db.QuerySingleOrDefaultAsync<ProjectDto>(
            IsolationProbeSql.ProjectById,
            new { project_id = projectA },
            Token);
        own.ShouldNotBeNull().Id.ShouldBe(projectA);

        (await scope.Db.QuerySingleOrDefaultAsync<ProjectDto>(
            IsolationProbeSql.ProjectById,
            new { project_id = projectB },
            Token))
            .ShouldBeNull();

        (await scope.Db.ScalarAsync<Guid>(
            "select id from public.organization where id = @org_id",
            new { org_id = orgA },
            Token))
            .ShouldBe(orgA);
        (await scope.Db.ScalarAsync<Guid?>(
            "select id from public.organization where id = @org_id",
            new { org_id = orgB },
            Token))
            .ShouldBeNull();
    }

    /// <summary>
    /// The <c>with check</c> half of <c>project_app_tenant</c>: the EF Core write of
    /// a project filed under another org is refused with a row-level-security
    /// error — refused, not silently dropped — and the forged row is genuinely
    /// absent afterwards, checked from the victim org's own scope on a new
    /// transaction (the failed one is aborted, 25P02).
    /// </summary>
    [Fact]
    public async Task Isolation_EfWrite_OtherOrg_Fails()
    {
        var (orgA, _) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var (orgB, _) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var forgedId = Guid.CreateVersion7();

        await using (var scope = await Fixture.BeginTenantScopeAsync(orgA, cancellationToken: Token))
        {
            await using var context = IsolationProbe.CreateContext(scope, Fixture.Interceptor);

            await context.Projects.AddAsync(new Project
            {
                Id = forgedId,
                OrgId = orgB,
                Name = "Cross-tenant write probe",
                Slug = string.Create(
                    CultureInfo.InvariantCulture,
                    $"forged-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12]}"),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            }, Token);

            var failure = await Should.ThrowAsync<DbUpdateException>(
                () => context.SaveChangesAsync(Token));
            var postgres = failure.InnerException.ShouldBeOfType<PostgresException>();
            postgres.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            postgres.MessageText.ShouldContain("row-level security");
        }

        await using (var victim = await Fixture.BeginTenantScopeAsync(orgB, cancellationToken: Token))
        {
            await using var count = victim.Command("select count(*)::int from public.project where id = @id");
            count.Parameters.AddWithValue("id", forgedId);
            (await count.ExecuteScalarAsync(Token)).ShouldBe(0);
        }
    }
}
