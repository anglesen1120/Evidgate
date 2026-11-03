using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Dev;
using Evidgate.Testing;
using Identity.IntegrationTests.Sessions.Http;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.DevLogin;

/// <summary>
/// The session <c>POST /v1/dev/session</c> issues is an ordinary one: the real host answers an authenticated
/// request with the cookies it returned, logout and the self-revoke route end it for every later request, and
/// nothing in the body can move the sign-in onto another account.
/// </summary>
/// <remarks>
/// <para>
/// The cases keep the cookie jar themselves (<see cref="SessionTestHost.SendAsync"/> states exactly which values
/// a request carries) instead of letting an <see cref="HttpClient"/> store them: that is what makes "the OLD
/// value is dead" and "a replay after logout stays 401" assertable, and what lets a repeat sign-in present the
/// predecessor on purpose.
/// </para>
/// <para>
/// The answer of a replayed cookie is the row's state, not the cookie's: revocation is a database update, so the
/// same bytes that authenticated once must be refused with <c>session_revoked</c> afterwards.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class DevSessionLifecycleTests : CatalogueTestBase
{
    /// <summary>The session list and the two revocation routes the cookie is used against.</summary>
    private const string SessionsPath = "/v1/auth/sessions";

    /// <summary>The sign-out route.</summary>
    private const string LogoutPath = "/v1/auth/logout";

    /// <summary>The constructor the harness's assembly fixture flows into.</summary>
    /// <param name="fixture">The shared container; this class gets a private database dropped after each test.</param>
    public DevSessionLifecycleTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    /// <summary>
    /// The cookies the sign-in returned authenticate a request: the list answers 200 with exactly the session
    /// the cookie value hashes to, marked current.
    /// </summary>
    [Fact]
    public async Task The_Issued_Cookie_Authenticates_And_Lists_Its_Own_Session()
    {
        await SeedDevAccountAsync(withMembership: true);

        using var host = DevSessionTestHost.Start(Fixture, DevSessionTestHost.Development);
        var signedIn = await SignInAsync(host);

        using var list = await SessionTestHost.SendAsync(
            host.CreateDevClient(),
            HttpMethod.Get,
            SessionsPath,
            SessionTestHost.CookieHeaderFor(signedIn.Cookie, signedIn.Xsrf));

        list.StatusCode.ShouldBe(HttpStatusCode.OK);

        var items = await ItemsAsync(list);
        items.Count.ShouldBe(1, "the dev user has exactly the session this cookie signs in");
        items[0].GetProperty("current").GetBoolean().ShouldBeTrue();
        items[0].GetProperty("id").GetGuid().ShouldBe(await LiveSessionIdAsync());
    }

    /// <summary>
    /// Logout revokes the row through the dev user's own credential, and replaying the dead value afterwards is
    /// 401 <c>session_revoked</c>: the route's session is the one every other session route already governs.
    /// </summary>
    [Fact]
    public async Task Logout_Revokes_The_Row_And_A_Replay_Is_401_Session_Revoked()
    {
        await SeedDevAccountAsync(withMembership: true);

        using var host = DevSessionTestHost.Start(Fixture, DevSessionTestHost.Development);
        var signedIn = await SignInAsync(host);

        using (var loggedOut = await SessionTestHost.SendAsync(
            host.CreateDevClient(),
            HttpMethod.Post,
            LogoutPath,
            SessionTestHost.CookieHeaderFor(signedIn.Cookie, signedIn.Xsrf),
            xsrfToken: signedIn.Xsrf.RequestToken))
        {
            loggedOut.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await DevSessionTestHost.CountAsync(
            Fixture,
            "select count(*) from public.session where revoked_at is not null")).ShouldBe(
            1,
            "logout is a row update, not a key rotation");

        using var replay = await SessionTestHost.SendAsync(
            host.CreateDevClient(),
            HttpMethod.Get,
            SessionsPath,
            SessionTestHost.CookieHeaderFor(signedIn.Cookie));

        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(replay)).ShouldBe(ApiErrorCode.SessionRevoked);
    }

    /// <summary>
    /// Revoking the caller's OWN session through <c>DELETE /v1/auth/sessions/{s}</c> ends it exactly as logout
    /// does: the next request with the same value is 401 <c>session_revoked</c>.
    /// </summary>
    [Fact]
    public async Task Self_Revoke_Ends_The_Session_And_A_Replay_Is_401_Session_Revoked()
    {
        await SeedDevAccountAsync(withMembership: true);

        using var host = DevSessionTestHost.Start(Fixture, DevSessionTestHost.Development);
        var signedIn = await SignInAsync(host);
        var sessionId = await LiveSessionIdAsync();

        using (var revoked = await SessionTestHost.SendAsync(
            host.CreateDevClient(),
            HttpMethod.Delete,
            SessionsPath + "/" + sessionId,
            SessionTestHost.CookieHeaderFor(signedIn.Cookie, signedIn.Xsrf),
            xsrfToken: signedIn.Xsrf.RequestToken))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent, "the caller may always revoke its own row");
        }

        (await DevSessionTestHost.CountAsync(
            Fixture,
            "select count(*) from public.session where revoked_at is not null")).ShouldBe(1);

        using var replay = await SessionTestHost.SendAsync(
            host.CreateDevClient(),
            HttpMethod.Get,
            SessionsPath,
            SessionTestHost.CookieHeaderFor(signedIn.Cookie));

        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(replay)).ShouldBe(ApiErrorCode.SessionRevoked);
    }

    /// <summary>
    /// A body that names another eligible account changes nothing: the session is still the seeded dev user's,
    /// and the named account holds no row. The route reads the secret and ignores every other member, so a caller
    /// cannot pick who it signs in as.
    /// </summary>
    [Fact]
    public async Task A_Body_Naming_Another_User_Never_Signs_That_User_In()
    {
        await SeedDevAccountAsync(withMembership: true);
        var otherOrgId = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var otherUserId = Guid.CreateVersion7();
        await SeedUserAsync(otherUserId, "other@example.test", otherOrgId, withMembership: true);

        using var host = DevSessionTestHost.Start(Fixture, DevSessionTestHost.Development);
        using var client = host.CreateDevClient();

        using var response = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Post,
            DevSessionTestHost.Route,
            jsonBody: JsonSerializer.Serialize(new
            {
                secret = host.Secret,
                user_id = otherUserId,
                user = otherUserId,
                email = "other@example.test",
                org_id = otherOrgId,
                provider = "google",
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await DevSessionTestHost.OwnerScalarAsync<long>(
            Fixture,
            "select count(*) from public.session where user_id = @user_id",
            [("user_id", otherUserId)])).ShouldBe(
            0,
            "the session is never created for an account the body names");
        (await DevSessionTestHost.OwnerScalarAsync<long>(
            Fixture,
            "select count(*) from public.session where user_id = @user_id",
            [("user_id", KnownDevUser.UserId)])).ShouldBe(1);
        (await DevSessionTestHost.SessionUserIdAsync(Fixture)).ShouldBe(KnownDevUser.UserId);
    }

    /// <summary>
    /// Signing in again while the browser still holds a session revokes that predecessor: one live row remains,
    /// the old value answers 401 <c>session_revoked</c> and the new one authenticates.
    /// </summary>
    [Fact]
    public async Task A_Repeat_Sign_In_With_The_Existing_Cookie_Revokes_The_Predecessor()
    {
        await SeedDevAccountAsync(withMembership: true);

        using var host = DevSessionTestHost.Start(Fixture, DevSessionTestHost.Development);
        var first = await SignInAsync(host);
        var firstId = await LiveSessionIdAsync();

        var second = await SignInAsync(host, predecessor: first.Cookie);

        (await DevSessionTestHost.CountAsync(
            Fixture,
            "select count(*) from public.session where revoked_at is null")).ShouldBe(
            1,
            "a sign-in never leaves a second live row behind");
        (await DevSessionTestHost.OwnerScalarAsync<long>(
            Fixture,
            "select count(*) from public.session where id = @id and revoked_at is not null",
            [("id", firstId)])).ShouldBe(1, "the predecessor row is revoked, not deleted");

        using (var old = await SessionTestHost.SendAsync(
            host.CreateDevClient(),
            HttpMethod.Get,
            SessionsPath,
            SessionTestHost.CookieHeaderFor(first.Cookie)))
        {
            old.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await SessionTestHost.ErrorCodeAsync(old)).ShouldBe(ApiErrorCode.SessionRevoked);
        }

        using var current = await SessionTestHost.SendAsync(
            host.CreateDevClient(),
            HttpMethod.Get,
            SessionsPath,
            SessionTestHost.CookieHeaderFor(second.Cookie));

        current.StatusCode.ShouldBe(HttpStatusCode.OK);
        var items = await ItemsAsync(current);
        items.Count(item => item.GetProperty("current").GetBoolean()).ShouldBe(
            1,
            "the replacement session is the one that authenticates");
    }

    /// <summary>Signs in over TLS, keeping the cookies the answer returns.</summary>
    /// <param name="host">The development host under test.</param>
    /// <param name="predecessor">The session value the browser already holds, or null for a first sign-in.</param>
    private static async Task<(string Cookie, XsrfPair Xsrf)> SignInAsync(
        DevSessionTestHost host,
        string? predecessor = null)
    {
        using var client = host.CreateDevClient();
        using var response = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Post,
            DevSessionTestHost.Route,
            cookieHeader: predecessor is null
                ? null
                : SessionTestHost.SessionCookieName + "=" + predecessor,
            jsonBody: JsonSerializer.Serialize(new { secret = host.Secret }));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var cookie = SessionTestHost.SessionCookieValue(response);
        cookie.ShouldNotBeNullOrEmpty("a successful sign-in sets the session cookie");

        return (cookie!, XsrfPair.From(response));
    }

    /// <summary>The <c>id</c> of the one live session row.</summary>
    private Task<Guid> LiveSessionIdAsync() =>
        DevSessionTestHost.OwnerScalarAsync<Guid>(
            Fixture,
            "select id from public.session where revoked_at is null",
            []);

    /// <summary>The items of a session-list body.</summary>
    /// <param name="response">The 200 the list answered.</param>
    private static async Task<IReadOnlyList<JsonElement>> ItemsAsync(HttpResponseMessage response)
    {
        var body = await SessionTestHost.BodyAsync(response);
        using var document = JsonDocument.Parse(body);

        return [.. document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.Clone())];
    }

    /// <summary>Seeds the account the seed-dev migrator writes: the user row, optionally with an active membership.</summary>
    /// <param name="withMembership">Whether the account gets the active membership a sign-in requires.</param>
    private async Task<Guid> SeedDevAccountAsync(bool withMembership)
    {
        var orgId = await Fixture.SeedOrgAsync();
        await SeedUserAsync(KnownDevUser.UserId, "dev@example.test", orgId, withMembership);

        return orgId;
    }

    /// <summary>Seeds one user row and, when asked, its active membership in <paramref name="orgId"/>.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="email">Its verified-looking address.</param>
    /// <param name="orgId">The organization the membership names.</param>
    /// <param name="withMembership">Whether the membership row is written.</param>
    private async Task SeedUserAsync(Guid userId, string email, Guid orgId, bool withMembership)
    {
        await Fixture.SeedAsync(
            """insert into public."user" (id, email) values (@id, @email)""",
            [("id", userId), ("email", email)],
            Token);

        if (withMembership)
        {
            await Fixture.SeedAsync(
                """
                insert into public.membership
                    (id, org_id, user_id, invited_email, status, created_at, updated_at, version)
                values (@id, @org_id, @user_id, @email, 'active', now(), now(), 1)
                """,
                [("id", Guid.CreateVersion7()), ("org_id", orgId), ("user_id", userId), ("email", email)],
                Token);
        }
    }
}
