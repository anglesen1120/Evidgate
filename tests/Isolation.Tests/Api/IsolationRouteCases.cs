using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Xunit;

namespace Isolation.Tests.Api;

/// <summary>
/// Who sends a probe: a member through the header stand-in (<see cref="IsolationApiFactory.UserHeader"/>, with
/// <see cref="OrgHeader"/> as <c>X-Evidgate-Org</c>), a real <c>ci_producer</c> bearer, or nobody.
/// </summary>
/// <param name="Label">The name failure messages print (never the credential).</param>
/// <param name="UserId">The member's user id, if a member.</param>
/// <param name="Email">The member's address, if a member.</param>
/// <param name="OrgHeader">The <c>X-Evidgate-Org</c> value, if any.</param>
/// <param name="Bearer">The <c>ci_producer</c> JWT, if a service caller.</param>
public sealed record ProbeCaller(string Label, Guid? UserId = null, string? Email = null, Guid? OrgHeader = null, string? Bearer = null)
{
    /// <summary>No credential at all.</summary>
    public static ProbeCaller Anonymous { get; } = new("anonymous");

    /// <summary>A member naming <paramref name="orgHeader"/> in <c>X-Evidgate-Org</c>.</summary>
    public static ProbeCaller Member(string label, IsolationUser user, Guid? orgHeader)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new(label, user.UserId, user.Email, orgHeader);
    }

    /// <summary>A <c>ci_producer</c> bearer.</summary>
    public static ProbeCaller Service(string label, string jwt) => new(label, Bearer: jwt);

    /// <summary>The label only: the bearer never reaches a failure message.</summary>
    public override string ToString() => Label;
}

/// <summary>One HTTP request of a probe.</summary>
/// <param name="Method">The verb.</param>
/// <param name="Url">The path and query.</param>
/// <param name="Body">The JSON body, if any.</param>
/// <param name="IfMatch">The <c>If-Match</c> value, if any (required on every PATCH, 00 §9.1).</param>
public sealed record ProbeRequest(HttpMethod Method, string Url, JsonNode? Body = null, string? IfMatch = null)
{
    /// <summary>"GET /v1/…".</summary>
    public override string ToString() => Method.Method + " " + Url;
}

/// <summary>One answer: the status, the 00 §9.2 <c>error.code</c> when the body is an envelope, and the raw text.</summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="Code">The envelope's <c>error.code</c>, or null.</param>
/// <param name="Text">The body text.</param>
/// <param name="Json">The parsed body, when it is JSON.</param>
public sealed record ProbeResponse(int Status, string? Code, string Text, JsonElement? Json)
{
    /// <summary>A 2xx status.</summary>
    public bool IsSuccess => Status is >= 200 and < 300;

    /// <summary>The length of a list body's <c>items</c>, or null when the body has none.</summary>
    public int? ItemCount =>
        Json is { ValueKind: JsonValueKind.Object } json
        && json.TryGetProperty("items", out var items)
        && items.ValueKind == JsonValueKind.Array
            ? items.GetArrayLength()
            : null;

    /// <summary>"404 not_found".</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Status} {Code ?? "-"}");
}

/// <summary>What a foreign/random request pair must answer (both halves, with the same status and error code).</summary>
public enum ProbeExpect
{
    /// <summary>An org-B member, own org, an org-A object id: 404, never the row (AC1; RLS hides it).</summary>
    NotFound,

    /// <summary>
    /// An org-B member naming org A (<c>{o}</c> or <c>X-Evidgate-Org</c>): refused at tenant resolution, 403 for a
    /// non-member (TenantResolver.cs:64-70, 00 §15 2026-10-03) or 404; never 2xx. AC1's 404 applies to object ids.
    /// </summary>
    ForeignOrg,

    /// <summary>An org-B member on an org-A project's list: 404 or 200 with zero items (CiAuthTests.cs:585-599).</summary>
    EmptyOrNotFound,

    /// <summary>
    /// A <c>ci_producer</c> of org B on an org-A id: 404 (service on a foreign org, TenantResolver.cs:84-85) or 403
    /// (<c>project_scope_mismatch</c> / missing permission), or a list's 200 with zero items; never a row.
    /// </summary>
    ServiceRefused,

    /// <summary>A refused write naming an org-A object inside the caller's own org (a body reference): any non-2xx.</summary>
    NotSuccess,
}

/// <summary>One isolation check of one route; <see cref="RunAsync"/> returns the violations, each naming route and case (AC6).</summary>
/// <param name="Route">The served route the probe covers.</param>
/// <param name="Case">What the probe proves.</param>
/// <param name="RunAsync">Sends the requests and judges the answers.</param>
public sealed record RouteProbe(ApiRoute Route, string Case, Func<RouteProbeRunner, Task<IReadOnlyList<string>>> RunAsync)
{
    /// <summary>"GET /v1/… [case]".</summary>
    public override string ToString() => Route + " [" + Case + "]";
}

/// <summary>
/// One served route and how to build its foreign requests. Path ids are produced per route TEMPLATE (never per
/// parameter name: <c>{r}</c> is a role id in <c>/roles/{r}</c> and a suite revision in <c>/suite-revisions/{r}</c>).
/// </summary>
/// <param name="Route">The route.</param>
/// <param name="Member">Probes sent by an org-B member (org B's <c>tenant_admin</c>, so no refusal is a missing permission).</param>
/// <param name="Service">Probes sent with org B's real <c>ci_producer</c> JWT (empty for header-scoped and tenantless routes).</param>
public sealed record RouteCase(
    ApiRoute Route,
    Func<IsolationWorld, IReadOnlyList<RouteProbe>> Member,
    Func<IsolationWorld, IReadOnlyList<RouteProbe>> Service);

/// <summary>One full-pagination walk of a list route as org B (02.9.1 AC2: page to exhaustion, count and ids exact).</summary>
/// <param name="Route">The list route.</param>
/// <param name="Case">What the walk proves.</param>
/// <param name="Caller">Org B's caller.</param>
/// <param name="Url">The first page's URL, with its <c>limit</c>.</param>
/// <param name="IdField">The item member holding the identity (<c>id</c>, or <c>code</c> for permissions).</param>
/// <param name="Expected">The exact identity set org B must see.</param>
/// <param name="ForeignCaller">Org A's caller whose first-page <c>next_cursor</c> is fed to org B (null: no cross-cursor step).</param>
/// <param name="ForeignUrl">Org A's analogue of <paramref name="Url"/>.</param>
/// <param name="ItemCheck">An extra per-item rule returning a violation, or null.</param>
public sealed record ListWalk(
    ApiRoute Route,
    string Case,
    ProbeCaller Caller,
    string Url,
    string IdField,
    Func<RouteProbeRunner, Task<IReadOnlyCollection<string>>> Expected,
    ProbeCaller? ForeignCaller = null,
    string? ForeignUrl = null,
    Func<JsonElement, string?>? ItemCheck = null);

/// <summary>
/// The API isolation cases of 02.9.1-T1: one <see cref="RouteCase"/> per served <c>/v1</c> route (org A =
/// <see cref="IsolationWorld.Acme"/>, the victim; org B = <see cref="IsolationWorld.Acme2"/>, the caller) and the
/// list walks. <see cref="Covered"/> feeds <c>CoverageTests</c>: a served route without a case fails the gate.
/// </summary>
/// <remarks>
/// Mutation bodies are valid for the route (the shapes the seeder sends successfully against the caller's own
/// objects, <see cref="IsolationWorld"/>), and every PATCH carries <c>If-Match</c>, so a foreign request reaches
/// tenancy and the handler instead of stopping at a 400/422 that could hide a leak; <see cref="ProbeExpect"/>
/// statuses exclude 400/422 for every pair except <see cref="ProbeExpect.NotSuccess"/>.
/// </remarks>
public static class IsolationRouteCases
{
    /// <summary>Every case, one per served route.</summary>
    public static IReadOnlyList<RouteCase> All { get; } = Build();

