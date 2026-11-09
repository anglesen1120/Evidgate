using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Domain;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.1.1-T2.1/T2.2 through the real C2 pipeline and a real PostgreSQL: the mint command's four named 422
/// refusals in the C2 order (AC1), the server-chosen key and the server-resolved sensitivity of a created row
/// (AC2), and the tenant boundary. Every mint runs through the host's Mediator with the tenant and a service
/// principal holding <c>artifact:write</c> published for the flow — the same stages 06.1.2's
/// <c>POST /v1/artifacts/upload-url</c> will drive.
/// </summary>
[Trait("Category", "Docker")]
public sealed class EvidenceCreateArtifactRecordTests
{
    /// <summary>A well-formed execution key: sha256 hex (00 §7).</summary>
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>A well-formed declared content address, <c>sha256:&lt;64 hex&gt;</c>.</summary>
    private const string Checksum = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly PostgresTenancyFixture _fixture;

    public EvidenceCreateArtifactRecordTests(PostgresTenancyFixture fixture) => _fixture = fixture;

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
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    /// <summary>
    /// One mint through the real Mediator pipeline (telemetry → validation → tenant transaction → authorization →
    /// handler → audit → commit): the tenant (org + project) and a service principal holding <c>artifact:write</c>
    /// are published for the flow, exactly as the upload-url entry point will.
    /// </summary>
    private static async Task<Result<ArtifactRecord>> MintAsync(
        World world,
        Guid runId,
        string? clientObjectKey = null,
        string kind = ArtifactKind.Trace,
        string checksum = Checksum,
        long declaredBytes = 1024,
        string executionKey = ExecutionKey,
        int attemptNo = 1,
        string mediaType = "application/zip",
        Guid? project = null)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        using var tenant = provider.GetRequiredService<IRequestTenantContextSetter>().Publish(world.Org, project ?? world.Project);
        using var principal = provider.GetRequiredService<IRequestPrincipalSetter>()
            .Publish(RequestPrincipal.Service(null, [Permissions.Artifact.Write]));

