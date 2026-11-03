// Request bodies are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.1.1 AC1–AC8 through the real host and a real PostgreSQL: a verified, immutable, content-addressed manifest, its
/// schema and catalogue checks, secret-free variants, matrix coverage, tenant isolation and the statement budget.
/// </summary>
[Trait("Category", "Docker")]
[Collection(ExclusiveTenancySuite.Name)]
public sealed class ManifestTests
{
    private readonly PostgresTenancyFixture _fixture;

    public ManifestTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    internal sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project, Guid SuiteRevision, Guid[] Cases)
        : IAsyncDisposable
    {
        public Action<System.Net.Http.Headers.HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>An org with a project, <paramref name="cases"/> registered case revisions and a suite revision over them.</summary>
    internal static async Task<World> StartAsync(PostgresTenancyFixture fixture, int cases = 2, string slug = "acme")
    {
        var database = await fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, slug);
        var project = await CreateProjectAsync(client, user, org, "checkout");
        var revisions = await SeedCasesAsync(fixture, api, org, project, database, cases);
        var admin = TenancyApiFactory.Resolved(user: user, orgHeader: org);

        var suite = await client.SendAsync(HttpMethod.Post, $"/v1/projects/{project}/suites", admin, new { name = "Smoke", slug = "smoke" }, NewKey());
        suite.Status.ShouldBe(HttpStatusCode.Created, suite.ErrorMessage);
        var revision = await client.SendAsync(
            HttpMethod.Post,
            $"/v1/suites/{suite.Id()}/revisions",
            admin,
            new { members = revisions.Select(id => new { case_revision_id = id }).ToArray() },
            NewKey());
        revision.Status.ShouldBe(HttpStatusCode.Created, revision.ErrorMessage);

        return new World(api, client, database, user, org, project, revision.Id("suite_revision_id"), revisions);
    }

    internal static async Task<Guid[]> SeedCasesAsync(PostgresTenancyFixture fixture, TenancyApiFactory api, Guid org, Guid project, string database, int count)
    {
        var files = Enumerable.Range(0, count).Select(i => new SourceFile(
            $"m/{i}.spec.ts",
            $"// evidgate.id: 018f2f0e-8a52-7c4b-9d3e-{i:D12}\ntest('{i}', () => {{ expect(a).toBe({i}); }});",
            "playwright",
            "typescript")).ToArray();

        await using var services = api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>().BeginAsync(org, null, false, TestContext.Current.CancellationToken);
        var outcome = await services.ServiceProvider.GetRequiredService<ICaseIdentityService>()
            .RegisterDiscoveredAsync(project, "bbbbbbb2222222", files, TestContext.Current.CancellationToken);
        outcome.Succeeded.ShouldBeTrue(outcome.Failure);
        await scope.CommitAsync(TestContext.Current.CancellationToken);

        var ids = new Guid[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = await fixture.ScalarAsync<Guid>(
                database,
                $"select r.id from public.test_revision r join public.test_case c on c.org_id = r.org_id and c.id = r.case_id where c.project_id = '{project}' and c.file_path = 'm/{i}.spec.ts'");
        }

        return ids;
    }

    private static JsonObject Manifest(World world, int variants = 2) => ManifestUnitTests.Build(
        world.SuiteRevision,
        world.Cases,
        [.. Enumerable.Range(0, variants).Select(i => new Dictionary<string, string> { ["browser"] = $"b{i}" })],
        ["staging"],
        shards: 2);

    private static async Task<ApiAnswer> UploadAsync(World world, string body, Action<System.Net.Http.Headers.HttpRequestHeaders>? caller = null, Guid? project = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/projects/{project ?? world.Project}/manifests");
        (caller ?? world.Admin)(request.Headers);
        request.Headers.Add("Idempotency-Key", NewKey());
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await world.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    private static List<JsonElement> Details(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()];

    // ------------------------------------------------------------------ AC1, AC6

    [Fact]
    public async Task UploadManifest_ReturnsStableHash_Rfc8785_AndDedupes()
    {
        await using var world = await StartAsync(_fixture);
        var manifest = Manifest(world);
        var body = manifest.ToJsonString();

        var first = await UploadAsync(world, body);

        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        var hash = first.Text("manifest_hash");
        hash.ShouldBe(CanonicalHash.Compute(body));
        first.ETag.ShouldBe($"\"{hash}\"");

        // The same content with every object's members reversed and extra whitespace: the same canonical form.
        var reordered = Reverse(JsonNode.Parse(body)!).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        reordered.ShouldNotBe(body);
        var again = await UploadAsync(world, reordered);

        again.Status.ShouldBe(HttpStatusCode.Created);
        again.Id("manifest_id").ShouldBe(first.Id("manifest_id"));
        again.Text("manifest_hash").ShouldBe(hash);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.manifest")).ShouldBe(1);

        var stored = await _fixture.ScalarAsync<int>(world.Database, "select planned_count from public.manifest");
        stored.ShouldBe(4);
        (await _fixture.ScalarAsync<string>(world.Database, "select verified_by from public.manifest")).ShouldBe($"user:{world.User:D}");
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.audit_log where action = 'manifest.upload'")).ShouldBe(2);

        static JsonNode Reverse(JsonNode node) => node switch
        {
            JsonObject obj => new JsonObject(obj.Reverse().Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)Reverse(pair.Value!.DeepClone())))),
            JsonArray array => new JsonArray([.. array.Select(item => (JsonNode?)Reverse(item!.DeepClone()))]),
            _ => node.DeepClone(),
        };
    }

    // ------------------------------------------------------------------ AC2

    [Fact]
    public async Task ManifestSchema_Violations_Are422_WithIndexedDetails()
    {
        await using var world = await StartAsync(_fixture);
        var manifest = Manifest(world);
        manifest["planned_executions"]![1]!.AsObject().Remove("target_env");
        manifest["password"] = "hunter2";

        var answer = await UploadAsync(world, manifest.ToJsonString());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("manifest_invalid");
        var details = Details(answer).Select(detail => (detail.GetProperty("path").GetString(), detail.GetProperty("reason").GetString())).ToList();
        details.ShouldContain(("/planned_executions/1/target_env", "required"));
        details.ShouldContain((string.Empty, "additional_properties"));
        answer.Body.GetRawText().ShouldNotContain("password");
        answer.Body.GetRawText().ShouldNotContain("hunter2");

        (await UploadAsync(world, "{ not json")).ErrorCode.ShouldBe("manifest_invalid");
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.manifest")).ShouldBe(0);
    }

    [Fact]
    public async Task UploadManifest_BodyOver256KiB_Is413()
    {
        await using var world = await StartAsync(_fixture);
        var manifest = Manifest(world);
        manifest["padding"] = new string(' ', 10);
        var body = manifest.ToJsonString().Replace("\"padding\":\"          \"", "\"padding\":\"" + new string('x', 270 * 1024) + "\"", StringComparison.Ordinal);

        var answer = await UploadAsync(world, body);

        answer.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    // ------------------------------------------------------------------ AC3

    [Fact]
    public async Task UploadManifest_UnknownCaseRevision_Is422_WithIndices()
    {
        await using var world = await StartAsync(_fixture);
        var unknown = Guid.CreateVersion7();
        var manifest = ManifestUnitTests.Build(world.SuiteRevision, [world.Cases[0], unknown], [new() { ["browser"] = "x" }], ["staging"]);

        var answer = await UploadAsync(world, manifest.ToJsonString());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("manifest_invalid");
        Details(answer).Select(detail => (detail.GetProperty("path").GetString(), detail.GetProperty("reason").GetString(), detail.TryGetProperty("index", out var i) ? i.GetInt32() : -1))
            .ShouldBe(
            [
                ("/planned_executions/1/case_revision_id", "unknown_case_revision", 1),
                ("/matrix/case_revision_ids/1", "unknown_case_revision", -1),
            ]);
    }

    [Fact]
    public async Task UploadManifest_ACaseOrSuiteRevisionOfAnotherProject_IsUnknown()
    {
        await using var world = await StartAsync(_fixture);
        var other = await CreateProjectAsync(world.Client, world.User, world.Org, "other");
        var foreignCase = (await SeedCasesAsync(_fixture, world.Api, world.Org, other, world.Database, 1))[0];

        var crossCase = ManifestUnitTests.Build(world.SuiteRevision, [foreignCase], [new() { ["browser"] = "x" }], ["staging"]);
        (await UploadAsync(world, crossCase.ToJsonString())).Body.GetProperty("error").GetProperty("details")[0].GetProperty("reason").GetString()
            .ShouldBe("unknown_case_revision");

        // The suite revision belongs to the first project: uploading the manifest into the other one finds no such suite.
        var crossSuite = ManifestUnitTests.Build(world.SuiteRevision, [foreignCase], [new() { ["browser"] = "x" }], ["staging"]);
        var answer = await UploadAsync(world, crossSuite.ToJsonString(), project: other);
        Details(answer).Single().GetProperty("reason").GetString().ShouldBe("unknown_suite_revision");
    }

    [Fact]
    public async Task UploadManifest_EnvironmentRevision_MustExistInTheProject()
    {
        await using var world = await StartAsync(_fixture);
        var manifest = Manifest(world);
        manifest["environment_revision_id"] = Guid.CreateVersion7().ToString();
        Details(await UploadAsync(world, manifest.ToJsonString())).Single().GetProperty("reason").GetString().ShouldBe("unknown_environment_revision");

        var environment = await world.Client.SendAsync(
            HttpMethod.Post,
            $"/v1/projects/{world.Project}/environments/staging/revisions",
            world.Admin,
            new { base_urls = new { web = "https://staging.example.test" }, dependency_versions = new { postgres = "18.0" } },
            NewKey());
        environment.Status.ShouldBe(HttpStatusCode.Created, environment.ErrorMessage);
        manifest["environment_revision_id"] = environment.Id("revision_id").ToString();

        var accepted = await UploadAsync(world, manifest.ToJsonString());
        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);
        (await _fixture.ScalarAsync<Guid>(world.Database, "select environment_revision_id from public.manifest")).ShouldBe(environment.Id("revision_id"));
    }

    // ------------------------------------------------------------------ AC4

    [Fact]
    public async Task UploadManifest_SecretInVariantParams_Is422_NoEcho()
    {
        await using var world = await StartAsync(_fixture);
        const string token = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        var manifest = ManifestUnitTests.Build(world.SuiteRevision, world.Cases, [new() { ["browser"] = "x", ["mode"] = token }], ["staging"]);
        using var listener = RejectionListener(out var reasons);

        var answer = await UploadAsync(world, manifest.ToJsonString());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(answer).Select(detail => detail.GetProperty("reason").GetString()).Distinct().ShouldBe(["secret_in_variant_params"]);
        Details(answer).First().GetProperty("path").GetString().ShouldBe("/planned_executions/0/variant/params/#2");
        answer.Body.GetRawText().ShouldNotContain(token);
        reasons.ShouldContain("secret_in_variant_params");

        // A reference is not a secret.
        var referenced = ManifestUnitTests.Build(world.SuiteRevision, world.Cases, [new() { ["browser"] = "x", ["api_token"] = "secret:ci_token" }], ["staging"]);
        (await UploadAsync(world, referenced.ToJsonString())).Status.ShouldBe(HttpStatusCode.Created);
    }

    // ------------------------------------------------------------------ AC5

    [Fact]
    public async Task UploadManifest_ShardPlanGapOrOverlap_Is422_AndTheEmptyBoundary()
    {
        await using var world = await StartAsync(_fixture);
        var gap = Manifest(world);
        gap["shard_plan"]!["shards"]![1]!["from_index"] = 3;
        Details(await UploadAsync(world, gap.ToJsonString())).First().GetProperty("reason").GetString().ShouldBe("matrix_not_covered");

        var overlap = Manifest(world);
        overlap["shard_plan"]!["shards"]![1]!["from_index"] = 1;
        Details(await UploadAsync(world, overlap.ToJsonString())).First().GetProperty("reason").GetString().ShouldBe("matrix_not_covered");

        // Declared empty (no case in the matrix): accepted. Cases with no planned execution: refused.
        var declared = ManifestUnitTests.Build(world.SuiteRevision, [], [], []);
        (await UploadAsync(world, declared.ToJsonString())).Status.ShouldBe(HttpStatusCode.Created);

        var undeclared = Manifest(world);
        undeclared["planned_executions"] = new JsonArray();
        undeclared["shard_plan"] = new JsonObject { ["shards"] = new JsonArray(new JsonObject { ["shard_id"] = "s", ["from_index"] = 0, ["to_index"] = 0 }) };
        Details(await UploadAsync(world, undeclared.ToJsonString())).First().GetProperty("reason").GetString().ShouldBe("matrix_not_covered");
    }

    // ------------------------------------------------------------------ AC7

    [Fact]
    public async Task Manifest_UpdateDelete_Revoked_ForAppRole_AndRefusedForTheOwner()
    {
        await using var world = await StartAsync(_fixture);
        (await UploadAsync(world, Manifest(world).ToJsonString())).Status.ShouldBe(HttpStatusCode.Created);

        foreach (var statement in new[]
        {
            "update public.manifest set planned_count = 0",
            "update public.manifest set document = '{}'::jsonb",
            "delete from public.manifest",
        })
        {
            await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(world.Database));
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
            {
                set.Parameters.AddWithValue("org", world.Org.ToString());
                await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            }

            await using var command = new NpgsqlCommand(statement, connection, transaction);
            (await Should.ThrowAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)))
                .SqlState.ShouldBe("42501", statement);

            (await Should.ThrowAsync<PostgresException>(async () => await _fixture.ExecuteAsync(world.Database, statement)))
                .MessageText.ShouldContain("manifest_immutable", customMessage: statement);
        }
    }

    // ------------------------------------------------------------------ AC8, C2

    [Fact]
    public async Task Manifest_CrossTenant_404_AndPermissionAndScope_403()
    {
        await using var world = await StartAsync(_fixture);
        var body = Manifest(world).ToJsonString();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");

        (await UploadAsync(world, body, TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg))).Status.ShouldBe(HttpStatusCode.NotFound);
        (await UploadAsync(world, body, TenancyApiFactory.Resolved(user: Guid.CreateVersion7(), orgHeader: world.Org))).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await UploadAsync(world, body, TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: world.Project, servicePermissions: ["run:read"])))
            .Status.ShouldBe(HttpStatusCode.Forbidden);

        var other = await CreateProjectAsync(world.Client, world.User, world.Org, "other");
        var wrongScope = await UploadAsync(world, body, TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: other, servicePermissions: ["run:create"]));
        wrongScope.Status.ShouldBe(HttpStatusCode.Forbidden);

        // The intended caller: a CI producer scoped to the project.
        var ci = await UploadAsync(world, body, TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: world.Project, servicePermissions: ["run:create"]));
        ci.Status.ShouldBe(HttpStatusCode.Created, ci.ErrorMessage);
        (await _fixture.ScalarAsync<string>(world.Database, "select verified_by from public.manifest")).ShouldStartWith("ci_producer:");

        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>().BeginAsync(otherOrg, null, true, TestContext.Current.CancellationToken);
        (await services.ServiceProvider.GetRequiredService<ITenantDb>().QueryAsync<Guid>("select id from public.manifest", null, TestContext.Current.CancellationToken))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task Manifest_Insert_WrongOrg_FailsWithCheck()
    {
        await using var world = await StartAsync(_fixture);
        var otherOrg = await CreateOrgAsync(world.Client, Guid.CreateVersion7(), "rival");

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("org", otherOrg.ToString());
            await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        await using var insert = new NpgsqlCommand(
            $"""
            insert into public.manifest (id, org_id, project_id, manifest_hash, schema_version, suite_revision_id, shard_plan, matrix, env_needs, planned_count, document, verified_by)
            values ('{Guid.CreateVersion7()}', '{world.Org}', '{world.Project}', 'sha256:{new string('a', 64)}', 1, '{world.SuiteRevision}', jsonb_build_object(), jsonb_build_object(), jsonb_build_object(), 0, jsonb_build_object(), 'x')
            """,
            connection,
            transaction);
        (await Should.ThrowAsync<PostgresException>(async () => await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)))
            .SqlState.ShouldBe("42501");
    }

    // ------------------------------------------------------------------ review fixes (05.1.1 §8a)

    [Fact]
    public async Task NoBodyWork_BeforeTheCallerIsKnownAndAllowed()
    {
        await using var world = await StartAsync(_fixture);
        using var listener = RejectionListener(out var reasons);

        // Anonymous: 401 even for a body that is not JSON at all.
        (await UploadAsync(world, "{ not json", _ => { })).Status.ShouldBe(HttpStatusCode.Unauthorized);

        // A member without run:create: 403, not the 422 the body would earn — the schema never ran.
        var stranger = TenancyApiFactory.Resolved(user: Guid.CreateVersion7(), orgHeader: world.Org);
        (await UploadAsync(world, "{ not json", stranger)).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await UploadAsync(world, """{"schema_version":2}""", stranger)).Status.ShouldBe(HttpStatusCode.Forbidden);
        reasons.ShouldNotContain("schema");
    }

    [Fact]
    public async Task ALoneSurrogate_Is422InvalidValue_NotA500()
    {
        await using var world = await StartAsync(_fixture);
        var body = ManifestUnitTests.Build(world.SuiteRevision, world.Cases, [new() { ["browser"] = "PLACEHOLDER" }], ["staging"])
            .ToJsonString()
            .Replace("PLACEHOLDER", "x\\ud800y", StringComparison.Ordinal);

        var answer = await UploadAsync(world, body);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("manifest_invalid");
        Details(answer).Single().GetProperty("reason").GetString().ShouldBe("invalid_value");
    }

    [Fact]
    public async Task AnEnvironmentRevisionOfAnotherProject_CannotBePinned_EvenByTheOwner()
    {
        await using var world = await StartAsync(_fixture);
        var other = await CreateProjectAsync(world.Client, world.User, world.Org, "other");
        var foreign = await world.Client.SendAsync(
            HttpMethod.Post,
            $"/v1/projects/{other}/environments/staging/revisions",
            world.Admin,
            new { base_urls = new { web = "https://other.example.test" }, dependency_versions = new { } },
            NewKey());
        foreign.Status.ShouldBe(HttpStatusCode.Created, foreign.ErrorMessage);

        var manifest = Manifest(world);
        manifest["environment_revision_id"] = foreign.Id("revision_id").ToString();
        Details(await UploadAsync(world, manifest.ToJsonString())).Single().GetProperty("reason").GetString().ShouldBe("unknown_environment_revision");

        (await UploadAsync(world, Manifest(world).ToJsonString())).Status.ShouldBe(HttpStatusCode.Created);
        (await Should.ThrowAsync<PostgresException>(async () => await _fixture.ExecuteAsync(
            world.Database,
            $"update public.manifest set environment_revision_id = '{foreign.Id("revision_id")}'")))
            .MessageText.ShouldContain("manifest_immutable");
        (await Should.ThrowAsync<PostgresException>(async () => await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.manifest (id, org_id, project_id, manifest_hash, schema_version, suite_revision_id, environment_revision_id, shard_plan, matrix, env_needs, planned_count, document, verified_by)
            values ('{Guid.CreateVersion7()}', '{world.Org}', '{world.Project}', 'sha256:{new string('b', 64)}', 1, '{world.SuiteRevision}', '{foreign.Id("revision_id")}', jsonb_build_object(), jsonb_build_object(), jsonb_build_object(), 0, jsonb_build_object(), 'x')
            """)))
            .MessageText.ShouldContain("cross_project_reference");
    }

    // ------------------------------------------------------------------ P3

    [Fact]
    public async Task UploadManifest_IsAFixedNumberOfStatements_WhateverItsSize()
    {
        await using var world = await StartAsync(_fixture, cases: 40);

        async Task<int> CountAsync(int variants)
        {
            world.Api.Statements.Clear();
            (await UploadAsync(world, Manifest(world, variants).ToJsonString())).Status.ShouldBe(HttpStatusCode.Created);

            return world.Api.Statements.Snapshot().Count(statement => statement.Contains("public.", StringComparison.Ordinal));
        }

        var small = await CountAsync(1);
        var large = await CountAsync(40); // 1 600 planned executions, about 200 KiB: near the 256 KiB cap

        large.ShouldBe(small);
        small.ShouldBeLessThanOrEqualTo(12, string.Join(Environment.NewLine, world.Api.Statements.Snapshot()));
    }

    private static MeterListener RejectionListener(out System.Collections.Concurrent.ConcurrentBag<string> reasons)
    {
        var bag = new System.Collections.Concurrent.ConcurrentBag<string>();
        reasons = bag;
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == EvidgateTelemetry.MeterName && instrument.Name == "evidgate_manifest_rejected_total")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "reason" && tag.Value is string reason)
                {
                    bag.Add(reason);
                }
            }
        });
        listener.Start();

        return listener;
    }
}
