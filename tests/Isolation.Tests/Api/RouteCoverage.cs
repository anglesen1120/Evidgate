using System.Text;

namespace Isolation.Tests.Api;

/// <summary>
/// One <c>/v1</c> operation (02.9.1 AC2): an upper-case HTTP method and a route
/// template. Equality is ordinal on both parts. Route constraints, defaults and the
/// optional marker are stripped (<c>{id:guid}</c> → <c>{id}</c>), so a spec path
/// and a served <c>RoutePattern</c> compare equal.
/// </summary>
/// <remarks>Method <c>*</c> stands for a served endpoint that declares no HTTP method.</remarks>
public sealed record ApiRoute(string Method, string Template)
{
    /// <summary>The version prefix every isolation route carries.</summary>
    public const string VersionPrefix = "/v1";

    private static readonly char[] ParameterNameEnd = [':', '=', '?'];

    /// <summary>Upper-case HTTP method, or <c>*</c>.</summary>
    public string Method { get; } = NormaliseMethod(Method);

    /// <summary>The normalised template, always starting with <c>/v1</c>.</summary>
    public string Template { get; } = NormaliseVersionedTemplate(Template);

    /// <summary>Orders routes by template, then method (ordinal), for stable messages.</summary>
    public static IComparer<ApiRoute> Order { get; } = Comparer<ApiRoute>.Create(static (left, right) =>
    {
        var byTemplate = string.CompareOrdinal(left.Template, right.Template);
        return byTemplate != 0 ? byTemplate : string.CompareOrdinal(left.Method, right.Method);
    });

    /// <summary>"GET /v1/orgs/{o}".</summary>
    public override string ToString() => Method + " " + Template;

    /// <summary>True for <c>/v1</c> and <c>/v1/…</c> (never <c>/v10</c>).</summary>
    public static bool IsVersioned(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return string.Equals(template, VersionPrefix, StringComparison.Ordinal)
            || template.StartsWith(VersionPrefix + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Leading slash, no doubled or trailing slash, and every <c>{…}</c> parameter reduced
    /// to its name (constraints after <c>:</c>, defaults after <c>=</c> and the optional
    /// <c>?</c> are dropped; a catch-all <c>*</c> is kept).
    /// </summary>
    /// <exception cref="ArgumentException">The template has unbalanced braces or an empty parameter.</exception>
    public static string NormaliseTemplate(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        var text = template.Trim();
        var result = new StringBuilder(text.Length + 1);
        result.Append('/');

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{')
            {
                var close = MatchingBrace(text, i, template);
                var content = text[(i + 1)..close];
                var nameEnd = content.IndexOfAny(ParameterNameEnd);
                var name = (nameEnd < 0 ? content : content[..nameEnd]).Trim();
                if (name.TrimStart('*').Length == 0)
                {
                    throw new ArgumentException($"Route template '{template}' has an empty parameter.", nameof(template));
                }

                result.Append('{').Append(name).Append('}');
                i = close;
            }
            else if (c == '}')
            {
                throw new ArgumentException($"Route template '{template}' has an unmatched '}}'.", nameof(template));
            }
            else if (c != '/' || result[^1] != '/')
            {
                result.Append(c);
            }
        }

        if (result.Length > 1 && result[^1] == '/')
        {
            result.Length--;
        }

        return result.ToString();
    }

    private static int MatchingBrace(string text, int open, string template)
    {
        // Depth-counted so a regex constraint such as {code:regex(^a{{2}}$)} stays inside its parameter.
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}' && --depth == 0)
            {
                return i;
            }
        }

        throw new ArgumentException($"Route template '{template}' has an unmatched '{{'.", nameof(template));
    }

    private static string NormaliseMethod(string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        return method.Trim().ToUpperInvariant();
    }

    private static string NormaliseVersionedTemplate(string template)
    {
        var normalised = NormaliseTemplate(template);
        return IsVersioned(normalised)
            ? normalised
            : throw new ArgumentException($"Route template '{template}' does not start with '{VersionPrefix}'.", nameof(template));
    }
}

