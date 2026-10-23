using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>Independent 06.1.1-T1 regression checks against the migrated database and app-role write path.</summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class EvidenceArtifactQaTests
{
    private readonly PostgresTenancyFixture _fixture;

    public EvidenceArtifactQaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project, Guid RowId)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "qa-org");
        var project = await CreateProjectAsync(client, user, org, "qa-project");
        return new World(api, client, database, user, org, project, Guid.CreateVersion7());
    }

    private static async Task InsertAsync(TenantScope scope, string table, Guid id, Guid org, Guid project, Guid run, string kind = "trace")
    {
        var sql = table == "artifact"
            ? """
              insert into public.artifact
                (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type, checksum, sensitivity)
              values (@id, @org, @project, @run, @execution, 1, @kind, @key, 'application/zip', @checksum, 'normal')
              """
            : """
              insert into public.artifact_kind_policy
                (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity)
              values (@id, @org, @project, @kind, 1024, array['application/zip'], 'normal')
              """;
        var execution = new string('a', 64);
        (await scope.ExecuteAsync(sql,
            ("id", id), ("org", org), ("project", project), ("kind", kind), ("run", run),
            ("execution", execution), ("key", $"org/{org}/project/{project}/run/{run}/attempt/{execution}/1/{id}"),
            ("checksum", "sha256:" + new string('b', 64)))).ShouldBe(1);
    }

    private async Task SeedAsync(World world, string table)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        await InsertAsync(scope, table, world.RowId, world.Org, world.Project, await CreateRunAsync(world, world.Org));
        await scope.CommitAsync();
    }

    /// <summary>One committed run row for <paramref name="org"/> (0031's composite run key).</summary>
    private async Task<Guid> CreateRunAsync(World world, Guid org) =>
        await RunRowSeeder.SeedRunAsync(_fixture, world.Database, org);

    [Theory]
    [InlineData("artifact", "object_key = 'replacement'")]
    [InlineData("artifact", "org_id = org_id")]
    [InlineData("artifact", "checksum = 'replacement'")]
    [InlineData("artifact", "version = version + 1")]
    [InlineData("artifact_kind_policy", "org_id = org_id")]
    [InlineData("artifact_kind_policy", "project_id = project_id")]
    [InlineData("artifact_kind_policy", "kind = 'video'")]
    [InlineData("artifact_kind_policy", "updated_at = now()")]
    public async Task AppUpdate_ImmutableColumn_IsDenied(string table, string assignment)
    {
        await using var world = await StartAsync();
        await SeedAsync(world, table);
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ScalarAsync<long>($"select count(*) from public.{table} where id = @id", ("id", world.RowId))).ShouldBe(1);
        (await scope.CaptureErrorAsync($"update public.{table} set {assignment} where id = @id", ("id", world.RowId)))
            .SqlState.ShouldBe("42501");
    }

    [Theory]
    [InlineData("artifact")]
    [InlineData("artifact_kind_policy")]
    public async Task AppDelete_OwnRow_IsDenied(string table)
    {
        await using var world = await StartAsync();
        await SeedAsync(world, table);
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ScalarAsync<long>($"select count(*) from public.{table} where id = @id", ("id", world.RowId))).ShouldBe(1);
        (await scope.CaptureErrorAsync($"delete from public.{table} where id = @id", ("id", world.RowId)))
            .SqlState.ShouldBe("42501");
    }

    [Theory]
    [InlineData("artifact")]
    [InlineData("artifact_kind_policy")]
    public async Task Insert_OtherOrg_WithValidProject_IsDeniedByWithCheck(string table)
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "other-org");
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "other-project");
        var id = Guid.CreateVersion7();
        // The row is otherwise valid: first prove it can be committed under its own tenant.
        await using (var ownScope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), otherOrg))
        {
            await InsertAsync(ownScope, table, id, otherOrg, otherProject, await CreateRunAsync(world, otherOrg));
            await ownScope.CommitAsync();
        }

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var deniedId = Guid.CreateVersion7();
        var deniedRun = Guid.CreateVersion7();
        var execution = new string('a', 64);
        var sql = table == "artifact"
            ? """
              insert into public.artifact
                (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type, checksum, sensitivity)
              values (@id, @org, @project, @run, @execution, 1, 'video', @key, 'video/webm', @checksum, 'normal')
              """
            : """
              insert into public.artifact_kind_policy
                (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity)
              values (@id, @org, @project, 'video', 1024, array['video/webm'], 'normal')
              """;
        // The row is otherwise valid — the key is minted for its own org/project and satisfies the
        // 0028 shape CHECK — so the only rejection left is the row-level-security `with check`.
        var error = await scope.CaptureErrorAsync(sql,
            ("id", deniedId), ("org", otherOrg), ("project", otherProject),
            ("run", deniedRun), ("execution", execution),
            ("key", $"org/{otherOrg}/project/{otherProject}/run/{deniedRun}/attempt/{execution}/1/{deniedId}"),
            ("checksum", "sha256:" + new string('b', 64)));
        error.SqlState.ShouldBe("42501");
        error.MessageText.ShouldContain("row-level security");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KindPolicy_SeededRows_UnsetOrOtherTenant_ReturnZero(bool otherTenant)
    {
        await using var world = await StartAsync();
        await SeedAsync(world, "artifact_kind_policy");
        await using (var ownScope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await ownScope.ScalarAsync<long>("select count(*) from public.artifact_kind_policy")).ShouldBe(1);
        }

        await using var scope = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(world.Database), otherTenant ? Guid.CreateVersion7() : null);
        (await scope.ScalarAsync<long>("select count(*) from public.artifact_kind_policy")).ShouldBe(0);
    }

    [Fact]
    public async Task KindPolicy_DuplicateProjectAndKind_FailsUnique()
    {
        await using var world = await StartAsync();
        await SeedAsync(world, "artifact_kind_policy");
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var error = await scope.CaptureErrorAsync(
            """
            insert into public.artifact_kind_policy
              (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity)
            values (@id, @org, @project, 'trace', 2048, array['application/zip'], 'sensitive')
            """, ("id", Guid.CreateVersion7()), ("org", world.Org), ("project", world.Project));
        error.SqlState.ShouldBe("23505");
        error.ConstraintName.ShouldBe("artifact_kind_policy_project_kind_key");
    }

    [Fact]
    public async Task KindPolicy_DifferentKindOrProject_IsAllowed()
    {
        await using var world = await StartAsync();
        var secondProject = await CreateProjectAsync(world.Client, world.User, world.Org, "second-project");
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        await InsertAsync(scope, "artifact_kind_policy", Guid.CreateVersion7(), world.Org, world.Project, Guid.CreateVersion7());
        await InsertAsync(scope, "artifact_kind_policy", Guid.CreateVersion7(), world.Org, world.Project, Guid.CreateVersion7(), "video");
        await InsertAsync(scope, "artifact_kind_policy", Guid.CreateVersion7(), world.Org, secondProject, Guid.CreateVersion7());
        (await scope.ScalarAsync<long>("select count(*) from public.artifact_kind_policy")).ShouldBe(3);
    }

    [Fact]
    public async Task KindPolicy_OtherOrgProject_FailsCompositeForeignKey()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "other-org");
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "other-project");
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var error = await scope.CaptureErrorAsync(
            """
            insert into public.artifact_kind_policy
              (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity)
            values (@id, @org, @project, 'trace', 1024, array['application/zip'], 'normal')
            """, ("id", Guid.CreateVersion7()), ("org", world.Org), ("project", otherProject));
        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("artifact_kind_policy_project_fk");
    }

    [Theory]
    [InlineData("artifact", "bytes = 2048")]
    [InlineData("artifact_kind_policy", "max_bytes = 2048")]
    public async Task AllowedColumnUpdate_TriggerIncrementsVersionEachTime(string table, string assignment)
    {
        await using var world = await StartAsync();
        await SeedAsync(world, table);
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ScalarAsync<int>($"select version from public.{table} where id = @id", ("id", world.RowId))).ShouldBe(1);
        var update = $"update public.{table} set {assignment} where id = @id returning version";
        (await scope.ScalarAsync<int>(update, ("id", world.RowId))).ShouldBe(2);
        (await scope.ScalarAsync<int>(update, ("id", world.RowId))).ShouldBe(3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EfKindPolicy_Insert_RoundTripsCaptureSwitchAndMediaTypes(bool captureEnabled)
    {
        await using var world = await StartAsync();
        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var factory = services.ServiceProvider.GetRequiredService<ITenantDbContextFactory<EvidenceDbContext>>();
        var policy = new ArtifactKindPolicy(world.RowId, world.Org, world.Project, ArtifactKind.Trace,
            long.MaxValue, ["application/zip", "application/octet-stream"], ArtifactSensitivity.Sensitive,
            rejectOnSecret: true, captureEnabled: captureEnabled);
        await using (var context = factory.Create())
        {
            await context.KindPolicies.AddAsync(policy, TestContext.Current.CancellationToken);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = factory.Create())
        {
            var read = await context.KindPolicies.SingleAsync(row => row.Id == world.RowId, TestContext.Current.CancellationToken);
            read.CaptureEnabled.ShouldBe(captureEnabled);
            read.RejectOnSecret.ShouldBeTrue();
            read.MaxBytes.ShouldBe(long.MaxValue);
            read.MediaTypes.ShouldBe(["application/zip", "application/octet-stream"]);
            read.DefaultSensitivity.ShouldBe(ArtifactSensitivity.Sensitive);
            read.Version.ShouldBe(1);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EfAllowedUpdate_RefreshesVersion_ForSecondTrackedSave(bool policyRow)
    {
        await using var world = await StartAsync();
        await SeedAsync(world, policyRow ? "artifact_kind_policy" : "artifact");
        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        await using var context = services.ServiceProvider.GetRequiredService<ITenantDbContextFactory<EvidenceDbContext>>().Create();
        if (policyRow)
        {
            var row = await context.KindPolicies.SingleAsync(item => item.Id == world.RowId, TestContext.Current.CancellationToken);
            context.Entry(row).Property(item => item.MaxBytes).CurrentValue = 2048;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            row.Version.ShouldBe(2);
            context.Entry(row).Property(item => item.MaxBytes).CurrentValue = 4096;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            row.Version.ShouldBe(3);
        }
        else
        {
            var row = await context.Artifacts.SingleAsync(item => item.Id == world.RowId, TestContext.Current.CancellationToken);
            context.Entry(row).Property(item => item.Bytes).CurrentValue = 2048;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            row.Version.ShouldBe(2);
            context.Entry(row).Property(item => item.Bytes).CurrentValue = 4096;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            row.Version.ShouldBe(3);
        }
    }
}
