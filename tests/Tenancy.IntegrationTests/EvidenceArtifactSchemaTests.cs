using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.1.1-T1.1/T1.2 through a real PostgreSQL: the migration's <c>artifact</c> and <c>artifact_kind_policy</c> tables
/// with FORCE RLS, the closed-list CHECK constraints, the tenant keys and the composite FK, the app-role grants, and
/// the EF write model built through the production path (00 §5.5 C4, §7; TL T2, T3, T17, T19, T20).
/// <para>
/// The fixture is this project's own <see cref="PostgresTenancyFixture"/> — one digest-pinned PostgreSQL 18 container
/// per assembly, a database brought to head by the real <c>Evidgate.Migrator</c> — which is the
/// precedent for a new table in this suite (<c>PolicyRevisionImmutabilityTests</c>). The Respawn harness in
/// <c>tests/Shared/DatabaseFixture.cs</c> is not linked into this project (its csproj links only
/// <c>tests/Shared/ContainerImages.cs</c>), so no new fixture was added.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class EvidenceArtifactSchemaTests : IClassFixture<TenancyHostPool>
{
    /// <summary>A well-formed execution key: sha256 hex (00 §7).</summary>
    private const string ExecutionKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>A well-formed declared content address, <c>sha256:&lt;64 hex&gt;</c>.</summary>
    private const string Checksum = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;
    public EvidenceArtifactSchemaTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>An org and a project on the shared database, created through the real registry routes.</summary>
    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = _hosts.RealResolver(database);
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    /// <summary>The 06.1.1 §1/AC2 final object key for one row's own identity (FR-06-1).</summary>
    private static string FinalKey(Guid org, Guid project, Guid runId, string executionKey, int attemptNo, Guid id) =>
        $"org/{org}/project/{project}/run/{runId}/attempt/{executionKey}/{attemptNo}/{id}";

    /// <summary>The 06.1.2 §3 staging key for one row's own identity (FR-06-2).</summary>
    private static string StagingKey(Guid org, Guid id) => $"pending/{org}/{id}";

    /// <summary>
    /// One raw insert of an artifact row as the app role. The object key defaults to the row's own
    /// final key, so the row satisfies the 0028 shape CHECK; every closed-list and bounded column is
    /// overridable for the negative cases.
    /// </summary>
    private static string InsertArtifactSql(
        Guid org,
        Guid project,
        Guid id,
        Guid runId,
        string? objectKey = null,
        string executionKey = ExecutionKey,
        int attemptNo = 1,
        string kind = ArtifactKind.Trace,
        string mediaType = "application/zip",
        string checksum = Checksum,
        string sensitivity = ArtifactSensitivity.Normal,
        string scanState = ArtifactScanState.Pending,
        string state = ArtifactState.PendingUpload,
        long? bytes = null,
        string? failureCode = null) =>
        "insert into public.artifact "
        + "(id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type, bytes, checksum, sensitivity, scan_state, state, failure_code) "
        + $"values ('{id}', '{org}', '{project}', '{runId}', '{executionKey}', {attemptNo}, "
        + $"'{kind}', '{objectKey ?? FinalKey(org, project, runId, executionKey, attemptNo, id)}', '{mediaType}', "
        + $"{(bytes is null ? "null" : bytes.Value.ToString(CultureInfo.InvariantCulture))}, '{checksum}', '{sensitivity}', '{scanState}', '{state}', "
        + $"{(failureCode is null ? "null" : $"'{failureCode}'")})";

    /// <summary>One raw insert of a kind-policy row as the app role.</summary>
    private static string InsertPolicySql(Guid org, Guid project, string kind, string sensitivity) =>
        "insert into public.artifact_kind_policy "
        + "(id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity) "
        + $"values ('{Guid.CreateVersion7()}', '{org}', '{project}', '{kind}', 1024, array['application/zip'], '{sensitivity}')";

    /// <summary>One committed artifact row for <paramref name="world"/>'s org.</summary>
    private async Task InsertArtifactAsync(World world, Guid id, Guid runId)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(InsertArtifactSql(world.Org, world.Project, id, runId))).ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>One committed run row for <paramref name="world"/>'s org (0031's composite run key).</summary>
    private async Task<Guid> CreateRunAsync(World world) =>
        await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);

    /// <summary>One committed run row for <paramref name="org"/> (0031's composite run key).</summary>
    private async Task<Guid> CreateRunAsync(World world, Guid org) =>
        await RunRowSeeder.SeedRunAsync(_fixture, world.Database, org);

    /// <summary>One committed kind-policy row for <paramref name="world"/>'s project.</summary>
    private async Task InsertKindPolicyAsync(World world, string kind = ArtifactKind.Trace)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(InsertPolicySql(world.Org, world.Project, kind, ArtifactSensitivity.Normal))).ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>An org and a project in it, created through the real routes.</summary>
    private static async Task<(Guid Org, Guid Project)> CreateOtherTenantAsync(World world, string slug)
    {
        var user = Guid.CreateVersion7();
        var unique = Unique.Slug(slug);
        var org = await CreateOrgAsync(world.Client, user, unique);
        var project = await CreateProjectAsync(world.Client, user, org, unique + "-project");

        return (org, project);
    }

    // ------------------------------------------------------------------ (a) migration: RLS flags

    [Fact]
    public async Task Artifact_AndKindPolicy_HaveForcedRls()
    {
        var database = await _fixture.SharedDatabaseAsync();

        foreach (var table in new[] { "artifact", "artifact_kind_policy" })
        {
            (await _fixture.TableExistsAsync(database, table)).ShouldBeTrue(table);

            var rls = await _fixture.RowSecurityAsync(database, table);
            rls.Enabled.ShouldBeTrue(table);
            rls.Forced.ShouldBeTrue(table);
        }
    }

    // ------------------------------------------------------------------ (b) AC3: unique (org_id, object_key)

    [Fact]
    public async Task SecondRow_SameKey_SameOrg_Fails()
    {
        await using var world = await StartAsync();
        var id = Guid.CreateVersion7();
        var runId = await CreateRunAsync(world);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.ExecuteAsync(InsertArtifactSql(world.Org, world.Project, id, runId))).ShouldBe(1);
        // The shape CHECK pins object_key to the row's own id, so a second row with the same key is
        // also the same id: the primary key and `unique (org_id, object_key)` both answer 23505.
        (await scope.CaptureErrorAsync(InsertArtifactSql(world.Org, world.Project, id, runId))).SqlState.ShouldBe("23505");

        // Because the shape CHECK makes a duplicate key require the same id, the 23505 above can come
        // from artifact_pkey alone: the AC3 constraint is therefore proven from the catalogue, on
        // exactly (org_id, object_key) in that order, not from the insert.
        (await _fixture.ConstraintsAsync(world.Database, "artifact")).ShouldContain(
            "u:artifact_org_object_key_key:UNIQUE (org_id, object_key)",
            "artifact needs unique (org_id, object_key) as artifact_org_object_key_key (AC3)");

        // artifact_kind_policy's own uniqueness has no insert test either, so it is proven the same
        // way: unique (org_id, project_id, kind) in that order.
        (await _fixture.ConstraintsAsync(world.Database, "artifact_kind_policy")).ShouldContain(
            "u:artifact_kind_policy_project_kind_key:UNIQUE (org_id, project_id, kind)",
            "artifact_kind_policy needs unique (org_id, project_id, kind) as artifact_kind_policy_project_kind_key");
    }

    [Fact]
    public async Task SameKeyShape_OtherOrg_Succeeds()
    {
        await using var world = await StartAsync();
        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "beta");

        await InsertArtifactAsync(world, Guid.CreateVersion7(), await CreateRunAsync(world));

        // The key embeds the org, so "the same key shape in another org" is a key minted for that
        // org: `unique (org_id, object_key)` is per org and both rows commit.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), otherOrg))
        {
            (await scope.ExecuteAsync(InsertArtifactSql(otherOrg, otherProject, Guid.CreateVersion7(), await CreateRunAsync(world, otherOrg)))).ShouldBe(1);
            await scope.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.artifact where org_id = '{world.Org}'")).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.artifact where org_id = '{otherOrg}'")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ (c) AC4: unique (org_id, id)

    [Fact]
    public async Task TenantKey_UniqueOrgIdId_Exists()
    {
        var database = await _fixture.SharedDatabaseAsync();

        foreach (var (table, name) in new[] { ("artifact", "artifact_org_id_key"), ("artifact_kind_policy", "artifact_kind_policy_org_id_key") })
        {
            var constraints = await _fixture.ConstraintsAsync(database, table);

            constraints.ShouldContain(
                constraint => constraint.StartsWith($"u:{name}:", StringComparison.Ordinal)
                    && constraint.Contains("org_id, id", StringComparison.Ordinal),
                $"{table} needs unique (org_id, id) as {name} (TL T3)");
        }
    }

    // ------------------------------------------------------------------ (d) AC5: RLS isolation

    [Fact]
    public async Task Select_WithoutTenantContext_ReturnsZero()
    {
        await using var world = await StartAsync();
        await InsertArtifactAsync(world, Guid.CreateVersion7(), await CreateRunAsync(world));

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database));

        (await scope.ScalarAsync<bool>("select evidgate.current_org_id() is null")).ShouldBeTrue();
        (await scope.ScalarAsync<long>("select count(*) from public.artifact")).ShouldBe(0);
        (await scope.ScalarAsync<long>("select count(*) from public.artifact_kind_policy")).ShouldBe(0);
    }

    [Fact]
    public async Task Select_OtherOrg_ReturnsZero()
    {
        await using var world = await StartAsync();
        await InsertArtifactAsync(world, Guid.CreateVersion7(), await CreateRunAsync(world));

        await using var scope = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(world.Database),
            Guid.CreateVersion7());

        (await scope.ScalarAsync<long>("select count(*) from public.artifact")).ShouldBe(0);
        (await scope.ScalarAsync<long>("select count(*) from public.artifact_kind_policy")).ShouldBe(0);
    }

    // ------------------------------------------------------------------ (d2) F1: the operator role has no policy

    [Fact]
    public async Task Operator_WithTenantSet_ReadsZeroRows()
    {
        await using var world = await StartAsync();
        await InsertArtifactAsync(world, Guid.CreateVersion7(), await CreateRunAsync(world));
        await InsertKindPolicyAsync(world);

        // The operator holds the SELECT grant but no policy on either table (TL T76, D1): with FORCE
        // row level security and `evidgate.org_id` set to the row's own org, the read is still zero.
        await using var scope = await TenantScope.BeginAsync(_fixture.OperatorConnectionString(world.Database), world.Org);

        (await scope.ScalarAsync<Guid>("select evidgate.current_org_id()")).ShouldBe(world.Org);
        (await scope.ScalarAsync<long>("select count(*) from public.artifact")).ShouldBe(0);
        (await scope.ScalarAsync<long>("select count(*) from public.artifact_kind_policy")).ShouldBe(0);
    }

    [Fact]
    public async Task TenantPolicies_AreScopedToTheAppRole()
    {
        var database = await _fixture.SharedDatabaseAsync();

        // A policy with no `to` clause applies to PUBLIC; the tenant policies must name evidgate_app
        // only, so the operator's SELECT grant reads zero rows (F1).
        (await _fixture.PoliciesAsync(database, "artifact")).ShouldBe(
        [
            "artifact_owner_backfill:evidgate_owner:*:true:true",
            "artifact_tenant:evidgate_app:*:(org_id = evidgate.current_org_id()):(org_id = evidgate.current_org_id())",
        ]);
        (await _fixture.PoliciesAsync(database, "artifact_kind_policy")).ShouldBe(
        [
            "artifact_kind_policy_owner_backfill:evidgate_owner:*:true:true",
            "artifact_kind_policy_tenant:evidgate_app:*:(org_id = evidgate.current_org_id()):(org_id = evidgate.current_org_id())",
        ]);
    }
}
