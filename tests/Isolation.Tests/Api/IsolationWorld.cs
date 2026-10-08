using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Isolation.Tests.Api;

/// <summary>One seeded member: who the stand-in authenticator is told the caller is, and what they hold.</summary>
/// <param name="Label">The role combination's name, e.g. <c>qa_engineer+test_maintainer</c>; the same label exists in both orgs.</param>
/// <param name="UserId">The user id the member header carries.</param>
/// <param name="Email">The member's address (the same local text in both orgs).</param>
/// <param name="MembershipId">The active membership in the org.</param>
/// <param name="RoleCodes">The built-in role codes the membership holds, in grant order.</param>
public sealed record IsolationUser(string Label, Guid UserId, string Email, Guid MembershipId, IReadOnlyList<string> RoleCodes);

/// <summary>A role a membership holds, addressable by <c>DELETE /v1/orgs/{o}/memberships/{m}/roles/{r}</c>.</summary>
/// <param name="MembershipId">The membership (<c>{m}</c>).</param>
/// <param name="RoleId">The role (<c>{r}</c>).</param>
public sealed record IsolationRoleGrant(Guid MembershipId, Guid RoleId);

/// <summary>One seeded project and every object id under it.</summary>
public sealed class IsolationProject
{
    /// <summary>The project id (<c>{p}</c>).</summary>
    public required Guid Id { get; init; }

    /// <summary>The slug — identical in both orgs (<c>web</c>, <c>api</c>).</summary>
    public required string Slug { get; init; }

    /// <summary>The project's first <c>ci_binding</c> (<c>{b}</c>).</summary>
    public required Guid BindingId { get; init; }

    /// <summary>Every <c>ci_binding</c> of the project, <see cref="BindingId"/> first; at least two, so the list pages.</summary>
    public required IReadOnlyList<Guid> BindingIds { get; init; }

    /// <summary>An active ingest token (<c>{t}</c>).</summary>
    public required Guid ActiveTokenId { get; init; }

    /// <summary>The active token's one-time secret (a synthetic per-run value, never persisted in clear).</summary>
    public required string ActiveTokenSecret { get; init; }

    /// <summary>A revoked ingest token (<c>{t}</c>), revoked through <c>DELETE /v1/ingest-tokens/{t}</c>.</summary>
    public required Guid RevokedTokenId { get; init; }

    /// <summary>The revoked token's one-time secret (a synthetic per-run value; <c>POST /v1/ci/token</c> must refuse it).</summary>
    public required string RevokedTokenSecret { get; init; }

    /// <summary>A real <c>ci_producer</c> JWT bound to this org and project (<c>POST /v1/ci/token</c>).</summary>
    public required string CiProducerJwt { get; init; }

    /// <summary>
    /// An OIDC-minted <c>ci_producer</c> JWT bound to this org and project: the exchange of a workflow token
    /// signed by the test JWKS, which also activated the project's primary <c>ci_binding</c> (02.9.1-T3, from #19).
    /// </summary>
    public required string OidcProducerJwt { get; init; }

    /// <summary>The test cases (<c>{c}</c>), in file-path order; at least three, so a list pages at limit 1 or 2.</summary>
    public required IReadOnlyList<Guid> CaseIds { get; init; }

    /// <summary>One case revision per case, aligned with <see cref="CaseIds"/> (the first of <see cref="CaseRevisions"/>).</summary>
    public required IReadOnlyList<Guid> CaseRevisionIds { get; init; }

    /// <summary>Every revision of each case, aligned with <see cref="CaseIds"/>; at least two per case, so the list pages.</summary>
    public required IReadOnlyList<IReadOnlyList<Guid>> CaseRevisions { get; init; }

    /// <summary>The suites (<c>{s}</c>): <c>smoke</c> then <c>regression</c>.</summary>
    public required IReadOnlyList<Guid> SuiteIds { get; init; }

    /// <summary>One suite revision per suite (<c>{r}</c> of <c>/suite-revisions/{r}</c>), aligned with <see cref="SuiteIds"/>.</summary>
    public required IReadOnlyList<Guid> SuiteRevisionIds { get; init; }

    /// <summary>The revision of environment <see cref="IsolationWorld.EnvironmentKey"/> (<c>{id}</c> of <c>/environment-revisions/{id}</c>).</summary>
    public required Guid EnvironmentRevisionId { get; init; }

    /// <summary>The project's policy revision (<c>{id}</c> of <c>/v1/policies/{id}</c>).</summary>
    public required Guid PolicyRevisionId { get; init; }

    /// <summary>The project's manifest.</summary>
    public required Guid ManifestId { get; init; }

    /// <summary>The project's waivers (<c>{w}</c> of <c>/v1/waivers/{w}/…</c>), in seeding order; at least two, so the list pages.</summary>
    public required IReadOnlyList<Guid> WaiverIds { get; init; }

