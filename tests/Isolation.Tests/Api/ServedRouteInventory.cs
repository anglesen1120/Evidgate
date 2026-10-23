using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Isolation.Tests.Api;

/// <summary>
/// Every <c>/v1</c> route a running host serves, as <see cref="ApiRoute"/>s
/// (02.9.1 AC2): the served side of the route-coverage gate.
/// </summary>
/// <remarks>
/// Read from the host's <see cref="EndpointDataSource"/>s rather than the served
/// OpenAPI document, so routes excluded from the description still count. Each
/// <see cref="IHttpMethodMetadata"/> method becomes one route; an endpoint without
/// one matches every method and is reported as <see cref="AnyMethod"/>.
/// </remarks>
public static class ServedRouteInventory
{
    /// <summary>The method reported for an endpoint that declares no HTTP method.</summary>
    public const string AnyMethod = "*";

    /// <summary>The <c>/v1</c> routes of every endpoint data source registered in <paramref name="services"/>.</summary>
    public static IReadOnlyList<ApiRoute> From(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return From(services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints));
    }

    /// <summary>The <c>/v1</c> routes of <paramref name="endpoints"/>, distinct and ordered by template, then method.</summary>
    public static IReadOnlyList<ApiRoute> From(IEnumerable<Endpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var routes = new HashSet<ApiRoute>();
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var template = ApiRoute.NormaliseTemplate(PatternText(endpoint.RoutePattern));
            if (!ApiRoute.IsVersioned(template))
            {
                continue;
            }

            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            if (methods is null || methods.Count == 0)
            {
                routes.Add(new ApiRoute(AnyMethod, template));
                continue;
            }

            foreach (var method in methods)
            {
                routes.Add(new ApiRoute(method, template));
            }
        }

        var ordered = routes.ToList();
        ordered.Sort(ApiRoute.Order);
        return ordered;
    }

    /// <summary>The pattern's raw text, or the text rebuilt from its segments when the pattern was built without one.</summary>
    private static string PatternText(RoutePattern pattern)
    {
        if (!string.IsNullOrWhiteSpace(pattern.RawText))
        {
            return pattern.RawText;
        }

        var text = new StringBuilder();
        foreach (var segment in pattern.PathSegments)
        {
            text.Append('/');
            foreach (var part in segment.Parts)
            {
                switch (part)
                {
                    case RoutePatternLiteralPart literal:
                        text.Append(literal.Content);
                        break;
                    case RoutePatternSeparatorPart separator:
                        text.Append(separator.Content);
                        break;
                    case RoutePatternParameterPart parameter:
                        text.Append('{').Append(parameter.IsCatchAll ? "*" : string.Empty).Append(parameter.Name).Append('}');
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown route pattern part '{part.GetType().Name}'.");
                }
            }
        }

        return text.Length == 0 ? "/" : text.ToString();
    }
}
