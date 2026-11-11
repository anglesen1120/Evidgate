// 06.1.2-T2 review fixes (C-F2, S-F3): the audit row of a stored-state no-op and the writer's own project guard.
// The replay case drives the HTTP surface (complete, then a replay with a DIFFERENT key); the writer-guard case
// drives the CompleteArtifactUpload command directly through the real C2 pipeline, because the endpoint's verifier
// already refuses a deleted project — the writer's re-guard only matters for a project that flips between the two.
// Expectations come from the plan (06.1.2 §5, T86/T19, 06.4.4 AC2) and the review findings, not from current
// behaviour.
#pragma warning disable CA1861
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Endpoints;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.1.2-T2 (AC5, 06.4.4 AC2) review fixes: the <c>artifact.complete</c> audit row a stored-state no-op still
/// records (C-F2) and the project-status re-guard the writer applies inside its own transaction (S-F3).
/// </summary>
[Trait("Category", "Docker")]
public sealed class EvidenceCompleteAuditTests(PostgresTenancyFixture fixture, SeaweedFsStore store, TenancyHostPool hosts)
    : IClassFixture<SeaweedFsStore>, IClassFixture<TenancyHostPool>
{
    /// <summary>A well-formed execution key: sha256 hex (00 §7).</summary>
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string ZipType = "application/zip";

    /// <summary>The caller's token id (the slot-binding identity, T9).</summary>
    private const string Jti = "jti-complete-audit-1";

    private const string ProducerId = "shard-1";

    /// <summary>The kind-policy cap every seeded policy row carries; the happy-path body is exactly this size.</summary>
    private const long Cap = 1024;

    private const string SecretScanTask = "artifact_secret_scan";

    private const string StagingDeleteTask = "artifact_staging_delete";

    /// <summary>The uploaded body on the happy path: a zip signature then filler, exactly <see cref="Cap"/> bytes (the declared type is <c>application/zip</c>, 06.3.1-T2.2).</summary>
    private static readonly byte[] GoodBody = [0x50, 0x4B, 0x03, 0x04, .. Encoding.UTF8.GetBytes(new string('A', (int)Cap - 4))];

    private readonly PostgresTenancyFixture _fixture = fixture;

    private readonly SeaweedFsStore _store = store;

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

    /// <summary>A service principal holding <c>artifact:write</c> and a token id (the slot-binding identity).</summary>
    private static RequestPrincipal Writer(string jti = Jti) =>
        RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: jti);

    /// <summary>
    /// One migrated database with an org and a project created through the real registry routes, plus a host whose
    /// requests carry <paramref name="principal"/> and the object-store configuration.
    /// </summary>
    private async Task<World> StartAsync(RequestPrincipal principal)
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = hosts.RealResolver(database);
        Guid user;
        Guid org;
        Guid project;

        using (var setup = api.CreateClient())
        {
            user = Guid.CreateVersion7();
            org = await CreateOrgAsync(setup, user, Unique.Slug("acme"));
            project = await CreateProjectAsync(setup, user, org, "checkout");
        }

        var host = hosts.WithPrincipalConfigured(database, org, project, principal, _store.Configuration());

        return new World(api, host, host.CreateClient(), database, user, org, project);
    }

    /// <summary>
    /// One committed run, one planned execution and one producer slot for <paramref name="world"/>'s org, the
    /// parents 0031's composite keys need (the same seed <c>EvidenceCompleteTests</c> uses).
    /// </summary>
    private async Task<Guid> SeedRunAsync(World world)
    {
        var runId = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
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

    /// <summary>One committed kind-policy row for <paramref name="world"/>'s project, as the app role.</summary>
    private async Task InsertKindPolicyAsync(World world)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity, capture_enabled) "
            + $"values ('{Guid.CreateVersion7()}', '{world.Org}', '{world.Project}', 'trace', {Cap}, array['{ZipType}'], 'normal', true)"))
            .ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>The upload-url request body (00 §9.3, snake_case wire contract) for <paramref name="body"/>.</summary>
    private static object UploadBody(Guid runId, byte[] body) =>
        new
        {
            run_id = runId,
            producer_id = ProducerId,
            execution_key = ExecutionKey,
            attempt_no = 1,
            kind = "trace",
            media_type = ZipType,
            bytes = body.LongLength,
            sha256 = Checksum(body),
        };

    private static string Checksum(byte[] body) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(body));

    private static Task<ApiAnswer> UploadUrlAsync(World world, object body, string? idempotencyKey = null) =>
        world.Client.SendAsync(HttpMethod.Post, "/v1/artifacts/upload-url", _ => { }, body, idempotencyKey);

    private static Task<ApiAnswer> CompleteAsync(World world, Guid artifactId, string? idempotencyKey = null) =>
        world.Client.SendAsync(HttpMethod.Post, $"/v1/artifacts/{artifactId:D}/complete", _ => { }, null, idempotencyKey);

    /// <summary>Sends the bytes to the presigned URL with exactly the required headers (T18).</summary>
    private static async Task<HttpResponseMessage> PutAsync(ApiAnswer minted, byte[] body)
    {
        using var http = new HttpClient();
        using var message = new HttpRequestMessage(HttpMethod.Put, minted.Text("upload_url"))
        {
            Content = new ByteArrayContent(body),
        };

        foreach (var header in minted.Body.GetProperty("required_headers").EnumerateObject())
        {
            if (string.Equals(header.Name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = header.Value.GetString()!;
            if (!message.Headers.TryAddWithoutValidation(header.Name, value))
            {
                message.Content.Headers.TryAddWithoutValidation(header.Name, value);
            }
        }

        return await http.SendAsync(message, TestContext.Current.CancellationToken);
    }

    /// <summary>Mints one artifact for <paramref name="body"/> and PUTs the bytes through the presigned URL.</summary>
    private async Task<(Guid ArtifactId, string StagingKey, string FinalKey)> MintAndUploadAsync(World world, Guid runId, byte[] body)
    {
        var minted = await UploadUrlAsync(world, UploadBody(runId, body), NewKey());
        minted.Status.ShouldBe(HttpStatusCode.OK, minted.ErrorMessage);

        var artifactId = minted.Id("artifact_id");
        var stagingKey = minted.Text("object_key")!;
        stagingKey.ShouldBe($"pending/{world.Org:D}/{artifactId:D}");

        using var put = await PutAsync(minted, body);
        put.IsSuccessStatusCode.ShouldBeTrue(
            $"the signed PUT must be accepted by the store; status={(int)put.StatusCode}");

        var finalKey = await ArtifactColumnAsync(world, artifactId, "object_key");
        return (artifactId, stagingKey, finalKey!);
    }

    private Task<string?> ArtifactColumnAsync(World world, Guid artifactId, string column) =>
        _fixture.ScalarAsync<string?>(world.Database, $"select {column} from public.artifact where id = '{artifactId:D}'");

    private Task<long> OutboxCountAsync(World world, string taskType, Guid artifactId) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where task_type = '{taskType}' and payload->>'artifact_id' = '{artifactId:D}'");

    /// <summary>
    /// Sends <see cref="CompleteArtifactUpload"/> through the real C2 pipeline with the caller and the tenant
    /// published for the flow — the same seam <c>RunCreationSeams.SendDirectAsync</c> uses. This bypasses the
    /// endpoint's <c>ArtifactCompletionVerifier</c>, which is the point of the S-F3 case: the verifier's own
    /// project check would refuse a deleted project before the writer ever runs.
    /// </summary>
    /// <param name="world">The started world.</param>
    /// <param name="artifactId">The artifact the command addresses.</param>
    /// <param name="verdict">The server-derived verdict the verifier would have handed the writer.</param>
    /// <param name="idempotencyKey">A fresh <c>Idempotency-Key</c>, or null for a generated one.</param>
    private static async Task<Result<CompleteResponse>> SendCompleteDirectAsync(
        World world,
        Guid artifactId,
        ArtifactCompletionVerdict verdict,
        string? idempotencyKey = null)
    {
        await using var services = world.Host.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        using var tenantScope = provider.GetRequiredService<IRequestTenantContextSetter>().Publish(world.Org, world.Project);
        using var principalScope = provider.GetRequiredService<IRequestPrincipalSetter>().Publish(Writer());

        return await provider.GetRequiredService<IMediator>()
            .Send(
                new CompleteArtifactUpload(artifactId, verdict, Jti, idempotencyKey ?? NewKey()),
                TestContext.Current.CancellationToken);
    }

    // ------------------------------------------------------------------ C-F2: the no-op audit row names the artifact

    /// <summary>
    /// 06.1.2-T2 AC5 / review C-F2: a completed artifact replayed with a DIFFERENT <c>Idempotency-Key</c> takes the
    /// stored-state no-op path (nothing is written), yet the authorization stage still records an
    /// <c>artifact.complete</c> entry for the success. Both that entry and the first commit's must name the
    /// artifact — no <c>artifact.complete</c> row may carry a null subject.
    /// </summary>
    [Fact]
    public async Task Complete_ReplayWithFreshKey_AuditRowsNameTheArtifact()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);

        var first = await CompleteAsync(world, artifactId, NewKey());
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        first.Text("state").ShouldBe("ready");

        var replay = await CompleteAsync(world, artifactId, NewKey());
        replay.Status.ShouldBe(HttpStatusCode.OK, replay.ErrorMessage);
        replay.Text("state").ShouldBe("ready");

        // The first commit and the replay each write exactly one `artifact.complete` row.
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.audit_log where org_id = '{world.Org:D}' and action = 'artifact.complete'"))
            .ShouldBe(2);

        // Neither row may have a null subject: the no-op names the artifact, not nothing (review C-F2).
        (await _fixture.ScalarAsync<long>(
            world.Database,
            "select count(*) from public.audit_log "
            + $"where org_id = '{world.Org:D}' and action = 'artifact.complete' "
            + "and (object_type is null or object_id is null)"))
            .ShouldBe(0);

        // Both rows point at the artifact they completed.
        (await _fixture.ScalarAsync<long>(
            world.Database,
            "select count(*) from public.audit_log "
            + $"where org_id = '{world.Org:D}' and action = 'artifact.complete' and object_id = '{artifactId:D}'"))
            .ShouldBe(2);
    }

    // ------------------------------------------------------------------ S-F3: the writer re-guards the project

    /// <summary>
    /// 06.1.2-T2 §5 / review S-F3 / 06.4.4 AC2: a project that flips to <c>deleted</c> between the verifier and
    /// the writer must not commit. Driving the writer command directly (the verifier is bypassed) after the flip
    /// must answer the same 409 <c>project_deleting</c> the verifier does, leave the artifact <c>pending_upload</c>
    /// and enqueue nothing.
    /// </summary>
    [Fact]
    public async Task Complete_WriterProjectDeleted_409_NoOutboxRows()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);

        await _fixture.ExecuteAsync(
            world.Database, $"update public.project set status = 'deleted' where id = '{world.Project:D}'");

        var answer = await SendCompleteDirectAsync(world, artifactId, ArtifactCompletionVerdict.Accept(Cap));

        answer.IsSuccess.ShouldBeFalse();
        answer.Error!.Code.ShouldBe(ApiErrorCode.ProjectDeleting);
        answer.Error!.Status.ShouldBe((int)HttpStatusCode.Conflict);
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
    }

    /// <summary>
    /// 06.1.2-T2 §5 / review S-F3: the writer returns a stored outcome as-is FIRST, before the project guard, so a
    /// project that flips to <c>deleted</c> after a completion does not turn a replay of an already-<c>ready</c>
    /// artifact into a 409 (the same stored-outcome-first ordering the verifier is being changed to).
    /// </summary>
    [Fact]
    public async Task Complete_WriterProjectDeletedAfterReady_ReturnsStoredOutcome()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);

        var first = await CompleteAsync(world, artifactId, NewKey());
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        first.Text("state").ShouldBe("ready");

        await _fixture.ExecuteAsync(
            world.Database, $"update public.project set status = 'deleted' where id = '{world.Project:D}'");

        var answer = await SendCompleteDirectAsync(world, artifactId, ArtifactCompletionVerdict.Accept(Cap));

        answer.IsSuccess.ShouldBeTrue(answer.Error?.Message);
        answer.Value.State.ShouldBe("ready");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("ready");

        // The stored-outcome answer is a no-op: no second scan or staging-delete row.
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(1);
    }
}
