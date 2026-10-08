using System.Globalization;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// 02.9.1-T3.1 (story 02.9.1 §2 AC3, §5 "organization scope"; 00 §5.4.1, TL T4):
/// <c>public.organization</c> is the root of the tenant hierarchy and the one business table
/// without an <c>org_id</c> column, so its policy compares the row's own <c>id</c> to the tenant
/// setting (0004). This suite proves the read side of that policy: an org sees itself and nothing
/// else, and a session with no tenant setting sees nothing at all.
/// <para>
/// The two orgs are seeded as <c>evidgate_app</c> inside a transaction that is rolled back, so the
/// suite leaves no row behind and exercises the same policy the API's <c>POST /v1/orgs</c> path
/// uses (0004: the setting must already point at the new org id before the insert).
/// </para>
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class OrganizationTests : HarnessTest
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid OrgB = Guid.Parse("22222222-2222-7222-8222-222222222222");

    /// <summary>Binds the test to the shared harness fixture (container, migrations, Respawn).</summary>
    public OrganizationTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// AC3: under org A's tenant setting the only visible organization is org A; under org B's it
    /// is org B; with the setting reset to its default, blank, or never set at all, none is
    /// visible. The seeded rows make the two positive assertions non-vacuous, and the negative
    /// ones are asserted in the same transaction that holds them.
    /// </summary>
    [Fact]
    public async Task OnlyCurrentOrgVisible()
    {
        await using var scope = await TenantScope.BeginAsync(Fixture.AppConnectionString, OrgA);

        // The policy's WITH CHECK requires id = current_org_id(), so each org is seeded under its
        // own setting — the same order POST /v1/orgs must use (0004).
        await InsertOrgAsync(scope, OrgA, "Org A");
        await scope.SetAsync(RlsTableCatalog.TenantSetting, OrgB.ToString("D", CultureInfo.InvariantCulture));
        await InsertOrgAsync(scope, OrgB, "Org B");

        await scope.SetAsync(RlsTableCatalog.TenantSetting, OrgA.ToString("D", CultureInfo.InvariantCulture));
        (await VisibleOrgsAsync(scope)).ShouldBe(
            [OrgA],
            $"under org {OrgA}'s tenant setting only org {OrgA} may be visible");

        await scope.SetAsync(RlsTableCatalog.TenantSetting, OrgB.ToString("D", CultureInfo.InvariantCulture));
        (await VisibleOrgsAsync(scope)).ShouldBe(
            [OrgB],
            $"under org {OrgB}'s tenant setting only org {OrgB} may be visible");

        // The setting reset to its default: the fail-closed "no tenant" read, with both rows
        // present in the same transaction (so the empty result is the policy, not an empty table).
        await RlsTableCatalog.ResetTenantAsync(scope);
        (await VisibleOrgsAsync(scope)).ShouldBeEmpty(
            "with the tenant setting reset to its default no organization may be visible");

        // The setting present but blank: NULLIF(…, '') must map it to NULL, not to a tenant.
        await scope.SetAsync(RlsTableCatalog.TenantSetting, string.Empty);
        (await VisibleOrgsAsync(scope)).ShouldBeEmpty(
            "with a blank tenant setting no organization may be visible");

        // A session that never set the setting at all: the helper is NULL and the read is
        // fail-closed. The rows above are uncommitted, so this scope cannot see them either way —
        // what it proves is the helper's own fail-closed default.
        await using var fresh = await TenantScope.BeginAsync(Fixture.AppConnectionString);
        (await fresh.ScalarAsync<Guid?>("select evidgate.current_org_id()")).ShouldBeNull(
            "a session that never set evidgate.org_id must read a NULL tenant, never a default org");
        (await VisibleOrgsAsync(fresh)).ShouldBeEmpty(
            "a session with no tenant setting must see no organization");
    }

    private static async Task InsertOrgAsync(TenantScope scope, Guid id, string name) =>
        await scope.ExecuteAsync(
            "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
            ("id", id),
            ("name", name),
            ("slug", "org-" + id.ToString("N", CultureInfo.InvariantCulture)[..12]));

    private static async Task<IReadOnlyList<Guid>> VisibleOrgsAsync(TenantScope scope) =>
        await scope.ScalarAsync<Guid[]>(
            "select coalesce(array_agg(id order by id), '{}'::uuid[]) from public.organization");
}