    /// <summary>The routes the suite covers.</summary>
    public static IReadOnlyList<ApiRoute> Covered => [.. All.Select(c => c.Route)];

    /// <summary>Every member probe of every case.</summary>
    public static IReadOnlyList<RouteProbe> MemberProbes(IsolationWorld world) => [.. All.SelectMany(c => c.Member(world))];

    /// <summary>Every service probe of every case.</summary>
    public static IReadOnlyList<RouteProbe> ServiceProbes(IsolationWorld world) => [.. All.SelectMany(c => c.Service(world))];

    /// <summary>Org B's caller: its founder (<c>tenant_admin</c>) naming org B.</summary>
    public static ProbeCaller OrgBAdmin(IsolationWorld world) =>
        ProbeCaller.Member("org-B tenant_admin (X-Evidgate-Org: B)", Require(world).Acme2.Admin, world.Acme2.Id);

    /// <summary>Org A's founder naming org A (cursor source and the A-side of symmetric lists only).</summary>
    public static ProbeCaller OrgAAdmin(IsolationWorld world) =>
        ProbeCaller.Member("org-A tenant_admin (X-Evidgate-Org: A)", Require(world).Acme.Admin, world.Acme.Id);

    /// <summary>Org B's <c>ci_producer</c> of project <c>web</c>.</summary>
    public static ProbeCaller OrgBService(IsolationWorld world) =>
        ProbeCaller.Service("org-B ci_producer (project web)", Require(world).Acme2.Projects[0].CiProducerJwt);

    /// <summary>The full-pagination walks of every served list route.</summary>
    public static IReadOnlyList<ListWalk> Lists(IsolationWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var a = world.Acme;
        var b = world.Acme2;
        var bAdmin = OrgBAdmin(world);
        var aAdmin = OrgAAdmin(world);
        var walks = new List<ListWalk>
        {
            new(Get("/v1/orgs"), "only the caller's own orgs", bAdmin, "/v1/orgs?limit=200", "id", Fixed([b.Id])),
            new(Get("/v1/orgs/{o}/projects"), "org B's projects", bAdmin, $"/v1/orgs/{b.Id}/projects?limit=1", "id",
                Fixed([.. b.Projects.Select(p => p.Id)]), aAdmin, $"/v1/orgs/{a.Id}/projects?limit=1"),
            new(Get("/v1/orgs/{o}/memberships"), "org B's memberships", bAdmin, $"/v1/orgs/{b.Id}/memberships?limit=1", "id",
                Fixed(b.MembershipIds), aAdmin, $"/v1/orgs/{a.Id}/memberships?limit=1"),
            new(Get("/v1/orgs/{o}/audit"), "org B's audit events", bAdmin, $"/v1/orgs/{b.Id}/audit?limit=2", "id",
                runner => runner.OwnerIdsAsync("select id::text from public.audit_log where org_id = @org", b.Id),
                aAdmin, $"/v1/orgs/{a.Id}/audit?limit=1"),
            new(Get("/v1/roles"), "built-in roles only, identical to org A's list", bAdmin, "/v1/roles?limit=1", "id",
                runner => runner.WalkIdsAsync(aAdmin, "/v1/roles?limit=1", "id"), aAdmin, "/v1/roles?limit=1",
                item => OrgIdOf(item) is { } org && org != b.Id ? $"role {IdOf(item, "id")} belongs to org {org}" : null),
            new(Get("/v1/permissions"), "the global catalogue, identical to org A's", bAdmin, "/v1/permissions?limit=1", "code",
                runner => runner.WalkIdsAsync(aAdmin, "/v1/permissions?limit=1", "code"), aAdmin, "/v1/permissions?limit=1"),
        };

        for (var i = 0; i < b.Projects.Count; i++)
        {
            var bp = b.Projects[i];
            var ap = a.Projects[i];
            walks.Add(new(Get("/v1/projects/{p}/ci-bindings"), $"org B project {bp.Slug} bindings", bAdmin, $"/v1/projects/{bp.Id}/ci-bindings?limit=1", "id",
                Fixed([bp.BindingId]), aAdmin, $"/v1/projects/{ap.Id}/ci-bindings?limit=1"));
            walks.Add(new(Get("/v1/projects/{p}/ingest-tokens"), $"org B project {bp.Slug} ingest tokens", bAdmin, $"/v1/projects/{bp.Id}/ingest-tokens?limit=1", "id",
                Fixed([bp.ActiveTokenId, bp.RevokedTokenId]), aAdmin, $"/v1/projects/{ap.Id}/ingest-tokens?limit=1"));
            walks.Add(new(Get("/v1/projects/{p}/cases"), $"org B project {bp.Slug} cases", bAdmin, $"/v1/projects/{bp.Id}/cases?limit=1", "id",
                Fixed(bp.CaseIds), aAdmin, $"/v1/projects/{ap.Id}/cases?limit=1"));
            walks.Add(new(Get("/v1/projects/{p}/suites"), $"org B project {bp.Slug} suites", bAdmin, $"/v1/projects/{bp.Id}/suites?limit=1", "id",
                Fixed(bp.SuiteIds), aAdmin, $"/v1/projects/{ap.Id}/suites?limit=1"));
            for (var c = 0; c < bp.CaseIds.Count; c++)
            {
                walks.Add(new(Get("/v1/cases/{c}/revisions"), $"org B case {bp.Slug}#{c} revisions", bAdmin, $"/v1/cases/{bp.CaseIds[c]}/revisions?limit=1", "id",
                    Fixed([bp.CaseRevisionIds[c]]), aAdmin, $"/v1/cases/{ap.CaseIds[c]}/revisions?limit=1"));
            }
        }

        return walks;
    }

