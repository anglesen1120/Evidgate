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

        var reevaluation = routes.Where(EvaluatesOnWrite).ToList();
        reevaluation.ShouldBeEmpty(
            "an evaluation is decided once per frozen revision; a write route that evaluates again was found: "
            + string.Join(", ", reevaluation.Select(route => $"{route.Method} {route.Template}")));
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
            if (!template.StartsWith("/v1/", StringComparison.Ordinal) && template != "/v1")
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

    /// <summary>A route that both triggers evaluation by verb and names an evaluation in its path.</summary>
    private static bool EvaluatesOnWrite((string Method, string Template) route)
    {
        if (route.Method is "GET" or "HEAD" or "OPTIONS")
        {
            return false;
        }

        return route.Template
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => !segment.StartsWith('{') && NamesEvaluation(segment));
    }

    private static bool NamesEvaluation(string segment)
    {
        var word = segment.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return word.StartsWith("evaluat", StringComparison.Ordinal) || word.Contains("reevaluat", StringComparison.Ordinal);
    }
}