/// <summary>
/// The 02.9.1 AC2 gate: every served <c>/v1</c> route has an isolation case, every
/// isolation case names a served route, and every served route is in
/// <c>contracts/openapi.yaml</c> or on an explicit allowlist with a reason.
/// </summary>
public static class RouteCoverage
{
    /// <summary>Compares the four route sets.</summary>
    /// <param name="spec">Operations in <c>contracts/openapi.yaml</c> (<see cref="OpenApiRouteInventory.Load()"/>).</param>
    /// <param name="served">Routes the running host serves (<see cref="ServedRouteInventory.From(IServiceProvider)"/>).</param>
    /// <param name="covered">Routes the isolation suite has a case for.</param>
    /// <param name="servedNotInSpecAllowlist">Served routes deliberately absent from the spec, each with its reason.</param>
    /// <exception cref="ArgumentException">An allowlist entry has a blank reason.</exception>
    public static RouteCoverageResult Check(
        IReadOnlyCollection<ApiRoute> spec,
        IReadOnlyCollection<ApiRoute> served,
        IReadOnlyCollection<ApiRoute> covered,
        IReadOnlyDictionary<ApiRoute, string> servedNotInSpecAllowlist)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(served);
        ArgumentNullException.ThrowIfNull(covered);
        ArgumentNullException.ThrowIfNull(servedNotInSpecAllowlist);

        foreach (var (route, reason) in servedNotInSpecAllowlist)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException($"Allowlisted route '{route}' needs a reason.", nameof(servedNotInSpecAllowlist));
            }
        }

        var specSet = spec.ToHashSet();
        var servedSet = served.ToHashSet();
        var coveredSet = covered.ToHashSet();

        return new RouteCoverageResult(
            uncovered: Sorted(servedSet.Where(route => !coveredSet.Contains(route))),
            stale: Sorted(coveredSet.Where(route => !servedSet.Contains(route))),
            unspecified: Sorted(servedSet.Where(route => !specSet.Contains(route) && !servedNotInSpecAllowlist.ContainsKey(route))),
            pending: Sorted(specSet.Where(route => !servedSet.Contains(route))),
            specCount: specSet.Count,
            servedCount: servedSet.Count,
            coveredCount: coveredSet.Count);
    }

    private static List<ApiRoute> Sorted(IEnumerable<ApiRoute> routes)
    {
        var list = routes.ToList();
        list.Sort(ApiRoute.Order);
        return list;
    }
}

/// <summary>The outcome of <see cref="RouteCoverage.Check"/>.</summary>
public sealed class RouteCoverageResult
{
    internal RouteCoverageResult(
        IReadOnlyList<ApiRoute> uncovered,
        IReadOnlyList<ApiRoute> stale,
        IReadOnlyList<ApiRoute> unspecified,
        IReadOnlyList<ApiRoute> pending,
        int specCount,
        int servedCount,
        int coveredCount)
    {
        Uncovered = uncovered;
        Stale = stale;
        Unspecified = unspecified;
        Pending = pending;
        SpecCount = specCount;
        ServedCount = servedCount;
        CoveredCount = coveredCount;
    }

    /// <summary>Served routes with no isolation case.</summary>
    public IReadOnlyList<ApiRoute> Uncovered { get; }

    /// <summary>Isolation cases whose route is not served.</summary>
    public IReadOnlyList<ApiRoute> Stale { get; }

    /// <summary>Served routes absent from the spec and not allowlisted.</summary>
    public IReadOnlyList<ApiRoute> Unspecified { get; }

    /// <summary>Spec routes not served yet (informational; never a violation).</summary>
    public IReadOnlyList<ApiRoute> Pending { get; }

    /// <summary>Distinct spec routes compared.</summary>
    public int SpecCount { get; }

    /// <summary>Distinct served routes compared.</summary>
    public int ServedCount { get; }

    /// <summary>Distinct covered routes compared.</summary>
    public int CoveredCount { get; }

    /// <summary>No uncovered, stale or unspecified route.</summary>
    public bool IsClean => Uncovered.Count == 0 && Stale.Count == 0 && Unspecified.Count == 0;

    /// <summary>A multi-line message naming every violating route with its category and fix.</summary>
    public string Describe()
    {
        var violations = Uncovered.Count + Stale.Count + Unspecified.Count;
        var text = new StringBuilder();
        text.Append(violations == 0 ? "route coverage clean" : "route coverage: " + Count(violations) + " violation(s)")
            .Append(" (served ").Append(Count(ServedCount))
            .Append(", covered ").Append(Count(CoveredCount))
            .Append(", spec ").Append(Count(SpecCount))
            .Append(", pending ").Append(Count(Pending.Count))
            .Append(')');

        foreach (var route in Uncovered)
        {
            text.AppendLine().Append("uncovered: ").Append(route.ToString())
                .Append(" — add an isolation case in IsolationRouteCases");
        }

        foreach (var route in Stale)
        {
            text.AppendLine().Append("stale: ").Append(route.ToString())
                .Append(" — the route is not served; remove its case from IsolationRouteCases");
        }

        foreach (var route in Unspecified)
        {
            text.AppendLine().Append("unspecified: ").Append(route.ToString())
                .Append(" — served but absent from contracts/openapi.yaml; add it to the spec or allowlist it with a reason");
        }

        return text.ToString();
    }

    private static string Count(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
