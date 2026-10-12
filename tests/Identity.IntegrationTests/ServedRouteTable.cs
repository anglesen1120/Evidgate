using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Identity.IntegrationTests;

/// <summary>
/// One <c>/v1</c> route the real host serves, with the permission its OpenAPI operation
/// declares (02.2.1-T3, AC3/AC4): an upper-case HTTP method, a normalised route template
/// (<c>{x:guid}</c> → <c>{x}</c>) and the spec's <c>x-permission</c> value — a catalogue
/// code, <c>none</c> or <c>authenticated</c>.
/// </summary>
/// <remarks>
/// Equality is ordinal on all three parts, so the served set and the classified set
/// compare as sets. The template is normalised in the constructor, so a hand-built
/// instance (a test naming one route) compares equal to the served one.
/// </remarks>
internal sealed record ServedRoute
{
    /// <summary>Builds a route, normalising the method and the template.</summary>
    public ServedRoute(string Method, string Template, string Permission)
    {
        this.Method = Method.ToUpperInvariant();
        this.Template = ServedRouteTable.NormaliseTemplate(Template);
        this.Permission = Permission;
    }

    /// <summary>Upper-case HTTP method.</summary>
    public string Method { get; }

    /// <summary>The normalised template, always starting with <c>/v1</c>.</summary>
    public string Template { get; }

    /// <summary>The spec's <c>x-permission</c> for this method + path.</summary>
    public string Permission { get; }

    /// <summary>"POST /v1/orgs/{o}".</summary>
    public override string ToString() => Method + " " + Template;
}

/// <summary>
/// Every <c>/v1</c> route the real host serves, read from its
/// <see cref="EndpointDataSource"/>s (never a hand list), each paired with the
/// <c>x-permission</c> its operation declares in <c>contracts/openapi.yaml</c>
/// (02.2.1-T3, AC3/AC4).
/// </summary>
/// <remarks>
/// <para>
/// The served side is the same source <c>tests/Isolation.Tests/Api/ServedRouteInventory</c>
/// reads: one entry per <see cref="IHttpMethodMetadata"/> method of every
/// <see cref="RouteEndpoint"/> under <c>/v1</c>. The spec side is read by indentation,
/// the approach <c>PolicyPermissionContractTests</c> and <c>OpenApiRouteInventory</c>
/// use (path keys at indent 2, methods at 4, an operation's fields at 6), with
/// <c>servers[0].url</c> (<c>/v1</c>) prefixed to each path.
/// </para>
/// <para>
/// A served route the spec does not declare throws, naming it — the classification
/// gate. The one recorded exception is the 01.1.2 <c>/v1/slice</c> dev surface, which
/// is served but deliberately absent from the spec (the same allowlist
/// <c>tests/Isolation.Tests/Api/CoverageTests.cs</c> carries); it is classified
/// <c>none</c>.
/// </para>
/// </remarks>
internal static class ServedRouteTable
{
    /// <summary>The version prefix every route carries.</summary>
    internal const string VersionPrefix = "/v1";

    /// <summary>The <c>x-permission</c> of an operation that needs no permission.</summary>
    internal const string NoPermission = "none";

