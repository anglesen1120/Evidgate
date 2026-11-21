using Evidgate.Modules.Evidence.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceArtifactSchemaTests
{
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
}
