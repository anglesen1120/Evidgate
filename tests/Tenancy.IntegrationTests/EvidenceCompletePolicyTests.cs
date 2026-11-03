// 06.1.2-T2 policy-recheck coverage: the completion re-reads the project's artifact_kind_policy row
// after the mint (ArtifactCompletionVerifier.RowFactsSql joins it, and the verifier re-checks the cap,
// the accepted media types and the capture switch before any storage call), so a policy edited between
// the mint and the completion decides the outcome. AC3 names `media_type_mismatch` but nothing else in
// the suite exercises the verifier's MediaTypeAllowed rejection or the complete-time kind_disabled
// refusal, so both are covered here through the real host, a real PostgreSQL and a real SeaweedFS S3
// container (Testcontainers; never the shared dev stack). Each test creates its own org and project, so
// the policy edits never leak into a sibling test. Expectations come from the plan (06.1.2 §2 AC3, §5,
// §8) and the coordinator rulings, not from the current behaviour.
#pragma warning disable CA1861
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.1.2-T2 (AC3, §5): the kind policy is re-read at completion, so a policy changed after the mint
/// decides the outcome — a media type that is no longer accepted rejects the artifact with
/// <c>media_type_mismatch</c> (a committed 200, refused before any storage call and with no outbox row),
/// and a kind whose capture was switched off after the mint is refused with 422 <c>kind_disabled</c>
/// (the artifact stays <c>pending_upload</c> and nothing is promoted).
/// </summary>
[Trait("Category", "Docker")]
public sealed class EvidenceCompletePolicyTests(PostgresTenancyFixture fixture, SeaweedFsStore store)
    : IClassFixture<SeaweedFsStore>
{
    /// <summary>A well-formed execution key: sha256 hex (00 §7).</summary>
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string ZipType = "application/zip";

    /// <summary>The caller's token id (the slot-binding identity, T9).</summary>
    private const string Jti = "jti-complete-policy-1";

    private const string ProducerId = "shard-1";

    /// <summary>The kind-policy cap every seeded policy row carries; the uploaded body is exactly this size.</summary>
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

    /// <summary>A service principal holding <c>artifact:write</c> and a token id (the slot-binding identity).</summary>
    private static RequestPrincipal Writer(string jti = Jti) =>
        RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: jti);

    /// <summary>
    /// One migrated database with an org and a project created through the real registry routes, plus a
    /// host whose requests carry the writer principal and the object-store configuration. A fresh database
    /// per test is what keeps the policy edits local (no shared policy row leaks into a sibling test).
    /// </summary>
    private async Task<World> StartAsync()
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
            _fixture.AppConnectionString(database), org, project, Writer(), _store.Configuration());

        return new World(api, host, host.CreateClient(), database, user, org, project);
    }

    /// <summary>
    /// One committed run, one planned execution and one producer slot for <paramref name="world"/>'s org
    /// (through <see cref="RunRowSeeder"/>), the parents the artifact mint needs.
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

    /// <summary>
    /// One committed kind-policy row for <paramref name="world"/>'s project, as the app role: cap
    /// <see cref="Cap"/>, only <see cref="ZipType"/> accepted, capture on.
    /// </summary>
    private async Task InsertKindPolicyAsync(World world)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity, capture_enabled) "
            + $"values ('{Guid.CreateVersion7()}', '{world.Org}', '{world.Project}', 'trace', {Cap}, array['{ZipType}'], 'normal', true)"))
            .ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>
    /// Edits the project's kind-policy row through the fixture's owner connection (the same seam the
    /// sibling QA suite uses to lower the cap): the completion re-reads this row, so the edit is exactly
    /// what the verifier sees, the way a policy change between the mint and the completion would be.
    /// </summary>
    private Task UpdateKindPolicyAsync(World world, string setClause) =>
        _fixture.ExecuteAsync(
            world.Database,
            $"update public.artifact_kind_policy set {setClause} where project_id = '{world.Project:D}'");

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

    // ------------------------------------------------------------------ AC3: media type re-checked at completion

    /// <summary>
    /// 06.1.2-T2 AC3: the declared media type is re-checked at completion against the kind policy as it
    /// stands then. An artifact minted while <c>application/zip</c> was accepted, whose policy is narrowed
    /// to <c>text/plain</c> before the completion, is a committed 200 <c>state: rejected</c> with
    /// <c>failure_code: media_type_mismatch</c> — the verifier refuses it before the <c>HEAD</c>/copy, so
    /// the final object is never created, the staging object is untouched and no outbox row is written.
    /// </summary>
    [Fact]
    public async Task Complete_MediaTypeNoLongerAllowed_RejectsMediaTypeMismatch_NoStorageCall()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, GoodBody);
        (await _store.ExistsAsync(stagingKey)).ShouldBeTrue("the staging object must exist after the PUT");

        // Narrow the policy so the artifact's declared media type (application/zip) is no longer accepted.
        await UpdateKindPolicyAsync(world, "media_types = array['text/plain']");

        var answer = await CompleteAsync(world, artifactId, NewKey());

        // A policy mismatch is a committed 200 rejection, never an error response.
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("media_type_mismatch");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("rejected");
        (await ArtifactColumnAsync(world, artifactId, "failure_code")).ShouldBe("media_type_mismatch");

        // The verifier refuses before the storage calls: nothing is promoted and the staging object survives.
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse("a rejected media type must never become evidence");
        (await _store.ExistsAsync(stagingKey)).ShouldBeTrue("the rejection happens before any storage call");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ AC3: capture switch re-checked at completion

    /// <summary>
    /// 06.1.2-T2 AC3 / §5: a kind whose capture is switched off between the mint and the completion is
    /// refused with 422 <c>kind_disabled</c> (the policy fails closed exactly as at mint), the artifact
    /// stays <c>pending_upload</c> and nothing is promoted to the final key.
    /// </summary>
    [Fact]
    public async Task Complete_KindDisabled_422_NoPromotion()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, GoodBody);

        // Switch capture off for the kind after the mint: the completion re-reads the policy and fails closed.
        await UpdateKindPolicyAsync(world, "capture_enabled = false");

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("kind_disabled");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse("a disabled kind must not be promoted");
        (await _store.ExistsAsync(stagingKey)).ShouldBeTrue("the refusal happens before any storage call");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
    }
}