    /// <summary>The <c>x-permission</c> of an operation that needs only an authenticated caller.</summary>
    internal const string Authenticated = "authenticated";

    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        "get", "put", "post", "patch", "delete", "head", "options", "trace",
    };

    /// <summary>
    /// Served routes deliberately absent from the spec, each with the recorded reason the
    /// classification cites. Mirrors <c>tests/Isolation.Tests/Api/CoverageTests.cs:17-21</c>.
    /// </summary>
    internal static readonly Dictionary<(string Method, string Template), string> ServedNotInSpec =
        new()
        {
            [("POST", "/v1/slice/run")] =
                "01.1.2 run-only slice dev surface, recorded in tests/Contract.Tests/ServedDocumentTests.cs:59",
        };

    /// <summary>The <c>/v1</c> routes of every endpoint data source registered in <paramref name="services"/>.</summary>
    /// <exception cref="InvalidOperationException">A served route is absent from the spec and not allowlisted.</exception>
    public static IReadOnlyList<ServedRoute> From(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var spec = SpecPermissions();
        var routes = new HashSet<ServedRoute>();

        foreach (var endpoint in services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints))
        {
            if (endpoint is not RouteEndpoint route)
            {
                continue;
            }

            var template = NormaliseTemplate(PatternText(route.RoutePattern));
            if (!IsVersioned(template))
            {
                continue;
            }

            var methods = route.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            if (methods is null || methods.Count == 0)
            {
                continue;
            }

            foreach (var method in methods)
            {
                var upper = method.ToUpperInvariant();
                if (!spec.TryGetValue((upper, template), out var permission))
                {
                    if (!ServedNotInSpec.TryGetValue((upper, template), out var reason))
                    {
                        throw new InvalidOperationException(
                            $"served route {upper} {template} is absent from contracts/openapi.yaml; "
                            + "classify it in the spec or record it in ServedRouteTable.ServedNotInSpec");
                    }

                    // The recorded reason is the classification's justification, not a
                    // throwaway: the exclusion-list assertions cite it, so an empty one is a
                    // broken classification and fails here rather than passing silently.
                    if (string.IsNullOrWhiteSpace(reason))
                    {
                        throw new InvalidOperationException(
                            $"served route {upper} {template} is allowlisted in ServedRouteTable.ServedNotInSpec "
                            + "without a reason");
                    }

                    permission = NoPermission;
                }

                routes.Add(new ServedRoute(upper, template, permission));
            }
        }

        var ordered = routes.ToList();
        ordered.Sort(static (left, right) =>
        {
            var byTemplate = string.CompareOrdinal(left.Template, right.Template);
            return byTemplate != 0 ? byTemplate : string.CompareOrdinal(left.Method, right.Method);
        });
        return ordered;
    }

    /// <summary>True for a method that mutates state (the viewer deny matrix's scope).</summary>
    internal static bool IsMutating(string method) =>
        method is "POST" or "PUT" or "PATCH" or "DELETE";

    /// <summary>
    /// True when a route carries no catalogue permission a caller could be denied for: the
    /// spec's <c>none</c> or <c>authenticated</c>, or a served route recorded in
    /// <see cref="ServedNotInSpec"/> (classified <c>none</c>). The viewer and ci_producer
    /// exclusion sets are derived from this, never from a hand list, so a route that
    /// changes its <c>x-permission</c> moves in or out of the matrix by itself.
    /// </summary>
    internal static bool IsUnpermissioned(ServedRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        return route.Permission is NoPermission or Authenticated
            || ServedNotInSpec.ContainsKey((route.Method, route.Template));
    }

    /// <summary>
    /// The recorded reason <paramref name="route"/> is absent from the spec, or null when
    /// the spec declares it. The exclusion-list assertions cite it, so a classification
    /// cannot be recorded without a justification.
    /// </summary>
    internal static string? NotInSpecReason(ServedRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        return ServedNotInSpec.TryGetValue((route.Method, route.Template), out var reason) ? reason : null;
    }

    /// <summary>True for <c>/v1</c> and <c>/v1/…</c> (never <c>/v10</c>).</summary>
    internal static bool IsVersioned(string template) =>
        string.Equals(template, VersionPrefix, StringComparison.Ordinal)
        || template.StartsWith(VersionPrefix + "/", StringComparison.Ordinal);

    /// <summary>
    /// Leading slash, no doubled or trailing slash, and every <c>{…}</c> parameter reduced
    /// to its name (constraints after <c>:</c>, defaults after <c>=</c> and the optional
    /// <c>?</c> are dropped).
    /// </summary>
    internal static string NormaliseTemplate(string template)
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
                var close = text.IndexOf('}', i);
                if (close < 0)
                {
                    throw new ArgumentException($"unbalanced '{{' in route template '{template}'", nameof(template));
                }

                var content = text[(i + 1)..close];
                var nameEnd = content.IndexOfAny([':', '=', '?']);
                var name = nameEnd < 0 ? content : content[..nameEnd];
                if (name.Length == 0)
                {
                    throw new ArgumentException($"empty parameter in route template '{template}'", nameof(template));
                }

                result.Append('{').Append(name).Append('}');
                i = close;
            }
            else if (c == '/')
            {
                if (result[^1] != '/')
                {
                    result.Append('/');
                }
            }
            else
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

    /// <summary>The pattern's raw text, or the text rebuilt from its segments.</summary>
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
                        throw new InvalidOperationException($"unknown route pattern part '{part.GetType().Name}'");
                }
            }
        }

        return text.Length == 0 ? "/" : text.ToString();
    }

    /// <summary>Every operation of <c>contracts/openapi.yaml</c>, keyed by (upper-case method, normalised template).</summary>
    private static Dictionary<(string Method, string Template), string> SpecPermissions()
    {
        var path = Path.Combine(RepoRoot(), "contracts", "openapi.yaml");
        var lines = File.ReadAllLines(path);
        var prefix = ServerPrefix(lines, path);
        var permissions = new Dictionary<(string, string), string>(EqualityComparer<(string, string)>.Default);

        var pathsLine = TopLevelKey(lines, "paths")
            ?? throw new InvalidOperationException($"{path}: no top-level 'paths:' block");
        var end = SectionEnd(lines, pathsLine + 1);

        string? currentPath = null;
        string? currentMethod = null;
        for (var i = pathsLine + 1; i < end; i++)
        {
            var line = lines[i];
            if (IsBlank(line))
            {
                continue;
            }

            var indent = IndentOf(line);
            if (indent > 6)
            {
                // Inside an operation, a path-level parameter list or a block scalar body.
                continue;
            }

            if (indent == 2)
            {
                currentPath = TryKey(line, 2, out var key) && key.StartsWith('/') ? key : null;
                currentMethod = null;
                continue;
            }

            if (indent == 4)
            {
                currentMethod = TryKey(line, 4, out var key) && Methods.Contains(key) ? key : null;
                continue;
            }

            if (indent == 6 && currentPath is not null && currentMethod is not null
                && TryKey(line, 6, out var field) && string.Equals(field, "x-permission", StringComparison.Ordinal))
            {
                var value = InlineValue(line);
                if (value.Length > 0)
                {
                    permissions[(currentMethod.ToUpperInvariant(), NormaliseTemplate(prefix + currentPath))] = value;
                }
            }
        }

        return permissions;
    }

    /// <summary>The single <c>servers[0].url</c>; spec paths are relative to it.</summary>
    private static string ServerPrefix(string[] lines, string source)
    {
        var serversLine = TopLevelKey(lines, "servers")
            ?? throw new InvalidOperationException($"{source}: no top-level 'servers:' block");
        var end = SectionEnd(lines, serversLine + 1);

        for (var i = serversLine + 1; i < end; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                trimmed = trimmed[2..].TrimStart();
            }

            if (!trimmed.StartsWith("url:", StringComparison.Ordinal))
            {
                continue;
            }

            var url = trimmed["url:".Length..].Trim().Trim('\'', '"');
            if (url.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            {
                url = absolute.AbsolutePath;
            }

            var prefix = NormaliseTemplate(url);
            return string.Equals(prefix, VersionPrefix, StringComparison.Ordinal)
                ? prefix
                : throw new InvalidOperationException($"{source}:{i + 1}: servers[0].url is '{url}'; expected '{VersionPrefix}'");
        }

        throw new InvalidOperationException($"{source}: 'servers:' has no url");
    }

    private static int? TopLevelKey(string[] lines, string name)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (TryKey(lines[i], 0, out var key) && string.Equals(key, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>Index one past the last line of a top-level section (the next non-blank line at indent 0).</summary>
    private static int SectionEnd(string[] lines, int start)
    {
        for (var i = start; i < lines.Length; i++)
        {
            if (!IsBlank(lines[i]) && IndentOf(lines[i]) == 0)
            {
                return i;
            }
        }

        return lines.Length;
    }

    /// <summary>Reads the key of a mapping line at exactly <paramref name="indent"/>.</summary>
    private static bool TryKey(string line, int indent, out string key)
    {
        key = string.Empty;
        var trimmed = line.TrimStart(' ');
        if (trimmed.Length == 0 || trimmed[0] is '#' or '-' || IndentOf(line) != indent)
        {
            return false;
        }

        var colon = line.IndexOf(':', indent);
        if (colon < 0 || (colon + 1 < line.Length && line[colon + 1] != ' '))
        {
            return false;
        }

        key = line[indent..colon].Trim();
        if (key.Length >= 2 && key[0] is '\'' or '"' && key[^1] == key[0])
        {
            key = key[1..^1];
        }

        return key.Length > 0;
    }

    /// <summary>The text after a key's colon, without a trailing comment.</summary>
    private static string InlineValue(string line)
    {
        var colon = line.IndexOf(':', IndentOf(line));
        var value = colon < 0 ? string.Empty : line[(colon + 1)..].Trim();
        return value.StartsWith('#') ? string.Empty : value;
    }

    private static int IndentOf(string line)
    {
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ')
        {
            indent++;
        }

        return indent;
    }

    private static bool IsBlank(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length == 0 || trimmed[0] == '#';
    }

    /// <summary>The directory holding <c>Evidgate.slnx</c>, found by walking up from the test output directory.</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Evidgate.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Evidgate.slnx not found above '{AppContext.BaseDirectory}'");
    }
}

