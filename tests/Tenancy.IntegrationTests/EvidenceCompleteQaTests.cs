// 06.1.2-T2 QA: negative, boundary, replay, permission and concurrency cases for
// POST /v1/artifacts/{a}/complete, through the real host, a real PostgreSQL and a real SeaweedFS S3
// container (Testcontainers; never the shared dev stack). Expectations come from the plan (06.1.2 §2
// AC3/AC4/AC5/AC8, §5, §8, §9) and the coordinator rulings, not from the current behaviour. A failing test
// here is a defect to report, not a reason to weaken it.
#pragma warning disable CA1861
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Evidence.Application;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class EvidenceCompleteQaTests(PostgresTenancyFixture fixture, SeaweedFsStore store, TenancyHostPool hosts)
    : IClassFixture<SeaweedFsStore>, IClassFixture<TenancyHostPool>
{
    /// <summary>A well-formed execution key: sha256 hex (00 §7).</summary>
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string ZipType = "application/zip";

    private const string Jti = "jti-complete-qa-1";

    private const string ProducerId = "shard-1";

    /// <summary>The kind-policy cap every seeded policy row carries; the happy-path body is exactly this size.</summary>
    private const long Cap = 1024;

    private const string SecretScanTask = "artifact_secret_scan";

    private const string StagingDeleteTask = "artifact_staging_delete";

    /// <summary>The uploaded body: a zip signature then filler, exactly <see cref="Cap"/> bytes (the declared type is <c>application/zip</c>, 06.3.1-T2.2).</summary>
    private static readonly byte[] GoodBody = [0x50, 0x4B, 0x03, 0x04, .. Encoding.UTF8.GetBytes(new string('A', (int)Cap - 4))];

    private static readonly byte[] OtherBody = Encoding.UTF8.GetBytes(new string('B', (int)Cap));

    private readonly PostgresTenancyFixture _fixture = fixture;
    private readonly TenancyHostPool _hosts = hosts;
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

    private static RequestPrincipal Writer(string jti = Jti) =>
        RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: jti);

    private async Task<World> StartAsync(RequestPrincipal principal)
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = _hosts.RealResolver(database);
        Guid user;
        Guid org;
        Guid project;

        using (var setup = api.CreateClient())
        {
            user = Guid.CreateVersion7();
            org = await CreateOrgAsync(setup, user, Unique.Slug("acme"));
            project = await CreateProjectAsync(setup, user, org, Unique.Slug("checkout"));
        }

        var host = _hosts.WithPrincipal(
            database, org, project, principal, _store.Configuration());

        return new World(api, host, host.CreateClient(), database, user, org, project);
    }

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

    private async Task InsertKindPolicyAsync(World world)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity, capture_enabled) "
            + $"values ('{Guid.CreateVersion7()}', '{world.Org}', '{world.Project}', 'trace', {Cap}, array['{ZipType}'], 'normal', true)"))
            .ShouldBe(1);
        await scope.CommitAsync();
    }

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

    private Task<string?> StagingDeletePayloadAsync(World world, Guid artifactId) =>
        _fixture.ScalarAsync<string?>(
            world.Database,
            $"select payload::text from public.outbox where task_type = '{StagingDeleteTask}' and payload->>'artifact_id' = '{artifactId:D}'");

    /// <summary>
    /// Invokes the registered <c>artifact_staging_delete</c> handler with <paramref name="payloadJson"/>,
    /// the same call the task consumer makes minus the queue (see <c>EvidenceCompleteTests</c>).
    /// </summary>
    private static async Task RunStagingDeleteAsync(World world, string payloadJson)
    {
        var handler = world.Host.Services.GetRequiredService<ArtifactStagingDeleteTaskHandler>();
        var invoker = new TaskHandlerAdapter<ArtifactStagingDeletePayload>(handler);
        invoker.TaskType.ShouldBe(StagingDeleteTask);

        var factory = world.Host.Services.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await factory.BeginAsync(world.Org, world.Project, cancellationToken: TestContext.Current.CancellationToken);
        await invoker.InvokeAsync(payloadJson, scope, TestContext.Current.CancellationToken);
        await scope.CommitAsync(TestContext.Current.CancellationToken);
    }

    // ------------------------------------------------------------------ AC8: Idempotency-Key is mandatory

    /// <summary>
    /// 06.1.2-T2 AC8 / T86: a completion refused for a missing <c>Idempotency-Key</c> is a 400 that changes
    /// nothing. The staged bytes must NOT be promoted to the final key before that refusal.
    /// (Expected to FAIL if the verifier's HEAD/COPY runs before the pipeline's C2 validation.)
    /// </summary>
    [Fact]
    public async Task Complete_WithoutIdempotencyKey_PromotesNothing()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var (artifactId, _, finalKey) = await MintAndUploadAsync(world, runId, GoodBody);

        var answer = await CompleteAsync(world, artifactId, null);

        answer.Status.ShouldBe(HttpStatusCode.BadRequest, answer.ErrorMessage);
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse(
            "a request refused for a missing Idempotency-Key must not copy the staged bytes to the final key");
    }

    // ------------------------------------------------------------------ AC8: concurrent completes

    /// <summary>
    /// 06.1.2-T2 AC4/AC8: two concurrent completes of one artifact with DIFFERENT keys race on the same
    /// <c>pending_upload</c> row. Neither may fail with a server error; exactly one <c>artifact_secret_scan</c>
    /// outbox row may exist. (Expected to FAIL if the second outbox INSERT violates <c>outbox_org_idem_uq</c>
    /// and that 23505 is not mapped to a conflict.)
    /// </summary>
    [Fact]
    public async Task Complete_RacingWithDifferentKeys_NoServerError_OneOutboxRow()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);

        var answers = await Task.WhenAll(
            CompleteAsync(world, artifactId, NewKey()),
            CompleteAsync(world, artifactId, NewKey()));

        foreach (var answer in answers)
        {
            answer.Status.ShouldNotBe(HttpStatusCode.InternalServerError, answer.ErrorMessage);
        }

        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("ready");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
    }

    /// <summary>
    /// 06.1.2-T2 AC8: two concurrent completes with the SAME key are one logical request. Both must return the
    /// same outcome and exactly one outbox row may be written.
    /// </summary>
    [Fact]
    public async Task Complete_RacingWithSameKey_OneOutcome_OneOutboxRow()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);
        var key = NewKey();

        var answers = await Task.WhenAll(
            CompleteAsync(world, artifactId, key),
            CompleteAsync(world, artifactId, key));

        foreach (var answer in answers)
        {
            answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
            answer.Text("state").ShouldBe("ready");
        }

        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ AC5: size and cap boundaries

    /// <summary>06.1.2-T2 AC5: a staged object one byte larger than the declared size is rejected, never promoted.</summary>
    [Fact]
    public async Task Complete_SizeOneByteLarger_RejectsSizeMismatch()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, GoodBody);

        var larger = GoodBody.Concat(new byte[] { (byte)'Z' }).ToArray();
        await _store.PutAsync(stagingKey, larger);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("size_mismatch");
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse("rejected bytes must never become evidence");
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
    }

    /// <summary>06.1.2-T2 AC5: a staged object one byte shorter than the declared size is rejected, never promoted.</summary>
    [Fact]
    public async Task Complete_SizeOneByteShorter_RejectsSizeMismatch()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, GoodBody);

        await _store.PutAsync(stagingKey, GoodBody[..^1]);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("size_mismatch");
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse("rejected bytes must never become evidence");
    }

    /// <summary>
    /// 06.1.2-T2 AC5: the cap is re-checked at completion. An artifact minted under a 1024-byte cap that is
    /// lowered to 512 before completion is rejected as <c>too_large</c>.
    /// </summary>
    [Fact]
    public async Task Complete_CapLoweredAfterMint_RejectsTooLarge()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var (artifactId, _, finalKey) = await MintAndUploadAsync(world, runId, GoodBody);

        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.artifact_kind_policy set max_bytes = 512 where project_id = '{world.Project:D}'");

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("too_large");
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse();
    }

    // ------------------------------------------------------------------ AC8: replay after the staging object is gone

    /// <summary>
    /// 06.1.2-T2 AC8 / T1 decision 11: once a completion is <c>ready</c>, a replay with the same key returns the
    /// stored outcome with no storage call, even after the staging object has been deleted by the outbox task.
    /// </summary>
    [Fact]
    public async Task Complete_ReplayAfterStagingGone_ReturnsStoredOutcome()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, GoodBody);
        var key = NewKey();

        var first = await CompleteAsync(world, artifactId, key);
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);

        await _store.DeleteAsync(stagingKey);

        var replay = await CompleteAsync(world, artifactId, key);

        replay.Status.ShouldBe(HttpStatusCode.OK, replay.ErrorMessage);
        replay.Text("state").ShouldBe("ready");
        (await _store.ReadAsync(finalKey)).ShouldBe(GoodBody);
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ AC3/AC4: permission gate before storage

    /// <summary>
    /// 06.1.2-T2 AC4 / decision 2: a caller without <c>artifact:write</c> is refused with 403 before the verifier's
    /// HEAD or COPY. No final object is created and the row is untouched.
    /// </summary>
    [Fact]
    public async Task Complete_CallerWithoutArtifactWrite_403_PromotesNothing()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var (artifactId, _, finalKey) = await MintAndUploadAsync(world, runId, GoodBody);

        var viewer = RequestPrincipal.Service(projectScope: null, Array.Empty<string>(), credentialId: "jti-viewer-qa");
        await using var viewerHost = _hosts.WithPrincipal(
            world.Database, world.Org, world.Project, viewer, _store.Configuration());
        using var viewerClient = viewerHost.CreateClient();

        var answer = await viewerClient.SendAsync(
            HttpMethod.Post, $"/v1/artifacts/{artifactId:D}/complete", _ => { }, null, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.Forbidden, answer.ErrorMessage);
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse("a refused caller must not reach the storage calls");
    }

    // ------------------------------------------------------------------ T86: the staging delete never trusts the payload

    /// <summary>
    /// 06.1.2-T2 decision 7 / T86: the staging-delete handler re-derives the staging key from the org and artifact
    /// id and refuses any other key. A payload that names the FINAL key must be refused, and nothing deleted.
    /// </summary>
    [Fact]
    public async Task StagingDelete_PayloadNamingFinalKey_RefusedAndDeletesNothing()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, GoodBody);

        var complete = await CompleteAsync(world, artifactId, NewKey());
        complete.Status.ShouldBe(HttpStatusCode.OK, complete.ErrorMessage);

        var payload = await StagingDeletePayloadAsync(world, artifactId);
        payload.ShouldNotBeNull();
        var crafted = payload!.Replace(stagingKey, finalKey, StringComparison.Ordinal);
        crafted.ShouldNotBe(payload);

        Exception? thrown = null;
        try
        {
            await RunStagingDeleteAsync(world, crafted);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        thrown.ShouldNotBeNull("the handler must refuse a key it did not derive from the org and artifact id");
        (await _store.ExistsAsync(finalKey)).ShouldBeTrue("the final object must never be deleted by a staging delete");
        (await _store.ExistsAsync(stagingKey)).ShouldBeTrue("a refused payload must not delete the staging object either");
    }
}
