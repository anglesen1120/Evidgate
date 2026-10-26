using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Http;

/// <summary>
/// 02.3.2-T1 AC5, the FAIL-CLOSED half: coverage of the antiforgery rule is the DEFAULT, and it is proven over the
/// route table the host actually serves rather than over a hand list. Every served <c>/v1</c> mutation is driven
/// with a valid session cookie and no <c>X-XSRF-TOKEN</c> header; each one must answer 403 <c>csrf_failed</c>
/// unless it is the single documented exemption, and the set of routes that are NOT refused must equal that
/// documented list exactly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the served table and not a list of routes.</b> The gate is host-wide middleware
/// (<c>SessionXsrfMiddleware</c>) precisely so a route added by a later task is covered without an annotation. A
/// test that enumerated its own list would keep passing after such a route appeared; this one fails, because the
/// new route enters the enumeration and must be refused.
/// </para>
/// <para>
/// <b>How a probe is built, and why it is checked for reachability.</b> Method, url and body come from the
/// canonical <c>RouteRequests.Build</c> table, so every served route has a request that is valid for it. A path
/// that matches no route is answered 404 through
/// the status-code pages — and, because the middleware decides on the method and the cookie rather than on the
/// endpoint, it would refuse an unmatched path too. A check that only looked at the status would therefore be
/// satisfied by the refusal of the wrong thing. The host records the route pattern routing actually matched, so
/// every probe is asserted to have reached the route it claims to cover.
/// </para>
/// </remarks>
/// <param name="fixture">The migrated harness database.</param>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class RouteTableXsrfTests(DatabaseFixture fixture) : HarnessTest(fixture)
{
    /// <summary>
    /// The ONE documented exemption (02.3.2 §10 T1.2 Notes; <c>SessionXsrfPolicy.ExemptRoutePatterns</c>):
    /// invite staging SETS a credential cookie and authenticates with none, so there is no session for a token to
    /// be bound to and no ambient authority a forged request could borrow.
    /// </summary>
    private static readonly string[] ExpectedExempt = ["POST /v1/auth/invite-token"];

    /// <summary>
    /// AC5: every served mutation outside the documented exemption is refused with 403 <c>csrf_failed</c> when it
    /// carries a valid session cookie and no header — and the routes that are not refused are exactly the
    /// documented exemption.
    /// </summary>
    [Fact]
    public async Task Every_Mutation_Outside_The_Exempt_Set_Is_403_CsrfFailed()
    {
        await using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var cookieHeader = SessionTestHost.CookieHeaderFor(member.SessionCookie);

        var mutating = ServedRouteTable.From(host.Services)
            .Where(route => ServedRouteTable.IsMutating(route.Method))
            .ToList();

        mutating.ShouldNotBeEmpty("the served route table must be readable, or this coverage claim is empty");

        // The canonical per-route request builder (ServedRouteTable.cs): every served route has one entry and its
        // request is valid for that route, so the refusal below is about the route and not about a malformed probe.
        // The tenant ids only have to satisfy the path: the pair is judged before any lookup runs.
        var requestContext = RouteRequestContext.Fresh(await host.SeedOrganizationAsync(), Guid.CreateVersion7());

        var refused = new List<string>();
        var exempt = new List<string>();

        foreach (var route in mutating)
        {
            var name = route.Method + " " + route.Template;
            var request = RouteRequests.Build(route, requestContext);
            var url = request.Url;
            var path = url.Split('?')[0];

            using var response = await SessionTestHost.SendAsync(
                client,
                request.Method,
                url,
                cookieHeader: cookieHeader,
                jsonBody: request.Body?.ToJsonString());

            // 1. The probe reached the route: a 404 path would be refused as well, and would prove nothing.
            var matched = host.Recorder.PatternFor(request.Method.Method, path);
            matched.ShouldNotBeNull(
                name + " was not matched at " + path + ": the probe must reach the route it claims to cover");
            ServedRouteTable.NormaliseTemplate(matched!).ShouldBe(
                route.Template,
                name + " was served by another route, so the refusal below would be about the wrong endpoint");

            // 2. The pair is what decides it, and nothing else: 403 csrf_failed, not the session's own 401/403.
            var refusedByXsrf = response.StatusCode == HttpStatusCode.Forbidden
                && string.Equals(
                    await SessionTestHost.ErrorCodeAsync(response),
                    ApiErrorCode.CsrfFailed,
                    StringComparison.Ordinal);

            // 3. Each route is refused exactly when it is outside the documented exemption, with a message that
            //    names the drift in the direction it happened.
            refusedByXsrf.ShouldBe(
                !ExpectedExempt.Contains(name),
                refusedByXsrf
                    ? name + " is a cookie-authenticated mutation that the antiforgery gate let through: either "
                        + "cover it or record it in the documented exemption"
                    : name + " is refused as csrf_failed although the test documents it as exempt");

            (refusedByXsrf ? refused : exempt).Add(name);
        }

        // 4. Both directions of the set: an exemption that appears without the documentation fails, and so does
        //    documentation that no longer names a real exemption.
        exempt.Order(StringComparer.Ordinal).ShouldBe(
            ExpectedExempt.Order(StringComparer.Ordinal),
            "the set of mutations that are NOT refused must be exactly the documented exemption");

        refused.Count.ShouldBe(mutating.Count - ExpectedExempt.Length);
    }

    /// <summary>
    /// AC5, the other half of the drift: the documented exemption must still be a route the host SERVES and one
    /// that mutates. An exemption for a renamed or deleted route covers nothing, and would otherwise stay green.
    /// </summary>
    [Fact]
    public async Task The_Documented_Exemption_Is_Still_A_Served_Mutation()
    {
        await using var host = SessionTestHost.Start(Fixture);

        var served = ServedRouteTable.From(host.Services)
            .Where(route => ServedRouteTable.IsMutating(route.Method))
            .Select(route => route.Method + " " + route.Template)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var exemption in ExpectedExempt)
        {
            served.ShouldContain(
                exemption,
                "the documented exemption must name a mutation the host serves, or it protects nothing");
        }
    }
}