/// <summary>One request a probe sends: the verb, the URL, an optional JSON body and the edge headers.</summary>
/// <param name="Method">The verb.</param>
/// <param name="Url">The path and query.</param>
/// <param name="Body">The JSON body, if any.</param>
/// <param name="IfMatch">The <c>If-Match</c> value, if the route's optimistic concurrency needs it.</param>
/// <param name="IdempotencyKey">The <c>Idempotency-Key</c> value, if the route's idempotency stage needs it.</param>
/// <param name="Bearer">The bearer token, when the probe presents a real minted credential.</param>
/// <param name="Claims">The claim set a <see cref="ServicePrincipalProbeFactory"/> host runs the request as.</param>
internal sealed record RouteRequest(
    HttpMethod Method,
    string Url,
    JsonNode? Body = null,
    string? IfMatch = null,
    string? IdempotencyKey = null,
    string? Bearer = null,
    IReadOnlyList<string>? Claims = null);

/// <summary>
/// The ids a <see cref="RouteRequest"/> builder substitutes into a route's path and body.
/// <see cref="OrgId"/> and <see cref="ProjectId"/> are the seeded tenant; the rest may be
/// fresh GUIDs, because authorization precedes every lookup on the routes the matrix
/// probes (a denied request never reaches the handler).
/// </summary>
internal sealed record RouteRequestContext(
    Guid OrgId,
    Guid ProjectId,
    Guid MembershipId,
    Guid RoleId,
    Guid CaseId,
    Guid SuiteId,
    Guid SuiteRevisionId,
    Guid CaseRevisionId,
    Guid EnvironmentRevisionId,
    Guid PolicyRevisionId,
    Guid ManifestId,
    Guid WaiverId,
    Guid QuarantineId,
    Guid CiBindingId,
    Guid IngestTokenId)
{
    /// <summary>The seeded tenant plus fresh ids for everything else.</summary>
    internal static RouteRequestContext Fresh(Guid orgId, Guid projectId) => new(
        orgId,
        projectId,
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7());
}

