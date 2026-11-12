using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Linking;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Linking;

/// <summary>The arrangement helpers every case of the suite shares.</summary>
public sealed partial class IdentityLinkingTests : TenantTest
{

    // ------------------------------------------------------------------ helpers

    /// <summary>A host with both providers and no case-specific substitution.</summary>
    private LinkTestHost MinimalHost() =>
        LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, Email));

    /// <summary>
    /// Signs <paramref name="provider"/> in once (creating the account and its identity row without a session),
    /// then seeds an org with an active membership — the harness's owner seam, which arranging rows is for — and
    /// signs in again for the session it mints.
    /// </summary>
    /// <param name="host">The running host.</param>
    /// <param name="client">A client of the host.</param>
    /// <param name="provider">The provider route value.</param>
    /// <param name="subject">The subject its fake verifies.</param>
    private static async Task<(Guid UserId, LinkTestHost.LinkSession Session, Guid OrgId)> MemberAsync(
        LinkTestHost host,
        HttpClient client,
        string provider,
        string subject)
    {
        using (var first = await host.SignInAsync(client, provider))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await LinkTestHost.ErrorCodeAsync(first)).ShouldBe(ApiErrorCode.NoMembership);
        }

        var userId = (await host.UserIdAsync(provider, subject))
            ?? throw new InvalidOperationException("the first sign-in created no identity for " + provider);

        var orgId = await host.Fixture.SeedOrgAsync(cancellationToken: LinkTestHost.Token);
        await host.SeedMembershipAsync(orgId, userId);

        using var second = await host.SignInAsync(client, provider);
        second.StatusCode.ShouldBe(HttpStatusCode.Found);

        var session = LinkTestHost.LinkSession.FromSignIn(second);

        return (userId, session, orgId);
    }

    /// <summary>Starts a link on the session and returns the state the callback needs.</summary>
    /// <param name="host">The running host.</param>
    /// <param name="client">A client of the host.</param>
    /// <param name="provider">The provider to add.</param>
    /// <param name="session">The user's session, as the sign-in left it.</param>
    private static async Task<(HttpResponseMessage Response, LoginStatePayload Payload, string StateCookie)> StartLinkAsync(
        LinkTestHost host,
        HttpClient client,
        string provider,
        LinkTestHost.LinkSession session)
    {
        var started = await host.StartLinkAsync(client, provider, session);

        return (started.Response, started.Payload, started.StateCookie);
    }

    /// <summary>Runs start and callback and returns the staged link cookie value.</summary>
    /// <param name="host">The running host.</param>
    /// <param name="client">A client of the host.</param>
    /// <param name="provider">The provider to add.</param>
    /// <param name="session">The user's session, as the sign-in left it.</param>
    private static async Task<string> StageAsync(
        LinkTestHost host,
        HttpClient client,
        string provider,
        LinkTestHost.LinkSession session)
    {
        var start = await StartLinkAsync(host, client, provider, session);
        using var startResponse = start.Response;

        using var callback = await LinkTestHost.LinkCallbackAsync(
            client,
            provider,
            start.StateCookie,
            start.Payload.State,
            session);

        callback.StatusCode.ShouldBe(HttpStatusCode.Found);

        return LinkTestHost.CookieValue(callback, LinkConfirmCookie.Name)
            ?? throw new InvalidOperationException("the callback staged no link cookie");
    }

    /// <summary><c>GET /v1/auth/link/pending</c> with the given cookies.</summary>
    /// <param name="client">A client of the host.</param>
    /// <param name="session">The session, as the sign-in left it.</param>
    /// <param name="linkCookie">The staged link cookie value, or null for none.</param>
    private static Task<HttpResponseMessage> PendingAsync(HttpClient client, LinkTestHost.LinkSession session, string? linkCookie)
    {
        return LinkTestHost.SendAsync(client, HttpMethod.Get, "/link/pending", session, linkCookie);
    }

    /// <summary><c>POST /v1/auth/link/confirm</c> with the given cookies.</summary>
    /// <param name="client">A client of the host.</param>
    /// <param name="session">The session, as the sign-in left it.</param>
    /// <param name="linkCookie">The staged link cookie value, or null for a browser that no longer has one.</param>
    private static Task<HttpResponseMessage> ConfirmAsync(HttpClient client, LinkTestHost.LinkSession session, string? linkCookie)
    {
        return LinkTestHost.SendAsync(client, HttpMethod.Post, "/link/confirm", session, linkCookie);
    }

    /// <summary>
    /// A store that answers one outcome for any request, so the route's mapping of the outcomes the database
    /// would rarely produce is proven at its seam.
    /// </summary>
    private sealed class ScriptedLinkStore(LinkResult result) : ILinkIdentityStore
    {
        /// <inheritdoc />
        public ValueTask<Result<LinkOutcome>> LinkAsync(
            byte[]? sessionProof,
            string provider,
            string subject,
            string email,
            string? requestId,
            string? traceId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success(new LinkOutcome(result, null)));
    }
}
