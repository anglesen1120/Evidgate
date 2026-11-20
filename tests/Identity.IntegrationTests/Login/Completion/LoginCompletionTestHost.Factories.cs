using Evidgate.Modules.Identity.Login;

namespace Identity.IntegrationTests.Login.Completion;

internal sealed partial class LoginCompletionTestHost
{
    /// <summary>
    /// A deployment on the test's real database whose replay store is the one thing that cannot answer: the
    /// limiter and the rest of the host keep their real stores, so the case's subject stays the callback's own
    /// outage answer and is not answered earlier by a broken rate limiter.
    /// </summary>
    internal static LoginCompletionTestHost StartWithReplayStoreOutage(
        Evidgate.Testing.DatabaseFixture fixture,
        params ILoginProvider[] providers)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        return new(
            fixture.AppConnectionString,
            new LoginHostSettings
            {
                Providers = providers,
                Seams = InMemoryLoginSeams.ApplyReplayStoreOutage,
            });
    }

    /// <summary>
    /// A <c>github</c> provider that verifies <paramref name="subject"/>'s identity, exactly as
    /// <c>GitHubLoginProvider</c> answers after <c>GET /user</c> and <c>GET /user/emails</c>.
    /// </summary>
    /// <param name="subject">The GitHub account's numeric id.</param>
    /// <param name="email">The address the provider selected.</param>
    /// <param name="emailVerified">
    /// Whether the provider marks that address verified. The real provider only ever answers <c>true</c>
    /// (<c>GitHubLoginProvider</c>: an account without a verified address is a token failure, AC4), so
    /// <c>false</c> is a shape only a fake can produce — and what the completion does with it is asserted
    /// rather than assumed.
    /// </param>
    internal static FakeLoginProvider GitHubProvider(
        string subject = GitHubSubject,
        string email = GitHubEmail,
        bool emailVerified = true) =>
        new(GitHub)
        {
            Result = LoginProviderResult.Ok(
                new VerifiedIdentity(GitHub, subject, email, emailVerified, "Test Member")),
        };
}
