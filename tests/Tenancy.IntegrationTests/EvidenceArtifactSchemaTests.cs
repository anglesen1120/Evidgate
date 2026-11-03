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
public sealed class EvidenceArtifactSchemaTests
{
    /// <summary>A well-formed execution key: sha256 hex (00 §7).</summary>
    private const string ExecutionKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>A well-formed declared content address, <c>sha256:&lt;64 hex&gt;</c>.</summary>
    private const string Checksum = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly PostgresTenancyFixture _fixture;

    public EvidenceArtifactSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

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
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
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

    // ------------------------------------------------------------------ (e) AC6: the closed lists

    [Fact]
    public async Task Kind_NotInList_FailsCheck()
    {
        await using var world = await StartAsync();
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.CaptureErrorAsync(
            InsertArtifactSql(world.Org, world.Project, Guid.CreateVersion7(), Guid.CreateVersion7(), kind: "bogus"))).SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task ScanState_Invalid_FailsCheck()
    {
        await using var world = await StartAsync();
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.CaptureErrorAsync(
            InsertArtifactSql(world.Org, world.Project, Guid.CreateVersion7(), Guid.CreateVersion7(), scanState: "bogus"))).SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task Sensitivity_NotInList_FailsCheck()
    {
        await using var world = await StartAsync();
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.CaptureErrorAsync(
            InsertArtifactSql(world.Org, world.Project, Guid.CreateVersion7(), Guid.CreateVersion7(), sensitivity: "bogus"))).SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task State_Invalid_FailsCheck()
    {
        await using var world = await StartAsync();
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.CaptureErrorAsync(
            InsertArtifactSql(world.Org, world.Project, Guid.CreateVersion7(), Guid.CreateVersion7(), state: "bogus"))).SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task KindPolicy_KindAndSensitivity_OutsideTheirLists_FailCheck()
    {
        await using var world = await StartAsync();

        // One statement per transaction: a rejected statement aborts the transaction, so a second
        // statement in the same scope would fail with 25P02 instead of the CHECK violation.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.CaptureErrorAsync(InsertPolicySql(world.Org, world.Project, "bogus", ArtifactSensitivity.Normal)))
                .SqlState.ShouldBe("23514");
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.CaptureErrorAsync(InsertPolicySql(world.Org, world.Project, ArtifactKind.Trace, "bogus")))
                .SqlState.ShouldBe("23514");
        }
    }

    // ------------------------------------------------------------------ (f) the composite FK

    [Fact]
    public async Task ProjectId_OtherOrg_ViolatesFk()
    {
        await using var world = await StartAsync();
        var (_, otherProject) = await CreateOtherTenantAsync(world, "beta");

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        // The key is minted for the row's own project, so the shape CHECK passes and only the
        // composite project FK can reject the row.
        (await scope.CaptureErrorAsync(
            InsertArtifactSql(world.Org, otherProject, Guid.CreateVersion7(), Guid.CreateVersion7()))).SqlState.ShouldBe("23503");
    }

    // ------------------------------------------------------------------ (f2) F2/F4: the bound CHECKs and the key shape

    [Fact]
    public async Task ExecutionKey_NotLowercaseHex_FailsCheck()
    {
        await using var world = await StartAsync();
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        // Uppercase hex is outside `^[0-9a-f]{64}$`; the key is built from the same value, so only
        // the execution_key CHECK can reject the row.
        (await scope.CaptureErrorAsync(
            InsertArtifactSql(world.Org, world.Project, Guid.CreateVersion7(), Guid.CreateVersion7(), executionKey: new string('A', 64))))
            .SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task ExecutionKey_WithPathSeparator_FailsCheck()
    {
        await using var world = await StartAsync();
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.CaptureErrorAsync(
            InsertArtifactSql(world.Org, world.Project, Guid.CreateVersion7(), Guid.CreateVersion7(), executionKey: "../../etc/passwd")))
            .SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task Checksum_Malformed_FailsCheck()
    {
        await using var world = await StartAsync();
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.CaptureErrorAsync(
            InsertArtifactSql(world.Org, world.Project, Guid.CreateVersion7(), Guid.CreateVersion7(), checksum: "sha256:nothex")))
            .SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task ObjectKey_OtherOrgPrefix_FailsShapeCheck()
    {
        await using var world = await StartAsync();
        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "beta");
        var id = Guid.CreateVersion7();
        var runId = await CreateRunAsync(world);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        var error = await scope.CaptureErrorAsync(
            InsertArtifactSql(world.Org, world.Project, id, runId, objectKey: FinalKey(otherOrg, otherProject, runId, ExecutionKey, 1, id)));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("artifact_object_key_shape_check");
    }

    [Fact]
    public async Task ObjectKey_BothDocumentedForms_AreAccepted()
    {
        await using var world = await StartAsync();
        var id = Guid.CreateVersion7();
        var runId = await CreateRunAsync(world);
        var stagingId = Guid.CreateVersion7();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        // The final 06.1.1 §1/AC2 key (the default) and the 06.1.2 §3 staging key both commit.
        (await scope.ExecuteAsync(InsertArtifactSql(world.Org, world.Project, id, runId))).ShouldBe(1);
        (await scope.ExecuteAsync(
            InsertArtifactSql(world.Org, world.Project, stagingId, await CreateRunAsync(world), objectKey: StagingKey(world.Org, stagingId)))).ShouldBe(1);
        await scope.CommitAsync();
    }

    [Theory]
    [InlineData(0, "application/zip", null, null)]
    [InlineData(1, "", null, null)]
    [InlineData(1, "application/zip", 0L, null)]
    [InlineData(1, "application/zip", null, "")]
    public async Task Artifact_BoundChecks_RejectOutOfRange(int attemptNo, string mediaType, long? bytes, string? failureCode)
    {
        await using var world = await StartAsync();
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.CaptureErrorAsync(InsertArtifactSql(
            world.Org, world.Project, Guid.CreateVersion7(), Guid.CreateVersion7(),
            attemptNo: attemptNo, mediaType: mediaType, bytes: bytes, failureCode: failureCode))).SqlState.ShouldBe("23514");
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task KindPolicy_MaxBytesNotPositive_FailsCheck(long maxBytes)
    {
        await using var world = await StartAsync();
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.CaptureErrorAsync(
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity) "
            + $"values ('{Guid.CreateVersion7()}', '{world.Org}', '{world.Project}', 'trace', {maxBytes}, array['application/zip'], 'normal')"))
            .SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task KindPolicy_EmptyMediaTypes_FailsCheck()
    {
        await using var world = await StartAsync();
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.CaptureErrorAsync(
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity) "
            + $"values ('{Guid.CreateVersion7()}', '{world.Org}', '{world.Project}', 'trace', 1024, array[]::text[], 'normal')"))
            .SqlState.ShouldBe("23514");
    }

    // ------------------------------------------------------------------ (g) T1.1: indexes and grants

    [Fact]
    public async Task ArtifactIndexes_ExistInPgIndexes()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var indexes = await _fixture.IndexesAsync(database, "artifact");

        indexes.ShouldContain(
            index => index.StartsWith("artifact_run_idx:", StringComparison.Ordinal)
                && index.Contains("(org_id, run_id)", StringComparison.Ordinal)
                && index.Contains("WHERE", StringComparison.Ordinal),
            "artifact_run_idx must be the partial (org_id, run_id) index of 06.1.1 §4");
        indexes.ShouldContain(
            index => index.StartsWith("artifact_expiry_idx:", StringComparison.Ordinal)
                && index.Contains("(expires_at)", StringComparison.Ordinal)
                && index.Contains("WHERE", StringComparison.Ordinal),
            "artifact_expiry_idx must be the partial (expires_at) index of 06.1.1 §4");
        indexes.ShouldContain(
            index => index.StartsWith("artifact_project_idx:", StringComparison.Ordinal)
                && index.Contains("(org_id, project_id)", StringComparison.Ordinal),
            "artifact_project_idx must serve the composite project FK (0024 precedent)");
    }

    [Fact]
    public async Task AppRole_HasNoDelete_AndOnlyTheContractUpdateColumns()
    {
        var database = await _fixture.SharedDatabaseAsync();

        foreach (var table in new[] { "artifact", "artifact_kind_policy" })
        {
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "SELECT")).ShouldBeTrue(table);
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "INSERT")).ShouldBeTrue(table);
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "DELETE")).ShouldBeFalse(table);
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "TRUNCATE")).ShouldBeFalse(table);
        }

        (await UpdateColumnsAsync(database, "artifact")).ShouldBe(
            ["attempt_id", "bytes", "expires_at", "failure_code", "scan_state", "state"]);
        (await UpdateColumnsAsync(database, "artifact_kind_policy")).ShouldBe(
            ["capture_enabled", "default_sensitivity", "max_bytes", "media_types", "reject_on_secret"]);
    }

    /// <summary>The columns <c>evidgate_app</c> may UPDATE on <paramref name="table"/>, in name order.</summary>
    private async Task<IReadOnlyList<string>> UpdateColumnsAsync(string database, string table) =>
        await _fixture.ColumnAsync<string>(
            database,
            $"""
            select column_name
              from information_schema.columns
             where table_schema = 'public' and table_name = '{table}'
               and has_column_privilege('evidgate_app', 'public.{table}', column_name, 'UPDATE')
             order by column_name
            """);

    // ------------------------------------------------------------------ (h) the EF write model

    [Fact]
    public async Task Insert_WithoutStateOrScanState_UsesDefaults()
    {
        await using var world = await StartAsync();

        // The migration declares both defaults; the EF insert below relies on them.
        var columns = await _fixture.ColumnsAsync(world.Database, "artifact");
        columns.ShouldContain(column => column.StartsWith("state:", StringComparison.Ordinal)
            && column.EndsWith(":'pending_upload'::text", StringComparison.Ordinal));
        columns.ShouldContain(column => column.StartsWith("scan_state:", StringComparison.Ordinal)
            && column.EndsWith(":'pending'::text", StringComparison.Ordinal));

        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var factory = services.ServiceProvider.GetRequiredService<ITenantDbContextFactory<EvidenceDbContext>>();

        var id = Guid.CreateVersion7();
        var runId = await CreateRunAsync(world);
        var artifact = new Artifact(
            id,
            world.Org,
            world.Project,
            runId,
            ExecutionKey,
            1,
            ArtifactKind.Trace,
            FinalKey(world.Org, world.Project, runId, ExecutionKey, 1, id),
            "application/zip",
            Checksum,
            ArtifactSensitivity.Normal);

        await using (var context = factory.Create())
        {
            await context.Artifacts.AddAsync(artifact, TestContext.Current.CancellationToken);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = factory.Create())
        {
            var read = await context.Artifacts.SingleAsync(
                row => row.Id == artifact.Id, TestContext.Current.CancellationToken);

            read.State.ShouldBe(ArtifactState.PendingUpload);
            read.ScanState.ShouldBe(ArtifactScanState.Pending);
            read.AttemptId.ShouldBeNull();
            read.Version.ShouldBe(1);
            read.ObjectKey.ShouldBe(artifact.ObjectKey);
            read.Kind.ShouldBe(ArtifactKind.Trace);
        }

        await scope.CommitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WriteModel_MapsTheContractColumns()
    {
        await using var world = await StartAsync();

        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, true, TestContext.Current.CancellationToken);
        await using var context = services.ServiceProvider.GetRequiredService<ITenantDbContextFactory<EvidenceDbContext>>().Create();

        var artifact = context.Model.FindEntityType(typeof(Artifact)).ShouldNotBeNull();
        artifact.GetTableName().ShouldBe("artifact");
        artifact.GetSchema().ShouldBe("public");
        artifact.GetProperties().Select(property => property.GetColumnName()).ShouldBe(
            [
                "id", "org_id", "project_id", "run_id", "execution_key", "attempt_no", "attempt_id", "kind",
                "object_key", "media_type", "bytes", "checksum", "sensitivity", "scan_state", "state",
                "expires_at", "failure_code", "version", "created_at", "updated_at",
            ],
            ignoreOrder: true);
        artifact.FindProperty(nameof(Artifact.Version)).ShouldNotBeNull().IsConcurrencyToken.ShouldBeTrue();

        var policy = context.Model.FindEntityType(typeof(ArtifactKindPolicy)).ShouldNotBeNull();
        policy.GetTableName().ShouldBe("artifact_kind_policy");
        policy.GetSchema().ShouldBe("public");
        policy.FindProperty(nameof(ArtifactKindPolicy.MediaTypes)).ShouldNotBeNull().GetColumnType().ShouldBe("text[]");
        policy.FindProperty(nameof(ArtifactKindPolicy.Version)).ShouldNotBeNull().IsConcurrencyToken.ShouldBeTrue();

        // The mapped columns must equal the physical columns of the migrated tables, or the model has drifted.
        await AssertColumnSetAsync(artifact, world.Database, "artifact");
        await AssertColumnSetAsync(policy, world.Database, "artifact_kind_policy");
    }

    /// <summary>The mapped column names must equal the physical columns of the migrated table.</summary>
    private async Task AssertColumnSetAsync(
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entityType,
        string database,
        string table)
    {
        entityType.GetProperties()
            .Select(property => property.GetColumnName())
            .Order(StringComparer.Ordinal)
            .ShouldBe(
                (await _fixture.ColumnsAsync(database, table))
                    .Select(column => column.Split(':')[0])
                    .Order(StringComparer.Ordinal));
    }
}