/// <summary>
/// The per-route request builder table (02.2.1-T3): one entry per served route, keyed by
/// (method, template), each producing a request that is VALID for the route — the path ids
/// filled in, a JSON body the route's FluentValidation validator accepts, an
/// <c>Idempotency-Key</c> on a mutation and <c>If-Match: "1"</c> where the route's
/// optimistic concurrency needs it. A served route with no entry fails the classification
/// gate (<c>ViewerDenyMatrixTests.Every_Served_Route_Is_Classified</c>), and an entry for a
/// route the host no longer serves fails it too.
/// </summary>
internal static class RouteRequests
{
    /// <summary>Every classified route and how to build its request.</summary>
    internal static Dictionary<(string Method, string Template), Func<RouteRequestContext, RouteRequest>> All { get; } =
        Build();

    /// <summary>The request for <paramref name="route"/>.</summary>
    /// <exception cref="InvalidOperationException">The route has no builder entry.</exception>
    internal static RouteRequest Build(ServedRoute route, RouteRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(context);

        return All.TryGetValue((route.Method, route.Template), out var builder)
            ? builder(context)
            : throw new InvalidOperationException(
                $"no request builder is classified for {route.Method} {route.Template}; "
                + "add it to RouteRequests.All");
    }

    private static Dictionary<(string Method, string Template), Func<RouteRequestContext, RouteRequest>> Build()
    {
        var builders = new Dictionary<(string, string), Func<RouteRequestContext, RouteRequest>>(EqualityComparer<(string, string)>.Default);

        void Add(string method, string template, Func<RouteRequestContext, RouteRequest> builder) =>
            builders[(method, ServedRouteTable.NormaliseTemplate(template))] = builder;

        // ---- Tenancy -----------------------------------------------------------------------------------------
        Add("POST", "/v1/orgs", _ => new(HttpMethod.Post, "/v1/orgs",
            new JsonObject { ["name"] = "Probe Org", ["slug"] = "probe-org" }, IdempotencyKey: Key()));
        Add("GET", "/v1/orgs", _ => new(HttpMethod.Get, "/v1/orgs"));
        Add("GET", "/v1/orgs/{o}", ctx => new(HttpMethod.Get, $"/v1/orgs/{ctx.OrgId}"));
        Add("PATCH", "/v1/orgs/{o}", ctx => new(HttpMethod.Patch, $"/v1/orgs/{ctx.OrgId}",
            new JsonObject { ["name"] = "Probe Org" }, IfMatch: "\"1\""));
        Add("POST", "/v1/orgs/{o}/projects", ctx => new(HttpMethod.Post, $"/v1/orgs/{ctx.OrgId}/projects",
            new JsonObject { ["name"] = "Probe", ["slug"] = "probe" }, IdempotencyKey: Key()));
        Add("GET", "/v1/orgs/{o}/projects", ctx => new(HttpMethod.Get, $"/v1/orgs/{ctx.OrgId}/projects"));
        Add("GET", "/v1/projects/{p}", ctx => new(HttpMethod.Get, $"/v1/projects/{ctx.ProjectId}"));
        Add("PATCH", "/v1/projects/{p}", ctx => new(HttpMethod.Patch, $"/v1/projects/{ctx.ProjectId}",
            new JsonObject { ["name"] = "Probe" }, IfMatch: "\"1\""));

        // ---- Membership --------------------------------------------------------------------------------------
        Add("POST", "/v1/orgs/{o}/memberships", ctx => new(HttpMethod.Post, $"/v1/orgs/{ctx.OrgId}/memberships",
            new JsonObject { ["email"] = "probe@example.test", ["roles"] = new JsonArray() }, IdempotencyKey: Key()));
        Add("GET", "/v1/orgs/{o}/memberships", ctx => new(HttpMethod.Get, $"/v1/orgs/{ctx.OrgId}/memberships"));
        Add("GET", "/v1/orgs/{o}/memberships/{m}", ctx =>
            new(HttpMethod.Get, $"/v1/orgs/{ctx.OrgId}/memberships/{ctx.MembershipId}"));
        Add("PATCH", "/v1/orgs/{o}/memberships/{m}", ctx =>
            new(HttpMethod.Patch, $"/v1/orgs/{ctx.OrgId}/memberships/{ctx.MembershipId}",
                new JsonObject { ["status"] = "removed" }, IfMatch: "\"1\""));
        Add("DELETE", "/v1/orgs/{o}/memberships/{m}", ctx =>
            new(HttpMethod.Delete, $"/v1/orgs/{ctx.OrgId}/memberships/{ctx.MembershipId}", IfMatch: "\"1\""));
        Add("POST", "/v1/orgs/{o}/memberships/{m}/roles", ctx =>
            new(HttpMethod.Post, $"/v1/orgs/{ctx.OrgId}/memberships/{ctx.MembershipId}/roles",
                new JsonObject { ["role_id"] = ctx.RoleId.ToString() }, IdempotencyKey: Key()));
        Add("DELETE", "/v1/orgs/{o}/memberships/{m}/roles/{r}", ctx =>
            new(HttpMethod.Delete, $"/v1/orgs/{ctx.OrgId}/memberships/{ctx.MembershipId}/roles/{ctx.RoleId}", IfMatch: "\"1\""));

        // ---- Identity ----------------------------------------------------------------------------------------
        Add("GET", "/v1/permissions", _ => new(HttpMethod.Get, "/v1/permissions"));
        Add("GET", "/v1/roles", _ => new(HttpMethod.Get, "/v1/roles"));
        Add("POST", "/v1/roles", _ => new(HttpMethod.Post, "/v1/roles",
            new JsonObject { ["code"] = "probe_role", ["name"] = "Probe", ["permissions"] = new JsonArray("case:read") },
            IdempotencyKey: Key()));
        Add("PATCH", "/v1/roles/{r}", ctx => new(HttpMethod.Patch, $"/v1/roles/{ctx.RoleId}",
            new JsonObject { ["name"] = "Probe" }, IfMatch: "\"1\""));

        // ---- Audit -------------------------------------------------------------------------------------------
        Add("GET", "/v1/orgs/{o}/audit", ctx => new(HttpMethod.Get, $"/v1/orgs/{ctx.OrgId}/audit"));
        Add("GET", "/v1/orgs/{o}/audit/export", ctx => new(HttpMethod.Get, $"/v1/orgs/{ctx.OrgId}/audit/export"));

        // ---- CI admin ----------------------------------------------------------------------------------------
        Add("POST", "/v1/projects/{p}/ci-bindings", ctx => new(HttpMethod.Post, $"/v1/projects/{ctx.ProjectId}/ci-bindings",
            Binding(), IdempotencyKey: Key()));
        Add("GET", "/v1/projects/{p}/ci-bindings", ctx => new(HttpMethod.Get, $"/v1/projects/{ctx.ProjectId}/ci-bindings"));
        Add("PATCH", "/v1/ci-bindings/{b}", ctx => new(HttpMethod.Patch, $"/v1/ci-bindings/{ctx.CiBindingId}",
            new JsonObject { ["status"] = "disabled" }, IfMatch: "\"1\""));
        Add("DELETE", "/v1/ci-bindings/{b}", ctx => new(HttpMethod.Delete, $"/v1/ci-bindings/{ctx.CiBindingId}", IfMatch: "\"1\""));
        Add("POST", "/v1/projects/{p}/ingest-tokens", ctx => new(HttpMethod.Post, $"/v1/projects/{ctx.ProjectId}/ingest-tokens",
            new JsonObject { ["name"] = "probe" }, IdempotencyKey: Key()));
        Add("GET", "/v1/projects/{p}/ingest-tokens", ctx => new(HttpMethod.Get, $"/v1/projects/{ctx.ProjectId}/ingest-tokens"));
        Add("POST", "/v1/ingest-tokens/{t}/rotate", ctx => new(HttpMethod.Post, $"/v1/ingest-tokens/{ctx.IngestTokenId}/rotate",
            IdempotencyKey: Key()));
        Add("DELETE", "/v1/ingest-tokens/{t}", ctx => new(HttpMethod.Delete, $"/v1/ingest-tokens/{ctx.IngestTokenId}", IfMatch: "\"1\""));

        // ---- CI token exchange (pre-tenant) ------------------------------------------------------------------
        Add("POST", "/v1/ci/token", _ => new(HttpMethod.Post, "/v1/ci/token",
            new JsonObject { ["ingest_token"] = "egt_probe" }));

        // ---- Catalog -----------------------------------------------------------------------------------------
        Add("GET", "/v1/projects/{p}/cases", ctx => new(HttpMethod.Get, $"/v1/projects/{ctx.ProjectId}/cases"));
        Add("GET", "/v1/cases/{c}", ctx => new(HttpMethod.Get, $"/v1/cases/{ctx.CaseId}"));
        Add("PATCH", "/v1/cases/{c}", ctx => new(HttpMethod.Patch, $"/v1/cases/{ctx.CaseId}",
            new JsonObject { ["name"] = "Probe" }, IfMatch: "\"1\""));
        Add("GET", "/v1/cases/{c}/revisions", ctx => new(HttpMethod.Get, $"/v1/cases/{ctx.CaseId}/revisions"));
        Add("POST", "/v1/projects/{p}/suites", ctx => new(HttpMethod.Post, $"/v1/projects/{ctx.ProjectId}/suites",
            new JsonObject { ["name"] = "Probe", ["slug"] = "probe" }, IdempotencyKey: Key()));
        Add("GET", "/v1/projects/{p}/suites", ctx => new(HttpMethod.Get, $"/v1/projects/{ctx.ProjectId}/suites"));
        Add("GET", "/v1/suites/{s}", ctx => new(HttpMethod.Get, $"/v1/suites/{ctx.SuiteId}"));
        Add("POST", "/v1/suites/{s}/revisions", ctx => new(HttpMethod.Post, $"/v1/suites/{ctx.SuiteId}/revisions",
            new JsonObject
            {
                ["members"] = new JsonArray(new JsonObject { ["case_revision_id"] = ctx.CaseRevisionId.ToString() }),
            },
            IdempotencyKey: Key()));
        Add("GET", "/v1/suite-revisions/{r}", ctx => new(HttpMethod.Get, $"/v1/suite-revisions/{ctx.SuiteRevisionId}"));
        Add("GET", "/v1/suite-revisions/{r}/diff", ctx =>
            new(HttpMethod.Get, $"/v1/suite-revisions/{ctx.SuiteRevisionId}/diff?against={ctx.SuiteRevisionId}"));

        // ---- Orchestrator ------------------------------------------------------------------------------------
        Add("POST", "/v1/projects/{p}/manifests", ctx => new(HttpMethod.Post, $"/v1/projects/{ctx.ProjectId}/manifests",
            Manifest(ctx), IdempotencyKey: Key()));
        Add("POST", "/v1/projects/{p}/runs", ctx => new(HttpMethod.Post, $"/v1/projects/{ctx.ProjectId}/runs",
            Run(ctx), IdempotencyKey: Key()));
        Add("POST", "/v1/projects/{p}/environments/{e}/revisions", ctx =>
            new(HttpMethod.Post, $"/v1/projects/{ctx.ProjectId}/environments/staging/revisions", Environment(), IdempotencyKey: Key()));
        Add("GET", "/v1/environment-revisions/{id}", ctx =>
            new(HttpMethod.Get, $"/v1/environment-revisions/{ctx.EnvironmentRevisionId}"));

        // ---- Policy ------------------------------------------------------------------------------------------
        Add("POST", "/v1/projects/{p}/policies", ctx => new(HttpMethod.Post, $"/v1/projects/{ctx.ProjectId}/policies",
            new JsonObject { ["flaky_budget"] = 0 }, IdempotencyKey: Key()));
        Add("GET", "/v1/policies/{id}", ctx => new(HttpMethod.Get, $"/v1/policies/{ctx.PolicyRevisionId}"));
        Add("GET", "/v1/policies/{id}/revisions", ctx => new(HttpMethod.Get, $"/v1/policies/{ctx.PolicyRevisionId}/revisions"));
        Add("POST", "/v1/projects/{p}/policy-activation", ctx => new(HttpMethod.Post, $"/v1/projects/{ctx.ProjectId}/policy-activation",
            new JsonObject { ["policy_revision_id"] = ctx.PolicyRevisionId.ToString("D") }, IdempotencyKey: Key()));

        // ---- Waivers -----------------------------------------------------------------------------------------
        Add("POST", "/v1/waivers", ctx => new(HttpMethod.Post, "/v1/waivers", Waiver(ctx), IdempotencyKey: Key()));
        Add("POST", "/v1/waivers/{w}/approve", ctx => new(HttpMethod.Post, $"/v1/waivers/{ctx.WaiverId}/approve", IdempotencyKey: Key()));
        Add("POST", "/v1/waivers/{w}/revoke", ctx => new(HttpMethod.Post, $"/v1/waivers/{ctx.WaiverId}/revoke", IdempotencyKey: Key()));
        Add("GET", "/v1/projects/{p}/waivers", ctx => new(HttpMethod.Get, $"/v1/projects/{ctx.ProjectId}/waivers"));

        // ---- Quarantine --------------------------------------------------------------------------------------
        Add("POST", "/v1/quarantine", ctx => new(HttpMethod.Post, "/v1/quarantine", Quarantine(ctx), IdempotencyKey: Key()));
        Add("DELETE", "/v1/quarantine/{q}", ctx => new(HttpMethod.Delete, $"/v1/quarantine/{ctx.QuarantineId}", IdempotencyKey: Key()));
        Add("GET", "/v1/projects/{p}/quarantine", ctx => new(HttpMethod.Get, $"/v1/projects/{ctx.ProjectId}/quarantine"));

        // ---- Evidence ----------------------------------------------------------------------------------------
        Add("POST", "/v1/artifacts/upload-url", _ => new(HttpMethod.Post, "/v1/artifacts/upload-url",
            UploadUrl(), IdempotencyKey: Key()));

        // ---- Ingestion ---------------------------------------------------------------------------------------
        // 04.1.1-T1: the run is addressed in the path, so a fresh id is used - the route answers 404 not_found
        // for an unknown run (never a 403), which is what the viewer and ci-producer matrices expect. The body
        // is a shape-valid one-event batch.
        Add("POST", "/v1/runs/{r}/events", _ => new(HttpMethod.Post, $"/v1/runs/{Guid.CreateVersion7()}/events",
            new JsonObject
            {
                ["events"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["event_id"] = Guid.CreateVersion7().ToString(),
                        ["producer_id"] = "probe-producer",
                        ["sequence"] = 1,
                        ["execution_key"] = new string('a', 64),
                        ["attempt_no"] = 1,
                        ["type"] = "run.log",
                        ["occurred_at"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                        ["payload"] = new JsonObject { ["message"] = "probe" },
                    },
                },
            }));

        // ---- Dev surface (absent from the spec; allowlisted in ServedRouteTable) ------------------------------
        Add("POST", "/v1/slice/run", ctx => new(HttpMethod.Post, "/v1/slice/run",
            new JsonObject { ["project_ref"] = ctx.ProjectId.ToString() }));

        return builders;
    }

