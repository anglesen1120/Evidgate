using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The tenant isolation of <c>public.retention_policy</c> at the database boundary: another org's row is invisible
/// through a tenant scope, a row naming another org cannot be written, a scope with no tenant context reads nothing,
/// and the migration's posture (ENABLE + FORCE row level security, a policy for the app role, no DELETE grant) is what
/// the schema-convention scan expects of every business table.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RetentionPolicyRlsTests
{
    private const string Insert =
        "insert into public.retention_policy (id, org_id, project_id, artifact_days, metadata_days) "
        + "values (@id, @org_id, @project_id, @artifact_days, @metadata_days)";

    private readonly PostgresTenancyFixture _fixture;

    public RetentionPolicyRlsTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A stored policy is readable through its own org's scope and invisible through another org's — the same
    /// statement, only the tenant context changes.
    /// </summary>
    [Fact]
    public async Task RetentionPolicy_OtherOrgScope_ReadsNoRow()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database);

        await using (var write = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            (await write.ExecuteAsync(
                Insert,
                ("id", Guid.CreateVersion7()),
                ("org_id", org),
                ("project_id", project),
                ("artifact_days", 14),
                ("metadata_days", 90))).ShouldBe(1);
            await write.CommitAsync();
        }

        await using (var own = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            (await own.ScalarAsync<long>(
                "select count(*) from public.retention_policy where project_id = @project_id",
                ("project_id", project))).ShouldBe(1L);
        }

        await using (var foreign = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), Guid.CreateVersion7()))
        {
            (await foreign.ScalarAsync<long>(
                "select count(*) from public.retention_policy where project_id = @project_id",
                ("project_id", project))).ShouldBe(0L);
        }
    }

    /// <summary>
    /// A row that names another org is refused by the policy's WITH CHECK, so a tenant cannot plant a policy the
    /// other tenant would then be judged by.
    /// </summary>
    [Fact]
    public async Task RetentionPolicy_InsertForAnotherOrg_IsRefused()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (ownOrg, _) = await SeedTenantAsync(database);
        var (otherOrg, otherProject) = await SeedTenantAsync(database);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), ownOrg);

        var error = await scope.CaptureErrorAsync(
            Insert,
            ("id", Guid.CreateVersion7()),
            ("org_id", otherOrg),
            ("project_id", otherProject),
            ("artifact_days", 14),
            ("metadata_days", 90));

        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    /// <summary>A scope with no tenant context reads nothing, rather than every tenant's rows.</summary>
    [Fact]
    public async Task RetentionPolicy_WithoutTenantContext_ReadsNothing()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database);

        await using (var write = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            (await write.ExecuteAsync(
                Insert,
                ("id", Guid.CreateVersion7()),
                ("org_id", org),
                ("project_id", project),
                ("artifact_days", 14),
                ("metadata_days", 90))).ShouldBe(1);
            await write.CommitAsync();
        }

        await using var anonymous = await TenantScope.BeginAsync(_fixture.AppConnectionString(database));

        (await anonymous.ScalarAsync<long>("select count(*) from public.retention_policy")).ShouldBe(0L);
    }

    /// <summary>
    /// The table's isolation posture: row level security is on and forced, the app role's policy is the tenant
    /// predicate, and the app role holds no DELETE — the row can be replaced, never removed.
    /// </summary>
    [Fact]
    public async Task RetentionPolicy_Table_IsTenantIsolated()
    {
        var database = await _fixture.SharedDatabaseAsync();

        var (enabled, forced) = await _fixture.RowSecurityAsync(database, "retention_policy");
        enabled.ShouldBeTrue();
        forced.ShouldBeTrue();

        var policies = await _fixture.PoliciesAsync(database, "retention_policy");
        policies.ShouldContain(policy =>
            policy.StartsWith("retention_policy_app_tenant:evidgate_app:", StringComparison.Ordinal)
            && policy.Contains("current_org_id()", StringComparison.Ordinal));

        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "retention_policy", "SELECT")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "retention_policy", "DELETE")).ShouldBeFalse();

        var names = await _fixture.ConstraintNamesAsync(database, "retention_policy");
        names.ShouldContain("u:retention_policy_org_id_key");
        names.ShouldContain("u:retention_policy_org_project_key");
        names.ShouldContain("f:retention_policy_project_fk");
        names.ShouldContain("c:retention_policy_sane_check");
    }

    /// <summary>One org and one project, created through the real registry routes.</summary>
    private async Task<(Guid Org, Guid Project)> SeedTenantAsync(string database)
    {
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("retention-rls"));

        return (org, await CreateProjectAsync(client, user, org, Unique.Slug("retention-rls-project")));
    }
}
