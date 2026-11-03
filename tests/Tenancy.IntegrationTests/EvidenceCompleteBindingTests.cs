// 06.1.2-T2 review fixes: the completion is bound to the artifact's run (S-F1) and a replay answers the
// stored outcome whatever the project's status since (C-F4), through the real host, a real PostgreSQL and a
// real SeaweedFS S3 container (Testcontainers; never the shared dev stack). Expectations come from the review
// findings and the plan (06.1.2 §2 AC3/AC5/AC8, §5, §6), not from the current behaviour.
#pragma warning disable CA1861
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.1.2-T2: the two guards the review asked for — <c>complete</c> is bound to the artifact's run's CI
/// reference (S-F1), so a service token minted for another CI run of the same project gets the same 404 an
/// unknown id gets and can never force a terminal verdict on an upload in flight; and the stored
/// <c>ready</c>/<c>rejected</c> outcome is returned before the project-deleting guard (C-F4), so a replay
/// after the project is deleted answers the stored 200 while a still-pending row is refused 409
/// <c>project_deleting</c>.
/// </summary>
[Trait("Category", "Docker")]
public sealed class EvidenceCompleteBindingTests(PostgresTenancyFixture fixture, SeaweedFsStore store)
    : IClassFixture<SeaweedFsStore>
{
    /// <summary>A well-formed execution key: sha256 hex (00 §7).</summary>
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string ZipType = "application/zip";

    private const string ProducerId = "shard-1";

    /// <summary>The kind-policy cap every seeded policy row carries; the happy-path body is exactly this size.</summary>
    private const long Cap = 1024;

    /// <summary>The CI reference the seeded run is created from (T88); the minting token carries the same one.</summary>
    private const string RunCiRunRef = """{"provider":"github_actions","repository_id":1,"run_id":7,"run_attempt":2}""";

    /// <summary>The minting producer token's CI reference: <c>{repository_id}/{run_id}/{run_attempt}</c>.</summary>
    private static readonly CiRunReference MintingCiRun = new("github_actions", 1L, 7L, 2);

    /// <summary>Another CI run of the same repository: a different <c>ci_producer</c> token of the same project.</summary>
    private static readonly CiRunReference ForeignCiRun = new("github_actions", 1L, 8L, 2);

    /// <summary>The uploaded body: a zip signature then filler, exactly <see cref="Cap"/> bytes (the declared type is <c>application/zip</c>, 06.3.1-T2.2).</summary>
    private static readonly byte[] GoodBody = [0x50, 0x4B, 0x03, 0x04, .. Encoding.UTF8.GetBytes(new string('A', (int)Cap - 4))];

    private static readonly byte[] OtherBody = Encoding.UTF8.GetBytes(new string('B', (int)Cap));

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

    /// <summary>
    /// A service principal holding <c>artifact:write</c>, a token id (the slot-binding identity, T9) and,
    /// when it is a CI producer, the CI run its token was minted for (T88).
    /// </summary>
    private static RequestPrincipal Writer(string jti, CiRunReference? ciRun = null) =>
        RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: jti, ciRun: ciRun);

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

    /// <summary>One committed run, one planned execution and one unbound producer slot (as the T1/T2 suites seed).</summary>
    private async Task<Guid> SeedRunAsync(World world, string? ciRunRef = null)
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

            await scope.CommitAsync();
        }

        if (ciRunRef is not null)
        {
            // The run's CI reference (T88), written as the migration role: the app role has no UPDATE on `run`.
            await _fixture.ExecuteAsync(
                world.Database, $"update public.run set ci_run_ref = '{ciRunRef}'::jsonb where id = '{runId:D}'");
        }

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

    private async Task MarkProjectDeletedAsync(World world)
    {
        await _fixture.ExecuteAsync(
            world.Database, $"update public.project set status = 'deleted' where id = '{world.Project:D}'");
    }

    // ------------------------------------------------------------------ C-F4: stored outcome before the deleting guard

    /// <summary>
    /// C-F4: a completion committed <c>ready</c> replays its stored 200 after the project is deleted (AC5/AC8),
    /// while a still-pending artifact on that project is refused 409 <c>project_deleting</c>. Expected to FAIL if
    /// the project-deleting guard runs before the stored-outcome returns.
    /// </summary>
    [Fact]
    public async Task Complete_ReplayAfterProjectDeleted_ReturnsStoredOutcome_AndPendingIs409()
    {
        await using var world = await StartAsync(Writer("jti-binding-cf4"));
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var (readyId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);
        var (pendingId, _, pendingFinalKey) = await MintAndUploadAsync(world, runId, OtherBody);
        var key = NewKey();

        var first = await CompleteAsync(world, readyId, key);
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        first.Text("state").ShouldBe("ready");

        await MarkProjectDeletedAsync(world);

        // The replay returns the stored 200 body: the project's status since cannot turn a committed outcome into
        // a 409, and the verifier's project-deleting guard only refuses a row that could still be completed.
        var replay = await CompleteAsync(world, readyId, key);
        replay.Status.ShouldBe(HttpStatusCode.OK, replay.ErrorMessage);
        replay.Body.GetRawText().ShouldBe(first.Body.GetRawText());

        // A still-pending artifact on the deleted project is still refused, and nothing is promoted.
        var pending = await CompleteAsync(world, pendingId, NewKey());
        pending.Status.ShouldBe(HttpStatusCode.Conflict, pending.ErrorMessage);
        pending.ErrorCode.ShouldBe("project_deleting");
        (await ArtifactColumnAsync(world, pendingId, "state")).ShouldBe("pending_upload");
        (await _store.ExistsAsync(pendingFinalKey)).ShouldBeFalse(
            "a refused completion must not reach the storage calls");
    }

    // ------------------------------------------------------------------ S-F1: complete is bound to the artifact's run

    /// <summary>
    /// S-F1: a service token minted for another CI run of the same project gets the identical 404 an unknown id
    /// gets and cannot force a terminal verdict on the upload in flight — the artifact stays <c>pending_upload</c>
    /// and nothing is promoted — while the minting producer still completes it. Expected to FAIL if <c>complete</c>
    /// is not bound to the artifact's run.
    /// </summary>
    [Fact]
    public async Task Complete_ForeignCiRun_404_ArtifactUntouched_AndMintingProducerSucceeds()
    {
        var minting = Writer("jti-binding-mint", MintingCiRun);
        await using var world = await StartAsync(minting);
        var runId = await SeedRunAsync(world, RunCiRunRef);
        await InsertKindPolicyAsync(world);

        var (artifactId, _, finalKey) = await MintAndUploadAsync(world, runId, GoodBody);

        // Another ci_producer token of the same project, minted for a different CI run (T88): authorized for
        // artifact:write, but not the identity the artifact's run was created from.
        var foreign = Writer("jti-binding-foreign", ForeignCiRun);
        await using var foreignHost = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database), world.Org, world.Project, foreign, _store.Configuration());
        using var foreignClient = foreignHost.CreateClient();

        var refused = await foreignClient.SendAsync(
            HttpMethod.Post, $"/v1/artifacts/{artifactId:D}/complete", _ => { }, null, NewKey());

        refused.Status.ShouldBe(HttpStatusCode.NotFound, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("artifact_not_found");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse(
            "a foreign CI run must never reach the storage calls");

        var accepted = await CompleteAsync(world, artifactId, NewKey());
        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        accepted.Text("state").ShouldBe("ready");
        (await _store.ExistsAsync(finalKey)).ShouldBeTrue();
    }
}
