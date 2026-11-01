using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Identity.Authorization;
using Evidgate.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// AC5 (02.2.1 §2, §9): a <c>sensitive</c> permission is reachable only by the roles
/// the 00 §2 matrix gives it to, and no response a member can read carries a secret
/// value or a secret-named member.
/// </summary>
/// <remarks>
/// <para>
/// Both halves run against the REAL evaluator and the REAL host: the first seeds one
/// active member per member-kind role and evaluates every sensitive code for every
/// role through <see cref="PermissionEvaluator"/> over the harness's production Dapper
/// path; the second creates real secret-bearing objects through the API and then reads
/// every served GET route as a <c>tenant_admin</c>.
/// </para>
/// <para>
/// The expectation comes from <see cref="RoleMatrixFixture"/> (the 00 §2 matrix) and
/// from <c>contracts/permissions.json</c>'s <c>sensitive</c> flag — never from the
/// evaluator's own answer, so a role that gains a sensitive code in the seed fails
/// here rather than agreeing with itself.
/// </para>
/// </remarks>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed partial class SensitivePermissionTests : CatalogueTestBase
{
    /// <summary>The roles AC5 names as holding no sensitive code at all.</summary>
    private static readonly string[] MustHoldNothing = ["viewer", "ci_producer", "agent"];

    /// <summary>
    /// The served GET routes this scan excludes from its body scan, one reason per entry.
    /// The scan reads the body of every route it calls and requires a 200, because AC5 is about
    /// what a member can read; an excluded route is one whose answer is not a readable resource
    /// body. The set is kept, and asserted against the served table, so a route that cannot be
    /// scanned must be named here with its reason instead of slipping past the scan silently.
    /// </summary>
    private static readonly string[] SkippedGetRoutes =
    [
        // 13.1.1-T2's manual case detail: it only answers for a case this suite does not create
        // (authoring rows are Tenancy.IntegrationTests' subject, and a seeded case here would
        // assert a state the writer never produced). The three authoring lists are scanned.
        "/v1/projects/{p}/authoring/manual-cases/{m}",

        // 08.6.3-T1's kill switch read: it needs `healing:triage`, which the member this suite signs in as does not hold
        // (403 `forbidden`), and its body is modes, timestamps and a redacted system row — no secret value or secret-named
        // member. Its shape, redaction and permission are asserted in Tenancy.IntegrationTests' HealingSwitchEndpointTests.
        "/v1/healing/switch",

        // 13.1.1-T3's authoring version detail: it only answers for a version id this suite does not have (a version
        // exists only after an authoring write, and authoring rows are Tenancy.IntegrationTests' subject). The timeline
        // list is scanned.
        "/v1/projects/{p}/authoring/versions/{v}",

        // 02.3.1-T1.1's pre-tenant login routes answer no resource body: `start` is a 302 to the
        // provider or a 503 `unavailable` envelope (no credential is configured in this host), and
        // `callback` without a pending-login cookie is a 401 `token_invalid` envelope. Neither
        // carries data a member could read, and both have no secret to leak (AC7: no password field
        // exists in the flow); their refusal and outage bodies are asserted in
        // Login/ProviderOutageTests.
        "/v1/auth/oidc/{provider}/start",
        "/v1/auth/oidc/{provider}/callback",

        // 02.3.1-T2's staged second-provider link is the caller's own session state, not a
        // resource: it reads the `__Host-evidgate_link` cookie this suite never stages, so it
        // answers the typed 503 `unavailable` (no session pepper in this host) or 404 `not_found`
        // and carries no body a member could read. Its refusals are asserted in
        // Login/Linking/IdentityLinkingTests.
        "/v1/auth/link/pending",

        // 02.3.2-T2's session list is the caller's own credential state, not a resource a service token can read:
        // it authenticates as the `__Host-evidgate_session` cookie and never as a bearer, so a service-token GET
        // answers 401 `unauthenticated` with no body. Its refusals and its own body are asserted in
        // Sessions/Http/SessionListTests.
        "/v1/auth/sessions",
    ];

    /// <summary>The wire shape of the create bodies (snake_case, like the API).</summary>
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>The ref patterns the probe binding allows (one entry, so the create body is valid).</summary>
    private static readonly string[] BindingRefPatterns = ["refs/heads/main"];

    /// <summary>The secret reference the probe environment revision names (a reference, never a value).</summary>
    private static readonly string[] EnvironmentSecretRefs = ["secret:db_password"];

    /// <summary>The environment key of the probe revision above; the seeded manifest targets it.</summary>
    private const string ProbeEnvironment = "ac5-probe";

    /// <summary>The workflow refs the probe binding allows.</summary>
    private static readonly string[] BindingWorkflowRefs =
        ["acme/ac5-probe/.github/workflows/ci.yml@refs/heads/main"];

    public SensitivePermissionTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// AC5, first half: for every <c>sensitive</c> code, every role the fixture does
    /// not list as a holder is refused by the real evaluator — a member role through a
    /// real active membership, a service role through its claim set, a staff role
    /// through the operator principal. The member-kind holders are the positive
    /// control: they must be granted, or the refusals would prove nothing.
    /// </summary>
    [Fact]
    public async Task Sensitive_Permissions_Are_Unreachable_For_Roles_That_Must_Not_Hold_Them()
    {
        var matrix = RoleMatrixFixture.Load();

        // The fixture is the 00 §2-derived expectation, so the sensitive set is
        // enumerated from it and the catalogue must agree: a code that gains or loses
        // the flag in contracts/permissions.json fails here instead of agreeing with
        // itself.
        var sensitive = matrix.SensitiveCodes;
        sensitive.ShouldNotBeEmpty("the fixture must pin the sensitive codes (AC5)");

        var catalogueSensitive = Catalogue.Document.Permissions
            .Where(entry => entry.Sensitive)
            .Select(entry => entry.Code)
            .Order(StringComparer.Ordinal)
            .ToList();

        catalogueSensitive.ShouldBe(
            sensitive,
            "the catalogue's sensitive set must equal the 00 §2-derived fixture's (AC5)");

        foreach (var role in MustHoldNothing)
        {
            var held = matrix.PermissionsOf(role)
                .Where(code => sensitive.Contains(code, StringComparer.Ordinal))
                .ToList();

            held.ShouldBeEmpty($"'{role}' must hold no sensitive code (AC5), but holds: {string.Join(", ", held)}");
        }

        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        // One active member per member-kind role: the evaluator's member road needs a
        // real membership row, and that row is what makes the verdict about the role.
        var members = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var role in matrix.RoleCodes.Where(role => matrix.KindOf(role) == "member"))
        {
            members[role] = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, role, cancellationToken: Token);
        }

        members.Count.ShouldBeGreaterThan(0, "the fixture must declare member-kind roles");

        await using var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token);
        var evaluator = new PermissionEvaluator(Fixture.Db);

        var evaluated = 0;
        var granted = 0;

        foreach (var code in sensitive)
        {
            var holders = matrix.HoldersOf(code).ToHashSet(StringComparer.Ordinal);

            foreach (var role in matrix.RoleCodes)
            {
                var kind = matrix.KindOf(role);
                var decision = await evaluator.EvaluateAsync(
                    code,
                    PrincipalFor(matrix, role, members),
                    new RequestTenantContext(org, null),
                    Token);

                evaluated++;

                if (kind == "staff")
                {
                    // 02.8.1 is not shipped: the operator principal holds nothing, so a
                    // staff role is refused every code — including the ones the matrix
                    // gives it. Pinned here like DenyPathTests pins the same deferral.
                    decision.IsGranted.ShouldBeFalse(
                        $"'{role}' is a staff role and the operator principal holds nothing until 02.8.1, "
                        + $"so '{code}' must be refused");
                    decision.DenyCode.ShouldBe(
                        ApiErrorCode.Forbidden,
                        $"'{role}' must be refused with 403 forbidden for '{code}'");
                    continue;
                }

                if (holders.Contains(role))
                {
                    decision.IsGranted.ShouldBeTrue(
                        $"'{role}' holds the sensitive '{code}' in the 00 §2 matrix, so the real evaluator must grant it");
                    granted++;
                    continue;
                }

                decision.IsGranted.ShouldBeFalse(
                    $"'{role}' does not hold the sensitive '{code}' (AC5), so the real evaluator must refuse it");
                decision.DenyCode.ShouldBe(
                    ApiErrorCode.Forbidden,
                    $"'{role}' must be refused with 403 forbidden for '{code}'");
            }
        }

        evaluated.ShouldBe(
            sensitive.Count * matrix.RoleCodes.Count,
            "every (sensitive code, role) pair must be evaluated, or the assertion is vacuous");
        granted.ShouldBeGreaterThan(
            0,
            "at least one member role holds a sensitive code, or the positive control is vacuous");
    }

    /// <summary>
    /// AC5, second half: a <c>tenant_admin</c> creates a real ingest token and a real
    /// CI binding through the API, then reads every served GET route it can address.
    /// No response body may carry the captured one-time values, the <c>egt_</c>
    /// plaintext, or a JSON member named like a secret — and the create responses must
    /// be the only place the secret appears.
    /// </summary>
    /// <remarks>
    /// The host is the real Api composition with the <c>EVIDGATE:Ci:*</c> key material
    /// configured: without it the two create routes answer 503 <c>unavailable</c>
    /// ("CI authentication is not configured on this deployment"), so no secret could
    /// be minted and the scan would be vacuous. It publishes the seeded
    /// <c>tenant_admin</c> member for every request that carries no bearer, so the
    /// module's REAL evaluator answers the permission checks (02.2.1-T2.2) — the
    /// point of the test is the shipping authorization path, not a substituted one.
    /// </remarks>
    [Fact]
    public async Task No_Permission_Exposes_Secret_Values()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var membership = await MembershipIdAsync(org, admin);

        await using var factory = CiProducerJwtFactory.Start(
            Fixture,
            CiKeyMaterial.Create(),
            org,
            project,
            admin,
            $"member-{admin:N}@example.test");
        using var client = factory.CreateClient();

        var token = await CreateIngestTokenAsync(client, project);
        var binding = await CreateCiBindingAsync(client, project);

        IngestTokenShape().IsMatch(token.Secret).ShouldBeTrue(
            "the ingest token secret must be the egt_ plaintext (02.4.2); the message must not echo the value");
        binding.Challenge.ShouldNotBeNullOrWhiteSpace("the binding challenge is a one-time value (02.4.1)");

        // Two more one-time values a later GET must never carry: the successor the
        // rotate route mints (the old token verifies for 24 h more), and the
        // short-lived ci_producer JWT the pre-tenant exchange route returns for the
        // ingest token. Both are captured before the scan, so the scan sees them.
        var rotated = await RotateIngestTokenAsync(client, token.Id);
        var exchanged = await ExchangeIngestTokenAsync(client, token.Secret);

        string[] secrets =
        [
            token.Secret,
            IngestSecretPart(token.Secret),
            rotated,
            IngestSecretPart(rotated),
            binding.Challenge,
            exchanged,
        ];

        // The routes whose path names a resource the identity routes do not create:
        // a case, a suite, two of its revisions, an environment revision and a policy
        // revision. Seeded here so the scan addresses every served GET route.
        var seeded = await SeedReadableResourcesAsync(factory, client, org, project);

        var served = ServedRouteTable.From(factory.Services)
            .Where(route => route.Method == "GET")
            .Select(route => route.Template)
            .Order(StringComparer.Ordinal)
            .ToList();

        served.ShouldNotBeEmpty("the host must serve GET routes for the scan to mean anything");

        var callable = CallableGetRoutes(org, project, membership, seeded);

        var unclassified = served
            .Where(template => !callable.ContainsKey(template) && !SkippedGetRoutes.Contains(template, StringComparer.Ordinal))
            .ToList();
        unclassified.ShouldBeEmpty(
            $"every served GET route must be called or named in {nameof(SkippedGetRoutes)}: {string.Join(", ", unclassified)}");

        var stale = callable.Keys
            .Concat(SkippedGetRoutes)
            .Where(template => !served.Contains(template))
            .ToList();
        stale.ShouldBeEmpty($"the callable and skipped sets must name real served GET routes: {string.Join(", ", stale)}");

        var violations = new List<string>();
        var answered = new List<string>();
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (template, url) in callable.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            using var response = await client.GetAsync(url, Token);
            var body = await response.Content.ReadAsStringAsync(Token);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                answered.Add(template);
                bodies[template] = body;
            }
            else
            {
                violations.Add($"{template} answered {(int)response.StatusCode} {response.StatusCode} instead of 200");
            }

            violations.AddRange(SecretScanner.Scan(body, secrets).Select(violation => $"{template}: {violation}"));
        }

        violations.ShouldBeEmpty(
            "no GET response may carry a secret value or a secret-named member (AC5):\n" + string.Join('\n', violations));

        // Non-vacuity: the identity list routes really returned the objects created
        // above, so the clean scan is a statement about real data, not empty pages.
        answered.ShouldContain("/v1/projects/{p}/ingest-tokens");
        answered.ShouldContain("/v1/projects/{p}/ci-bindings");
        answered.ShouldContain("/v1/permissions");
        answered.ShouldContain("/v1/roles");
        answered.ShouldContain("/v1/orgs/{o}/memberships");

        // …and so did every route that names a seeded resource: each body carries the
        // id the scan addressed it with, so none of them answered an empty page.
        (string Template, Guid Id)[] seededRoutes =
        [
            ("/v1/cases/{c}", seeded.CaseId),
            ("/v1/cases/{c}/revisions", seeded.CaseRevisionId),
            ("/v1/suites/{s}", seeded.SuiteId),
            ("/v1/suite-revisions/{r}", seeded.SuiteRevisionId),
            ("/v1/suite-revisions/{r}/diff", seeded.OtherSuiteRevisionId),
            ("/v1/environment-revisions/{id}", seeded.EnvironmentRevisionId),
            ("/v1/policies/{id}", seeded.PolicyRevisionId),
            ("/v1/policies/{id}/revisions", seeded.PolicyRevisionId),
            ("/v1/projects/{p}/policies/head", seeded.PolicyRevisionId),
            ("/v1/projects/{p}/policy-activation", seeded.PolicyRevisionId),
            ("/v1/runs/{r}", seeded.RunId),
        ];

        foreach (var (template, id) in seededRoutes)
        {
            answered.ShouldContain(template);
            bodies[template].ShouldContain(
                id.ToString(),
                Case.Sensitive,
                $"'{template}' must return the seeded object, or the scan is vacuous");
        }

        // The audit trail is where a credential's *metadata* is expected to appear, so
        // both routes must name the objects created above: the clean scan is then a
        // statement about real entries, not an empty page.
        string[] auditRoutes = ["/v1/orgs/{o}/audit", "/v1/orgs/{o}/audit/export"];

        foreach (var template in auditRoutes)
        {
            answered.ShouldContain(template);
            bodies[template].ShouldContain(
                token.Id.ToString(),
                Case.Sensitive,
                $"'{template}' must record the created ingest token, or the scan is vacuous");
            bodies[template].ShouldContain(
                binding.Id.ToString(),
                Case.Sensitive,
                $"'{template}' must record the created CI binding, or the scan is vacuous");
        }

        var tokenList = await client.GetStringAsync($"/v1/projects/{project}/ingest-tokens", Token);
        tokenList.ShouldContain(
            token.Id.ToString(),
            Case.Sensitive,
            "the created ingest token must be listed, or the scan is vacuous");

        var bindingList = await client.GetStringAsync($"/v1/projects/{project}/ci-bindings", Token);
        bindingList.ShouldContain(
            binding.Id.ToString(),
            Case.Sensitive,
            "the created CI binding must be listed, or the scan is vacuous");
    }

    /// <summary>The principal the evaluator sees for a role: a member, a service token, or the operator stub.</summary>
    private static RequestPrincipal PrincipalFor(
        RoleMatrixFixture matrix,
        string role,
        Dictionary<string, Guid> members) =>
        matrix.KindOf(role) switch
        {
            "member" => RequestPrincipal.Member(members[role]),
            "service" => RequestPrincipal.Service(projectScope: null, matrix.PermissionsOf(role)),
            _ => RequestPrincipal.Operator(),
        };

    /// <summary>
    /// The served GET routes this scan addresses with the ids it really has: the
    /// seeded org and project, the seeded membership, the two created objects' list
    /// routes, and the case, suite, suite revisions, environment revision, policy
    /// revision and run <see cref="SeedReadableResourcesAsync"/> arranges. Keyed by the
    /// normalised template <see cref="ServedRouteTable"/> reports, valued by the URL
    /// to call.
    /// </summary>
    private static Dictionary<string, string> CallableGetRoutes(
        Guid org,
        Guid project,
        Guid membership,
        SeededResources seeded) =>
        new(StringComparer.Ordinal)
        {
            ["/v1/orgs"] = "/v1/orgs",
            ["/v1/orgs/{o}"] = $"/v1/orgs/{org}",
            ["/v1/orgs/{o}/projects"] = $"/v1/orgs/{org}/projects",
            ["/v1/projects/{p}"] = $"/v1/projects/{project}",
            ["/v1/orgs/{o}/memberships"] = $"/v1/orgs/{org}/memberships",
            ["/v1/orgs/{o}/memberships/{m}"] = $"/v1/orgs/{org}/memberships/{membership}",
            ["/v1/permissions"] = "/v1/permissions",
            ["/v1/roles"] = "/v1/roles",
            ["/v1/orgs/{o}/audit"] = $"/v1/orgs/{org}/audit",
            ["/v1/orgs/{o}/audit/export"] = $"/v1/orgs/{org}/audit/export",

            // 11.5.1-T5.2: the security-events page. Scanned like the audit list: its items are ids, a closed
            // type/severity pair, an actor `{kind, id}` and details restricted to ids, hashes and counts, and this
            // org has no events seeded here, so the scan is about the envelope and an empty page being clean.
            ["/v1/orgs/{o}/security-events"] = $"/v1/orgs/{org}/security-events",
            ["/v1/projects/{p}/ci-bindings"] = $"/v1/projects/{project}/ci-bindings",
            ["/v1/projects/{p}/ingest-tokens"] = $"/v1/projects/{project}/ingest-tokens",
            ["/v1/projects/{p}/cases"] = $"/v1/projects/{project}/cases",
            ["/v1/projects/{p}/suites"] = $"/v1/projects/{project}/suites",
            ["/v1/projects/{p}/waivers"] = $"/v1/projects/{project}/waivers",
            ["/v1/projects/{p}/quarantine"] = $"/v1/projects/{project}/quarantine",
            ["/v1/projects/{p}/runs"] = $"/v1/projects/{project}/runs",
            // 13.1.1-T2's three authoring lists: one page each, empty for a project with no authoring rows, so the
            // scan covers them like every other list (their detail route is the one skipped entry). 13.1.1-T3's
            // version timeline list is the same shape — an empty page carries no secret — while its detail route
            // needs a version this suite does not seed.
            ["/v1/projects/{p}/authoring/tree"] = $"/v1/projects/{project}/authoring/tree",
            ["/v1/projects/{p}/authoring/checklist-items"] = $"/v1/projects/{project}/authoring/checklist-items",
            ["/v1/projects/{p}/authoring/manual-cases"] = $"/v1/projects/{project}/authoring/manual-cases",
            ["/v1/projects/{p}/authoring/versions"] = $"/v1/projects/{project}/authoring/versions",
            // 13.1.1-T5's export: a project with no authoring rows exports an empty rows array, so the download is
            // callable here like the lists above (no seeded tree, no secret in the file). The scan reads a JSON
            // document, so the call takes the JSON format rather than the CSV default: the permission, the projection
            // and the file itself are the same on the route, and the CSV bytes are not a document the scanner can read.
            ["/v1/projects/{p}/authoring/export"] = $"/v1/projects/{project}/authoring/export?format=json",
            ["/v1/cases/{c}"] = $"/v1/cases/{seeded.CaseId}",
            ["/v1/cases/{c}/revisions"] = $"/v1/cases/{seeded.CaseId}/revisions",
            ["/v1/suites/{s}"] = $"/v1/suites/{seeded.SuiteId}",
            ["/v1/suite-revisions/{r}"] = $"/v1/suite-revisions/{seeded.SuiteRevisionId}",
            ["/v1/suite-revisions/{r}/diff"] =
                $"/v1/suite-revisions/{seeded.SuiteRevisionId}/diff?against={seeded.OtherSuiteRevisionId}",
            ["/v1/environment-revisions/{id}"] = $"/v1/environment-revisions/{seeded.EnvironmentRevisionId}",
            ["/v1/policies/{id}"] = $"/v1/policies/{seeded.PolicyRevisionId}",
            ["/v1/policies/{id}/revisions"] = $"/v1/policies/{seeded.PolicyRevisionId}/revisions",
            // F18's two policy reads: the seeded revision is both the project's head and the pointer's target, so
            // both answer 200 on a project this suite prepared (the activation row is seeded with the revision).
            ["/v1/projects/{p}/policies/head"] = $"/v1/projects/{project}/policies/head",
            ["/v1/projects/{p}/policy-activation"] = $"/v1/projects/{project}/policy-activation",
            ["/v1/runs/{r}"] = $"/v1/runs/{seeded.RunId}",
            // F52 (S143): the run's attempts page; the seeded run has no attempt row, so the page is a clean 200 with
            // `items: []` — no secret, and the scan is not vacuous about the route.
            ["/v1/runs/{r}/attempts"] = $"/v1/runs/{seeded.RunId}/attempts",
            // The run's evidence-revision page: the seeded run has no revision, so the page is a clean 200 with
            // `items: []` — no secret, and the scan is not vacuous about the route.
            ["/v1/runs/{run_id}/evidence-revisions"] = $"/v1/runs/{seeded.RunId}/evidence-revisions",
            // The run's decision: a pending body carries no verdict, hash or reason yet, so it is clean by construction
            // and the scan still covers the route.
            ["/v1/runs/{r}/decision"] = $"/v1/runs/{seeded.RunId}/decision",
        };

    /// <summary>
    /// Arranges the resources the remaining served GET routes name, so no route is
    /// skipped: a case and its revision through the catalog module's own discovery
    /// service (03.1.1 — no served route creates a case), a suite and two of its
    /// revisions through the served routes as the same <c>tenant_admin</c> (it holds
    /// <c>suite:write</c>), an environment revision through the served route (which
    /// creates its environment), and a policy revision through the harness's owner
    /// seam (<c>tenant_admin</c> does not hold <c>policy:write</c>, 00 §2). The run of
    /// 05.3.1 needs a verified manifest, so one is uploaded and one run created
    /// through the served routes.
    /// </summary>
    private async Task<SeededResources> SeedReadableResourcesAsync(
        CiProducerJwtFactory factory,
        HttpClient client,
        Guid org,
        Guid project)
    {
        var (caseId, caseRevisionId) = await SeedCaseAsync(factory, org, project);

        var suiteId = await CreateSuiteAsync(client, project);
        var suiteRevisionId = await CreateSuiteRevisionAsync(
            client,
            suiteId,
            new { members = new[] { new { case_revision_id = caseRevisionId } } });

        // A second revision of the same suite, so the diff route has an "against".
        var otherSuiteRevisionId = await CreateSuiteRevisionAsync(client, suiteId, new { });

        var environmentRevisionId = await CreateEnvironmentRevisionAsync(client, project);
        var policyRevisionId = await SeedPolicyRevisionAsync(org, project);

        // 05.3.1-T1: a manifest and one run of it. Nothing else creates a run (the
        // create route needs a verified manifest), so GET /v1/runs/{r} would be
        // unaddressable — and the scan would miss the route — without both.
        var manifestId = await CreateManifestAsync(client, project, suiteRevisionId, [caseRevisionId], environmentRevisionId);
        var runId = await CreateRunAsync(client, project, manifestId);

        return new SeededResources(
            caseId,
            caseRevisionId,
            suiteId,
            suiteRevisionId,
            otherSuiteRevisionId,
            environmentRevisionId,
            policyRevisionId,
            runId);
    }

    /// <summary>
    /// Registers one discovered test file through the catalog module's own service —
    /// the production path that writes <c>test_case</c> and <c>test_revision</c>
    /// (03.1.1) — and reads both ids back inside the same tenant scope.
    /// </summary>
    private static async Task<(Guid CaseId, Guid CaseRevisionId)> SeedCaseAsync(
        CiProducerJwtFactory factory,
        Guid org,
        Guid project)
    {
        const string Path = "ac5/probe.spec.ts";

        await using var services = factory.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var scopes = provider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(org, null, false, Token);

        var outcome = await provider.GetRequiredService<ICaseIdentityService>()
            .RegisterDiscoveredAsync(
                project,
                new string('a', 12),
                [
                    new SourceFile(
                        Path,
                        "// evidgate.id: 018f2f0e-8a52-7c4b-9d3e-000000000001\ntest('ac5', () => {});",
                        "playwright",
                        "typescript"),
                ],
                Token);

        outcome.Succeeded.ShouldBeTrue(outcome.Failure);

        // The Dapper path of the host's own registration, so the read runs on this
        // scope's connection and transaction exactly like a module query (C3, C5).
        var db = provider.GetRequiredService<ITenantDb>();

        var caseId = await db.QuerySingleOrDefaultAsync<Guid>(
            "select c.id from public.test_case c where c.project_id = @project_id and c.file_path = @path",
            new { project_id = project, path = Path },
            Token);

        caseId.ShouldNotBe(Guid.Empty, "the discovery service must have written the case");

        var caseRevisionId = await db.QuerySingleOrDefaultAsync<Guid>(
            "select r.id from public.test_revision r where r.case_id = @case_id",
            new { case_id = caseId },
            Token);

        caseRevisionId.ShouldNotBe(Guid.Empty, "the discovery service must have written the case's revision");

        await scope.CommitAsync(Token);

        return (caseId, caseRevisionId);
    }

    /// <summary>Creates a suite through the served route and returns its id.</summary>
    private static async Task<Guid> CreateSuiteAsync(HttpClient client, Guid project)
    {
        var (status, body) = await PostAsync(
            client,
            $"/v1/projects/{project}/suites",
            new { Name = "ac5-probe", Slug = "ac5-probe" });

        status.ShouldBe(HttpStatusCode.Created, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>Creates a suite revision through the served route and returns its id.</summary>
    private static async Task<Guid> CreateSuiteRevisionAsync(HttpClient client, Guid suite, object body)
    {
        var (status, text) = await PostAsync(client, $"/v1/suites/{suite}/revisions", body);

        status.ShouldBeOneOf([HttpStatusCode.Created, HttpStatusCode.OK], text);

        using var document = JsonDocument.Parse(text);
        return document.RootElement.GetProperty("suite_revision_id").GetGuid();
    }

    /// <summary>
    /// Creates an environment revision through the served route (the handler creates
    /// the environment when the key is new) and returns the revision's id.
    /// </summary>
    private static async Task<Guid> CreateEnvironmentRevisionAsync(HttpClient client, Guid project)
    {
        var (status, body) = await PostAsync(
            client,
            $"/v1/projects/{project}/environments/ac5-probe/revisions",
            new
            {
                Name = "AC5 probe",
                BaseUrls = new Dictionary<string, string>(StringComparer.Ordinal) { ["web"] = "https://ac5.example.test" },
                DependencyVersions = new Dictionary<string, string>(StringComparer.Ordinal) { ["postgres"] = "18.0" },
                SecretRefs = EnvironmentSecretRefs,
            });

        status.ShouldBeOneOf([HttpStatusCode.Created, HttpStatusCode.OK], body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("revision_id").GetGuid();
    }

    /// <summary>
    /// Uploads a manifest through the served route (05.1.1) and returns its id: the one
    /// case revision it is given, in one variant on the probe environment, as one shard
    /// — the smallest plan the manifest verifier accepts (the scan only needs a
    /// manifest to exist, because the run route requires a verified one).
    /// </summary>
    private static async Task<Guid> CreateManifestAsync(
        HttpClient client,
        Guid project,
        Guid suiteRevision,
        Guid[] caseRevisions,
        Guid environmentRevision)
    {
        var variant = new { Params = new Dictionary<string, string>(StringComparer.Ordinal) { ["browser"] = "chromium" } };

        var (status, body) = await PostAsync(
            client,
            $"/v1/projects/{project}/manifests",
            new
            {
                SchemaVersion = 1,
                SuiteRevisionId = suiteRevision,
                EnvironmentRevisionId = environmentRevision,
                PlannedExecutions = caseRevisions
                    .Select(id => new { CaseRevisionId = id, Variant = variant, TargetEnv = ProbeEnvironment })
                    .ToArray(),
                Matrix = new
                {
                    CaseRevisionIds = caseRevisions,
                    Variants = new[] { variant },
                    TargetEnvs = new[] { ProbeEnvironment },
                },
                ShardPlan = new
                {
                    Shards = new[] { new { ShardId = "s0", FromIndex = 0, ToIndex = caseRevisions.Length } },
                },
                EnvNeeds = new { TargetEnvs = new[] { new { TargetEnv = ProbeEnvironment } } },
            });

        status.ShouldBe(HttpStatusCode.Created, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("manifest_id").GetGuid();
    }

    /// <summary>
    /// Creates one run of <paramref name="manifest"/> through the served route
    /// (05.2.1) and returns its id; <c>GET /v1/runs/{r}</c> addresses the row it writes.
    /// </summary>
    private static async Task<Guid> CreateRunAsync(HttpClient client, Guid project, Guid manifest)
    {
        var (status, body) = await PostAsync(client, $"/v1/projects/{project}/runs", new { ManifestId = manifest });

        status.ShouldBe(HttpStatusCode.Created, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("run_id").GetGuid();
    }

    /// <summary>
    /// Arranges one policy revision through the harness's owner seam: the served
    /// create route needs <c>policy:write</c>, which the 00 §2 matrix does not give
    /// <c>tenant_admin</c>, so the row is arranged like the policy suites arrange it.
    /// The stored <c>hash</c> is the real content address of the body
    /// (<see cref="CanonicalHash"/>, the same digest the module's hasher computes), or
    /// the read route would refuse the row as corrupted. The activation pointer (F18)
    /// is arranged here too: the served activation route needs the same
    /// <c>policy:write</c>, and without a pointer <c>GET …/policy-activation</c> would
    /// answer <c>404</c> and the scan would skip it.
    /// </summary>
    private async Task<Guid> SeedPolicyRevisionAsync(Guid org, Guid project)
    {
        const string Body = "{}";
        var id = Guid.CreateVersion7();

        await Fixture.SeedAsync(
            """
            insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, created_by)
            values (@id, @org_id, @project_id, 1, @hash, @body::jsonb, @created_by);

            insert into public.policy_activation (id, org_id, project_id, policy_revision_id, activated_by)
            values (@activation_id, @org_id, @project_id, @id, @created_by)
            """,
            [
                ("id", id),
                ("org_id", org),
                ("project_id", project),
                ("hash", CanonicalHash.Compute(Body)),
                ("body", Body),
                ("created_by", Guid.CreateVersion7()),
                ("activation_id", Guid.CreateVersion7()),
            ],
            Token);

        return id;
    }

    /// <summary>
    /// Posts a snake_case body with a fresh idempotency key, like the API expects, and
    /// returns the status and the body text.
    /// </summary>
    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(
        HttpClient client,
        string url,
        object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: Wire),
        };

        request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString("N"));

        using var response = await client.SendAsync(request, Token);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Token));
    }

    /// <summary>The ids the scan addresses the resource-naming GET routes with.</summary>
    private sealed record SeededResources(
        Guid CaseId,
        Guid CaseRevisionId,
        Guid SuiteId,
        Guid SuiteRevisionId,
        Guid OtherSuiteRevisionId,
        Guid EnvironmentRevisionId,
        Guid PolicyRevisionId,
        Guid RunId);

    /// <summary>The id of the seeded member's membership, read under RLS as the org.</summary>
    private async Task<Guid> MembershipIdAsync(Guid org, Guid userId)
    {
        await using var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token);

        var membership = await scope.Db.QuerySingleOrDefaultAsync<Guid>(
            "select id from public.membership where org_id = @org_id and user_id = @user_id",
            new { org_id = org, user_id = userId },
            Token);

        membership.ShouldNotBe(Guid.Empty, "the seeded member must have a membership row");
        return membership;
    }

    /// <summary>
    /// Creates an ingest token through the real route and captures the one-time
    /// secret. The create response is asserted to be the place the secret appears.
    /// </summary>
    private static async Task<CreatedToken> CreateIngestTokenAsync(HttpClient client, Guid project)
    {
        using var response = await client.PostAsync(
            $"/v1/projects/{project}/ingest-tokens",
            JsonContent.Create(new { Name = "ac5-probe" }, options: Wire),
            Token);

        var body = await response.Content.ReadAsStringAsync(Token);

        // Booleans, not ShouldBe/ShouldContain: a failure message must never echo the
        // one-time value the create response carries.
        (response.StatusCode == HttpStatusCode.Created).ShouldBeTrue(
            $"the ingest token create route must answer 201, not {(int)response.StatusCode}");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var secret = root.GetProperty("secret").GetString()!;

        body.Contains(secret, StringComparison.Ordinal).ShouldBeTrue(
            "the create response is the one place the secret appears");

        return new CreatedToken(root.GetProperty("token").GetProperty("id").GetGuid(), secret);
    }

    /// <summary>Creates a CI binding through the real route and captures the one-time challenge.</summary>
    private static async Task<CreatedBinding> CreateCiBindingAsync(HttpClient client, Guid project)
    {
        using var response = await client.PostAsync(
            $"/v1/projects/{project}/ci-bindings",
            JsonContent.Create(
                new
                {
                    Provider = "github_actions",
                    RepositoryId = 424242L,
                    RepositoryFullName = "acme/ac5-probe",
                    AllowedRefPatterns = BindingRefPatterns,
                    AllowedWorkflowRefs = BindingWorkflowRefs,
                },
                options: Wire),
            Token);

        var body = await response.Content.ReadAsStringAsync(Token);

        (response.StatusCode == HttpStatusCode.Created).ShouldBeTrue(
            $"the CI binding create route must answer 201, not {(int)response.StatusCode}");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var challenge = root.GetProperty("binding_challenge").GetString()!;

        body.Contains(challenge, StringComparison.Ordinal).ShouldBeTrue(
            "the create response is the one place the challenge appears");

        return new CreatedBinding(root.GetProperty("binding").GetProperty("id").GetGuid(), challenge);
    }

    /// <summary>
    /// Rotates the ingest token through the real route and captures the successor's
    /// one-time secret (the old token keeps verifying for 24 h, 02.4.2).
    /// </summary>
    private static async Task<string> RotateIngestTokenAsync(HttpClient client, Guid tokenId)
    {
        var (status, body) = await PostAsync(client, $"/v1/ingest-tokens/{tokenId}/rotate", new { });

        (status == HttpStatusCode.OK).ShouldBeTrue(
            $"the ingest token rotate route must answer 200, not {(int)status}");

        using var document = JsonDocument.Parse(body);
        var secret = document.RootElement.GetProperty("secret").GetString()!;

        IngestTokenShape().IsMatch(secret).ShouldBeTrue(
            "the rotated secret must be the egt_ plaintext (02.4.2); the message must not echo the value");

        return secret;
    }

    /// <summary>
    /// Exchanges the ingest token for a short-lived <c>ci_producer</c> JWT through the
    /// real pre-tenant route (anonymous: the credential travels in the body) and
    /// captures the token.
    /// </summary>
    private static async Task<string> ExchangeIngestTokenAsync(HttpClient client, string ingestToken)
    {
        var (status, body) = await PostAsync(client, "/v1/ci/token", new { IngestToken = ingestToken });

        (status == HttpStatusCode.OK).ShouldBeTrue(
            $"the CI token exchange route must answer 200, not {(int)status}");

        using var document = JsonDocument.Parse(body);
        var token = document.RootElement.GetProperty("token").GetString()!;

        SecretScanner.JwtShaped().IsMatch(token).ShouldBeTrue(
            "the exchange must return a JWT (02.4.1); the message must not echo the value");

        return token;
    }

    /// <summary>
    /// The secret half of a minted ingest token — everything after
    /// <c>egt_&lt;prefix&gt;_</c>. A GET that leaks only the half still leaks the
    /// credential, so the scan looks for it too.
    /// </summary>
    private static string IngestSecretPart(string token)
    {
        var match = IngestTokenShape().Match(token);

        match.Success.ShouldBeTrue("a minted ingest token must have the egt_ shape (02.4.2)");

        return match.Groups["secret"].Value;
    }

    /// <summary>
    /// The shape of a minted ingest token: <c>egt_</c>, the 12-character lookup prefix,
    /// and the secret half (<c>IngestTokenFormat</c>, 02.4.2).
    /// </summary>
    [GeneratedRegex(
        @"^egt_[A-Za-z0-9]{12}_(?<secret>[A-Za-z0-9_-]{40,})$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex IngestTokenShape();

    /// <summary>An ingest token created through the API: its id and its one-time secret.</summary>
    private sealed record CreatedToken(Guid Id, string Secret);

    /// <summary>A CI binding created through the API: its id and its one-time challenge.</summary>
    private sealed record CreatedBinding(Guid Id, string Challenge);
}

/// <summary>
/// The AC5 secret scanner as a pure function over a response body: the JSON member
/// names on the deny-list, a <c>secret_refs</c> value that is not a reference, a
/// JWT-shaped value, any captured secret value or <c>egt_</c> plaintext, and a
/// non-empty body that is neither JSON nor NDJSON.
/// </summary>
/// <remarks>
/// <para>
/// The deny-list is defined once, here, and matched case-insensitively as a substring
/// of the member name, so <c>binding_challenge</c>, <c>secret</c>, <c>token</c>,
/// <c>password</c>, <c>pepper</c>, <c>hmac</c>, <c>hash_of_secret</c>,
/// <c>credential</c>, <c>private_key</c>, <c>api_key</c>, <c>signing</c>,
/// <c>authorization</c>, <c>cookie</c>, <c>bearer</c>, <c>jwt</c>, <c>digest</c>,
/// <c>verifier</c> and <c>salt</c> all fire.
/// </para>
/// <para>
/// The allowed exceptions are the members that legitimately mention one of those words
/// without carrying a secret: <c>prefix</c> (the public lookup prefix of an ingest
/// token, never the secret), <c>next_cursor</c>, <c>manifest_hash</c>,
/// <c>token_id</c>, <c>entry_hash</c>, <c>content_hash</c>, <c>artifact_hash</c> and
/// <c>checksum</c> (content digests, not credential digests), and <c>secret_refs</c>
/// (the environment revision's <c>secret:&lt;name&gt;</c> references — the create route
/// refuses anything that is not a reference or that looks like a value, so the member
/// names secrets without carrying one, 05.1.2). The allowlist is an exact member-name
/// set, never a substring rule, and <c>secret_refs</c> is the one entry whose values
/// are checked instead: every value must match <see cref="SecretReference"/>.
/// </para>
/// <para>
/// The deny-list is deliberately test-side: it is the expectation a response must
/// meet, so it is not derived from the production <c>SecretShape</c> helper, which
/// answers a different question (whether a write-time key or value looks like a
/// secret) and treats every long hex run as one — the audit chain's hashes would trip
/// it.
/// </para>
/// </remarks>
internal static partial class SecretScanner
{
    /// <summary>The substrings that make a member name secret-bearing.</summary>
    internal static readonly string[] DenyTerms =
    [
        "secret", "token", "challenge", "password", "pepper", "hmac", "hash_of_secret",
        "credential", "private_key", "api_key", "signing", "authorization", "cookie",
        "bearer", "jwt", "digest", "verifier", "salt",
    ];

    /// <summary>The member names that may contain a deny term without carrying a secret.</summary>
    internal static readonly IReadOnlySet<string> AllowedMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "prefix",
        "next_cursor",
        "manifest_hash",
        "token_id",
        "entry_hash",
        "content_hash",
        "artifact_hash",
        "checksum",
        "secret_refs",
    };

    /// <summary>
    /// The reference shape a <c>secret_refs</c> value must have — the same pattern the
    /// create route enforces (<c>EnvironmentRevisionRules.Reference</c>, 05.1.2).
    /// </summary>
    [GeneratedRegex(
        "^secret:[A-Za-z0-9_.-]{1,64}$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex SecretReference();

    /// <summary>A JWT-shaped value: the base64url header <c>eyJ</c> and two dots.</summary>
    [GeneratedRegex(
        @"eyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    internal static partial Regex JwtShaped();

    /// <summary>
    /// The member-level violations of a body, ordinal: a member name on the deny-list,
    /// or a <c>secret_refs</c> value that is not a reference. The body may be a single
    /// JSON document or NDJSON (the audit export streams one object per line); a body
    /// that parses as neither contributes nothing.
    /// </summary>
    private static IReadOnlyList<string> MemberViolations(string body)
    {
        var violations = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var element in Elements(body))
        {
            Collect(element, violations);
        }

        return [.. violations];
    }

    /// <summary>The root elements of a body: one for a JSON document, one per NDJSON line.</summary>
    private static IEnumerable<JsonElement> Elements(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            yield break;
        }

        if (TryParse(body, out var whole))
        {
            using (whole)
            {
                yield return whole.RootElement.Clone();
            }

            yield break;
        }

        foreach (var line in body.Split('\n'))
        {
            if (line.Trim().Length == 0 || !TryParse(line, out var document))
            {
                continue;
            }

            using (document)
            {
                yield return document.RootElement.Clone();
            }
        }
    }

    /// <summary>
    /// Every violation of <paramref name="body"/>: one line per member-level violation,
    /// one per captured secret value it contains, one when it carries an <c>egt_</c>
    /// plaintext or a JWT-shaped value, and one when a non-empty body is neither JSON
    /// nor NDJSON.
    /// </summary>
    internal static IReadOnlyList<string> Scan(string body, IReadOnlyCollection<string> secretValues)
    {
        ArgumentNullException.ThrowIfNull(secretValues);

        var violations = MemberViolations(body).ToList();

        if (!string.IsNullOrWhiteSpace(body) && !IsJsonOrNdjson(body))
        {
            violations.Add("the body is neither a JSON document nor NDJSON");
        }

        foreach (var secret in secretValues)
        {
            if (body.Contains(secret, StringComparison.Ordinal))
            {
                violations.Add("the body contains a captured secret value");
            }
        }

        if (body.Contains("egt_", StringComparison.Ordinal))
        {
            violations.Add("the body contains an egt_ token plaintext");
        }

        if (JwtShaped().IsMatch(body))
        {
            violations.Add("the body contains a JWT-shaped value");
        }

        return violations;
    }

    /// <summary>True when a non-empty body is a JSON document or NDJSON.</summary>
    private static bool IsJsonOrNdjson(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return true;
        }

        if (TryParse(body, out var whole))
        {
            whole.Dispose();
            return true;
        }

        foreach (var line in body.Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            if (!TryParse(line, out var document))
            {
                return false;
            }

            document.Dispose();
        }

        return true;
    }

    private static bool TryParse(string body, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(body);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    private static void Collect(JsonElement element, SortedSet<string> violations)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsForbidden(property.Name))
                    {
                        violations.Add($"a JSON member named '{property.Name}' is on the secret deny-list");
                    }

                    if (IsSecretRefs(property.Name))
                    {
                        CollectSecretRefs(property.Value, violations);
                    }

                    Collect(property.Value, violations);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, violations);
                }

                break;
        }
    }

    /// <summary>Every <c>secret_refs</c> value that is not a <c>secret:&lt;name&gt;</c> reference.</summary>
    private static void CollectSecretRefs(JsonElement value, SortedSet<string> violations)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            violations.Add("a JSON member named 'secret_refs' is not an array of references");
            return;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !SecretReference().IsMatch(item.GetString()!))
            {
                violations.Add("a JSON member named 'secret_refs' carries a value that is not a secret:<name> reference");
            }
        }
    }

    private static bool IsSecretRefs(string member) =>
        member.Equals("secret_refs", StringComparison.OrdinalIgnoreCase);

    private static bool IsForbidden(string member) =>
        !AllowedMembers.Contains(member)
        && DenyTerms.Any(term => member.Contains(term, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// AC5's seeded violation: the scanner is a pure function — a synthetic body carrying a
/// forbidden member and a captured secret must report both, and a clean body must report
/// nothing. Tagged <c>Docker</c> anyway: this assembly's <c>DatabaseFixture</c> assembly
/// fixture starts a container before any test runs, so the Docker-less clean-build smoke
/// must skip the class like every other one here.
/// </summary>
[Trait("Category", "Docker")]
public sealed class SecretScannerTests
{
    /// <summary>A body with only allowlisted members and no secret is clean; a seeded one reports both violations.</summary>
    [Fact]
    public void Scan_Reports_A_Seeded_Forbidden_Member_And_Secret_Value()
    {
        const string secret = "egt_abcdefghijkl_secretvalue";

        var clean = """{"items":[{"id":"1","prefix":"abcdefghijkl","next_cursor":null}]}""";
        SecretScanner.Scan(clean, [secret])
            .ShouldBeEmpty("a body with only allowlisted members and no secret value is clean");

        var seeded = $$"""{"items":[{"id":"1","prefix":"abcdefghijkl","binding_challenge":"xyz","note":"{{secret}}"}]}""";
        var violations = SecretScanner.Scan(seeded, [secret]);

        violations.Any(violation => violation.Contains("binding_challenge", StringComparison.Ordinal))
            .ShouldBeTrue($"the seeded forbidden member must be reported; got: {string.Join(" | ", violations)}");
        violations.Any(violation => violation.Contains("secret value", StringComparison.Ordinal))
            .ShouldBeTrue($"the seeded captured secret value must be reported; got: {string.Join(" | ", violations)}");
    }

    /// <summary>Every deny term fires on a member name that is not allowlisted.</summary>
    [Fact]
    public void Scan_Reports_Every_Seeded_Deny_Term()
    {
        SecretScanner.DenyTerms.ShouldNotBeEmpty("the deny-list must not be empty");

        foreach (var term in SecretScanner.DenyTerms)
        {
            var body = $$"""{"{{term}}_value":"x"}""";

            SecretScanner.Scan(body, [])
                .ShouldNotBeEmpty($"the deny term '{term}' must fire on a member name");
        }
    }

    /// <summary>A <c>secret_refs</c> member is allowed, but only with reference values.</summary>
    [Fact]
    public void Scan_Reports_A_Seeded_Secret_Refs_Value_That_Is_Not_A_Reference()
    {
        var references = """{"secret_refs":["secret:db_password","secret:api_key"]}""";
        SecretScanner.Scan(references, [])
            .ShouldBeEmpty("secret:<name> references are the allowlisted shape (05.1.2)");

        var seeded = """{"secret_refs":["secret:db_password","hunter2"]}""";
        SecretScanner.Scan(seeded, [])
            .ShouldNotBeEmpty("a secret_refs value that is not a reference must be reported");
    }

    /// <summary>A JWT-shaped value and a non-JSON body are both reported.</summary>
    [Fact]
    public void Scan_Reports_A_Seeded_Jwt_And_A_Non_Json_Body()
    {
        // Assembled at runtime so the repository's literal-secret scan (jwt rule) never sees a JWT-shaped literal.
        var token = string.Join('.', "ey" + "JhbGciOiJIUzI1NiJ9", "ey" + "JzdWIiOiIxIn0", "signature");
        var jwt = $$"""{"note":"{{token}}"}""";
        SecretScanner.Scan(jwt, [])
            .ShouldNotBeEmpty("a JWT-shaped value must be reported");

        SecretScanner.Scan("<html>not json</html>", [])
            .ShouldNotBeEmpty("a non-empty body that is neither JSON nor NDJSON must be reported");

        SecretScanner.Scan(string.Empty, [])
            .ShouldBeEmpty("an empty body is not a violation");
    }
}
