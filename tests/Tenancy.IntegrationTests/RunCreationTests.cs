// Request bodies are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.Modules.Ingestion.Application;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.2.1-T1 AC1–AC12 through the real host and a real PostgreSQL: one tenant transaction writes the run, its planned
/// executions, one producer slot per manifest shard and the outbox row, or nothing at all.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RunCreationTests
{
    private readonly PostgresTenancyFixture _fixture;

    public RunCreationTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    internal sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project,
        Guid SuiteRevision,
        Guid EnvironmentRevision,
        Guid ManifestId,
        Guid[] Cases) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>An org with a project, cases, a suite revision, an environment revision and one uploaded manifest.</summary>
    /// <param name="configuration">
    /// Extra in-memory configuration for the Api this world boots — a suite that needs a non-default EVIDGATE__* value
    /// (the run-cancel windows, for one) passes it here instead of booting a second kind of host.
    /// </param>
    /// <param name="services">
    /// Extra registrations for that host — the task queue, when a suite drives the real consumer against the rows this
    /// host's requests wrote.
    /// </param>
    internal static async Task<World> StartAsync(
        PostgresTenancyFixture fixture,
        int cases = 2,
        string slug = "acme",
        string? database = null,
        IReadOnlyDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? services = null)
    {
        var started = await BeginAsync(fixture, slug, database, configuration, services);
        var api = started.Api;
        var client = started.Client;
        var project = started.Project;
        var admin = TenancyApiFactory.Resolved(user: started.User, orgHeader: started.Org);

        try
        {
            var revisions = await ManifestTests.SeedCasesAsync(fixture, api, started.Org, project, started.Database, cases);

            var suite = await client.SendAsync(HttpMethod.Post, $"/v1/projects/{project}/suites", admin, new { name = "Smoke", slug = "smoke" }, NewKey());
            suite.Status.ShouldBe(HttpStatusCode.Created, suite.ErrorMessage);
            var revision = await client.SendAsync(
                HttpMethod.Post,
                $"/v1/suites/{suite.Id()}/revisions",
                admin,
                new { members = revisions.Select(id => new { case_revision_id = id }).ToArray() },
                NewKey());
            revision.Status.ShouldBe(HttpStatusCode.Created, revision.ErrorMessage);

            var environment = await client.SendAsync(
                HttpMethod.Post,
                $"/v1/projects/{project}/environments/staging/revisions",
                admin,
                new { base_urls = new { web = "https://staging.example.test" }, dependency_versions = new { postgres = "18.0" } },
                NewKey());
            environment.Status.ShouldBe(HttpStatusCode.Created, environment.ErrorMessage);

            var manifest = Manifest(revision.Id("suite_revision_id"), revisions, variants: 2, shards: 2);
            manifest["environment_revision_id"] = environment.Id("revision_id").ToString();
            var manifestId = await UploadAsync(client, admin, project, manifest);

            return new World(api, client, started.Database, started.User, started.Org, project, revision.Id("suite_revision_id"), environment.Id("revision_id"), manifestId, revisions);
        }
        catch
        {
            // A world that never returns owns nothing: the host it was built on is disposed here, so a failed
            // arrange does not leave a server and its connections behind for the rest of the run.
            client.Dispose();
            await api.DisposeAsync();
            throw;
        }
    }

    /// <summary>The host, org, project and caller headers that every world starts with.</summary>
    private sealed record Started(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project);

    /// <summary>
    /// The head every world shares: a host on that database, a fresh org, its project and the caller the requests
    /// post as. A caller that fails here disposes the host it built before rethrowing.
    /// </summary>
    private static async Task<Started> BeginAsync(
        PostgresTenancyFixture fixture,
        string slug,
        string? database,
        IReadOnlyDictionary<string, string?>? configuration,
        Action<IServiceCollection>? services)
    {
        // A suite that counts the whole database passes its own dedicated database; the rest share one under a fresh org.
        database ??= await fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(fixture.AppConnectionString(database), configuration, services);
        var client = api.CreateClient();

        try
        {
            var user = Guid.CreateVersion7();
            var org = await CreateOrgAsync(client, user, Unique.Slug(slug));
            var project = await CreateProjectAsync(client, user, org, "checkout");

            return new Started(api, client, database, user, org, project);
        }
        catch
        {
            // Same rule as StartAsync: a head that never returns owns nothing.
            client.Dispose();
            await api.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// A world cut down to what a <em>body-shape</em> refusal can observe: an org, a project and the headers that post
    /// to it. Every shape check of <c>CreateRunCommand</c> returns before its first database read, so the cases, the
    /// suite, the environment and the manifest such a request never looks at are not built here — four round trips to
    /// the shared database fewer per refusal, and four fewer ways for a loaded host to time one of them out.
    /// <see cref="World.ManifestId"/> is a placeholder for that reason: a request refused on its body never resolves it,
    /// and a request that is not refused resolves it and fails loudly with 404 instead of passing quietly.
    /// </summary>
    internal static async Task<World> StartRefusalWorldAsync(PostgresTenancyFixture fixture, string slug = "acme")
    {
        var started = await BeginAsync(fixture, slug, database: null, configuration: null, services: null);

        return new World(
            started.Api,
            started.Client,
            started.Database,
            started.User,
            started.Org,
            started.Project,
            SuiteRevision: Guid.Empty,
            EnvironmentRevision: Guid.Empty,
            ManifestId: Guid.CreateVersion7(),
            Cases: []);
    }

    internal static JsonObject Manifest(Guid suite, Guid[] cases, int variants, int shards) => ManifestUnitTests.Build(
        suite,
        cases,
        [.. Enumerable.Range(0, variants).Select(i => new Dictionary<string, string> { ["browser"] = $"b{i}" })],
        ["staging"],
        shards: shards);

    internal static async Task<Guid> UploadAsync(HttpClient client, Action<HttpRequestHeaders> caller, Guid project, JsonObject manifest)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/projects/{project}/manifests");
        caller(request.Headers);
        request.Headers.Add("Idempotency-Key", NewKey());
        request.Content = new StringContent(manifest.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, text);

        return document.RootElement.GetProperty("manifest_id").GetGuid();
    }

    /// <summary>
    /// One <c>POST /v1/projects/{p}/runs</c>. Every call carries an <c>Idempotency-Key</c> (05.2.1-T2): a fresh one by
    /// default, or <paramref name="idempotencyKey"/> to replay/conflict, or none at all when
    /// <paramref name="omitIdempotencyKey"/> is set (the missing-header case).
    /// </summary>
    internal static async Task<ApiAnswer> CreateRunAsync(
        World world,
        string body,
        Action<HttpRequestHeaders>? caller = null,
        Guid? project = null,
        HttpClient? client = null,
        string? idempotencyKey = null,
        bool omitIdempotencyKey = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/projects/{project ?? world.Project}/runs");
        (caller ?? world.Admin)(request.Headers);
        if (!omitIdempotencyKey)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey ?? NewKey());
        }

        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await (client ?? world.Client).SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    internal static string RunBody(Guid manifestId) => $$"""{"manifest_id":"{{manifestId}}"}""";

    private static List<JsonElement> Details(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()];

    private Task AssertNoRunRowsAsync(World world) => AssertNoRunRowsAsync(_fixture, world);

    /// <summary>No run, planned execution, producer slot or outbox row exists for the world's org.</summary>
    internal static async Task AssertNoRunRowsAsync(PostgresTenancyFixture fixture, World world)
    {
        (await fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.run where org_id = '{world.Org}'")).ShouldBe(0);
        (await fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.planned_execution where org_id = '{world.Org}'")).ShouldBe(0);
        (await fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.producer_slot where org_id = '{world.Org}'")).ShouldBe(0);
        (await fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.outbox where org_id = '{world.Org}'")).ShouldBe(0);
    }

    // ------------------------------------------------------------------ AC1, AC7

    [Fact]
    public async Task CreateRun_HappyPath_AllRowsCommittedTogether()
    {
        await using var world = await StartAsync(_fixture);

        var answer = await CreateRunAsync(world, RunBody(world.ManifestId));

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        answer.Text("state").ShouldBe("queued");
        var runId = answer.Id("run_id");

        (await _fixture.ScalarAsync<Guid>(world.Database, $"select id from public.run where org_id = '{world.Org}'")).ShouldBe(runId);
        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.run where org_id = '{world.Org}'")).ShouldBe("queued");
        (await _fixture.ScalarAsync<string>(world.Database, $"select purpose from public.run where org_id = '{world.Org}'")).ShouldBe("regular");
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.planned_execution where org_id = '{world.Org}'")).ShouldBe(4);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.producer_slot where org_id = '{world.Org}'")).ShouldBe(2);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.outbox where org_id = '{world.Org}' and task_type = 'orchestrator.run.created'")).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(world.Database, $"select payload ->> 'run_id' from public.outbox where org_id = '{world.Org}'")).ShouldBe(runId.ToString("D"));
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.audit_log where org_id = '{world.Org}' and action = 'run.create'")).ShouldBe(1);
    }

    [Fact]
    public async Task CreateRun_RegistersOneSlotPerShard()
    {
        await using var world = await StartAsync(_fixture, cases: 2);
        var manifest = Manifest(world.SuiteRevision, world.Cases, variants: 3, shards: 3);
        manifest["environment_revision_id"] = world.EnvironmentRevision.ToString();
        var manifestId = await UploadAsync(world.Client, world.Admin, world.Project, manifest);

        (await CreateRunAsync(world, RunBody(manifestId))).Status.ShouldBe(HttpStatusCode.Created);

        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.producer_slot where org_id = '{world.Org}'")).ShouldBe(3);
        (await _fixture.ColumnAsync<string>(world.Database, $"select producer_id from public.producer_slot where org_id = '{world.Org}' order by producer_id"))
            .ShouldBe(["shard-1", "shard-2", "shard-3"]);
        (await _fixture.ColumnAsync<string>(world.Database, $"select distinct shard_id from public.planned_execution where org_id = '{world.Org}' order by shard_id"))
            .ShouldBe(["shard-1", "shard-2", "shard-3"]);
    }

    // ------------------------------------------------------------------ AC2

    [Fact]
    public async Task CreateRun_CrashMidTransaction_NoPartialRows()
    {
        await using var world = await StartAsync(_fixture);
        await using var crashing = world.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IOutboxWriter>();
            services.AddSingleton<IOutboxWriter, ThrowingOutboxWriter>();
        }));
        using var client = crashing.CreateClient();

        var answer = await CreateRunAsync(world, RunBody(world.ManifestId), client: client);

        answer.Status.ShouldBe(HttpStatusCode.InternalServerError);
        await AssertNoRunRowsAsync(world);
    }

    /// <summary>AC2 at the first write after the run row: the planned-execution insert fails, so the run row must not survive.</summary>
    [Fact]
    public async Task CreateRun_FailureAfterRunInsert_NoRows()
    {
        await using var world = await StartAsync(_fixture);
        await using var crashing = world.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITenantDb>();
            services.AddSingleton<TenantDb>();
            services.AddSingleton<ITenantDb>(provider =>
                new ThrowingTenantDb(provider.GetRequiredService<TenantDb>(), "insert into public.planned_execution"));
        }));
        using var client = crashing.CreateClient();

        var answer = await CreateRunAsync(world, RunBody(world.ManifestId), client: client);

        answer.Status.ShouldBe(HttpStatusCode.InternalServerError);
        await AssertNoRunRowsAsync(world);
    }

    /// <summary>AC2 at the write after the planned executions: the slot registration fails, so neither survives.</summary>
    [Fact]
    public async Task CreateRun_FailureAfterPlannedInsert_NoRows()
    {
        await using var world = await StartAsync(_fixture);
        await using var crashing = world.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IProducerRegistry>();
            services.AddScoped<ProducerRegistry>();
            services.AddScoped<IProducerRegistry>(provider => new ThrowingProducerRegistry(provider.GetRequiredService<ProducerRegistry>()));
        }));
        using var client = crashing.CreateClient();

        var answer = await CreateRunAsync(world, RunBody(world.ManifestId), client: client);

        answer.Status.ShouldBe(HttpStatusCode.InternalServerError);
        await AssertNoRunRowsAsync(world);
    }

    /// <summary>AC2 at the write after the slot registration: the outbox row fails, so the run, its planned executions and its slots roll back.</summary>
    [Fact]
    public async Task CreateRun_FailureAfterSlotRegistration_NoRows()
    {
        await using var world = await StartAsync(_fixture);
        await using var crashing = world.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IOutboxWriter>();
            services.AddSingleton<IOutboxWriter, ThrowingOutboxWriter>();
        }));
        using var client = crashing.CreateClient();

        var answer = await CreateRunAsync(world, RunBody(world.ManifestId), client: client);

        answer.Status.ShouldBe(HttpStatusCode.InternalServerError);
        await AssertNoRunRowsAsync(world);
    }

    // ------------------------------------------------------------------ AC5

    [Fact]
    public async Task CreateRun_UnresolvedEnvironmentOrSuite_422_NoWrites()
    {
        await using var world = await StartAsync(_fixture);

        // (a) A manifest with no environment revision: run.environment_revision_id is NOT NULL, so it is refused.
        var noEnvironment = Manifest(world.SuiteRevision, world.Cases, variants: 1, shards: 1);
        var noEnvironmentId = await UploadAsync(world.Client, world.Admin, world.Project, noEnvironment);
        var refused = await CreateRunAsync(world, RunBody(noEnvironmentId));
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("manifest_invalid");
        Details(refused).Single().GetProperty("reason").GetString().ShouldBe("unknown_environment_revision");
        Details(refused).Single().GetProperty("path").GetString().ShouldBe("/environment_revision_id");
        await AssertNoRunRowsAsync(world);

        // (b) A manifest whose suite revision belongs to another project of the same org: the composite FK accepts the
        // row, the project-scoped pre-check does not, so the run is refused before any write.
        var other = await CreateProjectAsync(world.Client, world.User, world.Org, "other");
        var otherCases = await ManifestTests.SeedCasesAsync(_fixture, world.Api, world.Org, other, world.Database, 1);
        var otherSuite = await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{other}/suites", world.Admin, new { name = "Other", slug = "other" }, NewKey());
        var otherRevision = await world.Client.SendAsync(
            HttpMethod.Post, $"/v1/suites/{otherSuite.Id()}/revisions", world.Admin,
            new { members = otherCases.Select(id => new { case_revision_id = id }).ToArray() }, NewKey());
        var foreignSuite = otherRevision.Id("suite_revision_id");
        var foreignManifest = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(world.Database, $"""
            insert into public.manifest (id, org_id, project_id, manifest_hash, schema_version, suite_revision_id, environment_revision_id, shard_plan, matrix, env_needs, planned_count, document, verified_by)
            select '{foreignManifest}', org_id, project_id, 'sha256:{new string('c', 64)}', 1, '{foreignSuite}', environment_revision_id, shard_plan, matrix, env_needs, planned_count, document, 'x'
              from public.manifest where id = '{world.ManifestId}'
            """);

        var crossSuite = await CreateRunAsync(world, RunBody(foreignManifest));
        crossSuite.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(crossSuite).Single().GetProperty("reason").GetString().ShouldBe("unknown_suite_revision");
        await AssertNoRunRowsAsync(world);
    }

    // ------------------------------------------------------------------ AC10

    [Fact]
    public async Task CreateRun_ReleaseNamed_422ReleaseUnresolvable()
    {
        await using var world = await StartAsync(_fixture);

        var empty = await CreateRunAsync(world, $$"""{"manifest_id":"{{world.ManifestId}}","release":""}""");
        empty.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        empty.ErrorCode.ShouldBe("request_invalid");

        var overLong = await CreateRunAsync(world, $$"""{"manifest_id":"{{world.ManifestId}}","release":"{{new string('r', 129)}}"}""");
        overLong.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        overLong.ErrorCode.ShouldBe("request_invalid");

        var named = await CreateRunAsync(world, $$"""{"manifest_id":"{{world.ManifestId}}","release":"v1.0"}""");
        named.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        named.ErrorCode.ShouldBe("release_unresolvable");
        await AssertNoRunRowsAsync(world);
    }

    // ------------------------------------------------------------------ AC11

    [Fact]
    public async Task CreateRun_CiRunRef_FromOidcClaims()
    {
        await using var world = await StartAsync(_fixture);
        var principal = RequestPrincipal.Service(
            world.Project,
            ["run:create"],
            credentialId: "jti-1",
            ciRun: new CiRunReference("github_actions", 987_654_321L, 42_424_242L, 3));

        var result = await RunCreationSeams.SendDirectAsync(world.Api, world.Org, world.Project, principal, RunBody(world.ManifestId));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        var runId = result.Value.RunId;
        // jsonb does not preserve key order, so each claim is read separately; the key count pins the exact shape.
        (await _fixture.ScalarAsync<long>(world.Database, $"select (select count(*) from jsonb_object_keys(ci_run_ref)) from public.run where id = '{runId}'")).ShouldBe(4);
        (await _fixture.ScalarAsync<string>(world.Database, $"select ci_run_ref ->> 'provider' from public.run where id = '{runId}'")).ShouldBe("github_actions");
        (await _fixture.ScalarAsync<long>(world.Database, $"select (ci_run_ref ->> 'repository_id')::bigint from public.run where id = '{runId}'")).ShouldBe(987_654_321L);
        (await _fixture.ScalarAsync<long>(world.Database, $"select (ci_run_ref ->> 'run_id')::bigint from public.run where id = '{runId}'")).ShouldBe(42_424_242L);
        (await _fixture.ScalarAsync<int>(world.Database, $"select (ci_run_ref ->> 'run_attempt')::int from public.run where id = '{runId}'")).ShouldBe(3);
    }

    [Fact]
    public async Task CreateRun_CiRunRef_NoClaims_Null()
    {
        await using var world = await StartAsync(_fixture);
        var principal = RequestPrincipal.Service(world.Project, ["run:create"], credentialId: "jti-2");

        var result = await RunCreationSeams.SendDirectAsync(world.Api, world.Org, world.Project, principal, RunBody(world.ManifestId));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        (await _fixture.ScalarAsync<string>(world.Database, $"select ci_run_ref::text from public.run where id = '{result.Value.RunId}'"))
            .ShouldBeNull();
    }

    // ------------------------------------------------------------------ AC12

    [Fact]
    public async Task CreateRun_DeadlineAndLateWindow_PinnedAtCreate()
    {
        await using var world = await StartAsync(_fixture);

        (await CreateRunAsync(world, RunBody(world.ManifestId))).Status.ShouldBe(HttpStatusCode.Created);

        // Both clocks are the transaction's now(), so the offset is exactly the default 02:00:00.
        (await _fixture.ScalarAsync<double>(world.Database, $"select (extract(epoch from (collection_deadline_at - created_at)))::double precision from public.run where org_id = '{world.Org}'"))
            .ShouldBe(7200d);
        (await _fixture.ScalarAsync<int>(world.Database, $"select late_window_minutes from public.run where org_id = '{world.Org}'")).ShouldBe(1440);
        (await _fixture.ScalarAsync<string>(world.Database, $"select policy_revision_id::text from public.run where org_id = '{world.Org}'")).ShouldBeNull();
    }

    // ------------------------------------------------------------------ body shape

    [Fact]
    public async Task CreateRun_UnknownFieldInBody_422()
    {
        await using var world = await StartAsync(_fixture);

        var answer = await CreateRunAsync(world, $$"""{"manifest_id":"{{world.ManifestId}}","adhoc_field":"x"}""");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        Details(answer).Single().GetProperty("path").GetString().ShouldBe("/adhoc_field");
        Details(answer).Single().GetProperty("reason").GetString().ShouldBe("additional_properties");
        await AssertNoRunRowsAsync(world);
    }

    [Fact]
    public async Task CreateRun_Components_AreStored_AndCapped()
    {
        await using var world = await StartAsync(_fixture);

        var accepted = await CreateRunAsync(
            world,
            $$$$"""{"manifest_id":"{{{{world.ManifestId}}}}","components":{"build":"42","nested":{"a":"b"}}}""");
        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);
        (await _fixture.ScalarAsync<string>(world.Database, $"select components::text from public.run where org_id = '{world.Org}'"))
            .ShouldBe("""{"build": "42", "nested": {"a": "b"}}""");

        var tooLarge = await CreateRunAsync(
            world,
            $$$"""{"manifest_id":"{{{world.ManifestId}}}","components":{"blob":"{{{new string('x', 9 * 1024)}}}"}}""");
        tooLarge.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        tooLarge.ErrorCode.ShouldBe("request_invalid");
    }

    /// <summary>
    /// F1: a number jsonb cannot hold is a body error before any write. `1e9999999` is a few bytes but Postgres
    /// refuses the cast (22003), so it must be 422 (not a 500) and leave no row; a large but representable exponent is
    /// stored.
    /// </summary>
    [Fact]
    public async Task CreateRun_Components_UnstorableNumber_422_NoRows()
    {
        await using var world = await StartAsync(_fixture);

        var overflow = await CreateRunAsync(
            world,
            $$$"""{"manifest_id":"{{{world.ManifestId}}}","components":{"a":1e9999999}}""");
        overflow.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        overflow.ErrorCode.ShouldBe("request_invalid");
        Details(overflow).Single().GetProperty("path").GetString().ShouldBe("/components");
        await AssertNoRunRowsAsync(world);

        var accepted = await CreateRunAsync(
            world,
            $$$"""{"manifest_id":"{{{world.ManifestId}}}","components":{"a":1.5e10}}""");
        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);
        (await _fixture.ScalarAsync<bool>(world.Database, $"select (components ->> 'a')::numeric = 15000000000 from public.run where org_id = '{world.Org}'"))
            .ShouldBeTrue();
    }

    // ------------------------------------------------------------------ 404, 403

    [Fact]
    public async Task CreateRun_UnknownManifest_404()
    {
        await using var world = await StartAsync(_fixture);

        var answer = await CreateRunAsync(world, RunBody(Guid.CreateVersion7()));

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
        await AssertNoRunRowsAsync(world);
    }

    [Fact]
    public async Task CreateRun_CrossTenantManifest_404()
    {
        await using var world = await StartAsync(_fixture);
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));

        var answer = await CreateRunAsync(
            world,
            RunBody(world.ManifestId),
            TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg));

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        await AssertNoRunRowsAsync(world);
    }

    [Fact]
    public async Task CreateRun_NoPermission_403()
    {
        await using var world = await StartAsync(_fixture);

        var answer = await CreateRunAsync(
            world,
            RunBody(world.ManifestId),
            TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: world.Project, servicePermissions: ["run:read"]));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        await AssertNoRunRowsAsync(world);
    }

    [Fact]
    public async Task CreateRun_ProjectScopeMismatch_403()
    {
        await using var world = await StartAsync(_fixture);
        var other = await CreateProjectAsync(world.Client, world.User, world.Org, "other");

        var answer = await CreateRunAsync(
            world,
            RunBody(world.ManifestId),
            TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: other, servicePermissions: ["run:create"]));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        await AssertNoRunRowsAsync(world);
    }

    [Fact]
    public async Task CreateRun_Anonymous_401_WithoutReadingTheBody()
    {
        await using var world = await StartAsync(_fixture);

        var answer = await CreateRunAsync(world, "{ not json", _ => { });

        answer.Status.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------------------ P3

    [Fact]
    public async Task CreateRun_CommandCounter_Bounded()
    {
        await using var world = await StartAsync(_fixture, cases: 40);

        async Task<int> CountAsync(int variants)
        {
            var manifest = Manifest(world.SuiteRevision, world.Cases, variants, shards: 2);
            manifest["environment_revision_id"] = world.EnvironmentRevision.ToString();
            var manifestId = await UploadAsync(world.Client, world.Admin, world.Project, manifest);

            world.Api.Statements.Clear();
            (await CreateRunAsync(world, RunBody(manifestId))).Status.ShouldBe(HttpStatusCode.Created);

            return world.Api.Statements.Snapshot().Count(statement => statement.Contains("public.", StringComparison.Ordinal));
        }

        var small = await CountAsync(1);
        var large = await CountAsync(20); // 800 planned executions (the variant cap): the same statement count

        large.ShouldBe(small);
        small.ShouldBeLessThanOrEqualTo(20, string.Join(Environment.NewLine, world.Api.Statements.Snapshot()));
    }
}