    private static List<RouteCase> Build() =>
    [
        // ---- Tenancy (TenancyEndpoints.cs:50-84) ---------------------------------------------------------------
        Case("POST", "/v1/orgs", (route, w) =>
        [
            // Tenantless (TenancyEndpoints.cs:52): a new org is only ever the caller's own, whatever header it sends.
            new(route, "creates only the caller's own org, never attaches to org A", runner => runner.CreateOrgNamingAsync(route, w.Acme)),
        ]),
        Case("GET", "/v1/orgs", (route, w) =>
        [
            // Tenantless (TenancyEndpoints.cs:57): the list is keyed by the user, never by X-Evidgate-Org.
            new(route, "lists only the caller's orgs while naming org A", runner => runner.OrgListNamingAsync(route, w)),
        ]),
        OrgCase("GET", "/v1/orgs/{o}", o => new(HttpMethod.Get, $"/v1/orgs/{o}")),
        OrgCase("PATCH", "/v1/orgs/{o}", o => new(HttpMethod.Patch, $"/v1/orgs/{o}", new JsonObject { ["name"] = "Intruder" }, "\"1\"")),
        OrgCase("POST", "/v1/orgs/{o}/projects", o => new(HttpMethod.Post, $"/v1/orgs/{o}/projects", new JsonObject { ["name"] = "intruder", ["slug"] = "intruder" })),
        OrgCase("GET", "/v1/orgs/{o}/projects", o => new(HttpMethod.Get, $"/v1/orgs/{o}/projects")),
        ProjectCase("GET", "/v1/projects/{p}", p => new(HttpMethod.Get, $"/v1/projects/{p}")),
        ProjectCase("PATCH", "/v1/projects/{p}", p => new(HttpMethod.Patch, $"/v1/projects/{p}", new JsonObject { ["name"] = "intruder" }, "\"1\"")),

        // ---- Membership (MembershipEndpoints.cs:49-75) ---------------------------------------------------------
        OrgCase("POST", "/v1/orgs/{o}/memberships", o => new(HttpMethod.Post, $"/v1/orgs/{o}/memberships", Invite())),
        OrgCase("GET", "/v1/orgs/{o}/memberships", o => new(HttpMethod.Get, $"/v1/orgs/{o}/memberships")),
        MembershipCase("GET", "/v1/orgs/{o}/memberships/{m}", (o, m, _) => new(HttpMethod.Get, $"/v1/orgs/{o}/memberships/{m}")),
        MembershipCase("PATCH", "/v1/orgs/{o}/memberships/{m}", (o, m, _) =>
            new(HttpMethod.Patch, $"/v1/orgs/{o}/memberships/{m}", new JsonObject { ["status"] = "removed" }, "\"1\"")),
        MembershipCase("DELETE", "/v1/orgs/{o}/memberships/{m}", (o, m, _) => new(HttpMethod.Delete, $"/v1/orgs/{o}/memberships/{m}")),
        MembershipCase("POST", "/v1/orgs/{o}/memberships/{m}/roles", (o, m, w) =>
            new(HttpMethod.Post, $"/v1/orgs/{o}/memberships/{m}/roles", new JsonObject { ["role_id"] = w.BuiltInRoleIds["viewer"].ToString() })),
        MembershipCase("DELETE", "/v1/orgs/{o}/memberships/{m}/roles/{r}", (o, m, w) =>
            new(HttpMethod.Delete, $"/v1/orgs/{o}/memberships/{m}/roles/{w.BuiltInRoleIds["viewer"]}"), grants: true),

        // ---- Identity (IdentityEndpoints.cs:59-84): org-scoped by X-Evidgate-Org, no {o} in the path ----------
        HeaderCase("GET", "/v1/permissions", _ => new(HttpMethod.Get, "/v1/permissions")),
        HeaderCase("GET", "/v1/roles", _ => new(HttpMethod.Get, "/v1/roles")),
        HeaderCase("POST", "/v1/roles", _ => new(HttpMethod.Post, "/v1/roles", new JsonObject
        {
            ["code"] = "intruder",
            ["name"] = "Intruder",
            ["permissions"] = new JsonArray("case:read"),
        })),
        // No org owns a role here: custom roles are off (IsolationWorld.CustomRoleId is null), so the built-in
        // viewer id stands in; the own-org half asserts a random role id is refused like any other id.
        HeaderCase("PATCH", "/v1/roles/{r}", w => new(HttpMethod.Patch, $"/v1/roles/{w.BuiltInRoleIds["viewer"]}", new JsonObject { ["name"] = "Intruder" }),
            ownOrg: (route, w) => new(route, "own org, random role id refused", runner => runner.RefusedAsync(
                route,
                "own org, random role id refused",
                OrgBAdmin(w),
                new(HttpMethod.Patch, $"/v1/roles/{Guid.CreateVersion7()}", new JsonObject { ["name"] = "Intruder" })))),

        // ---- Audit (AuditEndpoints.cs:42-46) -------------------------------------------------------------------
        OrgCase("GET", "/v1/orgs/{o}/audit", o => new(HttpMethod.Get, $"/v1/orgs/{o}/audit")),
        OrgCase("GET", "/v1/orgs/{o}/audit/export", o => new(HttpMethod.Get, $"/v1/orgs/{o}/audit/export")),

        // ---- CI admin (CiAdminEndpoints.cs:30-55) --------------------------------------------------------------
        ProjectCase("POST", "/v1/projects/{p}/ci-bindings", p => new(HttpMethod.Post, $"/v1/projects/{p}/ci-bindings", Binding())),
        ProjectCase("GET", "/v1/projects/{p}/ci-bindings", p => new(HttpMethod.Get, $"/v1/projects/{p}/ci-bindings"), list: true),
        ObjectCase("PATCH", "/v1/ci-bindings/{b}", p => [("binding", p.BindingId)], id =>
            new(HttpMethod.Patch, $"/v1/ci-bindings/{id}", new JsonObject { ["status"] = "disabled" }, "\"1\"")),
        ObjectCase("DELETE", "/v1/ci-bindings/{b}", p => [("binding", p.BindingId)], id => new(HttpMethod.Delete, $"/v1/ci-bindings/{id}")),
        ProjectCase("POST", "/v1/projects/{p}/ingest-tokens", p => new(HttpMethod.Post, $"/v1/projects/{p}/ingest-tokens", new JsonObject { ["name"] = "intruder" })),
        ProjectCase("GET", "/v1/projects/{p}/ingest-tokens", p => new(HttpMethod.Get, $"/v1/projects/{p}/ingest-tokens"), list: true),
        ObjectCase("POST", "/v1/ingest-tokens/{t}/rotate", p => [("active token", p.ActiveTokenId), ("revoked token", p.RevokedTokenId)], id =>
            new(HttpMethod.Post, $"/v1/ingest-tokens/{id}/rotate")),
        ObjectCase("DELETE", "/v1/ingest-tokens/{t}", p => [("active token", p.ActiveTokenId)], id => new(HttpMethod.Delete, $"/v1/ingest-tokens/{id}")),

        // ---- CI token exchange (CiTokenEndpoints.cs:41): pre-tenant, credential-authenticated --------------------
        Case("POST", "/v1/ci/token", (route, w) =>
        [
            new(route, "org A's revoked ingest token is refused, whoever presents it", runner => runner.RevokedExchangeAsync(route, w)),
        ]),

        // ---- Catalog (CatalogEndpoints.cs:39-83) ---------------------------------------------------------------
        ProjectCase("GET", "/v1/projects/{p}/cases", p => new(HttpMethod.Get, $"/v1/projects/{p}/cases"), list: true),
        ObjectCase("GET", "/v1/cases/{c}", p => Indexed("case", p.CaseIds), id => new(HttpMethod.Get, $"/v1/cases/{id}")),
        ObjectCase("PATCH", "/v1/cases/{c}", p => Indexed("case", p.CaseIds), id =>
            new(HttpMethod.Patch, $"/v1/cases/{id}", new JsonObject { ["name"] = "intruder" }, "\"1\"")),
        ObjectCase("GET", "/v1/cases/{c}/revisions", p => Indexed("case", p.CaseIds), id => new(HttpMethod.Get, $"/v1/cases/{id}/revisions"), list: true),
        ProjectCase("POST", "/v1/projects/{p}/suites", p => new(HttpMethod.Post, $"/v1/projects/{p}/suites", new JsonObject { ["name"] = "intruder", ["slug"] = "intruder" })),
        ProjectCase("GET", "/v1/projects/{p}/suites", p => new(HttpMethod.Get, $"/v1/projects/{p}/suites"), list: true),
        ObjectCase("GET", "/v1/suites/{s}", p => Indexed("suite", p.SuiteIds), id => new(HttpMethod.Get, $"/v1/suites/{id}")),
        SuiteRevisionCreateCase(),
        ObjectCase("GET", "/v1/suite-revisions/{r}", p => Indexed("suite revision", p.SuiteRevisionIds), id => new(HttpMethod.Get, $"/v1/suite-revisions/{id}")),
        SuiteRevisionDiffCase(),

        // ---- Orchestrator (OrchestratorEndpoints.cs:37-47) -----------------------------------------------------
        ManifestCase(),
        ProjectCase("POST", "/v1/projects/{p}/environments/{e}/revisions", p =>
            new(HttpMethod.Post, $"/v1/projects/{p}/environments/{IsolationWorld.EnvironmentKey}/revisions", Environment())),
        ObjectCase("GET", "/v1/environment-revisions/{id}", p => [("environment revision", p.EnvironmentRevisionId)], id =>
            new(HttpMethod.Get, $"/v1/environment-revisions/{id}")),

        // ---- Dev surface (SliceEndpoints.cs:29-40; recorded in ServedDocumentTests.cs:59) -------------------------
        Case("POST", "/v1/slice/run", (route, w) =>
        [
            // No tenant filter and in-memory stubs (InMemoryManifestStore): naming org A's project must neither echo
            // an org-A id nor write to org A (the mutation test's snapshot).
            new(route, "naming org A's project echoes no org-A data", runner => runner.SliceNamingAsync(route, w.Acme.Projects[0].Id)),
        ]),
    ];