    private static string Key() => Guid.NewGuid().ToString("N");

    private static JsonObject Binding() => new()
    {
        ["provider"] = "github_actions",
        ["repository_id"] = 900_000_000L + Random.Shared.NextInt64(1, 99_999_999),
        ["repository_full_name"] = "probe/repo",
        ["allowed_ref_patterns"] = new JsonArray("refs/heads/main"),
        ["allowed_workflow_refs"] = new JsonArray("probe/repo/.github/workflows/ci.yml@refs/heads/main"),
    };

    private static JsonObject Environment() => new()
    {
        ["base_urls"] = new JsonObject { ["web"] = "https://probe.example.test" },
        ["dependency_versions"] = new JsonObject { ["postgres"] = "18.0" },
    };

    private static JsonObject Waiver(RouteRequestContext ctx) => new()
    {
        ["project_id"] = ctx.ProjectId.ToString(),
        ["scope"] = "case",
        ["target_id"] = ctx.CaseId.ToString(),
        ["reason"] = "probe",
        ["expires_at"] = DateTimeOffset.UtcNow.AddDays(30).ToString("O", CultureInfo.InvariantCulture),
    };

    private static JsonObject Quarantine(RouteRequestContext ctx) => new()
    {
        ["project_id"] = ctx.ProjectId.ToString(),
        ["case_id"] = ctx.CaseId.ToString(),
        ["owner"] = ctx.MembershipId.ToString(),
        ["reason"] = "probe",
        ["expires_at"] = DateTimeOffset.UtcNow.AddDays(30).ToString("O", CultureInfo.InvariantCulture),
    };