/// <summary>
/// The <c>execution_key</c> layout of 05.2.1 (00 §7, T10): the expected value is computed here by an independent
/// code path (a byte list, not the shipping span writer), so the test pins the layout, not the implementation.
/// </summary>
public sealed class ExecutionKeyTests
{
    private static readonly Guid CaseId = Guid.Parse("018f2f0e-8a52-7c4b-9d3e-000000000001");

    [Fact]
    public void ExecutionKey_MatchesAnIndependentLayout()
    {
        var variant = "sha256:" + new string('a', 64);

        ExecutionKey.Compute(CaseId, variant, "staging").ShouldBe(Independent(CaseId, variant, "staging"));
        ExecutionKey.Compute(CaseId, variant, "staging").ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public void ExecutionKey_LengthPrefix_Disambiguates()
    {
        // Without the length prefix, ('ab','c') and ('a','bc') would concatenate to the same bytes.
        ExecutionKey.Compute(Guid.Empty, "ab", "c").ShouldNotBe(ExecutionKey.Compute(Guid.Empty, "a", "bc"));
    }

    [Fact]
    public void ExecutionKey_IsOrderSensitive()
    {
        ExecutionKey.Compute(CaseId, "sha256:" + new string('a', 64), "staging")
            .ShouldNotBe(ExecutionKey.Compute(CaseId, "sha256:" + new string('a', 64), "prod"));
    }

    private static string Independent(Guid caseId, string variant, string targetEnv)
    {
        var buffer = new List<byte>();
        foreach (var field in new[] { caseId.ToString("D"), variant, targetEnv })
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            buffer.AddRange(BitConverter.GetBytes(bytes.Length).Reverse()); // big-endian, unlike the shipping writer
            buffer.AddRange(bytes);
        }

        return Convert.ToHexString(SHA256.HashData([.. buffer])).ToLowerInvariant();
    }
}
