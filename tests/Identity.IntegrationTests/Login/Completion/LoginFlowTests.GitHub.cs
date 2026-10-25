using System.Net;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Completion;

/// <summary>
/// 02.3.1-T1.3 Done-when / AC1, GitHub path, and AC4's verified-address requirement
/// (<c>LoginFlowTests.GitHub</c>): the same whole-callback suite as <c>LoginFlowTests.cs</c>, driven with the
/// <c>github</c> provider route value.
/// </summary>
/// <remarks>
/// <para>
/// The second part of <see cref="LoginFlowTests"/> on purpose: the collection, the Docker trait and the
/// harness constructor are declared once, in <c>LoginFlowTests.cs</c>, and both files share the one host
/// helper — so a GitHub case cannot drift onto a different deployment than the Google ones.
/// </para>
/// <para>
/// Only the provider is a fake. The real GitHub provider's own reads (<c>GET /user</c> and
/// <c>GET /user/emails</c>) are <c>Login/GitHub/GitHubProviderFlowTests</c>' and <c>TokenValidationTests</c>';
/// everything else here — the upsert, the membership check, the session row, the cookie, the 302 — is the
/// deployment's.
/// </para>
/// </remarks>
public sealed partial class LoginFlowTests
{
    /// <summary>
    /// A second GitHub account id, so the unverified-address case can present one address from two identities.
    /// </summary>
    private const string SecondGitHubSubject = "149272103";

    /// <summary>
    /// AC1 GitHub path: a GitHub-verified identity with an active membership signs in end to end — the first
    /// sign-in is 403 <c>no_membership</c> and creates the user, then the 302 to the allowlisted destination,
    /// exactly one <c>__Host-evidgate_session</c> cookie, one <c>session</c> row for that user and a
    /// <c>user_identity</c> row under the <c>github</c> provider.
    /// </summary>
    [Fact]
    public async Task GitHubHappyPath_EndsWith_A_Session_Row_A_Cookie_And_A_302()
    {
        using var host = LoginCompletionTestHost.Start(Fixture, LoginCompletionTestHost.GitHubProvider());
        using var client = host.CreateLoginClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(LoginCompletionTestHost.UserAgent);

        // The first sign-in is the GitHub identity's first ever: refused for want of a membership (403
        // no_membership), which is exactly what creates its user and identity rows.
        var orgId = await LoginCompletionTestHost.EstablishMemberAsync(
            Fixture,
            host,
            client,
            LoginCompletionTestHost.GitHub,
            LoginCompletionTestHost.GitHubSubject);
        orgId.ShouldNotBe(
            Guid.Empty,
            "AC3 GitHub path: the first sign-in is 403 no_membership, and the user row it created is the one seeded");

        var userId = await LoginCompletionTestHost.UserIdAsync(
            Fixture,
            LoginCompletionTestHost.GitHub,
            LoginCompletionTestHost.GitHubSubject);

        (await LoginCompletionTestHost.OwnerScalarAsync<string>(
            Fixture,
            "select provider from public.user_identity where user_id = @user_id",
            [("user_id", userId)]))
            .ShouldBe(
                LoginCompletionTestHost.GitHub,
                "AC1 GitHub path: the identity is stored under the github provider, never under google");

        using var completed = await host.SignInAsync(
            client,
            LoginCompletionTestHost.GitHub,
            returnUrl: "/runs/42");

        completed.StatusCode.ShouldBe(
            HttpStatusCode.Found,
            "AC1 GitHub path: a verified GitHub identity with an active membership signs in");
        completed.Headers.Location!.OriginalString.ShouldBe("/runs/42");

        LoginCompletionTestHost.SessionCookies(completed)
            .Count.ShouldBe(1, "AC1 GitHub path: exactly one __Host-evidgate_session Set-Cookie");
        var cookieValue = LoginCompletionTestHost.SessionCookieValue(completed);
        cookieValue.ShouldNotBeNullOrEmpty("AC1 GitHub path: the completed login sets the session cookie");

        (await LoginCompletionTestHost.OwnerScalarAsync<int>(
            Fixture,
            "select count(*) from public.session where user_id = @user_id",
            [("user_id", userId)]))
            .ShouldBe(1, "AC1 GitHub path: the GitHub login wrote exactly one session row for that user");

        var session = await LoginCompletionTestHost.SessionAsync(Fixture, await SessionIdAsync(userId));

        session.UserId.ShouldBe(userId);
        session.CookieHmac.ShouldBe(
            host.CookieHmac(cookieValue!),
            "the row stores the HMAC of the cookie value under the configured pepper, never the value itself");
        session.UserAgent.ShouldBe(LoginCompletionTestHost.UserAgent);
        session.RevokedAt.ShouldBeNull();
    }