    /// <summary>The first waiver (<c>{w}</c>), requested by the org's admin; <see cref="WaiverIds"/>[0].</summary>
    public Guid WaiverId => WaiverIds[0];
}

/// <summary>One seeded org: its admin, members, projects and every id a route can name.</summary>
public sealed class IsolationOrg
{
    /// <summary>The org id (<c>{o}</c>).</summary>
    public required Guid Id { get; init; }

    /// <summary>The org slug (<c>acme</c> or <c>acme-2</c>; the display name is <c>Acme</c> in both).</summary>
    public required string Slug { get; init; }

    /// <summary>The founder (label <c>tenant_admin</c>), holding <c>tenant_admin</c> + <c>release_approver</c> as the create route grants them.</summary>
    public required IsolationUser Admin { get; init; }

    /// <summary>The non-admin members, one per built-in role combination.</summary>
    public required IReadOnlyList<IsolationUser> Members { get; init; }

    /// <summary>A pending invite created through <c>POST /v1/orgs/{o}/memberships</c> (no user bound yet).</summary>
    public required Guid PendingInviteMembershipId { get; init; }

    /// <summary>
    /// Every invite address the org owns: the shared <c>invitee@example.test</c>, plus org A's own
    /// <c>invitee-a@example.test</c> (org B has only the shared one). The platform-scope check's live
    /// org-A-only address set (02.9.1-T3.2 AC5, CR2).
    /// </summary>
    public required IReadOnlyList<string> InviteEmails { get; init; }

    /// <summary>The extra role granted through <c>POST /v1/orgs/{o}/memberships/{m}/roles</c>; the DELETE target.</summary>
    public required IsolationRoleGrant ExtraGrant { get; init; }

    /// <summary>The two projects, slugs <c>web</c> then <c>api</c>.</summary>
    public required IReadOnlyList<IsolationProject> Projects { get; init; }

    /// <summary>The role ids this org can name: the built-in catalogue (custom roles are off, see <see cref="IsolationWorld.CustomRoleId"/>).</summary>
    public required IReadOnlyDictionary<string, Guid> RoleIds { get; init; }

    /// <summary>The environment key every project has a revision of.</summary>
    public string EnvironmentKey { get; } = IsolationWorld.EnvironmentKey;

    /// <summary>Admin first, then <see cref="Members"/>.</summary>
    public IReadOnlyList<IsolationUser> Users => [Admin, .. Members];

    /// <summary>Every membership of the org: the users' and the pending invite.</summary>
    public IReadOnlyList<Guid> MembershipIds => [.. Users.Select(u => u.MembershipId), PendingInviteMembershipId];

    /// <summary>Every <c>ci_binding</c> id.</summary>
    public IReadOnlyList<Guid> BindingIds => [.. Projects.SelectMany(p => p.BindingIds)];

    /// <summary>Every active ingest-token id.</summary>
    public IReadOnlyList<Guid> ActiveTokenIds => [.. Projects.Select(p => p.ActiveTokenId)];

    /// <summary>Every revoked ingest-token id.</summary>
    public IReadOnlyList<Guid> RevokedTokenIds => [.. Projects.Select(p => p.RevokedTokenId)];

    /// <summary>Every test-case id.</summary>
    public IReadOnlyList<Guid> CaseIds => [.. Projects.SelectMany(p => p.CaseIds)];

    /// <summary>Every case-revision id.</summary>
    public IReadOnlyList<Guid> CaseRevisionIds => [.. Projects.SelectMany(p => p.CaseRevisions).SelectMany(r => r)];

    /// <summary>Every suite id.</summary>
    public IReadOnlyList<Guid> SuiteIds => [.. Projects.SelectMany(p => p.SuiteIds)];

    /// <summary>Every suite-revision id.</summary>
    public IReadOnlyList<Guid> SuiteRevisionIds => [.. Projects.SelectMany(p => p.SuiteRevisionIds)];

    /// <summary>Every environment-revision id.</summary>
    public IReadOnlyList<Guid> EnvironmentRevisionIds => [.. Projects.Select(p => p.EnvironmentRevisionId)];

    /// <summary>Every manifest id.</summary>
    public IReadOnlyList<Guid> ManifestIds => [.. Projects.Select(p => p.ManifestId)];

    /// <summary>The member with <paramref name="label"/> (e.g. <c>viewer</c>, <c>tenant_admin</c>).</summary>
    public IsolationUser User(string label) => Users.Single(u => u.Label == label);
}

