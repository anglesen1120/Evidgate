// 06.1.2-T1: POST /v1/artifacts/upload-url through the real host and a real PostgreSQL.
// Request bodies are written inline so each test reads as the request it sends.
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.1.2-T1 (AC1, AC2, AC6, AC8) through the real C2 pipeline and a real PostgreSQL: the presigned
/// <c>POST /v1/artifacts/upload-url</c> — run scope, the caller's bound producer slot, the manifest's planned
/// <c>(execution_key, attempt_no)</c>, the kind policy, the minted <c>pending/</c> staging key and the signed
/// <c>Content-Length</c> + checksum headers. The object-store adapter is CPU-only (presigning is SigV4
/// arithmetic), so no storage container is needed; the endpoint is configured with an unreachable local
/// endpoint and test credentials supplied through the host configuration.
/// <para>
/// A test startup filter supplies the authenticated ingest token ID because the header stand-in cannot carry it.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class EvidenceUploadUrlTests : IClassFixture<TenancyHostPool>
{
    /// <summary>A well-formed execution key: sha256 hex (00 §7).</summary>
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>A second well-formed execution key, never planned in the seeded run.</summary>
    private const string OtherExecutionKey = "1111111111111111111111111111111111111111111111111111111111111111";

    /// <summary>A well-formed declared content address, <c>sha256:&lt;64 hex&gt;</c>.</summary>
    private const string Checksum = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string ZipType = "application/zip";

    private const string ProducerId = "shard-1";

    /// <summary>The kind-policy cap every seeded policy row carries.</summary>
    private const long Cap = 1024;

    /// <summary>
    /// The object-store configuration the endpoint needs: an unreachable local endpoint (presigning never
    /// connects), path-style addressing, and the two credentials under the <c>SecretNames</c> configuration
    /// spelling the environment secret provider reads (10.1.1-T2).
    /// </summary>
    private static readonly Dictionary<string, string?> ObjectStorage = new(StringComparer.Ordinal)
    {
        ["EVIDGATE:Object:Bucket"] = "evidgate-staging",
        ["EVIDGATE:Object:Region"] = "us-east-1",
        ["EVIDGATE:Object:Endpoint"] = "http://127.0.0.1:9",
        ["EVIDGATE:Object:ForcePathStyle"] = "true",
        ["EVIDGATE:OBJECT:ACCESSKEY"] = "test-access-key",
        ["EVIDGATE:OBJECT:SECRETKEY"] = "test-secret-key",
    };

    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;
    public EvidenceUploadUrlTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

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
    private static RequestPrincipal Writer(string ownerId = IngestTestWorld.OwnerId) =>
        RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: ownerId);

    /// <summary>A service principal without <c>artifact:write</c>.</summary>
    private static RequestPrincipal Reader() =>
        RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Read], credentialId: IngestTestWorld.OwnerId);

    /// <summary>
    /// One migrated database with an org and a project created through the real registry routes, plus a host
    /// whose requests carry <paramref name="principal"/> and the object-store configuration.
    /// </summary>
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
            project = await CreateProjectAsync(setup, user, org, "checkout");
        }

        var host = _hosts.WithPrincipal(database, org, project, principal, ObjectStorage);

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
    /// One committed run (through <see cref="RunRowSeeder"/>), one planned execution and one producer slot for
    /// <paramref name="org"/> — the parents 0031's composite keys need. <paramref name="project"/> moves the run
    /// to another project of the same org (the run-of-another-project case).
    /// </summary>
    private async Task<Guid> SeedRunAsync(
        World world,
        string executionKey,
        Guid? org = null,
        string producerId = ProducerId,
        string? boundOwnerId = null,
        Guid? project = null)
    {
        var owningOrg = org ?? world.Org;
        var runId = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, owningOrg);

        if (project is { } moved)
        {
            await SiblingProjectParents.MoveRunAsync(_fixture, world.Database, owningOrg, runId, moved);
        }

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), owningOrg);
        var testRevision = await scope.ScalarAsync<Guid>(
            "select id from public.test_revision where org_id = @org_id limit 1", ("org_id", owningOrg));

        (await scope.ExecuteAsync(
            "insert into public.planned_execution (id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id, required) "
            + "values (@id, @org_id, @run_id, @execution_key, @case_revision_id, @variant_hash, 'staging', 'shard-1', true)",
            ("id", Guid.CreateVersion7()), ("org_id", owningOrg), ("run_id", runId), ("execution_key", executionKey),
            ("case_revision_id", testRevision), ("variant_hash", "sha256:" + new string('c', 64)))).ShouldBe(1);

        (await scope.ExecuteAsync(
            "insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id, bound_token_jti) "
            + "values (@id, @org_id, @run_id, 1, @producer_id, @owner_id)",
            ("id", Guid.CreateVersion7()), ("org_id", owningOrg), ("run_id", runId), ("producer_id", producerId),
            ("owner_id", (object?)boundOwnerId ?? DBNull.Value))).ShouldBe(1);

        await scope.CommitAsync();

        return runId;
    }

    /// <summary>One committed kind-policy row for <paramref name="world"/>'s project, as the app role.</summary>
    private async Task InsertKindPolicyAsync(
        World world,
        string kind,
        long maxBytes = Cap,
        string[]? mediaTypes = null,
        bool captureEnabled = true)
    {
        var types = mediaTypes ?? [ZipType];
        var array = "array[" + string.Join(",", types.Select(type => $"'{type}'")) + "]";

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity, capture_enabled) "
            + $"values ('{Guid.CreateVersion7()}', '{world.Org}', '{world.Project}', '{kind}', {maxBytes}, {array}, 'normal', {(captureEnabled ? "true" : "false")})"))
            .ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>The upload-url request body (00 §9.3, snake_case wire contract).</summary>
    private static object Body(
        Guid runId,
        string executionKey = ExecutionKey,
        string producerId = ProducerId,
        int attemptNo = 1,
        string kind = "trace",
        string mediaType = ZipType,
        long bytes = Cap,
        string checksum = Checksum) =>
        new
        {
            run_id = runId,
            producer_id = producerId,
            execution_key = executionKey,
            attempt_no = attemptNo,
            kind,
            media_type = mediaType,
            bytes,
            sha256 = checksum,
        };

    /// <summary>One upload-url request; the caller is the host's published principal.</summary>
    private static Task<ApiAnswer> UploadUrlAsync(World world, object body, string? idempotencyKey = null) =>
        world.Client.SendAsync(HttpMethod.Post, "/v1/artifacts/upload-url", _ => { }, body, idempotencyKey);

    private Task<long> ArtifactRowsAsync(World world) =>
        _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.artifact where org_id = '{world.Org}'");

    private Task<string> ArtifactColumnAsync(World world, Guid artifactId, string column) =>
        _fixture.ScalarAsync<string>(world.Database, $"select {column} from public.artifact where id = '{artifactId:D}'");

    private Task<string?> SlotBindingAsync(World world, Guid runId) =>
        _fixture.ScalarAsync<string?>(world.Database, $"select bound_token_jti from public.producer_slot where run_id = '{runId:D}'");
}
