using System.Globalization;
using System.Text.Json.Nodes;

namespace Isolation.Tests.Api;

/// <summary>
/// <c>POST /v1/projects/{p}/git-repo-bindings</c>: the request names its tenant only through <c>{p}</c>, and the
/// body names the GitHub installation the caller claims. An org-B caller naming org A's project, or its own
/// project while the body names org A's installation, must be refused as a random id is and must write no binding.
/// </summary>
/// <remarks>
/// The created route needs no positive control: the installation probe sends the body a real registration sends and
/// requires the ownership refusal by code, so a shape error (400/422), an unconfigured GitHub App (503) or a hidden
/// project (404) fails the case instead of passing as a refusal, and the served-route gate already proves the
/// template matches a real route.
/// </remarks>
public static partial class IsolationRouteCases
{
    /// <summary>The repository every probe names; the refusals never depend on GitHub answering.</summary>
    private const long ProbeRepositoryId = 5002;

    /// <summary>
    /// An installation id no org owns, the random half of the installation pair, so the refusal cannot depend on
    /// which foreign id the caller guessed.
    /// </summary>
    private const long UnownedInstallationId = 1;

    /// <summary>
    /// The one code <c>OwnsActiveAsync</c> produces for an unknown, another org's, or a suspended installation
    /// (<c>GitRepoBindingErrors.InstallationNotOwned</c>): on this route it proves the project resolved and the
    /// ownership proof ran, which a bare 403 from a permission gate would not.
    /// </summary>
    private const string InstallationNotOwnedCode = "installation_not_owned";

    private static RouteCase GitRepoBindingCase()
    {
        var route = new ApiRoute("POST", "/v1/projects/{p}/git-repo-bindings");

        return new RouteCase(
            route,
            w =>
            [
                .. w.Acme.Projects.Select(p => Pair(route, $"org A project {p.Slug} in {{p}}", ProbeExpect.NotFound, OrgBAdmin(w),
                    Register(p.Id, w.Acme.GithubInstallationId, ProbeRepositoryId),
                    Register(Guid.CreateVersion7(), UnownedInstallationId, ProbeRepositoryId))),
                new(route, "X-Evidgate-Org: A with an org-A project id", runner => runner.CallerPairAsync(
                    route, "X-Evidgate-Org: A with an org-A project id", ForeignHeaderCaller(w), RandomHeaderCaller(w),
                    Register(w.Acme.Projects[0].Id, w.Acme.GithubInstallationId, ProbeRepositoryId))),
                // The project is the caller's own and only the body names another org's installation, so the
                // ownership proof is the refusal left: a 201 here would be org B binding a foreign installation.
                new(route, "own project naming org A's installation", runner => InstallationRefusedAsync(
                    runner, route, OrgBAdmin(w),
                    Register(w.Acme2.Projects[0].Id, w.Acme.GithubInstallationId, ProbeRepositoryId),
                    Register(w.Acme2.Projects[0].Id, UnownedInstallationId, ProbeRepositoryId),
                    w.Acme2.Id)),
            ],
            w =>
            [
                new(route, "sibling project api of the token's org", runner => runner.ScopeMismatchAsync(
                    route, "sibling project api of the token's org", OrgBService(w),
                    Register(w.Acme2.Projects[1].Id, w.Acme2.GithubInstallationId, ProbeRepositoryId),
                    requireScopeCode: false)),
                Pair(route, "org A project web in {p}", ProbeExpect.ServiceRefused, OrgBService(w),
                    Register(w.Acme.Projects[0].Id, w.Acme.GithubInstallationId, ProbeRepositoryId),
                    Register(Guid.CreateVersion7(), UnownedInstallationId, ProbeRepositoryId)),
                new(route, "sibling project api of the OIDC token's org", runner => runner.ScopeMismatchAsync(
                    route, "sibling project api of the OIDC token's org", OrgBServiceOidc(w),
                    Register(w.Acme2.Projects[1].Id, w.Acme2.GithubInstallationId, ProbeRepositoryId),
                    requireScopeCode: false)),
                Pair(route, "org A project web in {p} (OIDC token)", ProbeExpect.ServiceRefused, OrgBServiceOidc(w),
                    Register(w.Acme.Projects[0].Id, w.Acme.GithubInstallationId, ProbeRepositoryId),
                    Register(Guid.CreateVersion7(), UnownedInstallationId, ProbeRepositoryId)),
            ]);
    }

    /// <summary>The registration body: the two ids the route accepts, and no member it refuses.</summary>
    private static ProbeRequest Register(Guid project, long installation, long repository) =>
        new(
            HttpMethod.Post,
            string.Create(CultureInfo.InvariantCulture, $"/v1/projects/{project:D}/git-repo-bindings"),
            new JsonObject
            {
                ["installation_id"] = installation,
                ["repository_id"] = repository,
            });

    /// <summary>
    /// The caller's own project with a foreign installation in the body, against the same project with an
    /// unowned id: both must answer the ownership refusal, and neither may write a binding. The rows are counted
    /// through the owner connection because a wrong answer here writes into the caller's own org, which the
    /// suite's org-A snapshot cannot see.
    /// </summary>
    private static async Task<IReadOnlyList<string>> InstallationRefusedAsync(
        RouteProbeRunner runner,
        ApiRoute route,
        ProbeCaller caller,
        ProbeRequest foreign,
        ProbeRequest random,
        Guid org)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(foreign);
        ArgumentNullException.ThrowIfNull(random);

        const string name = "own project naming org A's installation";
        var sql = string.Create(
            CultureInfo.InvariantCulture,
            $"select id::text from public.git_repo_binding where org_id = @org and repository_id = '{ProbeRepositoryId}'");

        var before = (await runner.OwnerIdsAsync(sql, org).ConfigureAwait(false)).Count;
        var violations = new List<string>();
        violations.AddRange(Judge(await runner.SendAsync(caller, foreign).ConfigureAwait(false), foreign, "foreign"));
        violations.AddRange(Judge(await runner.SendAsync(caller, random).ConfigureAwait(false), random, "random"));
        var after = (await runner.OwnerIdsAsync(sql, org).ConfigureAwait(false)).Count;

        if (after != before)
        {
            violations.Add(RouteProbeRunner.Describe(route, name, caller,
                $"wrote {after - before} binding row(s) for repository {ProbeRepositoryId.ToString(CultureInfo.InvariantCulture)}"));
        }

        return violations;

        IEnumerable<string> Judge(ProbeResponse response, ProbeRequest request, string half)
        {
            if (response.Status != 403 || !string.Equals(response.Code, InstallationNotOwnedCode, StringComparison.Ordinal))
            {
                yield return RouteProbeRunner.Describe(route, name, caller,
                    $"{half} {request} answered {response}, expected 403 {InstallationNotOwnedCode}");
            }

            foreach (var id in runner.ForeignIdsIn(response.Text))
            {
                yield return RouteProbeRunner.Describe(route, name, caller, $"{half} {request} answered {response} carrying org-A id {id}");
            }
        }
    }
}