/// <summary>
/// The 02 §7 fixture set of the API isolation suite (02.9.1-T1.1): two orgs, <c>acme</c> and
/// <c>acme-2</c>, both displayed as <c>Acme</c>, with deliberately colliding project slugs
/// (<c>web</c>, <c>api</c>), suite slugs, case annotation ids/paths, member addresses and invite
/// address, all seeded through the public API as each org's <c>tenant_admin</c> wherever a
/// route exists.
/// </summary>
/// <remarks>
/// <para>Seeded per org, in order:</para>
/// <list type="bullet">
/// <item>org and projects — <c>POST /v1/orgs</c>, <c>POST /v1/orgs/{o}/projects</c> (the founder becomes <c>tenant_admin</c>);</item>
/// <item>active members — owner SQL (<c>membership</c> + <c>membership_role</c>, as
/// <c>tests/Identity.IntegrationTests/IdentityTestData.cs</c> does): an invite only becomes
/// active through 02.3.x's login, which is not merged, so no route can produce an active member;</item>
/// <item>a pending invite — <c>POST /v1/orgs/{o}/memberships</c>; org A additionally gets one invite address
/// org B never has (<c>invitee-a@example.test</c>, the platform-scope check's live org-A-only address, CR2);
/// an extra role grant — <c>POST …/memberships/{m}/roles</c>;</item>
/// <item>test cases — <c>ICaseIdentityService.RegisterDiscoveredAsync</c> in an <c>ITenantScope</c> (no POST route for
/// cases), synced twice with changed content so every case has two revisions;</item>
/// <item>suites + revisions, the <c>staging</c> environment revision (the environment row is created by the
/// revision route), a manifest, two <c>ci_binding</c>s (the first activated by the OIDC exchange below), two
/// ingest tokens (one then revoked by <c>DELETE /v1/ingest-tokens/{t}</c>), a <c>ci_producer</c> JWT from
/// <c>POST /v1/ci/token</c> over the ingest token, and a second one over a workflow token signed by the test
/// JWKS (<see cref="IsolationApiFactory.Jwks"/>), which proves control of the repository and activates the
/// binding (02.4.1-T3, 02.9.1-T3 from #19).</item>
/// </list>
/// <para>
/// Role combinations: the member catalogue of <c>contracts/permissions.json</c> holds exactly five
/// built-in member roles (<c>viewer</c>, <c>qa_engineer</c>, <c>test_maintainer</c>,
/// <c>release_approver</c>, <c>tenant_admin</c>); each org has its founder (<c>tenant_admin</c> +
/// <c>release_approver</c>, granted by <c>POST /v1/orgs</c>) plus five members
/// (<see cref="MemberCombinations"/>), six users per org. The service roles (<c>ci_producer</c>,
/// <c>agent</c>) are token-only and the staff roles (<c>platform_operator</c>,
/// <c>billing_operator</c>) are never org members.
/// </para>
/// <para>Not buildable yet (see <see cref="NotYetBuildable"/>):</para>
/// <list type="bullet">
/// <item>an expired support grant — 02.8.1 (support access) is not merged;</item>
/// <item>one real session per user — 02.3.2 (sessions) is not merged; members are identified by
/// <see cref="IsolationApiFactory"/>'s header stand-in instead;</item>
/// <item>a custom role — <c>POST /v1/roles</c> is off by default (<c>EVIDGATE:Identity:CustomRolesEnabled</c>);
/// role-id routes use the built-in role ids.</item>
/// </list>
/// </remarks>
public sealed class IsolationWorld
{
    /// <summary>The environment key each project has one revision of.</summary>
    public const string EnvironmentKey = "staging";

    /// <summary>The colliding project slugs, in seeding order.</summary>
    public static IReadOnlyList<string> ProjectSlugs { get; } = ["web", "api"];

    /// <summary>The non-admin member role combinations seeded in each org (label → role codes).</summary>
    public static IReadOnlyList<(string Label, string[] Roles)> MemberCombinations { get; } =
    [
        ("viewer", ["viewer"]),
        ("qa_engineer", ["qa_engineer"]),
        ("test_maintainer", ["test_maintainer"]),
        ("release_approver", ["release_approver"]),
        ("qa_engineer+test_maintainer", ["qa_engineer", "test_maintainer"]),
    ];

    /// <summary>The 02 §7 fixture items this world cannot build yet, with the reason.</summary>
    public static IReadOnlyList<string> NotYetBuildable { get; } =
    [
        "expired support grant: 02.8.1 (support access) not merged",
        "one session per user: 02.3.2 (sessions) not merged; members use the header stand-in",
        "custom role: POST /v1/roles disabled by default (EVIDGATE:Identity:CustomRolesEnabled=false)",
    ];

    private const int CasesPerProject = 3;

    /// <summary>Two revisions per case, so <c>/v1/cases/{c}/revisions</c> pages at limit 1.</summary>
    private const int RevisionsPerCase = 2;

    /// <summary>Two waivers per project, so <c>/v1/projects/{p}/waivers</c> pages at limit 1 and the approve and revoke controls never share a row.</summary>
    private const int WaiversPerProject = 2;

    /// <summary>The commit of the first tree; the second sync uses <see cref="SecondCommitSha"/>.</summary>
    private const string CommitSha = "bbbbbbb2222222";