    // ------------------------------------------------------------------------------------------------ case shapes

    private static RouteCase Case(
        string method,
        string template,
        Func<ApiRoute, IsolationWorld, IReadOnlyList<RouteProbe>> member,
        Func<ApiRoute, IsolationWorld, IReadOnlyList<RouteProbe>>? service = null)
    {
        var route = new ApiRoute(method, template);
        return new RouteCase(route, w => member(route, Require(w)), w => service is null ? [] : service(route, Require(w)));
    }

    /// <summary>A route whose tenant is <c>{o}</c>: org A's id against a random org id.</summary>
    private static RouteCase OrgCase(string method, string template, Func<Guid, ProbeRequest> request) =>
        Case(
            method,
            template,
            (route, w) => [Pair(route, "org A named in {o}", ProbeExpect.ForeignOrg, OrgBAdmin(w), request(w.Acme.Id), request(Guid.CreateVersion7()))],
            (route, w) => [Pair(route, "org A named in {o}", ProbeExpect.ServiceRefused, OrgBService(w), request(w.Acme.Id), request(Guid.CreateVersion7()))]);

    /// <summary>
    /// A membership route: org A in <c>{o}</c> (tenant resolution refuses) and org B's own <c>{o}</c> with org A's
    /// membership in <c>{m}</c> (404). <paramref name="grants"/> uses the membership holding org A's extra grant.
    /// </summary>
    private static RouteCase MembershipCase(string method, string template, Func<Guid, Guid, IsolationWorld, ProbeRequest> request, bool grants = false) =>
        Case(
            method,
            template,
            (route, w) =>
            {
                List<(string, Guid)> memberships = grants
                    ? [("membership with org A's extra grant", w.Acme.ExtraGrant.MembershipId)]
                    : new List<(string, Guid)>
                    {
                        ("org A admin membership", w.Acme.Admin.MembershipId),
                        ("org A pending invite", w.Acme.PendingInviteMembershipId),
                    };
                var probes = new List<RouteProbe>
                {
                    Pair(route, "org A named in {o}", ProbeExpect.ForeignOrg, OrgBAdmin(w),
                        request(w.Acme.Id, memberships[0].Item2, w), request(Guid.CreateVersion7(), memberships[0].Item2, w)),
                };
                probes.AddRange(memberships.Select(m => Pair(route, "own {o}, " + m.Item1 + " in {m}", ProbeExpect.NotFound, OrgBAdmin(w),
                    request(w.Acme2.Id, m.Item2, w), request(w.Acme2.Id, Guid.CreateVersion7(), w))));
                return probes;
            },
            (route, w) =>
            [
                Pair(route, "org A named in {o}", ProbeExpect.ServiceRefused, OrgBService(w),
                    request(w.Acme.Id, w.Acme.Admin.MembershipId, w), request(Guid.CreateVersion7(), w.Acme.Admin.MembershipId, w)),
            ]);

    /// <summary>A route scoped by <c>X-Evidgate-Org</c> only: the header naming org A against a random org id.</summary>
    private static RouteCase HeaderCase(
        string method,
        string template,
        Func<IsolationWorld, ProbeRequest> request,
        Func<ApiRoute, IsolationWorld, RouteProbe>? ownOrg = null) =>
        Case(method, template, (route, w) =>
        {
            var aHeader = ProbeCaller.Member("org-B tenant_admin (X-Evidgate-Org: A)", w.Acme2.Admin, w.Acme.Id);
            var randomHeader = ProbeCaller.Member("org-B tenant_admin (X-Evidgate-Org: random)", w.Acme2.Admin, Guid.CreateVersion7());
            var probes = new List<RouteProbe>
            {
                new(route, "org A named in X-Evidgate-Org", runner => runner.CallerPairAsync(route, "org A named in X-Evidgate-Org", aHeader, randomHeader, request(w))),
            };
            if (ownOrg is not null)
            {
                probes.Add(ownOrg(route, w));
            }

            return probes;
        });

    /// <summary>
    /// A route naming a project in <c>{p}</c>: as an org-B member, each org-A project against a random id; as org
    /// B's <c>ci_producer</c> (project <c>web</c>), the sibling project <c>api</c> (403, ScopeGuard) and org A's
    /// project against a random id.
    /// </summary>
    private static RouteCase ProjectCase(string method, string template, Func<Guid, ProbeRequest> request, bool list = false) =>
        Case(
            method,
            template,
            (route, w) => [.. w.Acme.Projects.Select(p => Pair(route, $"org A project {p.Slug} in {{p}}",
                list ? ProbeExpect.EmptyOrNotFound : ProbeExpect.NotFound, OrgBAdmin(w), request(p.Id), request(Guid.CreateVersion7())))],
            (route, w) =>
            [
                new(route, "sibling project api of the token's org", runner => runner.ScopeMismatchAsync(
                    route, "sibling project api of the token's org", OrgBService(w), request(w.Acme2.Projects[1].Id),
                    requireScopeCode: route.Template.EndsWith("/manifests", StringComparison.Ordinal))),
                Pair(route, "org A project web in {p}", ProbeExpect.ServiceRefused, OrgBService(w), request(w.Acme.Projects[0].Id), request(Guid.CreateVersion7())),
            ]);

    /// <summary>
    /// A route naming an object id: as an org-B member, every org-A object the selector yields (both projects)
    /// against a random id; as org B's <c>ci_producer</c>, org A's objects against a random id and the sibling
    /// project's own objects (refused, never 2xx).
    /// </summary>
    private static RouteCase ObjectCase(
        string method,
        string template,
        Func<IsolationProject, IEnumerable<(string Label, Guid Id)>> ids,
        Func<Guid, ProbeRequest> request,
        bool list = false) =>
        Case(
            method,
            template,
            (route, w) => [.. w.Acme.Projects.SelectMany(p => ids(p).Select(id => Pair(route, $"org A {id.Label} of project {p.Slug}",
                list ? ProbeExpect.EmptyOrNotFound : ProbeExpect.NotFound, OrgBAdmin(w), request(id.Id), request(Guid.CreateVersion7()))))],
            (route, w) =>
            [
                .. ids(w.Acme.Projects[0]).Select(id => Pair(route, $"org A {id.Label} of project web", ProbeExpect.ServiceRefused, OrgBService(w),
                    request(id.Id), request(Guid.CreateVersion7()))),
                .. ids(w.Acme2.Projects[1]).Select(id => new RouteProbe(route, $"sibling project api's {id.Label}", runner => runner.RefusedAsync(
                    route, $"sibling project api's {id.Label}", OrgBService(w), request(id.Id), allowed: [403, 404]))),
            ]);

