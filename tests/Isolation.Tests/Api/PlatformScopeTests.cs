using System.Text;
using Shouldly;
using Xunit;

namespace Isolation.Tests.Api;

/// <summary>
/// 02.9.1-T3.2 (AC5): the platform-scope proof. The <c>user</c>, <c>user_identity</c> and <c>session</c> tables
/// (02.3.x) are platform scope, never tenant scope, so no <c>/v1</c> route may address them and no org-A user or
/// membership id may resolve for an org-B caller. Three checks, each naming the offending route:
/// <list type="number">
/// <item>no served or specified <c>/v1</c> route carries a platform-scope path segment (allowlist below);</item>
/// <item>org-A user and membership ids in every served GET route's id slots answer exactly as a random id does;</item>
/// <item>no org-B list body carries an org-A user id, or an address only org A owns.</item>
/// </list>
/// </summary>
/// <remarks>
/// The world seeds no <c>session</c> row (02.3.2 is not merged, see <see cref="IsolationWorld.NotYetBuildable"/>),
/// so the session half of AC5 is carried by check 1: the only session-segment routes are the four caller-owned
/// routes of 02.3.2-T2.1 on the allowlist above with their reasons, and any other route naming a session fails
/// here. The member addresses deliberately collide between the two orgs (the same local text, 02 §7), so check 3
/// scans for org-A addresses org B does not also own: org A's own pending invite
/// (<c>invitee-a@example.test</c>, seeded by <see cref="IsolationWorld"/>) is the live org-A-only address, and
/// the set is asserted non-empty so the branch cannot pass vacuously; its user-id half is strict.
/// </remarks>
[Collection(ApiIsolationSuite.Name)]
[Trait("Category", "Docker")]
public sealed class PlatformScopeTests(ApiIsolationFixture fixture)
{
    /// <summary>
    /// The path segments that name a platform-scope resource: the <c>user</c>, <c>user_identity</c> and
    /// <c>session</c> tables of 02.3.x, in the spellings a route could use.
    /// </summary>
    private static readonly string[] PlatformSegments =
        ["user", "users", "user-identity", "user-identities", "identity", "identities", "session", "sessions", "me"];

    /// <summary>
    /// Served routes that legitimately carry a platform-scope segment, each a recorded decision. The four session
    /// routes of 02.3.2-T2.1 are the caller's OWN rows only: the acting user is resolved inside the definer from
    /// the presented cookie's HMAC, no route value or body ever names a user or a session of another account, and
    /// the admin path of the revoke route needs an active <c>membership:write</c> in the org named by
    /// <c>X-Evidgate-Org</c> (02 §4.2, 02.3.2 §5) — so they are self-service, not tenant scope.
    /// </summary>
    private static readonly Dictionary<ApiRoute, string> PlatformScopeRoutes = new()
    {
        [new ApiRoute("GET", "/v1/auth/sessions")] =
            "the caller's own live sessions, resolved from the presented cookie inside the definer",
        [new ApiRoute("DELETE", "/v1/auth/sessions/{s}")] =
            "the caller's own row, or a member's under an active membership:write in the named org; every other id answers the same 404",
        [new ApiRoute("POST", "/v1/auth/sessions/revoke-all")] =
            "every live session of the caller only, the acting user resolved from the cookie",
        [new ApiRoute("POST", "/v1/auth/logout")] =
            "the one row the presented cookie proves",
    };

    /// <summary>
    /// Specified but mapped only by a Development host with the dev-login flag, so this Production-shaped host
    /// does not serve it. It is skipped while unserved; a host that does serve it fails the check below.
    /// </summary>
    private static readonly HashSet<ApiRoute> DevOnlyRoutes = [new ApiRoute("POST", "/v1/dev/session")];

