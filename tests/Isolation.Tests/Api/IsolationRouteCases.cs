using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
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
/// <param name="IfMatch">The <c>If-Match</c> value, if any (sent on every PATCH whose endpoint requires it, 00 §9.1).</param>
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
/// <param name="ETag">The response <c>ETag</c> as served, quotes included, or null.</param>
public sealed record ProbeResponse(int Status, string? Code, string Text, JsonElement? Json, string? ETag = null)
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
    /// <summary>An org-B member, own org, an org-A object id: 404 <c>not_found</c>, never the row (AC1; RLS hides it).</summary>
    NotFound,

    /// <summary>
    /// An org-B member naming org A (<c>{o}</c> or <c>X-Evidgate-Org</c>): refused at tenant resolution, 403 for a
    /// non-member (TenantResolver.cs:64-70, 00 §15 2026-10-03) or 404 <c>not_found</c>; never 2xx. AC1's 404
    /// applies to object ids.
    /// </summary>
    ForeignOrg,

    /// <summary>An org-B member on an org-A project's list: 404 <c>not_found</c>, or a GET list's 200 with zero items (CiAuthTests.cs:585-599).</summary>
    EmptyOrNotFound,

    /// <summary>
    /// A <c>ci_producer</c> of org B on an org-A id: 404 <c>not_found</c> (service on a foreign org,
    /// TenantResolver.cs:84-85) or 403 (<c>project_scope_mismatch</c> / missing permission), or a GET list's 200
    /// with zero items; never a row.
    /// </summary>
    ServiceRefused,

    /// <summary>
    /// A <c>ci_producer</c> of org B, authorized for the project its token names, on an org-A (or random) id at
    /// <c>POST /v1/artifacts/{a}/complete</c>: the route's project comes from the credential, so the request
    /// reaches the tenant read and is refused 404 <c>artifact_not_found</c> — the code 06.1.2 §5 names for an
    /// unknown, other-tenant or other-project artifact. The foreign and the random id answer identically (same
    /// status, same code, same body shape), so another tenant's artifact is never resolved and no existence
    /// oracle exists. Never a 2xx and never a row.
    /// </summary>
    ArtifactNotFound,

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
/// objects, <see cref="IsolationWorld"/>), and every PATCH whose endpoint requires <c>If-Match</c> carries it, so a
/// foreign request reaches tenancy and the handler instead of stopping at a 400/422 that could hide a leak;
/// <see cref="ProbeExpect"/> statuses exclude 400/422 for every pair except <see cref="ProbeExpect.NotSuccess"/>.
/// </remarks>
public static class IsolationRouteCases
{
    /// <summary>
    /// The 00 §9.2 <c>error.code</c> of the pre-tenant login routes' designed answers in this host
    /// (<c>ApiErrorCode.Unavailable</c>, ErrorEnvelope.cs:92): no provider credential is configured, so
    /// <c>start</c> refuses with 503 before any tenant or session exists.
    /// </summary>
    private const string UnavailableCode = "unavailable";

    /// <summary>
    /// The 00 §9.2 <c>error.code</c> of a callback that carries no pending-login cookie
    /// (<c>ApiErrorCode.TokenInvalid</c>, ErrorEnvelope.cs:59): 401, checked before the provider's configuration.
    /// </summary>
    private const string TokenInvalidCode = "token_invalid";

    /// <summary>
    /// The 00 §9.2 <c>error.code</c> of a request that carries no live session
    /// (<c>ApiErrorCode.Unauthenticated</c>, ErrorEnvelope.cs:56). The link routes of 02.3.1-T2 ask the
    /// deployment's session authenticator (02.3.2-T1, LinkEndpoints.cs:542-569), which answers this 401 as soon as
    /// the request presents no session cookie (SessionRequestAuthenticator.cs:87-91) — before a pepper, a tenant or
    /// a row is read.
    /// </summary>
    private const string UnauthenticatedCode = "unauthenticated";

    /// <summary>
    /// The <c>error.code</c> of <c>GET /v1/releases/{id}</c> for an unknown, other-tenant or other-project id: the
    /// module answers the shared <c>not_found</c> code, so all three answer identically and a refused read never
    /// names which case it hit.
    /// </summary>
    private const string ReleaseNotFoundCode = "not_found";

    /// <summary>
    /// A token with the contract's shape (<c>evgi_&lt;12-char prefix&gt;_&lt;43-char secret&gt;</c>,
    /// <c>InviteTokenFormat</c>) and no matching row: the invite staging route of 02.3.1-T2 validates the shape
    /// only — whether the token exists is decided by the binder at login — so this is the body it accepts.
    /// </summary>
    private static readonly string InviteTokenProbe = "evgi_" + "probe1234567" + "_" + new string('A', 43);

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