    /// <summary>
    /// <c>POST /v1/suites/{s}/revisions</c>: org A's suite (create and <c>dry_run</c> preview) against a random
    /// suite, and org B's own suite pinning org A's case revisions against random ones (a body reference).
    /// </summary>
    private static RouteCase SuiteRevisionCreateCase()
    {
        static JsonObject Members(IEnumerable<Guid> revisions) => new()
        {
            ["members"] = new JsonArray([.. revisions.Select(id => (JsonNode)new JsonObject { ["case_revision_id"] = id.ToString() })]),
        };

        return ObjectCaseWith(
            "POST",
            "/v1/suites/{s}/revisions",
            p => Indexed("suite", p.SuiteIds),
            (id, p) => new(HttpMethod.Post, $"/v1/suites/{id}/revisions", Members(p.CaseRevisionIds)),
            (route, w) =>
            [
                .. w.Acme.Projects.Select(p => Pair(route, $"org A suite of project {p.Slug}, dry_run", ProbeExpect.NotFound, OrgBAdmin(w),
                    new(HttpMethod.Post, $"/v1/suites/{p.SuiteIds[0]}/revisions?dry_run=true", Members(p.CaseRevisionIds)),
                    new(HttpMethod.Post, $"/v1/suites/{Guid.CreateVersion7()}/revisions?dry_run=true", Members(p.CaseRevisionIds)))),
                Pair(route, "own suite pinning org A's case revisions", ProbeExpect.NotSuccess, OrgBAdmin(w),
                    new(HttpMethod.Post, $"/v1/suites/{w.Acme2.Projects[0].SuiteIds[1]}/revisions", Members(w.Acme.Projects[0].CaseRevisionIds)),
                    new(HttpMethod.Post, $"/v1/suites/{w.Acme2.Projects[0].SuiteIds[1]}/revisions", Members([Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()]))),
            ]);
    }

    /// <summary>
    /// <c>GET /v1/suite-revisions/{r}/diff?against=</c>: org A's revision against org A's other one (404) and org
    /// B's own revision diffed against org A's (same refusal as against a random id).
    /// </summary>
    private static RouteCase SuiteRevisionDiffCase() =>
        ObjectCaseWith(
            "GET",
            "/v1/suite-revisions/{r}/diff",
            p => Indexed("suite revision", p.SuiteRevisionIds),
            (id, p) => new(HttpMethod.Get, $"/v1/suite-revisions/{id}/diff?against={p.SuiteRevisionIds[^1]}"),
            (route, w) =>
            [
                Pair(route, "own revision diffed against org A's", ProbeExpect.NotSuccess, OrgBAdmin(w),
                    new(HttpMethod.Get, $"/v1/suite-revisions/{w.Acme2.Projects[0].SuiteRevisionIds[0]}/diff?against={w.Acme.Projects[0].SuiteRevisionIds[1]}"),
                    new(HttpMethod.Get, $"/v1/suite-revisions/{w.Acme2.Projects[0].SuiteRevisionIds[0]}/diff?against={Guid.CreateVersion7()}")),
            ]);

    /// <summary>
    /// <c>POST /v1/projects/{p}/manifests</c>: org A's project with a manifest over org A's own revisions (404),
    /// and org B's own project with a manifest over org A's revisions (same refusal as over random ids).
    /// </summary>
    private static RouteCase ManifestCase()
    {
        var route = new ApiRoute("POST", "/v1/projects/{p}/manifests");
        static ProbeRequest Upload(Guid project, JsonObject manifest) => new(HttpMethod.Post, $"/v1/projects/{project}/manifests", manifest);
        static JsonObject Random() => Manifest(Guid.CreateVersion7(), [Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()], Guid.CreateVersion7());

        return new RouteCase(
            route,
            world =>
            {
                var w = Require(world);
                return
                [
                    .. w.Acme.Projects.Select(p => Pair(route, $"org A project {p.Slug} in {{p}}", ProbeExpect.NotFound, OrgBAdmin(w),
                        Upload(p.Id, Manifest(p)), Upload(Guid.CreateVersion7(), Manifest(p)))),
                    Pair(route, "own project, manifest over org A's revisions", ProbeExpect.NotSuccess, OrgBAdmin(w),
                        Upload(w.Acme2.Projects[0].Id, Manifest(w.Acme.Projects[0])), Upload(w.Acme2.Projects[0].Id, Random())),
                ];
            },
            world =>
            {
                var w = Require(world);
                return
                [
                    new(route, "sibling project api of the token's org", runner => runner.ScopeMismatchAsync(
                        route, "sibling project api of the token's org", OrgBService(w), Upload(w.Acme2.Projects[1].Id, Manifest(w.Acme2.Projects[1])), requireScopeCode: true)),
                    Pair(route, "org A project web in {p}", ProbeExpect.ServiceRefused, OrgBService(w),
                        Upload(w.Acme.Projects[0].Id, Manifest(w.Acme.Projects[0])), Upload(Guid.CreateVersion7(), Manifest(w.Acme.Projects[0]))),
                    Pair(route, "own project, manifest over org A's revisions", ProbeExpect.NotSuccess, OrgBService(w),
                        Upload(w.Acme2.Projects[0].Id, Manifest(w.Acme.Projects[0])), Upload(w.Acme2.Projects[0].Id, Random())),
                ];
            });
    }

    /// <summary><see cref="ObjectCase"/> whose request also needs the object's project, plus extra member probes.</summary>
    private static RouteCase ObjectCaseWith(
        string method,
        string template,
        Func<IsolationProject, IEnumerable<(string Label, Guid Id)>> ids,
        Func<Guid, IsolationProject, ProbeRequest> request,
        Func<ApiRoute, IsolationWorld, IEnumerable<RouteProbe>> extraMember) =>
        Case(
            method,
            template,
            (route, w) =>
            [
                .. w.Acme.Projects.SelectMany(p => ids(p).Select(id => Pair(route, $"org A {id.Label} of project {p.Slug}", ProbeExpect.NotFound, OrgBAdmin(w),
                    request(id.Id, p), request(Guid.CreateVersion7(), p)))),
                .. extraMember(route, w),
            ],
            (route, w) =>
            [
                .. ids(w.Acme.Projects[0]).Select(id => Pair(route, $"org A {id.Label} of project web", ProbeExpect.ServiceRefused, OrgBService(w),
                    request(id.Id, w.Acme.Projects[0]), request(Guid.CreateVersion7(), w.Acme.Projects[0]))),
                .. ids(w.Acme2.Projects[1]).Select(id => new RouteProbe(route, $"sibling project api's {id.Label}", runner => runner.RefusedAsync(
                    route, $"sibling project api's {id.Label}", OrgBService(w), request(id.Id, w.Acme2.Projects[1]), allowed: [403, 404]))),
            ]);

    // ---------------------------------------------------------------------------------------------------- helpers

    private static RouteProbe Pair(ApiRoute route, string name, ProbeExpect expect, ProbeCaller caller, ProbeRequest foreign, ProbeRequest random) =>
        new(route, name, runner => runner.PairAsync(route, name, expect, caller, foreign, random));

    private static ApiRoute Get(string template) => new("GET", template);

    private static IsolationWorld Require(IsolationWorld world) => world ?? throw new ArgumentNullException(nameof(world));

