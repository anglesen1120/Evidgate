using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// A verdict belongs to the frozen revision it was decided from: the API writes an evaluation only as
/// part of the run pipeline and serves no route that starts another one over an existing verdict. The
/// scan reads the real host's endpoint table, so a route added without a contract entry still counts.
/// </summary>
public sealed class ReevaluationRouteAbsenceTests
{
    private const string DeadConnectionString =
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Database=evidgate;Timeout=2";

    [Fact]
    public async Task NoV1ReevaluationRoute()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString);

        var routes = ServedV1Routes(factory.Services);

        // Positive control: the same scan must see a known /v1 write route, so a host that served
        // nothing (or only reads) cannot make the absence below pass.
        routes.ShouldContain(
            route => route.Method == "POST" && route.Template == "/v1/roles",
            "the scan did not read the real route table");
        routes.Count.ShouldBeGreaterThan(40, "a partial route table cannot prove an absence");

        var reevaluation = routes.Where(route => IsReevaluationRoute(route.Method, route.Template)).ToList();
        reevaluation.ShouldBeEmpty(
            "an evaluation is decided once per frozen revision; a route that starts another one was found: "
            + string.Join(", ", reevaluation.Select(route => $"{route.Method} {route.Template}")));
    }

    [Theory]
    [InlineData("/v1/roles", true)]
    [InlineData("/V1/roles", true)]
    [InlineData("/v1", true)]
    [InlineData("/V1", true)]
    [InlineData("/v10/roles", false)]
    [InlineData("/other/roles", false)]
    public void IsV1Template_Casing_MatchesRoutePrefix(string template, bool expected)
    {
        IsV1Template(template).ShouldBe(expected);
    }

    [Theory]
    [InlineData("POST", "/V1/runs/{r}/evaluations", true)]
    [InlineData("POST", "/v1/runs/{r}/rerun-evaluation", true)]
    [InlineData("GET", "/v1/runs/{r}/reevaluate", true)]
    [InlineData("PUT", "/v1/runs/{r}/evaluations", true)]
    [InlineData("GET", "/v1/runs/{r}/decision", false)]
    [InlineData("GET", "/v1/runs/{r}/evidence-revisions", false)]
    [InlineData("POST", "/v1/runs/{r}/custom-evaluation", true)]
    [InlineData("POST", "/v1/runs/{r}/EVALUATIONS", true)]
    [InlineData("PATCH", "/v1/runs/{r}/rerun_evaluation", true)]
    [InlineData("*", "/v1/runs/{r}/evaluations", true)]
    [InlineData("HEAD", "/v1/runs/{r}/re-evaluate", true)]
    [InlineData("OPTIONS", "/v1/runs/{r}/re_evaluate", true)]
    [InlineData("get", "/V1/runs/{r}/RERUN_EVALUATION", true)]
    [InlineData("GET", "/v1/runs/{r}/evaluation-rerun", true)]
    [InlineData("GET", "/v1/runs/{r}/evaluations", false)]
    [InlineData("HEAD", "/v1/runs/{r}/evaluations", false)]
    [InlineData("OPTIONS", "/v1/runs/{r}/evaluations", false)]
    [InlineData("POST", "/v1/runs/{r}/rerun", false)]
    [InlineData("POST", "/v1/runs/{evaluation}", false)]
    [InlineData("POST", "/v1/runs/{reevaluate}", false)]
    [InlineData("POST", "/v10/runs/{r}/evaluations", false)]
    [InlineData("POST", "/other/runs/{r}/evaluations", false)]
    public void IsReevaluationRoute_LiteralAndVerb_ClassifiesForbiddenRoute(string method, string template, bool expected)
    {
        IsReevaluationRoute(method, template).ShouldBe(expected);
    }

    /// <summary>Every <c>/v1</c> route the real host serves, one row per declared method (normalised templates).</summary>
    private static List<(string Method, string Template)> ServedV1Routes(IServiceProvider services)
    {
        var routes = new List<(string Method, string Template)>();
        foreach (var endpoint in services.GetRequiredService<EndpointDataSource>().Endpoints)
        {
            if (endpoint is not RouteEndpoint route)
            {
                continue;
            }

            var template = Normalise(TemplateText(route.RoutePattern));
            if (!IsV1Template(template))
            {
                continue;
            }

            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            if (methods is null || methods.Count == 0)
            {
                // No method metadata means every verb, a write among them.
                routes.Add(("*", template));
                continue;
            }

            routes.AddRange(methods.Select(method => (method.ToUpperInvariant(), template)));
        }

        return routes;
    }

    /// <summary>The pattern's own text; the reconstructed text covers a pattern built without one.</summary>
    private static string TemplateText(RoutePattern pattern)
    {
        var rawText = pattern.RawText;
        return string.IsNullOrWhiteSpace(rawText) ? pattern.ToString() ?? string.Empty : rawText;
    }

    private static string Normalise(string template)
    {
        var trimmed = template.TrimEnd('/');
        if (!trimmed.StartsWith('/'))
        {
            trimmed = "/" + trimmed;
        }

        return trimmed;
    }

    /// <summary>
    /// ASP.NET matches literal segments case-insensitively, so a route declared <c>/V1/...</c> answers the same
    /// requests as <c>/v1/...</c> and must be scanned and classified alike.
    /// </summary>
    internal static bool IsV1Template(string normalisedTemplate) =>
        normalisedTemplate.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase)
        || normalisedTemplate.Equals("/v1", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A v1 route that names an evaluation. A write verb makes any such name forbidden, because the run
    /// pipeline is the only writer; a read verb is refused only for a name that asks for a new decision.
    /// </summary>
    internal static bool IsReevaluationRoute(string method, string template)
    {
        var normalised = Normalise(template);
        if (!IsV1Template(normalised))
        {
            return false;
        }

        var readOnly = IsReadVerb(method);
        return normalised
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => !segment.StartsWith('{') && NamesEvaluation(segment, readOnly));
    }

    private static bool IsReadVerb(string method) =>
        method.Equals("GET", StringComparison.OrdinalIgnoreCase)
        || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
        || method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase);

    private static bool NamesEvaluation(string segment, bool readOnly)
    {
        var word = segment
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        // A compound name such as rerun-evaluation stays one word here, so no prefix test can see it.
        var rerunsEvaluation = word.Contains("evaluat", StringComparison.Ordinal)
            && word.Contains("rerun", StringComparison.Ordinal);

        return readOnly
            ? rerunsEvaluation || word.Contains("reevaluat", StringComparison.Ordinal)
            : word.Contains("evaluat", StringComparison.Ordinal);
    }
}
