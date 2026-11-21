using System.Net;
using Evidgate.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Http;

/// <summary>
/// Pins what a sink that keeps scopes would persist for an administrative session revoke: the framework's
/// request scope carries the route, and the route names the revoked session row.
/// </summary>
[Trait("Category", "Docker")]
public sealed class AdminRevokeScopeLogTests : TenantTest
{
    private const string RequestPathKey = "RequestPath";

    public AdminRevokeScopeLogTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// Known gap: the revoked session id appears in the <c>RequestPath</c> scope pair of the framework's
    /// request-logging categories. The shipped providers do not keep scopes, so this is a capture-only exposure.
    /// The test flips when the path is redacted or the owner rules that a session id in a route is not sensitive.
    /// </summary>
    [Fact]
    public async Task An_Admin_Revoke_Names_The_Session_Id_In_The_Request_Scope_KnownGap()
    {
        using var host = SessionTestHost.Start(Fixture, logProvider: new CapturingLoggerProvider());
        var logs = host.Services.GetServices<ILoggerProvider>().OfType<CapturingLoggerProvider>().ShouldHaveSingleItem();
        using var client = host.CreateSessionClient();
        var victim = await host.SignInMemberAsync(client);
        var victimSessionId = await host.SessionIdOfAsync(victim.SessionCookie);
        var admin = await host.SignInMemberInOrgAsync(client, victim.OrgId, "tenant_admin");

        logs.Clear();
        using var revoked = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            "/v1/auth/sessions/" + victimSessionId,
            SessionTestHost.CookieHeaderFor(admin.SessionCookie, admin.Xsrf),
            xsrfToken: admin.Xsrf.RequestToken,
            orgHeader: victim.OrgId.ToString());

        revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var id = victimSessionId.ToString("D");
        var inScope = logs.Entries
            .Where(entry => entry.Scopes.Any(scope => scope.StartsWith(RequestPathKey + "=", StringComparison.Ordinal)
                && scope.Contains(id, StringComparison.Ordinal)))
            .ToList();

        inScope.Count.ShouldBeGreaterThan(0, "the request scope pair " + RequestPathKey + " names the revoked session");
        inScope.ShouldAllBe(
            entry => entry.Category.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal)
                && entry.Level == LogLevel.Information,
            "only the framework's Information request logging carries it");

        // Not in the message, state or exception text: the gap is the scope alone.
        var elsewhere = logs.Entries.Count(entry => entry.Message.Contains(id, StringComparison.Ordinal)
            || entry.State.Any(pair => pair.Contains(id, StringComparison.Ordinal))
            || (entry.Exception?.Contains(id, StringComparison.Ordinal) ?? false));
        elsewhere.ShouldBe(0, "outside the scope the id never reaches a captured line");
    }
}
