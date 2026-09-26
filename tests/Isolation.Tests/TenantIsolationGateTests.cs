using System.Globalization;
using Evidgate.Modules.Tenancy.Contracts;
using Evidgate.Testing;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// 01.6.1-T4.2, AC5: the isolation suite is a real gate, not a tautology. An
/// assertion that can never observe a foreign row is not a check; this meta-test
/// plants a violation and asserts the same probes the suite's tests use actually
/// detect the leak.
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class TenantIsolationGateTests : HarnessTest
{
    /// <summary>Binds the test to the shared harness fixture (container, migrations, Respawn).</summary>
    public TenantIsolationGateTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }


    /// <summary>
    /// The violation is planted at the tenant context, not at the table: inside one
    /// transaction <c>evidgate.org_id</c> is set to the <em>victim</em> org — exactly
    /// what a caller-supplied org id produces when a scope is opened from message
    /// input instead of the authenticated principal (the bug class the C3 tenant
    /// scope exists to stop). <c>set_config(…, true)</c> is transaction-local, so
    /// the plant is safe even while other collections run: nothing global is
    /// touched, the scope is disposed without a commit, and leg 3 proves the guard
    /// is back.
    /// <para>
    /// Rejected alternative: <c>alter table … disable row level security</c> — it is
    /// transactional DDL but is visible to <em>every other session</em> from the
    /// moment it runs, so it would flake the tests of this assembly's other
    /// collection that share nothing but the server.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Isolation_PlantedViolation_Fails()
    {
        var (orgA, _) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var (orgB, projectB) = await Fixture.SeedProjectAsync(cancellationToken: Token);

        // 1. Baseline: with the tenant context intact the probes see org A only —
        //    they are the probes of TenantIsolationTests, not look-alikes.
        await using (var scope = await Fixture.BeginTenantScopeAsync(orgA, cancellationToken: Token))
        {
            (await scope.Db.QuerySingleOrDefaultAsync<ProjectDto>(
                IsolationProbeSql.ProjectById,
                new { project_id = projectB },
                Token))
                .ShouldBeNull();
        }

        // 2. Planted violation: the scope still *names* org A, but the GUC inside its
        //    transaction points at org B. Both probes must now see the foreign row —
        //    i.e. they would fail Isolation_DapperRead_OtherOrg_ReturnsEmpty and
        //    Isolation_EfRead_OtherOrg_ReturnsEmpty, which is what "the suite gates"
        //    means.
        await using (var forged = await Fixture.BeginTenantScopeAsync(orgA, cancellationToken: Token))
        {
            await using (var plant = forged.Command("select set_config(@key, @value, true)"))
            {
                plant.Parameters.AddWithValue("key", "evidgate.org_id");
                plant.Parameters.AddWithValue("value", orgB.ToString("D", CultureInfo.InvariantCulture));
                await plant.ExecuteScalarAsync(Token);
            }

            (await forged.Db.QuerySingleOrDefaultAsync<ProjectDto>(
                IsolationProbeSql.ProjectById,
                new { project_id = projectB },
                Token))
                .ShouldNotBeNull()
                .OrgId.ShouldBe(orgB);

            await using var context = IsolationProbe.CreateContext(forged, Fixture.Interceptor);
            (await context.Projects.SingleOrDefaultAsync(p => p.Id == projectB, Token)).ShouldNotBeNull();
        }

        // 3. The plant never escaped its transaction: a fresh org-A scope is blind again.
        await using (var scope = await Fixture.BeginTenantScopeAsync(orgA, cancellationToken: Token))
        {
            (await scope.Db.QuerySingleOrDefaultAsync<ProjectDto>(
                IsolationProbeSql.ProjectById,
                new { project_id = projectB },
                Token))
                .ShouldBeNull();
        }
    }
}