    private static JsonObject Run(RouteRequestContext ctx) => new()
    {
        ["manifest_id"] = ctx.ManifestId.ToString(),
    };

    /// <summary>
    /// The <c>POST /v1/artifacts/upload-url</c> body (06.1.2 §5): the run is addressed in the body, not the path,
    /// so a fresh id is used - the route answers 404 <c>not_found</c> for an unknown run, which is what the
    /// ci-producer and isolation matrices expect (never a 403). The rest is a shape-valid body.
    /// </summary>
    private static JsonObject UploadUrl() => new()
    {
        ["run_id"] = Guid.CreateVersion7().ToString(),
        ["producer_id"] = "probe-producer",
        ["execution_key"] = new string('a', 64),
        ["attempt_no"] = 1,
        ["kind"] = "trace",
        ["media_type"] = "application/json",
        ["bytes"] = 1024,
        ["sha256"] = "sha256:" + new string('b', 64),
    };

    private static JsonObject Manifest(RouteRequestContext ctx) => new()
    {
        ["schema_version"] = 1,
        ["suite_revision_id"] = ctx.SuiteRevisionId.ToString(),
        ["environment_revision_id"] = ctx.EnvironmentRevisionId.ToString(),
        ["planned_executions"] = new JsonArray(new JsonObject
        {
            ["case_revision_id"] = ctx.CaseRevisionId.ToString(),
            ["variant"] = new JsonObject { ["params"] = new JsonObject { ["browser"] = "chromium" } },
            ["target_env"] = "staging",
        }),
        ["matrix"] = new JsonObject
        {
            ["case_revision_ids"] = new JsonArray(ctx.CaseRevisionId.ToString()),
            ["variants"] = new JsonArray(new JsonObject { ["params"] = new JsonObject { ["browser"] = "chromium" } }),
            ["target_envs"] = new JsonArray("staging"),
        },
        ["shard_plan"] = new JsonObject
        {
            ["shards"] = new JsonArray(new JsonObject { ["shard_id"] = "s0", ["from_index"] = 0, ["to_index"] = 1 }),
        },
        ["env_needs"] = new JsonObject
        {
            ["target_envs"] = new JsonArray(new JsonObject { ["target_env"] = "staging" }),
        },
    };
}

