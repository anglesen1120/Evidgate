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
/// per assembly, a fresh database per test brought to head by the real <c>Evidgate.Migrator</c> — which is the
/// precedent for a new table in this suite (<c>PolicyRevisionImmutabilityTests</c>). The Respawn harness in
/// <c>tests/Shared/DatabaseFixture.cs</c> is not linked into this project (its csproj links only
/// <c>tests/Shared/ContainerImages.cs</c>), so no new fixture was added.
/// </para>
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class EvidenceArtifactSchemaTests
{
    /// <summary>A well-formed execution key: sha256 hex (00 §7).</summary>
    private const string ExecutionKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>A well-formed declared content address, <c>sha256:&lt;64 hex&gt;</c>.</summary>
    private const string Checksum = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly PostgresTenancyFixture _fixture;

    public EvidenceArtifactSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>One migrated database with an org and a project, created through the real registry routes.</summary>
    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    /// <summary>An object key of the 06.1.1 §2 shape, minted for <paramref name="world"/>'s org and project.</summary>
    private static string Key(World world, string executionKey, int attemptNo) =>
        $"org/{world.Org}/project/{world.Project}/run/{Guid.CreateVersion7()}/attempt/{executionKey}/{attemptNo}/{Guid.CreateVersion7()}";

    /// <summary>One raw insert of an artifact row as the app role, with every closed-list column overridable.</summary>
    private static string InsertArtifactSql(
        Guid org,
        Guid project,
        string objectKey,
        string kind = ArtifactKind.Trace,
        string sensitivity = ArtifactSensitivity.Normal,
        string scanState = ArtifactScanState.Pending,
        string state = ArtifactState.PendingUpload) =>
        "insert into public.artifact "
        + "(id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type, checksum, sensitivity, scan_state, state) "
        + $"values ('{Guid.CreateVersion7()}', '{org}', '{project}', '{Guid.CreateVersion7()}', '{ExecutionKey}', 1, "
        + $"'{kind}', '{objectKey}', 'application/zip', '{Checksum}', '{sensitivity}', '{scanState}', '{state}')";

    /// <summary>One committed artifact row for <paramref name="world"/>'s org.</summary>
    private async Task InsertArtifactAsync(World world, string objectKey)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(InsertArtifactSql(world.Org, world.Project, objectKey))).ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>An org and a project in it, created through the real routes.</summary>
    private static async Task<(Guid Org, Guid Project)> CreateOtherTenantAsync(World world, string slug)
    {
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(world.Client, user, slug);
        var project = await CreateProjectAsync(world.Client, user, org, slug + "-project");

        return (org, project);
    }

    // ------------------------------------------------------------------ (a) migration: RLS flags

    [Fact]
    public async Task Artifact_AndKindPolicy_HaveForcedRls()
    {
        var database = await _fixture.CreateDatabaseAsync();

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
        var key = Key(world, "exec-a", 1);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.ExecuteAsync(InsertArtifactSql(world.Org, world.Project, key))).ShouldBe(1);
        (await scope.CaptureErrorAsync(InsertArtifactSql(world.Org, world.Project, key))).SqlState.ShouldBe("23505");
    }

    [Fact]
    public async Task SameKey_OtherOrg_Succeeds()
    {
        await using var world = await StartAsync();
        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "beta");
        var key = Key(world, "exec-a", 1);

        await InsertArtifactAsync(world, key);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), otherOrg))
        {
            (await scope.ExecuteAsync(InsertArtifactSql(otherOrg, otherProject, key))).ShouldBe(1);
            await scope.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.artifact")).ShouldBe(2);
    }

    // ------------------------------------------------------------------ (c) AC4: unique (org_id, id)

    [Fact]
    public async Task TenantKey_UniqueOrgIdId_Exists()
    {
        var database = await _fixture.CreateDatabaseAsync();

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
        await InsertArtifactAsync(world, Key(world, "exec-a", 1));

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database));

        (await scope.ScalarAsync<bool>("select evidgate.current_org_id() is null")).ShouldBeTrue();
        (await scope.ScalarAsync<long>("select count(*) from public.artifact")).ShouldBe(0);
        (await scope.ScalarAsync<long>("select count(*) from public.artifact_kind_policy")).ShouldBe(0);
    }

    [Fact]
    public async Task Select_OtherOrg_ReturnsZero()
    {
        await using var world = await StartAsync();
        await InsertArtifactAsync(world, Key(world, "exec-a", 1));

        await using var scope = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(world.Database),
            Guid.CreateVersion7());

        (await scope.ScalarAsync<long>("select count(*) from public.artifact")).ShouldBe(0);
        (await scope.ScalarAsync<long>("select count(*) from public.artifact_kind_policy")).ShouldBe(0);
    }

    // ------------------------------------------------------------------ (e) AC6: the closed lists

    [Fact]
    public async Task Kind_NotInList_FailsCheck()
    {
        await using var world = await StartAsync();
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.CaptureErrorAsync(
            InsertArtifactSql(world.Org, world.Project, Key(world, "exec-a", 1), kind: "bogus"))).SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task ScanState_Invalid_FailsCheck()
    {
        await using var world = await StartAsync();
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.CaptureErrorAsync(
            InsertArtifactSql(world.Org, world.Project, Key(world, "exec-a", 1), scanState: "bogus"))).SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task Sensitivity_NotInList_FailsCheck()
    {
        await using var world = await StartAsync();
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.CaptureErrorAsync(
            InsertArtifactSql(world.Org, world.Project, Key(world, "exec-a", 1), sensitivity: "bogus"))).SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task State_Invalid_FailsCheck()
    {
        await using var world = await StartAsync();
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.CaptureErrorAsync(
            InsertArtifactSql(world.Org, world.Project, Key(world, "exec-a", 1), state: "bogus"))).SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task KindPolicy_KindAndSensitivity_OutsideTheirLists_FailCheck()
    {
        await using var world = await StartAsync();

        static string InsertPolicySql(Guid org, Guid project, string kind, string sensitivity) =>
            "insert into public.artifact_kind_policy "
            + "(id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity) "
            + $"values ('{Guid.CreateVersion7()}', '{org}', '{project}', '{kind}', 1024, array['application/zip'], '{sensitivity}')";

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

        (await scope.CaptureErrorAsync(
            InsertArtifactSql(world.Org, otherProject, Key(world, "exec-a", 1)))).SqlState.ShouldBe("23503");
    }

    // ------------------------------------------------------------------ (g) T1.1: indexes and grants

    [Fact]
    public async Task ArtifactIndexes_ExistInPgIndexes()
    {
        var database = await _fixture.CreateDatabaseAsync();
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
    }

    [Fact]
    public async Task AppRole_HasNoDelete_AndOnlyTheContractUpdateColumns()
    {
        var database = await _fixture.CreateDatabaseAsync();

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

        var artifact = new Artifact(
            Guid.CreateVersion7(),
            world.Org,
            world.Project,
            Guid.CreateVersion7(),
            ExecutionKey,
            1,
            ArtifactKind.Trace,
            Key(world, "exec-a", 1),
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