    /// <summary>
    /// Org B's OIDC-minted <c>ci_producer</c> of project <c>web</c> (02.9.1-T3, hand-over from #19): the same
    /// scope as <see cref="OrgBService"/>, minted through the JWKS-verified binding exchange instead of the
    /// ingest token.
    /// </summary>
    public static ProbeCaller OrgBServiceOidc(IsolationWorld world) =>
        ProbeCaller.Service("org-B OIDC ci_producer (project web)", Require(world).Acme2.Projects[0].OidcProducerJwt);

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
            // 11.5.1-T5.2: the world seeds three security events per org, so limit=1 pages three times and org A's
            // first page carries a next_cursor for the cross-org cursor step below.
            new(Get("/v1/orgs/{o}/security-events"), "org B's security events", bAdmin, $"/v1/orgs/{b.Id}/security-events?limit=1", "id",
                runner => runner.TenantIdsAsync("select id::text from public.security_event where org_id = @org", b.Id),
                aAdmin, $"/v1/orgs/{a.Id}/security-events?limit=1"),
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
                Fixed(bp.BindingIds), aAdmin, $"/v1/projects/{ap.Id}/ci-bindings?limit=1"));
            walks.Add(new(Get("/v1/projects/{p}/ingest-tokens"), $"org B project {bp.Slug} ingest tokens", bAdmin, $"/v1/projects/{bp.Id}/ingest-tokens?limit=1", "id",
                Fixed([bp.ActiveTokenId, bp.RevokedTokenId]), aAdmin, $"/v1/projects/{ap.Id}/ingest-tokens?limit=1"));
            walks.Add(new(Get("/v1/projects/{p}/cases"), $"org B project {bp.Slug} cases", bAdmin, $"/v1/projects/{bp.Id}/cases?limit=1", "id",
                Fixed(bp.CaseIds), aAdmin, $"/v1/projects/{ap.Id}/cases?limit=1"));
            walks.Add(new(Get("/v1/projects/{p}/suites"), $"org B project {bp.Slug} suites", bAdmin, $"/v1/projects/{bp.Id}/suites?limit=1", "id",
                Fixed(bp.SuiteIds), aAdmin, $"/v1/projects/{ap.Id}/suites?limit=1"));
            walks.Add(new(Get("/v1/projects/{p}/waivers"), $"org B project {bp.Slug} waivers", bAdmin, $"/v1/projects/{bp.Id}/waivers?limit=1", "id",
                Fixed(bp.WaiverIds), aAdmin, $"/v1/projects/{ap.Id}/waivers?limit=1"));
            walks.Add(new(Get("/v1/projects/{p}/quarantine"), $"org B project {bp.Slug} quarantine entries", bAdmin, $"/v1/projects/{bp.Id}/quarantine?limit=1", "id",
                Fixed(bp.QuarantineIds), aAdmin, $"/v1/projects/{ap.Id}/quarantine?limit=1"));
            walks.Add(new(Get("/v1/projects/{p}/authoring/tree"), $"org B project {bp.Slug} authoring roots", bAdmin,
                $"/v1/projects/{bp.Id}/authoring/tree?limit=1", "node_id",
                Fixed([bp.AuthoringRootNodeId, bp.AuthoringSecondRootNodeId]), aAdmin, $"/v1/projects/{ap.Id}/authoring/tree?limit=1"));
            walks.Add(new(Get("/v1/projects/{p}/authoring/checklist-items"), $"org B project {bp.Slug} checklist items", bAdmin,
                $"/v1/projects/{bp.Id}/authoring/checklist-items?limit=1", "item_id",
                // 13.1.1-T3: route 11's own control restores the seeded tombstone, so this list can hold one row more
                // than the seed names depending on which test ran first (nothing resets between them). The expectation
                // is the seed's live ids UNION the database's own live rows for the project — the shape the run walk
                // below uses for a creation positive control. A dropped row, a page from another project or another org,
                // and a repeated item each still fail the set equality.
                async runner =>
                {
                    var expected = new HashSet<string>(
                        await runner.OwnerIdsAsync(
                            "select id::text from public.checklist_item where project_id = @org and deleted_at is null",
                            bp.Id),
                        StringComparer.OrdinalIgnoreCase);
                    expected.UnionWith([bp.AuthoringItemId.ToString("D"), bp.AuthoringSecondItemId.ToString("D")]);
                    return expected;
                },
                aAdmin, $"/v1/projects/{ap.Id}/authoring/checklist-items?limit=1"));
            walks.Add(new(Get("/v1/projects/{p}/authoring/manual-cases"), $"org B project {bp.Slug} manual cases", bAdmin,
                $"/v1/projects/{bp.Id}/authoring/manual-cases?limit=1", "case_id",
                // 13.1.1-T3, for the same reason: routes 18 and 24 restore the seeded case tombstone (route 24 by
                // re-applying its delete version), so the live set is the seed's ids union the database's live rows.
                async runner =>
                {
                    var expected = new HashSet<string>(
                        await runner.OwnerIdsAsync(
                            "select id::text from public.manual_test_case where project_id = @org and deleted_at is null",
                            bp.Id),
                        StringComparer.OrdinalIgnoreCase);
                    expected.UnionWith([bp.AuthoringManualCaseId.ToString("D"), bp.AuthoringSecondManualCaseId.ToString("D")]);
                    return expected;
                },
                aAdmin, $"/v1/projects/{ap.Id}/authoring/manual-cases?limit=1"));
            // 13.1.1-T3: the version timeline is append-only and the seed cannot name its ids, so the expectation is the
            // database's own row set for the project (owner connection), exactly as the audit and run walks derive theirs.
            // Tombsones are rows too: a version is written for a delete, so the set covers every seeded entity change.
            walks.Add(new(Get("/v1/projects/{p}/authoring/versions"), $"org B project {bp.Slug} authoring versions", bAdmin,
                $"/v1/projects/{bp.Id}/authoring/versions?limit=1", "version_id",
                runner => runner.OwnerIdsAsync("select id::text from public.authoring_version where project_id = @org", bp.Id),
                aAdmin, $"/v1/projects/{ap.Id}/authoring/versions?limit=1"));
            // The run-create case below is a positive control: it proves that org B's own
            // `POST /v1/projects/{p}/runs` is accepted, so org B's first project can hold one run more than the
            // seed recorded, and whether it does depends on which test of the suite runs first (nothing resets
            // between them — see ApiIsolationFixture's remarks; the side effect is 05.2.1-T1's, not finalize's).
            // The expectation is therefore the seed's ids UNION the database's own row set for the project (owner
            // connection, filtered by `project_id`), exactly as the audit walk above derives its ids. The union is
            // the database set whenever the seed's rows are there, so a paged run from another project or another
            // org, a dropped row, or a seeded run missing from the database each still fail the set equality.
            walks.Add(new(Get("/v1/projects/{p}/runs"), $"org B project {bp.Slug} runs", bAdmin, $"/v1/projects/{bp.Id}/runs?limit=1", "run_id",
                async runner =>
                {
                    var expected = new HashSet<string>(
                        await runner.OwnerIdsAsync("select id::text from public.run where project_id = @org", bp.Id),
                        StringComparer.OrdinalIgnoreCase);
                    expected.UnionWith(bp.RunIds.Select(id => id.ToString("D")));
                    return expected;
                },
                aAdmin, $"/v1/projects/{ap.Id}/runs?limit=1"));
            for (var c = 0; c < bp.CaseIds.Count; c++)
            {
                walks.Add(new(Get("/v1/cases/{c}/revisions"), $"org B case {bp.Slug}#{c} revisions", bAdmin, $"/v1/cases/{bp.CaseIds[c]}/revisions?limit=1", "id",
                    Fixed(bp.CaseRevisions[c]), aAdmin, $"/v1/cases/{ap.CaseIds[c]}/revisions?limit=1"));
            }
        }

        return walks;
    }

    /// <summary>Org B's own non-list reads (the audit export stream): 200, and the text carries no org-A id.</summary>
    public static IReadOnlyList<RouteProbe> OwnStreams(IsolationWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var route = Get("/v1/orgs/{o}/audit/export");
        const string name = "org B's own audit export";
        var request = new ProbeRequest(HttpMethod.Get, $"/v1/orgs/{world.Acme2.Id}/audit/export");
        return [new(route, name, runner => runner.OwnReadAsync(route, name, OrgBAdmin(world), request))];
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
        OrgCase("GET", "/v1/orgs/{o}", o => new(HttpMethod.Get, $"/v1/orgs/{o}"), ownControl: true),
        OrgCase("PATCH", "/v1/orgs/{o}", o => new(HttpMethod.Patch, $"/v1/orgs/{o}", new JsonObject { ["name"] = "Intruder" }, "\"1\"")),
        OrgCase("POST", "/v1/orgs/{o}/projects", o => new(HttpMethod.Post, $"/v1/orgs/{o}/projects", new JsonObject { ["name"] = "intruder", ["slug"] = "intruder" })),
        OrgCase("GET", "/v1/orgs/{o}/projects", o => new(HttpMethod.Get, $"/v1/orgs/{o}/projects")),
        ProjectCase("GET", "/v1/projects/{p}", p => new(HttpMethod.Get, $"/v1/projects/{p}"), ownControl: true),
        ProjectCase("PATCH", "/v1/projects/{p}", p => new(HttpMethod.Patch, $"/v1/projects/{p}", new JsonObject { ["name"] = "intruder" }, "\"1\"")),

        // ---- Authoring (Authoring endpoints, 13.1.1-T2) ------------------------------------------------------
        // The feature tree is project-scoped and the authoring module's own gate answers 403
        // `project_scope_mismatch` for a credential scoped to a sibling project of the same org (a hole RLS alone
        // does not close, because the authoring tables are org-scoped). Every mutation needs `Idempotency-Key`
        // and the PATCH/DELETE/reorder/state/item routes additionally carry `If-Match`.
        ProjectCase("GET", "/v1/projects/{p}/authoring/tree", p => new(HttpMethod.Get, $"/v1/projects/{p}/authoring/tree?limit=1"),
            list: true, ownControl: true),
        ProjectCase("POST", "/v1/projects/{p}/authoring/nodes", p => new(HttpMethod.Post, $"/v1/projects/{p}/authoring/nodes",
            new JsonObject { ["kind"] = "module", ["title"] = "intruder module" })),
        ObjectCaseWith("PATCH", "/v1/projects/{p}/authoring/nodes/{n}", p => [("node", p.AuthoringChildNodeId)],
            (id, p) => new(HttpMethod.Patch, $"/v1/projects/{p.Id}/authoring/nodes/{id}",
                new JsonObject { ["title"] = "intruder" }, "\"1\""), (_, _) => []),
        ObjectCaseWith("DELETE", "/v1/projects/{p}/authoring/nodes/{n}", p => [("node", p.AuthoringChildNodeId)],
            (id, p) => new(HttpMethod.Delete, $"/v1/projects/{p.Id}/authoring/nodes/{id}", null, "\"1\""), (_, _) => []),
        ObjectCaseWith("POST", "/v1/projects/{p}/authoring/nodes/{n}/reorder", p => [("node", p.AuthoringChildNodeId)],
            (id, p) => new(HttpMethod.Post, $"/v1/projects/{p.Id}/authoring/nodes/{id}/reorder",
                new JsonObject { ["ordered_ids"] = new JsonArray { id } }, "\"order:sha256:" + new string('0', 64) + "\""), (_, _) => []),
        ProjectCase("GET", "/v1/projects/{p}/authoring/checklist-items", p => new(HttpMethod.Get,
            $"/v1/projects/{p}/authoring/checklist-items?limit=1"), list: true, ownControl: true),
        ProjectCase("POST", "/v1/projects/{p}/authoring/checklist-items", p => new(HttpMethod.Post,
            $"/v1/projects/{p}/authoring/checklist-items",
            new JsonObject { ["node_id"] = p, ["title"] = "intruder item", ["priority"] = "high" })),
        ObjectCaseWith("PATCH", "/v1/projects/{p}/authoring/checklist-items/{i}", p => [("item", p.AuthoringItemId)],
            (id, p) => new(HttpMethod.Patch, $"/v1/projects/{p.Id}/authoring/checklist-items/{id}",
                new JsonObject { ["title"] = "intruder" }, "\"1\""), (_, _) => []),
        ObjectCaseWith("DELETE", "/v1/projects/{p}/authoring/checklist-items/{i}", p => [("item", p.AuthoringItemId)],
            (id, p) => new(HttpMethod.Delete, $"/v1/projects/{p.Id}/authoring/checklist-items/{id}", null, "\"1\""), (_, _) => []),
        ProjectCase("GET", "/v1/projects/{p}/authoring/manual-cases", p => new(HttpMethod.Get,
            $"/v1/projects/{p}/authoring/manual-cases?limit=1"), list: true, ownControl: true),
        ProjectCase("POST", "/v1/projects/{p}/authoring/manual-cases", p => new(HttpMethod.Post,
            $"/v1/projects/{p}/authoring/manual-cases", new JsonObject
            {
                ["checklist_item_id"] = p,
                ["title"] = "intruder case",
                ["steps"] = new JsonArray { new JsonObject { ["no"] = 1, ["action"] = "open", ["expected"] = "shown" } },
                ["priority"] = "high",
            })),
        ObjectCaseWith("GET", "/v1/projects/{p}/authoring/manual-cases/{m}", p => [("case", p.AuthoringManualCaseId)],
            (id, p) => new(HttpMethod.Get, $"/v1/projects/{p.Id}/authoring/manual-cases/{id}"), (_, _) => [],
            own: p => new(HttpMethod.Get, $"/v1/projects/{p.Id}/authoring/manual-cases/{p.AuthoringManualCaseId}")),
        ObjectCaseWith("PATCH", "/v1/projects/{p}/authoring/manual-cases/{m}", p => [("case", p.AuthoringManualCaseId)],
            (id, p) => new(HttpMethod.Patch, $"/v1/projects/{p.Id}/authoring/manual-cases/{id}",
                new JsonObject { ["title"] = "intruder" }, "\"1\""), (_, _) => []),
        ObjectCaseWith("DELETE", "/v1/projects/{p}/authoring/manual-cases/{m}", p => [("case", p.AuthoringManualCaseId)],
            (id, p) => new(HttpMethod.Delete, $"/v1/projects/{p.Id}/authoring/manual-cases/{id}", null, "\"1\""), (_, _) => []),
        ObjectCaseWith("POST", "/v1/projects/{p}/authoring/manual-cases/{m}/state", p => [("case", p.AuthoringManualCaseId)],
            (id, p) => new(HttpMethod.Post, $"/v1/projects/{p.Id}/authoring/manual-cases/{id}/state",
                new JsonObject { ["state"] = "approved" }, "\"1\""), (_, _) => []),
        ObjectCaseWith("POST", "/v1/projects/{p}/authoring/manual-cases/{m}/item", p => [("case", p.AuthoringManualCaseId)],
            (id, p) => new(HttpMethod.Post, $"/v1/projects/{p.Id}/authoring/manual-cases/{id}/item",
                new JsonObject { ["checklist_item_id"] = p.AuthoringItemId }, "\"1\""), (_, _) => []),

        // 13.1.1-T3: the version timeline (routes 22 and 23) and the four restore routes (6, 11, 18, 24). A tombstone's
        // `If-Match` is the version the delete left and the seed reads back through route 22 (IsolationWorld), so a
        // restore's own control proves the route accepts a real tombstone at the tag it keeps; route 24 declares no
        // `If-Match` — the version id names the state to re-apply — so its probe carries none.
        ObjectCaseWith("POST", "/v1/projects/{p}/authoring/nodes/{n}/restore",
            p => [("soft-deleted node", p.AuthoringDeletedNodeId)],
            (id, p) => new(HttpMethod.Post, $"/v1/projects/{p.Id}/authoring/nodes/{id}/restore",
                IfMatch: VersionTag(p.AuthoringDeletedNodeVersion)),
            (_, _) => [],
            own: p => new(HttpMethod.Post, $"/v1/projects/{p.Id}/authoring/nodes/{p.AuthoringDeletedNodeId}/restore",
                IfMatch: VersionTag(p.AuthoringDeletedNodeVersion))),
        ObjectCaseWith("POST", "/v1/projects/{p}/authoring/checklist-items/{i}/restore",
            p => [("soft-deleted item", p.AuthoringDeletedItemId)],
            (id, p) => new(HttpMethod.Post, $"/v1/projects/{p.Id}/authoring/checklist-items/{id}/restore",
                IfMatch: VersionTag(p.AuthoringDeletedItemVersion)),
            (_, _) => [],
            own: p => new(HttpMethod.Post, $"/v1/projects/{p.Id}/authoring/checklist-items/{p.AuthoringDeletedItemId}/restore",
                IfMatch: VersionTag(p.AuthoringDeletedItemVersion))),
        ObjectCaseWith("POST", "/v1/projects/{p}/authoring/manual-cases/{m}/restore",
            p => [("soft-deleted case", p.AuthoringDeletedManualCaseId)],
            (id, p) => new(HttpMethod.Post, $"/v1/projects/{p.Id}/authoring/manual-cases/{id}/restore",
                IfMatch: VersionTag(p.AuthoringDeletedManualCaseVersion)),
            (_, _) => [],
            own: p => new(HttpMethod.Post, $"/v1/projects/{p.Id}/authoring/manual-cases/{p.AuthoringDeletedManualCaseId}/restore",
                IfMatch: VersionTag(p.AuthoringDeletedManualCaseVersion))),
        ProjectCase("GET", "/v1/projects/{p}/authoring/versions", p => new(HttpMethod.Get,
            $"/v1/projects/{p}/authoring/versions?limit=1"), list: true, ownControl: true),
        // 13.1.1-T4: the manual-case link pair (routes 20, 21). A foreign org's case id is 404 like every other
        // object id; the body names a live catalog case of the same project, the shape the seeder links.
        ObjectCaseWith("POST", "/v1/projects/{p}/authoring/manual-cases/{m}/links", p => [("case", p.AuthoringManualCaseId)],
            (id, p) => new(HttpMethod.Post, $"/v1/projects/{p.Id}/authoring/manual-cases/{id}/links",
                new JsonObject { ["test_case_id"] = p.CaseIds[0].ToString("D") }), (_, _) => []),
        ObjectCaseWith("DELETE", "/v1/projects/{p}/authoring/manual-cases/{m}/links/{c}", p => [("case", p.AuthoringManualCaseId)],
            (id, p) => new(HttpMethod.Delete, $"/v1/projects/{p.Id}/authoring/manual-cases/{id}/links/{p.CaseIds[0]}"), (_, _) => []),
        ObjectCaseWith("GET", "/v1/projects/{p}/authoring/versions/{v}", p => [("version", p.AuthoringVersionId)],
            (id, p) => new(HttpMethod.Get, $"/v1/projects/{p.Id}/authoring/versions/{id}"), (_, _) => [],
            own: p => new(HttpMethod.Get, $"/v1/projects/{p.Id}/authoring/versions/{p.AuthoringVersionId}")),
        ObjectCaseWith("POST", "/v1/projects/{p}/authoring/versions/{v}/restore", p => [("version", p.AuthoringVersionId)],
            (id, p) => new(HttpMethod.Post, $"/v1/projects/{p.Id}/authoring/versions/{id}/restore"), (_, _) => [],
            own: p => new(HttpMethod.Post, $"/v1/projects/{p.Id}/authoring/versions/{p.AuthoringVersionId}/restore")),

        // The pending-changes queue names a project in {p} and a real object of that project in its body: the queue
        // names an item, the detail and discard name a queued change, and the apply selection names that change.
        // A probe whose body carried the project id as its target would be refused by request validation rather
        // than by tenancy and would prove nothing. Both write routes prove their own project's request succeeds — 201
        // for the queue and 200 for an apply of the change it queued — on the project's second item: a comment changes
        // no item and writes no version, so the seeded change and every version tag the other controls send stay valid.
        ProjectCase("GET", "/v1/projects/{p}/authoring/changes", p => new(HttpMethod.Get,
            $"/v1/projects/{p}/authoring/changes?limit=1"), list: true, ownControl: true),
        ProjectBodyCase("POST", "/v1/projects/{p}/authoring/changes",
            p => QueueBody(p.Id, p.AuthoringItemId),
            () => QueueBody(Guid.CreateVersion7(), Guid.CreateVersion7()),
            (route, w) => new(route, "own project queues a change answers 201", runner => runner.OwnObjectAsync(
                route, "own project queues a change answers 201", OrgBAdmin(w),
                QueueBody(w.Acme2.Projects[0].Id, w.Acme2.Projects[0].AuthoringSecondItemId), expectedStatus: 201))),
        ObjectCaseWith("GET", "/v1/projects/{p}/authoring/changes/{c}", p => [("queued change", p.AuthoringChangeId)],
            (id, p) => new(HttpMethod.Get, $"/v1/projects/{p.Id}/authoring/changes/{id}"), (_, _) => [],
            own: p => new(HttpMethod.Get, $"/v1/projects/{p.Id}/authoring/changes/{p.AuthoringChangeId}")),
        ObjectCaseWith("POST", "/v1/projects/{p}/authoring/changes/{c}/discard", p => [("queued change", p.AuthoringChangeId)],
            (id, p) => new(HttpMethod.Post, $"/v1/projects/{p.Id}/authoring/changes/{id}/discard"), (_, _) => [],
            own: p => new(HttpMethod.Post, $"/v1/projects/{p.Id}/authoring/changes/{p.AuthoringChangeId}/discard")),
        ProjectBodyCase("POST", "/v1/projects/{p}/authoring/changes:apply",
            p => ApplyBody(p.Id, p.AuthoringChangeId),
            () => ApplyBody(Guid.CreateVersion7(), Guid.CreateVersion7()),
            (route, w) => new(route, "own project applies the change it queued answers 200", runner => runner.OwnQueuedApplyAsync(
                route, "own project applies the change it queued answers 200", OrgBAdmin(w),
                w.Acme2.Projects[0].Id, QueueBody(w.Acme2.Projects[0].Id, w.Acme2.Projects[0].AuthoringSecondItemId)))),

        // 13.2.1-T1: the project AI-settings edge. Every route names the project only, so the foreign-project probe is the
        // ordinary one; the save is a well-formed first save (If-Match "0" tags the default) and has no own control
        // because a granted probe would write. The history detail takes the project's version 1, which no project in the
        // world has, so it answers 404 for the org's own project too — the same reason the authoring version detail
        // carries no own control here.
        ProjectCase("GET", "/v1/projects/{p}/ai-settings", p => new(HttpMethod.Get,
            $"/v1/projects/{p}/ai-settings"), ownControl: true),
        ProjectCase("PUT", "/v1/projects/{p}/ai-settings", p => new(HttpMethod.Put,
            $"/v1/projects/{p}/ai-settings",
            new JsonObject
            {
                ["settings"] = new JsonObject
                {
                    ["description"] = string.Empty,
                    ["environment_url"] = null,
                    ["languages"] = new JsonArray("en", "vi"),
                    ["writing_style"] = string.Empty,
                    ["coverage_strategy"] = string.Empty,
                    ["priority_criteria"] = new JsonObject
                    {
                        ["critical"] = string.Empty,
                        ["high"] = string.Empty,
                        ["medium"] = string.Empty,
                        ["low"] = string.Empty,
                    },
                    ["exclusion_rules"] = string.Empty,
                    ["step_detail"] = string.Empty,
                },
                ["template"] = null,
                ["reason"] = null,
            },
            IfMatch: "\"0\"")),
        ProjectCase("GET", "/v1/projects/{p}/ai-settings/versions", p => new(HttpMethod.Get,
            $"/v1/projects/{p}/ai-settings/versions?limit=1"), list: true, ownControl: true),
        ProjectCase("GET", "/v1/projects/{p}/ai-settings/versions/{version_no}", p => new(HttpMethod.Get,
            $"/v1/projects/{p}/ai-settings/versions/1")),
        ProjectCase("GET", "/v1/projects/{p}/ai-settings/templates", p => new(HttpMethod.Get,
            $"/v1/projects/{p}/ai-settings/templates"), ownControl: true),

        // 13.1.1-T5: route 25, the import/export pair. The import probe is a dry run of a one-row JSON document, so a
        // caller who turned out to be authorized would still write nothing; an org-B credential naming an org-A project
        // is invisible to the project probe, exactly like the tree routes above (404 `not_found`).
        ProjectCase("POST", "/v1/projects/{p}/authoring/import", p => new(HttpMethod.Post,
            $"/v1/projects/{p}/authoring/import?format=json&dry_run=true", ImportDocument())),
        ProjectCase("GET", "/v1/projects/{p}/authoring/export", p => new(HttpMethod.Get,
            $"/v1/projects/{p}/authoring/export?format=csv")),

        // ---- Membership (MembershipEndpoints.cs:49-75) ---------------------------------------------------------
        OrgCase("POST", "/v1/orgs/{o}/memberships", o => new(HttpMethod.Post, $"/v1/orgs/{o}/memberships", Invite())),
        OrgCase("GET", "/v1/orgs/{o}/memberships", o => new(HttpMethod.Get, $"/v1/orgs/{o}/memberships"),
            // S1: the route's {o} wins over X-Evidgate-Org, so {o}=B with header A lists org B's memberships only.
            // The reverse ({o}=A with header B) is the "org A named in {o}" pair above.
            extra: (route, w) => [new(route, "route {o} wins over X-Evidgate-Org", runner => runner.OwnListAsync(
                route,
                "route {o} wins over X-Evidgate-Org",
                ForeignHeaderCaller(w),
                new(HttpMethod.Get, $"/v1/orgs/{w.Acme2.Id}/memberships?limit=200"),
                [.. w.Acme2.MembershipIds.Select(id => id.ToString("D"))]))]),
        MembershipCase("GET", "/v1/orgs/{o}/memberships/{m}", (o, m, _) => new(HttpMethod.Get, $"/v1/orgs/{o}/memberships/{m}"), ownControl: true),
        MembershipCase("PATCH", "/v1/orgs/{o}/memberships/{m}", (o, m, _) =>
            new(HttpMethod.Patch, $"/v1/orgs/{o}/memberships/{m}", new JsonObject { ["status"] = "removed" }, "\"1\"")),
        MembershipCase("DELETE", "/v1/orgs/{o}/memberships/{m}", (o, m, _) => new(HttpMethod.Delete, $"/v1/orgs/{o}/memberships/{m}")),
        MembershipCase("POST", "/v1/orgs/{o}/memberships/{m}/roles", (o, m, w) =>
            new(HttpMethod.Post, $"/v1/orgs/{o}/memberships/{m}/roles", new JsonObject { ["role_id"] = w.BuiltInRoleIds["viewer"].ToString() })),
        MembershipCase("POST", "/v1/orgs/{o}/memberships/{m}/resend", (o, m, _) =>
            new(HttpMethod.Post, $"/v1/orgs/{o}/memberships/{m}/resend")),
        MembershipCase("POST", "/v1/orgs/{o}/memberships/{m}/cancel-invite", (o, m, _) =>
            new(HttpMethod.Post, $"/v1/orgs/{o}/memberships/{m}/cancel-invite")),
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

        // ---- Security events (SecurityEventEndpoints.cs:55-66; 11.5.1-T5.2) -------------------------------------
        // {o} is the tenant the read runs in, and both halves of the case are refused at tenant resolution before
        // the handler runs: a member with no active membership in {o} is 403 `forbidden` (the same answer an org
        // that does not exist gives, TenantResolver.cs:64-70), a service credential naming another org is 404
        // `not_found` (TenantResolver.cs:84-85). The query's own org mismatch check adds the same 404 backstop for
        // a resolved tenant that differs from {o}; the rows are invisible to RLS besides.
        OrgCase("GET", "/v1/orgs/{o}/security-events", o => new(HttpMethod.Get, $"/v1/orgs/{o}/security-events")),

        // ---- Usage (UsageEndpoints.cs): {o}'s counters, refused at tenant resolution as above -------------------
        // The summary's own org check and RLS add the same 404 backstop the security-events read carries.
        OrgCase("GET", "/v1/orgs/{o}/usage/summary", o => new(HttpMethod.Get, $"/v1/orgs/{o}/usage/summary")),

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

        // ---- Federated login (LoginEndpoints.cs:22-34, 02.3.1-T1.1): pre-tenant, no tenant data, no database -----
        // The isolation host configures no provider credential, so the two designed answers are exact and provable:
        // start refuses with 503 unavailable (unconfigured provider), callback with 401 token_invalid (no pending cookie).
        Case("GET", "/v1/auth/oidc/{provider}/start", (route, w) =>
        [
            new(route, "start stays pre-tenant: org B's member headers cannot select a tenant", runner => runner.PreTenantLoginAsync(
                route,
                "start stays pre-tenant: org B's member headers cannot select a tenant",
                w,
                "/v1/auth/oidc/google/start",
                StatusCodes.Status503ServiceUnavailable,
                UnavailableCode)),
        ]),
        Case("GET", "/v1/auth/oidc/{provider}/callback", (route, w) =>
        [
            new(route, "callback stays pre-tenant: org B's member headers cannot select a tenant", runner => runner.PreTenantLoginAsync(
                route,
                "callback stays pre-tenant: org B's member headers cannot select a tenant",
                w,
                "/v1/auth/oidc/google/callback?code=probe&state=probe",
                StatusCodes.Status401Unauthorized,
                TokenInvalidCode)),
        ]),

        // ---- Invite staging + explicit linking (InviteTokenEndpoints.cs, LinkEndpoints.cs; 02.3.1-T2) ------------
        // Pre-tenant like the login routes: none of them resolves a tenant, so org B's member headers can change
        // nothing. The invite route accepts a body with the token's SHAPE and answers 204 plus the
        // `__Host-evidgate_invite` cookie (nothing is looked up: whether the token exists is the binder's business
        // at login); the four link routes require a live session (02.3.2-T1), so this host's real session
        // authenticator answers 401 `unauthenticated` to an anonymous caller and to org B's member headers alike,
        // before any tenant, pepper or row is read (SessionRequestAuthenticator.cs:87-91).
        Case("POST", "/v1/auth/invite-token", (route, w) =>
        [
            new(route, "invite staging stays pre-tenant: org B's member headers cannot select a tenant", runner => runner.PreTenantAnswerAsync(
                route,
                "invite staging stays pre-tenant: org B's member headers cannot select a tenant",
                w,
                new ProbeRequest(HttpMethod.Post, "/v1/auth/invite-token", new JsonObject
                {
                    ["invite_token"] = InviteTokenProbe,
                }),
                StatusCodes.Status204NoContent,
                expectedCode: null)),
        ]),
        Case("POST", "/v1/auth/link/{provider}", (route, w) =>
        [
            new(route, "link start stays pre-tenant: org B's member headers cannot select a tenant", runner => runner.PreTenantAnswerAsync(
                route,
                "link start stays pre-tenant: org B's member headers cannot select a tenant",
                w,
                new ProbeRequest(HttpMethod.Post, "/v1/auth/link/google"),
                StatusCodes.Status401Unauthorized,
                UnauthenticatedCode)),
        ]),
        Case("GET", "/v1/auth/link/pending", (route, w) =>
        [
            new(route, "staged link read stays pre-tenant: org B's member headers cannot select a tenant", runner => runner.PreTenantAnswerAsync(
                route,
                "staged link read stays pre-tenant: org B's member headers cannot select a tenant",
                w,
                new ProbeRequest(HttpMethod.Get, "/v1/auth/link/pending"),
                StatusCodes.Status401Unauthorized,
                UnauthenticatedCode)),
        ]),
        Case("DELETE", "/v1/auth/link/pending", (route, w) =>
        [
            new(route, "staged link discard stays pre-tenant: org B's member headers cannot select a tenant", runner => runner.PreTenantAnswerAsync(
                route,
                "staged link discard stays pre-tenant: org B's member headers cannot select a tenant",
                w,
                new ProbeRequest(HttpMethod.Delete, "/v1/auth/link/pending"),
                StatusCodes.Status401Unauthorized,
                UnauthenticatedCode)),
        ]),
        Case("POST", "/v1/auth/link/confirm", (route, w) =>
        [
            new(route, "link confirm stays pre-tenant: org B's member headers cannot select a tenant", runner => runner.PreTenantAnswerAsync(
                route,
                "link confirm stays pre-tenant: org B's member headers cannot select a tenant",
                w,
                new ProbeRequest(HttpMethod.Post, "/v1/auth/link/confirm"),
                StatusCodes.Status401Unauthorized,
                UnauthenticatedCode)),
        ]),

        // 02.3.2-T2.1's session surface (02.3.2 §5): the caller IS the session the presented cookie proves, resolved
        // inside the definer from its HMAC, so org B's member headers cannot select a tenant on any of the four
        // routes. With no session cookie the request authenticator answers 401 `unauthenticated` before the handler
        // infers a tenant, and the antiforgery middleware does not even apply without a session cookie
        // (SessionXsrfPolicy.IsInScope requires one), so the three mutations reach the same 401 as the read.
        Case("GET", "/v1/auth/sessions", (route, w) =>
        [
            new(route, "session list stays pre-tenant: org B's member headers cannot select a tenant", runner => runner.PreTenantAnswerAsync(
                route,
                "session list stays pre-tenant: org B's member headers cannot select a tenant",
                w,
                new ProbeRequest(HttpMethod.Get, "/v1/auth/sessions"),
                StatusCodes.Status401Unauthorized,
                UnauthenticatedCode)),
        ]),
        Case("DELETE", "/v1/auth/sessions/{s}", (route, w) =>
        [
            new(route, "session revoke stays pre-tenant: org B's member headers cannot select a tenant", runner => runner.PreTenantAnswerAsync(
                route,
                "session revoke stays pre-tenant: org B's member headers cannot select a tenant",
                w,
                new ProbeRequest(HttpMethod.Delete, $"/v1/auth/sessions/{Guid.CreateVersion7()}"),
                StatusCodes.Status401Unauthorized,
                UnauthenticatedCode)),
        ]),
        Case("POST", "/v1/auth/sessions/revoke-all", (route, w) =>
        [
            new(route, "revoke-all stays pre-tenant: org B's member headers cannot select a tenant", runner => runner.PreTenantAnswerAsync(
                route,
                "revoke-all stays pre-tenant: org B's member headers cannot select a tenant",
                w,
                new ProbeRequest(HttpMethod.Post, "/v1/auth/sessions/revoke-all"),
                StatusCodes.Status401Unauthorized,
                UnauthenticatedCode)),
        ]),
        Case("POST", "/v1/auth/logout", (route, w) =>
        [
            new(route, "logout stays pre-tenant: org B's member headers cannot select a tenant", runner => runner.PreTenantAnswerAsync(
                route,
                "logout stays pre-tenant: org B's member headers cannot select a tenant",
                w,
                new ProbeRequest(HttpMethod.Post, "/v1/auth/logout"),
                StatusCodes.Status401Unauthorized,
                UnauthenticatedCode)),
        ]),

        // 02.3.1-T3's own profile (02.3.1 §5): the caller is the session the presented cookie proves, resolved
        // inside the definer from its HMAC, so org B's member headers cannot select a tenant on either route and
        // neither template carries a user id. Without a session cookie the request authenticator answers 401
        // `unauthenticated` before the handler runs, and the write adds nothing of its own: the antiforgery
        // middleware does not apply without a session cookie, so it reaches the same 401 as the read.
        Case("GET", "/v1/me", (route, w) =>
        [
            new(route, "own profile stays pre-tenant: org B's member headers cannot select a tenant", runner => runner.PreTenantAnswerAsync(
                route,
                "own profile stays pre-tenant: org B's member headers cannot select a tenant",
                w,
                new ProbeRequest(HttpMethod.Get, "/v1/me"),
                StatusCodes.Status401Unauthorized,
                UnauthenticatedCode)),
        ]),
        Case("PATCH", "/v1/me", (route, w) =>
        [
            new(route, "own profile write stays pre-tenant: org B's member headers cannot select a tenant", runner => runner.PreTenantAnswerAsync(
                route,
                "own profile write stays pre-tenant: org B's member headers cannot select a tenant",
                w,
                new ProbeRequest(HttpMethod.Patch, "/v1/me", new JsonObject { ["preferred_locale"] = "en" }, "\"1\""),
                StatusCodes.Status401Unauthorized,
                UnauthenticatedCode)),
        ]),

        // ---- Catalog (CatalogEndpoints.cs:39-83) ---------------------------------------------------------------
        ProjectCase("GET", "/v1/projects/{p}/cases", p => new(HttpMethod.Get, $"/v1/projects/{p}/cases"), list: true),
        ObjectCase("GET", "/v1/cases/{c}", p => Indexed("case", p.CaseIds), id => new(HttpMethod.Get, $"/v1/cases/{id}"), ownControl: true),
        ObjectCase("PATCH", "/v1/cases/{c}", p => Indexed("case", p.CaseIds), id =>
            new(HttpMethod.Patch, $"/v1/cases/{id}", new JsonObject { ["name"] = "intruder" }, "\"1\"")),
        ObjectCaseWith("PATCH", "/v1/projects/{p}/cases/{c}", p => Indexed("case", p.CaseIds),
            (id, p) => new(HttpMethod.Patch, $"/v1/projects/{p.Id}/cases/{id}",
                new JsonObject { ["requirement_ref"] = "REQ-isolation" }), (_, _) => []),
        ObjectCase("GET", "/v1/cases/{c}/revisions", p => Indexed("case", p.CaseIds), id => new(HttpMethod.Get, $"/v1/cases/{id}/revisions"), ownControl: true),
        ProjectCase("POST", "/v1/projects/{p}/suites", p => new(HttpMethod.Post, $"/v1/projects/{p}/suites", new JsonObject { ["name"] = "intruder", ["slug"] = "intruder" })),
        ProjectCase("GET", "/v1/projects/{p}/suites", p => new(HttpMethod.Get, $"/v1/projects/{p}/suites"), list: true),
        ObjectCase("GET", "/v1/suites/{s}", p => Indexed("suite", p.SuiteIds), id => new(HttpMethod.Get, $"/v1/suites/{id}"), ownControl: true),
        SuiteRevisionCreateCase(),
        ObjectCase("GET", "/v1/suite-revisions/{r}", p => Indexed("suite revision", p.SuiteRevisionIds), id => new(HttpMethod.Get, $"/v1/suite-revisions/{id}"), ownControl: true),
        SuiteRevisionDiffCase(),
        ProjectCase("POST", "/v1/projects/{p}/fixtures", p => new(HttpMethod.Post, $"/v1/projects/{p}/fixtures",
            new JsonObject { ["fixture_key"] = "intruder", ["seed_version"] = "v1", ["body_ref"] = "seeds/intruder.json" })),
        ProjectCase("GET", "/v1/projects/{p}/fixtures", p => new(HttpMethod.Get, $"/v1/projects/{p}/fixtures"), list: true),
        ObjectCase("GET", "/v1/fixtures/{f}", p => Indexed("fixture", p.FixtureIds), id => new(HttpMethod.Get, $"/v1/fixtures/{id}"), ownControl: true),
        ObjectCase("POST", "/v1/fixtures/{f}/revisions", p => Indexed("fixture", p.FixtureIds), id =>
            new(HttpMethod.Post, $"/v1/fixtures/{id}/revisions", new JsonObject { ["seed_version"] = "v2", ["body_ref"] = "seeds/v2.json" })),

        // ---- Orchestrator (OrchestratorEndpoints.cs:37-47) -----------------------------------------------------
        ManifestCase(),
        RunCreateCase(),
        ProjectCase("POST", "/v1/projects/{p}/environments/{e}/revisions", p =>
            new(HttpMethod.Post, $"/v1/projects/{p}/environments/{IsolationWorld.EnvironmentKey}/revisions", Environment())),
        ObjectCase("GET", "/v1/environment-revisions/{id}", p => [("environment revision", p.EnvironmentRevisionId)], id =>
            new(HttpMethod.Get, $"/v1/environment-revisions/{id}"), ownControl: true),
        // 05.3.1-T1: the run is addressed by id alone, so the tenant scope is the RLS read — another org's run is the
        // same 404 not_found as a random id. A project-scoped credential asking for a sibling project's run of the same
        // org is that same 404 not_found too: GetRun maps OrchestratorScope.Project's scope mismatch to it (the OrchestratorScope.Project check in GetRun.cs),
        // so project_scope_mismatch is never the answer here. The one 403 is the permission stage's, for a service/
        // ci_producer principal, which can never hold run:read (outside the service-role ceiling) — hence the service
        // probe's allowed: [403, 404] below.
        ObjectCase("GET", "/v1/runs/{r}", p => [("run", p.RunId)], id =>
            new(HttpMethod.Get, $"/v1/runs/{id}"), ownControl: true),
        // F52 (S143): the run's attempts page is the same read shape — the run is addressed by id alone, so another
        // org's run is the 404 not_found of GET /v1/runs/{r} (never a 403). A project-scoped/service credential never
        // reaches the handler at all: run:read is outside the service-role ceiling (PermissionEvaluator.Service), so
        // the permission stage refuses it 403 forbidden — hence the service probes' allowed: [403, 404] below. The
        // handler's sibling-project mapping (OrchestratorScope.Project's scope mismatch → that same 404,
        // ListRunAttempts.cs) is therefore defensive and unreachable over HTTP. The own-project control answers 200
        // with an empty page because the world seeds no case_attempt row, which also proves the probe URL matches a
        // served route.
        ObjectCase("GET", "/v1/runs/{r}/attempts", p => [("run", p.RunId)], id =>
            new(HttpMethod.Get, $"/v1/runs/{id}/attempts"), ownControl: true),
        // The run's evidence-revision page is the same read shape: the run is addressed by id alone, so another org's
        // run is the 404 not_found of GET /v1/runs/{r} (never a 403), and a project-scoped/service credential is
        // refused 403 by the permission stage because run:read is outside the service-role ceiling. The own-project
        // control answers 200 with an empty page because the world freezes no revision, which also proves the probe
        // URL matches a served route.
        ObjectCase("GET", "/v1/runs/{run_id}/evidence-revisions", p => [("run", p.RunId)], id =>
            new(HttpMethod.Get, $"/v1/runs/{id}/evidence-revisions"), ownControl: true),
        // The run's decision is the same read shape: the run is addressed by id alone and resolved under RLS, so
        // another org's run is the 404 not_found of GET /v1/runs/{r} (never a 403 leaking existence). The own-project
        // control answers 200 with the pending body because the world stores no evaluation.
        ObjectCase("GET", "/v1/runs/{r}/decision", p => [("run", p.RunId)], id =>
            new(HttpMethod.Get, $"/v1/runs/{id}/decision"), ownControl: true),
        // The report comparison names the run and the report. The world stores no raw_report row and no served route
        // creates one, so the report id is a fresh one: another tenant's report, an unknown id and a report whose run
        // is not the one in the path all take the one 404 not_found envelope (never a 403 that would name existence),
        // and run:read is outside the service-role ceiling, so a project-scoped or service credential is refused by
        // the permission stage before the handler.
        ObjectCase("GET", "/v1/runs/{r}/reports/{report_id}/comparison", p => [("run", p.RunId)], id =>
            new(HttpMethod.Get, $"/v1/runs/{id}/reports/{Guid.CreateVersion7()}/comparison")),
        // The release pair. The create names its project in {p}, so its body must carry that project's own
        // environment revision (a foreign project is refused before the body is validated); the read names the
        // release alone, so its tenant scope is the RLS read and another tenant's release is the 404
        // release_not_found, never a 403 that would name existence.
        ReleaseCreateCase(),
        ReleaseReadCase(),
        // The bind route names the release in its path and the run in its body, so both sides are resolved under the
        // caller's tenant: another org's release and another org's run are the same 404 not_found, and run:create is
        // outside the service-role ceiling, so a ci_producer is refused before the handler.
        ReleaseBindCase(),
        // 05.4.1-T1: the cancel request names the run in its path, so the run — not the body — decides the
        // tenant. Another org's run is the 404 not_found of GET /v1/runs/{r} (never a 403 leaking existence), and
        // run:cancel sits outside the service-role ceiling, so the app and sibling-project service probes below are
        // refused before the handler (ProbeExpect.ServiceRefused / allowed: [403, 404]). No own control, the
        // 13.2.1-T1 AI-settings precedent: a granted cancel would write a request row and move the run, which
        // would leave this suite's world mutated for every later probe (the accepted 202 is covered by
        // CancelRunEndpointTests instead).
        ObjectCase("POST", "/v1/runs/{r}/cancel", p => [("run", p.RunId)], id =>
            new(HttpMethod.Post, $"/v1/runs/{id}/cancel", new JsonObject { ["reason"] = "isolation probe" })),
        // The reprocess command addresses the run in the path and the report in the body. The probe names a fresh
        // report id, so the foreign answer is the handler's one 404 for a report of a run the caller cannot see, and
        // the body never becomes an oracle over an existing report.
        ObjectCase("POST", "/v1/runs/{r}/reprocess", p => [("run", p.RunId)], id =>
            new(HttpMethod.Post, $"/v1/runs/{id}/reprocess", new JsonObject
            {
                ["raw_report_id"] = Guid.CreateVersion7().ToString(),
                ["parser_version"] = "1.1.0",
            })),
        // 05.2.2-T2: the project-scoped run list. As a list, its positive control is the full-pagination walk
        // (Lists); the extra probes pin the ref_name filter, which must not widen tenancy.
        ProjectCase("GET", "/v1/projects/{p}/runs", p => new(HttpMethod.Get, $"/v1/projects/{p}/runs"), list: true,
            foreignExpect: ProbeExpect.NotFound,
            extra: (route, w) =>
            [
                // ?ref_name=main on org A's project is the same refusal as on a random project (never an org-A
                // row, no existence oracle): the filter matches org A's seeded branch/main run, so a leak would
                // surface it here rather than returning an empty page.
                Pair(route, "filtered ?ref_name=main on org A's project", ProbeExpect.NotFound, OrgBAdmin(w),
                    new(HttpMethod.Get, $"/v1/projects/{w.Acme.Projects[0].Id}/runs?ref_name=main"),
                    new(HttpMethod.Get, $"/v1/projects/{Guid.CreateVersion7()}/runs?ref_name=main")),
                // The control: org B's own filtered page lists exactly its own branch/main run, never org A's.
                new(route, "filtered ?ref_name=main on own project lists only its own main-ref run", runner => runner.OwnListAsync(
                    route, "filtered ?ref_name=main on own project lists only its own main-ref run", OrgBAdmin(w),
                    new(HttpMethod.Get, $"/v1/projects/{w.Acme2.Projects[0].Id}/runs?ref_name=main"),
                    [w.Acme2.Projects[0].RunIds[0].ToString("D")], idField: "run_id")),
            ]),

        // ---- Policy (PolicyEndpoints.cs; 07.1.1-T3) -------------------------------------------------------------
        ProjectCase("POST", "/v1/projects/{p}/policies", p => new(HttpMethod.Post, $"/v1/projects/{p}/policies", Policy())),
        // 07.1.1-T4: the probe sends an unknown revision id, so every cross-tenant attempt is the same 404
        // (the route is project-scoped; the isolation suite asserts the 404, not the happy path).
        ProjectCase("POST", "/v1/projects/{p}/policy-activation", p => new(HttpMethod.Post, $"/v1/projects/{p}/policy-activation", Activation(Guid.CreateVersion7()))),
        // F18's two reads. Both hide a project the caller cannot read behind the same 404, so a foreign project is
        // never an existence oracle. Neither takes an own positive control: the world seeds a policy revision (so the
        // head route answers 200 there, which the sibling-project probe covers) but no `policy_activation` row, so an
        // own-project control on the activation read would assert a 200 the seed cannot produce — its own-project
        // answer is the same never-activated 404 the foreign probes expect.
        ProjectCase("GET", "/v1/projects/{p}/policy-activation", p => new(HttpMethod.Get, $"/v1/projects/{p}/policy-activation")),
        ProjectCase("GET", "/v1/projects/{p}/policies/head", p => new(HttpMethod.Get, $"/v1/projects/{p}/policies/head"), ownControl: true),
        ObjectCase("GET", "/v1/policies/{id}", p => [("policy revision", p.PolicyRevisionId)], id =>
            new(HttpMethod.Get, $"/v1/policies/{id}"), ownControl: true),
        ObjectCase("GET", "/v1/policies/{id}/revisions", p => [("policy revision", p.PolicyRevisionId)], id =>
            new(HttpMethod.Get, $"/v1/policies/{id}/revisions"), ownControl: true),

        // ---- Policy waivers (WaiverEndpoints.cs) ---------------------------------------------------------------
        WaiverCreateCase(),
        WaiverStateCase("approve"),
        WaiverStateCase("revoke"),
        ProjectCase("GET", "/v1/projects/{p}/waivers", p => new(HttpMethod.Get, $"/v1/projects/{p}/waivers"), list: true),

        // ---- Policy quarantine (QuarantineEndpoints.cs; 07.3.1-T2) ---------------------------------------------
        QuarantineCreateCase(),
        QuarantineRemoveCase(),
        ProjectCase("GET", "/v1/projects/{p}/quarantine", p => new(HttpMethod.Get, $"/v1/projects/{p}/quarantine"), list: true),

        // ---- Healing switch (HealingEndpoints.cs; 08.6.3-T1) ----------------------------------------------------
        HealingSwitchReadCase(),
        HealingSwitchSetCase(),

        // ---- Evidence (EvidenceEndpoints.cs:28; 06.1.2) ---------------------------------------------------------
        EvidenceUploadUrlCase(),
        EventBatchCase(),
        FinalizeShardCase(),
        EvidenceCompleteCase(),
        // 06.4.1-T1: the retention pair. The read answers the platform defaults for a project with no row (so the
        // own-project control is a real 200) and the same 404 for a foreign project; the upsert's body is sent by
        // every probe, but a foreign project is refused before the validation, so the windows never matter there.
        ProjectCase("GET", "/v1/projects/{p}/retention", p => new(HttpMethod.Get, $"/v1/projects/{p}/retention"), ownControl: true),
        ProjectCase("PUT", "/v1/projects/{p}/retention", p => new(HttpMethod.Put, $"/v1/projects/{p}/retention", Retention())),
        // The kind-policy upsert names its project in {p}, so it takes the project case — org A's project is
        // the same 404 an unknown one gets, and a token scoped to the sibling project is refused. The body is sent by
        // every probe because a foreign project is refused before the validator runs.
        ProjectCase("PUT", "/v1/projects/{p}/artifact-kind-policy/{kind}", p => new(HttpMethod.Put,
            $"/v1/projects/{p}/artifact-kind-policy/screenshot", KindPolicy())),
        // The capture-options read names its project in {p}, so it takes the same project case: a project with no
        // policy row answers every kind's default (a real 200 for the own-project control), and org A's project is the
        // same 404 an unknown one gets.
        ProjectCase("GET", "/v1/projects/{p}/artifact-kind-policy", p => new(HttpMethod.Get,
            $"/v1/projects/{p}/artifact-kind-policy"), ownControl: true),
        // The two artifact reads name no project: their project comes from the credential, so they are covered by
        // the case below rather than by ProjectCase.
        ArtifactReadCase("GET", "/v1/artifacts/{a}", a => new(HttpMethod.Get, $"/v1/artifacts/{a}")),
        ArtifactReadCase("GET", "/v1/artifacts/{a}/download-url", a => new(HttpMethod.Get, $"/v1/artifacts/{a}/download-url")),

    ];

    // ---------------------------------------------------------------------------------------------------- helpers
    /// <summary>
    /// A minimal valid <c>authoring-import.v1</c> document (one module node, no children): the import route refuses a
    /// body it cannot parse before the pipeline runs, so an isolation probe has to send a file the readers accept.
    /// </summary>
    private static JsonObject ImportDocument() => new()
    {
        ["format"] = "authoring-import",
        ["version"] = 1,
        ["rows"] = new JsonArray
        {
            new JsonObject
            {
                ["kind"] = "node",
                ["key"] = "n1",
                ["node_kind"] = "module",
                ["title"] = "intruder module",
            },
        },
    };

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

    /// <summary>
    /// A route whose tenant is <c>{o}</c>: org A's id against a random org id. <paramref name="ownControl"/> adds the
    /// positive control (org B's own <c>{o}</c> answers 200); <paramref name="extra"/> adds route-specific probes.
    /// A list route's positive control is its full-pagination walk (<c>Lists</c>).
    /// </summary>
    private static RouteCase OrgCase(
        string method,
        string template,
        Func<Guid, ProbeRequest> request,
        bool ownControl = false,
        Func<ApiRoute, IsolationWorld, IEnumerable<RouteProbe>>? extra = null) =>
        Case(
            method,
            template,
            (route, w) =>
            {
                var probes = new List<RouteProbe>
                {
                    Pair(route, "org A named in {o}", ProbeExpect.ForeignOrg, OrgBAdmin(w), request(w.Acme.Id), request(Guid.CreateVersion7())),
                };
                if (ownControl)
                {
                    probes.Add(OwnObject(route, "own org answers 200", OrgBAdmin(w), request(w.Acme2.Id)));
                }

                if (extra is not null)
                {
                    probes.AddRange(extra(route, w));
                }

                return probes;
            },
            (route, w) => [Pair(route, "org A named in {o}", ProbeExpect.ServiceRefused, OrgBService(w), request(w.Acme.Id), request(Guid.CreateVersion7()))]);

    /// <summary>
    /// A membership route: org A in <c>{o}</c> (tenant resolution refuses) and org B's own <c>{o}</c> with org A's
    /// membership in <c>{m}</c> (404). <paramref name="grants"/> uses the membership holding org A's extra grant.
    /// </summary>
    private static RouteCase MembershipCase(string method, string template, Func<Guid, Guid, IsolationWorld, ProbeRequest> request, bool grants = false, bool ownControl = false) =>
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
                if (ownControl)
                {
                    probes.Add(OwnObject(route, "own membership answers 200", OrgBAdmin(w), request(w.Acme2.Id, w.Acme2.Admin.MembershipId, w)));
                }

                return probes;
            },
            (route, w) =>
            [
                Pair(route, "org A named in {o}", ProbeExpect.ServiceRefused, OrgBService(w),
                    request(w.Acme.Id, w.Acme.Admin.MembershipId, w), request(Guid.CreateVersion7(), w.Acme.Admin.MembershipId, w)),
            ]);

    /// <summary>
    /// A route scoped by <c>X-Evidgate-Org</c> only: the header naming org A against a random org id, plus a
    /// <c>ci_producer</c> probe proving the header is ignored for service principals (S2). A GET list's positive
    /// control is its full-pagination walk (<c>Lists</c>).
    /// </summary>
    private static RouteCase HeaderCase(
        string method,
        string template,
        Func<IsolationWorld, ProbeRequest> request,
        Func<ApiRoute, IsolationWorld, RouteProbe>? ownOrg = null) =>
        Case(
            method,
            template,
            (route, w) =>
            {
                var aHeader = ForeignHeaderCaller(w);
                var randomHeader = RandomHeaderCaller(w);
                var probes = new List<RouteProbe>
                {
                    new(route, "org A named in X-Evidgate-Org", runner => runner.CallerPairAsync(route, "org A named in X-Evidgate-Org", aHeader, randomHeader, request(w))),
                };
                if (ownOrg is not null)
                {
                    probes.Add(ownOrg(route, w));
                }

                return probes;
            },
            (route, w) =>
            {
                var jwt = w.Acme2.Projects[0].CiProducerJwt;
                var withHeader = new ProbeCaller("org-B ci_producer (X-Evidgate-Org: A)", OrgHeader: w.Acme.Id, Bearer: jwt);
                var withoutHeader = ProbeCaller.Service("org-B ci_producer", jwt);
                return
                [
                    new(route, "service token ignores X-Evidgate-Org", runner => runner.ServiceHeaderIgnoredAsync(
                        route, "service token ignores X-Evidgate-Org", withHeader, withoutHeader, request(w))),
                ];
            });

    /// <summary>
    /// A route naming a project in <c>{p}</c>: as an org-B member, each org-A project against a random id; as org
    /// B's <c>ci_producer</c> (project <c>web</c>), the sibling project <c>api</c> (403, ScopeGuard) and org A's
    /// project against a random id. A list route's positive control is its full-pagination walk (<c>Lists</c>);
    /// <paramref name="extra"/> adds route-specific member probes (05.2.2-T2's filtered list).
    /// <paramref name="foreignExpect"/> overrides the member probe's expectation: a list route defaults to
    /// <see cref="ProbeExpect.EmptyOrNotFound"/>, but the 05.2.2-T2 run list always answers 404 for a foreign
    /// project (it checks project liveness), so it passes <see cref="ProbeExpect.NotFound"/>.
    /// </summary>
    private static RouteCase ProjectCase(string method, string template, Func<Guid, ProbeRequest> request, bool list = false, bool ownControl = false, Func<ApiRoute, IsolationWorld, IEnumerable<RouteProbe>>? extra = null, ProbeExpect? foreignExpect = null) =>
        Case(
            method,
            template,
            (route, w) =>
            {
                var probes = new List<RouteProbe>();
                probes.AddRange(w.Acme.Projects.Select(p => Pair(route, $"org A project {p.Slug} in {{p}}",
                    foreignExpect ?? (list ? ProbeExpect.EmptyOrNotFound : ProbeExpect.NotFound), OrgBAdmin(w), request(p.Id), request(Guid.CreateVersion7()))));
                probes.Add(new(route, "X-Evidgate-Org: A with an org-A project id", runner => runner.CallerPairAsync(
                    route, "X-Evidgate-Org: A with an org-A project id", ForeignHeaderCaller(w), RandomHeaderCaller(w), request(w.Acme.Projects[0].Id))));
                if (ownControl)
                {
                    probes.Add(OwnObject(route, "own project answers 200", OrgBAdmin(w), request(w.Acme2.Projects[0].Id)));
                }

                if (extra is not null)
                {
                    probes.AddRange(extra(route, w));
                }

                return probes;
            },
            (route, w) =>
            [
                new(route, "sibling project api of the token's org", runner => runner.ScopeMismatchAsync(
                    route, "sibling project api of the token's org", OrgBService(w), request(w.Acme2.Projects[1].Id),
                    requireScopeCode: route.Template.EndsWith("/manifests", StringComparison.Ordinal))),
                Pair(route, "org A project web in {p}", ProbeExpect.ServiceRefused, OrgBService(w), request(w.Acme.Projects[0].Id), request(Guid.CreateVersion7())),
                // The OIDC-minted token of project web carries the same scope, so it must be refused identically
                // (02.9.1-T3, hand-over from #19).
                new(route, "sibling project api of the OIDC token's org", runner => runner.ScopeMismatchAsync(
                    route, "sibling project api of the OIDC token's org", OrgBServiceOidc(w), request(w.Acme2.Projects[1].Id),
                    requireScopeCode: route.Template.EndsWith("/manifests", StringComparison.Ordinal))),
                Pair(route, "org A project web in {p} (OIDC token)", ProbeExpect.ServiceRefused, OrgBServiceOidc(w), request(w.Acme.Projects[0].Id), request(Guid.CreateVersion7())),
            ]);

    /// <summary>
    /// A project route whose body names a real object of the project in <c>{p}</c>: <paramref name="own"/> builds that
    /// request and <paramref name="random"/> the control whose project and object are both fresh. <see cref="ProjectCase"/>
    /// cannot express it, because its request builder sees only a project id and a body would therefore have to name
    /// the project itself — refused by request validation rather than by tenancy. <paramref name="ownControl"/> adds the
    /// route's positive control on the caller's own project, since a body reference has no other way to prove the
    /// foreign probes reached the command instead of a validation error.
    /// </summary>
    private static RouteCase ProjectBodyCase(
        string method,
        string template,
        Func<IsolationProject, ProbeRequest> own,
        Func<ProbeRequest> random,
        Func<ApiRoute, IsolationWorld, RouteProbe>? ownControl = null) =>
        Case(
            method,
            template,
            (route, w) =>
            {
                var probes = new List<RouteProbe>();
                probes.AddRange(w.Acme.Projects.Select(p => Pair(route, $"org A project {p.Slug} in {{p}}",
                    ProbeExpect.NotFound, OrgBAdmin(w), own(p), random())));
                probes.Add(new(route, "X-Evidgate-Org: A with an org-A project id", runner => runner.CallerPairAsync(
                    route, "X-Evidgate-Org: A with an org-A project id", ForeignHeaderCaller(w), RandomHeaderCaller(w),
                    own(w.Acme.Projects[0]))));
                if (ownControl is not null)
                {
                    probes.Add(ownControl(route, w));
                }

                return probes;
            },
            (route, w) =>
            [
                new(route, "sibling project api of the token's org", runner => runner.ScopeMismatchAsync(
                    route, "sibling project api of the token's org", OrgBService(w), own(w.Acme2.Projects[1]),
                    requireScopeCode: false)),
                Pair(route, "org A project web in {p}", ProbeExpect.ServiceRefused, OrgBService(w), own(w.Acme.Projects[0]), random()),
                new(route, "sibling project api of the OIDC token's org", runner => runner.ScopeMismatchAsync(
                    route, "sibling project api of the OIDC token's org", OrgBServiceOidc(w), own(w.Acme2.Projects[1]),
                    requireScopeCode: false)),
                Pair(route, "org A project web in {p} (OIDC token)", ProbeExpect.ServiceRefused, OrgBServiceOidc(w), own(w.Acme.Projects[0]), random()),
            ]);

    /// <summary>The queue body: a well-formed comment on the item of the project in the path.</summary>
    private static ProbeRequest QueueBody(Guid project, Guid item) =>
        new(HttpMethod.Post, $"/v1/projects/{project}/authoring/changes", new JsonObject
        {
            ["target_kind"] = "checklist_item",
            ["target_id"] = item,
            ["change_type"] = "comment",
            ["payload"] = new JsonObject { ["body"] = "intruder comment" },
            ["reason"] = "isolation probe",
        });

    /// <summary>
    /// The apply selection: one change of the project in the path, under an <c>If-Match</c> shaped like the order tag
    /// the route declares, so a granted caller reaches the command instead of failing validation.
    /// </summary>
    private static ProbeRequest ApplyBody(Guid project, Guid change) =>
        new(HttpMethod.Post, $"/v1/projects/{project}/authoring/changes:apply",
            new JsonObject { ["change_ids"] = new JsonArray { change } },
            IfMatch: "\"order:" + new string('0', 64) + "\"");

    /// <summary>
    /// A route naming an object id: as an org-B member, every org-A object the selector yields (both projects)
    /// against a random id; as org B's <c>ci_producer</c>, org A's objects against a random id and the sibling
    /// project's own objects (refused, never 2xx). A GET case's positive control is org B's own object
    /// (<paramref name="ownControl"/>); a list route's is its full-pagination walk (<c>Lists</c>).
    /// </summary>
    private static RouteCase ObjectCase(
        string method,
        string template,
        Func<IsolationProject, IEnumerable<(string Label, Guid Id)>> ids,
        Func<Guid, ProbeRequest> request,
        bool list = false,
        bool ownControl = false) =>
        Case(
            method,
            template,
            (route, w) =>
            {
                var probes = new List<RouteProbe>();
                probes.AddRange(w.Acme.Projects.SelectMany(p => ids(p).Select(id => Pair(route, $"org A {id.Label} of project {p.Slug}",
                    list ? ProbeExpect.EmptyOrNotFound : ProbeExpect.NotFound, OrgBAdmin(w), request(id.Id), request(Guid.CreateVersion7())))));
                probes.Add(new(route, "X-Evidgate-Org: A with an org-A id", runner => runner.CallerPairAsync(
                    route, "X-Evidgate-Org: A with an org-A id", ForeignHeaderCaller(w), RandomHeaderCaller(w), request(ids(w.Acme.Projects[0]).First().Id))));
                if (ownControl)
                {
                    probes.Add(OwnObject(route, "own object answers 200", OrgBAdmin(w), request(ids(w.Acme2.Projects[0]).First().Id)));
                }

                return probes;
            },
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
            ],
            // Every suite has one revision, so org B has no own-vs-other pair in one suite; the positive control
            // diffs org B's own revision against itself (the same URL shape, 200 with an empty diff).
            own: p => new(HttpMethod.Get, $"/v1/suite-revisions/{p.SuiteRevisionIds[0]}/diff?against={p.SuiteRevisionIds[0]}"));

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
                    new(route, "X-Evidgate-Org: A with an org-A project id", runner => runner.CallerPairAsync(
                        route, "X-Evidgate-Org: A with an org-A project id", ForeignHeaderCaller(w), RandomHeaderCaller(w),
                        Upload(w.Acme.Projects[0].Id, Manifest(w.Acme.Projects[0])))),
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
                    // The OIDC-minted token of project web: accepted for its own project (200 dedupe or 201), refused
                    // on the sibling project (403 project_scope_mismatch) and on org A's project (02.9.1-T3, from #19).
                    new(route, "own project, OIDC token accepted", async runner =>
                    {
                        var response = await runner.SendAsync(OrgBServiceOidc(w), Upload(w.Acme2.Projects[0].Id, Manifest(w.Acme2.Projects[0])));
                        var violations = new List<string>();
                        if (response.Status is not (200 or 201))
                        {
                            violations.Add(RouteProbeRunner.Describe(route, "own project, OIDC token accepted", OrgBServiceOidc(w), $"answered {response}, expected 200 or 201"));
                        }

                        return violations;
                    }),
                    new(route, "sibling project api of the OIDC token's org", runner => runner.ScopeMismatchAsync(
                        route, "sibling project api of the OIDC token's org", OrgBServiceOidc(w), Upload(w.Acme2.Projects[1].Id, Manifest(w.Acme2.Projects[1])), requireScopeCode: true)),
                    Pair(route, "org A project web in {p} (OIDC token)", ProbeExpect.ServiceRefused, OrgBServiceOidc(w),
                        Upload(w.Acme.Projects[0].Id, Manifest(w.Acme.Projects[0])), Upload(Guid.CreateVersion7(), Manifest(w.Acme.Projects[0]))),
                ];
            });
    }

    /// <summary>
    /// <c>POST /v1/projects/{p}/runs</c>: org A's project with org A's manifest (404), and org B's own project
    /// naming org A's manifest id in the body — the same 404 as a random id, so another tenant's manifest is
    /// never resolved (AC1). The caller must hold <c>run:create</c> (org B's <c>tenant_admin</c> and its
    /// <c>ci_producer</c> both do).
    /// </summary>
    private static RouteCase RunCreateCase()
    {
        var route = new ApiRoute("POST", "/v1/projects/{p}/runs");
        static ProbeRequest Create(Guid project, Guid manifest) => new(HttpMethod.Post, $"/v1/projects/{project}/runs",
            new JsonObject { ["manifest_id"] = manifest.ToString() });

        return new RouteCase(
            route,
            world =>
            {
                var w = Require(world);
                return
                [
                    .. w.Acme.Projects.Select(p => Pair(route, $"org A project {p.Slug} in {{p}}", ProbeExpect.NotFound, OrgBAdmin(w),
                        Create(p.Id, p.ManifestId), Create(Guid.CreateVersion7(), p.ManifestId))),
                    Pair(route, "own project, org A's manifest id in the body", ProbeExpect.NotFound, OrgBAdmin(w),
                        Create(w.Acme2.Projects[0].Id, w.Acme.Projects[0].ManifestId), Create(w.Acme2.Projects[0].Id, Guid.CreateVersion7())),
                    new(route, "X-Evidgate-Org: A with an org-A project id", runner => runner.CallerPairAsync(
                        route, "X-Evidgate-Org: A with an org-A project id", ForeignHeaderCaller(w), RandomHeaderCaller(w),
                        Create(w.Acme.Projects[0].Id, w.Acme.Projects[0].ManifestId))),
                    new(route, "own project, own manifest accepted", async runner =>
                    {
                        var response = await runner.SendAsync(OrgBAdmin(w), Create(w.Acme2.Projects[0].Id, w.Acme2.Projects[0].ManifestId));
                        var violations = new List<string>();
                        if (response.Status != 201)
                        {
                            violations.Add(RouteProbeRunner.Describe(route, "own project, own manifest accepted", OrgBAdmin(w), $"answered {response}, expected 201"));
                        }

                        return violations;
                    }),
                ];
            },
            world =>
            {
                var w = Require(world);
                return
                [
                    new(route, "sibling project api of the token's org", runner => runner.ScopeMismatchAsync(
                        route, "sibling project api of the token's org", OrgBService(w), Create(w.Acme2.Projects[1].Id, w.Acme2.Projects[1].ManifestId), requireScopeCode: true)),
                    Pair(route, "org A project web in {p}", ProbeExpect.ServiceRefused, OrgBService(w),
                        Create(w.Acme.Projects[0].Id, w.Acme.Projects[0].ManifestId), Create(Guid.CreateVersion7(), w.Acme.Projects[0].ManifestId)),
                    Pair(route, "own project, org A's manifest id in the body", ProbeExpect.NotFound, OrgBService(w),
                        Create(w.Acme2.Projects[0].Id, w.Acme.Projects[0].ManifestId), Create(w.Acme2.Projects[0].Id, Guid.CreateVersion7())),
                ];
            });
    }

    /// <summary>
    /// <c>POST /v1/projects/{p}/releases</c>: org A's project with org A's environment revision (404 —
    /// refused before the body is validated), org B's own project naming org A's environment revision (a non-2xx
    /// naming a revision outside the project, never the row), and org B's own project with its own revision accepted
    /// (201) as the positive control that the probe body reaches the command. The caller holds <c>run:create</c>; a
    /// service token scoped to the sibling project is refused 403 <c>project_scope_mismatch</c>.
    /// </summary>
    private static RouteCase ReleaseCreateCase()
    {
        var route = new ApiRoute("POST", "/v1/projects/{p}/releases");

        static ProbeRequest Create(Guid project, Guid environmentRevision) => new(HttpMethod.Post,
            $"/v1/projects/{project}/releases", new JsonObject
            {
                ["name"] = "isolation release " + Guid.CreateVersion7().ToString("N"),
                ["components"] = new JsonArray(new JsonObject
                {
                    ["component_key"] = "web",
                    ["digest"] = "sha256:" + new string('a', 64),
                    ["environment_revision_id"] = environmentRevision.ToString(),
                }),
            });

        return new RouteCase(
            route,
            world =>
            {
                var w = Require(world);
                var member = OrgBAdmin(w);
                return
                [
                    .. w.Acme.Projects.Select(p => Pair(route, $"org A project {p.Slug} in {{p}}", ProbeExpect.NotFound, member,
                        Create(p.Id, p.EnvironmentRevisionId), Create(Guid.CreateVersion7(), p.EnvironmentRevisionId))),
                    Pair(route, "own project, org A's environment revision in the body", ProbeExpect.NotSuccess, member,
                        Create(w.Acme2.Projects[0].Id, w.Acme.Projects[0].EnvironmentRevisionId),
                        Create(w.Acme2.Projects[0].Id, Guid.CreateVersion7())),
                    new(route, "X-Evidgate-Org: A with an org-A project id", runner => runner.CallerPairAsync(
                        route, "X-Evidgate-Org: A with an org-A project id", ForeignHeaderCaller(w), RandomHeaderCaller(w),
                        Create(w.Acme.Projects[0].Id, w.Acme.Projects[0].EnvironmentRevisionId))),
                    new(route, "own project, own environment revision accepted", async runner =>
                    {
                        var violations = new List<string>();
                        var response = await runner.SendAsync(member,
                            Create(w.Acme2.Projects[0].Id, w.Acme2.Projects[0].EnvironmentRevisionId));
                        if (response.Status != 201)
                        {
                            violations.Add(RouteProbeRunner.Describe(route, "own project, own environment revision accepted", member,
                                $"answered {response}, expected 201"));
                        }

                        return violations;
                    }),
                ];
            },
            world =>
            {
                var w = Require(world);
                var service = OrgBService(w);
                return
                [
                    new(route, "sibling project api of the token's org", runner => runner.ScopeMismatchAsync(
                        route, "sibling project api of the token's org", service,
                        Create(w.Acme2.Projects[1].Id, w.Acme2.Projects[1].EnvironmentRevisionId), requireScopeCode: true)),
                    Pair(route, "org A project web in {p}", ProbeExpect.ServiceRefused, service,
                        Create(w.Acme.Projects[0].Id, w.Acme.Projects[0].EnvironmentRevisionId),
                        Create(Guid.CreateVersion7(), w.Acme.Projects[0].EnvironmentRevisionId)),
                    Pair(route, "own project, org A's environment revision in the body", ProbeExpect.NotSuccess, service,
                        Create(w.Acme2.Projects[0].Id, w.Acme.Projects[0].EnvironmentRevisionId),
                        Create(w.Acme2.Projects[0].Id, Guid.CreateVersion7())),
                ];
            });
    }

    /// <summary>
    /// <c>POST /v1/releases/{id}/runs</c>: the release in the path and the run in the body are both resolved under the
    /// caller's tenant. Org B binding the world's org-A release answers the 404 <c>not_found</c> of a random release id
    /// and carries no org-A id; org B's own release addressed with org A's run answers the same 404 and carries no run
    /// id. Neither request writes, so org A's rows stay byte-identical around the mutation matrix. <c>run:create</c>
    /// sits outside the service-role ceiling, so a <c>ci_producer</c> is refused 403 or the same 404.
    /// </summary>
    private static RouteCase ReleaseBindCase()
    {
        var route = new ApiRoute("POST", "/v1/releases/{id}/runs");
        const string Name = "org A's release bound to org B's run";

        return new RouteCase(
            route,
            world =>
            {
                var w = Require(world);
                return
                [
                    new(route, Name, runner => BindAcrossTenantsAsync(runner, route, w, Name)),
                ];
            },
            world =>
            {
                var w = Require(world);
                return
                [
                    new(route, "org B's ci_producer token", runner => BindRefusedAsServiceAsync(runner, route, w)),
                ];
            });
    }

    /// <summary>One bind request: the release in the path, the run in the body.</summary>
    private static ProbeRequest BindRequest(Guid release, Guid run) =>
        new(HttpMethod.Post, $"/v1/releases/{release}/runs", new JsonObject { ["run_id"] = run.ToString() });

    /// <summary>Reads and binds across the two tenants, recording every answer that is not the shared refusal.</summary>
    private static async Task<IReadOnlyList<string>> BindAcrossTenantsAsync(
        RouteProbeRunner runner,
        ApiRoute route,
        IsolationWorld world,
        string name)
    {
        var violations = new List<string>();
        var foreigner = OrgBAdmin(world);
        var orgA = world.Acme.Projects[0];
        var orgB = world.Acme2.Projects[0];

        var foreignRelease = await runner.SendAsync(foreigner, BindRequest(orgA.ReleaseId, orgB.RunId));
        var control = await runner.SendAsync(foreigner, BindRequest(Guid.CreateVersion7(), orgB.RunId));
        foreach (var (label, response) in new[] { ("org A's release id", foreignRelease), ("a random release id", control) })
        {
            if (response.Status != 404 || response.Code != "not_found")
            {
                violations.Add(RouteProbeRunner.Describe(route, name, foreigner,
                    $"the bind of {label} answered {response}, expected 404 not_found"));
            }
        }

        if (foreignRelease.Text.Contains(orgA.ReleaseId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            violations.Add(RouteProbeRunner.Describe(route, name, foreigner,
                $"the refused answer carried org A's release id {orgA.ReleaseId}"));
        }

        var foreignRun = await runner.SendAsync(foreigner, BindRequest(orgB.ReleaseId, orgA.RunId));
        if (foreignRun.Status != 404 || foreignRun.Code != "not_found")
        {
            violations.Add(RouteProbeRunner.Describe(route, name, foreigner,
                $"binding an org-A run answered {foreignRun}, expected 404 not_found"));
        }

        if (foreignRun.Text.Contains(orgA.RunId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            violations.Add(RouteProbeRunner.Describe(route, name, foreigner,
                $"the refused answer carried org A's run id {orgA.RunId}"));
        }

        return violations;
    }

    /// <summary>A bind of a fresh release id as org B's <c>ci_producer</c>: 403 before the handler, or the same 404.</summary>
    private static async Task<IReadOnlyList<string>> BindRefusedAsServiceAsync(
        RouteProbeRunner runner,
        ApiRoute route,
        IsolationWorld world)
    {
        var violations = new List<string>();
        var service = OrgBService(world);
        var release = Guid.CreateVersion7();
        var refused = await runner.SendAsync(service, BindRequest(release, Guid.CreateVersion7()));

        if (refused.Status is not (403 or 404) || (refused.Status == 404 && refused.Code != "not_found"))
        {
            violations.Add(RouteProbeRunner.Describe(route, "org B's ci_producer token", service,
                $"the bind answered {refused}, expected 403 or 404 not_found"));
        }

        if (refused.Text.Contains(release.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            violations.Add(RouteProbeRunner.Describe(route, "org B's ci_producer token", service,
                $"the refused answer carried the release id {release}"));
        }

        return violations;
    }

    /// <summary>
    /// <c>GET /v1/releases/{id}</c>: the world's org-A release is read 200 by org A and refused 404
    /// <c>not_found</c> for org B, identically to a random id. The release is addressed by id alone, so its tenant
    /// scope is the RLS read; the refused answer carries no org-A id. <c>run:read</c> is outside the service-role
    /// ceiling, so a <c>ci_producer</c> is refused before the handler (403, or the same 404 on a foreign org).
    /// </summary>
    private static RouteCase ReleaseReadCase()
    {
        var route = new ApiRoute("GET", "/v1/releases/{id}");
        const string Name = "org A's release read by both tenants";

        return new RouteCase(
            route,
            world =>
            {
                var w = Require(world);
                return
                [
                    new(route, Name, runner => ReadReleaseAsync(runner, route, w, Name)),
                ];
            },
            world =>
            {
                var w = Require(world);
                return
                [
                    new(route, "org A's release with a ci_producer token", runner =>
                        ReadReleaseAsServiceAsync(w, runner, route, "org A's release with a ci_producer token")),
                ];
            });
    }

    /// <summary>
    /// Reads the world's org-A release three ways: the owner gets the 200 body carrying the id, and org B and a
    /// random id both answer 404 <c>not_found</c> without the org-A id. The probe writes nothing.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadReleaseAsync(RouteProbeRunner runner, ApiRoute route, IsolationWorld world, string name)
    {
        var violations = new List<string>();
        var release = world.Acme.Projects[0].ReleaseId;

        var owner = OrgAAdmin(world);
        var ownRead = await runner.SendAsync(owner, new(HttpMethod.Get, $"/v1/releases/{release}"));
        if (ownRead.Status != 200 || !ownRead.Text.Contains(release.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            violations.Add(RouteProbeRunner.Describe(route, name, owner,
                $"the owner's read answered {ownRead}, expected the 200 body of the world's release"));
        }

        var foreigner = OrgBAdmin(world);
        var foreign = await runner.SendAsync(foreigner, new(HttpMethod.Get, $"/v1/releases/{release}"));
        var control = await runner.SendAsync(foreigner, new(HttpMethod.Get, $"/v1/releases/{Guid.CreateVersion7()}"));
        foreach (var (label, response) in new[] { ("org A's release id", foreign), ("a random id", control) })
        {
            if (response.Status != 404 || response.Code != ReleaseNotFoundCode)
            {
                violations.Add(RouteProbeRunner.Describe(route, name, foreigner,
                    $"the read of {label} answered {response}, expected 404 {ReleaseNotFoundCode}"));
            }
        }

        if (foreign.Text.Contains(release.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            violations.Add(RouteProbeRunner.Describe(route, name, foreigner,
                $"the refused answer carried org A's release id {release}"));
        }

        return violations;
    }

    /// <summary>
    /// Reads a fresh release id as org B's <c>ci_producer</c>: <c>run:read</c> sits outside the service-role ceiling, so
    /// the permission stage refuses it before the handler (403), or a foreign org answers the same 404 as an unknown
    /// id. The probe creates nothing, so org A's rows stay byte-identical around the service matrix.
    /// </summary>
    private static Task<IReadOnlyList<string>> ReadReleaseAsServiceAsync(IsolationWorld world, RouteProbeRunner runner, ApiRoute route, string name)
    {
        var service = OrgBService(world);
        var release = Guid.CreateVersion7();
        return ReadRefusedAsync(runner, route, name, service, release);
    }

    private static async Task<IReadOnlyList<string>> ReadRefusedAsync(
        RouteProbeRunner runner,
        ApiRoute route,
        string name,
        ProbeCaller caller,
        Guid release)
    {
        var violations = new List<string>();
        var refused = await runner.SendAsync(caller, new(HttpMethod.Get, $"/v1/releases/{release}"));
        if (refused.Status is not (403 or 404) || (refused.Status == 404 && refused.Code != "not_found"))
        {
            violations.Add(RouteProbeRunner.Describe(route, name, caller,
                $"the read answered {refused}, expected 403 or 404 not_found"));
        }

        if (refused.Text.Contains(release.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            violations.Add(RouteProbeRunner.Describe(route, name, caller,
                $"the refused answer carried the release id {release}"));
        }

        return violations;
    }

    /// <summary>
    /// <c>POST /v1/artifacts/upload-url</c>: the run is named in the body, not the path, and the route carries no
    /// project in its URL. Org A's run id in the body against a random run id — the same 404 <c>not_found</c> for
    /// both, so another tenant's run is never resolved (AC1). The route is project-scoped by the credential, not
    /// by its path: org B's header-scoped <c>tenant_admin</c> names no project and is refused 403
    /// <c>project_required</c> for the foreign and the random run alike (no oracle); org B's <c>ci_producer</c>
    /// (project <c>web</c>) gets the 404 <c>not_found</c> the AC names.
    /// </summary>
    private static RouteCase EvidenceUploadUrlCase()
    {
        var route = new ApiRoute("POST", "/v1/artifacts/upload-url");
        static ProbeRequest Create(Guid run) => new(HttpMethod.Post, "/v1/artifacts/upload-url", new JsonObject
        {
            ["run_id"] = run.ToString(),
            ["producer_id"] = "probe-producer",
            ["execution_key"] = new string('a', 64),
            ["attempt_no"] = 1,
            ["kind"] = "trace",
            ["media_type"] = "application/json",
            ["bytes"] = 1024,
            ["sha256"] = "sha256:" + new string('b', 64),
        });

        return new RouteCase(
            route,
            world =>
            {
                var w = Require(world);
                return
                [
                    Pair(route, "org A's run id in the body", ProbeExpect.NotSuccess, OrgBAdmin(w),
                        Create(w.Acme.Projects[0].ManifestId), Create(Guid.CreateVersion7())),
                ];
            },
            world =>
            {
                var w = Require(world);
                return
                [
                    Pair(route, "org A's run id in the body", ProbeExpect.ServiceRefused, OrgBService(w),
                        Create(w.Acme.Projects[0].ManifestId), Create(Guid.CreateVersion7())),
                ];
            });
    }

    /// <summary>
    /// <c>POST /v1/runs/{r}/events</c> (04.1.1-T1): the run is named in the path, the org in
    /// <c>X-Evidgate-Org</c>. A fresh run id is used on purpose - the handler reads the run under the caller's
    /// tenant scope first, so an org-A run addressed by org B is the 404 <c>not_found</c> the isolation rule
    /// names (never a 403), exactly as 06.1.2's upload-url probe does.
    /// </summary>
    private static RouteCase EventBatchCase()
    {
        var route = new ApiRoute("POST", "/v1/runs/{r}/events");
        static ProbeRequest Create(Guid run) => new(HttpMethod.Post, $"/v1/runs/{run}/events", new JsonObject
        {
            ["events"] = new JsonArray
            {
                new JsonObject
                {
                    // The probe must be a schema-valid envelope, otherwise the parser answers 422
                    // /events/0/schema_version before the run lookup can answer 404.
                    ["schema_version"] = 1,
                    ["event_id"] = Guid.CreateVersion7().ToString(),
                    ["producer_id"] = "probe-producer",
                    ["sequence"] = 1,
                    ["execution_key"] = new string('a', 64),
                    ["attempt_no"] = 1,
                    ["type"] = "run.log",
                    ["occurred_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ["ext"] = new JsonObject { ["schema"] = "playwright/1" },
                },
            },
        });

        return new RouteCase(
            route,
            world =>
            {
                var w = Require(world);
                return
                [
                    Pair(route, "org A's run id in the path", ProbeExpect.NotSuccess, OrgBAdmin(w),
                        Create(Guid.CreateVersion7()), Create(Guid.CreateVersion7())),
                ];
            },
            world =>
            {
                var w = Require(world);
                return
                [
                    Pair(route, "org A's run id in the path", ProbeExpect.ServiceRefused, OrgBService(w),
                        Create(Guid.CreateVersion7()), Create(Guid.CreateVersion7())),
                ];
            });
    }

    /// <summary>
    /// <c>POST /v1/runs/{r}/finalize</c> (05.3.2-T1): the run is named in the path, the org in
    /// <c>X-Evidgate-Org</c>, and the shard's identity is the caller's token. A fresh run id is used on purpose —
    /// the handler reads the run under the caller's tenant scope before it touches a slot, so an org-A run addressed
    /// by org B is the 404 <c>not_found</c> the isolation rule names (never a 403), exactly as the events probe does.
    /// The body is shape-valid, because a malformed one is 422 before the run lookup; the hash is a well-formed but
    /// unrelated <c>sha256:</c> value, so a leak of the expected one would show up as a different answer.
    /// Org B's header-scoped <c>tenant_admin</c> holds no token identity and no <c>event:write</c>, so the C2 pipeline
    /// refuses it 403 before the handler runs — the events route's own answer to the same caller (05.3.2-T1).
    /// </summary>
    private static RouteCase FinalizeShardCase()
    {
        var route = new ApiRoute("POST", "/v1/runs/{r}/finalize");
        static ProbeRequest Close(Guid run) => new(HttpMethod.Post, $"/v1/runs/{run}/finalize", new JsonObject
        {
            ["producer_id"] = "shard-1",
            ["shard"] = 1,
            ["last_sequence"] = 0,
            ["count"] = 0,
            ["manifest_hash"] = "sha256:" + new string('a', 64),
        });

        return new RouteCase(
            route,
            world =>
            {
                var w = Require(world);
                return
                [
                    Pair(route, "org A's run id in the path", ProbeExpect.NotSuccess, OrgBAdmin(w),
                        Close(w.Acme.Projects[0].RunId), Close(Guid.CreateVersion7())),
                ];
            },
            world =>
            {
                var w = Require(world);
                return
                [
                    Pair(route, "org A's run id in the path", ProbeExpect.ServiceRefused, OrgBService(w),
                        Close(w.Acme.Projects[0].RunId), Close(Guid.CreateVersion7())),
                ];
            });
    }

    /// <summary>
    /// <c>POST /v1/artifacts/{a}/complete</c>: the artifact id is in the path and the route carries no project in
    /// its URL. No artifact row is seeded for either org (an artifact is minted only by <c>upload-url</c>, which
    /// needs a run), so the org-A half is an org-A id and the control a random id — the same 404
    /// <c>artifact_not_found</c> for both, so another tenant's artifact is never resolved (AC1). The route is
    /// project-scoped by the credential, not by its path: org B's header-scoped <c>tenant_admin</c> names no
    /// project and is refused 403 for the org-A and the random id alike (no oracle); org B's <c>ci_producer</c>
    /// (project <c>web</c>) holds <c>artifact:write</c> for the project its token names, so it reaches the
    /// endpoint's tenant read and gets the 404 <c>artifact_not_found</c> 06.1.2 §5 names for an unknown,
    /// other-tenant or other-project artifact.
    /// </summary>
    private static RouteCase EvidenceCompleteCase()
    {
        var route = new ApiRoute("POST", "/v1/artifacts/{a}/complete");
        static ProbeRequest Complete(Guid artifact) => new(HttpMethod.Post, $"/v1/artifacts/{artifact}/complete");

        return new RouteCase(
            route,
            world =>
            {
                var w = Require(world);
                return
                [
                    Pair(route, "an org A id in {a}", ProbeExpect.NotSuccess, OrgBAdmin(w),
                        Complete(w.Acme.Projects[0].ManifestId), Complete(Guid.CreateVersion7())),
                ];
            },
            world =>
            {
                var w = Require(world);
                return
                [
                    Pair(route, "an org A id in {a}", ProbeExpect.ArtifactNotFound, OrgBService(w),
                        Complete(w.Acme.Projects[0].ManifestId), Complete(Guid.CreateVersion7())),
                ];
            });
    }

    /// <summary>
    /// <c>GET /v1/artifacts/{a}</c> and <c>GET /v1/artifacts/{a}/download-url</c>: the artifact id is in the path and
    /// the route names no project, so the row's own project decides the scope. A member in this host is org-scoped
    /// (<c>TenantResolver.cs:69</c>) and another org's row is invisible to row level security, so both routes answer
    /// the one 404 <c>artifact_not_found</c> for an org-A id and a random one alike and no existence oracle exists;
    /// org B's <c>ci_producer</c> is project-scoped but holds <c>artifact:write</c> and not <c>artifact:read</c>, so
    /// the C2 pipeline refuses it 403 as well. The row-level half (a real artifact, its project and its
    /// classification) needs a seeded artifact this host cannot mint, so it is asserted by the evidence suite against
    /// a real database instead.
    /// </summary>
    private static RouteCase ArtifactReadCase(string method, string template, Func<Guid, ProbeRequest> request)
    {
        var route = new ApiRoute(method, template);
        return new RouteCase(
            route,
            world =>
            {
                var w = Require(world);
                return
                [
                    Pair(route, "an org A id in {a}", ProbeExpect.NotSuccess, OrgBAdmin(w),
                        request(w.Acme.Projects[0].ManifestId), request(Guid.CreateVersion7())),
                ];
            },
            world =>
            {
                var w = Require(world);
                return
                [
                    Pair(route, "an org A id in {a}", ProbeExpect.ServiceRefused, OrgBService(w),
                        request(w.Acme.Projects[0].ManifestId), request(Guid.CreateVersion7())),
                ];
            });
    }

    /// <summary>
    /// <c>POST /v1/waivers</c>: the project is named in the body, the org in <c>X-Evidgate-Org</c>. Org A's
    /// project against a random project id (404, no row written), plus the header naming org A.
    /// </summary>
    private static RouteCase WaiverCreateCase()
    {
        var route = new ApiRoute("POST", "/v1/waivers");
        static ProbeRequest Create(Guid project, Guid target) => new(HttpMethod.Post, "/v1/waivers", new JsonObject
        {
            ["project_id"] = project.ToString(),
            ["scope"] = "case",
            ["target_id"] = target.ToString(),
            ["reason"] = "isolation probe",
            ["expires_at"] = DateTimeOffset.UtcNow.AddDays(30).ToString("O", CultureInfo.InvariantCulture),
        });

        return new RouteCase(
            route,
            world =>
            {
                var w = Require(world);
                return
                [
                    .. w.Acme.Projects.Select(p => Pair(route, $"org A project {p.Slug} in the body", ProbeExpect.NotFound, OrgBAdmin(w),
                        Create(p.Id, p.CaseIds[0]), Create(Guid.CreateVersion7(), p.CaseIds[0]))),
                    new(route, "X-Evidgate-Org: A with an org-A project id", runner => runner.CallerPairAsync(
                        route, "X-Evidgate-Org: A with an org-A project id", ForeignHeaderCaller(w), RandomHeaderCaller(w),
                        Create(w.Acme.Projects[0].Id, w.Acme.Projects[0].CaseIds[0]))),
                ];
            },
            world =>
            {
                var w = Require(world);
                return
                [
                    Pair(route, "org A project web in the body", ProbeExpect.ServiceRefused, OrgBService(w),
                        Create(w.Acme.Projects[0].Id, w.Acme.Projects[0].CaseIds[0]), Create(Guid.CreateVersion7(), w.Acme.Projects[0].CaseIds[0])),
                ];
            });
    }

    /// <summary>
    /// <c>POST /v1/waivers/{w}/approve</c> and <c>.../revoke</c>: org A's waivers against random ids, the header
    /// probe, and one own-waiver control. The approve control is the requester approving their own waiver
    /// (403 <c>waiver_self_approval</c>, no write); the revoke control revokes a dedicated own waiver (200).
    /// </summary>
    private static RouteCase WaiverStateCase(string action)
    {
        var route = new ApiRoute("POST", $"/v1/waivers/{{w}}/{action}");
        var revoke = action == "revoke";
        return new RouteCase(
            route,
            world =>
            {
                var w = Require(world);
                var own = w.Acme2.Projects[0];
                var probes = new List<RouteProbe>();
                probes.AddRange(w.Acme.Projects.SelectMany(p => Indexed("waiver", p.WaiverIds).Select(id => Pair(route, $"org A {id.Label} of project {p.Slug}", ProbeExpect.NotFound, OrgBAdmin(w),
                    new(HttpMethod.Post, $"/v1/waivers/{id.Id}/{action}"), new(HttpMethod.Post, $"/v1/waivers/{Guid.CreateVersion7()}/{action}")))));
                probes.Add(new(route, "X-Evidgate-Org: A with an org-A waiver id", runner => runner.CallerPairAsync(
                    route, "X-Evidgate-Org: A with an org-A waiver id", ForeignHeaderCaller(w), RandomHeaderCaller(w),
                    new(HttpMethod.Post, $"/v1/waivers/{w.Acme.Projects[0].WaiverId}/{action}"))));
                probes.Add(revoke
                    ? OwnObject(route, "own waiver revoked by its requester answers 200", OrgBAdmin(w), new(HttpMethod.Post, $"/v1/waivers/{own.WaiverIds[1]}/{action}"))
                    : new RouteProbe(route, "own waiver, the requester approves their own -> 403 self-approval", runner => runner.OwnObjectAsync(
                        route, "own waiver, the requester approves their own -> 403 self-approval", OrgBAdmin(w),
                        new(HttpMethod.Post, $"/v1/waivers/{own.WaiverId}/{action}"), expectedStatus: 403)));
                return probes;
            },
            world =>
            {
                var w = Require(world);
                return
                [
                    .. Indexed("waiver", w.Acme.Projects[0].WaiverIds).Select(id => Pair(route, $"org A {id.Label} of project web", ProbeExpect.ServiceRefused, OrgBService(w),
                        new(HttpMethod.Post, $"/v1/waivers/{id.Id}/{action}"), new(HttpMethod.Post, $"/v1/waivers/{Guid.CreateVersion7()}/{action}"))),
                    .. Indexed("waiver", w.Acme2.Projects[1].WaiverIds).Select(id => new RouteProbe(route, $"sibling project api's {id.Label}", runner => runner.RefusedAsync(
                        route, $"sibling project api's {id.Label}", OrgBService(w), new(HttpMethod.Post, $"/v1/waivers/{id.Id}/{action}"), allowed: [403, 404]))),
                ];
            });
    }

    /// <summary>
    /// <c>GET /v1/healing/switch</c> (08.6.3-T1): the read side of the kill switch, which needs
    /// <c>healing:triage</c> (org B's <c>qa_engineer</c>). A <c>?project_id=</c> of another tenant is the same 404
    /// an unknown id gets, never an existence oracle, while org B's own project answers 200 — so the foreign 404 is
    /// decided by tenancy and not by a missing switch row.
    /// </summary>
    private static RouteCase HealingSwitchReadCase()
    {
        static ProbeRequest Read(Guid project) => new(HttpMethod.Get, $"/v1/healing/switch?project_id={project}");
        static ProbeRequest SetTenantMode() => new(HttpMethod.Put, "/v1/healing/switch", new JsonObject
        {
            ["scope"] = "tenant",
            ["mode"] = "disabled",
            ["reason"] = "isolation probe",
        });

        return Case(
            "GET",
            "/v1/healing/switch",
            (route, w) =>
            {
                var caller = HealingTriageCaller(w);
                const string Name = "org A's disabled tenant row never leaks into org B's read";
                return
                [
                    Pair(route, "?project_id= an org A project", ProbeExpect.NotFound, caller,
                        Read(w.Acme.Projects[0].Id), Read(Guid.CreateVersion7())),
                    OwnObject(route, "own project answers 200", caller, Read(w.Acme2.Projects[0].Id)),
                    // The row-level half of the isolation contract: org A stores `disabled` for its tenant, org A's
                    // qa_engineer resolves it, and org B's qa_engineer — reading the same route in the same database —
                    // must resolve `shadow` with no scope row at all. A leak would surface as org B resolving
                    // `disabled`, which a status-only parity probe cannot see.
                    new RouteProbe(route, Name, async runner =>
                    {
                        var violations = new List<string>();
                        static string Short(string text) => text.Length <= 200 ? text : text[..200] + "…";
                        var seed = await runner.SendAsync(OrgAAdmin(w), SetTenantMode());
                        if (!seed.IsSuccess)
                        {
                            violations.Add($"{route} [{Name}] seeding org A's tenant-wide disabled switch answered {seed.Status}: {Short(seed.Text)}");
                            return violations;
                        }

                        var own = await runner.SendAsync(caller, Read(w.Acme2.Projects[0].Id));
                        var ownResolved = ResolvedOf(own);
                        if (ownResolved != "shadow")
                        {
                            violations.Add($"{route} [{Name}] org B resolved '{ownResolved}', expected 'shadow' (org A's disabled row must not apply): {Short(own.Text)}");
                        }

                        var acme = await runner.SendAsync(TriageCallerOf(w.Acme, "A"), Read(w.Acme.Projects[0].Id));
                        var acmeResolved = ResolvedOf(acme);
                        if (acmeResolved != "disabled")
                        {
                            violations.Add($"{route} [{Name}] org A resolved its own tenant row as '{acmeResolved}', expected 'disabled': {Short(acme.Text)}");
                        }

                        return violations;
                    }),
                ];
            },
            (route, w) =>
            [
                Pair(route, "?project_id= an org A project as ci_producer", ProbeExpect.ServiceRefused, OrgBService(w),
                    Read(w.Acme.Projects[0].Id), Read(Guid.CreateVersion7())),
            ]);
    }

    /// <summary>
    /// <c>PUT /v1/healing/switch</c> (08.6.3-T1): the kill switch's write side, which needs <c>healing:configure</c>
    /// (org B's <c>tenant_admin</c>). A <c>scope=project</c> body naming org A's project is the same 404 an unknown
    /// project gets and writes no row; org B's own <c>scope=tenant</c> write answers 200, so the refusal is tenancy
    /// and not the V1 clamp (<c>mode=shadow</c> is always storable).
    /// </summary>
    private static RouteCase HealingSwitchSetCase()
    {
        static ProbeRequest SetProject(Guid project) => new(HttpMethod.Put, "/v1/healing/switch", new JsonObject
        {
            ["scope"] = "project",
            ["project_id"] = project.ToString(),
            ["mode"] = "shadow",
            ["reason"] = "isolation probe",
        });

        static ProbeRequest SetTenant() => new(HttpMethod.Put, "/v1/healing/switch", new JsonObject
        {
            ["scope"] = "tenant",
            ["mode"] = "shadow",
        });

        return Case(
            "PUT",
            "/v1/healing/switch",
            (route, w) =>
            [
                Pair(route, "scope=project naming an org A project", ProbeExpect.NotFound, OrgBAdmin(w),
                    SetProject(w.Acme.Projects[0].Id), SetProject(Guid.CreateVersion7())),
                OwnObject(route, "own tenant-wide switch answers 200", OrgBAdmin(w), SetTenant()),
            ],
            (route, w) =>
            [
                Pair(route, "scope=project naming an org A project as ci_producer", ProbeExpect.ServiceRefused, OrgBService(w),
                    SetProject(w.Acme.Projects[0].Id), SetProject(Guid.CreateVersion7())),
            ]);
    }

    /// <summary>Org B's <c>qa_engineer</c> naming org B: the only built-in member role that holds <c>healing:triage</c>.</summary>
    private static ProbeCaller HealingTriageCaller(IsolationWorld world) =>
        TriageCallerOf(Require(world).Acme2, "B");

    /// <summary>An org's <c>qa_engineer</c> naming its own org, for the switch's read side.</summary>
    private static ProbeCaller TriageCallerOf(IsolationOrg org, string label) =>
        ProbeCaller.Member(
            $"org-{label} qa_engineer (X-Evidgate-Org: {label})",
            org.User("qa_engineer"),
            org.Id);

    /// <summary>The <c>resolved</c> member of a healing switch read, or a placeholder when the answer is not one.</summary>
    private static string ResolvedOf(ProbeResponse response) =>
        response.Json is { ValueKind: JsonValueKind.Object } json
            && json.TryGetProperty("resolved", out var resolved)
            && resolved.ValueKind == JsonValueKind.String
            ? resolved.GetString()!
            : $"(no resolved in status {response.Status})";

    /// <summary>
    /// <c>POST /v1/quarantine</c>: the project is named in the body, the org in <c>X-Evidgate-Org</c>. Org A's
    /// project against a random project id (404, no row written), plus the header naming org A. The caller must
    /// hold <c>quarantine:write</c> (<c>test_maintainer</c> only, D1), so the member probes use org B's maintainer.
    /// </summary>
    private static RouteCase QuarantineCreateCase()
    {
        var route = new ApiRoute("POST", "/v1/quarantine");
        static ProbeRequest Create(Guid project, Guid caseId, Guid owner) => new(HttpMethod.Post, "/v1/quarantine", new JsonObject
        {
            ["project_id"] = project.ToString(),
            ["case_id"] = caseId.ToString(),
            ["owner"] = owner.ToString(),
            ["reason"] = "isolation probe",
            ["expires_at"] = DateTimeOffset.UtcNow.AddDays(30).ToString("O", CultureInfo.InvariantCulture),
        });

        return new RouteCase(
            route,
            world =>
            {
                var w = Require(world);
                var owner = w.Acme2.User("test_maintainer").MembershipId;
                return
                [
                    .. w.Acme.Projects.Select(p => Pair(route, $"org A project {p.Slug} in the body", ProbeExpect.NotFound, OrgBMaintainer(w),
                        Create(p.Id, p.CaseIds[0], owner), Create(Guid.CreateVersion7(), p.CaseIds[0], owner))),
                    new(route, "X-Evidgate-Org: A with an org-A project id", runner => runner.CallerPairAsync(
                        route, "X-Evidgate-Org: A with an org-A project id", ForeignHeaderMaintainer(w), RandomHeaderMaintainer(w),
                        Create(w.Acme.Projects[0].Id, w.Acme.Projects[0].CaseIds[0], owner))),
                ];
            },
            world =>
            {
                var w = Require(world);
                var owner = w.Acme2.User("test_maintainer").MembershipId;
                return
                [
                    Pair(route, "org A project web in the body", ProbeExpect.ServiceRefused, OrgBService(w),
                        Create(w.Acme.Projects[0].Id, w.Acme.Projects[0].CaseIds[0], owner), Create(Guid.CreateVersion7(), w.Acme.Projects[0].CaseIds[0], owner)),
                ];
            });
    }

    /// <summary>
    /// <c>DELETE /v1/quarantine/{q}</c>: org A's entries against random ids, the header probe, and one own-entry
    /// control (200). The caller must hold <c>quarantine:write</c>, so the member probes use org B's maintainer.
    /// </summary>
    private static RouteCase QuarantineRemoveCase()
    {
        var route = new ApiRoute("DELETE", "/v1/quarantine/{q}");
        return new RouteCase(
            route,
            world =>
            {
                var w = Require(world);
                var own = w.Acme2.Projects[0];
                var probes = new List<RouteProbe>();
                probes.AddRange(w.Acme.Projects.SelectMany(p => Indexed("quarantine entry", p.QuarantineIds).Select(id => Pair(route, $"org A {id.Label} of project {p.Slug}", ProbeExpect.NotFound, OrgBMaintainer(w),
                    new(HttpMethod.Delete, $"/v1/quarantine/{id.Id}"), new(HttpMethod.Delete, $"/v1/quarantine/{Guid.CreateVersion7()}")))));
                probes.Add(new(route, "X-Evidgate-Org: A with an org-A quarantine id", runner => runner.CallerPairAsync(
                    route, "X-Evidgate-Org: A with an org-A quarantine id", ForeignHeaderMaintainer(w), RandomHeaderMaintainer(w),
                    new(HttpMethod.Delete, $"/v1/quarantine/{w.Acme.Projects[0].QuarantineEntryId}"))));
                probes.Add(OwnObject(route, "own quarantine entry removed by its maintainer answers 200", OrgBMaintainer(w), new(HttpMethod.Delete, $"/v1/quarantine/{own.QuarantineIds[1]}")));
                return probes;
            },
            world =>
            {
                var w = Require(world);
                return
                [
                    .. Indexed("quarantine entry", w.Acme.Projects[0].QuarantineIds).Select(id => Pair(route, $"org A {id.Label} of project web", ProbeExpect.ServiceRefused, OrgBService(w),
                        new(HttpMethod.Delete, $"/v1/quarantine/{id.Id}"), new(HttpMethod.Delete, $"/v1/quarantine/{Guid.CreateVersion7()}"))),
                    .. Indexed("quarantine entry", w.Acme2.Projects[1].QuarantineIds).Select(id => new RouteProbe(route, $"sibling project api's {id.Label}", runner => runner.RefusedAsync(
                        route, $"sibling project api's {id.Label}", OrgBService(w), new(HttpMethod.Delete, $"/v1/quarantine/{id.Id}"), allowed: [403, 404]))),
                ];
            });
    }

    /// <summary><see cref="ObjectCase"/> whose request also needs the object's project, plus extra member probes.</summary>
    private static RouteCase ObjectCaseWith(
        string method,
        string template,
        Func<IsolationProject, IEnumerable<(string Label, Guid Id)>> ids,
        Func<Guid, IsolationProject, ProbeRequest> request,
        Func<ApiRoute, IsolationWorld, IEnumerable<RouteProbe>> extraMember,
        Func<IsolationProject, ProbeRequest>? own = null) =>
        Case(
            method,
            template,
            (route, w) =>
            {
                var probes = new List<RouteProbe>();
                probes.AddRange(w.Acme.Projects.SelectMany(p => ids(p).Select(id => Pair(route, $"org A {id.Label} of project {p.Slug}", ProbeExpect.NotFound, OrgBAdmin(w),
                    request(id.Id, p), request(Guid.CreateVersion7(), p)))));
                probes.Add(new(route, "X-Evidgate-Org: A with an org-A id", runner => runner.CallerPairAsync(
                    route, "X-Evidgate-Org: A with an org-A id", ForeignHeaderCaller(w), RandomHeaderCaller(w), request(ids(w.Acme.Projects[0]).First().Id, w.Acme.Projects[0]))));
                probes.AddRange(extraMember(route, w));
                if (own is not null)
                {
                    probes.Add(OwnObject(route, "own object answers 200", OrgBAdmin(w), own(w.Acme2.Projects[0])));
                }

                return probes;
            },
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

    /// <summary>Org B's own object with the same URL shape: the positive control (200) for a GET object case.</summary>
    private static RouteProbe OwnObject(ApiRoute route, string name, ProbeCaller caller, ProbeRequest request) =>
        new(route, name, runner => runner.OwnObjectAsync(route, name, caller, request));

    /// <summary>
    /// An <c>If-Match</c> value for a row's <c>version</c>: the number in quotes (<c>"2"</c>), the tag shape the
    /// authoring routes declare — never the order tag of a sibling set (13.1.1-T3).
    /// </summary>
    private static string VersionTag(int version) =>
        string.Create(CultureInfo.InvariantCulture, $"\"{version}\"");

    /// <summary>Org B's tenant_admin naming org A in <c>X-Evidgate-Org</c> (the header-vs-route precedence probe).</summary>
    private static ProbeCaller ForeignHeaderCaller(IsolationWorld world) =>
        ProbeCaller.Member("org-B tenant_admin (X-Evidgate-Org: A)", Require(world).Acme2.Admin, world.Acme.Id);

    /// <summary>Org B's tenant_admin naming a random org in <c>X-Evidgate-Org</c> (the control for the header probe).</summary>
    private static ProbeCaller RandomHeaderCaller(IsolationWorld world) =>
        ProbeCaller.Member("org-B tenant_admin (X-Evidgate-Org: random)", Require(world).Acme2.Admin, Guid.CreateVersion7());

    /// <summary>Org B's <c>test_maintainer</c> naming org B: the only member role holding <c>quarantine:write</c> (D1).</summary>
    private static ProbeCaller OrgBMaintainer(IsolationWorld world) =>
        ProbeCaller.Member("org-B test_maintainer (X-Evidgate-Org: B)", Require(world).Acme2.User("test_maintainer"), world.Acme2.Id);

    /// <summary>Org B's <c>test_maintainer</c> naming org A in <c>X-Evidgate-Org</c> (the header-vs-route precedence probe).</summary>
    private static ProbeCaller ForeignHeaderMaintainer(IsolationWorld world) =>
        ProbeCaller.Member("org-B test_maintainer (X-Evidgate-Org: A)", Require(world).Acme2.User("test_maintainer"), world.Acme.Id);

    /// <summary>Org B's <c>test_maintainer</c> naming a random org in <c>X-Evidgate-Org</c> (the control for the header probe).</summary>
    private static ProbeCaller RandomHeaderMaintainer(IsolationWorld world) =>
        ProbeCaller.Member("org-B test_maintainer (X-Evidgate-Org: random)", Require(world).Acme2.User("test_maintainer"), Guid.CreateVersion7());

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

    /// <summary>The minimal valid <c>policy-revision.v1</c> body a probe sends (the shape the seeder creates, <see cref="IsolationWorld"/>).</summary>
    private static JsonObject Policy() => new() { ["flaky_budget"] = 0 };

    /// <summary>The retention body a probe sends (06.4.1-T1): both windows inside the configured range.</summary>
    private static JsonObject Retention() => new() { ["artifact_days"] = 14, ["metadata_days"] = 90 };

    /// <summary>
    /// The <c>artifact_kind_policy</c> body a probe sends: all five members, because a body that omits one is
    /// a 400 before the handler and the probe would test the reader instead of tenancy.
    /// </summary>
    private static JsonObject KindPolicy() => new()
    {
        ["max_bytes"] = 1_048_576,
        ["media_types"] = new JsonArray("image/png"),
        ["default_sensitivity"] = "normal",
        ["capture_enabled"] = true,
        ["reject_on_secret"] = true,
    };

    /// <summary>The activation body a probe sends (07.1.1-T4): an unknown revision id, so the route answers 404 to every tenant.</summary>
    private static JsonObject Activation(Guid revisionId) => new() { ["policy_revision_id"] = revisionId.ToString("D") };

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

    /// <summary>The 00 §9.2 <c>error.code</c> for a hidden or missing row (<c>ApiErrorCode.NotFound</c>, ErrorEnvelope.cs:50).</summary>
    private const string NotFoundCode = "not_found";

    /// <summary>The 00 §9.2 <c>error.code</c> for an artifact outside the caller's tenant/project (06.1.2 §5).</summary>
    private const string ArtifactNotFoundCode = "artifact_not_found";

    /// <summary>
    /// The JSON members whose values are masked in a failure message. The ingest-token plaintext is <c>secret</c>
    /// (<c>IngestTokenCreatedResponse.Secret</c>, CiDtos.cs:89) and the CI exchange token is <c>token</c>
    /// (<c>CiTokenResponse.Token</c>, CiDtos.cs:112); both are snake_case on the wire (ApiHost.cs:88, SnakeCaseLower).
    /// </summary>
    private static readonly HashSet<string> SecretMembers = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "ingest_token", "secret", "access_token", "jwt", "binding_challenge", "password", "refresh_token",
    };

    private readonly ApiIsolationFixture _fixture;
    private readonly HttpClient _client;
    private readonly HashSet<string> _foreignIds;
    private readonly HashSet<string> _listTemplates;
    private bool _foreignIdsLoaded;

    /// <summary>A runner over <paramref name="fixture"/>'s host; org A's object ids are the forbidden set.</summary>
    public RouteProbeRunner(ApiIsolationFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;
        _client = fixture.AnonymousClient();
        _foreignIds = OwnedIds(fixture.World.Acme);
        _foreignIds.ExceptWith(OwnedIds(fixture.World.Acme2));
        _listTemplates = [.. IsolationRouteCases.Lists(fixture.World).Select(walk => walk.Route.Template)];
    }

    /// <summary>
    /// Adds org A's <c>audit_log</c> ids (owner query) to the forbidden set once, so an audit row whose actor or
    /// object is not in the world is still caught (C8).
    /// </summary>
    private async Task EnsureForeignIdsAsync()
    {
        if (_foreignIdsLoaded)
        {
            return;
        }

        _foreignIdsLoaded = true;
        var auditIds = await OwnerIdsAsync("select id::text from public.audit_log where org_id = @org", World.Acme.Id).ConfigureAwait(false);
        _foreignIds.UnionWith(auditIds);
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
            ids.AddRange([p.Id, p.ActiveTokenId, p.RevokedTokenId, p.EnvironmentRevisionId, p.ManifestId, p.ReleaseId]);
            ids.AddRange(p.RunIds);
            ids.AddRange(p.BindingIds);
            ids.AddRange(p.CaseIds);
            ids.AddRange(p.CaseRevisions.SelectMany(revisions => revisions));
            ids.AddRange(p.SuiteIds);
            ids.AddRange(p.SuiteRevisionIds);
            ids.AddRange(p.WaiverIds);
            ids.AddRange(p.QuarantineIds);
        }

        ids.AddRange(org.Projects.Select(p => p.PolicyRevisionId));

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

        await EnsureForeignIdsAsync().ConfigureAwait(false);

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

        return new ProbeResponse((int)response.StatusCode, code, text, json, response.Headers.ETag?.ToString());
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
        else if (!string.Equals(Shape(real), Shape(control), StringComparison.Ordinal))
        {
            violations.Add(Describe(route, name, caller, $"existence oracle: foreign {foreign} and random {random} answered {real} with different bodies: {Brief(real)} vs {Brief(control)}"));
        }

        violations.AddRange(ForeignIdsIn(real.Text).Select(id => Describe(route, name, caller, $"{foreign} answered {real} carrying org-A id {id}")));
        return violations;

        void Judge(ProbeResponse response, ProbeRequest request, string half)
        {
            var ok = expect switch
            {
                ProbeExpect.NotFound => response.Status == 404 && response.Code == NotFoundCode,
                ProbeExpect.ForeignOrg => response.Status == 403 || (response.Status == 404 && response.Code == NotFoundCode),
                ProbeExpect.EmptyOrNotFound => (response.Status == 404 && response.Code == NotFoundCode) || (IsListRoute(route) && response.Status == 200 && response.ItemCount == 0),
                ProbeExpect.ServiceRefused => (response.Status == 403 || (response.Status == 404 && response.Code == NotFoundCode)) || (IsListRoute(route) && response.Status == 200 && response.ItemCount == 0),
                ProbeExpect.ArtifactNotFound => response.Status == 404 && response.Code == ArtifactNotFoundCode,
                ProbeExpect.NotSuccess => !response.IsSuccess && response.Status < 500,
                _ => false,
            };

            if (!ok)
            {
                violations.Add(Describe(route, name, caller, $"{half} {request} answered {response}, expected {Expected(expect)}: {Brief(response)}"));
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
        if (real.Status is not (403 or 404) || (real.Status == 404 && real.Code != NotFoundCode))
        {
            violations.Add(Describe(route, name, foreignCaller, $"{request} answered {real}, expected {Expected(ProbeExpect.ForeignOrg)}: {Brief(real)}"));
        }

        if (real.Status != control.Status || !string.Equals(real.Code, control.Code, StringComparison.Ordinal))
        {
            violations.Add(Describe(route, name, foreignCaller, $"existence oracle: naming org A answered {real}, naming a random org answered {control}"));
        }
        else if (!string.Equals(Shape(real), Shape(control), StringComparison.Ordinal))
        {
            violations.Add(Describe(route, name, foreignCaller, $"existence oracle: naming org A and a random org answered {real} with different bodies: {Brief(real)} vs {Brief(control)}"));
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
            violations.Add(Describe(route, name, caller, $"{request} answered {response}, expected a refusal{(allowed is null ? string.Empty : " (" + string.Join('/', allowed) + ")")}: {Brief(response)}"));
        }

        violations.AddRange(ForeignIdsIn(response.Text).Select(id => Describe(route, name, caller, $"{request} answered {response} carrying org-A id {id}")));
        return violations;
    }

    /// <summary>
    /// Org B's own object with the same URL shape as the foreign probes: <paramref name="expectedStatus"/> (the
    /// positive control that proves the probe URL matches a real route and the object is visible), and no org-A id.
    /// </summary>
    public async Task<IReadOnlyList<string>> OwnObjectAsync(ApiRoute route, string name, ProbeCaller caller, ProbeRequest request, int expectedStatus = 200)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        var response = await SendAsync(caller, request).ConfigureAwait(false);
        var violations = new List<string>();
        if (response.Status != expectedStatus)
        {
            violations.Add(Describe(route, name, caller, $"{request} answered {response}, expected {expectedStatus} (positive control): {Brief(response)}"));
        }

        violations.AddRange(ForeignIdsIn(response.Text).Select(id => Describe(route, name, caller, $"{request} answered {response} carrying org-A id {id}")));
        return violations;
    }

    /// <summary>
    /// The apply's positive control, which has to build its own state: queue <paramref name="queue"/> in the caller's
    /// own project (201), read that selection's batch ETag from the list route, then apply the queued change under the
    /// tag the read served (200). A tag computed here would prove nothing about the route's own tag.
    /// </summary>
    public async Task<IReadOnlyList<string>> OwnQueuedApplyAsync(ApiRoute route, string name, ProbeCaller caller, Guid project, ProbeRequest queue)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(queue);

        var violations = new List<string>();
        var queued = await SendAsync(caller, queue).ConfigureAwait(false);
        violations.AddRange(ForeignIdsIn(queued.Text).Select(id => Describe(route, name, caller, $"{queue} answered {queued} carrying org-A id {id}")));

        if (queued.Status != 201
            || queued.Json is not { ValueKind: JsonValueKind.Object } queuedBody
            || !queuedBody.TryGetProperty("change_id", out var changeIdElement)
            || !changeIdElement.TryGetGuid(out var changeId))
        {
            violations.Add(Describe(route, name, caller, $"{queue} answered {queued}, expected 201 with a change_id (positive control): {Brief(queued)}"));
            return violations;
        }

        var selectionUrl = string.Create(CultureInfo.InvariantCulture, $"/v1/projects/{project:D}/authoring/changes?ids={changeId:D}");
        var selection = await SendAsync(caller, new ProbeRequest(HttpMethod.Get, selectionUrl)).ConfigureAwait(false);
        violations.AddRange(ForeignIdsIn(selection.Text).Select(id => Describe(route, name, caller, $"GET {selectionUrl} answered {selection} carrying org-A id {id}")));

        if (selection.Status != 200 || selection.ETag is null)
        {
            violations.Add(Describe(route, name, caller, $"GET {selectionUrl} answered {selection} without a batch ETag (positive control): {Brief(selection)}"));
            return violations;
        }

        var apply = await SendAsync(
            caller,
            new ProbeRequest(
                HttpMethod.Post,
                string.Create(CultureInfo.InvariantCulture, $"/v1/projects/{project:D}/authoring/changes:apply"),
                new JsonObject { ["change_ids"] = new JsonArray { changeId } },
                selection.ETag)).ConfigureAwait(false);

        if (apply.Status != 200)
        {
            violations.Add(Describe(route, name, caller, $"applying {changeId:D} under {selection.ETag} answered {apply}, expected 200 (positive control): {Brief(apply)}"));
        }
        else if (apply.Json is not { ValueKind: JsonValueKind.Object } appliedBody
            || !appliedBody.TryGetProperty("batch_id", out var batchIdElement)
            || !batchIdElement.TryGetGuid(out _))
        {
            // A comment writes no version, so the batch id in the answer is the only proof the route applied the selection.
            violations.Add(Describe(route, name, caller, $"applying {changeId:D} answered {apply} without a batch_id (positive control): {Brief(apply)}"));
        }

        violations.AddRange(ForeignIdsIn(apply.Text).Select(id => Describe(route, name, caller, $"applying {changeId:D} answered {apply} carrying org-A id {id}")));
        return violations;
    }

    /// <summary>
    /// Org B's own list while the caller's header names another org: the route's <c>{o}</c> wins, so 200 listing
    /// exactly <paramref name="expected"/> and no org-A id.
    /// </summary>
    public async Task<IReadOnlyList<string>> OwnListAsync(ApiRoute route, string name, ProbeCaller caller, ProbeRequest request, IReadOnlyCollection<string> expected, string idField = "id")
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(expected);
        var response = await SendAsync(caller, request).ConfigureAwait(false);
        var violations = new List<string>();
        var ids = Ids(response, idField);
        if (response.Status != 200 || !ids.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expected))
        {
            violations.Add(Describe(route, name, caller, $"{request} answered {response} listing [{string.Join(", ", ids)}], expected exactly [{string.Join(", ", expected)}]"));
        }

        violations.AddRange(ForeignIdsIn(response.Text).Select(id => Describe(route, name, caller, $"{request} answered {response} carrying org-A id {id}")));
        return violations;
    }

    /// <summary>
    /// A service token naming org A in <c>X-Evidgate-Org</c> answers exactly as it does without the header (the
    /// header is ignored for service principals, TenantResolutionFilter.cs:97-98), and carries no org-A id.
    /// </summary>
    public async Task<IReadOnlyList<string>> ServiceHeaderIgnoredAsync(ApiRoute route, string name, ProbeCaller withHeader, ProbeCaller withoutHeader, ProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(withHeader);
        ArgumentNullException.ThrowIfNull(withoutHeader);
        ArgumentNullException.ThrowIfNull(request);
        var real = await SendAsync(withHeader, request).ConfigureAwait(false);
        var control = await SendAsync(withoutHeader, request).ConfigureAwait(false);
        var violations = new List<string>();
        if (real.Status != control.Status || !string.Equals(real.Code, control.Code, StringComparison.Ordinal))
        {
            violations.Add(Describe(route, name, withHeader, $"{request} with X-Evidgate-Org: A answered {real}, without the header answered {control}"));
        }
        else if (!string.Equals(Shape(real), Shape(control), StringComparison.Ordinal))
        {
            violations.Add(Describe(route, name, withHeader, $"{request} with X-Evidgate-Org: A answered {real}, without the header answered {control} (body shape differs)"));
        }

        violations.AddRange(ForeignIdsIn(real.Text).Select(id => Describe(route, name, withHeader, $"{request} answered {real} carrying org-A id {id}")));
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
                $"{request} answered {response}, expected 403 {(requireScopeCode ? "project_scope_mismatch" : "project_scope_mismatch/forbidden")}: {Brief(response)}"));
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
        await SeedUserRowAsync(founder.UserId, founder.Email).ConfigureAwait(false);
        var caller = ProbeCaller.Member("fresh user (X-Evidgate-Org: A)", founder, orgA.Id);
        var violations = new List<string>();

        var slug = "iso-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
        var created = await SendAsync(caller, new(HttpMethod.Post, "/v1/orgs", new JsonObject { ["name"] = "Acme", ["slug"] = slug })).ConfigureAwait(false);
        if (created.Status != 201 || created.Json is not { } body || !body.TryGetProperty("id", out var idElement))
        {
            violations.Add(Describe(route, name, caller, $"POST /v1/orgs answered {created}, expected 201: {Brief(created)}"));
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
                violations.Add(Describe(route, name, caller, $"POST /v1/ci/token answered {response}, expected 401 without a token: {Brief(response)}"));
            }

            violations.AddRange(ForeignIdsIn(response.Text).Select(id => Describe(route, name, caller, $"answer carries org-A id {id}")));
        }

        if (answers[0].Status != answers[1].Status || answers[0].Code != answers[1].Code)
        {
            violations.Add(Describe(route, name, IsolationRouteCases.OrgBAdmin(world), $"member headers changed the answer: anonymous {answers[0]}, org-B member {answers[1]}"));
        }
        else if (!string.Equals(Shape(answers[0]), Shape(answers[1]), StringComparison.Ordinal))
        {
            violations.Add(Describe(route, name, IsolationRouteCases.OrgBAdmin(world), $"member headers changed the body: anonymous {answers[0]}, org-B member {answers[1]}"));
        }

        return violations;
    }

    /// <summary>
    /// A pre-tenant federated-login route (02.3.1-T1.1: <c>GET /v1/auth/oidc/{provider}/start|callback</c>): both answers are
    /// the exact status this host yields, carry no org-A id, and org B's member headers change nothing — a route that
    /// resolves no tenant cannot be pointed at one, and neither route touches the database.
    /// </summary>
    /// <remarks>
    /// The expected status is exact, not a range: the isolation host registers <c>google</c> without a client credential,
    /// so <c>start</c> answers 503 <c>unavailable</c> (LoginEndpoints.cs:115-118) and <c>callback</c> answers 401
    /// <c>token_invalid</c> at the pending-login check that precedes its configuration check (LoginEndpoints.cs:190-194).
    /// Both are designed answers of a pre-tenant route, so anything else fails the case: a 2xx would mean the request
    /// reached a completion, another 5xx a real fault, and a 404 would mean the provider is no longer registered here —
    /// the spec's own answer for an unknown route value, but a change of this host's premise rather than of the tenant,
    /// so the case must be revisited instead of passing.
    /// </remarks>
    /// <param name="route">The served login route.</param>
    /// <param name="name">What the probe proves.</param>
    /// <param name="world">The isolation world (org B's <c>tenant_admin</c> supplies the member headers).</param>
    /// <param name="url">The request URL (a registered provider route value, with the query a refused callback carries).</param>
    /// <param name="expectedStatus">The exact status both callers must receive in this host.</param>
    /// <param name="expectedCode">The exact 00 §9.2 <c>error.code</c> both callers must receive.</param>
    public Task<IReadOnlyList<string>> PreTenantLoginAsync(
        ApiRoute route,
        string name,
        IsolationWorld world,
        string url,
        int expectedStatus,
        string expectedCode) =>
        PreTenantAnswerAsync(route, name, world, new ProbeRequest(HttpMethod.Get, url), expectedStatus, expectedCode);

    /// <summary>
    /// A pre-tenant route of 02.3.1-T2 / 02.3.2-T2 (<c>POST /v1/auth/invite-token</c>, the four
    /// <c>/v1/auth/link</c> routes and the four <c>/v1/auth/sessions</c> + <c>/v1/auth/logout</c> routes): one exact
    /// answer for an anonymous caller and for org B's member headers, the same body shape, and no org-A id — the
    /// headers cannot select a tenant on a route that resolves none.
    /// </summary>
    /// <remarks>
    /// <paramref name="expectedCode"/> is null for the bodiless 204 of the invite staging route: there is no
    /// envelope to compare, so the exact status plus the identical shape carry the case. The link routes require a
    /// live session (02.3.2-T1: they ask the deployment's session authenticator, LinkEndpoints.cs:542-569), so a
    /// probe carrying no session cookie — anonymous or with org B's member headers — is refused 401
    /// <c>unauthenticated</c> before any tenant, pepper or row is read (SessionRequestAuthenticator.cs:87-91). That
    /// is the route's own answer, which is why the case asserts it exactly and must be revisited if these probes
    /// ever present a cookie.
    /// </remarks>
    /// <param name="route">The served route.</param>
    /// <param name="name">What the probe proves.</param>
    /// <param name="world">The isolation world (org B's <c>tenant_admin</c> supplies the member headers).</param>
    /// <param name="request">The request both callers send.</param>
    /// <param name="expectedStatus">The exact status both callers must receive in this host.</param>
    /// <param name="expectedCode">The exact 00 §9.2 <c>error.code</c> both callers must receive, or null for a bodiless answer.</param>
    public async Task<IReadOnlyList<string>> PreTenantAnswerAsync(
        ApiRoute route,
        string name,
        IsolationWorld world,
        ProbeRequest request,
        int expectedStatus,
        string? expectedCode)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(request);

        var caller = IsolationRouteCases.OrgBAdmin(world);
        var anonymous = await SendAsync(ProbeCaller.Anonymous, request).ConfigureAwait(false);
        var named = await SendAsync(caller, request).ConfigureAwait(false);
        var violations = new List<string>();
        var expected = expectedCode is null ? expectedStatus.ToString(CultureInfo.InvariantCulture) : $"{expectedStatus} {expectedCode}";

        if (anonymous.Status != expectedStatus || !string.Equals(anonymous.Code, expectedCode, StringComparison.Ordinal))
        {
            violations.Add(Describe(
                route, name, ProbeCaller.Anonymous,
                $"{request} answered {anonymous} anonymously, expected {expected}: {Brief(anonymous)}"));
        }

        if (named.Status != expectedStatus || !string.Equals(named.Code, expectedCode, StringComparison.Ordinal))
        {
            violations.Add(Describe(
                route, name, caller,
                $"{request} answered {named} naming org B, expected {expected}: {Brief(named)}"));
        }

        // Even with both statuses right, the body must be the same answer: a member
        // header that changes the envelope's members is a select that happened.
        if (!string.Equals(Shape(anonymous), Shape(named), StringComparison.Ordinal))
        {
            violations.Add(Describe(
                route, name, ProbeCaller.Anonymous,
                $"org-B member headers changed a pre-tenant answer: anonymous {anonymous}, naming org B {named}"));
        }

        violations.AddRange(ForeignIdsIn(named.Text).Select(id => Describe(route, name, ProbeCaller.Anonymous, $"{request} answered {named} carrying org-A id {id}")));
        return violations;
    }

    /// <summary>Org B's own read of a non-list answer (a stream): 200, and the text carries no org-A id.</summary>
    public async Task<IReadOnlyList<string>> OwnReadAsync(ApiRoute route, string name, ProbeCaller caller, ProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        var response = await SendAsync(caller, request).ConfigureAwait(false);
        var violations = new List<string>();
        if (response.Status != 200)
        {
            violations.Add(Describe(route, name, caller, $"{request} answered {response}, expected 200: {Brief(response)}"));
        }

        violations.AddRange(ForeignIdsIn(response.Text).Select(id => Describe(route, name, caller, $"{request} answered {response} carrying org-A id {id}")));
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
                return (ids, $"GET {pageUrl} answered {response}: {Brief(response)}", pages);
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

    /// <summary>
    /// Seeds the <c>public."user"</c> row a session implies (02.3.1-T2 / 0044's <c>membership_user_fk</c>):
    /// <c>POST /v1/orgs</c> founds the caller's membership, and that membership names the caller.
    /// </summary>
    private async Task SeedUserRowAsync(Guid user, string email)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(_fixture.OwnerConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """insert into public."user" (id, email) values (@id, @email) on conflict (id) do nothing""",
            connection);
        command.Parameters.AddWithValue("id", user);
        command.Parameters.AddWithValue("email", email);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The <c>id::text</c> values an owner-connection query returns for <paramref name="org"/> (<c>@org</c>), read
    /// inside that org's tenant scope: 0053's owner backfill policy (<c>security_event_owner_backfill</c>) lets a tenant-less
    /// owner read past FORCE RLS, so the query keeps the tenant scope on purpose; the GUC is set in the same transaction.
    /// </summary>
    public async Task<IReadOnlyCollection<string>> TenantIdsAsync(string sql, Guid org)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(_fixture.OwnerConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var scope = new NpgsqlCommand("select set_config('evidgate.org_id', @scope_org, true)", connection, transaction))
        {
            scope.Parameters.AddWithValue("scope_org", org.ToString("D", CultureInfo.InvariantCulture));
            await scope.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("org", org);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

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
        ProbeExpect.NotFound => "404 not_found",
        ProbeExpect.ForeignOrg => "403 or 404 not_found",
        ProbeExpect.EmptyOrNotFound => "404 not_found or a GET list's 200 with no items",
        ProbeExpect.ServiceRefused => "403 or 404 not_found (or a GET list's 200 with no items)",
        ProbeExpect.ArtifactNotFound => "404 artifact_not_found",
        ProbeExpect.NotSuccess => "a 4xx refusal",
        _ => expect.ToString(),
    };

    /// <summary>
    /// A GET route the suite walks to exhaustion (<c>Lists</c>): the only routes allowed to answer 200 with zero
    /// items (S6). An object route or a non-GET route is never in the set, so it must be refused.
    /// </summary>
    private bool IsListRoute(ApiRoute route) =>
        route.Method == "GET" && _listTemplates.Contains(route.Template);

    /// <summary>
    /// The comparable shape of a refusal body: the member-name skeleton plus <c>error.message</c>. Volatile
    /// request/trace ids are masked by name, so two refusals that differ only in such an id compare equal, while a
    /// foreign id that changes the message or the body's members is an existence oracle (S4).
    /// </summary>
    private static string Shape(ProbeResponse response)
    {
        if (response.Json is not { } json)
        {
            return string.Create(CultureInfo.InvariantCulture, $"non-JSON {response.Status}");
        }

        var names = new List<string>();
        Names(json, names);
        var message = json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("message", out var text)
            && text.ValueKind == JsonValueKind.String
                ? text.GetString()!
                : string.Empty;

        return string.Join(",", names) + "|" + message;
    }

    private static void Names(JsonElement element, List<string> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    names.Add(property.Name);
                    Names(property.Value, names);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Names(item, names);
                }

                break;
            default:
                break;
        }
    }

    /// <summary>
    /// The body for a failure message: JSON with every secret-bearing member and every JWT-shaped string masked,
    /// capped at 300 chars; a non-JSON body prints only status, code and length.
    /// </summary>
    private static string Brief(ProbeResponse response)
    {
        if (response.Json is not { } json)
        {
            return string.Create(CultureInfo.InvariantCulture, $"non-JSON body ({response.Status} {response.Code ?? "-"}, {response.Text.Length} chars)");
        }

        var text = Redact(json)?.ToJsonString() ?? "null";
        return text.Length <= 300 ? text : text[..300] + "…";
    }

    private static JsonNode? Redact(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => new JsonObject(element.EnumerateObject()
            .Select(p => KeyValuePair.Create<string, JsonNode?>(p.Name, SecretMembers.Contains(p.Name) ? Redacted() : Redact(p.Value)))),
        JsonValueKind.Array => new JsonArray([.. element.EnumerateArray().Select(Redact)]),
        JsonValueKind.String when element.GetString()!.StartsWith("eyJ", StringComparison.Ordinal) => Redacted(),
        _ => JsonValue.Create(element),
    };

    private static JsonValue Redacted() => JsonValue.Create("[redacted]")!;
}