        return await provider.GetRequiredService<IMediator>()
            .Send(
                new CreateArtifactRecord(runId, executionKey, attemptNo, kind, mediaType, declaredBytes, checksum, clientObjectKey),
                TestContext.Current.CancellationToken);
    }

    /// <summary>One committed kind-policy row for <paramref name="world"/>'s project, as the app role.</summary>
    private async Task InsertKindPolicyAsync(World world, string kind, string sensitivity)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity) "
            + $"values ('{Guid.CreateVersion7()}', '{world.Org}', '{world.Project}', '{kind}', 1024, array['application/zip'], '{sensitivity}')"))
            .ShouldBe(1);
        await scope.CommitAsync();
    }

    private async Task<long> RowsAsync(World world) =>
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.artifact where org_id = '{world.Org}'");

    /// <summary>One committed run row for <paramref name="world"/>'s org, so 0031's composite key is satisfied.</summary>
    private async Task<Guid> CreateRunAsync(World world) =>
        await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);

    // ------------------------------------------------------------------ AC1: the client key is refused

    [Fact]
    public async Task CreateArtifactRecord_WithClientKey_ReturnsObjectKeyNotAllowed()
    {
        await using var world = await StartAsync();

        var result = await MintAsync(world, Guid.CreateVersion7(), clientObjectKey: "org/other/project/other/run/other/attempt/x/1/y");

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.ObjectKeyNotAllowed);
        result.Error.Status.ShouldBe(422);
        result.Error.Retryable.ShouldBeFalse();

        // The refusal returns before any statement is built, so no row is written (AC1).
        (await RowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task CreateArtifactRecord_WithEmptyClientKey_ReturnsObjectKeyNotAllowed()
    {
        await using var world = await StartAsync();

        var result = await MintAsync(world, Guid.CreateVersion7(), clientObjectKey: string.Empty);

        result.Error!.Code.ShouldBe(ApiErrorCode.ObjectKeyNotAllowed);
        (await RowsAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ one test per named code

    [Fact]
    public async Task CreateArtifactRecord_WithUnknownKind_ReturnsKindUnknown()
    {
        await using var world = await StartAsync();

        var result = await MintAsync(world, Guid.CreateVersion7(), kind: "bogus");

        result.Error!.Code.ShouldBe(ApiErrorCode.KindUnknown);
        result.Error.Status.ShouldBe(422);
        (await RowsAsync(world)).ShouldBe(0);
    }

    [Theory]
    [InlineData("sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n")]
    [InlineData("sha256:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("sha256:bbbb")]
    public async Task CreateArtifactRecord_WithInvalidChecksum_ReturnsChecksumInvalid(string checksum)
    {
        await using var world = await StartAsync();

        var result = await MintAsync(world, Guid.CreateVersion7(), checksum: checksum);

        result.Error!.Code.ShouldBe(ApiErrorCode.ChecksumInvalid);
        result.Error.Status.ShouldBe(422);
        (await RowsAsync(world)).ShouldBe(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CreateArtifactRecord_WithNonPositiveSize_ReturnsSizeOutOfRange(long declaredBytes)
    {
        await using var world = await StartAsync();

        var result = await MintAsync(world, Guid.CreateVersion7(), declaredBytes: declaredBytes);

        result.Error!.Code.ShouldBe(ApiErrorCode.SizeOutOfRange);
        result.Error.Status.ShouldBe(422);
        (await RowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task CreateArtifactRecord_WithSeveralInvalidFields_ReturnsTheFirstInC2Order()
    {
        await using var world = await StartAsync();

        // Client key before kind, kind before checksum, checksum before size (06.1.1 §10 T2 Notes item 4).
        (await MintAsync(world, Guid.CreateVersion7(), clientObjectKey: "x", kind: "bogus", checksum: "nope", declaredBytes: 0))
            .Error!.Code.ShouldBe(ApiErrorCode.ObjectKeyNotAllowed);
        (await MintAsync(world, Guid.CreateVersion7(), kind: "bogus", checksum: "nope", declaredBytes: 0))
            .Error!.Code.ShouldBe(ApiErrorCode.KindUnknown);
        (await MintAsync(world, Guid.CreateVersion7(), checksum: "nope", declaredBytes: 0))
            .Error!.Code.ShouldBe(ApiErrorCode.ChecksumInvalid);
        (await MintAsync(world, Guid.CreateVersion7(), declaredBytes: 0))
            .Error!.Code.ShouldBe(ApiErrorCode.SizeOutOfRange);

        (await RowsAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ AC2: the created row

    [Fact]
    public async Task Mint_WithPlannedPair_AttemptIdNull()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);

        var result = await MintAsync(world, runId);

        result.IsSuccess.ShouldBeTrue();
        var record = result.Value;
        record.State.ShouldBe(ArtifactState.PendingUpload);
        record.ObjectKey.ShouldBe(
            $"org/{world.Org}/project/{world.Project}/run/{runId}/attempt/{ExecutionKey}/1/{record.ArtifactId}");

        // The row matches the contract: the server-chosen key, pending_upload, scan_state pending, no attempt yet.
        (await _fixture.ScalarAsync<string>(world.Database, $"select object_key from public.artifact where id = '{record.ArtifactId}'"))
            .ShouldBe(record.ObjectKey);
        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.artifact where id = '{record.ArtifactId}'"))
            .ShouldBe(ArtifactState.PendingUpload);
        (await _fixture.ScalarAsync<string>(world.Database, $"select scan_state from public.artifact where id = '{record.ArtifactId}'"))
            .ShouldBe(ArtifactScanState.Pending);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.artifact where id = '{record.ArtifactId}' and attempt_id is null"))
            .ShouldBe(1);
        (await _fixture.ScalarAsync<Guid>(world.Database, $"select org_id from public.artifact where id = '{record.ArtifactId}'"))
            .ShouldBe(world.Org);
        (await _fixture.ScalarAsync<Guid>(world.Database, $"select project_id from public.artifact where id = '{record.ArtifactId}'"))
            .ShouldBe(world.Project);
    }

    [Fact]
    public async Task Mint_WithoutPolicyRow_UsesTheKindFallback()
    {
        await using var world = await StartAsync();

        // No artifact_kind_policy row: the static T57(a) fallback applies — trace is sensitive, screenshot normal.
        var trace = await MintAsync(world, await CreateRunAsync(world), kind: ArtifactKind.Trace);
        var screenshot = await MintAsync(world, await CreateRunAsync(world), kind: ArtifactKind.Screenshot);

        (await _fixture.ScalarAsync<string>(world.Database, $"select sensitivity from public.artifact where id = '{trace.Value.ArtifactId}'"))
            .ShouldBe(ArtifactSensitivity.Sensitive);
        (await _fixture.ScalarAsync<string>(world.Database, $"select sensitivity from public.artifact where id = '{screenshot.Value.ArtifactId}'"))
            .ShouldBe(ArtifactSensitivity.Normal);
    }

    [Fact]
    public async Task Mint_WithPolicyRow_ProjectOverrideBeatsTheFallback()
    {
        await using var world = await StartAsync();

        // A project row lowering trace to normal is the only way to override the fallback (T57(a)); the command
        // carries no sensitivity field, so the client cannot influence it.
        await InsertKindPolicyAsync(world, ArtifactKind.Trace, ArtifactSensitivity.Normal);

        var result = await MintAsync(world, await CreateRunAsync(world), kind: ArtifactKind.Trace);

        (await _fixture.ScalarAsync<string>(world.Database, $"select sensitivity from public.artifact where id = '{result.Value.ArtifactId}'"))
            .ShouldBe(ArtifactSensitivity.Normal);
    }

    [Fact]
    public async Task Mint_ForARunOfAnotherProject_IsRefusedAndWritesNoRow()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        var otherProject = await CreateProjectAsync(world.Client, world.User, world.Org, "billing");

        var result = await MintAsync(world, runId, project: otherProject);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(EvidenceErrors.ArtifactRunProjectMismatchCode);
        (await RowsAsync(world)).ShouldBe(0);

        // The same run still mints for its own project.
        (await MintAsync(world, runId)).IsSuccess.ShouldBeTrue();
        (await RowsAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task Mint_ForAnUnknownRun_IsRefusedWithRunNotFound()
    {
        await using var world = await StartAsync();

        var result = await MintAsync(world, Guid.CreateVersion7());

        result.Error!.Code.ShouldBe(EvidenceErrors.RunNotFound.Code);
        (await RowsAsync(world)).ShouldBe(0);
    }
}