    /// <summary>AC5: no tenant API reaches a user, user_identity or session row.</summary>
    [Fact]
    public async Task SessionUserIdentity_NotReachableViaV1()
    {
        var world = fixture.World;
        var served = ServedRouteInventory.From(fixture.Services);
        var spec = OpenApiRouteInventory.Load();
        var runner = new RouteProbeRunner(fixture);
        var violations = new List<string>();

        // (a) No route addresses a platform-scope resource. The matcher is proven live first, so a broken pattern
        // cannot pass this check by matching nothing.
        served.Count.ShouldBeGreaterThan(0, "the served route inventory is empty");
        spec.Count.ShouldBeGreaterThan(0, "the OpenAPI route inventory is empty");
        PlatformSegmentsOf(new ApiRoute("GET", "/v1/users/{u}")).ShouldHaveSingleItem().ShouldBe("users", "the platform-scope segment matcher is a no-op");

        foreach (var route in served.Concat(spec).Distinct())
        {
            if (DevOnlyRoutes.Contains(route) && !served.Contains(route))
            {
                continue;
            }

            var segments = PlatformSegmentsOf(route);
            if (segments.Count > 0 && !PlatformScopeRoutes.ContainsKey(route))
            {
                violations.Add($"{route} addresses the platform-scope resource '{segments[0]}' but is not on the allowlist");
            }
        }

        foreach (var allowed in PlatformScopeRoutes.Keys.Where(route => !served.Contains(route)))
        {
            violations.Add($"{allowed} is on the platform-scope allowlist but the host does not serve it");
        }

        // (b) Org-A user and membership ids in every served GET route's id slots answer exactly as a random id.
        var foreignIds = PlatformIds(world.Acme);
        foreignIds.Count.ShouldBeGreaterThan(0, "the world seeds no org-A user or membership id");
        var getRoutes = served.Where(route => route.Method == "GET" && route.Template.Contains('{', StringComparison.Ordinal)).ToList();
        getRoutes.Count.ShouldBeGreaterThan(0, "no served GET route takes an id");
        foreach (var route in getRoutes)
        {
            foreach (var id in foreignIds)
            {
                violations.AddRange(await ForeignIdOracleAsync(runner, route, world, route.Template, id));
            }
        }

        // (b2) The one GET route taking a membership id: org B's own {o} with org A's {m} (the generic fill above
        // puts the foreign id in {o} too, where tenant resolution refuses before {m} is read).
        var membershipRoute = new ApiRoute("GET", "/v1/orgs/{o}/memberships/{m}");
        foreach (var membership in world.Acme.MembershipIds)
        {
            violations.AddRange(await ForeignIdOracleAsync(
                runner, membershipRoute, world, $"/v1/orgs/{world.Acme2.Id}/memberships/{{m}}", membership));
        }

        // (c) No org-B list body carries an org-A user id, or an address only org A owns.
        var walks = IsolationRouteCases.Lists(world);
        walks.Count.ShouldBeGreaterThan(0, "no list route to walk");
        var foreignUserIds = world.Acme.Users.Select(user => user.UserId.ToString("D")).ToList();
        var foreignOnlyEmails = world.Acme.Users.Select(user => user.Email)
            .Concat(world.Acme.InviteEmails)
            .Except(world.Acme2.Users.Select(user => user.Email).Concat(world.Acme2.InviteEmails), StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreignOnlyEmails.ShouldNotBeEmpty(
            "the world seeds no address org A owns and org B does not, so the address half of check 3 would pass vacuously");
        foreach (var walk in walks)
        {
            var (_, failure, pages) = await runner.WalkAsync(walk.Caller, walk.Url, walk.IdField);
            if (failure is not null)
            {
                violations.Add($"{walk.Route} [{walk.Case}]: {failure}");
                continue;
            }

            foreach (var body in pages)
            {
                violations.AddRange(foreignUserIds
                    .Where(id => body.Contains(id, StringComparison.OrdinalIgnoreCase))
                    .Select(id => $"{walk.Route} [{walk.Case}] as {walk.Caller}: the body carries org-A user id {id}"));
                violations.AddRange(foreignOnlyEmails
                    .Where(email => body.Contains(email, StringComparison.OrdinalIgnoreCase))
                    .Select(email => $"{walk.Route} [{walk.Case}] as {walk.Caller}: the body carries org-A address {email}"));
            }
        }

        violations.ShouldBeEmpty(
            $"platform scope (AC5): {violations.Count} violation(s) over {served.Count} served routes, {getRoutes.Count} GET routes and {walks.Count} list walks:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// (b): <paramref name="template"/> with <paramref name="id"/> in its id slots and with a random id must answer
    /// identically (status and error code), and the foreign answer must carry no org-A id.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ForeignIdOracleAsync(
        RouteProbeRunner runner,
        ApiRoute route,
        IsolationWorld world,
        string template,
        Guid id)
    {
        const string name = "org-A user/membership id in the id slots";
        var caller = IsolationRouteCases.OrgBAdmin(world);
        var foreign = Fill(template, id);
        var control = Fill(template, Guid.CreateVersion7());
        var real = await runner.SendAsync(caller, new(HttpMethod.Get, foreign)).ConfigureAwait(false);
        var random = await runner.SendAsync(caller, new(HttpMethod.Get, control)).ConfigureAwait(false);

        var violations = new List<string>();
        if (real.Status != random.Status || !string.Equals(real.Code, random.Code, StringComparison.Ordinal))
        {
            violations.Add(RouteProbeRunner.Describe(route, name, caller, $"{foreign} answered {real}, {control} answered {random}"));
        }

        violations.AddRange(runner.ForeignIdsIn(real.Text)
            .Select(foreignId => RouteProbeRunner.Describe(route, name, caller, $"{foreign} answered {real} carrying org-A id {foreignId}")));
        return violations;
    }

    /// <summary>The template with every parameter replaced by <paramref name="id"/> (<c>{e}</c> keeps the environment key).</summary>
    private static string Fill(string template, Guid id)
    {
        var text = new StringBuilder(template.Length);
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{')
            {
                text.Append(template[i]);
                continue;
            }

            var close = template.IndexOf('}', i);
            var name = template[(i + 1)..close];
            text.Append(name is "e" ? IsolationWorld.EnvironmentKey : id.ToString("D"));
            i = close;
        }

        return text.ToString();
    }

    /// <summary>The platform-scope segments <paramref name="route"/>'s template carries (empty when none).</summary>
    private static IReadOnlyList<string> PlatformSegmentsOf(ApiRoute route) =>
        [.. route.Template.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !segment.StartsWith('{'))
            .Where(segment => PlatformSegments.Contains(segment, StringComparer.OrdinalIgnoreCase))];

    /// <summary>Every id org <paramref name="org"/> owns in the <c>user</c>/<c>membership</c> tables.</summary>
    private static IReadOnlyList<Guid> PlatformIds(IsolationOrg org)
    {
        ArgumentNullException.ThrowIfNull(org);
        return [.. org.Users.SelectMany(user => new[] { user.UserId, user.MembershipId }), org.PendingInviteMembershipId];
    }
}
