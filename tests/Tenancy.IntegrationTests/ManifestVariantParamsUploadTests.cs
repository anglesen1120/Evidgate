using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Observability;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The upload refuses the variant parameters run creation would refuse (the catalogue normaliser's key, string and
/// stored-size bounds) before anything is written: 422 <c>manifest_invalid</c>, one detail per planned execution at
/// <c>/planned_executions/{i}/variant/params</c>, no value echoed, matrix variants left alone.
/// </summary>
[Trait("Category", "Docker")]
[Collection(ExclusiveTenancySuite.Name)]
public sealed class ManifestVariantParamsUploadTests
{
    private const string Token = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";

    private readonly PostgresTenancyFixture _fixture;

    public ManifestVariantParamsUploadTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    internal sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project,
        Guid SuiteRevision,
        Guid EnvironmentRevision,
        Guid[] Cases,
        DedicatedDatabase Dedicated)
        : IAsyncDisposable
    {
        public Action<System.Net.Http.Headers.HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try
            {
                await Api.DisposeAsync();
            }
            finally
            {
                await Dedicated.DisposeAsync();
            }
        }
    }

    /// <summary>A private database with an org, a project, one registered case, a suite revision and an environment revision.</summary>
    private static Task<World> StartAsync(PostgresTenancyFixture fixture, string slug = "acme") =>
        fixture.StartDedicatedAsync(async dedicated =>
        {
            var database = dedicated.Name;
            var api = TenancyApiFactory.RealResolver(fixture.AppConnectionString(database));
            var client = api.CreateClient();
            var user = Guid.CreateVersion7();
            var org = await CreateOrgAsync(client, user, slug);
            var project = await CreateProjectAsync(client, user, org, "checkout");
            var revisions = await ManifestTests.SeedCasesAsync(fixture, api, org, project, database, 1);
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
            var environment = await client.SendAsync(
                HttpMethod.Post,
                $"/v1/projects/{project}/environments/staging/revisions",
                admin,
                new { base_urls = new { web = "https://staging.example.test" }, dependency_versions = new { postgres = "18.0" } },
                NewKey());
            environment.Status.ShouldBe(HttpStatusCode.Created, environment.ErrorMessage);

            return new World(
                api,
                client,
                database,
                user,
                org,
                project,
                revision.Id("suite_revision_id"),
                environment.Id("revision_id"),
                revisions,
                dedicated);
        });

    private static string Body(World world, string parameters, bool includePlanned = true, bool includeMatrix = true) =>
        ManifestVariantParamsEquivalenceTests.ManifestJson(
            world.SuiteRevision,
            world.Cases[0],
            parameters,
            world.EnvironmentRevision,
            includePlanned,
            includeMatrix);

    private static Task<ApiAnswer> UploadAsync(World world, string body) =>
        ManifestVariantParamsEquivalenceTests.UploadAsync(world.Client, world.Admin, world.Project, body);

    private static List<JsonElement> Details(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()];

    private async Task<long[]> CountsAsync(World world) =>
    [
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.manifest where org_id = '{world.Org}'"),
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.idempotency_record where org_id = '{world.Org}'"),
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.outbox where org_id = '{world.Org}'"),
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.audit_log where org_id = '{world.Org}'"),
    ];

    [Fact]
    public async Task Upload_ThirtyTwoKeysOf256AsciiChars_Is422InvalidVariantAtThePointer_AndWritesNoRow()
    {
        await using var world = await StartAsync(_fixture);
        var parameters = JsonSerializer.Serialize(ManifestVariantParamsEquivalenceTests.Parameters(32, 256));
        Encoding.UTF8.GetByteCount(parameters).ShouldBeGreaterThan(8000);
        using var listener = RejectionListener(out var reasons);

        var controlBefore = await CountsAsync(world);
        var control = await UploadAsync(world, Body(world, $$"""{"mode":"{{Token}}"}"""));
        control.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var controlDelta = Delta(controlBefore, await CountsAsync(world));

        var before = await CountsAsync(world);
        var answer = await UploadAsync(world, Body(world, parameters));
        var delta = Delta(before, await CountsAsync(world));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("manifest_invalid");
        var detail = Details(answer).ShouldHaveSingleItem();
        detail.GetProperty("path").GetString().ShouldBe("/planned_executions/0/variant/params");
        detail.GetProperty("reason").GetString().ShouldBe("invalid_variant");
        detail.GetProperty("index").GetInt32().ShouldBe(0);
        answer.Body.GetRawText().ShouldNotContain(new string('x', 16));
        delta.ShouldBe(controlDelta);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.manifest")).ShouldBe(0);

        var repeatedBefore = await CountsAsync(world);
        var repeated = await UploadAsync(world, Body(world, parameters));
        repeated.Status.ShouldBe(answer.Status);
        Details(repeated).Select(item => item.GetRawText()).ShouldBe(Details(answer).Select(item => item.GetRawText()));
        Delta(repeatedBefore, await CountsAsync(world)).ShouldBe(controlDelta);
        reasons.ShouldContain("invalid_variant");
    }

    [Fact]
    public async Task Upload_AstralText_Is422At129CodePoints_AndCreatedAt128()
    {
        await using var world = await StartAsync(_fixture);

        var refused = await UploadAsync(world, Body(world, ManifestVariantParamsEquivalenceTests.AstralParameters(129)));
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(refused).ShouldHaveSingleItem().GetProperty("reason").GetString().ShouldBe("invalid_variant");
        refused.Body.GetRawText().ShouldNotContain("\U0001f600");

        var accepted = await UploadAsync(world, Body(world, ManifestVariantParamsEquivalenceTests.AstralParameters(128)));
        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.manifest")).ShouldBe(1);
    }

    [Fact]
    public async Task Upload_CjkSixKeysOf256_Is422_AndTheFittingStoredSizeIsCreated()
    {
        await using var world = await StartAsync(_fixture);
        var oversized = JsonSerializer.Serialize(ManifestVariantParamsEquivalenceTests.Parameters(6, 256, '\u754c'));
        Encoding.UTF8.GetByteCount(oversized).ShouldBeGreaterThan(8000);

        var refused = await UploadAsync(world, Body(world, oversized));
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(refused).ShouldHaveSingleItem().GetProperty("path").GetString().ShouldBe("/planned_executions/0/variant/params");

        var atLimit = await UploadAsync(world, Body(world, ManifestVariantParamsEquivalenceTests.SizedParameters(8000, cjk: true)));
        atLimit.Status.ShouldBe(HttpStatusCode.Created, atLimit.ErrorMessage);
    }

    [Fact]
    public async Task Upload_ThirtyThreeKeys_IsRefusedByTheSchema_NotByTheVariantGuard()
    {
        await using var world = await StartAsync(_fixture);

        var tooMany = await UploadAsync(world, Body(world, JsonSerializer.Serialize(ManifestVariantParamsEquivalenceTests.Parameters(33, 1))));
        tooMany.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(tooMany).Select(detail => detail.GetProperty("reason").GetString()).ShouldNotContain("invalid_variant");

        var atSchemaLimit = await UploadAsync(world, Body(world, JsonSerializer.Serialize(ManifestVariantParamsEquivalenceTests.Parameters(32, 1))));
        atSchemaLimit.Status.ShouldBe(HttpStatusCode.Created, atSchemaLimit.ErrorMessage);
    }

    [Fact]
    public async Task Upload_ExactlyEightThousandStoredBytes_CreatesTheManifestAndARun_AndOneByteOverIs422()
    {
        await using var world = await StartAsync(_fixture);

        var over = await UploadAsync(world, Body(world, ManifestVariantParamsEquivalenceTests.SizedParameters(8001)));
        over.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(over).ShouldHaveSingleItem().GetProperty("reason").GetString().ShouldBe("invalid_variant");

        var atLimit = await UploadAsync(world, Body(world, ManifestVariantParamsEquivalenceTests.SizedParameters(8000)));
        atLimit.Status.ShouldBe(HttpStatusCode.Created, atLimit.ErrorMessage);
        var manifestId = atLimit.Id("manifest_id");

        var run = await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{world.Project}/runs", world.Admin, new { manifest_id = manifestId }, NewKey());

        run.Status.ShouldBe(HttpStatusCode.Created, run.ErrorMessage);
        (await _fixture.ScalarAsync<Guid>(world.Database, $"select id from public.run where org_id = '{world.Org}'")).ShouldBe(run.Id("run_id"));
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.planned_execution where org_id = '{world.Org}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Upload_SecretAndOversizeParams_KeepsTheSecretRefusal()
    {
        await using var world = await StartAsync(_fixture);
        var parameters = JsonSerializer.Serialize(new Dictionary<string, string>(ManifestVariantParamsEquivalenceTests.Parameters(31, 256))
        {
            ["mode"] = Token,
        });
        using var listener = RejectionListener(out var reasons);

        var answer = await UploadAsync(world, Body(world, parameters));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(answer).Select(detail => detail.GetProperty("reason").GetString()).Distinct().ShouldBe(["secret_in_variant_params"]);
        Details(answer).First().GetProperty("path").GetString().ShouldBe("/planned_executions/0/variant/params/#32");
        answer.Body.GetRawText().ShouldNotContain(Token);
        reasons.ShouldContain("secret_in_variant_params");
        reasons.ShouldNotContain("invalid_variant");
    }

    [Fact]
    public async Task Upload_OversizeMatrixParamsWithNoPlannedExecutions_IsCreated()
    {
        await using var world = await StartAsync(_fixture);
        var parameters = JsonSerializer.Serialize(ManifestVariantParamsEquivalenceTests.Parameters(32, 256));

        var answer = await UploadAsync(world, Body(world, parameters, includePlanned: false));

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.manifest")).ShouldBe(1);
        (await _fixture.ScalarAsync<int>(world.Database, "select planned_count from public.manifest")).ShouldBe(0);
    }

    private static long[] Delta(long[] before, long[] after) => [.. before.Zip(after, (from, to) => to - from)];

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

    [Fact]
    public async Task Upload_SecretReferenceUnderASecretNamedKey_IsRefusedAsInvalidVariant_WithoutEcho()
    {
        await using var world = await StartAsync(_fixture);
        var parameters = JsonSerializer.Serialize(new Dictionary<string, string> { ["browser"] = "x", ["api_token"] = "secret:ci_token" });

        var answer = await UploadAsync(world, Body(world, parameters));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(answer).ShouldHaveSingleItem().GetProperty("path").GetString().ShouldBe("/planned_executions/0/variant/params");
        answer.Body.GetRawText().ShouldNotContain("ci_token");
        answer.Body.GetRawText().ShouldNotContain("api_token");
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.manifest where org_id = '{world.Org}'")).ShouldBe(0);
    }

    [Fact]
    public async Task Upload_FiftyOneRefusedExecutions_ReportFiftyDetailsAtTheirOwnIndexes()
    {
        await using var world = await StartAsync(_fixture);
        const int rows = 51;
        var template = System.Text.Json.Nodes.JsonNode.Parse(Body(world, "{\"browser\":\"x\"}"))!.AsObject();
        var executionTemplate = template["planned_executions"]![0]!.ToJsonString();
        var planned = new System.Text.Json.Nodes.JsonArray();
        var variants = new System.Text.Json.Nodes.JsonArray();
        for (var index = 0; index < rows; index++)
        {
            // A different k00 length per row keeps every params text distinct; the total stays over 8000 bytes.
            var parameters = new Dictionary<string, string>(ManifestVariantParamsEquivalenceTests.Parameters(32, 256)) { ["k00"] = new string('x', 256 - index) };
            var node = System.Text.Json.Nodes.JsonNode.Parse(executionTemplate)!.AsObject();
            node["variant"] = new System.Text.Json.Nodes.JsonObject { ["params"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(parameters)) };
            planned.Add(node);
            variants.Add(new System.Text.Json.Nodes.JsonObject { ["params"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(parameters)) });
        }

        template["planned_executions"] = planned;
        template["matrix"]!["variants"] = variants;
        template["shard_plan"]!["shards"]![0]!["to_index"] = rows;

        var answer = await UploadAsync(world, template.ToJsonString());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        var details = Details(answer);
        details.Count.ShouldBe(50);
        details.Select(detail => detail.GetProperty("reason").GetString()).Distinct().ShouldBe(["invalid_variant"]);
        details.Select((detail, position) => detail.GetProperty("path").GetString() == $"/planned_executions/{position}/variant/params").ShouldAllBe(match => match);
    }
}