    private static IEnumerable<(string Label, Guid Id)> Indexed(string label, IReadOnlyList<Guid> ids) =>
        ids.Select((id, i) => (string.Create(CultureInfo.InvariantCulture, $"{label} #{i}"), id));

    private static Func<RouteProbeRunner, Task<IReadOnlyCollection<string>>> Fixed(IEnumerable<Guid> ids)
    {
        IReadOnlyCollection<string> set = ids.Select(id => id.ToString("D")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _ => Task.FromResult(set);
    }

    private static Guid? OrgIdOf(JsonElement item) =>
        item.TryGetProperty("org_id", out var org) && org.ValueKind == JsonValueKind.String ? org.GetGuid() : null;

    private static string IdOf(JsonElement item, string field) =>
        item.TryGetProperty(field, out var id) ? id.ToString() : "?";

    private static JsonObject Invite() => new()
    {
        ["email"] = "intruder@example.test",
        ["roles"] = new JsonArray(),
    };

    /// <summary>The seeder's binding shape with a fresh repository id per request (a repeat would be 409 for the wrong reason).</summary>
    private static JsonObject Binding()
    {
        var repositoryId = 900_000_000L + Random.Shared.NextInt64(1, 99_999_999);
        return new JsonObject
        {
            ["provider"] = "github_actions",
            ["repository_id"] = repositoryId,
            ["repository_full_name"] = "intruder/repo",
            ["allowed_ref_patterns"] = new JsonArray("refs/heads/main"),
            ["allowed_workflow_refs"] = new JsonArray("intruder/repo/.github/workflows/ci.yml@refs/heads/main"),
        };
    }

    private static JsonObject Environment() => new()
    {
        ["base_urls"] = new JsonObject { ["web"] = "https://intruder.example.test" },
        ["dependency_versions"] = new JsonObject { ["postgres"] = "18.0" },
    };

    private static JsonObject Manifest(IsolationProject project) =>
        Manifest(project.SuiteRevisionIds[0], project.CaseRevisionIds, project.EnvironmentRevisionId);

    /// <summary>The seeder's 05.1.1 manifest shape (<see cref="IsolationWorld"/>, after <c>ManifestUnitTests.Build</c>).</summary>
    private static JsonObject Manifest(Guid suiteRevision, IReadOnlyList<Guid> caseRevisions, Guid environmentRevision)
    {
        var planned = new JsonArray();
        foreach (var caseRevision in caseRevisions)
        {
            planned.Add(new JsonObject
            {
                ["case_revision_id"] = caseRevision.ToString(),
                ["variant"] = new JsonObject { ["params"] = new JsonObject { ["browser"] = "chromium" } },
                ["target_env"] = IsolationWorld.EnvironmentKey,
            });
        }

        return new JsonObject
        {
            ["schema_version"] = 1,
            ["suite_revision_id"] = suiteRevision.ToString(),
            ["environment_revision_id"] = environmentRevision.ToString(),
            ["planned_executions"] = planned,
            ["matrix"] = new JsonObject
            {
                ["case_revision_ids"] = new JsonArray([.. caseRevisions.Select(id => (JsonNode)id.ToString())]),
                ["variants"] = new JsonArray(new JsonObject { ["params"] = new JsonObject { ["browser"] = "chromium" } }),
                ["target_envs"] = new JsonArray(IsolationWorld.EnvironmentKey),
            },
            ["shard_plan"] = new JsonObject
            {
                ["shards"] = new JsonArray(new JsonObject { ["shard_id"] = "s0", ["from_index"] = 0, ["to_index"] = caseRevisions.Count }),
            },
            ["env_needs"] = new JsonObject { ["target_envs"] = new JsonArray(new JsonObject { ["target_env"] = IsolationWorld.EnvironmentKey }) },
        };
    }
}

/// <summary>
/// Sends probes through the real host and judges them. Every violation is one line naming the route, the case and
/// the caller (AC6), so one run lists every leak.
/// </summary>
public sealed class RouteProbeRunner
{
    private const int MaxPages = 500;

    private readonly ApiIsolationFixture _fixture;
    private readonly HttpClient _client;
    private readonly HashSet<string> _foreignIds;

    /// <summary>A runner over <paramref name="fixture"/>'s host; org A's object ids are the forbidden set.</summary>
    public RouteProbeRunner(ApiIsolationFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;
        _client = fixture.AnonymousClient();
        _foreignIds = OwnedIds(fixture.World.Acme);
        _foreignIds.ExceptWith(OwnedIds(fixture.World.Acme2));
    }

    /// <summary>The seeded world.</summary>
    public IsolationWorld World => _fixture.World;

    /// <summary>Every id org <paramref name="org"/> owns in the world (shared built-in role ids excluded).</summary>
    public static HashSet<string> OwnedIds(IsolationOrg org)
    {
        ArgumentNullException.ThrowIfNull(org);
        var ids = new List<Guid> { org.Id, org.PendingInviteMembershipId };
        ids.AddRange(org.Users.SelectMany(u => new[] { u.UserId, u.MembershipId }));
        foreach (var p in org.Projects)
        {
            ids.AddRange([p.Id, p.BindingId, p.ActiveTokenId, p.RevokedTokenId, p.EnvironmentRevisionId, p.ManifestId]);
            ids.AddRange(p.CaseIds);
            ids.AddRange(p.CaseRevisionIds);
            ids.AddRange(p.SuiteIds);
            ids.AddRange(p.SuiteRevisionIds);
        }

        return ids.Select(id => id.ToString("D")).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The org-A ids <paramref name="text"/> carries.</summary>
    public IReadOnlyList<string> ForeignIdsIn(string text) =>
        string.IsNullOrEmpty(text) ? [] : [.. _foreignIds.Where(id => text.Contains(id, StringComparison.OrdinalIgnoreCase))];

    /// <summary>Sends one request as <paramref name="caller"/>; every write carries a fresh <c>Idempotency-Key</c>.</summary>
    public async Task<ProbeResponse> SendAsync(ProbeCaller caller, ProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);

        using var message = new HttpRequestMessage(request.Method, request.Url);
        if (caller.UserId is { } user)
        {
            message.Headers.Add(IsolationApiFactory.UserHeader, user.ToString());
            if (caller.Email is not null)
            {
                message.Headers.Add(IsolationApiFactory.EmailHeader, caller.Email);
            }
        }

        if (caller.OrgHeader is { } org)
        {
            message.Headers.Add(IsolationApiFactory.OrgHeader, org.ToString());
        }

        if (caller.Bearer is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Bearer);
        }

        if (request.Method != HttpMethod.Get && !request.Url.StartsWith("/v1/ci/token", StringComparison.Ordinal))
        {
            message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        }

        if (request.IfMatch is not null)
        {
            message.Headers.TryAddWithoutValidation("If-Match", request.IfMatch);
        }