/// <summary>
/// Sends a <see cref="RouteRequest"/> through a real host and judges the answer against the
/// 00 §9.2 denial envelope (02.2.1-T3): every violation is one line naming the route, the
/// status and the <c>error.code</c>, so one run lists every route that answered wrongly.
/// </summary>
internal static class RouteProbe
{
    /// <summary>Sends the request; the caller owns the response.</summary>
    internal static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        RouteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);

        using var message = new HttpRequestMessage(request.Method, request.Url);
        if (request.Body is not null)
        {
            message.Content = new StringContent(request.Body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        if (request.IfMatch is not null)
        {
            message.Headers.TryAddWithoutValidation("If-Match", request.IfMatch);
        }

        if (request.IdempotencyKey is not null)
        {
            message.Headers.TryAddWithoutValidation("Idempotency-Key", request.IdempotencyKey);
        }

        if (request.Bearer is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Bearer);
        }

        if (request.Claims is { } claims)
        {
            message.Headers.TryAddWithoutValidation(ServicePrincipalProbeFactory.ClaimsHeader, string.Join(",", claims));
        }

        return await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Null when the answer is the 403 <c>forbidden</c> envelope, else the violation line.</summary>
    internal static Task<string?> ForbiddenViolationAsync(
        ServedRoute route,
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        DeniedViolationAsync(route, response, ApiErrorCode.Forbidden, cancellationToken);

    /// <summary>Null when the answer is the 403 envelope with <paramref name="expectedCode"/>, else the violation line.</summary>
    internal static async Task<string?> DeniedViolationAsync(
        ServedRoute route,
        HttpResponseMessage response,
        string expectedCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(response);

        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return $"{route.Method} {route.Template}: expected 403, got {(int)response.StatusCode}";
        }

        if (!response.Headers.Contains("X-Request-Id"))
        {
            return $"{route.Method} {route.Template}: 403 without X-Request-Id";
        }

        using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var error = envelope.RootElement.GetProperty("error");
        var code = error.GetProperty("code").GetString();
        if (!string.Equals(code, expectedCode, StringComparison.Ordinal))
        {
            return $"{route.Method} {route.Template}: 403 with error.code '{code}' (expected '{expectedCode}')";
        }

        if (error.GetProperty("retryable").GetBoolean())
        {
            return $"{route.Method} {route.Template}: 403 retryable true";
        }

        return null;
    }

    /// <summary>Null when the answer is NOT the 403 <c>forbidden</c> envelope, else the violation line.</summary>
    internal static async Task<string?> NotForbiddenViolationAsync(
        ServedRoute route,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(response);

        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return null;
        }

        var code = await CodeAsync(response, cancellationToken).ConfigureAwait(false);
        return string.Equals(code, ApiErrorCode.Forbidden, StringComparison.Ordinal)
            ? $"{route.Method} {route.Template}: expected not-403, got 403 forbidden"
            : null;
    }

    /// <summary>The <c>error.code</c> of an envelope body, or null when the body is not one.</summary>
    internal static async Task<string?> CodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return envelope.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;
    }
}

