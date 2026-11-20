using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The refusal of a create-run <c>ref</c> block keeps at most <see cref="ContractValidationResult.MaxDetails"/> member
/// rows plus one terminal <c>truncated</c> marker, whatever the body carries up to the route cap.
/// </summary>
public sealed class RefDetailCapParseTests
{
    private const int Cap = ContractValidationResult.MaxDetails;

    [Fact]
    public void ParseBodyRef_FewErrors_AreAllReportedWithoutMarker()
    {
        var (block, errors) = RunIdentityRules.ParseBodyRef(Body(RefWith("\"kind\":1,\"zzz\":1,\"is_fork\":\"x\"")));

        block.ShouldBeNull();
        errors.Select(e => (e.Path, e.Reason)).ShouldBe(
            [("/ref/kind", "invalid_value"), ("/ref/zzz", "additional_properties"), ("/ref/is_fork", "invalid_value")]);
    }

    [Fact]
    public void ParseBodyRef_ExactlyTheCap_HasNoMarker_OneMoreAddsIt()
    {
        var exact = RunIdentityRules.ParseBodyRef(Body(RefWith(Unknowns(Cap)))).Errors;
        exact.Count.ShouldBe(Cap);
        exact.ShouldNotContain(e => e.Reason == ContractValidationError.TruncatedReason);

        var over = RunIdentityRules.ParseBodyRef(Body(RefWith(Unknowns(Cap + 1)))).Errors;
        over.Count.ShouldBe(Cap + 1);
        over[^1].Path.ShouldBe(string.Empty);
        over[^1].Reason.ShouldBe(ContractValidationError.TruncatedReason);
        over.Take(Cap).ShouldAllBe(e => e.Reason == "additional_properties");
    }

    [Fact]
    public void ParseBodyRef_UnknownAndDuplicateKnownMembers_ShareOneCounter()
    {
        var members = Unknowns(60) + "," + string.Join(",", Enumerable.Repeat("\"kind\":1", 60));

        var errors = RunIdentityRules.ParseBodyRef(Body(RefWith(members))).Errors;

        errors.Count.ShouldBe(Cap + 1);
        errors.Count(e => e.Reason == "additional_properties").ShouldBe(60);
        errors.Count(e => e.Path == "/ref/kind").ShouldBe(40);
        errors[^1].Reason.ShouldBe(ContractValidationError.TruncatedReason);
    }

    [Fact]
    public void ParseBodyRef_SameBodyTwice_GivesTheSameDetails()
    {
        var body = Body(RefWith(Unknowns(500)));

        var first = RunIdentityRules.ParseBodyRef(body).Errors;
        var second = RunIdentityRules.ParseBodyRef(body).Errors;

        second.ShouldBe(first);
    }

    internal static string Unknowns(int count) =>
        string.Join(",", Enumerable.Range(0, count).Select(i => $"\"m{i}\":1"));