        if (request.Body is not null)
        {
            message.Content = new StringContent(request.Body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var response = await _client.SendAsync(message, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

        JsonElement? json = null;
        string? code = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                using var document = JsonDocument.Parse(text);
                json = document.RootElement.Clone();
                if (json.Value.ValueKind == JsonValueKind.Object
                    && json.Value.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("code", out var errorCode))
                {
                    code = errorCode.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // A non-JSON body (the audit export stream, a 204): judged by status and text only.
        }

        return new ProbeResponse((int)response.StatusCode, code, text, json);
    }

    /// <summary>
    /// The foreign request and its random-id control: both must answer as <paramref name="expect"/> says, with the
    /// same status and error code (no existence oracle), and the foreign answer must carry no org-A id.
    /// </summary>
    public async Task<IReadOnlyList<string>> PairAsync(ApiRoute route, string name, ProbeExpect expect, ProbeCaller caller, ProbeRequest foreign, ProbeRequest random)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(foreign);
        ArgumentNullException.ThrowIfNull(random);

        var real = await SendAsync(caller, foreign).ConfigureAwait(false);
        var control = await SendAsync(caller, random).ConfigureAwait(false);
        var violations = new List<string>();

        Judge(real, foreign, "foreign");
        Judge(control, random, "random");
        if (real.Status != control.Status || !string.Equals(real.Code, control.Code, StringComparison.Ordinal))
        {
            violations.Add(Describe(route, name, caller, $"existence oracle: foreign {foreign} answered {real}, random {random} answered {control}"));
        }

        violations.AddRange(ForeignIdsIn(real.Text).Select(id => Describe(route, name, caller, $"{foreign} answered {real} carrying org-A id {id}")));
        return violations;

        void Judge(ProbeResponse response, ProbeRequest request, string half)
        {
            var ok = expect switch
            {
                ProbeExpect.NotFound => response.Status == 404,
                ProbeExpect.ForeignOrg => response.Status is 403 or 404,
                ProbeExpect.EmptyOrNotFound => response.Status == 404 || (response.Status == 200 && response.ItemCount == 0),
                ProbeExpect.ServiceRefused => response.Status is 403 or 404 || (response.Status == 200 && response.ItemCount == 0),
                ProbeExpect.NotSuccess => !response.IsSuccess && response.Status < 500,
                _ => false,
            };

            if (!ok)
            {
                violations.Add(Describe(route, name, caller, $"{half} {request} answered {response}, expected {Expected(expect)}: {Brief(response.Text)}"));
            }
        }
    }

    /// <summary>
    /// The same request under two callers that differ only in the org they name (org A, a random org): both refused
    /// at tenant resolution, identically, with no org-A id in the answer.
    /// </summary>
    public async Task<IReadOnlyList<string>> CallerPairAsync(ApiRoute route, string name, ProbeCaller foreignCaller, ProbeCaller randomCaller, ProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(foreignCaller);
        ArgumentNullException.ThrowIfNull(randomCaller);

        var real = await SendAsync(foreignCaller, request).ConfigureAwait(false);
        var control = await SendAsync(randomCaller, request).ConfigureAwait(false);
        var violations = new List<string>();
        if (real.Status is not (403 or 404))
        {
            violations.Add(Describe(route, name, foreignCaller, $"{request} answered {real}, expected {Expected(ProbeExpect.ForeignOrg)}: {Brief(real.Text)}"));
        }

        if (real.Status != control.Status || !string.Equals(real.Code, control.Code, StringComparison.Ordinal))
        {
            violations.Add(Describe(route, name, foreignCaller, $"existence oracle: naming org A answered {real}, naming a random org answered {control}"));
        }

        violations.AddRange(ForeignIdsIn(real.Text).Select(id => Describe(route, name, foreignCaller, $"{request} answered {real} carrying org-A id {id}")));
        return violations;
    }

    /// <summary>One request that must be refused (non-2xx; within <paramref name="allowed"/> when given) and carry no org-A id.</summary>
    public async Task<IReadOnlyList<string>> RefusedAsync(ApiRoute route, string name, ProbeCaller caller, ProbeRequest request, IReadOnlyCollection<int>? allowed = null)
    {
        var response = await SendAsync(caller, request).ConfigureAwait(false);
        var violations = new List<string>();
        if (response.IsSuccess || (allowed is not null && !allowed.Contains(response.Status)))
        {
            violations.Add(Describe(route, name, caller, $"{request} answered {response}, expected a refusal{(allowed is null ? string.Empty : " (" + string.Join('/', allowed) + ")")}: {Brief(response.Text)}"));
        }

        violations.AddRange(ForeignIdsIn(response.Text).Select(id => Describe(route, name, caller, $"{request} answered {response} carrying org-A id {id}")));
        return violations;
    }

    /// <summary>
    /// A <c>ci_producer</c> on a sibling project of its own org: 403 (ScopeGuard / CatalogRules / OrchestratorRules);
    /// <c>project_scope_mismatch</c> exactly when <paramref name="requireScopeCode"/> (the token holds the route's permission).
    /// </summary>
    public async Task<IReadOnlyList<string>> ScopeMismatchAsync(ApiRoute route, string name, ProbeCaller caller, ProbeRequest request, bool requireScopeCode)
    {
        var response = await SendAsync(caller, request).ConfigureAwait(false);
        var violations = new List<string>();
        if (response.Status != 403 || (requireScopeCode && response.Code != "project_scope_mismatch"))
        {
            violations.Add(Describe(route, name, caller,
                $"{request} answered {response}, expected 403 {(requireScopeCode ? "project_scope_mismatch" : "project_scope_mismatch/forbidden")}: {Brief(response.Text)}"));
        }

        return violations;
    }

    /// <summary>
    /// <c>POST /v1/orgs</c> by a fresh user naming org A in <c>X-Evidgate-Org</c>: 201 with a new org that is not org
    /// A, listed for its founder only; org A's founder still lists org A alone.
    /// </summary>
    public async Task<IReadOnlyList<string>> CreateOrgNamingAsync(ApiRoute route, IsolationOrg orgA)
    {
        ArgumentNullException.ThrowIfNull(orgA);
        const string name = "creates only the caller's own org, never attaches to org A";
        var founder = new IsolationUser("fresh", Guid.CreateVersion7(), "fresh@example.test", Guid.Empty, []);
        var caller = ProbeCaller.Member("fresh user (X-Evidgate-Org: A)", founder, orgA.Id);
        var violations = new List<string>();

        var slug = "iso-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
        var created = await SendAsync(caller, new(HttpMethod.Post, "/v1/orgs", new JsonObject { ["name"] = "Acme", ["slug"] = slug })).ConfigureAwait(false);
        if (created.Status != 201 || created.Json is not { } body || !body.TryGetProperty("id", out var idElement))
        {
            violations.Add(Describe(route, name, caller, $"POST /v1/orgs answered {created}, expected 201: {Brief(created.Text)}"));
            return violations;
        }

        var id = idElement.GetGuid();
        if (id == orgA.Id)
        {
            violations.Add(Describe(route, name, caller, "the create returned org A's id"));
        }

        var own = await SendAsync(ProbeCaller.Member("fresh user", founder, null), new(HttpMethod.Get, "/v1/orgs")).ConfigureAwait(false);
        var ownIds = Ids(own, "id");
        if (ownIds.Count != 1 || !ownIds.Contains(id.ToString("D")))
        {
            violations.Add(Describe(route, name, caller, $"the founder's GET /v1/orgs lists [{string.Join(", ", ownIds)}], expected only the new org {id}"));
        }

        var aList = await SendAsync(IsolationRouteCases.OrgAAdmin(World), new(HttpMethod.Get, "/v1/orgs")).ConfigureAwait(false);
        if (Ids(aList, "id").Contains(id.ToString("D")))
        {
            violations.Add(Describe(route, name, caller, "org A's founder lists the new org"));
        }

        violations.AddRange(ForeignIdsIn(created.Text).Select(foreign => Describe(route, name, caller, $"the create answer carries org-A id {foreign}")));
        return violations;
    }

    /// <summary><c>GET /v1/orgs</c> by org B's founder naming org A, then a random org: only org B, both times.</summary>
    public async Task<IReadOnlyList<string>> OrgListNamingAsync(ApiRoute route, IsolationWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        const string name = "lists only the caller's orgs while naming org A";
        var violations = new List<string>();
        var expected = world.Acme2.Id.ToString("D");
        foreach (var header in new[] { world.Acme.Id, Guid.CreateVersion7() })
        {
            var caller = ProbeCaller.Member(header == world.Acme.Id ? "org-B tenant_admin (X-Evidgate-Org: A)" : "org-B tenant_admin (X-Evidgate-Org: random)", world.Acme2.Admin, header);
            var response = await SendAsync(caller, new(HttpMethod.Get, "/v1/orgs")).ConfigureAwait(false);
            var ids = Ids(response, "id");
            if (response.Status != 200 || ids.Count != 1 || !ids.Contains(expected))
            {
                violations.Add(Describe(route, name, caller, $"answered {response} listing [{string.Join(", ", ids)}], expected only org B {expected}"));
            }

            violations.AddRange(ForeignIdsIn(response.Text).Select(id => Describe(route, name, caller, $"answer carries org-A id {id}")));
        }

        return violations;
    }

    /// <summary>
    /// <c>POST /v1/ci/token</c> with org A's revoked ingest token: 401, no JWT, anonymously and when presented with
    /// org B member headers naming org B (the headers never rebind a credential).
    /// </summary>
    public async Task<IReadOnlyList<string>> RevokedExchangeAsync(ApiRoute route, IsolationWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        const string name = "org A's revoked ingest token is refused, whoever presents it";
        var violations = new List<string>();
        var request = new ProbeRequest(HttpMethod.Post, "/v1/ci/token", new JsonObject { ["ingest_token"] = world.Acme.Projects[0].RevokedTokenSecret });
        var answers = new List<ProbeResponse>();
        foreach (var caller in new[] { ProbeCaller.Anonymous, IsolationRouteCases.OrgBAdmin(world) })
        {
            var response = await SendAsync(caller, request).ConfigureAwait(false);
            answers.Add(response);
            if (response.Status != 401 || (response.Json is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("token", out _)))
            {
                violations.Add(Describe(route, name, caller, $"POST /v1/ci/token answered {response}, expected 401 without a token: {Brief(response.Text)}"));
            }

            violations.AddRange(ForeignIdsIn(response.Text).Select(id => Describe(route, name, caller, $"answer carries org-A id {id}")));
        }

        if (answers[0].Status != answers[1].Status || answers[0].Code != answers[1].Code)
        {
            violations.Add(Describe(route, name, IsolationRouteCases.OrgBAdmin(world), $"member headers changed the answer: anonymous {answers[0]}, org-B member {answers[1]}"));
        }

        return violations;
    }

    /// <summary><c>POST /v1/slice/run</c> naming org A's project: the answer carries no org-A id and is not a server error.</summary>
    public async Task<IReadOnlyList<string>> SliceNamingAsync(ApiRoute route, Guid orgAProject)
    {
        const string name = "naming org A's project echoes no org-A data";
        var request = new ProbeRequest(HttpMethod.Post, "/v1/slice/run", new JsonObject { ["project_ref"] = orgAProject.ToString() });
        var response = await SendAsync(ProbeCaller.Anonymous, request).ConfigureAwait(false);
        var violations = new List<string>();
        if (response.Status >= 500)
        {
            violations.Add(Describe(route, name, ProbeCaller.Anonymous, $"{request} answered {response}: {Brief(response.Text)}"));
        }

        violations.AddRange(ForeignIdsIn(response.Text).Select(id => Describe(route, name, ProbeCaller.Anonymous, $"{request} answered {response} carrying org-A id {id}")));
        return violations;
    }

    /// <summary>Pages <paramref name="url"/> as <paramref name="caller"/> to exhaustion and returns the identities (a refused page throws).</summary>
    public async Task<IReadOnlyCollection<string>> WalkIdsAsync(ProbeCaller caller, string url, string idField)
    {
        var (ids, failure, _) = await WalkAsync(caller, url, idField).ConfigureAwait(false);
        return failure is null ? ids.ToHashSet(StringComparer.OrdinalIgnoreCase) : throw new InvalidOperationException($"{caller}: {failure}");
    }

    /// <summary>
    /// Pages <paramref name="url"/> following <c>next_cursor</c> until it is null: every identity in page order, the
    /// first refused page (or a cursor loop) as <c>Failure</c>, and every page body.
    /// </summary>
    public async Task<(IReadOnlyList<string> Ids, string? Failure, IReadOnlyList<string> Pages)> WalkAsync(ProbeCaller caller, string url, string idField)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrEmpty(url);

        var ids = new List<string>();
        var pages = new List<string>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var pageUrl = cursor is null ? url : url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "cursor=" + Uri.EscapeDataString(cursor);
            var response = await SendAsync(caller, new(HttpMethod.Get, pageUrl)).ConfigureAwait(false);
            pages.Add(response.Text);
            if (response.Status != 200 || response.ItemCount is null)
            {
                return (ids, $"GET {pageUrl} answered {response}: {Brief(response.Text)}", pages);
            }

            ids.AddRange(Ids(response, idField));
            cursor = NextCursor(response);
            if (cursor is null)
            {
                return (ids, null, pages);
            }

            if (!cursors.Add(cursor))
            {
                return (ids, $"GET {pageUrl} repeated cursor {cursor}", pages);
            }
        }

