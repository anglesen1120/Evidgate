using System.Net;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Evidence.Domain;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Sweeper-style writes racing POST /v1/artifacts/{a}/complete through the real host, a real PostgreSQL and a real
/// object store. Each interleaving is forced: the test holds the artifact row lock, the completion passes its verifier
/// and blocks inside the handler's UPDATE, and only then does the competing write commit. The version token makes the
/// completion's update match no row, so the loser re-reads the committed row: <c>pending_delete</c> is the 409
/// <c>artifact_state_conflict</c> (never a 500), and a committed <c>rejected</c> outcome is answered verbatim.
/// </summary>
[Trait("Category", "Docker")]
public sealed class EvidenceArtifactTransitionRaceTests(PostgresTenancyFixture fixture, SeaweedFsStore store)
    : IClassFixture<SeaweedFsStore>
{
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string ZipType = "application/zip";

    private const string Jti = "jti-artifact-transition-race";

    private const long Cap = 1024;

    /// <summary>The uploaded body: a zip signature then filler, exactly <see cref="Cap"/> bytes.</summary>
    private static readonly byte[] Body = [0x50, 0x4B, 0x03, 0x04, .. Encoding.UTF8.GetBytes(new string('A', (int)Cap - 4))];

    /// <summary>The declared content address of <see cref="Body"/>.</summary>
    private static readonly string BodyChecksum = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Body));

    private static readonly string[] WritePermission = [Permissions.Artifact.Write];

    private readonly PostgresTenancyFixture _fixture = fixture;

    private readonly SeaweedFsStore _store = store;

    private sealed record World(
        TenancyApiFactory Api,
        TenancyApiFactory Host,
        HttpClient Client,
        DedicatedDatabase Owned,
        Guid Org,
        Guid Project) : IAsyncDisposable
    {
        public string Database => Owned.Name;

        public ValueTask DisposeAsync() => WorldDisposal.DisposeAsync(Client, Owned, Host, Api);
    }

    private static RequestPrincipal Writer() =>
        RequestPrincipal.Service(projectScope: null, WritePermission, credentialId: Jti);

    private Task<World> StartAsync(RequestPrincipal principal) =>
        _fixture.StartDedicatedAsync(async owned =>
        {
            var database = owned.Name;
            // Every world here runs on its own database, so its hosts are private rather than pooled.
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

            return new World(api, host, host.CreateClient(), owned, org, project);
        });

    private Task<Guid> SeedRunAsync(World world) =>
        RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);

    private async Task InsertKindPolicyAsync(World world)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity, capture_enabled) "
            + $"values ('{Guid.CreateVersion7()}', '{world.Org}', '{world.Project}', '{ArtifactKind.Trace}', {Cap}, "
            + $"array['{ZipType}'], '{ArtifactSensitivity.Normal}', true)"))
            .ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>One pending artifact whose bytes are already at the final key — the verifier's retry path.</summary>
    private async Task<Guid> SeedArtifactAsync(World world, Guid runId)
    {
        var artifactId = Guid.CreateVersion7();
        var objectKey =
            $"org/{world.Org:D}/project/{world.Project:D}/run/{runId:D}/attempt/{ExecutionKey}/1/{artifactId:D}";

        await _store.PutAsync(objectKey, Body);

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.artifact (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, "
            + "media_type, checksum, sensitivity, scan_state, state, bytes) "
            + $"values ('{artifactId:D}', '{world.Org:D}', '{world.Project:D}', '{runId:D}', '{ExecutionKey}', 1, "
            + $"'{ArtifactKind.Trace}', '{objectKey}', '{ZipType}', '{BodyChecksum}', "
            + $"'{ArtifactSensitivity.Normal}', '{ArtifactScanState.Pending}', '{ArtifactState.PendingUpload}', {Cap})");

        return artifactId;
    }

    private static Task<ApiAnswer> CompleteAsync(World world, Guid artifactId, string? idempotencyKey = null) =>
        world.Client.SendAsync(HttpMethod.Post, $"/v1/artifacts/{artifactId:D}/complete", _ => { }, null, idempotencyKey);

    private Task<string?> ArtifactColumnAsync(World world, Guid artifactId, string column) =>
        _fixture.ScalarAsync<string?>(world.Database, $"select {column} from public.artifact where id = '{artifactId:D}'");

    /// <summary>
    /// Blocks until <paramref name="expected"/> backends of this test's database are observably waiting on a lock (the
    /// artifact row lock the caller holds), so the race window is entered deterministically instead of hoped for.
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

    [Fact]
    public async Task Complete_RacingSweeperRejection_Returns409ArtifactStateConflict()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var artifactId = await SeedArtifactAsync(world, runId);

        var gate = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        Task<ApiAnswer> complete;

        try
        {
            (await gate.ScalarAsync<int>(
                "select 1 from public.artifact where id = @artifact_id for update",
                ("artifact_id", artifactId))).ShouldBe(1);

            complete = CompleteAsync(world, artifactId, NewKey());

            // The completion has passed its verifier and is blocked inside the handler's artifact UPDATE.
            await WaitForBlockedAsync(world, expected: 1);

            (await gate.ExecuteAsync(
                "update public.artifact set state = @state where id = @artifact_id",
                ("state", ArtifactState.Ready), ("artifact_id", artifactId))).ShouldBe(1);
            (await gate.ExecuteAsync(
                "update public.artifact set state = @state, failure_code = @code where id = @artifact_id",
                ("state", ArtifactState.PendingDelete),
                ("code", ArtifactFailureCode.SecretRejected),
                ("artifact_id", artifactId))).ShouldBe(1);

            await gate.CommitAsync();
        }
        finally
        {
            await gate.DisposeAsync();
        }

        var answer = await complete;

        answer.Status.ShouldBe(HttpStatusCode.Conflict, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("artifact_state_conflict");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe(ArtifactState.PendingDelete);
    }

    [Fact]
    public async Task Complete_RacingVerdictRejection_ReturnsStoredRejected()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var artifactId = await SeedArtifactAsync(world, runId);

        var gate = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        Task<ApiAnswer> complete;

        try
        {
            (await gate.ScalarAsync<int>(
                "select 1 from public.artifact where id = @artifact_id for update",
                ("artifact_id", artifactId))).ShouldBe(1);

            complete = CompleteAsync(world, artifactId, NewKey());

            await WaitForBlockedAsync(world, expected: 1);

            (await gate.ExecuteAsync(
                "update public.artifact set state = @state, failure_code = @code where id = @artifact_id",
                ("state", ArtifactState.Rejected),
                ("code", ArtifactFailureCode.TooLarge),
                ("artifact_id", artifactId))).ShouldBe(1);

            await gate.CommitAsync();
        }
        finally
        {
            await gate.DisposeAsync();
        }

        var answer = await complete;

        // A rejection is a committed outcome the route answers verbatim; the losing completion never overwrites it.
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe(ArtifactState.Rejected);
        answer.Text("failure_code").ShouldBe(ArtifactFailureCode.TooLarge);
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe(ArtifactState.Rejected);
    }
}
