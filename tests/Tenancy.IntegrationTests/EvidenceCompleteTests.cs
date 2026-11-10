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
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Evidence.Application;
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
/// <para>
/// The suite is split by responsibility into the <c>EvidenceCompleteTests.*.cs</c> partials: this
/// file holds the harness, its seeding helpers and the shared request helpers.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class EvidenceCompleteTests(PostgresTenancyFixture fixture, SeaweedFsStore store)
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
}