        return (ids, $"GET {url} did not end within {MaxPages} pages", pages);
    }

    /// <summary>The first page's <c>next_cursor</c> of <paramref name="url"/> as <paramref name="caller"/>, or null.</summary>
    public async Task<string?> FirstCursorAsync(ProbeCaller caller, string url) =>
        NextCursor(await SendAsync(caller, new(HttpMethod.Get, url)).ConfigureAwait(false));

    /// <summary>The <c>id::text</c> values an owner-connection query returns for <paramref name="org"/> (<c>@org</c>).</summary>
    public async Task<IReadOnlyCollection<string>> OwnerIdsAsync(string sql, Guid org)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(_fixture.OwnerConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("org", org);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>The identities of a list answer's <c>items</c>.</summary>
    public static IReadOnlyList<string> Ids(ProbeResponse response, string idField)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Json is { ValueKind: JsonValueKind.Object } json
            && json.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Array
                ? [.. items.EnumerateArray().Select(item => item.TryGetProperty(idField, out var id) ? id.ToString() : "<no " + idField + ">")]
                : [];
    }

    /// <summary>"GET /v1/… [case] as caller: message".</summary>
    public static string Describe(ApiRoute route, string name, ProbeCaller caller, string message) =>
        $"{route} [{name}] as {caller}: {message}";

    private static string? NextCursor(ProbeResponse response) =>
        response.Json is { ValueKind: JsonValueKind.Object } json
        && json.TryGetProperty("next_cursor", out var next)
        && next.ValueKind == JsonValueKind.String
        && next.GetString() is { Length: > 0 } value
            ? value
            : null;

    private static string Expected(ProbeExpect expect) => expect switch
    {
        ProbeExpect.NotFound => "404",
        ProbeExpect.ForeignOrg => "403/404",
        ProbeExpect.EmptyOrNotFound => "404 or 200 with no items",
        ProbeExpect.ServiceRefused => "403/404 (or 200 with no items)",
        ProbeExpect.NotSuccess => "a 4xx refusal",
        _ => expect.ToString(),
    };

    private static string Brief(string text) => text.Length <= 300 ? text : text[..300] + "…";
}