    internal static string RefWith(string members) => "{\"ref\":{" + members + "}}";

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

/// <summary>The same bound through the create-run route, with a body at the route's 64 KiB cap.</summary>
[Trait("Category", "Docker")]
public sealed class RefDetailCapRouteTests(
    PostgresTenancyFixture fixture,
    TenancyHostPool hosts) : IClassFixture<TenancyHostPool>
{
    /// <summary>101 rows of at most about 120 bytes plus the envelope stay far below this.</summary>
    private const int AnswerBound = 16 * 1024;

    private const int Cap = ContractValidationResult.MaxDetails;

    [Fact]
    public async Task CreateRun_UnknownRefMemberFlood_AnswersTheCapPlusMarker_Deterministically()
    {
        await using var world = await RunCreationTests.StartAsync(fixture, cases: 1, hosts: hosts);
        var body = Flood(world, i => $"\"m{i}\":1");

        var first = await PostAsync(world.Client, RunRoute(world), body, world.Admin);
        var second = await PostAsync(world.Client, RunRoute(world), body, world.Admin);

        var details = AssertCapped(first);
        details.Take(Cap).ShouldAllBe(d => d.Reason == "additional_properties" && d.Path.StartsWith("/ref/m", StringComparison.Ordinal));
        details[0].Path.ShouldBe("/ref/m0");
        DetailsText(second).ShouldBe(DetailsText(first));

        var control = await PostAsync(world.Client, RunRoute(world), RunCreationTests.RunBody(world.ManifestId), world.Admin);
        control.Status.ShouldBe(HttpStatusCode.Created, control.ErrorMessage);
    }

    [Fact]
    public async Task CreateRun_DuplicateKnownMemberFlood_AnswersTheCapPlusMarker_Deterministically()
    {
        await using var world = await RunCreationTests.StartAsync(fixture, cases: 1, hosts: hosts);
        var body = Flood(world, _ => "\"kind\":1");

        var first = await PostAsync(world.Client, RunRoute(world), body, world.Admin);
        var second = await PostAsync(world.Client, RunRoute(world), body, world.Admin);

        var details = AssertCapped(first);
        details.Take(Cap).ShouldAllBe(d => d.Path == "/ref/kind" && d.Reason == "invalid_value");
        DetailsText(second).ShouldBe(DetailsText(first));
    }

    [Fact]
    public async Task CreateRun_ErrorBeforeTheFlood_IsReported_ErrorAfterIt_IsBehindTheMarker()
    {
        await using var world = await RunCreationTests.StartAsync(fixture, cases: 1, hosts: hosts);

        var before = await PostAsync(
            world.Client, RunRoute(world), Flood(world, i => $"\"m{i}\":1", prefix: "\"is_fork\":\"x\","), world.Admin);
        var beforeDetails = AssertCapped(before);
        beforeDetails[0].Path.ShouldBe("/ref/is_fork");

        var after = await PostAsync(
            world.Client, RunRoute(world), Flood(world, i => $"\"m{i}\":1", suffix: ",\"is_fork\":\"x\""), world.Admin);
        AssertCapped(after).ShouldNotContain(d => d.Path == "/ref/is_fork");
    }

    [Fact]
    public async Task CreateRun_FewRefErrors_AreAnsweredOneRowEach()
    {
        await using var world = await RunCreationTests.StartAsync(fixture, cases: 1, hosts: hosts);
        var body = $"{{\"manifest_id\":\"{world.ManifestId}\",\"ref\":{{\"kind\":1,\"zzz\":1}}}}";

        var answer = await PostAsync(world.Client, RunRoute(world), body, world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        Details(answer).Select(d => (d.Path, d.Reason)).ShouldBe(
            [("/ref/kind", "invalid_value"), ("/ref/zzz", "additional_properties")]);
    }

    /// <summary>A create-run body filled with ref members up to just under the 64 KiB route cap.</summary>
    private static string Flood(
        RunCreationTests.World world,
        Func<int, string> member,
        string prefix = "",
        string suffix = "")
    {
        var head = $"{{\"manifest_id\":\"{world.ManifestId}\",\"ref\":{{" + prefix;
        var tail = suffix + "}}";
        var builder = new StringBuilder(head);
        var limit = Evidgate.Modules.Orchestrator.Endpoints.OrchestratorEndpoints.MaxRunBytes - tail.Length - 32;
        for (var i = 0; builder.Length < limit; i++)
        {
            builder.Append(member(i)).Append(',');
        }

        builder.Length--;
        return builder.Append(tail).ToString();
    }

    private static List<(string Path, string Reason)> AssertCapped(ApiAnswer answer)
    {
        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        answer.Body.GetRawText().Length.ShouldBeLessThan(AnswerBound);

        var details = Details(answer);
        details.Count.ShouldBe(Cap + 1);
        details[^1].ShouldBe((string.Empty, ContractValidationError.TruncatedReason));
        return details;
    }

    private static List<(string Path, string Reason)> Details(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(d => (d.GetProperty("path").GetString()!, d.GetProperty("reason").GetString()!))];

    private static string DetailsText(ApiAnswer answer) =>
        answer.Body.GetProperty("error").GetProperty("details").GetRawText();

    private static string RunRoute(RunCreationTests.World world) => $"/v1/projects/{world.Project}/runs";

    private static async Task<ApiAnswer> PostAsync(
        HttpClient client,
        string route,
        string body,
        Action<HttpRequestHeaders> caller)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route);
        caller(request.Headers);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", NewKey());
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }
}
