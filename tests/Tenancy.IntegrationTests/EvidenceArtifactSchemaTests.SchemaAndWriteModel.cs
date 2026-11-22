using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceArtifactSchemaTests
{
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

        // 0066 added `sensitivity` to the artifact's updatable set: the masking scan is the one writer that raises a
        // flagged row to `sensitive` (06.2.3 AC2), and the scan's own transition is the fifth of the lifecycle writes.
        (await UpdateColumnsAsync(database, "artifact")).ShouldBe(
            ["attempt_id", "bytes", "expires_at", "failure_code", "scan_state", "sensitivity", "state"]);
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
