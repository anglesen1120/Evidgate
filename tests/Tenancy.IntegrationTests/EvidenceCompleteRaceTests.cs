// 06.1.2-T2 D2: the concurrent-completion race of POST /v1/artifacts/{a}/complete, through the real
// host, a real PostgreSQL and a real SeaweedFS S3 container (Testcontainers). The interleaving is FORCED
// with a row lock the test holds on the artifact, so both completes pass their verifier, read the
// still-pending row and block inside the handler's artifact UPDATE — not hoped for with Task.WhenAll.
// Expectations come from the plan (06.1.2 §2 AC4/AC5/AC8, T19/T86) and the coordinator ruling: the loser
// of the race is the idempotent no-op that answers the winner's committed outcome, never a 500 and never
// a second outbox row.
#pragma warning disable CA1861
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.1.2-T2 (AC4/AC5/AC8): two completes of one <c>pending_upload</c> artifact with DIFFERENT
/// <c>Idempotency-Key</c>s race on the same row. The test holds the artifact row's lock in its own
/// <see cref="TenantScope"/> and releases it only once both requests are observably blocked on it, so the
/// race window is entered deterministically. The loser must answer the winner's stored <c>ready</c> state
/// (one <c>artifact_secret_scan</c> and one <c>artifact_staging_delete</c> row), and the defensive 23505
/// mapping of the outbox dedupe keys must answer 409, never a 500.
/// </summary>
[Trait("Category", "Docker")]
public sealed class EvidenceCompleteRaceTests(PostgresTenancyFixture fixture, SeaweedFsStore store)
    : IClassFixture<SeaweedFsStore>
{
    /// <summary>A well-formed execution key: sha256 hex (00 §7).</summary>
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string ZipType = "application/zip";

    private const string Jti = "jti-complete-race-1";

    private const string ProducerId = "shard-1";

    /// <summary>The kind-policy cap every seeded policy row carries; the happy-path body is exactly this size.</summary>
    private const long Cap = 1024;

    private const string SecretScanTask = "artifact_secret_scan";

    private const string StagingDeleteTask = "artifact_staging_delete";

    /// <summary>The uploaded body: a zip signature then filler, exactly <see cref="Cap"/> bytes (the declared type is <c>application/zip</c>, 06.3.1-T2.2).</summary>
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

    private static RequestPrincipal Writer(string jti = Jti) =>
        RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: jti);

    private async Task<World> StartAsync(RequestPrincipal principal)
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        Guid user;
        Guid org;
        Guid project;

        using (var setup = api.CreateClient())
        {
            user = Guid.CreateVersion7();
            org = await CreateOrgAsync(setup, user, "acme");
            project = await CreateProjectAsync(setup, user, org, "checkout");
        }

        var host = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(database), org, project, principal, _store.Configuration());

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
    /// Blocks until <paramref name="expected"/> backends of this test's database are observably waiting on a lock
    /// (the artifact row lock the caller holds), so the race window is entered deterministically instead of hoped
    /// for. Fails with the reason after 30 s rather than releasing the lock and racing by accident.
    /// </summary>
    private async Task WaitForBlockedAsync(World world, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            var blocked = await _fixture.ScalarAsync<long>(
                world.Database,
                "select count(*) from pg_stat_activity "
                + $"where datname = '{world.Database}' and wait_event_type = 'Lock'");

            if (blocked >= expected)
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException(
            $"the {expected} concurrent completes never blocked on the artifact row lock within 30 s");
    }

    // ------------------------------------------------------------------ D2: the forced race

    /// <summary>
    /// 06.1.2-T2 AC4/AC5/AC8: two concurrent completes with DIFFERENT keys on one artifact, both forced into
    /// the handler's critical section. Neither may 500; both answer the committed <c>ready</c> outcome (the
    /// loser re-reads it, the idempotent no-op of AC5); exactly one <c>artifact_secret_scan</c> and one
    /// <c>artifact_staging_delete</c> row exist.
    /// </summary>
    [Fact]
    public async Task Complete_ConcurrentDifferentKeys_ForcedRace_LoserReturnsStoredReady()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var (artifactId, _, finalKey) = await MintAndUploadAsync(world, runId, GoodBody);

        // Hold the artifact row's lock in the test's own scope, on the owner connection (the precedent of
        // PolicyActivationConcurrencyTests: a lock holder bypasses the app role). `FOR UPDATE` takes the row lock
        // both completes must take before they can write, and — unlike a locking UPDATE — it never touches the
        // version column, so the loser's optimistic `where version = …` still matches the value it read.
        var gate = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        Task<ApiAnswer> first;
        Task<ApiAnswer> second;

        try
        {
            (await gate.ScalarAsync<int>(
                "select 1 from public.artifact where id = @artifact_id for update",
                ("artifact_id", artifactId))).ShouldBe(1);

            first = CompleteAsync(world, artifactId, NewKey());
            second = CompleteAsync(world, artifactId, NewKey());

            // Both requests have passed the verifier and are blocked inside the handler's artifact UPDATE;
            // release the gate only now, so the winner and the loser both act on the same still-pending row.
            await WaitForBlockedAsync(world, expected: 2);
        }
        finally
        {
            // Disposing rolls the scope back, which releases the row lock.
            await gate.DisposeAsync();
        }

        var answers = await Task.WhenAll(first, second);

        foreach (var answer in answers)
        {
            answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
            answer.Text("state").ShouldBe("ready");
        }

        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("ready");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(1);
        (await _store.ReadAsync(finalKey)).ShouldBe(GoodBody);
    }

    /// <summary>
    /// 06.1.2-T2 AC4 / T19: the outbox dedupe key of the scan is per artifact (<c>outbox_org_idem_uq</c>,
    /// migration 0006). A row already carrying it makes the handler's own INSERT raise SQLSTATE 23505; that
    /// must answer the loser path (409 <c>artifact_state_conflict</c>), never a 500, and the artifact update
    /// must roll back with it.
    /// </summary>
    [Fact]
    public async Task Complete_OutboxDedupeKeyAlreadyTaken_409_NotServerError()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);

        // The outbox table has no row policy (it is a mechanism table) and only an INSERT grant for the app role,
        // so the row is planted as the migration role. Its idempotency key is exactly the one the handler derives.
        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.outbox (id, org_id, aggregate_type, aggregate_id, task_type, payload, idempotency_key) "
            + $"values ('{Guid.CreateVersion7():D}', '{world.Org:D}', 'artifact', '{artifactId:D}', '{SecretScanTask}', "
            + $"'{JsonSerializer.Serialize(new { artifact_id = artifactId })}'::jsonb, 'evidence:artifact:{artifactId:D}:secret_scan')");

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.Conflict, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("artifact_state_conflict");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
    }
}
