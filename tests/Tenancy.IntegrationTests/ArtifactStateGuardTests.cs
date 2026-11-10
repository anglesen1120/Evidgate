using Evidgate.Modules.Evidence.Domain;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The artifact state guard in PostgreSQL (migration 0095) against a real migrated database: the app role moves every
/// allowed edge, is refused with SQLSTATE 23514 on every non-edge, and a refused update leaves both the state and the
/// version untouched. The matrix comes from <see cref="ArtifactStateMachine"/>, the same data the domain suite uses.
/// </summary>
[Trait("Category", "Docker")]
public sealed class ArtifactStateGuardTests(PostgresTenancyFixture fixture)
{
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string Checksum = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string UpdateState = "update public.artifact set state = @state where id = @artifact_id";

    private readonly PostgresTenancyFixture _fixture = fixture;

    public static TheoryData<string, string> AllowedEdges => ArtifactStateMachine.AllowedEdges();

    public static TheoryData<string, string> IllegalEdges => ArtifactStateMachine.IllegalEdges();

    public static TheoryData<string> SameStates => [.. ArtifactStateMachine.States];

    private sealed record Seeded(string Database, Guid Org, Guid ArtifactId);

    [Theory]
    [MemberData(nameof(AllowedEdges))]
    public async Task Transition_AllowedEdges_Succeed(string from, string to)
    {
        var seeded = await SeedAsync(from);

        await using (var scope = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(seeded.Database), seeded.Org))
        {
            (await scope.ExecuteAsync(
                UpdateState, ("state", to), ("artifact_id", seeded.ArtifactId))).ShouldBe(1);
            await scope.CommitAsync();
        }

