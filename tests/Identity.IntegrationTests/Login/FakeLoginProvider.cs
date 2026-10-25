using Evidgate.Modules.Identity.Login;

namespace Identity.IntegrationTests.Login;

/// <summary>
/// A scriptable <see cref="ILoginProvider"/> for the login protocol tests: it builds a real authorization
/// URL on a fixed host, answers with whatever the test scripted, and records what the routes handed it
/// (the PKCE verifier, the nonce and the redirect URI), so the tests can prove the protocol handed the
/// right thing over without a provider implementation.
/// </summary>
/// <remarks>
/// The exception a scripted completion throws carries <see cref="Canary"/>, so a test can assert the text
/// never reaches a response body or a log line. No test asserts on the provider's own wording otherwise.
/// </remarks>
internal sealed class FakeLoginProvider(string name) : ILoginProvider
{
    /// <summary>Host the fake authorization URL points at (never called by the server).</summary>
    internal const string AuthorizationHost = "https://provider.evidgate.test";

    /// <summary>A recognisable token no response body may ever contain.</summary>
    internal const string Canary = "provider-canary-do-not-echo";

    /// <summary>Route value this provider answers to.</summary>
    public string Name => name;

    /// <inheritdoc />
    public bool IsConfigured { get; set; } = true;

    /// <summary>The outcome <see cref="CompleteAsync"/> answers when no <see cref="OnComplete"/> script is set.</summary>
    public LoginProviderResult Result { get; set; } = LoginProviderResult.Fail(LoginFailure.TokenInvalid);

    /// <summary>A scripted completion (for a timeout, a transport failure or a delay), or null.</summary>
    public Func<CancellationToken, Task<LoginProviderResult>>? OnComplete { get; set; }

    /// <summary>How many times the routes called <see cref="CompleteAsync"/>.</summary>
    public int Completions { get; private set; }

    /// <summary>What <c>start</c> asked the provider to build the authorization URL from.</summary>
    public LoginAuthorizationRequest? LastAuthorization { get; private set; }

    /// <summary>What <c>callback</c> handed the provider for the code exchange.</summary>
    public LoginCallbackRequest? LastCallback { get; private set; }

    /// <summary>A verified identity for this provider, the shape a successful flow yields.</summary>
    public static VerifiedIdentity VerifiedIdentity(string provider, string subject = "subject-1") =>
        new(provider, subject, "member@example.test", EmailVerified: true, DisplayName: "Test Member");

    /// <inheritdoc />
    public Uri BuildAuthorizationUrl(LoginAuthorizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        LastAuthorization = request;

        return new Uri(
            $"{AuthorizationHost}/authorize"
            + "?response_type=code"
            + "&client_id=fake-client"
            + "&redirect_uri=" + Uri.EscapeDataString(request.RedirectUri.AbsoluteUri)
            + "&state=" + Uri.EscapeDataString(request.State)
            + "&nonce=" + Uri.EscapeDataString(request.Nonce)
            + "&code_challenge=" + Uri.EscapeDataString(request.CodeChallenge)
            + "&code_challenge_method=S256");
    }

    /// <inheritdoc />
    public async Task<LoginProviderResult> CompleteAsync(LoginCallbackRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Completions++;
        LastCallback = request;

        return OnComplete is { } scripted ? await scripted(cancellationToken).ConfigureAwait(false) : Result;
    }
}