    /// <summary>
    /// AC4 (<c>VerifiedIdentity.EmailVerified</c>): an address the provider did not mark verified is never an
    /// identity key — the identity is <c>(provider, subject)</c> and nothing else (D3/AC6), so one address
    /// presented by two accounts stays two users, and <c>user.email</c> holds what the provider supplied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AC4 puts the verified-address requirement at the provider boundary, not in the completion: the real
    /// GitHub provider answers <c>token_invalid</c> for an account whose every address is unverified
    /// (<c>GitHubLoginProvider.ReadIdentityAsync</c>), and the Google validator likewise fails closed on an
    /// unverified address (<c>GoogleIdTokenValidator</c>). An identity with
    /// <c>EmailVerified: false</c> is therefore a shape only a fake can produce, and what the completion does
    /// with it is asserted here rather than assumed.
    /// </para>
    /// <para>
    /// It cannot be a null <c>user.email</c>: the column is <c>not null</c> with a 3..320 character bound
    /// (0041), and <c>login_upsert_identity</c> inserts the supplied address on a first login (0042), so a
    /// null would abort a login every other gate admitted instead of enforcing AC4. The invariant that no
    /// unverified address ever creates a user row is held by the providers, and every other claim here is
    /// asserted against the rows the real upsert wrote.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GitHubVerifiedEmailOnly_Never_Binds_By_Email()
    {
        using var host = LoginCompletionTestHost.Start(
            Fixture,
            LoginCompletionTestHost.GitHubProvider(emailVerified: false));
        using var client = host.CreateLoginClient();

        await LoginCompletionTestHost.EstablishMemberAsync(
            Fixture,
            host,
            client,
            LoginCompletionTestHost.GitHub,
            LoginCompletionTestHost.GitHubSubject);

        var userId = await LoginCompletionTestHost.UserIdAsync(
            Fixture,
            LoginCompletionTestHost.GitHub,
            LoginCompletionTestHost.GitHubSubject);

        (await LoginCompletionTestHost.OwnerScalarAsync<string>(
            Fixture,
            "select email from public.\"user\" where id = @id",
            [("id", userId)]))
            .ShouldBe(
                LoginCompletionTestHost.GitHubEmail,
                "AC4: `user.email` is not null (0041) — the address the provider supplied is the one stored, as supplied");
        (await LoginCompletionTestHost.OwnerScalarAsync<string>(
            Fixture,
            "select email_at_provider from public.user_identity where user_id = @user_id",
            [("user_id", userId)]))
            .ShouldBe(
                LoginCompletionTestHost.GitHubEmail,
                "AC1: `email_at_provider` records the provider's address, and D3/AC6 make it no identity key");

        // D3/AC6: signing in again resolves the identity by (provider, subject) alone, so the login completes.
        using var completed = await host.SignInAsync(client, LoginCompletionTestHost.GitHub);

        completed.StatusCode.ShouldBe(
            HttpStatusCode.Found,
            "D3/AC6: the identity is (provider, subject); the address plays no part in resolving or refusing it");

        // D3/AC6: the same address under a second GitHub account is a second user — a login never merges on email.
        using var secondHost = LoginCompletionTestHost.Start(
            Fixture,
            LoginCompletionTestHost.GitHubProvider(subject: SecondGitHubSubject, emailVerified: false));
        using var secondClient = secondHost.CreateLoginClient();

        await LoginCompletionTestHost.EstablishMemberAsync(
            Fixture,
            secondHost,
            secondClient,
            LoginCompletionTestHost.GitHub,
            SecondGitHubSubject);

        var secondUserId = await LoginCompletionTestHost.UserIdAsync(
            Fixture,
            LoginCompletionTestHost.GitHub,
            SecondGitHubSubject);

        secondUserId.ShouldNotBe(userId, "D3/AC6: (provider, subject) is the identity, never the email");
        (await LoginCompletionTestHost.CountAsync(Fixture, "select count(*) from public.\"user\""))
            .ShouldBe(2, "D3/AC6: one address at two subjects stays two users; no login merges on email");
        (await LoginCompletionTestHost.CountAsync(Fixture, "select count(*) from public.user_identity"))
            .ShouldBe(2, "D3/AC6: each subject owns its own user_identity row");
        (await LoginCompletionTestHost.OwnerScalarAsync<string>(
            Fixture,
            "select email from public.\"user\" where id = @id",
            [("id", secondUserId)]))
            .ShouldBe(
                LoginCompletionTestHost.GitHubEmail,
                "D3/AC6: both rows hold the one address, which is what proves it is not the key");
    }
}
