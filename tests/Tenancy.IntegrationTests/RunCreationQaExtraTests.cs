// QA-added negative, boundary and exploratory tests for 05.2.1-T1 (run creation). Written by the QA agent, which
// did not write the implementation. Request bodies are built inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.2.1-T1 QA extras: body-shape, rune and byte boundaries, cross-project scope, and concurrent creates, all through
/// the real host and a real PostgreSQL.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class RunCreationQaExtraTests
{
    private readonly PostgresTenancyFixture _fixture;

    public RunCreationQaExtraTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record RawAnswer(HttpStatusCode Status, string Text)
    {
        public string? ErrorCode
        {
            get
            {
                try
                {
                    return JsonNode.Parse(Text)?["error"]?["code"]?.GetValue<string>();
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }

        public JsonNode? Detail(int index = 0) => JsonNode.Parse(Text)?["error"]?["details"]?[index];
    }

    private static async Task<RawAnswer> SendRawAsync(
        RunCreationTests.World world,
        string body,
        Guid? project = null,
        Action<HttpRequestHeaders>? caller = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/projects/{project ?? world.Project}/runs");
        (caller ?? world.Admin)(request.Headers);
        request.Headers.Add("Idempotency-Key", NewKey());
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await world.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return new RawAnswer(response.StatusCode, text);
    }

    private static string Body(Guid manifestId, string extra = "") =>
        "{\"manifest_id\":\"" + manifestId + "\"" + extra + "}";

    private static string Astral(int runes) => string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x1F600), runes));

    private async Task AssertNoRunRowsAsync(RunCreationTests.World world)
    {
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.run")).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.planned_execution")).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.producer_slot")).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.outbox")).ShouldBe(0);
    }

    // ------------------------------------------------------------------ body shape (422 request_invalid, no write)

    [Fact]
    public async Task QaExtra_NonJsonBody_422_NoRows()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answer = await SendRawAsync(world, "not json");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        await AssertNoRunRowsAsync(world);
    }

    [Fact]
    public async Task QaExtra_EmptyBody_422_NoRows()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answer = await SendRawAsync(world, string.Empty);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        await AssertNoRunRowsAsync(world);
    }

    [Fact]
    public async Task QaExtra_JsonArrayBody_422_NotAnObject()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answer = await SendRawAsync(world, "[]");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        await AssertNoRunRowsAsync(world);
    }

    [Fact]
    public async Task QaExtra_ManifestIdNumber_422_RequiredAtPath()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answer = await SendRawAsync(world, "{\"manifest_id\":42}");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        answer.Detail()!["path"]!.GetValue<string>().ShouldBe("/manifest_id");
        await AssertNoRunRowsAsync(world);
    }

    [Fact]
    public async Task QaExtra_ManifestIdNotAGuid_422_AtPath()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answer = await SendRawAsync(world, "{\"manifest_id\":\"not-a-guid\"}");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.Detail()!["path"]!.GetValue<string>().ShouldBe("/manifest_id");
        await AssertNoRunRowsAsync(world);
    }

    [Fact]
    public async Task QaExtra_ReleaseNumber_422_AtReleasePath()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answer = await SendRawAsync(world, Body(world.ManifestId, ",\"release\":7"));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        answer.Detail()!["path"]!.GetValue<string>().ShouldBe("/release");
        await AssertNoRunRowsAsync(world);
    }

    [Fact]
    public async Task QaExtra_EmptyRelease_422_RequestInvalid()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answer = await SendRawAsync(world, Body(world.ManifestId, ",\"release\":\"\""));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        answer.Detail()!["path"]!.GetValue<string>().ShouldBe("/release");
        await AssertNoRunRowsAsync(world);
    }

    // ------------------------------------------------------------------ rune boundary (code points, not UTF-16)

    [Fact]
    public async Task QaExtra_Release128AstralRunes_IsUnresolvableNotInvalid()
    {
        // 128 code points = 256 UTF-16 units. Counted as code points it is in range, so the syntactic check passes and
        // AC10 answers release_unresolvable. Counted in UTF-16 it would wrongly be request_invalid.
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answer = await SendRawAsync(world, Body(world.ManifestId, ",\"release\":\"" + Astral(128) + "\""));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("release_unresolvable");
        await AssertNoRunRowsAsync(world);
    }

    [Fact]
    public async Task QaExtra_Release129AstralRunes_422_RequestInvalid()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answer = await SendRawAsync(world, Body(world.ManifestId, ",\"release\":\"" + Astral(129) + "\""));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        answer.Detail()!["path"]!.GetValue<string>().ShouldBe("/release");
        await AssertNoRunRowsAsync(world);
    }

    // ------------------------------------------------------------------ text that cannot be stored

    [Fact]
    public async Task QaExtra_NulInComponents_422_NoRows()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answer = await SendRawAsync(world, Body(world.ManifestId, ",\"components\":{\"a\":\"x\\u0000y\"}"));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        await AssertNoRunRowsAsync(world);
    }

    [Fact]
    public async Task QaExtra_LoneSurrogateInComponents_422_NoRows()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answer = await SendRawAsync(world, Body(world.ManifestId, ",\"components\":{\"a\":\"x\\ud800y\"}"));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        await AssertNoRunRowsAsync(world);
    }

    // ------------------------------------------------------------------ byte boundaries

    [Fact]
    public async Task QaExtra_ComponentsOneByteOver8KiB_422_NoRows()
    {
        // Raw serialised form is 11 bytes of framing plus the payload: 8182 + 11 = 8193 bytes, one over the cap.
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var blob = new string('x', 8182);

        var answer = await SendRawAsync(world, Body(world.ManifestId, ",\"components\":{\"blob\":\"" + blob + "\"}"));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        await AssertNoRunRowsAsync(world);
    }

    [Fact]
    public async Task QaExtra_ComponentsExactly8KiB_Accepted()
    {
        // 8192 bytes exactly is the largest accepted object. This is a success-path case, so it depends on the run
        // INSERT path and is expected to fail while the array-write defect (D1) is present.
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var blob = new string('x', 8181);

        var answer = await SendRawAsync(world, Body(world.ManifestId, ",\"components\":{\"blob\":\"" + blob + "\"}"));

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.Text);
    }

    [Fact]
    public async Task QaExtra_BodyOver64KiB_413_NoRows()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var blob = new string('x', 70 * 1024);

        var answer = await SendRawAsync(world, Body(world.ManifestId, ",\"components\":{\"blob\":\"" + blob + "\"}"));

        answer.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        await AssertNoRunRowsAsync(world);
    }

    // ------------------------------------------------------------------ scope

    [Fact]
    public async Task QaExtra_ManifestOfSiblingProject_404_NoRows()
    {
        // Same org, different project: the manifest belongs to project A, so a request on project B must be a 404.
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, "sibling");

        var answer = await SendRawAsync(world, Body(world.ManifestId), project: sibling);

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
        await AssertNoRunRowsAsync(world);
    }

    // ------------------------------------------------------------------ exploratory

    [Fact]
    public async Task QaExtra_ConcurrentCreates_EachGetsItsOwnRun()
    {
        // Two creates from the same manifest are two runs, not an error: both return 201 with distinct ids, and each
        // run has its own planned executions. Expected to fail while the array-write defect (D1) is present.
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answers = await Task.WhenAll(
            SendRawAsync(world, Body(world.ManifestId)),
            SendRawAsync(world, Body(world.ManifestId)));

        answers.ShouldAllBe(a => a.Status == HttpStatusCode.Created);
        var ids = answers.Select(a => JsonNode.Parse(a.Text)!["run_id"]!.GetValue<string>()).ToArray();
        ids[0].ShouldNotBe(ids[1]);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.run")).ShouldBe(2);
    }
}