    private const string SecondCommitSha = "ccccccc3333333";

    private IsolationWorld(IsolationOrg acme, IsolationOrg acme2, IReadOnlyDictionary<string, Guid> builtInRoleIds)
    {
        Acme = acme;
        Acme2 = acme2;
        BuiltInRoleIds = builtInRoleIds;
    }

    /// <summary>Org <c>acme</c> (org A).</summary>
    public IsolationOrg Acme { get; }

    /// <summary>Org <c>acme-2</c> (org B).</summary>
    public IsolationOrg Acme2 { get; }

    /// <summary>Both orgs, <see cref="Acme"/> first.</summary>
    public IReadOnlyList<IsolationOrg> Orgs => [Acme, Acme2];

    /// <summary>The built-in catalogue roles by code (shared by every org).</summary>
    public IReadOnlyDictionary<string, Guid> BuiltInRoleIds { get; }

    /// <summary>Always null: custom roles are disabled on the host, so no org-owned role exists.</summary>
    public static Guid? CustomRoleId => null;

    /// <summary>The other org of <paramref name="org"/>.</summary>
    public IsolationOrg Other(IsolationOrg org) => org == Acme ? Acme2 : Acme;

    /// <summary>Seeds both orgs through <paramref name="client"/> (the real host) and the owner connection.</summary>
    /// <param name="client">A client of the host under test.</param>
    /// <param name="services">The host's root service provider (case registration).</param>
    /// <param name="ownerConnectionString">The <c>evidgate_owner</c> connection (member rows and id lookups only).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<IsolationWorld> SeedAsync(
        HttpClient client,
        IServiceProvider services,
        string ownerConnectionString,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(ownerConnectionString);

        var seeder = new Seeder(client, services, ownerConnectionString, services.GetRequiredService<TestJwks>(), cancellationToken);
        var roles = await seeder.BuiltInRolesAsync().ConfigureAwait(false);
        var acme = await seeder.SeedOrgAsync("acme", 1, roles).ConfigureAwait(false);
        var acme2 = await seeder.SeedOrgAsync("acme-2", 2, roles).ConfigureAwait(false);