/// <summary>
/// A cheap fingerprint of everything an org owns, read through the harness's owner
/// connection: the org's own <c>organization</c> row plus every <c>public</c> table with an
/// <c>org_id</c> column (discovered from <c>information_schema</c>, partitions folded into
/// their parent) summed into one count. Equal before and after a block of requests means
/// none of them wrote to the org (02.2.1-T3, AC3/AC4 — the "nothing was written" half of a
/// denial).
/// </summary>
internal static class OrgRowCounter
{
    /// <summary>The total row count of <paramref name="orgId"/>'s tables, as the owner.</summary>
    internal static async Task<long> CountAsync(
        DatabaseFixture fixture,
        Guid orgId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var tables = new List<string>();
        await using (var discover = new NpgsqlCommand(
            """
            select c.table_name::text
              from information_schema.columns c
              join information_schema.tables t
                on t.table_schema = c.table_schema and t.table_name = c.table_name and t.table_type = 'BASE TABLE'
              join pg_catalog.pg_class pc
                on pc.oid = format('%I.%I', c.table_schema, c.table_name)::regclass
             where c.table_schema = 'public'
               and c.column_name = 'org_id'
               and not pc.relispartition
             order by c.table_name
            """,
            connection))
        {
            await using var reader = await discover.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add(reader.GetString(0));
            }
        }

        var selects = new List<string> { "select count(*) from public.organization where id = @org" };
        foreach (var table in tables)
        {
            var quoted = "\"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            selects.Add($"select count(*) from public.{quoted} where org_id = @org");
        }

        await using var command = new NpgsqlCommand(
            "select " + string.Join(" + ", selects.Select(select => $"({select})")),
            connection);
        command.Parameters.AddWithValue("org", orgId);

        var total = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(total, CultureInfo.InvariantCulture);
    }
}
