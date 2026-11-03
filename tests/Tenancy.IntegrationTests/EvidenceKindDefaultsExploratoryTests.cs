// 06.1.2-T3 exploratory QA (QaS46, not the implementer): gaps the sibling suites do not pin. Media-type parameter
// and case handling on the mint, negative declared bytes, and the isolation of one kind's policy row from another
// kind's default. Expectations come from RFC 9110 (media-type grammar, case-insensitive type and subtype) and the
// 06.1.2 plan (§5 error codes, §10 T3), not from the current behaviour.
#pragma warning disable CA1861
using System.Net;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class EvidenceKindDefaultsExploratoryTests(PostgresTenancyFixture fixture, SeaweedFsStore store)
    : IClassFixture<SeaweedFsStore>
{
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Checksum = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Jti = "jti-kind-defaults-qa";
    private const string ProducerId = "shard-1";

    private sealed record World(
        TenancyApiFactory Api,
        TenancyApiFactory Host,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Host.DisposeAsync();
            await Api.DisposeAsync();
        }
    }

    private static RequestPrincipal Writer(string jti = Jti) =>
        RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: jti);

    private async Task<World> StartAsync()
    {
        var database = await fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(fixture.AppConnectionString(database));
        Guid user;
        Guid org;
        Guid project;

        using (var setup = api.CreateClient())
        {
            user = Guid.CreateVersion7();
            org = await CreateOrgAsync(setup, user, Unique.Slug("acme"));
            project = await CreateProjectAsync(setup, user, org, Unique.Slug("checkout"));
        }

        var host = TenancyApiFactory.WithPrincipal(
            fixture.AppConnectionString(database), org, project, Writer(), store.Configuration());

        return new World(api, host, host.CreateClient(), database, user, org, project);
    }

    private async Task<Guid> SeedRunAsync(World world)
    {
        var runId = await RunRowSeeder.SeedRunAsync(fixture, world.Database, world.Org);

        await using var scope = await TenantScope.BeginAsync(fixture.AppConnectionString(world.Database), world.Org);
        var testRevision = await scope.ScalarAsync<Guid>(
            "select id from public.test_revision where org_id = @org_id limit 1", ("org_id", world.Org));

        (await scope.ExecuteAsync(
            "insert into public.planned_execution (id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id, required) "
            + "values (@id, @org_id, @run_id, @execution_key, @case_revision_id, @variant_hash, 'staging', 'shard-1', true)",
            ("id", Guid.CreateVersion7()), ("org_id", world.Org), ("run_id", runId), ("execution_key", ExecutionKey),
            ("case_revision_id", testRevision), ("variant_hash", "sha256:" + new string('c', 64)))).ShouldBe(1);

        (await scope.ExecuteAsync(
            "insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id, bound_token_jti) "
            + "values (@id, @org_id, @run_id, 1, @producer_id, null)",
            ("id", Guid.CreateVersion7()), ("org_id", world.Org), ("run_id", runId), ("producer_id", ProducerId))).ShouldBe(1);

        await scope.CommitAsync();

        return runId;
    }

    private async Task InsertKindPolicyAsync(
        World world,
        string kind,
        long maxBytes,
        string[] mediaTypes,
        bool captureEnabled = true)
    {
        var array = "array[" + string.Join(",", mediaTypes.Select(type => $"'{type}'")) + "]";

        await using var scope = await TenantScope.BeginAsync(fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity, capture_enabled) "
            + $"values ('{Guid.CreateVersion7()}', '{world.Org}', '{world.Project}', '{kind}', {maxBytes}, {array}, 'normal', {(captureEnabled ? "true" : "false")})"))
            .ShouldBe(1);
        await scope.CommitAsync();
    }

    private static object Body(Guid runId, string kind, string mediaType, long bytes) =>
        new
        {
            run_id = runId,
            producer_id = ProducerId,
            execution_key = ExecutionKey,
            attempt_no = 1,
            kind,
            media_type = mediaType,
            bytes,
            sha256 = Checksum,
        };

    private static Task<ApiAnswer> UploadUrlAsync(World world, object body, string? idempotencyKey = null) =>
        world.Client.SendAsync(HttpMethod.Post, "/v1/artifacts/upload-url", _ => { }, body, idempotencyKey);

    private Task<long> ArtifactRowsAsync(World world) =>
        fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.artifact where org_id = '{world.Org:D}'");

    // ------------------------------------------------------------------ media-type grammar (RFC 9110 §8.3.1)

    /// <summary>A media-type parameter with no whitespace around the <c>;</c> is stripped before the allow-list compare.</summary>
    [Fact]
    public async Task UploadUrl_ImageMediaTypeWithParameterNoWhitespace_IsAccepted()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);

        var answer = await UploadUrlAsync(world, Body(runId, "screenshot", "image/png;charset=binary", 1024), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        (await ArtifactRowsAsync(world)).ShouldBe(1);
    }

    /// <summary>
    /// RFC 9110 §8.3.1: type and subtype are case-insensitive. The allowlist match is OrdinalIgnoreCase, so the
    /// upper-cased screenshot type is accepted on the no-row default.
    /// </summary>
    [Fact]
    public async Task UploadUrl_UppercaseMediaTypeOnDefaultKind_IsAccepted()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);

        var answer = await UploadUrlAsync(world, Body(runId, "screenshot", "IMAGE/PNG", 1024), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        (await ArtifactRowsAsync(world)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ declared size

    /// <summary>
    /// 06.1.2 AC: a declared size of <c>0</c> or less is <c>size_out_of_range</c> (422), before any policy is read.
    /// The sibling suite pins 0; this pins a negative value, which the signed <c>long</c> lets through the wire.
    /// </summary>
    [Fact]
    public async Task UploadUrl_NegativeBytes_422_SizeOutOfRange_NoRow()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, "trace", maxBytes: 100L * 1024 * 1024, mediaTypes: ["application/zip"]);

        var answer = await UploadUrlAsync(world, Body(runId, "trace", "application/zip", -1), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("size_out_of_range");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ one kind's row does not touch another's default

    /// <summary>
    /// D-A9 / D-S46-Q1: a row for <c>screenshot</c> (capture switched off) refuses screenshots 422 <c>kind_disabled</c>
    /// and leaves <c>video</c>, which has no row, at its own static default. The row is keyed by kind, so a
    /// lookup that resolved by project alone would wrongly refuse the video mint.
    /// </summary>
    [Fact]
    public async Task UploadUrl_RowForOneKind_DoesNotChangeAnotherKindsDefault()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, "screenshot", maxBytes: 10L * 1024 * 1024, mediaTypes: ["image/png"], captureEnabled: false);

        var refused = await UploadUrlAsync(world, Body(runId, "screenshot", "image/png", 1024), NewKey());
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("kind_disabled");
        (await ArtifactRowsAsync(world)).ShouldBe(0);

        var video = await UploadUrlAsync(world, Body(runId, "video", "video/mp4", 1024), NewKey());
        video.Status.ShouldBe(HttpStatusCode.OK, video.ErrorMessage);
        (await ArtifactRowsAsync(world)).ShouldBe(1);
    }
}