        (await StateAsync(seeded)).ShouldBe(to);
    }

    [Theory]
    [MemberData(nameof(IllegalEdges))]
    public async Task Transition_IllegalEdges_RefusedAndRowUnchanged(string from, string to)
    {
        var seeded = await SeedAsync(from);

        await using (var scope = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(seeded.Database), seeded.Org))
        {
            var error = await scope.CaptureErrorAsync(
                UpdateState, ("state", to), ("artifact_id", seeded.ArtifactId));

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            error.MessageText.ShouldBe($"artifact_illegal_transition: {from} -> {to}");
        }

        (await StateAsync(seeded)).ShouldBe(from);
        (await VersionAsync(seeded)).ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(SameStates))]
    public async Task Transition_SameState_NoOp_Succeeds(string state)
    {
        var seeded = await SeedAsync(state);

        await using (var scope = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(seeded.Database), seeded.Org))
        {
            (await scope.ExecuteAsync(
                UpdateState, ("state", state), ("artifact_id", seeded.ArtifactId))).ShouldBe(1);
            await scope.CommitAsync();
        }

        (await StateAsync(seeded)).ShouldBe(state);
    }

    [Fact]
    public async Task RawUpdate_RejectedToReady_TriggerRaises()
    {
        var seeded = await SeedAsync(ArtifactState.Rejected);

        await using (var scope = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(seeded.Database), seeded.Org))
        {
            var error = await scope.CaptureErrorAsync(
                UpdateState, ("state", ArtifactState.Ready), ("artifact_id", seeded.ArtifactId));

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            error.MessageText.ShouldBe("artifact_illegal_transition: rejected -> ready");
        }

        (await StateAsync(seeded)).ShouldBe(ArtifactState.Rejected);
        (await VersionAsync(seeded)).ShouldBe(1);
    }

    [Fact]
    public async Task RawUpdate_ReadyToUploaded_TriggerRaises()
    {
        var seeded = await SeedAsync(ArtifactState.Ready);

        await using (var scope = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(seeded.Database), seeded.Org))
        {
            var error = await scope.CaptureErrorAsync(
                UpdateState, ("state", ArtifactState.Uploaded), ("artifact_id", seeded.ArtifactId));

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            error.MessageText.ShouldBe("artifact_illegal_transition: ready -> uploaded");
        }

        (await StateAsync(seeded)).ShouldBe(ArtifactState.Ready);
        (await VersionAsync(seeded)).ShouldBe(1);
    }

    [Fact]
    public async Task RawUpdate_ReadyToRejected_ParseVerdict_Succeeds()
    {
        var seeded = await SeedAsync(ArtifactState.Ready);

        await using (var scope = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(seeded.Database), seeded.Org))
        {
            (await scope.ExecuteAsync(
                "update public.artifact set state = @state, failure_code = @code where id = @artifact_id",
                ("state", ArtifactState.Rejected),
                ("code", ArtifactFailureCode.ParseCrashed),
                ("artifact_id", seeded.ArtifactId))).ShouldBe(1);
            await scope.CommitAsync();
        }

        (await StateAsync(seeded)).ShouldBe(ArtifactState.Rejected);
        (await _fixture.ScalarAsync<string>(
            seeded.Database,
            $"select failure_code from public.artifact where id = '{seeded.ArtifactId:D}'"))
            .ShouldBe(ArtifactFailureCode.ParseCrashed);
    }

    [Fact]
    public async Task Update_OfAnotherTenantsArtifact_UnderTenantScope_ChangesNothing()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var ownOrg = await SeedOrgAsync(database);
        var otherOrg = await SeedOrgAsync(database);
        var own = await SeedArtifactAsync(database, ownOrg, ArtifactState.PendingUpload);
        var other = await SeedArtifactAsync(database, otherOrg, ArtifactState.PendingUpload);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), ownOrg))
        {
            (await scope.ExecuteAsync(
                UpdateState, ("state", ArtifactState.Uploaded), ("artifact_id", other)))
                .ShouldBe(0, "RLS hides another org's row from the scoped update");
            (await scope.ExecuteAsync(
                UpdateState, ("state", ArtifactState.Uploaded), ("artifact_id", own))).ShouldBe(1);
            await scope.CommitAsync();
        }

        (await StateAsync(database, other)).ShouldBe(ArtifactState.PendingUpload);
        (await StateAsync(database, own)).ShouldBe(ArtifactState.Uploaded);
    }

    /// <summary>An org with one project, seeded as the superuser; the artifact only needs the composite project FK.</summary>
    private async Task<Guid> SeedOrgAsync(string database)
    {
        var org = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            database,
            $"insert into public.organization (id, name, slug) values ('{org:D}', 'state guard', '{Unique.Slug("org")}')");
        await _fixture.ExecuteAsync(
            database,
            "insert into public.project (id, org_id, name, slug) "
            + $"values ('{Guid.CreateVersion7():D}', '{org:D}', 'state guard', '{Unique.Slug("project")}')");
        return org;
    }

    private async Task<Seeded> SeedAsync(string state)
    {
        var database = await _fixture.SharedDatabaseAsync();
        var org = await SeedOrgAsync(database);
        return new Seeded(database, org, await SeedArtifactAsync(database, org, state));
    }

    private async Task<Guid> SeedArtifactAsync(string database, Guid org, string state)
    {
        var project = await _fixture.ScalarAsync<Guid>(
            database, $"select id from public.project where org_id = '{org:D}'");
        var id = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var objectKey =
            $"org/{org:D}/project/{project:D}/run/{runId:D}/attempt/{ExecutionKey}/1/{id:D}";

        await _fixture.ExecuteAsync(
            database,
            "insert into public.artifact (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, "
            + "media_type, checksum, sensitivity, scan_state, state, bytes) "
            + $"values ('{id:D}', '{org:D}', '{project:D}', '{runId:D}', '{ExecutionKey}', 1, "
            + $"'{ArtifactKind.Trace}', '{objectKey}', 'application/zip', '{Checksum}', "
            + $"'{ArtifactSensitivity.Normal}', '{ArtifactScanState.Clean}', '{state}', 1024)");

        return id;
    }

    private Task<string> StateAsync(Seeded seeded) => StateAsync(seeded.Database, seeded.ArtifactId);

    private Task<string> StateAsync(string database, Guid artifactId) =>
        _fixture.ScalarAsync<string>(
            database, $"select state from public.artifact where id = '{artifactId:D}'");

    private Task<int> VersionAsync(Seeded seeded) =>
        _fixture.ScalarAsync<int>(
            seeded.Database, $"select version from public.artifact where id = '{seeded.ArtifactId:D}'");
}