        return new IsolationWorld(acme, acme2, roles);
    }

    /// <summary>The seeding steps; every refused call fails the fixture with the route and the body.</summary>
    private sealed class Seeder(HttpClient client, IServiceProvider services, string ownerConnectionString, TestJwks jwks, CancellationToken cancellationToken)
    {
        private const string DisplayName = "Acme";

        private static readonly string[] AllowedRefPatterns = ["refs/heads/main", "refs/tags/v*"];

        public async Task<IReadOnlyDictionary<string, Guid>> BuiltInRolesAsync()
        {
            await using var connection = await OpenOwnerAsync().ConfigureAwait(false);
            await using var command = new NpgsqlCommand("select code, id from public.role where org_id is null", connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var roles = new Dictionary<string, Guid>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                roles[reader.GetString(0)] = reader.GetGuid(1);
            }

            return roles;
        }

        public async Task<IsolationOrg> SeedOrgAsync(string slug, int orgIndex, IReadOnlyDictionary<string, Guid> roles)
        {
            // Org + founder (tenant_admin) through the tenantless create route.
            var adminUser = Guid.CreateVersion7();
            var adminEmail = "admin@example.test";
            var org = (await SendAsync(
                HttpMethod.Post, "/v1/orgs", adminUser, adminEmail, org: null, new { name = DisplayName, slug }, HttpStatusCode.Created).ConfigureAwait(false))
                .GetProperty("id").GetGuid();
            var (founderMembership, founderRoles) = await FounderMembershipAsync(org, adminUser).ConfigureAwait(false);
            var admin = new IsolationUser("tenant_admin", adminUser, adminEmail, founderMembership, founderRoles);

            var projects = new List<Guid>();
            foreach (var projectSlug in ProjectSlugs)
            {
                projects.Add((await SendAsync(
                    HttpMethod.Post, $"/v1/orgs/{org}/projects", admin, org, new { name = projectSlug, slug = projectSlug }, HttpStatusCode.Created).ConfigureAwait(false))
                    .GetProperty("id").GetGuid());
            }

            var members = new List<IsolationUser>();
            foreach (var (label, codes) in MemberCombinations)
            {
                members.Add(await SeedActiveMemberAsync(org, label, codes, roles).ConfigureAwait(false));
            }

            var inviteEmails = new List<string> { "invitee@example.test" };
            var invite = (await SendAsync(
                HttpMethod.Post,
                $"/v1/orgs/{org}/memberships",
                admin,
                org,
                new { email = inviteEmails[0], roles = new[] { roles["viewer"] } },
                HttpStatusCode.Created).ConfigureAwait(false)).GetProperty("id").GetGuid();

            // Org A only (orgIndex 1 = acme): one address no other org owns, so the platform-scope check
            // (02.9.1-T3.2 AC5) scans a live org-A-only address instead of an always-empty set (CR2). Seeded
            // through the same invite route as the shared invite above; org B never gets it.
            if (orgIndex == 1)
            {
                const string orgAOnlyInviteEmail = "invitee-a@example.test";
                await SendAsync(
                    HttpMethod.Post,
                    $"/v1/orgs/{org}/memberships",
                    admin,
                    org,
                    new { email = orgAOnlyInviteEmail, roles = new[] { roles["viewer"] } },
                    HttpStatusCode.Created).ConfigureAwait(false);
                inviteEmails.Add(orgAOnlyInviteEmail);
            }

            var approver = members.Single(m => m.Label == "release_approver");
            await SendAsync(
                HttpMethod.Post,
                $"/v1/orgs/{org}/memberships/{approver.MembershipId}/roles",
                admin,
                org,
                new { role_id = roles["viewer"] },
                HttpStatusCode.OK).ConfigureAwait(false);
            approver = approver with { RoleCodes = [.. approver.RoleCodes, "viewer"] };
            members[members.FindIndex(m => m.Label == "release_approver")] = approver;

            var seeded = new List<IsolationProject>();
            for (var i = 0; i < projects.Count; i++)
            {
                seeded.Add(await SeedProjectAsync(slug, org, projects[i], ProjectSlugs[i], admin, 7000 + (orgIndex * 10) + i).ConfigureAwait(false));
            }

            return new IsolationOrg
            {
                Id = org,
                Slug = slug,
                Admin = admin,
                Members = members,
                PendingInviteMembershipId = invite,
                InviteEmails = inviteEmails,
                ExtraGrant = new IsolationRoleGrant(approver.MembershipId, roles["viewer"]),
                Projects = seeded,
                RoleIds = roles,
            };
        }

        private async Task<IsolationProject> SeedProjectAsync(string orgSlug, Guid org, Guid project, string projectSlug, IsolationUser admin, long repositoryId)
        {
            var (cases, caseRevisions) = await SeedCasesAsync(org, project).ConfigureAwait(false);
            var primaryRevisions = caseRevisions.Select(revisions => revisions[0]).ToArray();

            var suites = new List<Guid>();
            var suiteRevisions = new List<Guid>();
            foreach (var (suiteSlug, members) in new[] { ("smoke", primaryRevisions), ("regression", primaryRevisions.Take(2).ToArray()) })
            {
                var suite = (await SendAsync(
                    HttpMethod.Post, $"/v1/projects/{project}/suites", admin, org, new { name = suiteSlug, slug = suiteSlug }, HttpStatusCode.Created).ConfigureAwait(false))
                    .GetProperty("id").GetGuid();
                var revision = (await SendAsync(
                    HttpMethod.Post,
                    $"/v1/suites/{suite}/revisions",
                    admin,
                    org,
                    new { members = members.Select(id => new { case_revision_id = id }).ToArray() },
                    HttpStatusCode.Created).ConfigureAwait(false)).GetProperty("suite_revision_id").GetGuid();
                suites.Add(suite);
                suiteRevisions.Add(revision);
            }

            var environmentRevision = (await SendAsync(
                HttpMethod.Post,
                $"/v1/projects/{project}/environments/{EnvironmentKey}/revisions",
                admin,
                org,
                new
                {
                    base_urls = new Dictionary<string, string>(StringComparer.Ordinal) { ["web"] = $"https://{projectSlug}.{orgSlug}.example.test" },
                    dependency_versions = new Dictionary<string, string>(StringComparer.Ordinal) { ["postgres"] = "18.0" },
                },
                HttpStatusCode.Created).ConfigureAwait(false)).GetProperty("revision_id").GetGuid();

            var policyRevision = (await SendAsync(
                HttpMethod.Post,
                $"/v1/projects/{project}/policies",
                admin,
                org,
                new { flaky_budget = 0 },
                HttpStatusCode.Created).ConfigureAwait(false)).GetProperty("id").GetGuid();

            var manifest = (await SendAsync(
                HttpMethod.Post,
                $"/v1/projects/{project}/manifests",
                admin,
                org,
                Manifest(suiteRevisions[0], primaryRevisions, environmentRevision),
                HttpStatusCode.Created).ConfigureAwait(false)).GetProperty("manifest_id").GetGuid();

            var waivers = new List<Guid>();
            for (var i = 0; i < WaiversPerProject; i++)
            {
                waivers.Add((await SendAsync(
                    HttpMethod.Post,
                    "/v1/waivers",
                    admin,
                    org,
                    new
                    {
                        project_id = project,
                        scope = "case",
                        target_id = cases[i % cases.Length],
                        reason = $"isolation waiver {i}",
                        expires_at = DateTimeOffset.UtcNow.AddDays(30),
                    },
                    HttpStatusCode.Created).ConfigureAwait(false)).GetProperty("id").GetGuid());
            }

            var repository = $"{orgSlug}/{projectSlug}";
            var (binding, challenge) = await CreateBindingAsync(project, admin, org, repositoryId, repository).ConfigureAwait(false);
            var (mirrorBinding, _) = await CreateBindingAsync(project, admin, org, repositoryId + 100, $"{repository}-mirror").ConfigureAwait(false);

            var active = await SendAsync(
                HttpMethod.Post, $"/v1/projects/{project}/ingest-tokens", admin, org, new { name = "active" }, HttpStatusCode.Created).ConfigureAwait(false);
            var revokedToken = await SendAsync(
                HttpMethod.Post, $"/v1/projects/{project}/ingest-tokens", admin, org, new { name = "revoked" }, HttpStatusCode.Created).ConfigureAwait(false);
            var revoked = revokedToken.GetProperty("token").GetProperty("id").GetGuid();
            await SendAsync(HttpMethod.Delete, $"/v1/ingest-tokens/{revoked}", admin, org, body: null, HttpStatusCode.NoContent).ConfigureAwait(false);

            var secret = active.GetProperty("secret").GetString()
                ?? throw new InvalidOperationException($"seed: ingest token of project {project} returned no secret");
            var exchanged = await SendAsync(
                HttpMethod.Post, "/v1/ci/token", user: null, email: null, org: null, new { ingest_token = secret }, HttpStatusCode.OK).ConfigureAwait(false);
            if (exchanged.GetProperty("project_id").GetGuid() != project)
            {
                throw new InvalidOperationException($"seed: the ci_producer token of project {project} names another project");
            }

            // The binding becomes active through the real OIDC exchange (02.4.1-T3): a workflow token signed by
            // the test JWKS proves control of the repository, and the minted ci_producer token is kept beside the
            // ingest-token one (02.9.1-T3 hand-over from #19).
            var oidc = jwks.Mint(repositoryId, (claims, _) =>
            {
                claims["sub"] = $"repo:{repository}:ref:refs/heads/main";
                claims["repository"] = repository;
                claims["job_workflow_ref"] = $"{repository}/.github/workflows/ci.yml@refs/heads/main";
            });
            var oidcExchanged = await SendBearerAsync(
                "/v1/ci/token", oidc, new { binding_challenge = challenge }, HttpStatusCode.OK).ConfigureAwait(false);
            if (oidcExchanged.GetProperty("project_id").GetGuid() != project)
            {
                throw new InvalidOperationException($"seed: the OIDC ci_producer token of project {project} names another project");
            }

            return new IsolationProject
            {
                Id = project,
                Slug = projectSlug,
                BindingId = binding,
                BindingIds = [binding, mirrorBinding],
                ActiveTokenId = active.GetProperty("token").GetProperty("id").GetGuid(),
                ActiveTokenSecret = secret,
                RevokedTokenId = revoked,
                RevokedTokenSecret = revokedToken.GetProperty("secret").GetString()
                    ?? throw new InvalidOperationException($"seed: revoked ingest token of project {project} returned no secret"),
                CiProducerJwt = exchanged.GetProperty("token").GetString()
                    ?? throw new InvalidOperationException($"seed: the ci/token exchange of project {project} returned no token"),
                OidcProducerJwt = oidcExchanged.GetProperty("token").GetString()
                    ?? throw new InvalidOperationException($"seed: the OIDC ci/token exchange of project {project} returned no token"),
                CaseIds = cases,
                CaseRevisionIds = primaryRevisions,
                CaseRevisions = [.. caseRevisions.Select(perCase => (IReadOnlyList<Guid>)perCase)],
                SuiteIds = suites,
                SuiteRevisionIds = suiteRevisions,
                EnvironmentRevisionId = environmentRevision,
                PolicyRevisionId = policyRevision,
                ManifestId = manifest,
                WaiverIds = waivers,
            };
        }

        /// <summary>
        /// One <c>ci_binding</c> through <c>POST /v1/projects/{p}/ci-bindings</c>; the project's list needs two.
        /// Returns the binding id and the one-time challenge the OIDC exchange must present to activate it.
        /// </summary>
        private async Task<(Guid Id, string Challenge)> CreateBindingAsync(Guid project, IsolationUser admin, Guid org, long repositoryId, string repositoryFullName)
        {
            var created = await SendAsync(
                HttpMethod.Post,
                $"/v1/projects/{project}/ci-bindings",
                admin,
                org,
                new
                {
                    provider = "github_actions",
                    repository_id = repositoryId,
                    repository_full_name = repositoryFullName,
                    allowed_ref_patterns = AllowedRefPatterns,
                    allowed_workflow_refs = new[] { $"{repositoryFullName}/.github/workflows/ci.yml@refs/heads/main" },
                },
                HttpStatusCode.Created).ConfigureAwait(false);

            return (
                created.GetProperty("binding").GetProperty("id").GetGuid(),
                created.GetProperty("binding_challenge").GetString()
                    ?? throw new InvalidOperationException($"seed: the ci_binding of project {project} returned no challenge"));
        }

        /// <summary>
        /// Registers <see cref="CasesPerProject"/> cases through the public case-identity service, as
        /// <c>ManifestTests.SeedCasesAsync</c> does, then syncs the same tree once more with changed content so every
        /// case has <see cref="RevisionsPerCase"/> revisions (the list route pages at limit 1). Paths and annotation
        /// ids are the same in every project of both orgs on purpose (they are unique per org and project only).
        /// </summary>
        private async Task<(Guid[] Cases, Guid[][] Revisions)> SeedCasesAsync(Guid org, Guid project)
        {
            await RegisterTreeAsync(org, project, CommitSha, salt: 0).ConfigureAwait(false);
            await RegisterTreeAsync(org, project, SecondCommitSha, salt: 1).ConfigureAwait(false);

            await using var connection = await OpenOwnerAsync().ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                """
                select c.id, r.id
                  from public.test_case c
                  join public.test_revision r on r.org_id = c.org_id and r.case_id = c.id
                 where c.org_id = @org and c.project_id = @project
                 order by c.file_path, r.created_at, r.id
                """,
                connection);
            command.Parameters.AddWithValue("org", org);
            command.Parameters.AddWithValue("project", project);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var cases = new List<Guid>();
            var revisions = new List<List<Guid>>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var caseId = reader.GetGuid(0);
                if (cases.Count == 0 || cases[^1] != caseId)
                {
                    cases.Add(caseId);
                    revisions.Add([]);
                }

                revisions[^1].Add(reader.GetGuid(1));
            }

            if (cases.Count != CasesPerProject || revisions.Any(perCase => perCase.Count != RevisionsPerCase))
            {
                throw new InvalidOperationException(
                    $"seed: project {project} has {cases.Count} case(s) with revisions [{string.Join(", ", revisions.Select(r => r.Count))}], expected {CasesPerProject} cases of {RevisionsPerCase}");
            }

            return ([.. cases], [.. revisions.Select(perCase => perCase.ToArray())]);
        }

        /// <summary>
        /// One case-identity sync of the project's tree; <paramref name="salt"/> changes the file content so the
        /// second sync appends a revision instead of hitting the <c>(org_id, case_id, revision_hash)</c> key.
        /// </summary>
        private async Task RegisterTreeAsync(Guid org, Guid project, string commitSha, int salt)
        {
            var files = Enumerable.Range(0, CasesPerProject).Select(i => new SourceFile(
                string.Create(CultureInfo.InvariantCulture, $"iso/{i}.spec.ts"),
                string.Create(CultureInfo.InvariantCulture, $"// evidgate.id: 018f2f0e-8a52-7c4b-9d3e-{i:D12}\ntest('{i}', () => {{ expect(a).toBe({i + salt}); }});"),
                "playwright",
                "typescript")).ToArray();

            await using (var scoped = services.CreateAsyncScope())
            {
                await using var scope = await scoped.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
                    .BeginAsync(org, null, false, cancellationToken).ConfigureAwait(false);
                var outcome = await scoped.ServiceProvider.GetRequiredService<ICaseIdentityService>()
                    .RegisterDiscoveredAsync(project, commitSha, files, cancellationToken).ConfigureAwait(false);
                if (!outcome.Succeeded)
                {
                    throw new InvalidOperationException($"seed: case registration in project {project} failed: {outcome.Failure}");
                }

                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// An active member with <paramref name="codes"/> through the owner seam (the shape of
        /// <c>IdentityTestData.SeedActiveMemberAsync</c>): no route activates a membership before 02.3.x.
        /// </summary>
        private async Task<IsolationUser> SeedActiveMemberAsync(Guid org, string label, string[] codes, IReadOnlyDictionary<string, Guid> roles)
        {
            var user = Guid.CreateVersion7();
            var membership = Guid.CreateVersion7();
            var email = $"{label.Replace('+', '-')}@example.test";

            await using var connection = await OpenOwnerAsync().ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (var insert = new NpgsqlCommand(
                """
                insert into public.membership (id, org_id, user_id, invited_email, status, created_at, updated_at, version)
                values (@id, @org_id, @user_id, @email, 'active', now(), now(), 1)
                """,
                connection,
                transaction))
            {
                insert.Parameters.AddWithValue("id", membership);
                insert.Parameters.AddWithValue("org_id", org);
                insert.Parameters.AddWithValue("user_id", user);
                insert.Parameters.AddWithValue("email", email);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var code in codes)
            {
                await using var grant = new NpgsqlCommand(
                    """
                    insert into public.membership_role (id, org_id, membership_id, role_id, granted_at, created_at, updated_at, version)
                    values (@id, @org_id, @membership_id, @role_id, now(), now(), now(), 1)
                    """,
                    connection,
                    transaction);
                grant.Parameters.AddWithValue("id", Guid.CreateVersion7());
                grant.Parameters.AddWithValue("org_id", org);
                grant.Parameters.AddWithValue("membership_id", membership);
                grant.Parameters.AddWithValue("role_id", roles[code]);
                await grant.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new IsolationUser(label, user, email, membership, codes);
        }

        /// <summary>The founder's membership and its role codes (the create route grants <c>tenant_admin</c> and <c>release_approver</c>).</summary>
        private async Task<(Guid Membership, IReadOnlyList<string> Roles)> FounderMembershipAsync(Guid org, Guid user)
        {
            await using var connection = await OpenOwnerAsync().ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                """
                select m.id, r.code
                  from public.membership m
                  join public.membership_role mr on mr.org_id = m.org_id and mr.membership_id = m.id
                  join public.role r on r.id = mr.role_id
                 where m.org_id = @org and m.user_id = @user and m.status = 'active'
                 order by r.code
                """,
                connection);
            command.Parameters.AddWithValue("org", org);
            command.Parameters.AddWithValue("user", user);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            Guid? membership = null;
            var roles = new List<string>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                membership = reader.GetGuid(0);
                roles.Add(reader.GetString(1));
            }

            if (membership is not { } id || !roles.Contains("tenant_admin"))
            {
                throw new InvalidOperationException($"seed: org {org} has no active tenant_admin founder membership for {user}");
            }

            return (id, roles);
        }

        /// <summary>A 05.1.1 manifest over the suite revision, one variant, the seeded environment (shape of <c>ManifestUnitTests.Build</c>).</summary>
        private static JsonObject Manifest(Guid suiteRevision, Guid[] caseRevisions, Guid environmentRevision)
        {
            var planned = new JsonArray();
            foreach (var caseRevision in caseRevisions)
            {
                planned.Add(new JsonObject
                {
                    ["case_revision_id"] = caseRevision.ToString(),
                    ["variant"] = new JsonObject { ["params"] = new JsonObject { ["browser"] = "chromium" } },
                    ["target_env"] = EnvironmentKey,
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
                    ["target_envs"] = new JsonArray(EnvironmentKey),
                },
                ["shard_plan"] = new JsonObject
                {
                    ["shards"] = new JsonArray(new JsonObject { ["shard_id"] = "s0", ["from_index"] = 0, ["to_index"] = caseRevisions.Length }),
                },
                ["env_needs"] = new JsonObject { ["target_envs"] = new JsonArray(new JsonObject { ["target_env"] = EnvironmentKey }) },
            };
        }

        private Task<JsonElement> SendAsync(HttpMethod method, string url, IsolationUser caller, Guid org, object? body, params HttpStatusCode[] expected) =>
            SendAsync(method, url, caller.UserId, caller.Email, org, body, expected);

        private Task<JsonElement> SendAsync(HttpMethod method, string url, Guid? user, string? email, Guid? org, object? body, params HttpStatusCode[] expected) =>
            SendCoreAsync(method, url, user, email, org, bearer: null, body, expected);

        /// <summary>One seeding request presenting a bearer credential (the OIDC exchange of 02.4.1-T3).</summary>
        private Task<JsonElement> SendBearerAsync(string url, string bearer, object? body, params HttpStatusCode[] expected) =>
            SendCoreAsync(HttpMethod.Post, url, user: null, email: null, org: null, bearer, body, expected);

        /// <summary>One seeding request: member headers when a user is given, an <c>Idempotency-Key</c> on every write.</summary>
        private async Task<JsonElement> SendCoreAsync(
            HttpMethod method,
            string url,
            Guid? user,
            string? email,
            Guid? org,
            string? bearer,
            object? body,
            params HttpStatusCode[] expected)
        {
            using var request = new HttpRequestMessage(method, url);
            if (user is { } u)
            {
                request.Headers.Add(IsolationApiFactory.UserHeader, u.ToString());
                if (email is not null)
                {
                    request.Headers.Add(IsolationApiFactory.EmailHeader, email);
                }
            }

            if (org is { } o)
            {
                request.Headers.Add(IsolationApiFactory.OrgHeader, o.ToString());
            }

            if (bearer is not null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
            }

            if (method != HttpMethod.Get && url != "/v1/ci/token")
            {
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            }

            request.Content = body switch
            {
                null => null,
                JsonNode node => new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json"),
                _ => JsonContent.Create(body),
            };

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!expected.Contains(response.StatusCode))
            {
                throw new InvalidOperationException(
                    $"seed: {method} {url} answered {(int)response.StatusCode}, expected {string.Join('/', expected.Select(s => (int)s))}: {text}");
            }

            using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);
            return document.RootElement.Clone();
        }

        private async Task<NpgsqlConnection> OpenOwnerAsync()
        {
            var connection = new NpgsqlConnection(ownerConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
    }
}
