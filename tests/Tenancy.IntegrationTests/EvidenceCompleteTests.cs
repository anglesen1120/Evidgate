// 06.1.2-T2: POST /v1/artifacts/{a}/complete through the real host, a real PostgreSQL and a real
// SeaweedFS S3 container (Testcontainers; never the shared dev stack). The tests drive the HTTP
// surface of the 06.1.2 §5 contract — mint via /v1/artifacts/upload-url, a real HTTP PUT of the
// bytes to the presigned URL with the required headers, then complete — and assert the database
// state by SQL. Expectations come from the plan (06.1.2 §2 AC3/AC4/AC5/AC8, §5, §6, §7, §8, §9)
// and the coordinator rulings, not from the current behaviour.
#pragma warning disable CA1861
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Endpoints;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.1.2-T2 (AC3, AC4, AC5, AC8) through the real C2 pipeline, a real PostgreSQL and a real
/// SeaweedFS container: the completion of a presigned upload — <c>HEAD</c> + checksum verification,
/// the idempotent promotion to the final key, <c>ready</c>/<c>rejected</c> and the two outbox rows
/// written in the same transaction, with the staging object deleted only by the outbox task after
/// the commit (T86).
/// <para>
/// The object-store endpoint, bucket and credentials travel through the same in-memory
/// configuration seam the 06.1.2-T1 tests use (<see cref="SeaweedFsStore.Configuration"/>), pointing
/// at this class's bucket on the shared container; the caller is published by the same test startup filter T1
/// uses, because a service principal's <c>CredentialId</c> cannot be expressed through headers.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed class EvidenceCompleteTests(PostgresTenancyFixture fixture, SeaweedFsStore store)
    : IClassFixture<SeaweedFsStore>
{
    /// <summary>A well-formed execution key: sha256 hex (00 §7).</summary>
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string ZipType = "application/zip";

    /// <summary>The caller's token id (the slot-binding identity, T9).</summary>
    private const string Jti = "jti-complete-1";

    /// <summary>Another identity's token id, for the idempotency-conflict case.</summary>
    private const string OtherJti = "jti-complete-2";

    private const string ProducerId = "shard-1";

    /// <summary>The kind-policy cap every seeded policy row carries; the happy-path body is exactly this size.</summary>
    private const long Cap = 1024;

    private const string SecretScanTask = "artifact_secret_scan";

    private const string StagingDeleteTask = "artifact_staging_delete";

    /// <summary>The uploaded body on the happy path: a zip signature then filler, exactly <see cref="Cap"/> bytes (the declared type is <c>application/zip</c>, 06.3.1-T2.2).</summary>
    private static readonly byte[] GoodBody = [0x50, 0x4B, 0x03, 0x04, .. Encoding.UTF8.GetBytes(new string('A', (int)Cap - 4))];

    /// <summary>A second body of the same length but different content (the checksum-mismatch case).</summary>
    private static readonly byte[] OtherBody = Encoding.UTF8.GetBytes(new string('B', (int)Cap));

    /// <summary>A shorter body (the size-mismatch case).</summary>
    private static readonly byte[] ShortBody = Encoding.UTF8.GetBytes(new string('C', (int)Cap / 2));

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
    /// The shared database with this test's own org and a project created through the real registry routes, plus
    /// a host whose requests carry <paramref name="principal"/> and the object-store configuration.
    /// </summary>
    private async Task<World> StartAsync(RequestPrincipal principal, string? database = null)
    {
        database ??= await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
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
            _fixture.AppConnectionString(database), org, project, principal, _store.Configuration());

        return new World(api, host, host.CreateClient(), database, user, org, project);
    }

    /// <summary>A second org with a project, created through the real routes (the cross-tenant case).</summary>
    private static async Task<(Guid Org, Guid Project)> CreateOtherOrgAsync(World world, string slug)
    {
        using var setup = world.Api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(setup, user, slug);

        return (org, await CreateProjectAsync(setup, user, org, slug + "-project"));
    }

    /// <summary>
    /// One committed run, one planned execution and one producer slot for <paramref name="org"/>
    /// (through <see cref="RunRowSeeder"/>), the parents 0031's composite keys need.
    /// </summary>
    private async Task<Guid> SeedRunAsync(World world, Guid? org = null)
    {
        var owningOrg = org ?? world.Org;
        var runId = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, owningOrg);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), owningOrg);
        var testRevision = await scope.ScalarAsync<Guid>(
            "select id from public.test_revision where org_id = @org_id limit 1", ("org_id", owningOrg));

        (await scope.ExecuteAsync(
            "insert into public.planned_execution (id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id, required) "
            + "values (@id, @org_id, @run_id, @execution_key, @case_revision_id, @variant_hash, 'staging', 'shard-1', true)",
            ("id", Guid.CreateVersion7()), ("org_id", owningOrg), ("run_id", runId), ("execution_key", ExecutionKey),
            ("case_revision_id", testRevision), ("variant_hash", "sha256:" + new string('c', 64)))).ShouldBe(1);

        (await scope.ExecuteAsync(
            "insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id, bound_token_jti) "
            + "values (@id, @org_id, @run_id, 1, @producer_id, null)",
            ("id", Guid.CreateVersion7()), ("org_id", owningOrg), ("run_id", runId), ("producer_id", ProducerId))).ShouldBe(1);

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
                // Content-Length travels with the content; a different length is exactly what T18 refuses.
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

    private Task<long> TaskRowsAsync(World world, string taskType) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.task where task_type = '{taskType}' and org_id = '{world.Org:D}'");

    /// <summary>Runs one real outbox dispatch batch (01.5.1-T3) — the hop the API host itself never runs.</summary>
    private static async Task DispatchAsync(World world)
    {
        var dataSource = world.Host.Services.GetRequiredService<NpgsqlDataSource>();
        using var dispatcher = new OutboxDispatcher(
            dataSource, Options.Create(new TasksOptions()), NullLogger<OutboxDispatcher>.Instance);

        var dispatched = await dispatcher.DispatchBatchAsync(TestContext.Current.CancellationToken);
        dispatched.Enqueued.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// Invokes the registered <c>artifact_staging_delete</c> handler through DI with the exact payload
    /// the outbox row carries — the same call the task consumer makes, minus the queue. The handler is
    /// resolved by its own type instead of by enumerating every <see cref="ITaskHandlerInvoker"/>: the Api
    /// host composes the write path only, so enumerating them would materialise the module handlers whose
    /// Worker-only dependencies (e.g. <c>IWorkerSchedDataSource</c>) the Api never registers, and the
    /// Evidence handler would never be reached. The adapter is the same <c>TaskHandlerAdapter</c>
    /// <c>AddTaskHandler</c> registers, so the payload binding is the consumer's.
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

    /// <summary>
    /// Inserts one artifact row directly (as the migration role), for the scope cases that need a row
    /// the caller must not resolve. The run must already exist for <c>(org, project)</c>.
    /// </summary>
    private async Task<Guid> InsertArtifactAsync(World world, Guid org, Guid project, Guid runId, string state = "pending_upload")
    {
        var artifactId = Guid.CreateVersion7();
        var objectKey = $"pending/{org:D}/{artifactId:D}";

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.artifact (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type, checksum, sensitivity, scan_state, state) "
            + $"values ('{artifactId:D}', '{org:D}', '{project:D}', '{runId:D}', '{ExecutionKey}', 1, 'trace', '{objectKey}', '{ZipType}', '{Checksum(GoodBody)}', 'normal', 'pending', '{state}')");

        return artifactId;
    }

    // ------------------------------------------------------------------ AC4: the happy path

    [Fact]
    public async Task Upload_Complete_HappyPath()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var (artifactId, _, finalKey) = await MintAndUploadAsync(world, runId, GoodBody);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Id("artifact_id").ShouldBe(artifactId);
        answer.Text("state").ShouldBe("ready");
        answer.Body.GetProperty("bytes").GetInt64().ShouldBe(GoodBody.LongLength);
        answer.Text("scan_state").ShouldBe("pending");
        answer.Body.GetProperty("failure_code").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);

        // The row is ready with the FINAL key, the HEAD size and scan_state pending; the staging key is never stored.
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("ready");
        (await ArtifactColumnAsync(world, artifactId, "object_key")).ShouldBe(finalKey);
        (await _fixture.ScalarAsync<long>(world.Database, $"select bytes from public.artifact where id = '{artifactId:D}'"))
            .ShouldBe(GoodBody.LongLength);
        (await ArtifactColumnAsync(world, artifactId, "scan_state")).ShouldBe("pending");
        (await ArtifactColumnAsync(world, artifactId, "failure_code")).ShouldBeNull();

        // The verified bytes were promoted to the final key.
        (await _store.ReadAsync(finalKey)).ShouldBe(GoodBody);

        // Exactly one async-scan row and one staging-delete row, both in the same commit (T19/T86).
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ AC4: staging delete only after commit

    [Fact]
    public async Task Complete_StagingDelete_OnlyAfterCommit()
    {
        // The dispatcher claims undispatched outbox rows of every tenant, so this test needs a database of its own.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(Writer(), dedicated.Name);
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, GoodBody);
        (await _store.ExistsAsync(stagingKey)).ShouldBeTrue("the staging object must exist after the PUT");

        var answer = await CompleteAsync(world, artifactId, NewKey());
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        // AC4: the commit is the boundary — the staging object is still there, and the outbox row that
        // will delete it exists. The API host runs no dispatcher, so no task row exists yet.
        (await _store.ExistsAsync(stagingKey)).ShouldBeTrue(
            "the staging object must survive the commit: it is deleted by the outbox task, never before");
        (await _store.ExistsAsync(finalKey)).ShouldBeTrue("the verified bytes must be promoted before the commit");
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(1);
        (await TaskRowsAsync(world, StagingDeleteTask)).ShouldBe(0);

        // The outbox hop (01.5.1-T3) moves the rows into the task queue...
        await DispatchAsync(world);
        (await TaskRowsAsync(world, StagingDeleteTask)).ShouldBe(1);
        (await _store.ExistsAsync(stagingKey)).ShouldBeTrue("the dispatch alone must not delete the staging object");

        // ...and the async scan row lands too, but its handler is 06.2.3-T1: no consumer claims the type,
        // so the row stays queued and `ready` never waits for the scan (T19).
        (await TaskRowsAsync(world, SecretScanTask)).ShouldBe(1);
        (await _fixture.ScalarAsync<string?>(
            world.Database,
            $"select state from public.task where task_type = '{SecretScanTask}' and org_id = '{world.Org:D}'"))
            .ShouldBe("queued");

        // ...and the task handler deletes it, idempotently, after the commit.
        var payload = await StagingDeletePayloadAsync(world, artifactId);
        payload.ShouldNotBeNull();
        await RunStagingDeleteAsync(world, payload!);

        (await _store.ExistsAsync(stagingKey)).ShouldBeFalse("the staging-delete handler must remove the staging object");
        (await _store.ExistsAsync(finalKey)).ShouldBeTrue("the final object must never be touched by the staging delete");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("ready");
    }

    [Fact]
    public async Task Complete_WithoutCommit_StagingUntouched()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var (artifactId, stagingKey, _) = await MintAndUploadAsync(world, runId, GoodBody);

        // No complete: the commit never happens, so nothing is deleted and no work is enqueued.
        (await _store.ExistsAsync(stagingKey)).ShouldBeTrue();
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ AC3: size/checksum verification

    [Fact]
    public async Task Complete_ChecksumMismatch_Rejects()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        // Declare GoodBody, but write OtherBody (same length, different bytes) straight to the staging key,
        // bypassing the signed checksum header the presigned URL would have enforced.
        var minted = await UploadUrlAsync(world, UploadBody(runId, GoodBody), NewKey());
        minted.Status.ShouldBe(HttpStatusCode.OK, minted.ErrorMessage);
        var artifactId = minted.Id("artifact_id");
        var stagingKey = minted.Text("object_key")!;
        var finalKey = await ArtifactColumnAsync(world, artifactId, "object_key");
        await _store.PutAsync(stagingKey, OtherBody);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        // A rejection is a committed 200, not an error: the row persists with its reason.
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("checksum_mismatch");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("rejected");
        (await ArtifactColumnAsync(world, artifactId, "failure_code")).ShouldBe("checksum_mismatch");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
        (await _store.ExistsAsync(finalKey!)).ShouldBeFalse("rejected bytes must never become evidence");
    }

    [Fact]
    public async Task Complete_SizeMismatch_Rejects()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var minted = await UploadUrlAsync(world, UploadBody(runId, GoodBody), NewKey());
        minted.Status.ShouldBe(HttpStatusCode.OK, minted.ErrorMessage);
        var artifactId = minted.Id("artifact_id");
        var stagingKey = minted.Text("object_key")!;
        var finalKey = await ArtifactColumnAsync(world, artifactId, "object_key");
        await _store.PutAsync(stagingKey, ShortBody);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("size_mismatch");
        (await ArtifactColumnAsync(world, artifactId, "failure_code")).ShouldBe("size_mismatch");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
        (await _store.ExistsAsync(finalKey!)).ShouldBeFalse();
    }

    // ------------------------------------------------- counters: measured on the commit, never inside it

    /// <summary>
    /// The upload failure counter measures a committed verdict: the handler registers the measurement on the request's
    /// tenant scope, so the commit — and only the commit — counts the rejection, once and with its code. The refusal
    /// code is a member of the closed vocabulary no other suite completes with, since the instrument is process-wide.
    /// </summary>
    [Fact]
    public async Task Complete_Rejection_Is_Counted_Once_On_The_Commit()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var artifactId = await InsertArtifactAsync(world, world.Org, world.Project, runId);

        using var listener = IngestTelemetryCapture.ArtifactFailures(out var failures);
        var scopes = new RecordingTenantScopeAccessor(
            world.Host.Services.GetRequiredService<ITenantScopeAccessor>());

        var result = await CompleteDirectAsync(
            world,
            scopes,
            artifactId,
            ArtifactCompletionVerdict.Reject(ArtifactFailureCode.SecretRejected),
            commit: true);

        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());
        scopes.Last.ShouldNotBeNull();
        scopes.Last!.RegisteredCount.ShouldBe(1, "one rejected completion, one measurement registered for the commit");
        scopes.Last!.ExecutedCount.ShouldBe(1, "the commit is what runs the measurement");
        failures.Count(entry => entry == ("upload", ArtifactFailureCode.SecretRejected))
            .ShouldBe(1, "the rejection is counted once, after the commit");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("rejected");
    }

    /// <summary>
    /// A rolled-back attempt records nothing: the row it rejected never became durable, so the counter must not name
    /// it. The registration proves the attempt meant to measure; the missing commit is what cancels it, and the code
    /// is again one no other suite emits, so the absence below is this attempt's alone.
    /// </summary>
    [Fact]
    public async Task Complete_Rejection_RolledBack_Is_Not_Counted()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var artifactId = await InsertArtifactAsync(world, world.Org, world.Project, runId);

        using var listener = IngestTelemetryCapture.ArtifactFailures(out var failures);
        var scopes = new RecordingTenantScopeAccessor(
            world.Host.Services.GetRequiredService<ITenantScopeAccessor>());

        var result = await CompleteDirectAsync(
            world,
            scopes,
            artifactId,
            ArtifactCompletionVerdict.Reject(ArtifactFailureCode.CapExceeded),
            commit: false);

        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());
        scopes.Last.ShouldNotBeNull();
        scopes.Last!.RegisteredCount.ShouldBe(1, "the rejected attempt registered its measurement");
        scopes.Last!.ExecutedCount.ShouldBe(0, "a scope disposed without a commit runs no post-commit work");
        failures.ShouldNotContain(
            ("upload", ArtifactFailureCode.CapExceeded), "a rolled-back verdict is not a counted failure");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload", "the scope rolled back");
    }

    /// <summary>
    /// Runs the completion handler directly over a tenant scope this test owns, so the commit or the rollback is the
    /// test's own decision; the recording accessor it receives is how the counters above tell a deferred measurement
    /// from one taken inside the open transaction.
    /// </summary>
    /// <param name="world">The tenant the attempt runs as.</param>
    /// <param name="scopes">The ambient-scope accessor this attempt reads, recording what it registers.</param>
    /// <param name="artifactId">The artifact row the command addresses.</param>
    /// <param name="verdict">The verdict the completion verifier derives before this command commits it.</param>
    /// <param name="commit">Whether the attempt commits (otherwise it rolls back on disposal).</param>
    /// <returns>The handler's answer.</returns>
    private static async Task<Result<CompleteResponse>> CompleteDirectAsync(
        World world,
        ITenantScopeAccessor scopes,
        Guid artifactId,
        ArtifactCompletionVerdict verdict,
        bool commit)
    {
        var services = world.Host.Services;
        var handler = new CompleteArtifactUploadHandler(
            services.GetRequiredService<ITenantDbContextFactory<EvidenceDbContext>>(),
            services.GetRequiredService<IOutboxWriter>(),
            services.GetRequiredService<IAuditTrail>(),
            services.GetRequiredService<ITenantDb>(),
            services.GetRequiredService<IOptions<ParseRunnerOptions>>(),
            services.GetRequiredService<IRawReportProducerKindReader>(),
            scopes);

        var factory = services.GetRequiredService<ITenantScopeFactory>();
        var token = TestContext.Current.CancellationToken;

        await using var scope = await factory.BeginAsync(world.Org, world.Project, cancellationToken: token);
        var result = await handler.Handle(
            new CompleteArtifactUpload(artifactId, verdict, CredentialId: null, NewKey()), token);

        if (commit)
        {
            await scope.CommitAsync(token);
        }

        return result;
    }

    [Fact]
    public async Task Complete_MissingObject_Rejects()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        // Mint only: nothing was ever PUT, so neither the staging nor the final object exists.
        var minted = await UploadUrlAsync(world, UploadBody(runId, GoodBody), NewKey());
        minted.Status.ShouldBe(HttpStatusCode.OK, minted.ErrorMessage);
        var artifactId = minted.Id("artifact_id");

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("object_missing");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("rejected");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ AC5: idempotency and the retry path

    [Fact]
    public async Task Complete_Twice_SameBody_NoSecondOutboxRow()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);

        var first = await CompleteAsync(world, artifactId, NewKey());
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        first.Text("state").ShouldBe("ready");

        // A fresh key forces the handler to run again on an already-ready artifact (not a stored-response replay).
        var second = await CompleteAsync(world, artifactId, NewKey());
        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
        second.Text("state").ShouldBe("ready");
        second.Body.GetProperty("bytes").GetInt64().ShouldBe(first.Body.GetProperty("bytes").GetInt64());

        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(1);
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("ready");
    }

    [Fact]
    public async Task Complete_IdempotentReplay_SameBody()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);
        var key = NewKey();

        var first = await CompleteAsync(world, artifactId, key);
        var replay = await CompleteAsync(world, artifactId, key);

        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        replay.Status.ShouldBe(HttpStatusCode.OK, replay.ErrorMessage);
        replay.Id("artifact_id").ShouldBe(first.Id("artifact_id"));
        replay.Text("state").ShouldBe(first.Text("state"));
        replay.Body.GetProperty("bytes").GetInt64().ShouldBe(first.Body.GetProperty("bytes").GetInt64());
        replay.Body.GetRawText().ShouldBe(first.Body.GetRawText());
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(1);
    }

    [Fact]
    public async Task Complete_OnRejected_ReturnsCurrentState()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var minted = await UploadUrlAsync(world, UploadBody(runId, GoodBody), NewKey());
        var artifactId = minted.Id("artifact_id");
        await _store.PutAsync(minted.Text("object_key")!, OtherBody);

        var rejected = await CompleteAsync(world, artifactId, NewKey());
        rejected.Status.ShouldBe(HttpStatusCode.OK, rejected.ErrorMessage);
        rejected.Text("state").ShouldBe("rejected");
        rejected.Text("failure_code").ShouldBe("checksum_mismatch");

        // A second complete on a rejected artifact returns the current state, unchanged and without new work.
        var again = await CompleteAsync(world, artifactId, NewKey());
        again.Status.ShouldBe(HttpStatusCode.OK, again.ErrorMessage);
        again.Text("state").ShouldBe("rejected");
        again.Text("failure_code").ShouldBe("checksum_mismatch");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("rejected");
    }

    [Fact]
    public async Task Complete_StagingGone_FinalMatches_Commits()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        // Simulate a previous complete that copied staging → final but crashed before its commit: the final
        // object holds the declared bytes (same size, same checksum) and the staging object is gone.
        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, GoodBody);
        await _store.PutAsync(finalKey, GoodBody);
        await _store.DeleteAsync(stagingKey);
        (await _store.ExistsAsync(stagingKey)).ShouldBeFalse();
        (await _store.ExistsAsync(finalKey)).ShouldBeTrue();

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("ready");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("ready");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(1);
        (await _store.ReadAsync(finalKey)).ShouldBe(GoodBody);
    }

    // ------------------------------------------------------------------ AC8: Idempotency-Key

    [Fact]
    public async Task Complete_MissingIdempotencyKey_Rejected()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);

        var answer = await CompleteAsync(world, artifactId);

        answer.Status.ShouldBe(HttpStatusCode.BadRequest);
        answer.ErrorCode.ShouldBe("request_invalid");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
    }

    [Fact]
    public async Task Complete_DifferentPrincipalSameKey_409()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);
        var key = NewKey();

        var first = await CompleteAsync(world, artifactId, key);
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);

        // The caller's token identity is part of the idempotency hash (F3): another principal replaying the
        // same key is a conflict, never the first caller's stored response.
        await using var other = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database), world.Org, world.Project, Writer(OtherJti), _store.Configuration());
        using var otherClient = other.CreateClient();
        var conflict = await otherClient.SendAsync(
            HttpMethod.Post, $"/v1/artifacts/{artifactId:D}/complete", _ => { }, null, key);

        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("idempotency_conflict");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ isolation and state guards

    [Fact]
    public async Task Complete_CrossTenantArtifact_404()
    {
        await using var world = await StartAsync(Writer());
        var (otherOrg, otherProject) = await CreateOtherOrgAsync(world, "globex");
        var otherRun = await SeedRunAsync(world, otherOrg);
        var artifactId = await InsertArtifactAsync(world, otherOrg, otherProject, otherRun);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("artifact_not_found");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");
    }

    [Fact]
    public async Task Complete_OtherProjectArtifact_404()
    {
        await using var world = await StartAsync(Writer());
        using var setup = world.Api.CreateClient();
        var otherProject = await CreateProjectAsync(setup, world.User, world.Org, "other");
        var otherRun = await SeedRunAsync(world);
        await _fixture.ExecuteAsync(
            world.Database, $"update public.run set project_id = '{otherProject:D}' where id = '{otherRun:D}'");
        var artifactId = await InsertArtifactAsync(world, world.Org, otherProject, otherRun);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("artifact_not_found");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");
    }

    [Fact]
    public async Task Complete_OrgScopedPrincipal_403_ProjectRequired()
    {
        // The route names no project: a project-scoped principal (the CI producer token) is required, and an
        // org-scoped service principal is refused with project_required before any storage work (T1 decision 10).
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        Guid user;
        Guid org;
        Guid project;

        using (var setup = api.CreateClient())
        {
            user = Guid.CreateVersion7();
            org = await CreateOrgAsync(setup, user, Unique.Slug("acme"));
            project = await CreateProjectAsync(setup, user, org, Unique.Slug("checkout"));
        }

        using var client = api.CreateClient();
        var artifactId = Guid.CreateVersion7();
        var answer = await client.SendAsync(
            HttpMethod.Post,
            $"/v1/artifacts/{artifactId:D}/complete",
            TenancyApiFactory.Resolved(serviceOrg: org, servicePermissions: [Permissions.Artifact.Write]),
            null,
            NewKey());

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe("project_required");
        project.ShouldNotBe(Guid.Empty, "the org has a project, but the caller's credential names none");
    }

    [Fact]
    public async Task Complete_ProjectDeleted_409_project_deleting()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);
        await _fixture.ExecuteAsync(
            world.Database, $"update public.project set status = 'deleted' where id = '{world.Project:D}'");

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.Conflict);
        answer.ErrorCode.ShouldBe("project_deleting");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
    }

    [Fact]
    public async Task Complete_StateGuard_409()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        // A matching staging object is in place, so only the state guard can refuse the completion. The row reaches
        // `pending_delete` over the two edges the contract allows, because the trigger refuses the direct move from
        // `pending_upload`.
        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);
        await _fixture.ExecuteAsync(
            world.Database, $"update public.artifact set state = 'ready' where id = '{artifactId:D}'");
        await _fixture.ExecuteAsync(
            world.Database, $"update public.artifact set state = 'pending_delete' where id = '{artifactId:D}'");

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.Conflict);
        answer.ErrorCode.ShouldBe("artifact_state_conflict");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_delete");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ P3: bounded statements, redaction

    [Fact]
    public async Task Complete_BoundedStatements()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);

        world.Api.Statements.Clear();
        world.Host.Statements.Clear();
        var answer = await CompleteAsync(world, artifactId, NewKey());
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        // One completion is a constant number of statements (P3), derived from the code paths:
        //   verifier  — the row-facts read (the read-only scope's `set transaction read only; select set_config(…)`
        //               begin is dropped by the recorder's transaction-control filter, so it is not counted);
        //   writer    — the `select set_config(evidgate.org_id, …)` scope begin, the idempotency claim, the artifact
        //               read, the project-status read (review S-F3), the artifact UPDATE and the two outbox inserts;
        //   audit     — the lock_timeout set_config, the advisory lock, the chain-tip read and the audit insert;
        //   idempotency — the response completion.
        // That is 1 + (1 + 1 + 1 + 1 + 1 + 2) + 4 + 1 = 13 statements; 16 leaves a small margin for an EF or
        // pipeline variation without admitting an unbounded count. The storage HEAD/copy are network calls, never
        // SQL (P5).
        var statements = world.Host.Statements.Snapshot();
        statements.Count.ShouldBeGreaterThan(0);
        statements.Count.ShouldBeLessThanOrEqualTo(16);
    }

    [Fact]
    public async Task Complete_ErrorResponses_DoNotContainKeys()
    {
        await using var world = await StartAsync(Writer());

        var answer = await CompleteAsync(world, Guid.CreateVersion7(), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("artifact_not_found");

        var body = answer.Body.GetRawText();
        body.ShouldNotContain("pending/");
        body.ShouldNotContain("sha256:");
        body.ShouldNotContain("X-Amz");
        body.ShouldNotContain("upload_url");
    }
}
