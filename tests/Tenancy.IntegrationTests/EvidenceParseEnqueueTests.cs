// 06.3.6-T1.4 on the scheduling half of AC3: the completion's third outbox row. The tests drive the real host and the
// real complete path over a real PostgreSQL and a real SeaweedFS container (never the shared dev stack), and assert the
// outbox by SQL. Expectations come from the plan (06.3.6 §5, §11 rollout) and the coordinator's decision: the row is
// written only for `raw_report` and only with `EVIDGATE__PARSE__OUTOFPROCESS` on, in the completion's own transaction.
#pragma warning disable CA1861
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The enqueue half of 06.3.6-T1.4: <c>POST /v1/artifacts/{a}/complete</c> writes the <c>artifact_parse</c> row for a
/// <c>raw_report</c> artifact when out-of-process parsing is composed, and nothing extra otherwise — the two existing
/// rows the completion already wrote for a verified artifact are untouched, so the flag-off composition is the
/// in-process rollback of the parse switch. The same switch also schedules the report's mapping row, which has its
/// own suite.
/// </summary>
/// <remarks>
/// The whole path is production: the mint, the presigned <c>PUT</c>, the check/copy verification, the conditional state
/// change and the outbox writes in one transaction. The only thing the test chooses is the flag in the host's
/// configuration, which is exactly the switch the module binds for the completion to read.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class EvidenceParseEnqueueTests(PostgresTenancyFixture fixture, SeaweedFsStore store, TenancyHostPool hosts)
    : IClassFixture<SeaweedFsStore>, IClassFixture<TenancyHostPool>
{
    /// <summary>A well-formed execution key: sha256 hex (00 §7).</summary>
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string ProducerId = "shard-1";

    /// <summary>The kind-policy cap the seeded <c>raw_report</c> policy carries; the report body is exactly this size.</summary>
    private const long Cap = 1024;

    private const string XmlType = "application/xml";

    private const string ZipType = "application/zip";

    private const string SecretScanTask = "artifact_secret_scan";

    private const string StagingDeleteTask = "artifact_staging_delete";

    private const string ParseTask = "artifact_parse";

    /// <summary>The flag the module binds (06.3.6 §11): on schedules the parse, off is the in-process path.</summary>
    private const string OutOfProcessKey = "EVIDGATE:PARSE:OUTOFPROCESS";

    /// <summary>
    /// The report body: a well-formed XML prolog and document, padded to exactly <see cref="Cap"/> bytes so the declared
    /// size and the stored size agree (the completion refuses a mismatch, T18).
    /// </summary>
    private static readonly byte[] ReportBody = PadToCap("<?xml version=\"1.0\" encoding=\"utf-8\"?><report><testcase name=\"checkout\"/></report>");

    /// <summary>A zip-signature body for the control case: another kind schedules no parse.</summary>
    private static readonly byte[] ZipBody = [0x50, 0x4B, 0x03, 0x04, .. Encoding.UTF8.GetBytes(new string('A', (int)Cap - 4))];

    private readonly PostgresTenancyFixture _fixture = fixture;

    private readonly SeaweedFsStore _store = store;

    private sealed record World(
        TenancyApiFactory Api,
        TenancyApiFactory Host,
        HttpClient Client,
        string Database,
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

    /// <summary>
    /// With the flag off, a completed <c>raw_report</c> writes exactly the two rows 06.1.2-T2 defines and no parse row:
    /// the in-process path is the only one composed (06.3.6 §11).
    /// </summary>
    [Fact]
    public async Task Complete_With_The_Flag_Off_Schedules_Only_The_Two_Rows()
    {
        await using var world = await StartAsync(outOfProcess: false);
        var (artifactId, runId) = await MintAndUploadAsync(world, "raw_report", XmlType, ReportBody, Cap);

        await CompleteSucceedsAsync(world, artifactId);

        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(1);
        (await OutboxCountAsync(world, ParseTask, artifactId)).ShouldBe(0, "the flag is off: nothing is parsed out of process");
        (await ScheduleCountAsync(world, artifactId)).ShouldBe(2, "the completion schedules exactly what 06.1.2-T2 introduced");
        runId.ShouldNotBe(Guid.Empty);
    }

    /// <summary>
    /// With the flag on, the same completion adds the parse row: type <c>artifact_parse</c>, payload exactly
    /// <c>{artifact_id}</c>, idempotency key per artifact — and a replayed completion adds no second row.
    /// </summary>
    [Fact]
    public async Task Complete_With_The_Flag_On_Schedules_The_Parse_Of_A_Raw_Report()
    {
        await using var world = await StartAsync(outOfProcess: true);
        var (artifactId, _) = await MintAndUploadAsync(world, "raw_report", XmlType, ReportBody, Cap);

        await CompleteSucceedsAsync(world, artifactId);

        (await OutboxCountAsync(world, ParseTask, artifactId)).ShouldBe(1);
        (await OutboxPayloadAsync(world, ParseTask, artifactId)).ShouldBe(ParsePayload(artifactId));
        (await OutboxIdempotencyKeyAsync(world, ParseTask, artifactId))
            .ShouldBe($"evidence:artifact:{artifactId:D}:parse");
        (await ScheduleCountAsync(world, artifactId))
            .ShouldBe(4, "the two 06.1.2 rows, the artifact parse and the report's mapping row");

        // The same completion replayed (same Idempotency-Key) answers from the stored outcome and adds no row.
        await CompleteSucceedsAsync(world, artifactId);
        (await OutboxCountAsync(world, ParseTask, artifactId)).ShouldBe(1, "one artifact schedules at most one parse");
    }

    /// <summary>
    /// With the flag on, a kind that has no parse arm schedules none: the third row is <c>raw_report</c>-only, so a
    /// <c>trace</c> completion is byte-for-byte the 06.1.2-T2 behaviour.
    /// </summary>
    [Fact]
    public async Task Complete_With_The_Flag_On_Schedules_No_Parse_For_Another_Kind()
    {
        await using var world = await StartAsync(outOfProcess: true);
        var (artifactId, _) = await MintAndUploadAsync(world, "trace", ZipType, ZipBody, Cap);

        await CompleteSucceedsAsync(world, artifactId);

        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(1);
        (await OutboxCountAsync(world, ParseTask, artifactId)).ShouldBe(0, "only raw_report has a parse arm");
        (await ScheduleCountAsync(world, artifactId)).ShouldBe(2);
    }

    /// <summary>
    /// A rejected completion schedules no parse either: the artifact never became evidence, so there is nothing to parse
    /// and the parse row is written only by the ready path.
    /// </summary>
    [Fact]
    public async Task Complete_That_Rejects_Schedules_No_Parse()
    {
        await using var world = await StartAsync(outOfProcess: true);
        var (artifactId, _) = await MintAndUploadAsync(world, "raw_report", XmlType, ReportBody, Cap);

        // The bytes the store holds differ from the ones the declaration signed, so the completion refuses the artifact.
        await _store.PutAsync($"pending/{world.Org:D}/{artifactId:D}", Encoding.UTF8.GetBytes(new string('Z', (int)Cap)));
        var answer = await CompleteAsync(world, artifactId, NewKey());

        // A rejection is a committed 200, not an error (06.1.2-T2): the row persists with its reason and no work is
        // scheduled, because nothing became evidence.
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("checksum_mismatch");
        (await OutboxCountAsync(world, ParseTask, artifactId)).ShouldBe(0);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------------------------

    private async Task<World> StartAsync(bool outOfProcess)
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = hosts.RealResolver(database);
        Guid org;
        Guid project;

        using (var setup = api.CreateClient())
        {
            var user = Guid.CreateVersion7();
            org = await CreateOrgAsync(setup, user, Unique.Slug("acme"));
            project = await CreateProjectAsync(setup, user, org, Unique.Slug("checkout"));
        }

        var configuration = _store.Configuration();
        configuration[OutOfProcessKey] = outOfProcess ? "true" : "false";

        var host = hosts.WithPrincipalConfigured(
            database,
            org,
            project,
            RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: "jti-parse-enqueue"),
            configuration);

        return new World(api, host, host.CreateClient(), database, org, project);
    }

    private static byte[] PadToCap(string prefix)
    {
        var body = new byte[Cap];
        var head = Encoding.UTF8.GetBytes(prefix);
        head.CopyTo(body, 0);

        for (var i = head.Length; i < body.Length; i++)
        {
            body[i] = (byte)' ';
        }

        return body;
    }

    private static string Checksum(byte[] body) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(body));

    private static string ParsePayload(Guid artifactId) => $"{{\"artifact_id\": \"{artifactId:D}\"}}";

    /// <summary>
    /// Mints one artifact of <paramref name="kind"/> and puts its bytes through the presigned URL. The kind policy row
    /// for the kind is seeded first, because the mint resolves the effective policy before it signs.
    /// </summary>
    /// <param name="world">The tenant.</param>
    /// <param name="kind">The artifact kind.</param>
    /// <param name="mediaType">Its declared media type.</param>
    /// <param name="body">The bytes, exactly the declared length.</param>
    /// <param name="cap">The policy's <c>max_bytes</c>.</param>
    private async Task<(Guid ArtifactId, Guid RunId)> MintAndUploadAsync(
        World world,
        string kind,
        string mediaType,
        byte[] body,
        long cap)
    {
        var runId = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
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

            (await scope.ExecuteAsync(
                "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity, capture_enabled) "
                + "values (@id, @org_id, @project_id, @kind, @max_bytes, array[@media_type]::text[], 'normal', true)",
                ("id", Guid.CreateVersion7()), ("org_id", world.Org), ("project_id", world.Project), ("kind", kind),
                ("max_bytes", cap), ("media_type", mediaType))).ShouldBe(1);

            await scope.CommitAsync();
        }

        var minted = await world.Client.SendAsync(
            HttpMethod.Post,
            "/v1/artifacts/upload-url",
            _ => { },
            new
            {
                run_id = runId,
                producer_id = ProducerId,
                execution_key = ExecutionKey,
                attempt_no = 1,
                kind,
                media_type = mediaType,
                bytes = body.LongLength,
                sha256 = Checksum(body),
            },
            NewKey());

        minted.Status.ShouldBe(HttpStatusCode.OK, minted.ErrorMessage);

        var artifactId = minted.Id("artifact_id");
        using var put = await PutAsync(minted, body);
        put.IsSuccessStatusCode.ShouldBeTrue($"the signed PUT must be accepted; status={(int)put.StatusCode}");

        return (artifactId, runId);
    }

    /// <summary>Sends the bytes to the presigned URL with exactly the headers the mint answered.</summary>
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

    private static async Task CompleteSucceedsAsync(World world, Guid artifactId)
    {
        var answer = await CompleteAsync(world, artifactId, NewKey());
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("ready");
    }

    private static Task<ApiAnswer> CompleteAsync(World world, Guid artifactId, string? idempotencyKey = null) =>
        world.Client.SendAsync(HttpMethod.Post, $"/v1/artifacts/{artifactId:D}/complete", _ => { }, null, idempotencyKey);

    private Task<long> OutboxCountAsync(World world, string taskType, Guid artifactId) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where task_type = '{taskType}' and payload->>'artifact_id' = '{artifactId:D}'");

    /// <summary>
    /// The row's work payload without the correlation carrier the outbox writer adds (10.3.1-T1.2): the task's own
    /// shape is what these tests constrain, and the carrier is platform-wide and asserted elsewhere.
    /// </summary>
    private Task<string?> OutboxPayloadAsync(World world, string taskType, Guid artifactId) =>
        _fixture.ScalarAsync<string?>(
            world.Database,
            $"select (payload - '_evidgate_correlation')::text from public.outbox where task_type = '{taskType}' and payload->>'artifact_id' = '{artifactId:D}'");

    private Task<string?> OutboxIdempotencyKeyAsync(World world, string taskType, Guid artifactId) =>
        _fixture.ScalarAsync<string?>(
            world.Database,
            $"select idempotency_key from public.outbox where task_type = '{taskType}' and payload->>'artifact_id' = '{artifactId:D}'");

    private Task<long> ScheduleCountAsync(World world, Guid artifactId) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where aggregate_id = '{artifactId:D}'");
}
