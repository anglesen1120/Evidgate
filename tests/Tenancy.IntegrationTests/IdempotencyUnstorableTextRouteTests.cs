using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;
using World = Tenancy.IntegrationTests.RunCreationTests.World;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The idempotency stage re-serialises a parsed body before the handler runs, so a body the writer refuses — an unpaired
/// UTF-16 surrogate in a member name or string, or nesting the command wrapper pushes past the writer's depth limit — must
/// answer 400 <c>request_invalid</c> with nothing stored and no echo. The second route is <c>CreateRun</c>, whose handler
/// refuses unparsed text itself because those bytes never reach the command; the Authoring edge answers 422
/// <c>invalid_text</c> before the pipeline, so no Authoring route can reach this guard.
/// </summary>
[Trait("Category", "Docker")]
public sealed class IdempotencyUnstorableTextRouteTests(
    PostgresTenancyFixture fixture,
    TenancyHostPool hosts) : IClassFixture<TenancyHostPool>
{
    [Fact]
    public Task SuiteRevision_LoneHighSurrogateName_400WithoutEchoOrWrites() =>
        AssertSuiteRefusedAsync(
            """{"\ud800":1,"marker-zq7":"SECRETMARK"}""",
            """{"k":1,"marker-zq7":"SECRETMARK"}""");

    [Fact]
    public Task SuiteRevision_LoneLowSurrogateName_400WithoutEchoOrWrites() =>
        AssertSuiteRefusedAsync(
            """{"\udc00":1,"marker-zq7":"SECRETMARK"}""",
            """{"k":1,"marker-zq7":"SECRETMARK"}""");

    [Fact]
    public Task SuiteRevision_LoneHighSurrogateValue_400WithoutEchoOrWrites() =>
        AssertSuiteRefusedAsync(
            """{"k":"\ud800","marker-zq7":"SECRETMARK"}""",
            """{"k":"ok","marker-zq7":"SECRETMARK"}""");

    [Fact]
    public async Task CreateRun_LoneSurrogateName_RefusesWithoutRunOrIdempotencyRow()
    {
        await using var world = await StartAsync();
        var route = $"/v1/projects/{world.Project}/runs";
        var key = NewKey();
        var before = await CountsAsync(world);
        var malformed = $$"""{"manifest_id":"{{world.ManifestId}}","\ud800":1,"marker-zq7":"SECRETMARK"}""";

        using var response = await PostAsync(world, route, malformed, key);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // The body bytes are not serialised into the command, so the handler's own check answers (422), not the idempotency stage.
        AssertRefusal(response.StatusCode, text, HttpStatusCode.UnprocessableEntity);
        (await RecordCountAsync(world, key)).ShouldBe(0);
        (await CountsAsync(world)).ShouldBe(before);
        await RunCreationTests.AssertNoRunRowsAsync(fixture, world);

        using var accepted = await PostAsync(world, route, RunCreationTests.RunBody(world.ManifestId), key);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Created,
            await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        (await RecordCountAsync(world, key)).ShouldBe(1);
    }

    [Fact]
    public Task SuiteRevision_AsciiParams_ReplaysWithoutChangingRequestHash() =>
        AssertReplayAsync("""{"k":"ok"}""");

    [Fact]
    public Task SuiteRevision_ValidBmpAndPairedSurrogateMetadata_ReplaysWithoutChangingRequestHash() =>
        // Direct params keys are ASCII-only; ignored metadata accepts nested Unicode names without changing variant identity.
        AssertReplayAsync("""{"k":"ok","_meta":{"é":1,"\ud83d\ude00":2}}""");

    [Theory]
    [InlineData(55)]
    [InlineData(56)]
    [InlineData(57)]
    [InlineData(58)]
    [InlineData(59)]
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(62)]
    [InlineData(63)]
    public async Task SuiteRevision_RequestDepth55Through63_IsStoredWithOneIdempotencyRow(int requestDepth)
    {
        await using var world = await StartAsync();
        var route = await SuiteRouteAsync(world);
        var key = NewKey();

        using var response = await PostAsync(world, route, RevisionBody(world, NestedParams(requestDepth)), key);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, $"request depth {requestDepth}: HTTP {(int)response.StatusCode}: {text}");
        await AssertStoredRevisionAsync(world, text);
        (await RecordCountAsync(world, key)).ShouldBe(1);
    }

    [Fact]
    public async Task SuiteRevision_RequestDepth64_RefusesWithoutEchoOrIdempotencyRecord()
    {
        await using var world = await StartAsync();
        var route = await SuiteRouteAsync(world);
        var key = NewKey();
        var before = await CountsAsync(world);

        // The body parses at depth 64; the command wrapper adds the level that overflows the writer's default limit.
        using var response = await PostAsync(world, route, RevisionBody(world, NestedParams(64, "\"SECRETMARK\"")), key);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        AssertRefusal(response.StatusCode, text, HttpStatusCode.BadRequest);
        (await RecordCountAsync(world, key)).ShouldBe(0);
        (await CountsAsync(world)).ShouldBe(before,
            "a refused request adds no audit entry, outbox row, task, run or suite revision");

        // The same key still works, so the refusal claimed nothing.
        using var accepted = await PostAsync(world, route, RevisionBody(world, NestedParams(63)), key);
        var acceptedText = await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Created, acceptedText);
        await AssertStoredRevisionAsync(world, acceptedText);
        (await RecordCountAsync(world, key)).ShouldBe(1);
    }

    /// <summary>
    /// One object level per depth unit: the body root, members array and member object take the first three, so the leaf
    /// sits at <paramref name="requestDepth"/>.
    /// </summary>
    private static string NestedParams(int requestDepth, string leaf = "0")
    {
        var parameters = leaf;
        for (var depth = 3; depth < requestDepth; depth++)
        {
            parameters = "{\"_meta\":" + parameters + "}";
        }

        return parameters;
    }

    private Task<World> StartAsync() => RunCreationTests.StartAsync(fixture, cases: 1, hosts: hosts);

    private async Task AssertSuiteRefusedAsync(string malformedParams, string validParams)
    {
        await using var world = await StartAsync();
        var route = await SuiteRouteAsync(world);
        var key = NewKey();
        var before = await CountsAsync(world);

        using var response = await PostAsync(world, route, RevisionBody(world, malformedParams), key);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        AssertRefusal(response.StatusCode, text, HttpStatusCode.BadRequest);
        (await RecordCountAsync(world, key)).ShouldBe(0);
        (await CountsAsync(world)).ShouldBe(before,
            "a refused request adds no audit entry, outbox row, task, run or suite revision");

        using var accepted = await PostAsync(world, route, RevisionBody(world, validParams), key);
        var acceptedText = await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Created, acceptedText);
        await AssertStoredRevisionAsync(world, acceptedText);
        (await RecordCountAsync(world, key)).ShouldBe(1);
    }

    private async Task AssertReplayAsync(string parameters)
    {
        await using var world = await StartAsync();
        var route = await SuiteRouteAsync(world);
        var key = NewKey();
        var body = RevisionBody(world, parameters);
        using var first = await PostAsync(world, route, body, key);
        var firstText = await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        first.StatusCode.ShouldBe(HttpStatusCode.Created, firstText);
        var revision = await AssertStoredRevisionAsync(world, firstText);
        (await RecordCountAsync(world, key)).ShouldBe(1);
        var firstHash = await RequestHashAsync(world, key);
        firstHash.ShouldNotBeNullOrWhiteSpace();
        var beforeReplay = await CountsAsync(world);

        using var replay = await PostAsync(world, route, body, key);
        var replayText = await replay.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        replay.StatusCode.ShouldBe(first.StatusCode, replayText);
        replayText.ShouldBe(firstText);
        (await AssertStoredRevisionAsync(world, replayText)).ShouldBe(revision);
        (await RecordCountAsync(world, key)).ShouldBe(1);
        (await RequestHashAsync(world, key)).ShouldBe(firstHash);
        (await CountsAsync(world)).ShouldBe(beforeReplay);
    }

    private static string RevisionBody(World world, string parameters) =>
        $$$"""{"members":[{"case_revision_id":"{{{world.Cases[0]}}}","params":{{{parameters}}}}]}""";

    private async Task<string> SuiteRouteAsync(World world)
    {
        var suite = await fixture.ScalarAsync<Guid>(world.Database,
            $"select suite_id from public.suite_revision where org_id = '{world.Org}' and id = '{world.SuiteRevision}'");
        return $"/v1/suites/{suite}/revisions";
    }

    private async Task<Guid> AssertStoredRevisionAsync(World world, string text)
    {
        using var document = JsonDocument.Parse(text);
        var revision = document.RootElement.GetProperty("suite_revision_id").GetGuid();
        (await fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.suite_revision where org_id = '{world.Org}' and id = '{revision}'")).ShouldBe(1);
        return revision;
    }

    private static async Task<HttpResponseMessage> PostAsync(World world, string route, string body, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route);
        world.Admin(request.Headers);
        request.Headers.Add("Idempotency-Key", key);
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await world.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static void AssertRefusal(HttpStatusCode status, string text, params HttpStatusCode[] expected)
    {
        expected.ShouldContain(status, text);
        using var document = JsonDocument.Parse(text);
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("request_invalid");
        var detail = error.GetProperty("details").EnumerateArray().Single();
        detail.GetProperty("path").GetString().ShouldBe(string.Empty);
        detail.GetProperty("reason").GetString().ShouldBe("invalid_value");
        text.ShouldNotContain("SECRETMARK");
        text.ShouldNotContain("marker-zq7");
        text.Contains("\\ud800", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
        text.Contains("\\udc00", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
    }

    private Task<long> RecordCountAsync(World world, string key) =>
        fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.idempotency_record where org_id = '{world.Org}' and key = '{key}'");

    private Task<string> RequestHashAsync(World world, string key) =>
        fixture.ScalarAsync<string>(world.Database,
            $"select request_hash from public.idempotency_record where org_id = '{world.Org}' and key = '{key}'");

    private async Task<long[]> CountsAsync(World world)
    {
        var audit = await fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.audit_log where org_id = '{world.Org}'");
        var outbox = await fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.outbox where org_id = '{world.Org}'");
        var task = await fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.task where org_id = '{world.Org}'");
        var run = await fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.run where org_id = '{world.Org}'");
        var revisions = await fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.suite_revision where org_id = '{world.Org}'");
        return [audit, outbox, task, run, revisions];
    }
}
