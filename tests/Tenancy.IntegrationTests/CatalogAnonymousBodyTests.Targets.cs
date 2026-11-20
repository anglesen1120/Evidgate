using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;
using World = Tenancy.IntegrationTests.RunCreationTests.World;

namespace Tenancy.IntegrationTests;

public sealed partial class CatalogAnonymousBodyTests
{
    /// <summary>A fixed request id, so two refusals of one route differ in nothing but what the test compares.</summary>
    private const string FixedRequestId = "catalog-body-order";

    /// <summary>One raw-body route of the catalog, with the payload an authorized caller can send it.</summary>
    private sealed record Target(
        HttpMethod Method,
        string Route,
        string ValidBody,
        int Cap,
        string Permission,
        string? IfMatch = null);

    private sealed record BodyAnswer(ApiAnswer Answer, string Text, HttpResponseHeaders Headers);

    private async Task<Target> TargetAsync(World world, string route)
    {
        if (route == "fixture-revision")
        {
            var fixtureId = await FixtureIdAsync(world);

            return new Target(
                HttpMethod.Post,
                $"/v1/fixtures/{fixtureId}/revisions",
                """{"seed_version":"v2","body_ref":"fixtures/orders/seed.sql"}""",
                64 * 1024,
                "fixture:write");
        }

        var caseId = await CaseIdAsync(world);
        var suiteId = await SuiteIdAsync(world);

        return route switch
        {
            "suite-revision" => new Target(
                HttpMethod.Post,
                $"/v1/suites/{suiteId}/revisions",
                $$$"""{"members":[{"case_revision_id":"{{{world.Cases[0]}}}"}]}""",
                4 * 1024 * 1024,
                "suite:write"),
            "requirement-update" => new Target(
                HttpMethod.Patch,
                $"/v1/projects/{world.Project}/cases/{caseId}",
                """{"requirement_ref":"REQ-1042"}""",
                16 * 1024,
                "case:write"),
            "case-update" => new Target(
                HttpMethod.Patch,
                $"/v1/cases/{caseId}",
                """{"name":"renamed-anonymous"}""",
                16 * 1024,
                "case:write",
                await CaseETagAsync(world, caseId)),
            "fixture-create" => FixtureCreateTarget(world),
            "git-binding" => new Target(
                HttpMethod.Post,
                $"/v1/projects/{world.Project}/git-repo-bindings",
                """{"installation_id":1,"repository_id":5001}""",
                4 * 1024,
                "project:write"),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "unknown catalog write route"),
        };
    }

    /// <summary>
    /// A fixture of the world's project, so the revision route names a resource another tenant must not reach — instead
    /// of an id no tenant holds, which would prove only that an unknown id is 404.
    /// </summary>
    private static async Task<Guid> FixtureIdAsync(World world)
    {
        var create = FixtureCreateTarget(world);
        var answer = await SendBodyAsync(world, create, create.ValidBody, world.Admin);
        answer.Answer.Status.ShouldBe(HttpStatusCode.Created, answer.Text);

        return answer.Answer.Id("fixture_id");
    }

    private static Target FixtureCreateTarget(World world) =>
        new(
            HttpMethod.Post,
            $"/v1/projects/{world.Project}/fixtures",
            """{"fixture_key":"orders-anonymous","seed_version":"v1","body_ref":"fixtures/orders/seed.sql"}""",
            64 * 1024,
            "fixture:write");

    private static async Task<BodyAnswer> SendBodyAsync(
        World world,
        Target target,
        string body,
        Action<HttpRequestHeaders>? caller = null,
        HttpClient? client = null,
        Action<HttpRequestHeaders>? extraHeaders = null)
    {
        using var request = new HttpRequestMessage(target.Method, target.Route);
        (caller ?? world.Admin)(request.Headers);
        extraHeaders?.Invoke(request.Headers);
        request.Headers.Add("Idempotency-Key", NewKey());
        request.Headers.Add("X-Request-Id", FixedRequestId);
        if (target.IfMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", target.IfMatch);
        }

        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await (client ?? world.Client).SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new BodyAnswer(
            new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers),
            text,
            response.Headers);
    }

    private Task<Guid> SuiteIdAsync(World world) =>
        fixture.ScalarAsync<Guid>(world.Database,
            $"select suite_id from public.suite_revision where org_id = '{world.Org}' and id = '{world.SuiteRevision}'");

    private Task<Guid> CaseIdAsync(World world) =>
        fixture.ScalarAsync<Guid>(world.Database,
            $"select case_id from public.test_revision where org_id = '{world.Org}' and id = '{world.Cases[0]}'");

    private static async Task<string> CaseETagAsync(World world, Guid caseId)
    {
        var answer = await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{caseId}", world.Admin);
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        return answer.ETag ?? throw new InvalidOperationException("the case read carried no ETag");
    }

}
